module Fuaran.Program.Parity.Tests.Main

open Expecto
open Fuaran.Program.Parity.Runner
open Fuaran.Program.Parity

/// Regenerate the on-disk fixture corpus from the seeds.
///
/// Emitting is DELIBERATE and refuses to record a disagreement: the expectation
/// is only written once the placements already agree, because an expectation
/// minted from a divergence would enshrine the bug as the contract. That check
/// is the whole reason this is a mode rather than a build step.
let private emit (root: string) : int =
    let mutable failures = 0

    for seed in Seeds.all do
        match checkFixture seed with
        | Error e ->
            eprintfn "%s: %s" seed.Name e
            failures <- failures + 1
        | Ok(_ :: _ as divergences) ->
            eprintfn "%s: placements disagree — refusing to emit an expectation" seed.Name

            for d in divergences do
                eprintfn "%s" (Divergence.describe d)

            failures <- failures + 1
        | Ok [] ->
            match runClientPlacement seed with
            | Error e ->
                eprintfn "%s: %s" seed.Name e
                failures <- failures + 1
            | Ok observations ->
                FixtureIo.writeTree root seed
                FixtureIo.writeExpected root seed observations
                printfn "emitted %s (%d step(s))" seed.Name (List.length observations)

    if failures > 0 then
        eprintfn "%d fixture(s) not emitted" failures

    failures

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "--emit-fixtures" :: rest ->
        let root =
            match rest with
            | path :: _ -> path
            | [] -> ParityTests.fixtureRoot

        emit root
    | _ -> runTestsInAssemblyWithCLIArgs [] argv
