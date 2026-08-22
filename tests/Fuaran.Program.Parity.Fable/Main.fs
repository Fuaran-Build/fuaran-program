module Fuaran.Program.Parity.Fable.Main

open Fable.Core
open Fable.Core.JsInterop
open Fuaran.Program.Parity.Runner
open Fuaran.Program.Parity

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

let private parseExpectation (json: string) : StepObservation list =
    let arr: obj array = JS.JSON.parse json |> unbox

    arr
    |> Array.toList
    |> List.map (fun el ->
        let effects: string array = el?effects |> unbox

        // The tree is an embedded DOCUMENT. Re-serialising it here hands the
        // decoder the right MEANING, never the right bytes — which is the
        // whole point of the placement-independent format: this loader's JSON
        // writer is not the corpus's, and nothing downstream cares.
        { ResolvedJson = JS.JSON.stringify (el?tree)
          Effects = List.ofArray effects
          Refused = el?refused })

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

    entries
    |> Array.toList
    |> List.map (fun entry ->
        let files: obj = entry?files

        { Name = entry?name
          TreeJson = read (fixturesRoot + "/" + unbox<string> files?tree)
          Events = parseEvents (read (fixturesRoot + "/" + unbox<string> files?events))
          Expected = parseExpectation (read (fixturesRoot + "/" + unbox<string> files?expectation)) })

let private run () =
    let root = if argv.Length > 0 then argv.[0] else "../wire-fixtures"

    let fixtures = loadScenarios root

    // A leg that found no scenarios must FAIL, not pass quietly. The vacuous
    // green is the failure mode a conformance family has to be immune to.
    if List.isEmpty fixtures then
        eprintfn "the corpus at %s enumerates no driver-semantics scenario" root
        exit 1
    else

        let mutable failures = 0

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

        printfn "%d scenario(s), %d failure(s)" (List.length fixtures) failures
        exit (if failures > 0 then 1 else 0)

run ()
