module Fuaran.Program.Tests.StagedQueryPostureTests

// ============================================================================
//  The static postures name a staged query evaluator (Phase 2187,
//  DECISIONS.md D44), at the toy witness.
//
//  D34 staged a read whose evaluator its host could not declare a pure read:
//  the run records it as `Reached`, the undo refuses there, and the durable
//  interpreter journals it. The demanded document's replay reasons and undo
//  posture now say so, read off the host's declared query posture
//  (`ServerEffectRegistry.queryPosture`).
//
//  What is pinned here:
//    * for each evaluator posture — absent, a declared pure read, reaching —
//      the STATIC replay reasons and undo posture agree with what the RUN does:
//      a read the projection calls staged is exactly a read the durable
//      interpreter journals and replays from the journal, and an undo posture
//      one-way at a read is exactly a run whose undo is refused there;
//    * the agreement check can go red: the static postures read under a
//      declaration the registry does not make disagree with the run;
//    * absent and pure read publish the same document, carrying no reason for
//      the read, as every posture did before the token existed.
// ============================================================================

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain

type private ToyHandler = Handler<ToyAction, ToyOp>
type private ToyServerStore = ServerStore<ToyNode, ToyStore>

let private orders: Table =
    { Schema = [ "id", IntType ]
      Columns =
        [ { Name = "id"
            Type = IntType
            Cells = [ Int 1; Int 2 ] } ] }

let private resolve =
    QueryEvaluatorLaws.resolverOf (Map.ofList [ "orders", orders ])

let private leaf (id: string) : ToyNode =
    { Id = id
      Label = Const(JStr id)
      Handlers = []
      Children = [] }

let private store: ToyServerStore =
    { Tree =
        { leaf "root" with
            Children = [ leaf "call" ] }
      Bindings = Map.empty }

/// One read, at stage 0: the handler whose postures turn on the host alone.
let private handler: ToyHandler =
    { Name = "orders.read"
      Stages = [ Effect(ServerEffect.RunQuery("orders", Ref "orders", [])) ] }

let private permissive: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll

/// The three postures a host can be in, as the REGISTRY declares them — the
/// static side reads the posture off the same registry the run is given.
let private hosts: (string * ServerEffectRegistry) list =
    [ "absent", permissive
      "pure read",
      permissive
      |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.pureRead QueryEvaluator.fold)
      "reaching",
      permissive
      |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.reaching QueryEvaluator.fold) ]

/// What the projection says about the read at stage 0, under a posture.
type private StaticSays =
    {
        /// The replay reasons name the read as staged.
        ReplayStaged: bool
        /// The undo posture is one-way, because of the read.
        UndoOneWayAtRead: bool
    }

/// What the run does with the read at stage 0.
type private RunDoes =
    {
        /// The durable interpreter journaled the read, and a second run served
        /// it from the journal rather than asking again.
        Journaled: bool
        /// The undo of a committed run was refused at the read.
        UndoRefusedAtRead: bool
    }

let private staticUnder (query: QueryPosture) : StaticSays =
    let replay = HandlerWire.replayReasons query witness handler
    let undo = Undo.postureOf query witness handler

    { ReplayStaged =
        replay = [ { ReplayReason.Stage = 0
                     Defect = ReplayDefect.StagedQuery } ]
      UndoOneWayAtRead =
        undo.Undo = "one-way"
        && undo.Reasons = [ { UndoReasonDemand.Stage = 0
                              Defect = "staged-query" } ] }

let private runUnder (registry: ServerEffectRegistry) : RunDoes =
    let services =
        DurableServices.create
        |> DurableServices.withJournal (Journal.inMemory ())
        |> DurableServices.declaringQueryEvaluator IdempotencyFacet.Idempotent

    let first = Durable.run witness services "inv" registry resolve "call" handler store
    let again = Durable.run witness services "inv" registry resolve "call" handler store

    let outcome, plan =
        Handler.runPlanned witness registry OpPerformance.InMemory resolve "call" handler store

    let undo =
        Undo.run witness registry OpPerformance.InMemory resolve "call" plan outcome.Store

    { Journaled = first.Invoked = [ 0 ] && again.Replayed = [ 0 ]
      UndoRefusedAtRead =
        match undo with
        | Error refusal -> refusal.Code = UndoCode.OneWayStep && refusal.Reason = "RunQuery"
        | Ok _ -> false }

/// The agreement the shard asks for, as one predicate: the projection calls
/// the read staged exactly when the run journals it, and one-way exactly when
/// the run's undo is refused at it.
let private agrees (says: StaticSays) (does: RunDoes) : bool =
    says.ReplayStaged = does.Journaled
    && says.UndoOneWayAtRead = does.UndoRefusedAtRead

[<Tests>]
let tests =
    testList
        "Phase 2187 — the static postures name a staged query evaluator"
        [ testList
              "static agrees with runtime, per evaluator posture"
              [ for name, registry in hosts ->
                    testCase name
                    <| fun () ->
                        let says = staticUnder (ServerEffectRegistry.queryPosture registry)
                        let does = runUnder registry

                        Expect.isTrue
                            (agrees says does)
                            $"the projection says {says} and the run does {does} — they must describe one handler"

                        // And the posture each side lands on, so agreement on the
                        // wrong answer cannot pass either.
                        let staged = name = "reaching"
                        Expect.equal says.ReplayStaged staged "the replay reasons name the read only when it is staged"
                        Expect.equal does.Journaled staged "the run journals the read only when it is staged" ]

          testCase "the agreement check can go red: a posture the registry does not declare disagrees with the run"
          <| fun () ->
              for name, registry in hosts do
                  let declared = ServerEffectRegistry.queryPosture registry

                  let other =
                      match declared with
                      | QueryPosture.Reaching -> QueryPosture.PureRead
                      | QueryPosture.PureRead -> QueryPosture.Reaching

                  Expect.isFalse
                      (agrees (staticUnder other) (runUnder registry))
                      $"{name}: the projection read under the wrong posture is caught"

          testCase "absent is the fold, a pure read: the registry declares it so"
          <| fun () ->
              Expect.equal
                  (ServerEffectRegistry.queryPosture ServerEffectRegistry.denyAll)
                  QueryPosture.PureRead
                  "an absent evaluator is the in-memory fold"

              Expect.equal (QueryEvaluator.postureOf None) QueryEvaluator.inMemory.Posture "…which declares a pure read"

          testCase "absent and pure read publish the same document; reaching names the read in both postures"
          <| fun () ->
              let published (registry: ServerEffectRegistry) =
                  (Harvest.ofRegistration (ServerEffectRegistry.queryPosture registry) witness [ handler ]).Projection
                  |> Demanded.encode

              let absent = published permissive
              let pureRead = published (snd hosts[1])
              let reaching = published (snd hosts[2])

              Expect.equal pureRead absent "a declared pure read publishes exactly what the fold does"

              Expect.stringContains
                  absent
                  "\"replay\":[{\"handler\":\"orders.read\",\"safety\":\"safe\",\"reasons\":[]}]"
                  "under the fold the read carries no replay reason"

              Expect.stringContains
                  absent
                  "\"undo\":[{\"handler\":\"orders.read\",\"undo\":\"reversible\",\"reasons\":[]}]"
                  "…and no undo reason"

              Expect.stringContains
                  reaching
                  "\"replay\":[{\"handler\":\"orders.read\",\"safety\":\"unsafe\",\"reasons\":[{\"stage\":0,\"defect\":\"staged-query\"}]}]"
                  "a staged read is a reach a re-run repeats"

              Expect.stringContains
                  reaching
                  "\"undo\":[{\"handler\":\"orders.read\",\"undo\":\"one-way\",\"reasons\":[{\"stage\":0,\"defect\":\"staged-query\"}]}]"
                  "…and a step the undo cannot take back"

          testCase "a resume of a staged read is refused, as a host call's is"
          <| fun () ->
              let decide registry =
                  Replay.admit
                      (ServerEffectRegistry.queryPosture registry)
                      witness
                      ReplayMode.Resume
                      Replay.strict
                      handler

              for name, registry in hosts do
                  let refused =
                      match (decide registry).Admission with
                      | ReplayAdmission.Refused _ -> true
                      | _ -> false

                  Expect.equal refused (name = "reaching") $"{name}: resume is refused only for a staged read" ]
