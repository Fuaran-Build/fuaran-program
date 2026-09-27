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
        [ test "the four shapes fold at the toy's own types" {
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
          } ]
