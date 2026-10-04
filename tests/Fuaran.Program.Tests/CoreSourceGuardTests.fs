/// The core's structural guards, checked by scanning its SOURCES (fuaran#2012).
///
/// Three tests that read `src/Fuaran.Program.Server` and `src/Fuaran.Program.Bounded`
/// as text rather than calling either: the durable interpreter keeps no second
/// stage fold, its contract surface names no durable-execution product, and the
/// server package interprets an action nowhere but through the shared fold. They
/// lived in the server suite beside the UI-typed cases until fuaran#2012 moved
/// that suite out with the UI adapters; a guard about THIS repository's sources
/// can only run where the sources are, so they were re-homed here, verbatim, in
/// a project that reaches no UI type. Each still runs its probe against the file
/// that DOES carry the shape, so a green result is one that could have been red.
module Fuaran.Program.Tests.CoreSourceGuardTests

open Expecto

[<Tests>]
let tests =
    testList
        "the core's source guards (D1, Phase 1714, fuaran#2012)"
        [ test "no second stage fold: the durable interpreter never matches on a handler stage" {
              // The structural half of claim 1, checked rather than
              // asserted. `Durable.fs` supplies a registry and calls
              // `Handler.run`; the moment it starts matching on `Compute`
              // or `Effect` it has begun keeping a second copy of the
              // stage fold in step with the first by hand, which is the
              // failure this guard exists to make visible.
              let sources =
                  System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "Fuaran.Program.Server")
                  |> System.IO.Path.GetFullPath

              let stageMatches (file: string) =
                  System.IO.File.ReadAllLines(System.IO.Path.Combine(sources, file))
                  |> Array.indexed
                  |> Array.filter (fun (_, line) ->
                      let trimmed = line.TrimStart()
                      trimmed.StartsWith "| Compute" || trimmed.StartsWith "| Effect")
                  |> Array.map (fun (i, line) -> sprintf "%s:%d %s" file (i + 1) (line.Trim()))

              // The probe, proven able to fail: the same scan over the file
              // that DOES fold stages.
              Expect.isNonEmpty (stageMatches "Handler.fs") "the scan finds stage arms where stage arms exist"

              Expect.isEmpty
                  (stageMatches "Durable.fs")
                  "and none in the second interpreter: it journals effects, it does not re-fold stages"

              Expect.isTrue
                  (System.IO.File.ReadAllText(System.IO.Path.Combine(sources, "Durable.fs")).Contains "Handler.run")
                  "…because it calls the shared stage fold instead"
          }

          test "no contract surface names an engine" {
              // The phase's last acceptance clause, as a scan. Durable
              // execution is a contract with several implementations, and
              // naming one here would turn a portable guarantee into a
              // procurement decision.
              let sources =
                  System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "Fuaran.Program.Server")
                  |> System.IO.Path.GetFullPath

              // Unambiguous PRODUCT identifiers rather than the ordinary
              // words some of them are built from. A scan for "temporal"
              // would fire on "temporal coupling" and a scan for "restate"
              // on the English verb, and a check that fires on correct
              // prose is one the next reader deletes. What this can catch
              // is a name; what it cannot is a circumlocution, and saying
              // so is more useful than pretending otherwise.
              let vendors =
                  [ "temporal.io"
                    "cadence workflow"
                    "durabletask"
                    "restate.dev"
                    "inngest"
                    "step functions"
                    "durable functions" ]

              let scan (text: string) =
                  let lowered = text.ToLowerInvariant()
                  vendors |> List.filter lowered.Contains

              // The probe, proven able to fail.
              Expect.equal
                  (scan "journaled through Inngest")
                  [ "inngest" ]
                  "the scan finds a product name where one exists"

              let offenders =
                  [ for file in [ "Journal.fs"; "Facets.fs"; "Durable.fs" ] do
                        for vendor in scan (System.IO.File.ReadAllText(System.IO.Path.Combine(sources, file))) do
                            yield file + ": " + vendor ]

              Expect.isEmpty offenders "the contract surface names the discipline, never a product"
          }

          test "no second evaluator: this package matches on an Action nowhere" {
              // D1's guard, checked rather than asserted in a comment. The
              // handler arm moved into the shared fold precisely so that finding
              // a nested call would not require a second `Action` match here —
              // and a grep is the cheapest possible way to keep that true, since
              // the next person to add a special case would have to delete this
              // test to do it.
              //
              // Resolved from this source file rather than the working directory,
              // for the same reason the parity leg's fixture root is.
              let packageSources =
                  System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "Fuaran.Program.Server")
                  |> System.IO.Path.GetFullPath

              let armMatches (dir: string) =
                  System.IO.Directory.GetFiles(dir, "*.fs")
                  |> Array.collect (fun path ->
                      System.IO.File.ReadAllLines path
                      |> Array.indexed
                      // Since Phase 1896 the fold reads an action through the
                      // witness's VIEW, so an evaluating arm is a view arm as
                      // readily as a UI-action arm; both count.
                      |> Array.filter (fun (_, line) ->
                          let arm = line.TrimStart()
                          arm.StartsWith "| Action." || arm.StartsWith "| ActionView.")
                      |> Array.map (fun (i, line) ->
                          sprintf "%s:%d %s" (System.IO.Path.GetFileName path) (i + 1) (line.Trim())))

              Expect.isTrue (System.IO.Directory.Exists packageSources) $"the server package is at {packageSources}"

              Expect.isNonEmpty
                  (System.IO.Directory.GetFiles(packageSources, "*.fs"))
                  "…and it has sources to scan, so an empty result below is a finding rather than a miss"

              // The probe, proven able to fail: run the same scan over the
              // package that DOES interpret actions. A check that has never been
              // seen to go red is a check whose mechanism nobody has verified.
              let fold =
                  System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "Fuaran.Program.Bounded")
                  |> System.IO.Path.GetFullPath

              Expect.isNonEmpty (armMatches fold) "the scan finds arms where arms exist"

              Expect.isEmpty
                  (armMatches packageSources)
                  "and none here: the only place this domain interprets an Action is the shared fold"
          } ]
