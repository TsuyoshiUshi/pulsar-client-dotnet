﻿using System;
using System.Text;
using System.Threading.Tasks;
using Pulsar.Client.Api;

namespace CsharpExamples
{
    internal class Simple
    {
        internal static async Task RunSimple()
        {
            const string serviceUrl = "pulsar://my-pulsar-cluster:30002";
            const string subscriptionName = "my-subscription";
            var topicName = $"my-topic-{DateTime.Now.Ticks}";

            var client = new PulsarClientBuilder()
                .ServiceUrl(serviceUrl)
                .Build();

            var producer = await new ProducerBuilder(client)
                .Topic(topicName)
                .CreateAsync();

            var consumer = await new ConsumerBuilder(client)
                .Topic(topicName)
                .SubscriptionName(subscriptionName)
                .SubscribeAsync();

            var messageId = await producer.SendAsync(Encoding.UTF8.GetBytes($"Sent from C# at '{DateTime.Now}'"));
            Console.WriteLine($"MessageId is: '{messageId}'");

            var message = await consumer.ReceiveAsync();
            Console.WriteLine($"Received: {Encoding.UTF8.GetString(message.Data)}");

            await consumer.AcknowledgeAsync(message.MessageId);
        }
    }
}