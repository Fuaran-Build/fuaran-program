module Fuaran.Program.Parity.Tests.ParityTests

open Expecto
open Fuaran.Program.Parity.Runner
open Fuaran.Program.Parity

// ─── Tier parity: one tree, one event script, identical everywhere ───────────
//
// The .NET half of the family — legs (a) `BoundedDriver` and (b)
// `Fuaran.Program.Runtime` on .NET. Leg (c), the same runner under Fable, is a
// separate node harness reading the SAME fixture files; `run.ps1` drives both.
//
// The fixtures on disk are the shared artefact. The seeds in `Seeds.fs` are what
// GENERATED them (`--emit-fixtures`), and are re-checked here against the files
// so a seed edited without a re-emit fails rather than silently disagreeing with
// the corpus every other leg reads.

/// The fixture root, resolved from the test assembly rather than the working
/// directory — a suite that only passes when invoked from one directory is not
/// a gate, it is a coincidence.
let fixtureRoot =
    System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "fixtures")
    |> System.IO.Path.GetFullPath

[<Tests>]
let tests =
    let fixtures = FixtureIo.load fixtureRoot

    testList
        "tier parity"
        [ test "the fixture corpus is present" {
              // A parity family that silently found no fixtures would report
              // green while asserting nothing — the vacuous-pass shape.
              Expect.isNonEmpty fixtures $"no fixtures under {fixtureRoot}"

              Expect.equal
                  (fixtures |> List.map _.Name |> List.sort)
                  (Seeds.all |> List.map _.Name |> List.sort)
                  "the on-disk corpus and the seeds name the same fixtures"
          }

          testList
              "placements agree, step by step"
              [ for fixture in fixtures ->
                    test fixture.Name {
                        match checkFixture fixture with
                        | Error e -> failtestf "%s: %s" fixture.Name e
                        | Ok [] -> ()
                        | Ok divergences ->
                            let report = divergences |> List.map Divergence.describe |> String.concat "\n\n"
                            failtestf "%s" report
                    } ]

          testList
              "the seed and the on-disk tree agree"
              [ for seed in Seeds.all ->
                    test seed.Name {
                        match fixtures |> List.tryFind (fun f -> f.Name = seed.Name) with
                        | None -> failtestf "%s has no on-disk fixture — run --emit-fixtures" seed.Name
                        | Some onDisk ->
                            Expect.equal
                                onDisk.TreeJson
                                seed.TreeJson
                                $"{seed.Name}: the on-disk tree.json is stale — re-run --emit-fixtures"

                            Expect.equal
                                onDisk.Events
                                seed.Events
                                $"{seed.Name}: the on-disk events.json is stale — re-run --emit-fixtures"
                    } ]

          test "the comparison DETECTS a divergence (the probe, verified)" {
              // A parity check that cannot fail is worth nothing, and the
              // cheapest way to know it can is to hand it two runs that differ.
              let a =
                  [ { ResolvedJson = "same"
                      Effects = []
                      Refused = false }
                    { ResolvedJson = "left"
                      Effects = []
                      Refused = false } ]

              let b =
                  [ { ResolvedJson = "same"
                      Effects = []
                      Refused = false }
                    { ResolvedJson = "right"
                      Effects = []
                      Refused = false } ]

              match compare "probe" "A" a "B" b with
              | None -> failtest "the comparison missed a divergence it was handed"
              | Some d ->
                  Expect.equal d.Step 1 "it named the FIRST differing step, not the last"
                  Expect.equal d.Field "resolved tree" "and the field that differed"
          }

          test "the comparison detects a step-count divergence" {
              let a =
                  [ { ResolvedJson = "x"
                      Effects = []
                      Refused = false } ]

              Expect.isSome (compare "probe" "A" a "B" []) "a placement that stopped early is a divergence"
          } ]
