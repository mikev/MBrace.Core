﻿namespace MBrace.Core

open MBrace.Core.Internals

/// Distributed Queue abstraction
type CloudQueue<'T> =
    inherit ICloudDisposable

    /// Queue identifier
    abstract Id : string

    /// Asynchronously gets the current message count of the queue
    abstract GetCountAsync : unit -> Async<int64>

    /// <summary>
    ///     Asynchronously enqueues a message to the start of the queue.
    /// </summary>
    /// <param name="message">Message to be queued.</param>
    abstract EnqueueAsync : message:'T -> Async<unit>

    /// <summary>
    ///     Enqueues a batch of messages to the start of the queue.
    /// </summary>
    /// <param name="messages">Messages to be enqueued.</param>
    abstract EnqueueBatchAsync : messages:seq<'T> -> Async<unit>

    /// <summary>
    ///     Asynchronously dequeues a message from the queue.
    /// </summary>
    /// <param name="timeout">Timeout in milliseconds. Defaults to no timeout.</param>
    abstract DequeueAsync : [<O;D(null:obj)>]?timeout:int -> Async<'T>

    /// <summary>
    ///     Asynchronously dequeues a batch of messages from the queue.
    ///     Will dequeue up to the given maximum number of items, depending on current availability.
    /// </summary>
    /// <param name="maxItems">Maximum number of items to dequeue.</param>
    abstract DequeueBatchAsync : maxItems:int -> Async<'T []>

    /// <summary>
    ///     Asynchronously attempts to dequeue a message from the queue.
    ///     Returns None instantly if no message is currently available.
    /// </summary>
    abstract TryDequeueAsync : unit -> Async<'T option>

namespace MBrace.Core.Internals

open MBrace.Core

/// Defines a factory for distributed queues
type ICloudQueueProvider =

    /// Implementation name
    abstract Name : string

    /// unique cloud queue source identifier
    abstract Id : string

    /// Generates a unique, random queue name.
    abstract GetRandomQueueName : unit -> string

    /// <summary>
    ///     Creates a new queue instance of given type.
    /// </summary>
    /// <param name="queueId">Unique queue identifier.</param>
    abstract CreateQueue<'T> : queueId:string -> Async<CloudQueue<'T>>

    /// <summary>
    ///     Attempt to recover an existing queue instance of specified id.
    /// </summary>
    /// <param name="queueId">Queue identifier.</param>
    abstract GetQueueById<'T> : queueId:string -> Async<CloudQueue<'T>>


namespace MBrace.Core

open System.ComponentModel
open System.Runtime.CompilerServices

open MBrace.Core
open MBrace.Core.Internals

#nowarn "444"

/// Queue methods for MBrace
type CloudQueue =

    /// <summary>
    ///     Creates a new queue instance.
    /// </summary>
    /// <param name="queueId">Unique queue identifier. Defaults to randomly generated queue name.</param>
    static member New<'T>([<O;D(null:obj)>]?queueId : string) = local {
        let! provider = Cloud.GetResource<ICloudQueueProvider> ()
        let queueId = match queueId with Some qi -> qi | None -> provider.GetRandomQueueName()
        return! Cloud.OfAsync <| provider.CreateQueue<'T> queueId
    }

    /// <summary>
    ///     Attempt to recover an existing of given type and identifier.
    /// </summary>
    /// <param name="queueId">Unique queue identifier.</param>
    static member GetById<'T>(queueId : string) = local {
        let! provider = Cloud.GetResource<ICloudQueueProvider> ()
        return! Cloud.OfAsync <| provider.GetQueueById<'T> queueId
    }

    /// <summary>
    ///     Deletes cloud queue instance.
    /// </summary>
    static member Delete(queue : CloudQueue<'T>) : LocalCloud<unit> = 
        local { return! Cloud.OfAsync <| queue.Dispose() }


[<Extension; EditorBrowsable(EditorBrowsableState.Never)>]
type CloudQueueExtensions =

    /// Gets the current message count of the queue.
    [<Extension>]
    static member GetCount<'T> (this : CloudQueue<'T>) : int64 =
        Async.RunSync <| this.GetCountAsync()

    /// <summary>
    ///     Add message to the queue.
    /// </summary>
    /// <param name="message">Message to send.</param>
    [<Extension>]
    static member Enqueue<'T> (this : CloudQueue<'T>, message : 'T) : unit =
        Async.RunSync <| this.EnqueueAsync message

    /// <summary>
    ///     Add batch of messages to the queue.
    /// </summary>
    /// <param name="messages">Messages to be enqueued.</param>
    [<Extension>]
    static member EnqueueBatch<'T> (this : CloudQueue<'T>, messages : seq<'T>) : unit =
        Async.RunSync <| this.EnqueueBatchAsync messages

    /// <summary>
    ///     Receive a single message from queue.
    /// </summary>
    /// <param name="timeout">Timeout in milliseconds.</param>
    [<Extension>]
    static member Dequeue<'T> (this : CloudQueue<'T>, [<O;D(null:obj)>]?timeout : int) : 'T =
        Async.RunSync <| this.DequeueAsync (?timeout = timeout)

    /// <summary>
    ///     Dequeues a batch of messages from the queue.
    ///     Will dequeue up to the given maximum number of items, depending on current availability.
    /// </summary>
    /// <param name="maxItems">Maximum number of items to dequeue.</param>
    [<Extension>]
    static member DequeueBatch<'T>(this : CloudQueue<'T>, maxItems : int) : 'T [] =
        Async.RunSync <| this.DequeueBatchAsync(maxItems)

    /// <summary>
    ///     Asynchronously attempts to dequeue a message from the queue.
    ///     Returns None instantly if no message is currently available.
    /// </summary>
    [<Extension>]
    static member TryDequeue<'T> (this : CloudQueue<'T>) : 'T option =
        Async.RunSync <| this.TryDequeueAsync()