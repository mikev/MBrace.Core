﻿namespace MBrace.Flow

open System
open System.IO
open System.Threading
open System.Text
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq

open Nessos.Streams
open Nessos.Streams.Internals

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Library
open MBrace.Library.CloudCollectionUtils

open MBrace.Flow
open MBrace.Flow.Internals
open MBrace.Flow.Internals.Consumers
open MBrace.Library.CloudCollectionUtils

#nowarn "444"

/// Provides CloudFlow producers.
type CloudFlow =

    /// <summary>Wraps array as a CloudFlow.</summary>
    /// <param name="source">The input array.</param>
    /// <returns>The result CloudFlow.</returns>
    static member OfArray (source : 'T []) : CloudFlow<'T> = Array.ToCloudFlow source

    /// <summary>
    ///     Creates a CloudFlow according to partitions of provided cloud collection.
    /// </summary>
    /// <param name="collection">Input cloud collection.</param>
    /// <param name="sizeThresholdPerWorker">Restricts concurrent processing of collection partitions up to specified size per worker.</param>
    static member OfCloudCollection (collection : ICloudCollection<'T>, [<O;D(null:obj)>]?sizeThresholdPerWorker:unit -> int64) : CloudFlow<'T> =
        CloudCollection.ToCloudFlow(collection, ?sizeThresholdPerWorker = sizeThresholdPerWorker)

    /// <summary>
    ///     Creates a CloudFlow from a collection of provided cloud sequences.
    /// </summary>
    /// <param name="cloudArrays">Cloud sequences to be evaluated.</param>
    static member OfCloudArrays (cloudArrays : seq<#CloudArray<'T>>) : LocalCloud<PersistedCloudFlow<'T>> =
        PersistedCloudFlow.OfCloudArrays cloudArrays

    /// <summary>
    ///     Creates a CloudFlow instance from a finite collection of serializable enumerations.
    /// </summary>
    /// <param name="enumerations">Input enumerations.</param>
    static member OfSeqs (enumerations : seq<#seq<'T>>) : CloudFlow<'T> =
        let collection = enumerations |> Seq.map CloudCollection.OfSeq |> CloudCollection.Concat
        CloudCollection.ToCloudFlow(collection)

    /// <summary>
    ///     Constructs a CloudFlow from a collection of CloudFiles using the given deserializer.
    /// </summary>
    /// <param name="paths">Cloud file input paths.</param>
    /// <param name="deserializer">Element deserialization function for cloud files. Defaults to runtime serializer.</param>
    /// <param name="sizeThresholdPerCore">Restricts concurrent processing of collection partitions up to specified size per core. Defaults to 256MiB.</param>
    static member OfCloudFiles (paths : seq<string>, [<O;D(null:obj)>]?deserializer : System.IO.Stream -> seq<'T>, [<O;D(null:obj)>]?sizeThresholdPerCore : int64) : CloudFlow<'T> =
        { new CloudFlow<'T> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<'T, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) =
                cloud {
                    let sizeThresholdPerCore = defaultArg sizeThresholdPerCore (1024L * 1024L * 256L)
                    let toCloudSeq (path : string) = PersistedSequence.OfCloudFile(path, ?deserializer = deserializer, forceEvaluation = false, resolveEtag = false, ensureFileExists = false)
                    let! cseqs = Local.Parallel.map toCloudSeq paths
                    let collection = cseqs |> Seq.map (fun f -> f :> ICloudCollection<'T>) |> CloudCollection.Concat
                    let threshold () = int64 Environment.ProcessorCount * sizeThresholdPerCore
                    let collectionFlow = CloudFlow.OfCloudCollection(collection, sizeThresholdPerWorker = threshold)
                    return! collectionFlow.WithEvaluators collectorf projection combiner
                }
        }

    /// <summary>
    ///     Constructs a CloudFlow from a collection of CloudFiles using the given serializer implementation.
    /// </summary>
    /// <param name="paths">Cloud file input paths.</param>
    /// <param name="serializer">Element deserialization function for cloud files.</param>
    /// <param name="sizeThresholdPerCore">Restricts concurrent processing of collection partitions up to specified size per core. Defaults to 256MiB.</param>
    static member OfCloudFiles (paths : seq<string>, serializer : ISerializer, [<O;D(null:obj)>]?sizeThresholdPerCore : int64) =
        { new CloudFlow<'T> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<'T, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) =
                cloud {
                    let deserializer (stream : System.IO.Stream) = serializer.SeqDeserialize(stream, leaveOpen = false)
                    let filesFlow = CloudFlow.OfCloudFiles(paths, deserializer, ?sizeThresholdPerCore = sizeThresholdPerCore)
                    return! filesFlow.WithEvaluators collectorf projection combiner
                }
        }

    /// <summary>
    ///     Constructs a CloudFlow from a collection of text files using the given reader.
    /// </summary>
    /// <param name="paths">Cloud file input paths.</param>
    /// <param name="deserializer">A function to transform the contents of a CloudFile to a stream of elements.</param>
    /// <param name="encoding">Text encoding used by the underlying text files.</param>
    /// <param name="sizeThresholdPerCore">Restricts concurrent processing of collection partitions up to specified size per core. Defaults to 256MiB.</param>
    static member OfCloudFiles (paths : seq<string>, deserializer : System.IO.TextReader -> seq<'T>, [<O;D(null:obj)>]?encoding : Encoding, [<O;D(null:obj)>]?sizeThresholdPerCore : int64) : CloudFlow<'T> =
        { new CloudFlow<'T> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<'T, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) =
                cloud {
                    let deserializer (stream : System.IO.Stream) =
                        let sr =
                            match encoding with
                            | None -> new System.IO.StreamReader(stream)
                            | Some e -> new System.IO.StreamReader(stream, e)

                        deserializer sr

                    let filesFlow = CloudFlow.OfCloudFiles(paths, deserializer, ?sizeThresholdPerCore = sizeThresholdPerCore)
                    return! filesFlow.WithEvaluators collectorf projection combiner
                }
        }

    /// <summary>
    ///     Constructs a CloudFlow of all files in provided cloud directory using the given deserializer.
    /// </summary>
    /// <param name="dirPath">Input CloudDirectory.</param>
    /// <param name="deserializer">Element deserialization function for cloud files. Defaults to runtime serializer.</param>
    /// <param name="sizeThresholdPerCore">Restricts concurrent processing of collection partitions up to specified size per core. Defaults to 256MiB.</param>
    static member OfCloudDirectory (dirPath : string, [<O;D(null:obj)>]?deserializer : System.IO.Stream -> seq<'T>, [<O;D(null:obj)>]?sizeThresholdPerCore : int64) : CloudFlow<'T> =
        { new CloudFlow<'T> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<'T, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                let! files = CloudFile.Enumerate dirPath
                let paths = files |> Array.map (fun f -> f.Path)
                let flow = CloudFlow.OfCloudFiles(paths, ?deserializer = deserializer, ?sizeThresholdPerCore = sizeThresholdPerCore)
                return! flow.WithEvaluators collectorf projection combiner
            }
        }

    /// <summary>
    ///      Constructs a CloudFlow of all files in provided cloud directory using the given serializer implementation.
    /// </summary>
    /// <param name="dirPath">Input CloudDirectory.</param>
    /// <param name="serializer">Element deserialization function for cloud files. Defaults to runtime serializer.</param>
    /// <param name="sizeThresholdPerCore">Restricts concurrent processing of collection partitions up to specified size per core. Defaults to 256MiB.</param>
    static member OfCloudDirectory (dirPath : string, serializer : ISerializer, [<O;D(null:obj)>]?sizeThresholdPerCore : int64) : CloudFlow<'T> =
        { new CloudFlow<'T> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<'T, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                let! files = CloudFile.Enumerate dirPath
                let paths = files |> Array.map (fun f -> f.Path)
                let flow = CloudFlow.OfCloudFiles(paths, serializer = serializer, ?sizeThresholdPerCore = sizeThresholdPerCore)
                return! flow.WithEvaluators collectorf projection combiner
            }
        }

    /// <summary>
    ///     Constructs a CloudFlow from all files in provided directory using the given reader.
    /// </summary>
    /// <param name="dirPath">Cloud file input paths.</param>
    /// <param name="deserializer">A function to transform the contents of a CloudFile to a stream of elements.</param>
    /// <param name="encoding">Text encoding used by the underlying text files.</param>
    /// <param name="sizeThresholdPerCore">Restricts concurrent processing of collection partitions up to specified size per core. Defaults to 256MiB.</param>
    static member OfCloudDirectory (dirPath : string, deserializer : System.IO.TextReader -> seq<'T>, [<O;D(null:obj)>]?encoding : Encoding, [<O;D(null:obj)>]?sizeThresholdPerCore : int64) : CloudFlow<'T> =
        { new CloudFlow<'T> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<'T, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                let! files = CloudFile.Enumerate dirPath
                let paths = files |> Array.map (fun f -> f.Path)
                let flow = CloudFlow.OfCloudFiles(paths, deserializer = deserializer, ?encoding = encoding, ?sizeThresholdPerCore = sizeThresholdPerCore)
                return! flow.WithEvaluators collectorf projection combiner
            }
        }

    /// <summary>
    ///     Constructs a CloudFlow of lines from a collection of text files.
    /// </summary>
    /// <param name="paths">Paths to input cloud files.</param>
    /// <param name="encoding">Optional encoding.</param>
    /// <param name="sizeThresholdPerCore">Restricts concurrent processing of collection partitions up to specified size per core. Defaults to 256MiB.</param>
    static member OfCloudFileByLine (paths : seq<string>, [<O;D(null:obj)>]?encoding : Encoding, [<O;D(null:obj)>]?sizeThresholdPerCore : int64) : CloudFlow<string> =
        { new CloudFlow<string> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<string, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                let sizeThresholdPerCore = defaultArg sizeThresholdPerCore (1024L * 1024L * 256L)
                let toLineReader (path : string) = PersistedSequence.OfCloudFileByLine(path, ?encoding = encoding, forceEvaluation = false, resolveEtag = false, ensureFileExists = false)
                let! cseqs = Local.Parallel.map toLineReader paths
                let collection = cseqs |> Seq.map (fun f -> f :> ICloudCollection<string>) |> CloudCollection.Concat
                let threshold () = int64 Environment.ProcessorCount * sizeThresholdPerCore
                let collectionFlow = CloudFlow.OfCloudCollection(collection, sizeThresholdPerWorker = threshold)
                return! collectionFlow.WithEvaluators collectorf projection combiner
            }
        }

    /// <summary>
    ///     Constructs a CloudFlow of lines from a single large text file.
    /// </summary>
    /// <param name="path">The path to the text file.</param>
    /// <param name="encoding">Optional encoding.</param>
    static member OfCloudFileByLine (path : string, [<O;D(null:obj)>]?encoding : Encoding) : CloudFlow<string> =
        { new CloudFlow<string> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<string, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                let! cseq = PersistedSequence.OfCloudFileByLine(path, ?encoding = encoding, forceEvaluation = false, ensureFileExists = false)
                let collectionStream = CloudFlow.OfCloudCollection cseq
                return! collectionStream.WithEvaluators collectorf projection combiner
            }
        }

    /// <summary>
    ///     Constructs a text CloudFlow by line from all files in supplied CloudDirectory.
    /// </summary>
    /// <param name="dirPath">Paths to input cloud files.</param>
    /// <param name="encoding">Optional encoding.</param>
    /// <param name="sizeThresholdPerCore">Restricts concurrent processing of collection partitions up to specified size per core. Defaults to 256MiB.</param>
    static member OfCloudDirectoryByLine (dirPath : string, [<O;D(null:obj)>]?encoding : Encoding, [<O;D(null:obj)>]?sizeThresholdPerCore : int64) : CloudFlow<string> =
        { new CloudFlow<string> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<string, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                let! files = CloudFile.Enumerate dirPath
                let paths = files |> Array.map (fun f -> f.Path)
                let flow = CloudFlow.OfCloudFileByLine(paths, ?encoding = encoding, ?sizeThresholdPerCore = sizeThresholdPerCore)
                return! flow.WithEvaluators collectorf projection combiner
            }
        }

    /// <summary>
    ///     Constructs a CloudFlow of lines from a single HTTP text file.
    /// </summary>
    /// <param name="url">Url path to the text file.</param>
    /// <param name="encoding">Optional encoding.</param>
    static member OfHttpFileByLine (url : string, [<O;D(null:obj)>]?encoding : Encoding) : CloudFlow<string> =
        { new CloudFlow<string> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<string, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                let! httpCollection = CloudCollection.OfHttpFile(url, ?encoding = encoding, ensureThatFileExists = false) |> Cloud.OfAsync
                let collectionStream = CloudFlow.OfCloudCollection httpCollection
                return! collectionStream.WithEvaluators collectorf projection combiner
            }
        }

    /// <summary>
    ///     Constructs a CloudFlow of lines from a collection of HTTP text files.
    /// </summary>
    /// <param name="url">Url paths to the text files.</param>
    /// <param name="encoding">Optional encoding.</param>
    static member OfHttpFileByLine (urls : seq<string>, [<O;D(null:obj)>]?encoding : Encoding) : CloudFlow<string> =
        { new CloudFlow<string> with
            member self.DegreeOfParallelism = None
            member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<string, 'S>>) (projection : 'S -> LocalCloud<'R>) (combiner : 'R [] -> LocalCloud<'R>) = cloud {
                let! httpCollections = 
                    urls
                    |> Seq.map (fun uri ->  CloudCollection.OfHttpFile(uri, ?encoding = encoding, ensureThatFileExists = false))
                    |> Async.Parallel
                    |> Cloud.OfAsync

                let collection = CloudCollection.Concat httpCollections
                let collectionStream = CloudFlow.OfCloudCollection collection
                return! collectionStream.WithEvaluators collectorf projection combiner
            }
        }

    /// <summary>Creates a CloudFlow from the ReceivePort of a CloudQueue.</summary>
    /// <param name="queue">the ReceivePort of a CloudQueue.</param>
    /// <param name="degreeOfParallelism">The number of concurrently receiving tasks.</param>
    /// <returns>A CloudFlow that distributively performs operations on received messages from queue.</returns>
    static member OfCloudQueue (queue : CloudQueue<'T>, degreeOfParallelism : int) : CloudFlow<'T> =
        CloudQueue.ToCloudFlow(queue, degreeOfParallelism)

/// Provides basic operations on CloudFlows.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CloudFlow =

    let private cloudFlowStaticId = mkUUID()

    //#region Intermediate functions

    /// <summary>Transforms each element of the input CloudFlow.</summary>
    /// <param name="f">A function to transform items from the input CloudFlow.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The result CloudFlow.</returns>
    let inline map (f : 'T -> 'R) (flow : CloudFlow<'T>) : CloudFlow<'R> =
        Transformers.map f flow

    /// <summary>Enables the insertion of a monadic side-effect in a distributed flow. Output remains the same.</summary>
    /// <param name="f">A locally executing cloud function that performs side effect on input flow elements.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The result CloudFlow.</returns>
    let peek (f : 'T -> LocalCloud<unit>) (flow : CloudFlow<'T>) : CloudFlow<'T> =
        Transformers.peek f flow

    /// <summary>Transforms each element of the input CloudFlow to a new sequence and flattens its elements.</summary>
    /// <param name="f">A function to transform items from the input CloudFlow.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The result CloudFlow.</returns>
    let inline collect (f : 'T -> #seq<'R>) (flow : CloudFlow<'T>) : CloudFlow<'R> =
        Transformers.collect f flow

    /// <summary>Filters the elements of the input CloudFlow.</summary>
    /// <param name="predicate">A function to test each source element for a condition.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The result CloudFlow.</returns>
    let inline filter (predicate : 'T -> bool) (flow : CloudFlow<'T>) : CloudFlow<'T> =
        Transformers.filter predicate flow

    /// <summary>Applies the given chooser function to each element of the input CloudFlow and returns a CloudFlow yielding each element where the function returns Some value</summary>
    /// <param name="predicate">A function to transform items of type 'T into options of type 'R.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The result CloudFlow.</returns>
    let inline choose (chooser : 'T -> 'R option) (flow : CloudFlow<'T>) : CloudFlow<'R> =
        Transformers.choose chooser flow

    /// <summary>Returns a cloud flow with a new degree of parallelism.</summary>
    /// <param name="degreeOfParallelism">The degree of parallelism.</param>
    /// <param name="flow">The input cloud flow.</param>
    /// <returns>The result cloud flow.</returns>
    let withDegreeOfParallelism (degreeOfParallelism : int) (flow : CloudFlow<'T>) : CloudFlow<'T> =
        if degreeOfParallelism < 1 then
            raise <| new ArgumentOutOfRangeException("degreeOfParallelism")
        else
            { new CloudFlow<'T> with
                    member self.DegreeOfParallelism = Some degreeOfParallelism
                    member self.WithEvaluators<'S, 'R> (collectorf : LocalCloud<Collector<'T, 'S>>) (projection : 'S -> LocalCloud<'R>) combiner =
                        flow.WithEvaluators collectorf projection combiner }

    /// <summary>Applies a function to each element of the CloudFlow, threading an accumulator argument through the computation. If the input function is f and the elements are i0...iN, then this function computes f (... (f s i0)...) iN.</summary>
    /// <param name="folder">A function that updates the state with each element from the CloudFlow.</param>
    /// <param name="combiner">A function that combines partial states into a new state.</param>
    /// <param name="state">A function that produces the initial state.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The final result.</returns>
    let inline fold (folder : 'State -> 'T -> 'State) 
                    (combiner : 'State -> 'State -> 'State)
                    (state : unit -> 'State) (flow : CloudFlow<'T>) : Cloud<'State> =

        Fold.fold folder combiner state flow

    /// <summary>Applies a key-generating function to each element of a CloudFlow and return a CloudFlow yielding unique keys and the result of the threading an accumulator.</summary>
    /// <param name="projection">A function to transform items from the input CloudFlow to keys.</param>
    /// <param name="folder">A function that updates the state with each element from the CloudFlow.</param>
    /// <param name="combiner">A function that combines partial states into a new state.</param>
    /// <param name="state">A function that produces the initial state.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The final result.</returns>
    let inline foldBy (projection : 'T -> 'Key)
                      (folder : 'State -> 'T -> 'State)
                      (combiner : 'State -> 'State -> 'State)
                      (state : unit -> 'State) (flow : CloudFlow<'T>) : CloudFlow<'Key * 'State> =

        Fold.foldBy projection folder combiner state flow

    /// <summary>
    /// Applies a key-generating function to each element of a CloudFlow and return a CloudFlow yielding unique keys and their number of occurrences in the original sequence.
    /// </summary>
    /// <param name="projection">A function that maps items from the input CloudFlow to keys.</param>
    /// <param name="flow">The input CloudFlow.</param>
    let inline countBy (projection : 'T -> 'Key) (flow : CloudFlow<'T>) : CloudFlow<'Key * int64> =
        Fold.foldBy projection (fun state _ -> state + 1L) (fun x y -> x + y) (fun () -> 0L) flow

    /// <summary>Runs the action on each element. The actions are not necessarily performed in order.</summary>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>Nothing.</returns>
    let inline iter (action: 'T -> unit) (flow : CloudFlow< 'T >) : Cloud< unit > =
        fold (fun () x -> action x) (fun () () -> ()) (fun () -> ()) flow

    /// <summary>Returns the sum of the elements.</summary>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The sum of the elements.</returns>
    let inline sum (flow : CloudFlow< ^T >) : Cloud< ^T >
            when ^T : (static member ( + ) : ^T * ^T -> ^T)
            and  ^T : (static member Zero : ^T) =
        fold (+) (+) (fun () -> LanguagePrimitives.GenericZero) flow


    /// <summary>Applies a key-generating function to each element of a CloudFlow and return the sum of the keys.</summary>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The sum of the keys.</returns>
    let inline sumBy projection (flow : CloudFlow< 'T >) : Cloud< ^S >
            when ^S : (static member ( + ) : ^S * ^S -> ^S)
            and  ^S : (static member Zero : ^S) =
        fold (fun s x -> s + projection x) (+) (fun () -> LanguagePrimitives.GenericZero) flow

    /// <summary>Returns the total number of elements of the CloudFlow.</summary>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The total number of elements.</returns>
    let length (flow : CloudFlow<'T>) : Cloud<int64> =
        fold (fun acc _  -> 1L + acc) (+) (fun () -> 0L) flow

    /// <summary>Creates an array from the given CloudFlow.</summary>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The result array.</returns>
    let toArray (flow : CloudFlow<'T>) : Cloud<'T[]> = cloud {
        let! arrayCollector =
            fold (fun (acc : ArrayCollector<'T>) value -> acc.Add(value); acc)
                (fun left right -> left.AddRange(right); left)
                (fun () -> new ArrayCollector<'T>()) flow

        return arrayCollector.ToArray()
    }

    //static let workerGuid = Guid.NewGuid().ToString()
    /// <summary>Returns an array of line separated CloudFiles from the given CloudFlow of strings.</summary>
    /// <param name="dirPath">The directory where the cloudfiles are going to be saved.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The result array of CloudFiles.</returns>
    let toTextCloudFiles (dirPath : string) (flow : CloudFlow<string>) : Cloud<CloudFileInfo []> = 
        let collectorf (cloudCt : ICloudCancellationToken) =
            local {
                let cts = CancellationTokenSource.CreateLinkedTokenSource(cloudCt.LocalToken)
                let results = new List<string * StreamWriter>()
                let! store = Cloud.GetResource<ICloudFileStore>()
                store.CreateDirectory dirPath |> Async.RunSync
                return
                    { new Collector<string, CloudFileInfo []> with
                        member self.DegreeOfParallelism = flow.DegreeOfParallelism
                        member self.Iterator() =
                            let path = store.Combine(dirPath, sprintf "Part-%s-%s.txt" cloudFlowStaticId (mkUUID ()))
                            let stream = store.BeginWrite(path) |> Async.RunSync
                            let writer = new StreamWriter(stream)
                            results.Add((path, writer))
                            {   Func = (fun line -> writer.WriteLine(line));
                                Cts = cts }
                        member self.Result =
                            results |> Seq.iter (fun (_, writer) -> writer.Dispose())
                            results |> Seq.map (fun (path, _) -> new CloudFileInfo(store, path)) |> Seq.toArray }
            }

        cloud {
            let! ct = Cloud.CancellationToken
            use! cts = Cloud.CreateCancellationTokenSource(ct)
            return! flow.WithEvaluators (collectorf cts.Token) (fun cloudFiles -> local { return cloudFiles }) (fun result -> local { return Array.concat result })
        }

    /// <summary>Creates a PersistedCloudFlow from the given CloudFlow.</summary>
    /// <param name="flow">CloudFlow to be persisted.</param>
    /// <returns>A persisted copy of given CloudFlow.</returns>
    let cache (flow : CloudFlow<'T>) : Cloud<PersistedCloudFlow<'T>> = PersistedCloudFlow.OfCloudFlow(flow, storageLevel = StorageLevel.Memory)

    /// <summary>Creates a PersistedCloudFlow from the given CloudFlow, with its partitions cached to local memory.</summary>
    /// <param name="storageLevel">Storage level to be used when persisting CloudFlow.</param>
    /// <param name="flow">CloudFlow to be persisted.</param>
    /// <returns>A persisted copy of given CloudFlow.</returns>
    let persist (storageLevel : StorageLevel) (flow : CloudFlow<'T>) : Cloud<PersistedCloudFlow<'T>> = PersistedCloudFlow.OfCloudFlow(flow, storageLevel = storageLevel)

    // keep inline to ensure efficient comparison is used
    let inline private mkComparer<'K when 'K : comparison> = { new IComparer<'K> with member __.Compare(x,y) = compare x y }
    let inline private mkDescendingComparer<'K when 'K : comparison> = { new IComparer<'K> with member __.Compare(x,y) = - compare x y }

    /// <summary>Applies a key-generating function to each element of the input CloudFlow and yields the CloudFlow of the given length, ordered by keys.</summary>
    /// <param name="projection">A function to transform items of the input CloudFlow into comparable keys.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <param name="takeCount">The number of elements to return.</param>
    /// <returns>The result CloudFlow.</returns>
    let inline sortBy<'T, 'Key when 'Key : comparison> (projection : 'T -> 'Key) (takeCount : int) (flow : CloudFlow<'T>) : CloudFlow<'T> =
        flow
        //Sort.sortByGen mkComparer<'Key> projection takeCount flow

    /// <summary>Applies a key-generating function to each element of the input CloudFlow and yields the CloudFlow of the given length, ordered using the given comparer for the keys.</summary>
    /// <param name="projection">A function to transform items of the input CloudFlow into comparable keys.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <param name="takeCount">The number of elements to return.</param>
    /// <returns>The result CloudFlow.</returns>
    let inline sortByUsing<'T, 'Key> (projection : 'T -> 'Key) (comparer : IComparer<'Key>) (takeCount : int) (flow : CloudFlow<'T>) : CloudFlow<'T> =
        flow
        //Sort.sortByGen comparer projection takeCount flow

    /// <summary>Applies a key-generating function to each element of the input CloudFlow and yields the CloudFlow of the given length, ordered descending by keys.</summary>
    /// <param name="projection">A function to transform items of the input CloudFlow into comparable keys.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <param name="takeCount">The number of elements to return.</param>
    /// <returns>The result CloudFlow.</returns>
    let inline sortByDescending<'T, 'Key when 'Key : comparison> (projection : 'T -> 'Key) (takeCount : int) (flow : CloudFlow<'T>) : CloudFlow<'T> =
        flow
        //Sort.sortByGen mkDescendingComparer projection takeCount flow

    /// <summary>Returns the first element for which the given function returns true. Returns None if no such element exists.</summary>
    /// <param name="predicate">A function to test each source element for a condition.</param>
    /// <param name="flow">The input cloud flow.</param>
    /// <returns>The first element for which the predicate returns true, or None if every element evaluates to false.</returns>
    let inline tryFind (predicate : 'T -> bool) (flow : CloudFlow<'T>) : Cloud<'T option> =
        NonDeterministic.tryFind predicate flow

    /// <summary>Returns the first element for which the given function returns true. Raises KeyNotFoundException if no such element exists.</summary>
    /// <param name="predicate">A function to test each source element for a condition.</param>
    /// <param name="flow">The input cloud flow.</param>
    /// <returns>The first element for which the predicate returns true.</returns>
    /// <exception cref="System.KeyNotFoundException">Thrown if the predicate evaluates to false for all the elements of the cloud flow.</exception>
    let inline find (predicate : 'T -> bool) (flow : CloudFlow<'T>) : Cloud<'T> =
        cloud {
            let! result = tryFind predicate flow
            return
                match result with
                | Some value -> value
                | None -> raise <| new KeyNotFoundException()
        }

    /// <summary>Applies the given function to successive elements, returning the first result where the function returns a Some value.</summary>
    /// <param name="chooser">A function that transforms items into options.</param>
    /// <param name="flow">The input cloud flow.</param>
    /// <returns>The first element for which the chooser returns Some, or None if every element evaluates to None.</returns>
    let inline tryPick (chooser : 'T -> 'R option) (flow : CloudFlow<'T>) : Cloud<'R option> =
        NonDeterministic.tryPick chooser flow

    /// <summary>Applies the given function to successive elements, returning the first result where the function returns a Some value.
    /// Raises KeyNotFoundException when every item of the cloud flow evaluates to None when the given function is applied.</summary>
    /// <param name="chooser">A function that transforms items into options.</param>
    /// <param name="flow">The input cloud flow.</param>
    /// <returns>The first element for which the chooser returns Some, or raises KeyNotFoundException if every element evaluates to None.</returns>
    /// <exception cref="System.KeyNotFoundException">Thrown if every item of the cloud flow evaluates to None when the given function is applied.</exception>
    let inline pick (chooser : 'T -> 'R option) (flow : CloudFlow<'T>) : Cloud<'R> =
        cloud {
            let! result = tryPick chooser flow
            return
                match result with
                | Some value -> value
                | None -> raise <| new KeyNotFoundException()
        }

    /// <summary>Tests if any element of the flow satisfies the given predicate.</summary>
    /// <param name="predicate">A function to test each source element for a condition.</param>
    /// <param name="flow">The input cloud flow.</param>
    /// <returns>true if any element satisfies the predicate. Otherwise, returns false.</returns>
    let inline exists (predicate : 'T -> bool) (flow : CloudFlow<'T>) : Cloud<bool> =
        cloud {
            let! result = tryFind predicate flow
            return
                match result with
                | Some _ -> true
                | None -> false
        }

    /// <summary>Tests if all elements of the parallel flow satisfy the given predicate.</summary>
    /// <param name="predicate">A function to test each source element for a condition.</param>
    /// <param name="flow">The input cloud flow.</param>
    /// <returns>true if all of the elements satisfies the predicate. Otherwise, returns false.</returns>
    let inline forall (predicate : 'T -> bool) (flow : CloudFlow<'T>) : Cloud<bool> =
        cloud {
            let! result = exists (fun x -> not <| predicate x) flow
            return not result
        }

    /// <summary> Returns the elements of a CloudFlow up to a specified count. </summary>
    /// <param name="n">The maximum number of items to take.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>The resulting CloudFlow.</returns>
    let take (n : int) (flow: CloudFlow<'T>) : CloudFlow<'T> = Take.take n flow

    /// <summary>Sends the values of CloudFlow to the SendPort of a CloudQueue</summary>
    /// <param name="queue">Target CloudQueue.</param>
    /// <param name="flow">The input CloudFlow.</param>
    /// <returns>Nothing.</returns>
    let toCloudQueue (queue : CloudQueue<'T>) (flow : CloudFlow<'T>)  : Cloud<unit> =
        // TODO : use EnqueueBatch overload
        flow |> iter queue.Enqueue

    /// <summary>
    ///     Returs true if the flow is empty and false otherwise.
    /// </summary>
    /// <param name="stream">The input flow.</param>
    /// <returns>true if the input flow is empty, false otherwise</returns>
    let inline isEmpty (flow : CloudFlow<'T>) : Cloud<bool> =
        cloud { let! isNotEmpty = flow |> exists (fun _ -> true) in return not isNotEmpty }


    /// <summary>Locates the maximum element of the flow by given key.</summary>
    /// <param name="projection">A function to transform items of the input flow into comparable keys.</param>
    /// <param name="source">The input flow.</param>
    /// <returns>The maximum item.</returns>
    /// <exception cref="System.ArgumentException">Thrown if the input flow is empty.</exception>
    let maxBy<'T, 'Key when 'Key : comparison> (projection: 'T -> 'Key) (flow: CloudFlow<'T>) : Cloud<'T> =
        cloud {
            let! result =
                Fold.fold (fun state x ->
                               let kx = projection x
                               match state with
                               | None -> Some (ref x, ref kx)
                               | Some (v, k) when !k < kx ->
                                   v := x
                                   k := kx
                                   state
                               | _ -> state)
                        (fun left right ->
                             match left, right with
                             | Some (_, k), Some (_, k') -> if !k' > !k then right else left
                             | None, _ -> right
                             | _, None -> left)
                        (fun _ -> None)
                        flow

            match result with
            | None -> return! Cloud.Raise (new System.ArgumentException("The input flow was empty.", "flow"))
            | Some (maxVal, _) -> return !maxVal
        }

    /// <summary>Locates the minimum element of the flow by given key.</summary>
    /// <param name="projection">A function to transform items of the input flow into comparable keys.</param>
    /// <param name="source">The input flow.</param>
    /// <returns>The minimum item.</returns>
    /// <exception cref="System.ArgumentException">Thrown if the input flow is empty.</exception>
    let minBy<'T, 'Key when 'Key : comparison> (projection : 'T -> 'Key) (flow : CloudFlow<'T>) : Cloud<'T> =
        cloud {
            let! result =
                Fold.fold (fun state x ->
                             let kx = projection x
                             match state with
                             | None -> Some (ref x, ref kx)
                             | Some (v, k) when !k > kx ->
                                 v := x
                                 k := kx
                                 state
                             | _ -> state)
                        (fun left right ->
                             match left, right with
                             | Some (_, k), Some (_, k') -> if !k' > !k then left else right
                             | None, _ -> right
                             | _, None -> left)
                        (fun _ -> None)
                        flow

            match result with
            | None -> return! Cloud.Raise (new System.ArgumentException("The input flow was empty.", "flow"))
            | Some (minVal, _) -> return !minVal
        }

    /// <summary>
    ///    Reduces the elements of the input flow to a single value via the given reducer function.
    ///    The reducer function is first applied to the first two elements of the flow.
    ///    Then, the reducer is applied on the result of the first reduction and the third element.
    //     The process continues until all the elements of the flow have been reduced.
    /// </summary>
    /// <param name="reducer">The reducer function.</param>
    /// <param name="flow">The input flow.</param>
    /// <returns>The reduced value.</returns>
    /// <exception cref="System.ArgumentException">Thrown if the input flow is empty.</exception>
    let reduce (reducer : 'T -> 'T -> 'T) (flow : CloudFlow<'T>) : Cloud<'T> =
        cloud {
            let! result =
                Fold.fold (fun state x -> match state with Some y -> y := reducer !y x; state | None -> Some (ref x))
                        (fun left right ->
                         match left, right with
                         | Some y, Some x -> y := reducer !y !x; left
                         | None, Some _ -> right
                         | Some _, None -> left
                         | None, None -> left)
                        (fun _ -> None)
                        flow

            match result with
            | None -> return! Cloud.Raise (new System.ArgumentException("The input flow was empty.", "flow"))
            | Some reducedVal -> return !reducedVal
        }

    /// <summary>
    ///    Groups the elements of the input flow according to given key generating function
    ///    and reduces the elements of each group to a single value via the given reducer function.
    /// </summary>
    /// <param name="projection">A function to transform items of the input flow into a key.</param>
    /// <param name="reducer">The reducer function.</param>
    /// <param name="source">The input flow.</param>
    /// <returns>A flow of key - reduced value pairs.</returns>
    let reduceBy (projection : 'T -> 'Key) (reducer : 'T -> 'T -> 'T) (source : CloudFlow<'T>) : CloudFlow<'Key * 'T> =
        foldBy (fun v -> projection v)
               (fun state x -> match state with Some y -> y := reducer !y x; state | None -> Some (ref x))
               (fun left right ->
                   match left, right with
                   | Some y, Some x -> y := reducer !y !x; left
                   | None, Some _ -> right
                   | Some _, None -> left
                   | None, None -> left)
               (fun _ -> None)
               source
        |> filter (fun (_, v) -> v.IsSome)
        |> map (fun (k, v) -> k, v.Value.Value)


    /// <summary>Computes the average of the projections given by the supplied function on the input flow.</summary>
    /// <param name="projection">A function to transform items of the input flow into a projection.</param>
    /// <param name="source">The input flow.</param>
    /// <returns>The computed average.</returns>
    /// <exception cref="System.ArgumentException">Thrown if the input flow is empty.</exception>
    let inline averageBy (projection : 'T -> ^U) (source : CloudFlow<'T>) : Cloud< ^U >
            when ^U : (static member (+) : ^U * ^U -> ^U)
            and  ^U : (static member DivideByInt : ^U * int -> ^U)
            and  ^U : (static member Zero : ^U) =
        cloud {
            let! (y, c) =
                fold (fun ((y, c) as state) v ->
                          y := Checked.(+) !y (projection v)
                          incr c
                          state)
                     (fun ((y, c) as state) (y', c') ->
                          y := Checked.(+) !y !y'
                          c := !c + !c'
                          state)
                     (fun () -> ref LanguagePrimitives.GenericZero, ref 0)
                     source

            if !c = 0 then return! Cloud.Raise (new System.ArgumentException("The input flow was empty.", "source"))
            else return LanguagePrimitives.DivideByInt !y !c
        }

    /// <summary>Computes the average of the elements in the input flow.</summary>
    /// <param name="source">The input flow.</param>
    /// <returns>The computed average.</returns>
    /// <exception cref="System.ArgumentException">Thrown if the input flow is empty.</exception>
    let inline average (source : CloudFlow< ^T >) : Cloud< ^T >
        when ^T : (static member (+) : ^T * ^T -> ^T)
        and  ^T : (static member DivideByInt : ^T * int -> ^T)
        and  ^T : (static member Zero : ^T) =
        averageBy id source


    /// <summary>
    ///     Applies a key-generating function to each element of the input flow and yields a flow of unique keys and a sequence of all elements that have each key.
    /// <remarks>
    ///     Note: This combinator may be very expensive; for example if the group sizes are expected to be large.
    ///     If you intend to perform an aggregate operation, such as sum or average,
    ///     you are advised to use CloudFlow.foldBy or CloudFlow.countBy, for much better performance.
    /// </remarks>
    /// </summary>
    /// <param name="projection">A function to transform items of the input flow into comparable keys.</param>
    /// <param name="source">The input flow.</param>
    /// <returns>A flow of tuples where each tuple contains the unique key and a sequence of all the elements that match the key.</returns>
    let inline groupBy (projection : 'T -> 'Key) (source : CloudFlow<'T>) : CloudFlow<'Key * seq<'T>> =
        foldBy projection
               (fun (xs : ResizeArray<'T>) x -> xs.Add x; xs)
               (fun xs ys -> xs.AddRange(ys); xs)
               (fun () -> new ResizeArray<'T>())
               source
        |> map (fun (k, xs) -> k, xs :> seq<_>)

    /// <summary>Applies a key-generating function to each element of the input flow and computes the average of the projections in each group.</summary>
    /// <param name="keyProjection">A function to transform items of the input flow into a key.</param>
    /// <param name="valueProjection">A function to transform items of the input flow into a value.</param>
    /// <param name="source">The input flow.</param>
    /// <returns>A CloudFlow of pairs, each with a key and computed average.</returns>
    /// <exception cref="System.ArgumentException">Thrown if the input flow is empty.</exception>
    let inline averageByKey (keyProjection: 'T -> 'Key) (valueProjection: 'T -> 'Value) (source: CloudFlow<'T>) : CloudFlow<'Key * 'Value> =
        source 
        |> foldBy 
            keyProjection  
            (fun (count, sum)  row -> (count + 1, sum + valueProjection row)) 
            (fun (count,sum) (count2,sum2) -> (count + count2, sum + sum2))
            (fun () -> (0,LanguagePrimitives.GenericZero<'Value>))
        |> map (fun (key, (count,sum)) -> (key, LanguagePrimitives.DivideByInt sum count))

    /// <summary>Applies a key-generating function to each element of the input flow and computes the sum of the projections in each group.</summary>
    /// <param name="keyProjection">A function to transform items of the input flow into a key.</param>
    /// <param name="valueProjection">A function to transform items of the input flow into a value.</param>
    /// <param name="source">The input flow.</param>
    /// <returns>A CloudFlow of pairs, each with a key and computed sum.</returns>
    /// <exception cref="System.ArgumentException">Thrown if the input flow is empty.</exception>
    let inline sumByKey (keyProjection: 'T -> 'Key) (valueProjection: 'T -> 'Value)  (source: CloudFlow<'T>) : CloudFlow<'Key * 'Value> =
        source |> foldBy keyProjection  (fun sum  row -> (sum + valueProjection row)) (+) (fun () -> LanguagePrimitives.GenericZero<'Value>)


    /// <summary>Applies a key-generating functions to each element of the flows and yields a flow of unique keys and sequences of all elements that have each key.</summary>
    /// <param name="firstProjection">A function to transform items of the first flow into comparable keys.</param>
    /// <param name="secondProjection">A function to transform items of the second flow into comparable keys.</param>
    /// <param name="firstSource">The first input flow.</param>
    /// <param name="secondSource">The second input flow.</param>
    /// <returns>A flow of tuples where each tuple contains the unique key and the sequences of all the elements that match the key.</returns>
    let inline groupJoinBy (firstProjection : 'T -> 'Key) (secondProjection : 'R -> 'Key) (secondSource : CloudFlow<'R>) (firstSource : CloudFlow<'T>) : CloudFlow<'Key * seq<'T> * seq<'R>> =
        Fold.foldBy2
               firstProjection
               secondProjection
               (fun ((xs : ResizeArray<'T>, _ : ResizeArray<'R>) as tuple) x -> xs.Add x; tuple)
               (fun ((_ : ResizeArray<'T>, ys : ResizeArray<'R>) as tuple) y -> ys.Add y; tuple)
               (fun (xs, ys) (xs', ys') -> ((xs.AddRange(xs'); xs), (ys.AddRange(ys'); ys)))
               (fun () -> (new ResizeArray<'T>(), new ResizeArray<'R>()))
               firstSource
               secondSource
        |> map (fun (k, (xs, ys)) -> k, xs :> seq<_>, ys :> seq<_>)

    /// <summary>Applies a key-generating functions to each element of the flows and yields a flow of unique keys and elements that have each key.</summary>
    /// <param name="firstProjection">A function to transform items of the first flow into comparable keys.</param>
    /// <param name="secondProjection">A function to transform items of the second flow into comparable keys.</param>
    /// <param name="firstSource">The first input flow.</param>
    /// <param name="secondSource">The second input flow.</param>
    /// <returns>A flow of tuples where each tuple contains the unique key and the values that match the key.</returns>
    let inline join (firstProjection : 'T -> 'Key) (secondProjection : 'R -> 'Key) (secondSource : CloudFlow<'R>) (firstSource : CloudFlow<'T>) : CloudFlow<'Key * 'T * 'R> =
        groupJoinBy firstProjection secondProjection secondSource firstSource
        |> collect (fun (key, xs, ys) -> xs |> Seq.collect (fun x -> ys |> Seq.map (fun y -> (key, x, y))) )


    /// <summary>Applies a key-generating functions to each element of the flows and yields a flow of unique keys and elements of the left flow together with the optional values of the right flow that match the key.</summary>
    /// <param name="leftProjection">A function to transform items of the left flow into comparable keys.</param>
    /// <param name="rightProjection">A function to transform items of the right flow into comparable keys.</param>
    /// <param name="leftSource">The left input flow.</param>
    /// <param name="rightSource">The right input flow.</param>
    /// <returns>A flow of tuples where each tuple contains the unique key and the values of the left flow together with the optional values of the right flow that match the key.</returns>
    let inline leftOuterJoin (leftProjection : 'T -> 'Key) (rightProjection : 'R -> 'Key) (rightSource : CloudFlow<'R>) (leftSource : CloudFlow<'T>) : CloudFlow<'Key * 'T * 'R option> =
        groupJoinBy leftProjection rightProjection rightSource leftSource
        |> collect (fun (key, xs, ys) -> 
            if Seq.isEmpty ys then
                xs |> Seq.map (fun x -> (key, x, None))
            else
                xs |> Seq.collect (fun x -> ys |> Seq.map (fun y -> (key, x, Some y))) )

    /// <summary>Applies a key-generating functions to each element of the flows and yields a flow of unique keys and elements of the right flow together with the optional values of the left flow that match the key.</summary>
    /// <param name="leftProjection">A function to transform items of the left flow into comparable keys.</param>
    /// <param name="rightProjection">A function to transform items of the right flow into comparable keys.</param>
    /// <param name="leftSource">The left input flow.</param>
    /// <param name="rightSource">The right input flow.</param>
    /// <returns>A flow of tuples where each tuple contains the unique key and the values of the right flow together with the optional values of the left flow that match the key.</returns>
    let inline rightOuterJoin (leftProjection : 'T -> 'Key) (rightProjection : 'R -> 'Key) (rightSource : CloudFlow<'R>) (leftSource : CloudFlow<'T>) : CloudFlow<'Key * 'T option * 'R> =
        groupJoinBy leftProjection rightProjection rightSource leftSource
        |> collect (fun (key, xs, ys) -> 
            if Seq.isEmpty xs then
                ys |> Seq.map (fun y -> (key, None, y))
            else
                xs |> Seq.collect (fun x -> ys |> Seq.map (fun y -> (key, Some x, y))) )


    /// <summary>Applies a key-generating functions to each element of the flows and yields a flow of unique keys and optional values of the right flow together with the optional values of the left flow that match the key.</summary>
    /// <param name="leftProjection">A function to transform items of the left flow into comparable keys.</param>
    /// <param name="rightProjection">A function to transform items of the right flow into comparable keys.</param>
    /// <param name="leftSource">The left input flow.</param>
    /// <param name="rightSource">The right input flow.</param>
    /// <returns>A flow of tuples where each tuple contains the unique key and the optional values of the right flow together with the optional values of the left flow that match the key.</returns>
    let inline fullOuterJoin (leftProjection : 'T -> 'Key) (rightProjection : 'R -> 'Key) (rightSource : CloudFlow<'R>) (leftSource : CloudFlow<'T>) : CloudFlow<'Key * 'T option * 'R option> =
        groupJoinBy leftProjection rightProjection rightSource leftSource
        |> collect (fun (key, xs, ys) -> 
            if Seq.isEmpty xs then
                ys |> Seq.map (fun y -> (key, None, Some y))
            else if Seq.isEmpty ys then
                xs |> Seq.map (fun x -> (key, Some x, None))
            else
                xs |> Seq.collect (fun x -> ys |> Seq.map (fun y -> (key, Some x, Some y))) )

    /// <summary>Returns a flow that contains no duplicate entries according to the generic hash and equality comparisons on the keys returned by the given key-generating function. If an element occurs multiple times in the flow then only one is retained.</summary>
    /// <param name="projection">A function to transform items of the input flow into comparable keys.</param>
    /// <param name="source">The input flow.</param>
    /// <returns>A flow of elements distinct on their keys.</returns>
    let distinctBy (projection : 'T -> 'Key) (source : CloudFlow<'T>) : CloudFlow<'T> = Distinct.distinctBy projection source

    /// <summary>Returns a flow that contains no duplicate elements according to their generic hash and equality comparisons. If an element occurs multiple times in the flow then only one is retained.</summary>
    /// <param name="source">The input flow.</param>
    /// <returns>A flow of distinct elements.</returns>
    let inline distinct (source : CloudFlow<'T>) : CloudFlow<'T> = distinctBy id source
