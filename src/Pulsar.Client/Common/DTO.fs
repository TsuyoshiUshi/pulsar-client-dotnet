﻿namespace Pulsar.Client.Common

open System.Net
open System
open pulsar.proto
open System.IO.Pipelines
open Pipelines.Sockets.Unofficial
open System.IO
open System.Threading.Tasks
open FSharp.UMX
open FSharp.Control.Tasks.V2.ContextInsensitive
open Pulsar.Client.Internal
open Microsoft.Extensions.Logging

type ChecksumType =
    | Crc32c
    | No

type PartitionedTopicMetadata =
    {
        Partitions: uint32
    }

type ProducerSuccess =
    {
        GeneratedProducerName: string
    }

type LookupTopicResult =
    {
        Proxy: bool
        BrokerServiceUrl : string
    }

type TopicsOfNamespace =
    {
        Topics : string list
    }

type SubscriptionType =
    | Exclusive = 0
    | Shared = 1
    | Failover = 2

type SubscriptionInitialPosition =
    | Latest = 0
    | Earliest = 1

type TopicDomain =
    | Persistent
    | NonPersistent

type MessageId =
    {
        LedgerId: LedgerId
        EntryId: EntryId
        Partition: int
    }
    with
        static member FromMessageIdData(messageIdData: MessageIdData) =
            {
                LedgerId = %messageIdData.ledgerId
                EntryId = %messageIdData.entryId
                Partition = messageIdData.Partition
            }
        member this.ToMessageIdData() =
            MessageIdData(
                ledgerId = %this.LedgerId,
                entryId = %this.EntryId,
                Partition = this.Partition
            )

type LogicalAddress = LogicalAddress of DnsEndPoint
type PhysicalAddress = PhysicalAddress of DnsEndPoint

type Broker =
    {
        LogicalAddress: LogicalAddress
        PhysicalAddress: PhysicalAddress
    }

type Message =
    {
        MessageId: MessageId
        Payload: byte[]
    }

type WriterStream = Stream
type Payload = WriterStream -> Task
type Connection = SocketConnection * WriterStream

type PendingMessage =
    {
        SequenceId: SequenceId
        Payload: Payload
        Tcs : TaskCompletionSource<MessageId>
    }

type PulsarResponseType =
    | PartitionedTopicMetadata of PartitionedTopicMetadata
    | LookupTopicResult of LookupTopicResult
    | ProducerSuccess of ProducerSuccess
    | TopicsOfNamespace of TopicsOfNamespace
    | Error
    | Empty

    static member GetPartitionedTopicMetadata req =
        match req with
        | PartitionedTopicMetadata x -> x
        | _ -> failwith "Incorrect return type"

    static member GetLookupTopicResult req =
        match req with
        | LookupTopicResult x -> x
        | _ -> failwith "Incorrect return type"

    static member GetProducerSuccess req =
        match req with
        | ProducerSuccess x -> x
        | _ -> failwith "Incorrect return type"

    static member GetTopicsOfNamespace req =
        match req with
        | TopicsOfNamespace x -> x
        | _ -> failwith "Incorrect return type"

    static member GetEmpty req =
        match req with
        | Empty -> ()
        | _ -> failwith "Incorrect return type"

type ProducerMessage =
    | ConnectionOpened
    | ConnectionFailed of exn
    | ConnectionClosed
    | SendReceipt of CommandSendReceipt
    | BeginSendMessage of byte[] * AsyncReplyChannel<TaskCompletionSource<MessageId>>
    | SendMessage of PendingMessage
    | SendError of CommandSendError
    | Close of AsyncReplyChannel<Task>

type ConsumerMessage =
    | ConnectionOpened
    | ConnectionFailed of exn
    | ConnectionClosed
    | ReachedEndOfTheTopic
    | MessageReceived of Message
    | GetMessage of AsyncReplyChannel<Message>
    | Send of Payload * AsyncReplyChannel<unit>
    | Close of AsyncReplyChannel<Task>
    | Unsubscribe of AsyncReplyChannel<Task>

exception InvalidServiceURL
exception InvalidConfigurationException of string
exception NotFoundException of string
exception TimeoutException of string
exception IncompatibleSchemaException of string
exception LookupException of string
exception TooManyRequestsException of string
exception ConnectException of string
exception AlreadyClosedException of string
exception TopicTerminatedException of string
exception AuthenticationException of string
exception AuthorizationException of string
exception GettingAuthenticationDataException of string
exception UnsupportedAuthenticationException of string
exception BrokerPersistenceException of string
exception BrokerMetadataException of string
exception ProducerBusyException of string
exception ConsumerBusyException of string
exception NotConnectedException of string
exception InvalidMessageException of string
exception InvalidTopicNameException of string
exception NotSupportedException of string
exception ProducerQueueIsFullError of string
exception ProducerBlockedQuotaExceededError of string
exception ProducerBlockedQuotaExceededException of string
exception ChecksumException of string
exception CryptoExceptionof of string


