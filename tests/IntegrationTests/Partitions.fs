module Pulsar.Client.IntegrationTests.Partitions

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open Expecto
open Expecto.Flip
open Pulsar.Client.Api
open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Text
open System.Threading.Tasks
open Pulsar.Client.Common
open Serilog
open Pulsar.Client.IntegrationTests.Common
open FSharp.Control

[<CLIMutable>]
type SimpleRecord =
    {
        Name: string
        Age: int
    }
    
[<Tests>]
let tests =

    testList "partitions" [

        testAsync "Single producer and single consumer with 3 partitions" {

            Log.Debug("Started Single producer and single consumer with 3 partitions")
            let client = getClient()
            let topicName = "public/default/partitioned"
            let consumerName = "PartitionedConsumer"
            let producerName = "PartitionedProducer"

            let! producer =
                client.NewProducer()
                    .Topic(topicName)
                    .ProducerName(producerName)
                    .CreateAsync() |> Async.AwaitTask

            let! consumer =
                client.NewConsumer()
                    .Topic(topicName)
                    .SubscriptionName("test-subscription")
                    .ConsumerName(consumerName)
                    .AcknowledgementsGroupTime(TimeSpan.FromMilliseconds(50.0))
                    .SubscribeAsync() |> Async.AwaitTask

            let messages = generateMessages 100 producerName

            let producerTask =
                Task.Run(fun () ->
                    task {
                        do! producePredefinedMessages producer messages
                    }:> Task)

            let consumerTask =
                Task.Run(fun () ->
                    task {
                        do! consumeAndVerifyMessages consumer consumerName messages
                    }:> Task)

            do! Task.WhenAll(producerTask, consumerTask) |> Async.AwaitTask
            do! Async.Sleep(110) // wait for acks

            Log.Debug("Finished Single producer and single consumer with 3 partitions")

        }

        // make active after https://github.com/apache/pulsar/issues/5877 is resolved
        testAsync "Two producers and two consumers with 2 partitions" {

            Log.Debug("Started Two producers and two consumers with 2 partitions")
            let client = getClient()
            let topicName = "public/default/partitioned2"
            let consumerName1 = "PartitionedConsumer1"
            let consumerName2 = "PartitionedConsumer2"
            let producerName1 = "PartitionedProducer1"
            let producerName2 = "PartitionedProducer2"

            let! producer1 =
                client.NewProducer()
                    .Topic(topicName)
                    .ProducerName(producerName1)
                    .MessageRoutingMode(MessageRoutingMode.SinglePartition)
                    .CreateAsync() |> Async.AwaitTask

            let! producer2 =
                client.NewProducer()
                    .Topic(topicName)
                    .ProducerName(producerName2)
                    .MessageRoutingMode(MessageRoutingMode.SinglePartition)
                    .CreateAsync() |> Async.AwaitTask

            let! consumer1 =
                client.NewConsumer()
                    .Topic(topicName)
                    .SubscriptionName("test-subscription")
                    .SubscriptionType(SubscriptionType.Shared)
                    .AcknowledgementsGroupTime(TimeSpan.FromMilliseconds(50.0))
                    .ConsumerName(consumerName1)
                    .SubscribeAsync() |> Async.AwaitTask

            let! consumer2 =
                client.NewConsumer()
                    .Topic(topicName)
                    .SubscriptionName("test-subscription")
                    .SubscriptionType(SubscriptionType.Shared)
                    .AcknowledgementsGroupTime(TimeSpan.FromMilliseconds(50.0))
                    .ConsumerName(consumerName2)
                    .SubscribeAsync() |> Async.AwaitTask

            let messages1 = generateMessages 50 producerName1
            let messages2 = generateMessages 50 producerName2
            let allMessages =  [| yield! messages1; yield! messages2 |]

            let producerTask1 =
                Task.Run(fun () ->
                    task {
                        do! producePredefinedMessages producer1 messages1
                    }:> Task)

            let producerTask2 =
                Task.Run(fun () ->
                    task {
                        do! producePredefinedMessages producer2 messages2
                    }:> Task)

            let getStream (consumer: IConsumer<'T>) =
                asyncSeq {
                    while not consumer.HasReachedEndOfTopic do
                        let! message = (consumer.ReceiveAsync() |> Async.AwaitTask)
                        do! consumer.AcknowledgeAsync(message.MessageId) |> Async.AwaitTask
                        yield message
                }

            let stream1 = getStream consumer1
            let stream2 = getStream consumer2
            let resultStream = AsyncSeq.mergeAll([stream1;stream2])
            let enumerator = resultStream.GetEnumerator()

            let consumerTask =
                Task.Run(fun () ->
                    task {
                        for _ in [1..100] do
                            let! message = enumerator.MoveNext()
                            let received = Encoding.UTF8.GetString(message.Value.Data)
                            Log.Debug("Some consumer received {1}", received)
                            if allMessages |> Array.contains received |> not then
                                failwith <| sprintf "Received unexpected message '%s'" received
                    }:> Task)

            do! Task.WhenAll(producerTask1, producerTask2, consumerTask) |> Async.AwaitTask
            do! Async.Sleep(110) // wait for acks

            Log.Debug("Finished Two producers and two consumers with 2 partitions")
        }
        
        testAsync "Batch read works with partitions" {

            Log.Debug("Started Batch read works with partitions")
            let client = getClient()
            let topicName = "public/default/partitioned3"
            let consumerName = "PartitionedConsumerBatchRead"
            let producerName = "PartitionedProducerBatchRead"
            let batchTimeout = TimeSpan.FromSeconds(2.0)
            let numberOfMessages = 10
            let batchSize = 8

            let! producer =
                client.NewProducer()
                    .Topic(topicName)
                    .ProducerName(producerName)
                    .EnableBatching(false)
                    .CreateAsync() |> Async.AwaitTask

            let! consumer =
                client.NewConsumer()
                    .Topic(topicName)
                    .SubscriptionName("test-subscription")
                    .ConsumerName(consumerName)
                    .BatchReceivePolicy(BatchReceivePolicy(batchSize, -1L, batchTimeout))
                    .SubscribeAsync() |> Async.AwaitTask

            let messages = generateMessages numberOfMessages producerName

            let producerTask =
                Task.Run(fun () ->
                    task {
                        do! producePredefinedMessages producer messages
                    }:> Task)

            let consumerTask =
                Task.Run(fun () ->
                    task {
                        let sw = Stopwatch()
                        sw.Start()
                        let! messagesBatch = consumer.BatchReceiveAsync()
                        let firstBatchTime = sw.Elapsed
                        if firstBatchTime > TimeSpan.FromSeconds(0.5) then
                                failwith <| sprintf "Too long to receive first batch consumer %s passed %f ms" consumerName firstBatchTime.TotalMilliseconds
                        if messagesBatch.Count <> batchSize then
                            failwith <| sprintf "Wrong number of messages received %i consumer %s" messagesBatch.Count consumerName
                        for message in messagesBatch do    
                            let received = Encoding.UTF8.GetString(message.Data)                        
                            Log.Debug("{0} received {1}", consumerName, received)
                            if messages |> Array.contains received |> not then
                                failwith <| sprintf "Incorrect message received %s consumer %s" received consumerName
                        do! consumer.AcknowledgeAsync(messagesBatch)
                        sw.Restart()
                        let! messagesBatch2 = consumer.BatchReceiveAsync()
                        let secondBatchTime = sw.Elapsed
                        if secondBatchTime < (batchTimeout - TimeSpan.FromMilliseconds(15.0)) then
                                failwith <| sprintf "Too fast to get second batch consumer %s passed %f ms" consumerName secondBatchTime.TotalMilliseconds
                        if messagesBatch2.Count <> (numberOfMessages - batchSize) then
                            failwith <| sprintf "Wrong number of messages2 received %i consumer %s" messagesBatch2.Count consumerName
                        for message in messagesBatch2 do    
                            let received = Encoding.UTF8.GetString(message.Data)                        
                            Log.Debug("{0} received {1}", consumerName, received)
                            if messages |> Array.contains received |> not then
                                failwith <| sprintf "Incorrect message received %s consumer %s" received consumerName
                        do! consumer.AcknowledgeAsync(messagesBatch2)
                            
                        
                    } :> Task)

            do! Task.WhenAll(producerTask, consumerTask) |> Async.AwaitTask
            do! Async.Sleep(110) // wait for acks

            Log.Debug("Finished Batch read works with partitions")

        }
        
        testAsync "Auto schema works fine with partitioned topic" {

            Log.Debug("Start Auto schema works fine with partitioned topic")
            let client = getClient()
            let topicName1 = "public/default/partitioned4"
            let topicName2 = "public/default/partitioned5"

            let! producer =
                client.NewProducer(Schema.JSON<SimpleRecord>())
                    .Topic(topicName1)
                    .ProducerName("autoPartitionedNormal")
                    .EnableBatching(false)
                    .CreateAsync() |> Async.AwaitTask

            let! consumer =
                client.NewConsumer(Schema.AUTO_CONSUME())
                    .Topic(topicName1)
                    .ConsumerName("autoPartitionedConsume")
                    .SubscriptionName("test-subscription")
                    .SubscribeAsync() |> Async.AwaitTask

            let! consumer2 =
                client.NewConsumer(Schema.JSON<SimpleRecord>())
                    .Topic(topicName2)
                    .ConsumerName("autoNormal")
                    .SubscriptionName("test-subscription")
                    .SubscriptionInitialPosition(SubscriptionInitialPosition.Earliest)
                    .SubscribeAsync() |> Async.AwaitTask
            
            let! producer2 =
                client.NewProducer(Schema.AUTO_PRODUCE())
                    .Topic(topicName2)
                    .ProducerName("autoProduce")
                    .EnableBatching(false)
                    .CreateAsync() |> Async.AwaitTask
            
            let messages = [|
                    for i in [1..10] do
                        yield { Name = "Name"; Age = i }
                |]
            
            for message in messages do
                let! _ = producer.SendAsync(message) |> Async.AwaitTask
                ()

            let firstDict =
                messages
                |> Seq.map (fun msg -> msg.Age, msg)
                |> dict
                |> Dictionary
            for i in [1..10] do
                let! msg1 = consumer.ReceiveAsync() |> Async.AwaitTask
                do! consumer.AcknowledgeAsync msg1.MessageId |> Async.AwaitTask
                let genericRecord = msg1.GetValue()
                Expect.equal "" "Name" (genericRecord.GetField("Name") |> unbox)
                firstDict.Remove(genericRecord.GetField("Age") |> unbox<float> |> int) |> ignore
                let! _ = producer2.SendAsync(msg1.Data) |> Async.AwaitTask
                ()
            Expect.isEmpty "firstDict" firstDict
            
            let secondDict =
                messages
                |> Seq.map (fun msg -> msg.Age, msg)
                |> dict
                |> Dictionary
            for i in [1..10] do
                let! msg1 = consumer2.ReceiveAsync() |> Async.AwaitTask
                do! consumer2.AcknowledgeAsync msg1.MessageId |> Async.AwaitTask
                let jsonRecord = msg1.GetValue()
                Expect.equal "" "Name" jsonRecord.Name
                secondDict.Remove(jsonRecord.Age) |> ignore
            Expect.isEmpty "secondDict" secondDict            
            
            Log.Debug("Finished Auto schema works fine with partitioned topic")
        }

    ]
