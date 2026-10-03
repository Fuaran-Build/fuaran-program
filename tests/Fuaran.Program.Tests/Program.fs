module Fuaran.Program.Tests.Main

open Expecto

/// Regenerate the `driver-semantics-toy` scenarios from the seeds (fuaran#2011)
/// — the resident emitter for the toy subject, in the posture the UI family's
/// `--emit-fixtures` takes: DELIBERATE, and refusing to record a disagreement,
/// because a trace minted from a divergence would enshrine the bug as the
/// contract. It writes the scenario BYTES and prints each seed's manifest
/// entry; the manifest's digests and step counts are the corpus's own tool's
/// to refresh (`node wire-fixtures/check-scenarios.mjs --write`), so a host
/// never rewrites the index it is certified against.
let private emit (root: string) : int =
    match ToyCorpus.emit root with
    | Error e ->
        eprintfn "refusing to emit: %s" e
        1
    | Ok manifestEntries ->
        printfn "emitted %d scenario(s) under %s; manifest entries:" (List.length manifestEntries) root
        printfn "%s" ("[\n" + String.concat ",\n" manifestEntries + "\n]")
        0

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "--emit-toy-scenarios" :: rest ->
        emit (
            match rest with
            | path :: _ -> path
            | [] -> ToyCorpus.fixturesRoot
        )
    | _ -> runTestsInAssemblyWithCLIArgs [] argv
