module Fuaran.Program.Tests.FlowDecisionTests

// ─── B3: the outcome names the arms it took (Phase 1982) ─────────────
//
// The second witness's third run mapped a verb's arm onto an exit code by
// reading the branch conditions a second time, which is sound only while no
// arm writes what a condition reads. `HandlerOutcome.Flow` says which arm each
// branch took and how many times each repeat ran, in plan order and pre-order,
// from the op axis (the verb domain's `Branch` / `Times`) and from a compute
// stage's fold (the toy domain's `Pick` / `Times`) alike.

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.Program.Bounded
open Fuaran.Program.Server

let private registry: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll

// ── the op axis, through the verb domain ────────────────────────────────────

module private Verb =
    open Fuaran.Program.Tests.VerbDomain

    let empty: FileMap = { Files = Map.empty; Published = [] }

    let seeded () =
        let world = World()
        world.Seed("notes/x.md", "live")
        world

    let run (world: World) (handler: Handler<Nothing, FileOp>) =
        Handler.runWith
            witness
            registry
            (OpPerformance.performedBy (world.Performer None))
            DataFrame.noResolve
            "verb"
            handler
            { Tree = { empty with Files = world.Files }
              Bindings = () }

    let migrate: Handler<Nothing, FileOp> =
        { Name = "migrate"
          Stages =
            [ Effect(
                  ServerEffect.ApplyOps
                      [ Branch(
                            Exists "notes/x.md",
                            [ Write("notes/x.md", "migrated") ],
                            [ Write("notes/x.md", "created"); Write("notes/created.marker", "") ],
                            Some(Missing "notes/created.marker")
                        )
                        Publish "origin" ]
              ) ] }

// ── a compute stage's fold, through the toy domain ─────────────────────────

module private Toy =
    open Fuaran.Program.Tests.ToyDomain

    let root: ToyNode =
        { Id = "n1"
          Label = Const(JStr "x")
          Handlers = []
          Children = [] }

    let run (handler: Handler<ToyAction, ToyOp>) (bindings: ToyStore) =
        Handler.run witness registry DataFrame.noResolve "n1" handler { Tree = root; Bindings = bindings }

    let put (key: string) (value: string) = Put(key, Some(JStr value), None)

[<Tests>]
let tests =
    testList
        "flow decisions on the outcome (Phase 1982, B3)"
        [ test "a branch on the op axis names the arm it took, true and false" {
              let took = Verb.run (Verb.seeded ()) Verb.migrate
              Expect.isTrue took.Committed "the true arm commits"
              Expect.equal took.Flow [ FlowDecision.Chose true ] "the true arm, named"

              let created = Verb.run (VerbDomain.World()) Verb.migrate
              Expect.isTrue created.Committed "the false arm commits"
              Expect.equal created.Flow [ FlowDecision.Chose false ] "the false arm, named"
          }

          test "a repeat names its count, and the decisions inside it follow it in pre-order" {
              let nested: Handler<Nothing, VerbDomain.FileOp> =
                  { Name = "nested"
                    Stages =
                      [ Effect(
                            ServerEffect.ApplyOps
                                [ VerbDomain.Times(
                                      2,
                                      [ VerbDomain.Branch(
                                            VerbDomain.Exists "notes/x.md",
                                            [ VerbDomain.Publish "origin" ],
                                            [],
                                            None
                                        ) ]
                                  )
                                  VerbDomain.Branch(VerbDomain.Missing "notes/x.md", [], [], None) ]
                        ) ] }

              let outcome = Verb.run (Verb.seeded ()) nested
              Expect.isTrue outcome.Committed "commits"

              Expect.equal
                  outcome.Flow
                  [ FlowDecision.Repeated 2
                    FlowDecision.Chose true
                    FlowDecision.Chose true
                    FlowDecision.Chose false ]
                  "the repeat, then each iteration's branch, then the branch after it"
          }

          test "a handler that uses neither shape reports no flow" {
              let plain: Handler<Nothing, VerbDomain.FileOp> =
                  { Name = "plain"
                    Stages = [ Effect(ServerEffect.ApplyOps [ VerbDomain.Publish "origin" ]) ] }

              Expect.isEmpty (Verb.run (Verb.seeded ()) plain).Flow "no branch, no repeat, no decision"
          }

          test "a halted handler keeps the decisions taken before the effect that halted" {
              let halting: Handler<Nothing, VerbDomain.FileOp> =
                  { Name = "halting"
                    Stages =
                      [ Effect(
                            ServerEffect.ApplyOps
                                [ VerbDomain.Branch(
                                      VerbDomain.Exists "notes/x.md",
                                      [ VerbDomain.Write("notes/x.md", "migrated") ],
                                      [],
                                      None
                                  ) ]
                        )
                        // The guard fails: the file is still there.
                        Effect(
                            ServerEffect.ApplyOps
                                [ VerbDomain.Branch(VerbDomain.Exists "notes/x.md", [], [], None)
                                  VerbDomain.Check(VerbDomain.Missing "notes/x.md") ]
                        ) ] }

              let world = Verb.seeded ()
              let outcome = Verb.run world halting
              Expect.isFalse outcome.Committed "the guard halted the handler"
              Expect.isEmpty world.Invocations "nothing performed"

              Expect.equal
                  outcome.Flow
                  [ FlowDecision.Chose true ]
                  "the first effect's decision survives the rollback; the refused effect contributes none of its own"
          }

          test "a compute stage's branch and repeat are named too, read off the fold's trace" {
              let handler: Handler<ToyDomain.ToyAction, ToyDomain.ToyOp> =
                  { Name = "toy"
                    Stages =
                      [ Compute(
                            ToyDomain.Pick(ToyDomain.Read "flag", Toy.put "took" "true", Toy.put "took" "false", None)
                        )
                        Compute(ToyDomain.Times(Bound.Literal 3, ToyDomain.Beep 1)) ] }

              let onTrue = Toy.run handler (Map.ofList [ "flag", JBool true ])
              Expect.isTrue onTrue.Committed "commits"
              Expect.equal onTrue.Flow [ FlowDecision.Chose true; FlowDecision.Repeated 3 ] "the arm, then the count"

              let onFalse = Toy.run handler (Map.ofList [ "flag", JBool false ])
              Expect.equal onFalse.Flow [ FlowDecision.Chose false; FlowDecision.Repeated 3 ] "the other arm"
          }

          test "the trace reading is pre-order and counts the iterations that ran" {
              let trace =
                  Trace.Seq
                      [ Trace.Repeat [ Trace.Choose(true, Trace.Nothing); Trace.Choose(false, Trace.Wrote None) ]
                        Trace.Choose(false, Trace.Repeat []) ]

              Expect.equal
                  (FlowDecision.ofTrace trace)
                  [ FlowDecision.Repeated 2
                    FlowDecision.Chose true
                    FlowDecision.Chose false
                    FlowDecision.Chose false
                    FlowDecision.Repeated 0 ]
                  "each decision before the decisions inside it"
          } ]
