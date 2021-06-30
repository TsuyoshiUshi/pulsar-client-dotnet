namespace Pulsar.Client.Transaction

open System
open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open Pulsar.Client.Api
open Pulsar.Client.Common
open Pulsar.Client.Internal
open Microsoft.Extensions.Logging
open pulsar.proto

type internal TransactionCoordinatorState =
    | NONE
    | STARTING
    | READY
    | CLOSING
    | CLOSED

type internal TransactionCoordinatorMessage =
    | Start of AsyncReplyChannel<ResultOrException<Unit>>
    | Close

type internal TransactionCoordinatorClient (clientConfig: PulsarClientConfiguration,
                                            connectionPool: ConnectionPool,
                                            lookup: BinaryLookupService) =

    let DEFAULT_TXN_TTL = TimeSpan.FromSeconds(60.0)
    let prefix = "tcClient"
    let mutable state = TransactionCoordinatorState.NONE
    let mutable epoch = 0
    let handlers = ResizeArray()
    
    let nextHandler () =
        let index = signSafeMod (Interlocked.Increment(&epoch)) handlers.Count
        handlers.[index]
    
    let getTCAssignTopicName partition =
        if partition >=0 then
            TopicName.TRANSACTION_COORDINATOR_ASSIGN.ToString() + TopicNameHelpers.PartitionTopicSuffix + partition.ToString()
        else
            TopicName.TRANSACTION_COORDINATOR_ASSIGN.ToString()
        |> UMX.tag
    
    let mb = MailboxProcessor<TransactionCoordinatorMessage>.Start(fun inbox ->

        let rec loop () =
            async {
                match! inbox.Receive() with
                | TransactionCoordinatorMessage.Start ch ->
                    
                    Log.Logger.LogDebug("{0} starting", prefix)
                    match state with
                    | NONE ->
                        state <- TransactionCoordinatorState.STARTING
                        try
                            let! partitionMeta =
                                lookup.GetPartitionedTopicMetadata(TopicName.TRANSACTION_COORDINATOR_ASSIGN.CompleteTopicName)
                                |> Async.AwaitTask
                            let tasks = 
                                if partitionMeta.Partitions > 0 then
                                    seq {
                                        for i in 0..partitionMeta.Partitions-1 do
                                            let tcs = TaskCompletionSource()
                                            let handler = TransactionMetaStoreHandler(clientConfig, %(uint64 i),
                                                                                     getTCAssignTopicName(i), connectionPool, lookup, tcs)
                                            handlers.Add(handler)
                                            yield tcs.Task
                                    }
                                else
                                    let tcs = TaskCompletionSource()
                                    let handler = TransactionMetaStoreHandler(clientConfig, %0UL,
                                                                              getTCAssignTopicName(-1), connectionPool, lookup, tcs)
                                    handlers.Add(handler)
                                    seq { tcs.Task }                                
                            do! tasks |> Task.WhenAll |> Async.AwaitTask |> Async.Ignore
                            Log.Logger.LogInformation("{0} connected with partitions count {1}", prefix, partitionMeta.Partitions)
                            state <- TransactionCoordinatorState.READY
                            ch.Reply(Ok ())
                        with Flatten ex ->
                            Log.Logger.LogError(ex, "{0} connection error on start", prefix)
                            state <- TransactionCoordinatorState.NONE
                            ch.Reply(Error ex)
                    | _ ->
                        Log.Logger.LogError("{0} Can not start while current state is {1}", prefix, state)
                        state <- TransactionCoordinatorState.NONE
                        ch.Reply(CoordinatorClientStateException $"Can not start while current state is {state}" :> exn |> Error)
                    return! loop ()
                
                | Close ->
                    
                    match state with
                    | CLOSING | CLOSED ->
                        Log.Logger.LogError("{0} The transaction meta store is closing or closed, doing nothing.", prefix)
                    | _ ->
                        for handler in handlers do
                            handler.Close()
                        handlers.Clear()
                        state <- TransactionCoordinatorState.CLOSED
           }
        loop ()
    )
    
    do mb.Error.Add(fun ex -> Log.Logger.LogCritical(ex, "transaction coordinator mailbox failure"))
    
    member this.Mb: MailboxProcessor<TransactionCoordinatorMessage> = mb
    
    member this.Start() =
        mb.PostAndAsyncReply(Start)
        
    member this.NewTransactionAsync() =
        this.NewTransactionAsync(DEFAULT_TXN_TTL)
        
    member this.NewTransactionAsync(timeSpan) =
        let handler = nextHandler()
        handler.NewTransactionAsync(timeSpan)
        
    member this.AddPublishPartitionToTxnAsync(txnId: TxnId, partition) =
        let handlerId = int txnId.MostSigBits
        if handlerId >= handlers.Count then
            raise <| MetaStoreHandlerNotExistsException $"Transaction meta store handler for transaction meta store {txnId.MostSigBits} not exists."
        let handler = handlers.[handlerId]
        handler.AddPublishPartitionToTxnAsync(txnId, partition)
        
    member this.AddSubscriptionToTxnAsync(txnId: TxnId, topic: CompleteTopicName, subscription: SubscriptionName) =
        let handlerId = int txnId.MostSigBits
        if handlerId >= handlers.Count then
            raise <| MetaStoreHandlerNotExistsException $"Transaction meta store handler for transaction meta store {txnId.MostSigBits} not exists."
        let handler = handlers.[handlerId]
        handler.AddSubscriptionToTxnAsync(txnId, topic, subscription)
        
    member this.CommitAsync(txnId: TxnId) =
        let handlerId = int txnId.MostSigBits
        if handlerId >= handlers.Count then
            raise <| MetaStoreHandlerNotExistsException $"Transaction meta store handler for transaction meta store {txnId.MostSigBits} not exists."
        let handler = handlers.[handlerId]
        handler.EdTxnAsync(txnId, TxnAction.Commit)

    member this.AbortAsync(txnId: TxnId) =
        let handlerId = int txnId.MostSigBits
        if handlerId >= handlers.Count then
            raise <| MetaStoreHandlerNotExistsException $"Transaction meta store handler for transaction meta store {txnId.MostSigBits} not exists."
        let handler = handlers.[handlerId]
        handler.EdTxnAsync(txnId, TxnAction.Abort)
        
    member this.Close() =
        mb.Post(Close)