module Fuaran.Program.Parity.Fable.Main

open Fable.Core
open Fable.Core.JsInterop
open Fuaran.Program.Runtime
open Fuaran.Program.UI
open Fuaran.Program.Parity.Runner
open Fuaran.Program.Parity
open Fuaran.Program.Tests

// ============================================================================
//  Leg (c) — the client placement UNDER FABLE.
//
//  This harness exists because "it compiles under Fable" and "it behaves the
//  same under Fable" are different claims, and only the second one matters. It
//  reads the SAME scenario files the .NET legs read — the conformance corpus's
//  driver-semantics family — runs the SAME runner compiled to JavaScript, and
//  compares against the SAME recorded expectation.
//
//  The only thing that differs from the .NET legs is the loader — node's `fs`
//  instead of `System.IO` — which is the irreducible per-host part. It reads
//  the corpus MANIFEST rather than the directory, for the reason the .NET
//  loader does: a scenario the manifest forgot is a behaviour nobody is
//  required to reproduce, and a directory listing cannot see that.
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

let private parseEvents (json: string) : ScriptedEvent list =
    let arr: obj array = JS.JSON.parse json |> unbox

    arr
    |> Array.toList
    |> List.map (fun el ->
        let payload: obj = el?payload

        let keys: string array =
            if isNullOrUndefined payload then
                [||]
            else
                JS.Constructors.Object.keys payload |> Array.ofSeq

        { NodeId = el?nodeId
          Event = el?event
          Payload = keys |> Array.map (fun k -> k, unbox<string> payload?(k)) |> Map.ofArray })

/// Read one recorded denial into this host's own vocabulary — the same
/// obligation the .NET loader has, for the same reason: a harness holding the
/// recorded bytes beside its own would compare strings and assert nothing about
/// whether this host RECOGNISES §5.3's vocabulary.
let private parseDenial (name: string) (index: int) (d: obj) : EffectDenial =
    let stringMember (key: string) : string option =
        let v: obj = d?(key)
        if isNullOrUndefined v then None else Some(unbox<string> v)

    let arm =
        match stringMember "$type" with
        | Some t -> t
        | None -> failwithf "%s: step %d records a denial with no '$type'" name index

    let capability =
        match stringMember "capability" with
        | Some c -> c
        | None -> failwithf "%s: step %d records a denial with no 'capability'" name index

    match EffectDenial.ofWire arm capability (stringMember "origin") with
    | Ok denial -> denial
    | Error message -> failwithf "%s: step %d: %s" name index message

let private parseExpectation (name: string) (json: string) : StepObservation list =
    let arr: obj array = JS.JSON.parse json |> unbox

    arr
    |> Array.toList
    |> List.mapi (fun index el ->
        let effects: string array = el?effects |> unbox
        let recordedDenials: obj = el?denials

        // The tree is an embedded DOCUMENT. Re-serialising it here hands the
        // decoder the right MEANING, never the right bytes — which is the
        // whole point of the placement-independent format: this loader's JSON
        // writer is not the corpus's, and nothing downstream cares.
        { ResolvedJson = JS.JSON.stringify (el?tree)
          Effects = List.ofArray effects
          Refused = el?refused
          // Absent and empty are different facts (§10.3), which is exactly the
          // distinction `isNullOrUndefined` is here to preserve: an absent
          // member means the seam was unobserved, an empty array means it was
          // observed and declined nothing.
          Denials =
            if isNullOrUndefined recordedDenials then
                None
            else
                let ds: obj array = unbox recordedDenials
                Some(ds |> Array.toList |> List.map (parseDenial name index)) })

/// The manifest's driver-semantics enumeration. Reading the index rather than
/// the directory is what makes "every scenario the corpus declares was run" a
/// statement this leg can make.
let private loadScenarios (fixturesRoot: string) : Fixture list =
    let manifestPath = fixturesRoot + "/manifest.json"

    if not (existsSync manifestPath) then
        eprintfn
            "the conformance corpus is not present at %s. It is a sibling clone and a BUILD INPUT to this gate."
            fixturesRoot

        exit 1

    let manifest: obj = JS.JSON.parse (read manifestPath)
    let entries: obj array = manifest?scenarios |> unbox

    // The UI family's entries only: since fuaran#2011 the manifest carries a
    // second scenario family, over a toy witness this UI decoder cannot read.
    entries
    |> Array.toList
    |> List.filter (fun entry -> unbox<string> entry?family = ToyScenarios.UiFamily)
    |> List.map (fun entry ->
        let files: obj = entry?files
        let name: string = entry?name
        let policy: obj = entry?hostPolicy

        { Name = name
          TreeJson = read (fixturesRoot + "/" + unbox<string> files?tree)
          Events = parseEvents (read (fixturesRoot + "/" + unbox<string> files?events))
          Expected = parseExpectation name (read (fixturesRoot + "/" + unbox<string> files?expectation))
          HostPolicy =
            if isNullOrUndefined policy then
                None
            else
                Some(unbox<string> policy) })

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

    let toyFailures =
        if List.contains ToyScenarios.Family selected then
            runToyFamily root
        else
            printfn "-- %s: SKIPPED - not selected by %s" ToyScenarios.Family ToyScenarios.SelectionVariable
            0

    let fixtures =
        if List.contains ToyScenarios.UiFamily selected then
            loadScenarios root
        else
            printfn "-- %s: SKIPPED - not selected by %s" ToyScenarios.UiFamily ToyScenarios.SelectionVariable
            []

    // A leg that found no scenarios must FAIL, not pass quietly. The vacuous
    // green is the failure mode a conformance family has to be immune to.
    if List.isEmpty fixtures && List.contains ToyScenarios.UiFamily selected then
        eprintfn "the corpus at %s enumerates no driver-semantics scenario" root
        exit 1
    else

        let mutable failures = toyFailures

        for fixture in fixtures do
            if List.isEmpty fixture.Expected then
                eprintfn "%s: no recorded expectation — run --emit-fixtures on the .NET side" fixture.Name
                failures <- failures + 1
            else
                // The expectation is brought into THIS host's terms first: it is
                // a document, not a string of somebody's bytes, so a host with
                // its own encoder compares its own output on both sides.
                match normaliseExpectation fixture.Name fixture.Expected with
                | Error e ->
                    eprintfn "%s: %s" fixture.Name e
                    failures <- failures + 1
                | Ok expected ->
                    match runClientPlacement fixture with
                    | Error e ->
                        eprintfn "%s: %s" fixture.Name e
                        failures <- failures + 1
                    | Ok observed ->
                        match compare fixture.Name "expected" expected "Runtime/Fable" observed with
                        | None -> printfn "ok   %s (%d step(s))" fixture.Name (List.length observed)
                        | Some divergence ->
                            eprintfn "FAIL %s" (Divergence.describe divergence)
                            failures <- failures + 1

        printfn
            "%s: %d scenario(s); %d failure(s) across the selected families"
            ToyScenarios.UiFamily
            (List.length fixtures)
            failures

        exit (if failures > 0 then 1 else 0)

run ()
