module Pulsar.Client.UnitTests.Common.MessageTests

open Expecto
open Expecto.Flip
open Pulsar.Client.Common
open FSharp.UMX
open Pulsar.Client.Internal

[<Tests>]
let tests =

    testList "MessageIdTests" [
        test "Less works correctly" {
            let msgId1 = { LedgerId = %1L; EntryId = %1L; Type = Individual; Partition = 1; TopicName = %""; ChunkMessageIds = None }
            let msgId2 = { msgId1 with LedgerId = %2L; EntryId = %0L; }
            let msgId3 = { msgId1 with EntryId = %2L; Partition = 0; }
            let msgId4 = { msgId1 with LedgerId = %2L; Partition = 0; }
            let msgId5 = { msgId1 with Partition = 2; }
            Expect.isLessThan "" (msgId1, msgId2)
            Expect.isLessThan "" (msgId1, msgId3)
            Expect.isLessThan "" (msgId1, msgId4)
            Expect.isLessThan "" (msgId1, msgId5)
        }
        
        test "Less works correctly with batches" {
            let acker = BatchMessageAcker(0)
            let msgId1 = { LedgerId = %1L; EntryId = %1L; Type = Cumulative(%1, acker); Partition = 1; TopicName = %""; ChunkMessageIds = None }
            let msgId2 = { msgId1 with LedgerId = %2L; Type = Cumulative(%0, acker) }
            let msgId3 = { msgId1 with EntryId = %2L; Type = Cumulative(%0, acker) }
            let msgId4 = { msgId1 with Type = Cumulative(%2, acker); Partition = 0; }
            Expect.isLessThan "" (msgId1, msgId2)
            Expect.isLessThan "" (msgId1, msgId3)
            Expect.isLessThan "" (msgId1, msgId4)
        }
        
        test "Equals works correctly" {
            let msgId1 = { LedgerId = %1L; EntryId = %1L; Type = Individual; Partition = 1; TopicName = %""; ChunkMessageIds = None }
            let msgId2 = { msgId1 with TopicName = %"abcd" }
            let msgId3 = { msgId1 with ChunkMessageIds = Some [||] }
            Expect.equal "" msgId1 msgId2
            Expect.equal "" msgId1 msgId3
        }
        
        test "Equals works correctly with batches" {
            let acker = BatchMessageAcker(0)
            let msgId1 = { LedgerId = %1L; EntryId = %1L; Type = Cumulative(%1, acker); Partition = 1; TopicName = %""; ChunkMessageIds = None }
            let msgId2 = { msgId1 with TopicName = %"abcd" }
            let msgId3 = { msgId1 with ChunkMessageIds = Some [||] }
            let msgId4 = { msgId1 with Type = Cumulative(%1, BatchMessageAcker(2)) }
            Expect.equal "" msgId1 msgId2
            Expect.equal "" msgId1 msgId3
            Expect.equal "" msgId1 msgId4
        }
    ]