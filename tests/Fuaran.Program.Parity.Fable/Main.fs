module Fuaran.Program.Parity.Fable.Main

open Fable.Core
open Fable.Core.JsInterop
open Fuaran.Program.Runtime
open Fuaran.Program.Tests

// ============================================================================
//  Leg (c) — the client placement UNDER FABLE, at the toy witness.
//
//  This harness exists because "it compiles under Fable" and "it behaves the
//  same under Fable" are different claims, and only the second one matters. It
//  reads the SAME scenario files the .NET conformance leg reads — the
//  specification's driver-semantics-toy family — runs the SAME shared runner
//  compiled to JavaScript, and compares against the SAME recorded trace.
//
//  The only thing that differs from the .NET leg is the loader — node's `fs`
//  instead of `System.IO` — which is the irreducible per-host part. It reads
//  the corpus MANIFEST rather than the directory, for the reason the .NET
//  loader does: a scenario the manifest forgot is a behaviour nobody is
//  required to reproduce, and a directory listing cannot see that.
//
//  Since fuaran#2012 this leg runs the toy family alone. The UI-vocabulary
//  driver-semantics family's Fable leg left with the UI adapters, to the UI
//  tier's own repository, which runs it there.
// ============================================================================

[<Import("readFileSync", "fs")>]
let private readFileSync (path: string, encoding: string) : string = jsNative

[<Import("existsSync", "fs")>]
let private existsSync (path: string) : bool = jsNative

[<Emit("process.argv.slice(2)")>]
let private argv: string array = jsNative

[<Emit("process.exit($0)")>]
let private exit (code: int) : unit = jsNative

/// The family selection (fuaran#2011), read from node's environment — the same
/// variable the .NET legs read, so one gate run selects the same families on
/// every leg.
[<Emit("process.env.FUARAN_PROGRAM_FAMILIES")>]
let private selectionVariable: string = jsNative

let private read (path: string) : string = readFileSync (path, "utf8")

/// The toy family under Fable (fuaran#2011): the SAME shared loader and the
/// SAME client placement the .NET conformance leg runs, compiled to
/// JavaScript, against the same files. A handler-loop scenario is out of scope
/// for a placement that answers no call (§10.6), and is named as such rather
/// than counted as run. Returns the failures.
let private runToyFamily (root: string) : int =
    let manifest = read (root + "/manifest.json")

    match ToyScenarios.entries manifest with
    | Error e ->
        eprintfn "%s: %s" ToyScenarios.Family e
        1
    | Ok [] ->
        eprintfn "the corpus at %s enumerates no %s scenario" root ToyScenarios.Family
        1
    | Ok entries ->
        let mutable failures = 0
        let mutable ran = 0
        let mutable outOfScope = 0

        for entry in entries do
            let file (relative: string) = read (root + "/" + relative)

            match
                ToyScenarios.load entry (file entry.TreeFile) (file entry.EventsFile) (file entry.ExpectationFile)
            with
            | Error e ->
                eprintfn "FAIL %s" e
                failures <- failures + 1
            | Ok scenario when not (ToyScenarios.clientInScope scenario) ->
                printfn "out of scope %s (requires %s)" scenario.Name scenario.Requires
                outOfScope <- outOfScope + 1
            | Ok scenario ->
                let observed = ToyScenarios.runClient scenario

                match ToyScenarios.firstDivergence "expected" scenario.Expected "client/Fable" observed with
                | None ->
                    ran <- ran + 1
                    printfn "ok   %s (%d step(s))" scenario.Name (List.length observed)
                | Some d ->
                    eprintfn "FAIL %s: %s" scenario.Name d
                    failures <- failures + 1

        printfn
            "Fable leg: %s - %d scenario(s): %d run, %d out of scope (handler-loop), %d failure(s)"
            ToyScenarios.Family
            (List.length entries)
            ran
            outOfScope
            failures

        failures

let private run () =
    let root = if argv.Length > 0 then argv.[0] else "../wire-fixtures"

    if not (existsSync (root + "/manifest.json")) then
        eprintfn
            "the conformance corpus is not present at %s. It is a sibling clone and a BUILD INPUT to this gate."
            root

        exit 1

    let declared =
        match ToyScenarios.declaredFamilies (read (root + "/manifest.json")) with
        | Ok families -> families
        | Error e -> failwith e

    let selected =
        match
            ToyScenarios.selection
                (if isNullOrUndefined selectionVariable then
                     None
                 else
                     Some selectionVariable)
                declared
        with
        | Ok families -> families
        | Error e ->
            eprintfn "%s" e
            exit 1
            []

    let failures =
        if List.contains ToyScenarios.Family selected then
            runToyFamily root
        else
            printfn "-- %s: SKIPPED - not selected by %s" ToyScenarios.Family ToyScenarios.SelectionVariable
            0

    exit (if failures > 0 then 1 else 0)

run ()
