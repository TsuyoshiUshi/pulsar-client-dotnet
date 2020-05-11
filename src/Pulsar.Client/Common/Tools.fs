﻿[<AutoOpen>]
module Pulsar.Client.Common.Tools

open System.Net
open System
open Microsoft.IO
open System.Runtime.ExceptionServices
open System.Collections.Generic

let internal MemoryStreamManager = RecyclableMemoryStreamManager()
let internal MagicNumber = int16 0x0e01
let internal RandomGenerator = Random()
let internal EmptyProps: IReadOnlyDictionary<string, string> = readOnlyDict []

// Converts

let inline int32ToBigEndian(num : Int32) =
    IPAddress.HostToNetworkOrder(num)

let inline int32FromBigEndian(num : Int32) =
    IPAddress.NetworkToHostOrder(num)
    
let inline int16ToBigEndian(num : Int16) =
    IPAddress.HostToNetworkOrder(num)
let inline int16FromBigEndian(num : Int16) =
    IPAddress.NetworkToHostOrder(num)

let inline int64ToBigEndian(num : Int64) =
    IPAddress.HostToNetworkOrder(num)

let inline int64FromBigEndian(num : Int64) =
    IPAddress.NetworkToHostOrder(num)

// Exception helper

let throwIf predicate createException arg =
    if predicate(arg) then
        raise(createException())
    else
        arg

let invalidArgIf predicate message =
    throwIf predicate (fun() -> ArgumentException(message))

let invalidArgIfTrue value message =
    if value then raise (ArgumentException(message))

let invalidArgIfBlankString =
    invalidArgIf (String.IsNullOrWhiteSpace)

let invalidArgIfNotGreaterThanZero =
    invalidArgIf ((>=) 0)

let invalidArgIfLessThanZero =
    invalidArgIf ((>) 0)

let invalidArgIfDefault msg =
    invalidArgIf (fun (arg) -> arg = Unchecked.defaultof<'a>) msg

let reraize<'a> ex =
    (ExceptionDispatchInfo.Capture ex).Throw()
    Unchecked.defaultof<'a>

let throwIfNotNull (exn:Exception) = if not(isNull exn) then raise exn

// DateTime conversions

let UTC_EPOCH = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
let convertToMsTimestamp dateTime =
    let elapsed = dateTime - UTC_EPOCH
    elapsed.TotalMilliseconds |> int64

let convertToDateTime (msTimestamp: int64) =
    let ms = msTimestamp |> float
    UTC_EPOCH.AddMilliseconds ms

// Mix

let asyncDelay delay work =
    async {
        do! Async.Sleep delay
        work()
    } |> Async.StartImmediate
    
let asyncCancellableDelay delay work ct =
    Async.StartImmediate(async {
        do! Async.Sleep delay
        work()
    }, ct)

let signSafeMod dividend divisor =
    let modulo = dividend % divisor
    if modulo < 0
    then modulo + divisor
    else modulo