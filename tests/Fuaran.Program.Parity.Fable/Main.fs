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
//  reads the SAME fixture files the .NET legs read, runs the SAME runner
//  compiled to JavaScript, and compares against the SAME recorded expectation.
//
//  The only thing that differs from the .NET legs is the loader — node's `fs`
//  instead of `System.IO` — which is the irreducible per-host part.
// ============================================================================

[<Import("readFileSync", "fs")>]
let private readFileSync (path: string, encoding: string) : string = jsNative

[<Import("readdirSync", "fs")>]
let private readdirSync (path: string) : string array = jsNative

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

let private parseExpected (json: string) : StepObservation list =
    let arr: obj array = JS.JSON.parse json |> unbox

    arr
    |> Array.toList
    |> List.map (fun el ->
        let effects: string array = el?effects |> unbox

        { ResolvedJson = el?resolved
          Effects = List.ofArray effects
          Refused = el?refused })

let private loadFixtures (root: string) : Fixture list =
    readdirSync root
    |> Array.sort
    |> Array.toList
    |> List.map (fun name ->
        let dir = root + "/" + name
        let expectedPath = dir + "/expected-resolved.json"

        { Name = name
          TreeJson = read (dir + "/tree.json")
          Events = parseEvents (read (dir + "/events.json"))
          Expected =
            if existsSync expectedPath then
                parseExpected (read expectedPath)
            else
                [] })

let private run () =
    let root = if argv.Length > 0 then argv.[0] else "../fixtures"

    let fixtures = loadFixtures root

    // A leg that found no fixtures must FAIL, not pass quietly. The vacuous
    // green is the failure mode a conformance family has to be immune to.
    if List.isEmpty fixtures then
        eprintfn "no fixtures under %s" root
        exit 1
    else

        let mutable failures = 0

        for fixture in fixtures do
            if List.isEmpty fixture.Expected then
                eprintfn "%s: no recorded expectation — run --emit-fixtures on the .NET side" fixture.Name
                failures <- failures + 1
            else
                match runClientPlacement fixture with
                | Error e ->
                    eprintfn "%s: %s" fixture.Name e
                    failures <- failures + 1
                | Ok observed ->
                    match compare fixture.Name "expected" fixture.Expected "Runtime/Fable" observed with
                    | None -> printfn "ok   %s (%d step(s))" fixture.Name (List.length observed)
                    | Some divergence ->
                        eprintfn "FAIL %s" (Divergence.describe divergence)
                        failures <- failures + 1

        printfn "%d fixture(s), %d failure(s)" (List.length fixtures) failures
        exit (if failures > 0 then 1 else 0)

run ()
