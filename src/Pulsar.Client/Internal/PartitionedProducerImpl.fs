﻿namespace Pulsar.Client.Api

open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Threading.Tasks
open FSharp.UMX
open Pulsar.Client.Common
open Pulsar.Client.Internal
open System
open System.Collections.Generic
open System.Runtime.InteropServices
open Microsoft.Extensions.Logging
open Pulsar.Client.Schema
open System.Threading
open System.Timers

type internal PartitionedProducerMessage =
    | Init
    | LastSequenceId of AsyncReplyChannel<SequenceId>
    | Close of AsyncReplyChannel<ResultOrException<unit>>
    | TickTime
    | GetStats of AsyncReplyChannel<ProducerStats>

type internal PartitionedConnectionState =
    | Uninitialized
    | Failed
    | Ready
    | Closing
    | Closed

type internal PartitionedProducerImpl<'T> private (producerConfig: ProducerConfiguration, clientConfig: PulsarClientConfiguration, connectionPool: ConnectionPool,
                                      numPartitions: int, lookup: BinaryLookupService, schema: ISchema<'T>,
                                      interceptors: ProducerInterceptors<'T>, cleanup: PartitionedProducerImpl<'T> -> unit) as this =
    let _this = this :> IProducer<'T>
    let producerId = Generators.getNextProducerId()
    let prefix = sprintf "p/producer(%u, %s)" %producerId producerConfig.ProducerName
    
    let keyValueProcessor: IKeyValueProcessor option = KeyValueProcessor.GetInstance schema
    
    let producers = ResizeArray<IProducer<'T>>(numPartitions)
    let producerCreatedTsc = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let maxPendingMessages = Math.Min(producerConfig.MaxPendingMessages, producerConfig.MaxPendingMessagesAcrossPartitions / numPartitions)
    let mutable connectionState = PartitionedConnectionState.Uninitialized
    let mutable numPartitions = numPartitions
    let hashingFunction =
        match producerConfig.HashingScheme with
        | HashingScheme.DotnetStringHash ->
            fun (s: String)-> s.GetHashCode()
        | HashingScheme.Murmur3_32Hash ->
            MurmurHash3.Hash
        | _ ->
            failwith "Unknown HashingScheme"
    let router =
        match producerConfig.MessageRoutingMode with
        | MessageRoutingMode.SinglePartition ->
            SinglePartitionMessageRouterImpl (RandomGenerator.Next(0, numPartitions), hashingFunction) :> IMessageRouter
        | MessageRoutingMode.RoundRobinPartition ->
            RoundRobinPartitionMessageRouterImpl (
                RandomGenerator.Next(0, numPartitions),
                producerConfig.BatchingEnabled,
                producerConfig.BatchingPartitionSwitchFrequencyIntervalMs,
                hashingFunction) :> IMessageRouter
        | MessageRoutingMode.CustomPartition when producerConfig.CustomMessageRouter.IsSome ->
            producerConfig.CustomMessageRouter.Value
        | _ ->
            failwith "Unknown MessageRoutingMode"

    let statsReduce (statsArray: ProducerStats array) =
        let mutable numMsgsSent: int64 = 0L
        let mutable numBytesSent: int64 = 0L
        let mutable numSendFailed: int64 = 0L
        let mutable numAcksReceived: int64 = 0L
        let mutable sendMsgsRate: float = 0.0
        let mutable sendBytesRate: float = 0.0
        let mutable sendLatencyMin: float = Double.MaxValue
        let mutable sendLatencyMax: float = Double.MinValue
        let mutable sendLatencySum: float = 0.0
        let mutable totalMsgsSent: int64 = 0L
        let mutable totalBytesSent: int64 = 0L
        let mutable totalSendFailed: int64 = 0L
        let mutable totalAcksReceived: int64 = 0L
        let mutable intervalDurationSum: float = 0.0
        let mutable pendingMsgs: int = 0
        
        statsArray |> Array.iter(fun stats ->
            numMsgsSent <- numMsgsSent + stats.NumMsgsSent
            numBytesSent <- numBytesSent + stats.NumBytesSent
            numSendFailed <- numSendFailed + stats.NumSendFailed
            numAcksReceived <- numAcksReceived + stats.NumAcksReceived
            sendMsgsRate <- sendMsgsRate + stats.SendMsgsRate
            sendBytesRate <- sendBytesRate + stats.SendBytesRate
            sendLatencyMin <- min sendLatencyMin stats.SendLatencyMin
            sendLatencyMax <- max sendLatencyMax stats.SendLatencyMax
            sendLatencySum <- sendLatencySum + stats.SendLatencyAverage * float stats.NumAcksReceived
            totalMsgsSent <- totalMsgsSent + stats.TotalMsgsSent
            totalBytesSent <- totalBytesSent + stats.TotalBytesSent
            totalSendFailed <- totalSendFailed + stats.TotalSendFailed
            totalAcksReceived <- totalAcksReceived + stats.TotalAcksReceived
            intervalDurationSum <- intervalDurationSum + stats.IntervalDuration
            pendingMsgs <- pendingMsgs + stats.PendingMsgs
            )
        
        {
            NumMsgsSent = numMsgsSent
            NumBytesSent = numBytesSent
            NumSendFailed = numSendFailed
            NumAcksReceived = numAcksReceived
            SendMsgsRate = sendMsgsRate
            SendBytesRate = sendBytesRate
            SendLatencyMin = sendLatencyMin
            SendLatencyMax = sendLatencyMax
            SendLatencyAverage = if numAcksReceived > 0L then sendLatencySum / float numAcksReceived else 0.0
            TotalMsgsSent = totalMsgsSent
            TotalBytesSent = totalBytesSent
            TotalSendFailed = totalSendFailed
            TotalAcksReceived = totalAcksReceived
            IntervalDuration = if statsArray.Length > 0 then intervalDurationSum / float statsArray.Length else 0.0
            PendingMsgs = pendingMsgs 
        }
    
    let timer = new Timer(1000.0 * 60.0) // 1 minute

    let stopProducer() =
        cleanup(this)
        timer.Close()

    let mb = MailboxProcessor<PartitionedProducerMessage>.Start(fun inbox ->

        let rec loop () =
            async {
                let! msg = inbox.Receive()
                match msg with
                | Init ->
                    let producerTasks =
                        Seq.init numPartitions (fun partitionIndex ->
                            let partitionedTopic = producerConfig.Topic.GetPartition(partitionIndex)
                            let partititonedConfig = { producerConfig with
                                                        MaxPendingMessages = maxPendingMessages
                                                        Topic = partitionedTopic }
                            task {
                                let! producer = ProducerImpl.Init(partititonedConfig, clientConfig, connectionPool, partitionIndex, lookup,
                                                    schema, interceptors, fun _ -> ())
                                return producer :> IProducer<'T>
                            })
                    // we mark success if all the partitions are created
                    // successfully, else we throw an exception
                    // due to any
                    // failure in one of the partitions and close the successfully
                    // created partitions
                    try
                        let! producerResults =
                            producerTasks
                            |> Task.WhenAll
                            |> Async.AwaitTask
                        producers.AddRange(producerResults)
                        this.ConnectionState <- Ready
                        Log.Logger.LogInformation("{0} created", prefix)
                        producerCreatedTsc.SetResult()
                        return! loop ()
                    with Flatten ex ->
                        Log.Logger.LogError(ex, "{0} could not create", prefix)
                        do! producerTasks
                            |> Seq.filter (fun t -> t.Status = TaskStatus.RanToCompletion)
                            |> Seq.map (fun t -> task { return! t.Result.DisposeAsync() })
                            |> Task.WhenAll
                            |> Async.AwaitTask
                            |> Async.Ignore
                        this.ConnectionState <- Failed
                        producerCreatedTsc.SetException(ex)
                        stopProducer()
                        
                | LastSequenceId channel ->

                    Log.Logger.LogDebug("{0} LastSequenceId", prefix)
                    producers
                    |> Seq.map (fun producer -> producer.LastSequenceId)
                    |> Seq.max
                    |> channel.Reply
                    return! loop ()

                | Close channel ->

                    match this.ConnectionState with
                    | Closing | Closed ->
                        channel.Reply <| Ok()
                    | _ ->
                        this.ConnectionState <- Closing
                        let producersTasks = producers |> Seq.map(fun producer -> task { return! producer.DisposeAsync() })
                        try
                            let! _ = Task.WhenAll producersTasks |> Async.AwaitTask
                            this.ConnectionState <- Closed
                            Log.Logger.LogInformation("{0} closed", prefix)
                            stopProducer()
                            channel.Reply <| Ok()
                        with Flatten ex ->
                            Log.Logger.LogError(ex, "{0} could not close", prefix)
                            this.ConnectionState <- Failed
                            channel.Reply <| Error ex
                            return! loop ()

                | TickTime  ->

                    match this.ConnectionState with
                    | Ready ->
                        // Check partitions changes of passed in topics, and add new topic partitions.
                        let! partitionedTopicNames = lookup.GetPartitionsForTopic(producerConfig.Topic) |> Async.AwaitTask
                        Log.Logger.LogDebug("{0} partitions number. old: {1}, new: {2}", prefix, numPartitions, partitionedTopicNames.Length )
                        if numPartitions = partitionedTopicNames.Length
                        then
                            // topic partition number not changed
                            ()
                        elif numPartitions < partitionedTopicNames.Length
                        then
                            let producerTasks =
                                seq { numPartitions..partitionedTopicNames.Length - 1 }
                                |> Seq.map (fun partitionIndex ->
                                    let partitionedTopic = partitionedTopicNames.[partitionIndex]
                                    let partititonedConfig = { producerConfig with
                                                                MaxPendingMessages = maxPendingMessages
                                                                Topic = partitionedTopic }
                                    task {
                                        let! producer = ProducerImpl.Init(partititonedConfig, clientConfig, connectionPool, partitionIndex, lookup,
                                                            schema, interceptors, fun _ -> ())
                                        return producer :> IProducer<'T>
                                    })
                            try
                                let! producerResults =
                                    producerTasks
                                    |> Task.WhenAll
                                    |> Async.AwaitTask
                                producers.AddRange(producerResults)
                                Log.Logger.LogDebug("{0} success create producers for extended partitions. old: {1}, new: {2}",
                                    prefix, numPartitions, partitionedTopicNames.Length )
                                numPartitions <- partitionedTopicNames.Length
                            with Flatten ex ->
                                do! producerTasks
                                    |> Seq.filter (fun t -> t.Status = TaskStatus.RanToCompletion)
                                    |> Seq.map (fun t -> task { return! t.Result.DisposeAsync() })
                                    |> Task.WhenAll
                                    |> Async.AwaitTask
                                    |> Async.Ignore
                                Log.Logger.LogWarning(ex, "{0} fail create producers for extended partitions. old: {1}, new: {2}",
                                    prefix, numPartitions, partitionedTopicNames.Length )
                        else
                            Log.Logger.LogError("{0} not support shrink topic partitions. old: {1}, new: {2}",
                                prefix, numPartitions, partitionedTopicNames.Length )
                    | _ ->
                        ()
                    return! loop ()
                    
                | GetStats channel ->
                    
                    let! stats =
                        producers
                        |> Seq.map(fun p -> p.GetStatsAsync())
                        |> Task.WhenAll
                        |> Async.AwaitTask
                    statsReduce stats |> channel.Reply
                    return! loop ()

            }

        loop ()
    )

    do mb.Error.Add(fun ex -> Log.Logger.LogCritical(ex, "{0} mailbox failure", prefix))

    do
        if producerConfig.AutoUpdatePartitions
        then
            timer.AutoReset <- true
            timer.Elapsed.Add(fun _ -> mb.Post TickTime)
            timer.Start()

    override this.Equals producer =
        producerId = (producer :?> IProducer<'T>).ProducerId

    override this.GetHashCode () = int producerId

    member private this.ChoosePartitionIfActive (message: MessageBuilder<'T>) =
        match this.ConnectionState with
        | Closing | Closed ->
            raise (AlreadyClosedException(prefix + " already closed"))
        | Uninitialized | Failed ->
            raise (NotConnectedException(prefix + " Invalid connection state: " + this.ConnectionState.ToString()))
        | Ready ->
            let keyString = message.Key |> Option.map(fun k -> k.PartitionKey) |> Option.defaultValue %""
            let partition = router.ChoosePartition(keyString, numPartitions)
            if partition < 0 || partition >= numPartitions
            then
                failwith (prefix + " Illegal partition index chosen by the message routing policy: " + partition.ToString())
            else
                partition

    member private this.ConnectionState
        with get() = Volatile.Read(&connectionState)
        and set(value) = Volatile.Write(&connectionState, value)

    member private this.InitInternal() =
       task {
           mb.Post Init
           return! producerCreatedTsc.Task
       }

    static member Init(producerConfig: ProducerConfiguration, clientConfig: PulsarClientConfiguration, connectionPool: ConnectionPool,
                        partitions: int, lookup: BinaryLookupService, schema: ISchema<'T>,
                        interceptors:ProducerInterceptors<'T>, cleanup: PartitionedProducerImpl<'T> -> unit) =
        task {
            let producer = PartitionedProducerImpl(producerConfig, clientConfig, connectionPool, partitions, lookup,
                                                   schema, interceptors, cleanup)
            do! producer.InitInternal()
            return producer
        }

    interface IProducer<'T> with

        member this.SendAndForgetAsync (message: 'T) =
            task {
                let partition = _this.NewMessage message |> this.ChoosePartitionIfActive
                return! producers.[partition].SendAndForgetAsync(message)
            }

        member this.SendAndForgetAsync (message: MessageBuilder<'T>) =
            task {
                let partition = this.ChoosePartitionIfActive(message)
                return! producers.[partition].SendAndForgetAsync(message)
            }

        member this.SendAsync (message: 'T) =
            task {
                let partition = _this.NewMessage message |> this.ChoosePartitionIfActive
                return! producers.[partition].SendAsync(message)
            }

        member this.SendAsync (message: MessageBuilder<'T>) =
            task {
                let partition = this.ChoosePartitionIfActive(message)
                return! producers.[partition].SendAsync(message)
            }
            
        member this.NewMessage (value:'T,
            [<Optional; DefaultParameterValue(null:string)>]key:string,
            [<Optional; DefaultParameterValue(null:IReadOnlyDictionary<string, string>)>]properties: IReadOnlyDictionary<string, string>,
            [<Optional; DefaultParameterValue(Nullable():Nullable<int64>)>]deliverAt:Nullable<int64>,
            [<Optional; DefaultParameterValue(Nullable():Nullable<SequenceId>)>]sequenceId:Nullable<SequenceId>,
            [<Optional; DefaultParameterValue(null:byte[])>]keyBytes:byte[],
            [<Optional; DefaultParameterValue(null:byte[])>]orderingKey:byte[]) =
            ProducerImpl.NewMessage(keyValueProcessor, schema, value, key, properties,
                                    deliverAt, sequenceId, keyBytes, orderingKey)

        member this.ProducerId = producerId

        member this.Topic = %producerConfig.Topic.CompleteTopicName

        member this.LastSequenceId = mb.PostAndReply(LastSequenceId)

        member this.Name = producerConfig.ProducerName

        member this.GetStatsAsync() = mb.PostAndAsyncReply(GetStats) |> Async.StartAsTask

        
    interface IAsyncDisposable with
        member this.DisposeAsync() =
            task {
                match this.ConnectionState with
                | Closing | Closed ->
                    return ()
                | _ ->
                    let! result = mb.PostAndAsyncReply(Close)
                    match result with
                    | Ok () -> ()
                    | Error ex -> reraize ex
            } |> ValueTask
