/// Reading and writing the `driver-semantics-toy` family on .NET (fuaran#2011).
/// Reading a file is the one per-host part of the family, so it is the one part
/// here; the Fable parity leg has its own loader over node's `fs` and hands the
/// SAME text to the SAME shared `ToyScenarios.load`.
///
/// The corpus is a sibling clone and a BUILD INPUT, resolved exactly as the UI
/// family's loader resolves it: `FUARAN_PROGRAM_SPEC`, else the sibling path
/// relative to this source file. Its absence FAILS rather than skips.
module Fuaran.Program.Tests.ToyCorpus

open System
open System.IO
open System.Text
open Fuaran.Program.Tests.ToyScenarios
open Fuaran.Program.Tests.ToySeeds

let corpusRoot: string =
    match Environment.GetEnvironmentVariable "FUARAN_PROGRAM_SPEC" with
    | null
    | "" ->
        Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Fuaran-UI", "fuaran-program-spec")
        |> Path.GetFullPath
    | declared -> Path.GetFullPath declared

let fixturesRoot: string = Path.Combine(corpusRoot, "wire-fixtures")

let private manifestText (root: string) : string =
    let path = Path.Combine(root, "manifest.json")

    if not (File.Exists path) then
        failwithf
            "the conformance corpus is not present at '%s'. It is a sibling clone and a BUILD INPUT to this gate, \
             not an optional extra — clone it beside this repository, or point FUARAN_PROGRAM_SPEC at it. This \
             suite fails rather than skipping: a conformance check that passes when its oracle is missing is worse \
             than no check."
            root

    File.ReadAllText path

let private orFail (result: Result<'T, string>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failwith e

/// The scenario families the manifest declares.
let declared (root: string) : string list =
    declaredFamilies (manifestText root) |> orFail

/// The families this run SELECTS — `FUARAN_PROGRAM_FAMILIES`, else all. An
/// undeclared name fails the run (`ToyScenarios.selection`).
let selected (root: string) : string list =
    selection (Option.ofObj (Environment.GetEnvironmentVariable SelectionVariable)) (declared root)
    |> orFail

/// This family's manifest entries.
let entries (root: string) : ToyEntry list =
    ToyScenarios.entries (manifestText root) |> orFail

/// Load every scenario of this family the manifest enumerates. A file the
/// manifest names that the tree does not carry is a failure, not an omission.
let load (root: string) : ToyScenario list =
    entries root
    |> List.map (fun entry ->
        let read (relative: string) =
            let path = Path.Combine(root, relative)

            if not (File.Exists path) then
                failwithf "the corpus enumerates '%s', which is not present at '%s'" relative path

            File.ReadAllText path

        ToyScenarios.load entry (read entry.TreeFile) (read entry.EventsFile) (read entry.ExpectationFile)
        |> orFail)

// ─── The resident emitter: `--emit-toy-scenarios` ──────────────────────────

/// UTF-8 without a byte-order mark, LF only, no trailing newline: a scenario
/// file is digested, and every one of those is a byte.
let private writeFile (path: string) (text: string) : unit =
    File.WriteAllText(path, text.Replace("\r\n", "\n"), UTF8Encoding false)

/// The trace every placement in scope produces for a seed — or the reason they
/// do not agree, in which case NOTHING is written: emitting records what the
/// placements agree on, and recording a disagreement would make one
/// placement's accident the corpus.
let observe (seed: ToySeed) : Result<ToyStep list, string> =
    let scenario = asScenario seed

    if seed.Requires = BoundedLoop then
        let client = runClient scenario

        let server =
            ToyServerPlacement.run (Fuaran.Program.Server.ServerServices.createPermissive ToyDomain.witness) scenario

        match firstDivergence "client" client "server-logic (no handlers)" server with
        | None -> Ok client
        | Some d -> Error(seed.Name + ": " + d)
    else
        ToyServerPlacement.runNamed scenario

/// Write every seed's three files under `<root>/driver-semantics-toy/`, and
/// return each seed's manifest entry (digests are the corpus checker's to
/// refresh: `node check-scenarios.mjs --write`).
let emit (root: string) : Result<string list, string> =
    let traces = seeds |> List.map (fun seed -> seed, observe seed)

    match
        traces
        |> List.tryPick (fun (_, r) ->
            match r with
            | Error e -> Some e
            | Ok _ -> None)
    with
    | Some e -> Error e
    | None ->
        traces
        |> List.map (fun (seed, trace) ->
            let steps =
                match trace with
                | Ok s -> s
                | Error e -> failwith e

            let dir = Path.Combine(root, Family, seed.Name)
            Directory.CreateDirectory dir |> ignore
            writeFile (Path.Combine(dir, "tree.json")) (ToyWire.renderNode seed.Tree)
            writeFile (Path.Combine(dir, "events.json")) (renderEvents seed.Events)
            writeFile (Path.Combine(dir, "expectation.json")) (renderTrace steps)

            let q (s: string) = "\"" + Fuaran.Core.Json.escape s + "\""

            let rel (file: string) =
                q (Family + "/" + seed.Name + "/" + file)

            String.concat
                "\n"
                [ "{"
                  sprintf "  \"id\": %s," (q (Family + "/" + seed.Name))
                  sprintf "  \"family\": %s," (q Family)
                  sprintf "  \"name\": %s," (q seed.Name)
                  "  \"kind\": \"step-trace\","
                  sprintf "  \"requires\": %s," (q seed.Requires)
                  (match seed.HostHandlers with
                   | Some h -> sprintf "  \"hostHandlers\": %s," (q h)
                   | None -> "")
                  sprintf "  \"dir\": %s," (q (Family + "/" + seed.Name))
                  sprintf
                      "  \"files\": { \"tree\": %s, \"events\": %s, \"expectation\": %s },"
                      (rel "tree.json")
                      (rel "events.json")
                      (rel "expectation.json")
                  "  \"sha256\": { \"tree\": \"\", \"events\": \"\", \"expectation\": \"\" },"
                  sprintf "  \"steps\": %d," (List.length steps)
                  sprintf "  \"description\": %s" (q seed.Description)
                  "}" ]
            |> fun s -> s.Replace("\n\n", "\n"))
        |> Ok
