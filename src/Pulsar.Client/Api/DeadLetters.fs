﻿namespace Pulsar.Client.Api

open Pulsar.Client.Common
open System.Runtime.InteropServices
open System.Threading.Tasks

type DeadLetterPolicy(maxRedeliveryCount: int
                       , [<Optional; DefaultParameterValue(null:string)>] deadLetterTopic: string
                       , [<Optional; DefaultParameterValue(null:string)>] retryLetterTopic: string
                       ) =
    member __.MaxRedeliveryCount = maxRedeliveryCount
    member __.DeadLetterTopic = deadLetterTopic
    member __.RetryLetterTopic = retryLetterTopic

type IDeadLetterProcessor<'T> =
    abstract member ClearMessages: unit -> unit
    abstract member AddMessage: MessageId -> Message<'T> -> unit
    abstract member RemoveMessage: MessageId -> unit
    abstract member ProcessMessages: MessageId -> (MessageId -> Async<unit>) -> Task<bool>
    abstract member MaxRedeliveryCount: uint32