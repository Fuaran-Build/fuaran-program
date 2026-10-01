/// The generic core at a NON-UI witness (Phase 1896). This project references
/// the three core packages and no UI-tier package at all, so everything below
/// runs with no UI type in the process — the toy domain is the whole domain.
module Fuaran.Program.Tests.GenericityTests

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain

let private noCall =
    fun () -> failwith "the core invoked a closure a carried action holds"

let private run (action: ToyAction) (store: ToyStore) =
    BoundedActions.runInert witness "n1" action store

[<Tests>]
let tests =
    testList
        "the generic core at a non-UI witness"
        [ test "the view shapes fold at the toy's own types" {
              let action =
                  Seq
                      [ Put("a", Some(JStr "literal"), None)
                        Put("b", None, Some(Read "a"))
                        Beep 3
                        Beep 11
                        Hush
                        Ring("/nowhere", false, noCall)
                        Ring("/targeted", true, noCall)
                        Put("sys.secret", Some(JStr "no"), None)
                        Put("c", None, Some Missing)
                        Put("d", None, Some(Fail "boom")) ]

              let outcome = run action Map.empty

              Expect.equal
                  (outcome.Store |> Map.toList)
                  [ "a", JStr "literal"; "b", JStr "literal" ]
                  "exactly the two admitted writes land, the second reading the first"

              Expect.equal outcome.Effects [ Sound("n1", 3) ] "one effect, from the one admitted leaf"

              Expect.equal
                  outcome.Diagnostics
                  [ BoundedDiagnostic.Refused("n1", "Beep(11)", "volume 11 is above the ceiling")
                    BoundedDiagnostic.UnsupportedOnBoundedPath("n1", "Hush")
                    BoundedDiagnostic.UnsupportedOnBoundedPath("n1", "Ring(/nowhere)")
                    BoundedDiagnostic.Refused(
                        "n1",
                        "Ring(/targeted)",
                        "the call declares a result target; a handler declares where its own results land"
                    )
                    BoundedDiagnostic.Refused(
                        "n1",
                        "Put(sys.secret)",
                        "State key 'sys.secret' is under the host-reserved 'sys.' namespace"
                    )
                    BoundedDiagnostic.Refused(
                        "n1",
                        "Put(c)",
                        "valueFrom did not resolve to a value — no write performed"
                    )
                    BoundedDiagnostic.Refused("n1", "Put(d)", "valueFrom errored: boom — no write performed") ]
                  "every refusal and decline, in order, in the core's own words"
          }

          test "a leaf reads the store but never writes it (K3)" {
              let outcome = run (Beep 4) (Map.ofList [ "muted", JBool true ])
              Expect.equal outcome.Store (Map.ofList [ "muted", JBool true ]) "the store is untouched"
              Expect.isEmpty outcome.Effects "a muted beep emits nothing"
          }

          test "an answered call splices in place: it sees the write before it and is seen by the write after" {
              let arm: HandlerArm<ToyStore, ToyEffect, string list> =
                  { Answer =
                      fun nodeId endpoint store seen ->
                          let before = Map.tryFind "x" store

                          Some
                              { Store = Map.add "answered" (JStr endpoint) store
                                Effects = [ Sound(nodeId, 1) ]
                                Diagnostics = []
                                Placement = seen @ [ sprintf "%A" before ] } }

              let outcome, seen =
                  BoundedActions.run
                      witness
                      arm
                      "n1"
                      (Seq
                          [ Put("x", Some(JStr "first"), None)
                            Ring("/h", false, noCall)
                            Put("y", None, Some(Read "answered")) ])
                      Map.empty
                      []

              Expect.equal seen [ sprintf "%A" (Some(JStr "first")) ] "the arm saw the write before it"
              Expect.equal (Map.tryFind "y" outcome.Store) (Some(JStr "/h")) "and the write after it saw the answer"
              Expect.equal outcome.Effects [ Sound("n1", 1) ] "the answer's effect is folded in"
          }

          test "the static walks read the same view" {
              let action =
                  Seq
                      [ Put("ns.k", None, Some(Read "other.k"))
                        Ring("/e", false, noCall)
                        Beep 1
                        Hush ]

              Expect.equal (Budget.actionCascadeCost witness action) 4 "the cascade flattens the sequence"

              let projection = Demanded.ofAction witness action
              Expect.equal projection.Effects [ "Sound" ] "a leaf's declared effect kind"

              Expect.equal
                  (projection.HostCalls |> List.map (fun c -> c.Channel, c.Name))
                  [ "Call", "/e"; "Hush", "quiet" ]
                  "the call channel and the leaf's declared host call"

              Expect.equal
                  (projection.StateNamespaces |> List.map (fun n -> n.Namespace, n.Written, n.Read))
                  [ "ns", true, false; "other", false, true ]
                  "the assignment writes its namespace and its expression reads another"

              Expect.equal
                  (ProgramWire.replayDefectsOfAction witness action)
                  [ ReplayDefect.NonLiteralWrite; ReplayDefect.UndecidableAction ]
                  "the replay classification reads the view, not a wire tag"
          }

          test "the tree walks run over the toy tree" {
              let leaf =
                  { Id = "leaf"
                    Label = Read "title"
                    Handlers = [ "tap", Beep 2 ]
                    Children = [] }

              let root =
                  { Id = "root"
                    Label = Const(JStr "root")
                    Handlers = []
                    Children = [ leaf ] }

              let resolved =
                  Resolve.resolveTree witness (Map.ofList [ "title", JStr "hello" ]) root

              Expect.equal resolved.Children.Head.Label (Const(JStr "hello")) "re-resolution reaches the child"

              Expect.equal (Budget.treeCost witness System.Int32.MaxValue root) 3 "two nodes plus one handler's weight"
              Expect.equal (Demanded.ofTree witness root).Effects [ "Sound" ] "the handler's demand is found"

              let hash = SignedEnvelope.treeHash witness.Tree root

              Expect.stringStarts
                  hash
                  ProgramWire.ContentAddressPrefix
                  "the tree hash is Core's SHA-256 of the canonical form"
          }

          test "the server handler loop runs a toy handler" {
              let handler: Handler<ToyAction, ToyOp> =
                  { Name = "toy"
                    Stages =
                      [ Compute(Put("k", Some(JStr "v"), None))
                        Effect(ServerEffect.ApplyOps [ Relabel("root", "renamed") ]) ] }

              let tree =
                  { Id = "root"
                    Label = Const(JStr "root")
                    Handlers = []
                    Children = [] }

              let outcome =
                  Handler.run
                      witness
                      (ServerEffectRegistry.permissive ServerEffectRegistry.denyAll)
                      DataFrame.noResolve
                      "root"
                      handler
                      { Tree = tree; Bindings = Map.empty }

              Expect.isTrue outcome.Committed "the handler commits"
              Expect.equal (Map.tryFind "k" outcome.Store.Bindings) (Some(JStr "v")) "the compute stage wrote the store"

              Expect.equal
                  outcome.Store.Tree.Label
                  (Const(JStr "renamed"))
                  "the op stage applied through the op witness"
          }

          test "a guard that does not hold HALTS the sequence; a leaf's refusal does not (Phase 1967)" {
              // The fifth shape. A false guard stops everything after it and
              // the outcome says so; a refused leaf is a diagnostic the
              // sequence carries on past, exactly as before.
              let halting =
                  run
                      (Seq
                          [ Put("before", Some(JStr "1"), None)
                            Beep 11
                            Need(Const(JBool false))
                            Put("after", Some(JStr "2"), None)
                            Beep 2 ])
                      Map.empty

              Expect.isTrue halting.Halted "the guard halted the fold"

              Expect.equal
                  (halting.Store |> Map.toList)
                  [ "before", JStr "1" ]
                  "the write before the guard stands; the one after never ran"

              Expect.isEmpty halting.Effects "nothing after the guard emitted"

              Expect.equal
                  halting.Diagnostics
                  [ BoundedDiagnostic.Refused("n1", "Beep(11)", "volume 11 is above the ceiling")
                    BoundedDiagnostic.Refused("n1", "Need", "the guard did not hold") ]
                  "the leaf's refusal did not halt; the guard's did, and it is the last word"

              let holding = run (Seq [ Need(Const(JBool true)); Beep 2 ]) Map.empty
              Expect.isFalse holding.Halted "a guard that holds changes nothing"
              Expect.equal holding.Effects [ Sound("n1", 2) ] "and the sequence runs on"

              // The three ways a guard halts, and what each says.
              let reasonOf (condition: ToyExpr) =
                  match (run (Need condition) Map.empty).Diagnostics with
                  | [ BoundedDiagnostic.Refused(_, _, reason) ] -> reason
                  | other -> failwithf "expected one refusal, got %A" other

              Expect.equal (reasonOf (Const(JStr "yes"))) "the guard did not hold" "a non-boolean does not hold"
              Expect.equal (reasonOf Missing) "the guard did not resolve to a value" "an unresolved condition"

              Expect.equal
                  (reasonOf (Fail "bundle 'x' is ambiguous"))
                  "bundle 'x' is ambiguous"
                  "an errored one carries the domain's own text"

              // The static walks read the guard: it costs one step, reads what
              // its condition reads, and is undecidable on replay.
              let guarded = Seq [ Need(Read "args.note"); Beep 1 ]
              Expect.equal (Budget.actionCascadeCost witness guarded) 2 "a guard is one step"

              Expect.equal
                  ((Demanded.ofAction witness guarded).StateNamespaces
                   |> List.map (fun n -> n.Namespace, n.Written, n.Read))
                  [ "args", false, true ]
                  "a guard reads its condition's namespace and writes nothing"

              Expect.equal
                  (ProgramWire.replayDefectsOfAction witness guarded)
                  [ ReplayDefect.UndecidableAction ]
                  "a guard re-run against a moved store is undecidable"
          } ]
