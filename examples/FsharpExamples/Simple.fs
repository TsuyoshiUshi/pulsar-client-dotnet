﻿module Simple

// Learn more about F# at http://fsharp.org
open System
open System.Diagnostics
open Pulsar.Client.Api

open Pulsar.Client.Common
open System.Text

let runSimple () =

    let serviceUrl = "pulsar://127.0.0.1:6650"
    let subscriptionName = "my-subscription"
    let topicName = sprintf "my-topic-%i" DateTime.Now.Ticks;


    task {
        let! client =
            PulsarClientBuilder()
                .ServiceUrl(serviceUrl)
                .BuildAsync()

        let! producer =
            client.NewProducer()
                .Topic(topicName)
                .BlockIfQueueFull(true)
                .CreateAsync()

        // let! consumer =
        //     client.NewConsumer()
        //         .Topic(topicName)
        //         .SubscriptionName(subscriptionName)
        //         .SubscriptionType(SubscriptionType.Exclusive)
        //         .SubscribeAsync()

        let n = 10000000
        let bytes = Array.zeroCreate 750

        // warmup
        for _ = 0 to n/10 do
            do! producer.SendAndForgetAsync(bytes)

        let sw = Stopwatch()
        sw.Start()
        for i = 0 to n do
            do! producer.SendAndForgetAsync(bytes)
            if i % 100000 = 0 then
                Console.WriteLine($"Sent {i} messages")
        sw.Stop()
        Console.WriteLine($"Sent {n} messages in {sw.ElapsedMilliseconds}ms. Speed: {n / (int sw.ElapsedMilliseconds)}K msg/s")

        // let! message = consumer.ReceiveAsync()
        // printfn "Received: %A" (message.Data |> Encoding.UTF8.GetString)
        //
        // do! consumer.AcknowledgeAsync(message.MessageId)
    }
