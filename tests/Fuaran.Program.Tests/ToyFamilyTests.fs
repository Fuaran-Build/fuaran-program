/// Program's CONFORMANCE LEG over the `driver-semantics-toy` family
/// (fuaran#2011, `PROGRAM_WIRE.md` §10.6): every scenario the manifest
/// enumerates for the family, driven through every placement in scope and
/// compared step by step with the recorded trace — in a project that
/// references no UI-tier package, so nothing UI-typed is in the process
/// (`CoreBoundaryTests` checks it).
///
/// Also here: the toy codec's round trips, the coupling between the seeds and
/// the corpus, the harness obligations §10.1 point 6 states (the count, and a
/// mutated trace going red), and the record of which scenario families each
/// suite of this repository certifies.
module Fuaran.Program.Tests.ToyFamilyTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain
open Fuaran.Program.Tests.ToyScenarios
open Fuaran.Program.Tests.ToySeeds

let private root = ToyCorpus.fixturesRoot

/// Every action a tree carries, and every action beneath each, through the
/// witness's own view — what "the family covers this vocabulary" is read from.
let rec private actionsBeneath (action: ToyAction) : ToyAction list =
    action
    :: (match witness.Dispatch.Action.View action with
        | ActionView.Sequence members -> members |> List.collect actionsBeneath
        | ActionView.Choose(_, whenTrue, whenFalse, _) -> actionsBeneath whenTrue @ actionsBeneath whenFalse
        | ActionView.Repeat(_, body)
        | ActionView.Each(_, _, body) -> actionsBeneath body
        | ActionView.Assign _
        | ActionView.Call _
        | ActionView.Require _
        | ActionView.Leaf _ -> [])

let rec private treeActions (node: ToyNode) : ToyAction list =
    (node.Handlers |> List.collect (snd >> actionsBeneath))
    @ (node.Children |> List.collect treeActions)

let private shapeOf (action: ToyAction) : string =
    match witness.Dispatch.Action.View action with
    | ActionView.Sequence _ -> "Sequence"
    | ActionView.Assign _ -> "Assign"
    | ActionView.Call _ -> "Call"
    | ActionView.Require _ -> "Require"
    | ActionView.Choose _ -> "Choose"
    | ActionView.Repeat _ -> "Repeat"
    | ActionView.Each _ -> "Each"
    | ActionView.Leaf _ -> "Leaf"

// ─── Which families each suite certifies ────────────────────────────────────

/// The record the gate keeps of which scenario families each suite of this
/// repository certifies. Phase 2012 moves the UI family's certification out
/// with the UI adapters, and this is the table that move edits — and the test
/// below reads every suite's sources, so a suite that starts or stops reading a
/// family without this table saying so goes red here.
///
/// A family a suite reads is certified whenever the run SELECTS it
/// (`FUARAN_PROGRAM_FAMILIES`) — except in a suite listed in `ignoresSelection`,
/// which reads its families on every run.
let certifies: (string * string list) list =
    [ "Fuaran.Program.Bench", []
      "Fuaran.Program.Bounded.Tests", []
      "Fuaran.Program.Parity.Fable", [ UiFamily; Family ]
      "Fuaran.Program.Parity.Tests", [ UiFamily; Family ]
      "Fuaran.Program.Runtime.Tests", []
      "Fuaran.Program.Server.Tests", [ UiFamily ]
      "Fuaran.Program.Tests", [ Family ] ]

/// The suites that read a scenario family WITHOUT consulting the selection, and
/// so certify it even on a run that selected the toy family alone. Today that is
/// the server suite, whose two UI-family legs (the server-logic parity leg and
/// the durable interpreter's corpus pass) read through the parity loader
/// unconditionally; they are UI-typed and leave this repository with the UI
/// adapters in Phase 2012, and this entry with them. Recorded rather than
/// hidden: a "toy alone" run that still exercised a UI leg would otherwise read
/// as one that did not.
let ignoresSelection: string list = [ "Fuaran.Program.Server.Tests" ]

/// The calls through which a suite consults the selection.
let private selectionReader =
    Regex(@"\bFixtureIo\.selected\b|\bToyCorpus\.selected\b|\bToy(Scenarios|Family)\.selection\b")

/// The loader calls that read a family, by family. A suite reads the UI family
/// through the parity project's loader (or, under Fable, its own
/// `loadScenarios`), and this family through `ToyCorpus.load`, the shared
/// `ToyScenarios.load` (under Fable), or the proof host's `ToyFamily` alias of
/// that module.
let private readers: (string * Regex) list =
    [ UiFamily, Regex(@"\bFixtureIo\.(load|scenarios)\b|\bloadScenarios\b")
      Family, Regex(@"\bToy(Corpus|Scenarios|Family)\.load\b") ]

let private testsDir = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

/// A suite's own sources, comments stripped.
let private codeOf (suite: string) : string =
    Directory.GetFiles(Path.Combine(testsDir, suite), "*.fs")
    |> Array.collect File.ReadAllLines
    |> Array.map (fun line ->
        match line.IndexOf "//" with
        | -1 -> line
        | at -> line.Substring(0, at))
    |> String.concat "\n"

/// The families a suite's own sources read.
let private familiesReadBy (suite: string) : string list =
    let code = codeOf suite

    readers |> List.filter (fun (_, r) -> r.IsMatch code) |> List.map fst

[<Tests>]
let tests =
    let declared = ToyCorpus.declared root
    let selected = ToyCorpus.selected root

    testList
        "fuaran#2011 - the driver-semantics-toy family (conformance leg)"
        [ test "the manifest declares the family" {
              Expect.contains declared Family "the toy family is a declared scenario family"
          }

          test "a family selection names declared families only, and the toy family alone is a selection" {
              Expect.equal (selection None declared) (Ok declared) "unset selects every declared family"
              Expect.equal (selection (Some " ") declared) (Ok declared) "empty selects every declared family"
              Expect.equal (selection (Some Family) declared) (Ok [ Family ]) "the toy family alone"

              Expect.isError
                  (selection (Some "driver-semantics-nonesuch") declared)
                  "an undeclared name is refused, never matched against nothing"
          }

          test "which families each suite certifies, read from the suites' own sources" {
              let runners =
                  Directory.GetDirectories testsDir
                  |> Array.map Path.GetFileName
                  |> Array.filter (fun d ->
                      Directory.GetFiles(Path.Combine(testsDir, d), "*.fsproj")
                      |> Array.exists (fun p ->
                          let text = File.ReadAllText p
                          text.Contains "<OutputType>Exe</OutputType>" || text.Contains "FABLE_COMPILER"))
                  |> Array.sort
                  |> List.ofArray

              Expect.equal (certifies |> List.map fst) runners "every suite under tests/ is classified, and only those"

              for suite, families in certifies do
                  Expect.equal
                      (familiesReadBy suite)
                      families
                      (sprintf "%s reads exactly the families the table says it certifies" suite)

              for family in declared do
                  Expect.isTrue
                      (certifies |> List.exists (snd >> List.contains family))
                      (sprintf "the declared family %s is certified by some suite" family)

              // Every suite that reads a family consults the selection, save
              // the ones the table names as not doing so — and those do not.
              for suite, families in certifies do
                  if not (List.isEmpty families) then
                      Expect.equal
                          (selectionReader.IsMatch(codeOf suite))
                          (not (List.contains suite ignoresSelection))
                          (sprintf "%s consults the family selection exactly when the table says it does" suite)

              // A suite that ignores the selection reads no toy scenario, so on a
              // toy-alone run every piece of toy evidence is selected evidence.
              for suite in ignoresSelection do
                  Expect.isFalse
                      (certifies |> List.find (fst >> (=) suite) |> snd |> List.contains Family)
                      (sprintf "%s reads no toy scenario" suite)

              for suite, families in certifies do
                  printfn
                      "certifies: %-30s %s%s"
                      suite
                      (if List.isEmpty families then
                           "(no scenario family)"
                       else
                           String.concat ", " families)
                      (if List.contains suite ignoresSelection then
                           "  [reads them on every run]"
                       else
                           "")
          }

          if not (List.contains Family selected) then
              test "the toy family's scenarios" { skiptestf "%s is not selected by %s" Family SelectionVariable }
          else
              let scenarios = ToyCorpus.load root
              let entries = ToyCorpus.entries root

              test "the family is present and fully enumerated" {
                  Expect.isNonEmpty scenarios $"the corpus enumerates no {Family} scenario under {root}"

                  Expect.equal
                      (List.length scenarios)
                      (List.length entries)
                      "every scenario the manifest enumerates for the family was loaded and runs"

                  let bounded = scenarios |> List.filter clientInScope |> List.length

                  printfn
                      "conformance leg: %s - %d scenario(s) (%d bounded-loop, %d handler-loop)"
                      Family
                      (List.length scenarios)
                      bounded
                      (List.length scenarios - bounded)
              }

              testList
                  "every scenario, at every placement in scope"
                  [ for scenario in scenarios ->
                        test scenario.Name {
                            match ToyServerPlacement.runNamed scenario with
                            | Error e -> failtest e
                            | Ok server ->
                                match firstDivergence "expected" scenario.Expected "server-logic" server with
                                | Some d -> failtestf "%s: %s" scenario.Name d
                                | None -> ()

                            if clientInScope scenario then
                                let client = runClient scenario

                                match firstDivergence "expected" scenario.Expected "client" client with
                                | Some d -> failtestf "%s: %s" scenario.Name d
                                | None -> ()

                                // The no-handler reading: a server placement with
                                // nothing registered agrees with the client.
                                let bare = ToyServerPlacement.run (ServerServices.createPermissive witness) scenario

                                match firstDivergence "client" client "server-logic (no handlers)" bare with
                                | Some d -> failtestf "%s: %s" scenario.Name d
                                | None -> ()
                        } ]

              testList
                  "the harness obligations (§10.1 point 6)"
                  [ test "a trace that lost a step goes red, naming the step count" {
                        let s = scenarios.Head

                        match
                            firstDivergence
                                "expected"
                                (List.take (List.length s.Expected - 1) s.Expected)
                                "client"
                                (runClient s)
                        with
                        | Some d -> Expect.stringContains d "step(s)" "the count is what is reported"
                        | None -> failtest "a shortened trace passed"
                    }

                    test "a mutated tree, effect or refusal goes red at the FIRST step it touches" {
                        let s =
                            scenarios |> List.find (fun s -> clientInScope s && List.length s.Expected > 2)

                        let observed = runClient s

                        let mutateAt (i: int) (f: ToyStep -> ToyStep) =
                            s.Expected |> List.mapi (fun j step -> if j = i then f step else step)

                        let cases =
                            [ "tree", mutateAt 1 (fun st -> { st with Tree = st.Tree + " " })
                              "effects",
                              mutateAt 1 (fun st ->
                                  { st with
                                      Effects = st.Effects @ [ "{}" ] })
                              "refusal", mutateAt 1 (fun st -> { st with Refused = not st.Refused }) ]

                        for field, mutated in cases do
                            match firstDivergence "expected" mutated "client" observed with
                            | Some d ->
                                Expect.stringContains
                                    d
                                    "at step 1 "
                                    (sprintf "a mutated %s is reported at step 1" field)
                            | None -> failtestf "a mutated %s passed" field
                    } ]

              testList
                  "the toy codec"
                  [ test "every scenario tree is a canonical document: decoding and re-encoding reproduces its bytes" {
                        for entry in entries do
                            let bytes = File.ReadAllText(Path.Combine(root, entry.TreeFile))

                            match ToyWire.parseWith ToyWire.decodeNode bytes with
                            | Ok tree -> Expect.equal (ToyWire.renderNode tree) bytes (entry.Name + " round-trips")
                            | Error e -> failtestf "%s: %s" entry.Name e
                    }

                    test "every action in the family round-trips" {
                        for scenario in scenarios do
                            for action in treeActions scenario.Tree do
                                let encoded = Canon.render (ToyWire.encodeAction action)

                                match ToyWire.parseWith ToyWire.decodeAction encoded with
                                | Ok back -> Expect.equal (Canon.render (ToyWire.encodeAction back)) encoded encoded
                                | Error e -> failtestf "%s: %s" encoded e
                    }

                    test "the op is the bytes the witness's own stream writes, and decodes back" {
                        let op = Relabel("title", "Relabelled")
                        let bytes = Canon.render (ToyWire.encodeOp op)
                        Expect.equal bytes (witness.State.Stream.Encode op) "one spelling of the op"
                        Expect.equal (ToyWire.parseWith ToyWire.decodeOp bytes) (Ok op) "and it decodes"

                        Expect.isError
                            (ToyWire.parseWith
                                ToyWire.decodeOp
                                """{"$type":"Relabel","label":"x","target":"t","via":"y"}""")
                            "an undeclared member is refused"

                        Expect.isError
                            (ToyWire.parseWith
                                ToyWire.decodeOp
                                """{"$type":"Relabel","label":"x","target":"has space"}""")
                            "a target is an identifier"
                    }

                    test "the state round-trips, and a repeated key is refused" {
                        let store = Map.ofList [ "a", JStr "one"; "n", JInt 2; "sys.k", JBool true ]
                        let bytes = Canon.render (ToyWire.encodeState store)
                        Expect.equal bytes """{"a":"one","n":2,"sys.k":true}""" "members Ordinal, values as they are"
                        Expect.equal (ToyWire.parseWith ToyWire.decodeState bytes) (Ok store) "and it decodes"
                        Expect.isError (ToyWire.parseWith ToyWire.decodeState """{"a":1,"a":2}""") "a repeated key"
                        Expect.isError (ToyWire.parseWith ToyWire.decodeState """{"a":null}""") "a null member"
                    }

                    test "an effect is read in the emitter's member order and no other" {
                        let bytes = ToyWire.encodeEffect (Sound("n1", 3))
                        Expect.equal bytes """{"kind":"Sound","nodeId":"n1","volume":3}""" "the emitter's bytes"
                        Expect.equal (ToyWire.decodeEffect bytes) (Ok(Sound("n1", 3))) "decoded"

                        Expect.isError
                            (ToyWire.decodeEffect """{"nodeId":"n1","kind":"Sound","volume":3}""")
                            "re-ordered members are refused"

                        Expect.isError (ToyWire.decodeEffect """{"kind":"Bell","nodeId":"n1"}""") "an unknown arm"
                    } ]

              testList
                  "the seeds, the corpus and the vocabulary"
                  [ test "the corpus holds exactly the seeds: same names, same trees, same scripts" {
                        Expect.equal
                            (scenarios |> List.map _.Name)
                            (seeds |> List.map _.Name)
                            "one scenario per seed, in seed order"

                        for seed in seeds do
                            let scenario = scenarios |> List.find (fun s -> s.Name = seed.Name)

                            Expect.equal
                                (ToyWire.renderNode scenario.Tree)
                                (ToyWire.renderNode seed.Tree)
                                (seed.Name + ": the tree")

                            Expect.equal scenario.Events seed.Events (seed.Name + ": the event script")
                            Expect.equal scenario.Requires seed.Requires (seed.Name + ": the obligation")
                            Expect.equal scenario.HostHandlers seed.HostHandlers (seed.Name + ": the handler set")
                    }

                    test "the family names every view shape, both obligations and the registered handler set" {
                        let shapes =
                            scenarios
                            |> List.collect (_.Tree >> treeActions)
                            |> List.map shapeOf
                            |> Set.ofList

                        Expect.equal
                            shapes
                            (Set.ofList [ "Sequence"; "Assign"; "Call"; "Require"; "Choose"; "Repeat"; "Each"; "Leaf" ])
                            "all eight shapes the core folds"

                        Expect.equal
                            (scenarios |> List.map _.Requires |> List.distinct |> List.sort)
                            [ BoundedLoop; HandlerLoop ]
                            "both obligations"

                        Expect.equal
                            (scenarios |> List.choose _.HostHandlers |> List.distinct)
                            [ ToyRelabel ]
                            "the one registered handler set"

                        let refused =
                            scenarios
                            |> List.filter (fun s -> s.Expected |> List.exists _.Refused)
                            |> List.map _.Name

                        Expect.containsAll refused [ "refused-event"; "budget-refusal" ] "both event-level refusals"
                    }

                    test "every endpoint the handler set registers is named by some handler-loop scenario" {
                        let named =
                            scenarios
                            |> List.filter (fun s -> s.Requires = HandlerLoop)
                            |> List.collect (_.Tree >> treeActions)
                            |> List.choose (fun a ->
                                match witness.Dispatch.Action.View a with
                                | ActionView.Call(endpoint, _) -> Some endpoint
                                | _ -> None)
                            |> Set.ofList

                        Expect.equal
                            named
                            (ToyServerPlacement.toyRelabel.Handlers
                             |> Map.toList
                             |> List.map fst
                             |> Set.ofList)
                            "the argument-policy, staged-perform and op-channel handlers are all reached"
                    } ] ]
