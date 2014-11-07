﻿module internal Nessos.MBrace.Runtime.Azure.Worker

open System.Diagnostics
open System.Threading

open Nessos.MBrace.Runtime.Azure.Actors
open Nessos.MBrace.Runtime.Azure.Tasks
open Nessos.MBrace.Runtime.Azure.RuntimeProvider

/// Thread-safe printfn
let printfn fmt = Printf.ksprintf System.Console.WriteLine fmt

/// <summary>
///     Initializes a worker loop. Worker polls task queue of supplied
///     runtime for available tasks and executes as appropriate.
/// </summary>
/// <param name="runtime">Runtime to subscribe to.</param>
/// <param name="maxConcurrentTasks">Maximum tasks to be executed concurrently by worker.</param>
let initWorker (runtime : RuntimeState) (maxConcurrentTasks : int) = async {

    let localEndPoint = Nessos.MBrace.Runtime.Azure.Config.getLocalEndpoint()
    printfn "MBrace worker initialized on %O." localEndPoint

    let currentTaskCount = ref 0
    let runTask procId deps t =
        let provider = RuntimeProvider.FromTask runtime procId deps t
        Task.RunAsync provider deps t

    let rec loop () = async {
        if !currentTaskCount >= maxConcurrentTasks then
            do! Async.Sleep 500
            return! loop ()
        else
            try
                let! task = runtime.TryDequeue()
                match task with
                | None -> do! Async.Sleep 500
                | Some (task, procId, dependencies, leaseMonitor) ->
                    let _ = Interlocked.Increment currentTaskCount
                    let runTask () = async {
                        printfn "Starting task %s of type '%O'." task.TaskId task.Type

                        //use hb = leaseMonitor.InitHeartBeat()

                        let sw = new Stopwatch()
                        sw.Start()
                        let! result = runTask procId dependencies task |> Async.Catch
                        sw.Stop()

                        match result with
                        | Choice1Of2 () -> 
                            //leaseMonitor.Release()
                            printfn "Task %s completed after %O." task.TaskId sw.Elapsed
                                
                        | Choice2Of2 e -> 
                            //leaseMonitor.DeclareFault()
                            printfn "Task %s faulted with:\n %O." task.TaskId e

                        let _ = Interlocked.Decrement currentTaskCount
                        return ()
                    }
        
                    let! handle = Async.StartChild(runTask())
                    do! Async.Sleep 200

            with e -> 
                printfn "WORKER FAULT: %O" e
                do! Async.Sleep 1000

            return! loop ()
    }

    return! loop ()
}
        