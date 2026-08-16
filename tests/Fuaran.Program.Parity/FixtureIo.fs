module Fuaran.Program.Parity.FixtureIo

// .NET-only: System.IO + System.Text.Json. The Fable leg has its own loader
// over node's fs and reads the SAME files, so this file is excluded from the
// Fable compilation rather than shimmed.
#if !FABLE_COMPILER

open System.IO
open System.Text.Json
open Fuaran.Program.Parity.Runner

// ============================================================================
//  Fixture IO for the .NET legs.
//
//  Deliberately NOT in `Parity.fs`: reading a directory is the one part of this
//  family that genuinely differs per host, so it is the one part that is
//  host-specific. The Fable leg has its own loader over node's `fs` and reads
//  the SAME files — which is the point. A fixture that only one host can read
//  would not be a parity fixture.
//
//  On-disk shape, one directory per fixture:
//
//    <name>/tree.json              the wire tree, canonical JSON
//    <name>/events.json            [ { "nodeId", "event", "payload" } ]
//    <name>/expected-resolved.json [ { "resolved", "effects", "refused" } ]
//
//  `expected-resolved.json` carries one entry per STEP, index 0 being the state
//  before any event — so a placement that resolved the initial tree differently
//  fails at step 0 rather than looking like a fold bug later.
// ============================================================================

let private readEvents (json: string) : ScriptedEvent list =
    use doc = JsonDocument.Parse json

    [ for el in doc.RootElement.EnumerateArray() ->
          { NodeId = el.GetProperty("nodeId").GetString()
            Event = el.GetProperty("event").GetString()
            Payload =
              match el.TryGetProperty "payload" with
              | true, p ->
                  [ for prop in p.EnumerateObject() -> prop.Name, prop.Value.GetString() ]
                  |> Map.ofList
              | _ -> Map.empty } ]

let private readExpected (json: string) : StepObservation list =
    use doc = JsonDocument.Parse json

    [ for el in doc.RootElement.EnumerateArray() ->
          { ResolvedJson = el.GetProperty("resolved").GetString()
            Effects = [ for e in el.GetProperty("effects").EnumerateArray() -> e.GetString() ]
            Refused = el.GetProperty("refused").GetBoolean() } ]

/// Load every fixture directory under `root`, in name order so a failure report
/// is stable run to run.
let load (root: string) : Fixture list =
    if not (Directory.Exists root) then
        []
    else
        Directory.GetDirectories root
        |> Array.sortBy Path.GetFileName
        |> Array.toList
        |> List.map (fun dir ->
            let read name =
                File.ReadAllText(Path.Combine(dir, name))

            let expectedPath = Path.Combine(dir, "expected-resolved.json")

            { Name = Path.GetFileName dir
              TreeJson = read "tree.json"
              Events = readEvents (read "events.json")
              Expected =
                if File.Exists expectedPath then
                    readExpected (read "expected-resolved.json")
                else
                    [] })

let private escape (s: string) : string = JsonSerializer.Serialize s

/// Write a fixture's tree + event script. Together with `writeExpected` this is
/// the whole on-disk artefact — the thing every leg reads, including the ones
/// that cannot run F#.
let writeTree (root: string) (fixture: Fixture) : unit =
    let dir = Path.Combine(root, fixture.Name)
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "tree.json"), fixture.TreeJson)

    let events =
        fixture.Events
        |> List.map (fun ev ->
            let payload =
                ev.Payload
                |> Map.toList
                |> List.map (fun (k, v) -> sprintf "%s: %s" (escape k) (escape v))
                |> String.concat ", "

            sprintf
                "  { \"nodeId\": %s, \"event\": %s, \"payload\": {%s} }"
                (escape ev.NodeId)
                (escape ev.Event)
                payload)
        |> String.concat
            ",
"

    File.WriteAllText(
        Path.Combine(dir, "events.json"),
        "[
"
        + events
        + "
]
"
    )

/// Write a fixture's expectation from an observed run — the `--emit-fixtures`
/// path, mirroring the estate's corpus-emit convention. Emitting is a
/// DELIBERATE act: it records what the placements currently agree on, so it must
/// only ever run when they DO agree, which the caller checks first.
let writeExpected (root: string) (fixture: Fixture) (observations: StepObservation list) : unit =
    let dir = Path.Combine(root, fixture.Name)
    Directory.CreateDirectory dir |> ignore

    let body =
        observations
        |> List.map (fun o ->
            let effects = o.Effects |> List.map escape |> String.concat ","

            sprintf
                "  { \"resolved\": %s, \"effects\": [%s], \"refused\": %s }"
                (escape o.ResolvedJson)
                effects
                (if o.Refused then "true" else "false"))
        |> String.concat ",\n"

    File.WriteAllText(Path.Combine(dir, "expected-resolved.json"), "[\n" + body + "\n]\n")
#endif
