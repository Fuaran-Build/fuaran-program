module Fuaran.Program.Parity.Tests.ParityTests

open Expecto
open Fuaran.Program.Parity.Runner
open Fuaran.Program.Parity

// ─── Tier parity: one tree, one event script, identical everywhere ───────────
//
// The .NET half of the family — legs (a) `BoundedDriver` and (b)
// `Fuaran.Program.Runtime` on .NET. Leg (c), the same runner under Fable, is a
// separate node harness reading the SAME scenario files; `run.ps1` drives both.
//
// ── The scenarios are the corpus's, not this repository's ────────────────────
// They are the driver-semantics family of the program wire conformance corpus,
// which this repository certifies against as a HOST. The seeds in `Seeds.fs` are
// what GENERATED them (`--emit-fixtures`), and are re-checked here against the
// committed scenarios so a seed edited without a re-emit fails rather than
// silently disagreeing with the corpus every other leg reads.
//
// The MANIFEST is the enumeration, so the count is asserted rather than hoped
// for: a loader that silently found fewer scenarios than the corpus declares
// reports the same green as one that ran them all.

/// The corpus's fixture root — a sibling clone, overridable with
/// `FUARAN_PROGRAM_SPEC`. Resolved from the source tree rather than the working
/// directory: a suite that only passes when invoked from one directory is not a
/// gate, it is a coincidence.
let fixturesRoot = FixtureIo.fixturesRoot

[<Tests>]
let tests =
    let declared = FixtureIo.scenarios fixturesRoot
    let fixtures = FixtureIo.load fixturesRoot

    testList
        "tier parity"
        [ test "the corpus's driver-semantics family is present and fully enumerated" {
              // A parity family that silently found no scenarios would report
              // green while asserting nothing — the vacuous-pass shape.
              Expect.isNonEmpty fixtures $"the corpus enumerates no driver-semantics scenario under {fixturesRoot}"

              Expect.equal fixtures.Length declared.Length "every scenario the manifest enumerates was loaded and run"

              Expect.equal
                  (fixtures |> List.map _.Name |> List.sort)
                  (Seeds.all |> List.map _.Name |> List.sort)
                  "the corpus and the seeds name the same scenarios"
          }

          test "the manifest's declared step count is what each scenario carries" {
              // Counts are authoritative in the manifest, never in prose and
              // never in a harness's own arithmetic — so the file is measured
              // against the manifest rather than the other way round.
              for entry in declared do
                  let scenario = fixtures |> List.find (fun f -> f.Name = entry.Name)

                  Expect.equal
                      scenario.Expected.Length
                      entry.Steps
                      $"{entry.Name}: the expectation carries the number of steps the manifest declares"

                  Expect.equal
                      scenario.Expected.Length
                      (scenario.Events.Length + 1)
                      $"{entry.Name}: one entry per step, index 0 being the state before any event"
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
              "the seed and the committed scenario agree"
              [ for seed in Seeds.all ->
                    test seed.Name {
                        match fixtures |> List.tryFind (fun f -> f.Name = seed.Name) with
                        | None -> failtestf "%s has no committed scenario — run --emit-fixtures" seed.Name
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
          }

          test "the expectation is compared SEMANTICALLY, not byte-for-byte" {
              // The placement-independence claim, made executable — and the
              // probe verified in both directions, because a normalisation that
              // accepted everything would pass the first half alone.
              //
              // Re-serialising the recorded tree indented changes every byte and
              // no meaning, which is exactly the position a host with its own
              // encoder is in. It must still compare equal.
              let scenario = fixtures |> List.head
              let recorded = scenario.Expected

              let reserialise (indented: bool) (document: string) =
                  use doc = System.Text.Json.JsonDocument.Parse document

                  System.Text.Json.JsonSerializer.Serialize(
                      doc.RootElement,
                      System.Text.Json.JsonSerializerOptions(WriteIndented = indented)
                  )

              let reshaped =
                  recorded
                  |> List.map (fun step ->
                      { step with
                          ResolvedJson = reserialise true step.ResolvedJson })

              Expect.notEqual
                  (reshaped |> List.map _.ResolvedJson)
                  (recorded |> List.map _.ResolvedJson)
                  "the reshaping actually changed the bytes"

              let normalise input =
                  match normaliseExpectation scenario.Name input with
                  | Ok steps -> steps |> List.map _.ResolvedJson
                  | Error e -> failtestf "%s" e

              let fromCorpus = normalise recorded

              Expect.equal
                  (normalise reshaped)
                  fromCorpus
                  "two encodings of one tree normalise to the same thing — the corpus's bytes bind nobody"

              // And the other half: a normalisation that could not tell two
              // DIFFERENT trees apart would pass the above and be worthless.
              // Deliberately a change the decoder ACCEPTS — renaming the root
              // node rather than naming a case that does not exist. A
              // perturbation that fails to decode would be detected by the
              // decoder rather than by the comparison, which is a different
              // check passing under this one's name.
              let altered =
                  recorded
                  |> List.map (fun step ->
                      { step with
                          ResolvedJson = step.ResolvedJson.Replace("\"id\":\"root\"", "\"id\":\"rooted\"") })

              Expect.notEqual
                  (altered |> List.map _.ResolvedJson)
                  (recorded |> List.map _.ResolvedJson)
                  "the alteration actually changed the document"

              Expect.notEqual
                  (normalise altered)
                  fromCorpus
                  "a genuinely different tree does not normalise onto the recorded one"
          } ]
