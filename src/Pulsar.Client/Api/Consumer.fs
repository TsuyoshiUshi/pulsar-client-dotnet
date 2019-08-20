﻿namespace Pulsar.Client.Api

open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Threading.Tasks
open FSharp.UMX
open System.Collections.Generic
open System
open Pulsar.Client.Internal
open System.Runtime.CompilerServices
open Pulsar.Client.Common
open Microsoft.Extensions.Logging
open System.Threading
open System.Runtime.InteropServices
open System.IO
open ProtoBuf
open pulsar.proto

type ConsumerException(message) =
    inherit Exception(message)

type SubscriptionMode =
    | Durable
    | NonDurable

type ConsumerState = {
    WaitingChannel: AsyncReplyChannel<Message>
}

type Consumer private (consumerConfig: ConsumerConfiguration, subscriptionMode: SubscriptionMode, lookup: BinaryLookupService) as this =

    [<Literal>]
    let MAX_REDELIVER_UNACKNOWLEDGED = 1000
    let consumerId = Generators.getNextConsumerId()
    let incomingMessages = new Queue<Message>()
    let nullChannel = Unchecked.defaultof<AsyncReplyChannel<Message>>
    let subscribeTsc = TaskCompletionSource<Consumer>()
    let partitionIndex = -1
    let prefix = sprintf "consumer(%u, %s)" %consumerId consumerConfig.ConsumerName
    // TODO take from configuration
    let subscribeTimeout = DateTime.Now.Add(TimeSpan.FromSeconds(60.0))
    let mutable avalablePermits = 0
    let receiverQueueRefillThreshold = consumerConfig.ReceiverQueueSize / 2
    let connectionHandler =
        ConnectionHandler(prefix,
                          lookup,
                          consumerConfig.Topic.CompleteTopicName,
                          (fun () -> this.Mb.Post(ConsumerMessage.ConnectionOpened)),
                          (fun ex -> this.Mb.Post(ConsumerMessage.ConnectionFailed ex)),
                          Backoff({ BackoffConfig.Default with Initial = TimeSpan.FromMilliseconds(100.0); Max = TimeSpan.FromSeconds(60.0) }))

    let increaseAvailablePermits delta =
        avalablePermits <- avalablePermits + delta
        if avalablePermits >= receiverQueueRefillThreshold then
            this.Mb.Post(ConsumerMessage.SendFlowPermits avalablePermits)
            avalablePermits <- 0

    let redeliverMessages messages =
        async {
            do! this.RedeliverUnacknowledgedMessagesAsync messages |> Async.AwaitTask
        } |> Async.StartImmediate

    let unAckedMessageTracker =
        if consumerConfig.AckTimeout > TimeSpan.Zero then
            if consumerConfig.TickDuration > TimeSpan.Zero then
                let tickDuration = if consumerConfig.AckTimeout > consumerConfig.TickDuration then consumerConfig.TickDuration else consumerConfig.TickDuration
                UnAckedMessageTracker(prefix, consumerConfig.AckTimeout, tickDuration, redeliverMessages) :> IUnAckedMessageTracker
            else
                UnAckedMessageTracker(prefix, consumerConfig.AckTimeout, consumerConfig.AckTimeout, redeliverMessages) :> IUnAckedMessageTracker
        else
            UnAckedMessageTracker.UNACKED_MESSAGE_TRACKER_DISABLED

    let acksGroupingTracker =
        if consumerConfig.Topic.IsPersistent then
            AcknowledgmentsGroupingTracker(prefix, consumerId, consumerConfig.AcknowledgementsGroupTime, connectionHandler) :> IAcknowledgmentsGroupingTracker
        else
            AcknowledgmentsGroupingTracker.NonPersistentAcknowledgmentGroupingTracker

    let sendAcknowledge (messageId: MessageId) ackType =
        async {
            match ackType with
            | AckType.Individual ->
                //TODO
                ()
            | AckType.Cumulative ->
                //TODO
                ()
            do! acksGroupingTracker.AddAcknowledgment(messageId, ackType)
            // Consumer acknowledgment operation immediately succeeds. In any case, if we're not able to send ack to broker,
            // the messages will be re-delivered
        }

    let markAckForBatchMessage msgId ackType (batchDetails: BatchDetails) =
        let (batchIndex, batchAcker) = batchDetails
        let isAllMsgsAcked =
            match ackType with
            | AckType.Individual ->
                batchAcker.AckIndividual(batchIndex)
            | AckType.Cumulative ->
                batchAcker.AckGroup(batchIndex)
        let outstandingAcks = batchAcker.GetOutstandingAcks()
        let batchSize = batchAcker.GetBatchSize()
        if isAllMsgsAcked then
            Log.Logger.LogDebug("{0} can ack message acktype {1}, cardinality {2}, length {3}",
                prefix, ackType, outstandingAcks, batchSize)
            true
        else
            match ackType with
            | AckType.Cumulative when not batchAcker.PrevBatchCumulativelyAcked ->
                sendAcknowledge msgId ackType |> Async.StartImmediate
                batchAcker.PrevBatchCumulativelyAcked <- true
            | _ ->
                ()
            Log.Logger.LogDebug("{0} cannot ack message acktype {1}, cardinality {2}, length {3}",
                prefix, ackType, outstandingAcks, batchSize)
            false

    let receiveIndividualMessagesFromBatch (msg: Message) =
        let batchSize = msg.Metadata.NumMessages
        let acker = BatchMessageAcker(batchSize)
        use stream = new MemoryStream(msg.Payload)
        use binaryReader = new BinaryReader(stream)
        for i in 0..batchSize-1 do
            Log.Logger.LogDebug("{0} processing message num - {1} in batch", prefix, i)
            let singleMessageMetadata = Serializer.DeserializeWithLengthPrefix<SingleMessageMetadata>(stream, PrefixStyle.Fixed32BigEndian)
            let singleMessagePayload = binaryReader.ReadBytes(singleMessageMetadata.PayloadSize)
            //TODO handle non-durable with StartMessageId
            let messageId = { LedgerId = msg.MessageId.LedgerId; EntryId = msg.MessageId.EntryId; Partition = partitionIndex; Type = Cumulative(%i,acker) }
            let message = { msg with MessageId = messageId; Payload = singleMessagePayload }
            incomingMessages.Enqueue(message)


    /// Record the event that one message has been processed by the application.
    /// Periodically, it sends a Flow command to notify the broker that it can push more messages
    let messageProcessed (msg: Message) =
        increaseAvailablePermits 1
        if consumerConfig.AckTimeout <> TimeSpan.Zero then
            if (partitionIndex <> -1) then
                // we should no longer track this message, TopicsConsumer will take care from now onwards
                unAckedMessageTracker.Remove msg.MessageId |> ignore
            else
                unAckedMessageTracker.Add msg.MessageId |> ignore

    let mb = MailboxProcessor<ConsumerMessage>.Start(fun inbox ->

        let rec loop (state: ConsumerState) =
            async {
                let! msg = inbox.Receive()
                match msg with
                | ConsumerMessage.ConnectionOpened ->

                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        Log.Logger.LogInformation("{0} starting subscribe to topic {1}", prefix, consumerConfig.Topic)
                        clientCnx.AddConsumer consumerId this.Mb
                        let requestId = Generators.getNextRequestId()
                        let payload =
                            Commands.newSubscribe
                                consumerConfig.Topic.CompleteTopicName consumerConfig.SubscriptionName
                                consumerId requestId consumerConfig.ConsumerName consumerConfig.SubscriptionType
                                consumerConfig.SubscriptionInitialPosition
                        try
                            let! response = clientCnx.SendAndWaitForReply requestId payload |> Async.AwaitTask
                            response |> PulsarResponseType.GetEmpty
                            Log.Logger.LogInformation("{0} subscribed", prefix)
                            connectionHandler.ResetBackoff()
                            let initialFlowCount = consumerConfig.ReceiverQueueSize
                            let firstTimeConnect = subscribeTsc.TrySetResult(this)
                            let isDurable = subscriptionMode = SubscriptionMode.Durable;
                            // if the consumer is not partitioned or is re-connected and is partitioned, we send the flow
                            // command to receive messages.
                            // For readers too (isDurable==false), the partition idx will be set though we have to
                            // send available permits immediately after establishing the reader session
                            if (not (firstTimeConnect && partitionIndex > -1 && isDurable) && consumerConfig.ReceiverQueueSize <> 0) then
                                let flowCommand = Commands.newFlow consumerId initialFlowCount
                                let! success = clientCnx.Send flowCommand
                                if success then
                                    Log.Logger.LogInformation("{0} initial flow sent {1}", prefix, initialFlowCount)
                                else
                                    raise (ConnectionFailedOnSend "FlowCommand")
                        with
                        | ex ->
                            clientCnx.RemoveConsumer consumerId
                            Log.Logger.LogError(ex, "{0} failed to subscribe to topic", prefix)
                            if (connectionHandler.IsRetriableError ex) || not (subscribeTsc.TrySetException ex)  then
                                // Either we had already created the consumer once (subscribeFuture.isDone()) or we are
                                // still within the initial timeout budget and we are dealing with a retriable error
                                connectionHandler.ReconnectLater ex
                            else
                                // unable to create new consumer, fail operation
                                connectionHandler.Failed()
                    | _ ->
                        Log.Logger.LogWarning("{0} connection opened but connection is not ready", prefix)
                    return! loop state

                | ConsumerMessage.ConnectionClosed clientCnx ->

                    Log.Logger.LogDebug("{0} ConnectionClosed", prefix)
                    let clientCnx = clientCnx :?> ClientCnx
                    connectionHandler.ConnectionClosed clientCnx
                    clientCnx.RemoveConsumer(consumerId)
                    return! loop state

                | ConsumerMessage.ConnectionFailed ex ->

                    Log.Logger.LogDebug("{0} ConnectionFailed", prefix)
                    if (DateTime.Now > subscribeTimeout && subscribeTsc.TrySetException(ex)) then
                        Log.Logger.LogInformation("{0} creation failed", prefix)
                        connectionHandler.Failed()
                    return! loop state

                | ConsumerMessage.MessageReceived message ->

                    let hasWaitingChannel = state.WaitingChannel <> nullChannel
                    Log.Logger.LogDebug("{0} MessageReceived {1} queueLength={2}, hasWatingChannel={3}",
                        prefix, message.MessageId, incomingMessages.Count, hasWaitingChannel)
                    if (acksGroupingTracker.IsDuplicate(message.MessageId)) then
                        Log.Logger.LogInformation("{0} Ignoring message as it was already being acked earlier by same consumer {1}", prefix, message.MessageId)
                        increaseAvailablePermits message.Metadata.NumMessages
                    else
                        if (message.Metadata.NumMessages = 1 && not message.Metadata.HasNumMessagesInBatch) then
                            if not hasWaitingChannel then
                                incomingMessages.Enqueue(message)
                                return! loop state
                            else
                                if (incomingMessages.Count = 0) then
                                    messageProcessed message
                                    state.WaitingChannel.Reply <| message
                                else
                                    incomingMessages.Enqueue(message)
                                    let nextMessage = incomingMessages.Dequeue()
                                    messageProcessed nextMessage
                                    state.WaitingChannel.Reply nextMessage
                                return! loop { state with WaitingChannel = nullChannel }
                        else
                             // handle batch message enqueuing; uncompressed payload has all messages in batch
                            receiveIndividualMessagesFromBatch message
                            if hasWaitingChannel then
                                state.WaitingChannel.Reply <| incomingMessages.Dequeue()
                                return! loop { state with WaitingChannel = nullChannel }
                            else
                                return! loop state

                | ConsumerMessage.Receive ch ->

                    Log.Logger.LogDebug("{0} Receive", prefix)
                    if incomingMessages.Count > 0 then
                        let msg = incomingMessages.Dequeue()
                        messageProcessed msg
                        ch.Reply msg
                        return! loop state
                    else
                        Log.Logger.LogDebug("{0} GetMessage waiting", prefix)
                        return! loop { state with WaitingChannel = ch }

                | ConsumerMessage.Acknowledge (messageId, ackType, channel) ->

                    Log.Logger.LogDebug("{0} Acknowledge", prefix)
                    match connectionHandler.ConnectionState with
                    | Ready _ ->
                        match messageId.Type with
                        | Cumulative batchDetails when not (markAckForBatchMessage messageId ackType batchDetails) ->
                            // other messages in batch are still pending ack.
                            ()
                        | _ ->
                            do! sendAcknowledge messageId ackType
                            Log.Logger.LogDebug("{0} acknowledged message - {1}, acktype {2}", prefix, messageId, ackType)
                        channel.Reply(true)
                    | _ ->
                        Log.Logger.LogWarning("{0} not connected, skipping send", prefix)
                        channel.Reply(false)
                    return! loop state

                | ConsumerMessage.RedeliverUnacknowledged (messageIds, channel) ->

                    Log.Logger.LogDebug("{0} RedeliverUnacknowledged", prefix)
                    if Seq.isEmpty messageIds |> not then
                        match consumerConfig.SubscriptionType with
                        | SubscriptionType.Shared | SubscriptionType.KeyShared ->
                            this.Mb.Post(RedeliverAllUnacknowledged channel)
                            Log.Logger.LogInformation("{0} We cannot redeliver single messages if subscription type is not Shared", prefix)
                        | _ ->
                            match connectionHandler.ConnectionState with
                            | Ready clientCnx ->
                                let batches = messageIds |> Seq.chunkBySize MAX_REDELIVER_UNACKNOWLEDGED
                                for batch in batches do
                                    let command = Commands.newRedeliverUnacknowledgedMessages consumerId (Some(batch))
                                    let! success = clientCnx.Send command
                                    if success then
                                        Log.Logger.LogDebug("{0} RedeliverAcknowledged complete", prefix)
                                    else
                                        Log.Logger.LogWarning("{0} RedeliverAcknowledged was not complete", prefix)
                            | _ ->
                                Log.Logger.LogWarning("{0} not connected, skipping send", prefix)
                            channel.Reply()
                    return! loop state

                | ConsumerMessage.RedeliverAllUnacknowledged channel ->

                    Log.Logger.LogDebug("{0} RedeliverAllUnacknowledged", prefix)
                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        let command = Commands.newRedeliverUnacknowledgedMessages consumerId None
                        let! success = clientCnx.Send command
                        if success then
                            let currentSize = incomingMessages.Count
                            if currentSize > 0 then
                                incomingMessages.Clear()
                                increaseAvailablePermits currentSize
                            Log.Logger.LogDebug("{0} RedeliverAllUnacknowledged complete", prefix)
                        else
                            Log.Logger.LogWarning("{0} RedeliverAllUnacknowledged was not complete", prefix)
                    | _ ->
                        Log.Logger.LogWarning("{0} not connected, skipping send", prefix)
                    channel.Reply()
                    return! loop state

                | ConsumerMessage.SendFlowPermits numMessages ->

                    Log.Logger.LogDebug("{0} SendFlowPermits {1}", prefix, numMessages)
                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        let flowCommand = Commands.newFlow consumerId numMessages
                        let! success = clientCnx.Send flowCommand
                        if not success then
                            Log.Logger.LogWarning("{0} failed SendFlowPermits {1}", prefix, numMessages)
                    | _ ->
                        Log.Logger.LogWarning("{0} not connected, skipping SendFlowPermits {1}", prefix, numMessages)
                    return! loop state

                | ConsumerMessage.ReachedEndOfTheTopic ->

                    Log.Logger.LogWarning("{0} ReachedEndOfTheTopic", prefix)
                    //TODO notify client app that topic end reached
                    connectionHandler.Terminate()

                | ConsumerMessage.Close channel ->

                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        connectionHandler.Closing()
                        // TODO failPendingReceive
                        Log.Logger.LogInformation("{0} starting close", prefix)
                        let requestId = Generators.getNextRequestId()
                        let payload = Commands.newCloseConsumer consumerId requestId
                        task {
                            try
                                let! response = clientCnx.SendAndWaitForReply requestId payload |> Async.AwaitTask
                                response |> PulsarResponseType.GetEmpty
                                clientCnx.RemoveConsumer(consumerId)
                                unAckedMessageTracker.Close();
                                connectionHandler.Closed()
                                Log.Logger.LogInformation("{0} closed", prefix)
                            with
                            | ex ->
                                Log.Logger.LogError(ex, "{0} failed to close", prefix)
                                reraize ex
                        } |> channel.Reply
                    | _ ->
                        Log.Logger.LogInformation("{0} closing but current state {1}", prefix, connectionHandler.ConnectionState)
                        unAckedMessageTracker.Close();
                        connectionHandler.Closed()
                        channel.Reply(Task.FromResult())
                    return! loop state

                | ConsumerMessage.Unsubscribe channel ->

                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        connectionHandler.Closing()
                        unAckedMessageTracker.Close()
                        Log.Logger.LogInformation("{0} starting unsubscribe ", prefix)
                        let requestId = Generators.getNextRequestId()
                        let payload = Commands.newUnsubscribeConsumer consumerId requestId
                        task {
                            try
                                let! response = clientCnx.SendAndWaitForReply requestId payload |> Async.AwaitTask
                                response |> PulsarResponseType.GetEmpty
                                clientCnx.RemoveConsumer(consumerId)
                                connectionHandler.Closed()
                                Log.Logger.LogInformation("{0} unsubscribed", prefix)
                            with
                            | ex ->
                                connectionHandler.SetReady clientCnx
                                Log.Logger.LogError(ex, "{0} failed to unsubscribe", prefix)
                                reraize ex
                        } |> channel.Reply
                    | _ ->
                        Log.Logger.LogError("{0} can't unsubscribe since connection already closed", prefix)
                        channel.Reply(Task.FromException(Exception("Not connected to broker")))
                    return! loop state
            }
        loop { WaitingChannel = nullChannel }
    )

    member this.ReceiveAsync() =
        task {
            return! mb.PostAndAsyncReply(Receive)
        }

    member this.AcknowledgeAsync (msgId: MessageId) =
        task {
            let! success = mb.PostAndAsyncReply(fun channel -> Acknowledge (msgId, AckType.Individual, channel))
            if success then
                unAckedMessageTracker.Remove(msgId) |> ignore
            else
                raise (ConnectionFailedOnSend "AcknowledgeAsync")
        }

    member this.AcknowledgeCumulativeAsync (msgId: MessageId) =
        task {
            let! success = mb.PostAndAsyncReply(fun channel -> Acknowledge (msgId, AckType.Cumulative, channel))
            if success then
                unAckedMessageTracker.Remove(msgId) |> ignore
            else
                raise (ConnectionFailedOnSend "AcknowledgeCumulativeAsync")
        }

    member this.RedeliverUnacknowledgedMessagesAsync () =
        task {
            do! mb.PostAndAsyncReply(RedeliverAllUnacknowledged)
        }

    member this.RedeliverUnacknowledgedMessagesAsync messages =
        task {
            do! mb.PostAndAsyncReply(fun channel -> RedeliverUnacknowledged (messages, channel))
        }

    member this.CloseAsync() =
        task {
            let! result = mb.PostAndAsyncReply(ConsumerMessage.Close)
            return! result
        }

    member this.UnsubscribeAsync() =
        task {
            let! result = mb.PostAndAsyncReply(ConsumerMessage.Unsubscribe)
            return! result
        }

    member private this.InitInternal() =
        task {
            do connectionHandler.GrabCnx()
            return! subscribeTsc.Task
        }

    member private this.Mb with get(): MailboxProcessor<ConsumerMessage> = mb

    static member Init(consumerConfig: ConsumerConfiguration, subscriptionMode: SubscriptionMode, lookup: BinaryLookupService) =
        task {
            let consumer = Consumer(consumerConfig, subscriptionMode, lookup)
            return! consumer.InitInternal()
        }



