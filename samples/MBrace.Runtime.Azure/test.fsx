﻿#I "../../bin/"

#r "MBrace.Core.dll"
#r "MBrace.Library.dll"
#r "MBrace.Runtime.Azure.exe"

open Nessos.MBrace
open Nessos.MBrace.Library
open Nessos.MBrace.Runtime.Azure

MBraceRuntime.WorkerExecutable <- __SOURCE_DIRECTORY__ + "/../../bin/MBrace.Runtime.Azure.exe"

let runtime = MBraceRuntime.InitLocal(4)

let getWordCount inputSize =
    let map (text : string) = cloud { return text.Split(' ').Length }
    let reduce i i' = cloud { return i + i' }
    let inputs = Array.init inputSize (fun i -> "lorem ipsum dolor sit amet")
    MapReduce.mapReduce map 0 reduce inputs


let t = runtime.RunAsTask(getWordCount 2000)
do System.Threading.Thread.Sleep 3000
runtime.KillAllWorkers() 
runtime.AppendWorkers 4

t.Result