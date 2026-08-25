module Fuaran.Program.Server.Tests.DurableInterpreterTests

// ─── The second interpreter, and the facet it certifies ──────────────────────
//
// Four claims, and each of them is the kind that is easy to assert and hard to
// earn, so each is checked in the form that could go red.
//
//  1. ONE ALGEBRA, TWO INTERPRETERS. The same handler registration and the same
//     corpus scenarios produce the same results under both — asserted over the
//     whole driver-semantics corpus step by step, not over a hand-picked case,
//     and at the handler level over every arm of the closed vocabulary.
//
//  2. A CRASH MID-HANDLER COSTS NO DUPLICATE EFFECT. The fixtures kill the
//     interpreter inside a performer, replay from the journal, and count the
//     performer's own invocations across BOTH runs. A count is the only form of
//     this claim that cannot be satisfied by an implementation that merely looks
//     careful.
//
//  3. THE BOUNDARY IS DECLARED, NOT PAPERED OVER. A crash can land between the
//     effect and the record of it, and no engineering inside this repository
//     closes that window — so the fixtures drive the indeterminate step and pin
//     what each policy does with it, INCLUDING the one that duplicates.
//
//  4. THE CONJUNCTION NEVER INFLATES. The composition's facet is at least as
//     weak as every arm's, exhaustively over the lattice — and the negative test
//     at the foot of this file is an inflating declaration that must be
//     REFUSED. A check that has never been seen to refuse anything is a check
//     nobody has verified.
//
// ── The one rule this suite must never quietly relax ─────────────────────────
// The indeterminate step is REFUSED by default. Rounding it up to "it probably
// ran, serve the record" or down to "it probably did not, run it again" both
// read as tidier code and both publish a guarantee the substrate does not
// provide. A future edit that removes the refusal fails here, on purpose.

open Expecto
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Ops
open Fuaran.UI.Ops.Types
open Fuaran.UI.OpStream.Abstractions
open Fuaran.UI.OpStream.Replay
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Parity
open Fuaran.Program.Parity.Runner

// ─── fixtures ────────────────────────────────────────────────────────────────

let private jstr (s: string) = Fuaran.Core.JStr s

let private handlerEndpoint = "/handlers/refresh"

[<Literal>]
let private handlerFixture = "server-handler-call"

/// A counting performer: the number of times the host actually ran it, which is
/// the whole subject of the crash-replay family.
type private Counter() =
    let mutable count = 0
    member _.Count = count
    member _.Bump() = count <- count + 1

/// A performer that succeeds, counting.
let private counting (counter: Counter) (answer: string) =
    fun (_: Fuaran.Core.JVal) ->
        counter.Bump()
        Ok(jstr answer)

/// A performer that COMMITS and then the process dies — the crash inside the
/// indeterminate window, where the effect happened and the record of it did not.
exception private ProcessDied of string

let private committingThenDying (counter: Counter) =
    fun (_: Fuaran.Core.JVal) ->
        counter.Bump()
        raise (ProcessDied "after the effect")

/// A performer that dies BEFORE it commits anything — the other side of the same
/// window, indistinguishable from the above in the journal, and deliberately so.
let private dyingBeforeCommitting (_: Counter) =
    fun (_: Fuaran.Core.JVal) -> raise (ProcessDied "before the effect")

/// Run something that is expected to die, and report whether it did. A crash the
/// fixture did not observe would leave every assertion after it meaningless.
let private crashing (f: unit -> 'T) : bool =
    try
        f () |> ignore
        false
    with ProcessDied _ ->
        true

let private rows: Fuaran.Core.Table =
    { Schema = [ "n", Fuaran.Core.IntType ]
      Columns =
        [ { Name = "n"
            Type = Fuaran.Core.IntType
            Cells = [ Fuaran.Core.Int 1; Fuaran.Core.Int 2; Fuaran.Core.Int 3 ] } ] }

/// One arm of every server capability, in the order a real handler uses them.
/// The same shape the tier-parity family's handler takes, so a difference
/// between the two interpreters would show against a registration the corpus
/// already exercises.
let private refreshHandler: Handler =
    { Name = "refresh"
      Stages =
        [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, [ Fuaran.Core.Limit(2, 0) ]))
          Compute(Action.SetState("rows", Some(jstr "2 rows"), None))
          Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "refresh") ])
          Effect(ServerEffect.HostCall("audit", jstr "refreshed", None))
          Effect(ServerEffect.EmitPatch [ TreeOp.RemoveNode(NodeId "readout") ])
          Effect(ServerEffect.Notify("audit", jstr "refreshed")) ] }

/// A handler with two host calls, so a crash can land at the SECOND and leave
/// the first recorded — the shape the certification actually needs.
let private twoCalls (a: string) (b: string) : Handler =
    { Name = "two"
      Stages =
        [ Effect(ServerEffect.HostCall(a, jstr "one", Some "first"))
          Effect(ServerEffect.HostCall(b, jstr "two", Some "second")) ] }

/// The domain tree the handler fixtures run against. It carries the two nodes
/// `refreshHandler` addresses, so an `ApplyOps` arm exercises the apply engine
/// rather than halting on a node that is not there — which would make every
/// assertion after it a test of the rollback path instead.
let private baseTree: Node<obj> =
    Fuaran.dashboard
        "root"
        { Defaults.dashboard<obj> with
            Children =
                [ Fuaran.button
                      "refresh"
                      { Defaults.button<obj> with
                          Label = TextSource.Literal "refresh" }
                  Fuaran.markdown "readout" "idle" ] }

let private emptyStore: ServerStore = { Tree = baseTree; Bindings = empty }

let private registryOf (performers: (string * (Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>)) list) =
    performers
    |> List.fold (fun r (fn, p) -> ServerEffectRegistry.register fn p r) ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.permissive

/// The comparable projection of a handler outcome.
///
/// Not the record itself: a resolved tree's nodes carry handler slots, so a
/// structural comparison of `Node<obj>` is not defined. The canonical encoding
/// IS defined and is what every other parity leg in this repository compares, so
/// it is what this one compares too.
let private projectionOf (outcome: HandlerOutcome) =
    {| Tree = CanonicalJson.encodeNode outcome.Store.Tree
       State = outcome.Store.Bindings.State |> Map.map (fun _ v -> sprintf "%A" v)
       Queries = outcome.Store.Bindings.QueryResults |> Map.toList |> List.map fst
       Committed = outcome.Committed
       Performed = outcome.Performed
       Patches = outcome.Patches |> List.map (sprintf "%A")
       Notifications = outcome.Notifications
       Effects = outcome.ClientEffects |> List.map ClientEffect.encode
       Diagnostics = outcome.Diagnostics |> List.map (sprintf "%A") |}

// ─── the corpus, under both interpreters ─────────────────────────────────────

let private toLiveEvent (index: int) (ev: ScriptedEvent) : LiveEvent =
    { ConnId = "durable"
      NodeId = ev.NodeId
      Event = ev.Event
      Payload = ev.Payload |> Map.map (fun _ v -> LiveValue.Str v)
      LastSeq = index }

/// Drive a fixture through the server placement with a NAMED arm, producing the
/// tier-parity family's per-step observation. The arm is the only parameter, so
/// a divergence between two runs of this function is a divergence between two
/// interpreters and can be nothing else.
let private driveWith
    (armFor: ServerSession -> string -> HandlerArm<HandlerTally>)
    (services: ServerServices)
    (fixture: Fixture)
    : Result<StepObservation list, string> =
    match JsonDecode.decodeNode fixture.TreeJson, registryFor fixture.HostPolicy with
    | Error err, _ -> Error(sprintf "decode failed: %A" err)
    | _, Error e -> Error(sprintf "%s: %s" fixture.Name e)
    | Ok wire, Ok registry ->
        let session = ServerSession.init services empty wire

        let observations =
            fixture.Events
            |> List.mapi toLiveEvent
            |> List.mapi (fun i ev -> i, ev)
            |> List.scan
                (fun (session, _) (i, ev) ->
                    let next, out =
                        ServerSession.stepWith (armFor session (sprintf "%s#%d" fixture.Name i)) session ev

                    next,
                    Some
                        { ResolvedJson = CanonicalJson.encodeNode out.Resolved
                          Effects = out.ClientEffects |> List.map ClientEffect.encode
                          Refused = out.Rejected.IsSome
                          Denials = observeDenials fixture.HostPolicy registry out.ClientEffects })
                (session, None)
            |> List.choose snd

        Ok(
            { ResolvedJson = CanonicalJson.encodeNode session.Resolved
              Effects = []
              Refused = false
              Denials = observeDenials fixture.HostPolicy registry [] }
            :: observations
        )

let private directly =
    fun (session: ServerSession) _ -> ServerSession.directArm session.Services

let private durably (services: DurableServices) =
    fun (session: ServerSession) (invocation: string) -> Durable.arm services invocation session.Services

let private openServices =
    { ServerServices.createPermissive with
        Effects = registryOf [ "audit", (fun _ -> Ok(jstr "recorded")) ] }

// ─── the facet lattice, for the law tests ────────────────────────────────────

let private allHazards =
    [ for lose in [ false; true ] do
          for duplicate in [ false; true ] ->
              { MayLose = lose
                MayDuplicate = duplicate } ]

let private allDerived =
    [ for delivery in allHazards do
          for idempotency in IdempotencyFacet.all do
              for restart in RestartVisibility.all ->
                  { Delivery = delivery
                    Idempotency = idempotency
                    Restart = restart } ]

let private allEffects =
    [ ServerEffect.RunQuery("slot", Fuaran.Core.Embedded rows, [])
      ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "x") ]
      ServerEffect.HostCall("audit", jstr "a", None)
      ServerEffect.EmitPatch [ TreeOp.RemoveNode(NodeId "x") ]
      ServerEffect.Notify("ops", jstr "n") ]

let private allDisciplines =
    PlacementDiscipline.Direct
    :: [ for survives in [ false; true ] do
             for reinvoke in [ false; true ] ->
                 PlacementDiscipline.DeterministicReplay
                     { JournalSurvivesRestart = survives
                       ReinvokeIndeterminate = reinvoke } ]

let private durableDiscipline (reinvoke: bool) =
    PlacementDiscipline.DeterministicReplay
        { JournalSurvivesRestart = true
          ReinvokeIndeterminate = reinvoke }

let private logicTree: LogicTreeRef = { Ref = "orders/refresh"; Hash = None }

[<Tests>]
let tests =
    let fixtures = FixtureIo.load FixtureIo.fixturesRoot

    testList
        "durable execution — the second interpreter"
        [ testList
              "one algebra, two interpreters"
              [ test "the corpus is present" {
                    Expect.isNonEmpty fixtures "the corpus enumerates no driver-semantics scenario"
                }

                testList
                    "every corpus scenario agrees step by step under both interpreters"
                    [ for fixture in fixtures ->
                          test fixture.Name {
                              let services =
                                  openServices |> ServerServices.withHandler handlerEndpoint refreshHandler

                              let durable =
                                  DurableServices.create
                                  |> DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ()))
                                  |> DurableServices.declaringPerformer "audit" IdempotencyFacet.Idempotent

                              match
                                  driveWith directly services fixture, driveWith (durably durable) services fixture
                              with
                              | Error e, _
                              | _, Error e -> failtestf "%s: %s" fixture.Name e
                              | Ok direct, Ok replayed ->
                                  match compare fixture.Name "Handler.run" direct "Durable.run" replayed with
                                  | None -> ()
                                  | Some divergence -> failtestf "%s" (Divergence.describe divergence)
                          } ]

                test "at the handler level, every arm of the vocabulary agrees" {
                    let counter = Counter()
                    let registry = registryOf [ "audit", counting counter "recorded" ]

                    let direct =
                        Handler.run registry Fuaran.Core.DataFrame.noResolve "node" refreshHandler emptyStore

                    let durable =
                        Durable.run
                            (DurableServices.create |> DurableServices.withJournal (Journal.inMemory ()))
                            "inv"
                            registry
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            refreshHandler
                            emptyStore

                    Expect.isTrue direct.Committed "the direct interpreter committed"
                    Expect.isTrue durable.Outcome.Committed "and so did the durable one"

                    Expect.equal
                        (projectionOf durable.Outcome)
                        (projectionOf direct)
                        "the same handler, the same store, the same result — the outcome is the direct \
                         interpreter's own type and compares as one"

                    Expect.equal counter.Count 2 "each interpreter reached the performer exactly once"
                }

                test "a halted handler agrees too — including what it rolled back" {
                    // The interesting half of parity: agreement on the SUCCESS
                    // path is what any two implementations achieve first, and
                    // agreement on a rollback is what tells you the second one
                    // did not quietly reimplement the fold.
                    let registry =
                        registryOf [ "ok", (fun _ -> Ok(jstr "y")); "no", (fun _ -> Error "refused by host") ]

                    let handler = twoCalls "ok" "no"

                    let direct =
                        Handler.run registry Fuaran.Core.DataFrame.noResolve "node" handler emptyStore

                    let durable =
                        Durable.run
                            (DurableServices.create |> DurableServices.withJournal (Journal.inMemory ()))
                            "inv"
                            registry
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            handler
                            emptyStore

                    Expect.isFalse direct.Committed "the direct interpreter rolled back"

                    Expect.equal
                        (projectionOf durable.Outcome)
                        (projectionOf direct)
                        "and the durable one rolled back identically — same Performed prefix, same \
                         PerformFailed diagnostic"
                }

                test "no second stage fold: the durable interpreter never matches on a handler stage" {
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
                        (System.IO.File.ReadAllText(System.IO.Path.Combine(sources, "Durable.fs")).Contains
                            "Handler.run")
                        "…because it calls the shared stage fold instead"
                } ]

          testList
              "crash mid-handler, replayed from the journal"
              [ test "replaying a completed invocation reaches no performer at all" {
                    // The cleanest reading of exactly-once-effective: the process
                    // died after the handler finished and before the caller
                    // recorded the outcome, so the whole invocation runs again.
                    // Every step is served, nothing outside is touched, and the
                    // outcome is identical because it was recomputed rather than
                    // stored.
                    let counter = Counter()
                    let registry = registryOf [ "audit", counting counter "recorded" ]
                    let journal = Journal.declaringDurable (Journal.inMemory ())
                    let services = DurableServices.create |> DurableServices.withJournal journal

                    let first =
                        Durable.run
                            services
                            "inv"
                            registry
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            refreshHandler
                            emptyStore

                    let replay =
                        Durable.run
                            services
                            "inv"
                            registry
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            refreshHandler
                            emptyStore

                    Expect.equal first.Invoked [ 0 ] "the first run performed the one host call"
                    Expect.equal replay.Invoked [] "the replay performed none"
                    Expect.equal replay.Replayed [ 0 ] "it served that step from the journal"
                    Expect.equal counter.Count 1 "so the performer ran ONCE across both runs — no duplicate effect"

                    Expect.equal
                        (projectionOf replay.Outcome)
                        (projectionOf first.Outcome)
                        "and the replayed outcome is the recorded one, recomputed"

                    Expect.isTrue
                        (Journal.isComplete (journal.Read "inv"))
                        "the invocation carries its completion marker"
                }

                test "a crash at the second call leaves the first served and never re-run" {
                    let first = Counter()
                    let boom = Counter()
                    let journal = Journal.declaringDurable (Journal.inMemory ())
                    let services = DurableServices.create |> DurableServices.withJournal journal
                    let handler = twoCalls "first" "boom"

                    let crashed =
                        crashing (fun () ->
                            Durable.run
                                services
                                "inv"
                                (registryOf [ "first", counting first "a"; "boom", committingThenDying boom ])
                                Fuaran.Core.DataFrame.noResolve
                                "node"
                                handler
                                emptyStore)

                    Expect.isTrue crashed "the interpreter was killed inside the second performer"
                    Expect.equal first.Count 1 "the first call had already run"
                    Expect.equal boom.Count 1 "and the second had committed before the process died"

                    Expect.equal
                        (Journal.describe (journal.Read "inv"))
                        [ "0 host:first attempted"; "0 host:first completed"; "1 host:boom attempted" ]
                        "the journal records exactly that: one step decided, one step attempted and no more"

                    // The replay. The second performer would now succeed, which
                    // is the point — a resume must not be tempted by it.
                    let replay =
                        Durable.run
                            services
                            "inv"
                            (registryOf [ "first", counting first "a"; "boom", counting boom "b" ])
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            handler
                            emptyStore

                    Expect.equal replay.Replayed [ 0 ] "the decided step was served from the journal"
                    Expect.equal first.Count 1 "so the first call was NOT performed a second time"
                    Expect.equal replay.Indeterminate [ 1 ] "and the undecided step was refused"
                    Expect.equal boom.Count 1 "so nothing ran twice — zero duplicate effects across the crash"

                    Expect.isFalse replay.Outcome.Committed "the handler rolled back around the refusal"

                    Expect.equal
                        replay.Outcome.Diagnostics
                        [ ServerDiagnostic.PerformFailed("host:boom", DurableCode.IndeterminateStep) ]
                        "…naming the step it could not decide, in the closed vocabulary and with no payload"
                }

                test "the indeterminate step is REFUSED by default — the rule that must not relax" {
                    let boom = Counter()
                    let journal = Journal.declaringDurable (Journal.inMemory ())
                    let services = DurableServices.create |> DurableServices.withJournal journal
                    let handler = twoCalls "boom" "boom"

                    crashing (fun () ->
                        Durable.run
                            services
                            "inv"
                            (registryOf [ "boom", committingThenDying boom ])
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            handler
                            emptyStore)
                    |> fun died -> Expect.isTrue died "killed inside the first performer"

                    Expect.equal boom.Count 1 "the effect happened once"

                    let replay =
                        Durable.run
                            services
                            "inv"
                            (registryOf [ "boom", counting boom "b" ])
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            handler
                            emptyStore

                    Expect.equal boom.Count 1 "the strict policy refused rather than repeating it"
                    Expect.equal replay.Overrides [] "and recorded no override, because none was used"
                }

                test "the accepting policy re-invokes, records the override, and DUPLICATES" {
                    // The honest boundary, driven rather than described. This is
                    // the fixture a reader should look at before believing any
                    // exactly-once claim on this page: the placement CAN be
                    // configured to duplicate, and when it is, the facet
                    // derivation below says at-least-once.
                    let boom = Counter()
                    let journal = Journal.declaringDurable (Journal.inMemory ())

                    let services =
                        DurableServices.create
                        |> DurableServices.withJournal journal
                        |> DurableServices.acceptingIndeterminateReplay

                    let handler = twoCalls "boom" "boom"

                    crashing (fun () ->
                        Durable.run
                            services
                            "inv"
                            (registryOf [ "boom", committingThenDying boom ])
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            handler
                            emptyStore)
                    |> ignore

                    let replay =
                        Durable.run
                            services
                            "inv"
                            (registryOf [ "boom", counting boom "b" ])
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            handler
                            emptyStore

                    Expect.equal boom.Count 3 "the effect ran again — once before the crash, twice on the replay"

                    Expect.equal
                        (replay.Overrides |> List.map (fun o -> o.Step, o.Capability))
                        [ 0, "host:boom" ]
                        "and the override was RECORDED, so a caller receives the fact rather than being \
                         trusted to remember it"
                }

                test "a performer the host declares idempotent closes the window with no override" {
                    let boom = Counter()
                    let journal = Journal.declaringDurable (Journal.inMemory ())

                    let services =
                        DurableServices.create
                        |> DurableServices.withJournal journal
                        |> DurableServices.declaringPerformer "boom" IdempotencyFacet.Idempotent

                    let handler = twoCalls "boom" "boom"

                    crashing (fun () ->
                        Durable.run
                            services
                            "inv"
                            (registryOf [ "boom", dyingBeforeCommitting boom ])
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            handler
                            emptyStore)
                    |> ignore

                    Expect.equal boom.Count 0 "the process died before the effect — the other side of the same window"

                    let replay =
                        Durable.run
                            services
                            "inv"
                            (registryOf [ "boom", counting boom "b" ])
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            handler
                            emptyStore

                    Expect.isTrue replay.Outcome.Committed "the resume completed"
                    Expect.equal replay.Overrides [] "no override was needed: the performer's own shape closes it"
                    Expect.equal boom.Count 2 "both calls ran, each exactly once"
                }

                test "a replay that diverges is refused rather than served the wrong answer" {
                    // The premise of ordinal addressing is that the plan phase
                    // recomputes the same call list. A `RunQuery` reads data that
                    // may have moved, so the premise can fail — and when it does,
                    // serving one call's recorded answer to another is the worst
                    // available outcome. The journal records the capability so
                    // the divergence is detectable at all.
                    let journal = Journal.declaringDurable (Journal.inMemory ())
                    let services = DurableServices.create |> DurableServices.withJournal journal
                    let counter = Counter()

                    Durable.run
                        services
                        "inv"
                        (registryOf [ "alpha", counting counter "a"; "beta", counting counter "b" ])
                        Fuaran.Core.DataFrame.noResolve
                        "node"
                        (twoCalls "alpha" "beta")
                        emptyStore
                    |> ignore

                    // The same invocation id, a differently-shaped recomputation.
                    let replay =
                        Durable.run
                            services
                            "inv"
                            (registryOf [ "alpha", counting counter "a"; "beta", counting counter "b" ])
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            (twoCalls "beta" "alpha")
                            emptyStore

                    Expect.isFalse replay.Outcome.Committed "the divergent replay did not commit"

                    Expect.equal
                        replay.Outcome.Diagnostics
                        [ ServerDiagnostic.PerformFailed("host:beta", DurableCode.ReplayDivergence) ]
                        "it named the ordinal's capability mismatch and stopped"

                    Expect.equal counter.Count 2 "and reached no performer on the replay"
                }

                test "with no journal the interpreter degrades honestly to the direct one" {
                    let counter = Counter()
                    let registry = registryOf [ "audit", counting counter "recorded" ]

                    let first =
                        Durable.run
                            DurableServices.create
                            "inv"
                            registry
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            refreshHandler
                            emptyStore

                    let again =
                        Durable.run
                            DurableServices.create
                            "inv"
                            registry
                            Fuaran.Core.DataFrame.noResolve
                            "node"
                            refreshHandler
                            emptyStore

                    Expect.equal first.Invoked [ 0 ] "the first run performed the call"
                    Expect.equal again.Replayed [] "the second served nothing — there was nothing to serve"
                    Expect.equal counter.Count 2 "so it ran twice, exactly as the direct interpreter would"
                } ]

          testList
              "the conjunction rule, and what it refuses to claim"
              [ test "combination is associative, commutative, idempotent, with a two-sided identity" {
                    // Pinned over the whole 36-value lattice rather than
                    // sampled: a combination whose laws nobody checked is a
                    // combination nobody can reason with.
                    for a in allDerived do
                        Expect.equal (Facets.combine a Facets.neutral) a "neutral is a right identity"
                        Expect.equal (Facets.combine Facets.neutral a) a "and a left identity"
                        Expect.equal (Facets.combine a a) a "idempotent"

                        for b in allDerived do
                            Expect.equal (Facets.combine a b) (Facets.combine b a) "commutative"

                            for c in allDerived do
                                Expect.equal
                                    (Facets.combine (Facets.combine a b) c)
                                    (Facets.combine a (Facets.combine b c))
                                    "associative"
                }

                test "combination NEVER strengthens: every part's hazards survive into the whole" {
                    for a in allDerived do
                        for b in allDerived do
                            let combined = Facets.combine a b

                            for part in [ a; b ] do
                                Expect.isTrue
                                    (not part.Delivery.MayLose || combined.Delivery.MayLose)
                                    "a part that may lose makes the whole one that may lose"

                                Expect.isTrue
                                    (not part.Delivery.MayDuplicate || combined.Delivery.MayDuplicate)
                                    "and a part that may duplicate makes the whole one that may duplicate"

                                Expect.isTrue
                                    (IdempotencyFacet.hazardRank combined.Idempotency
                                     >= IdempotencyFacet.hazardRank part.Idempotency)
                                    "the whole is no more idempotent than its least idempotent part"

                                Expect.isTrue
                                    (RestartVisibility.hazardRank combined.Restart
                                     >= RestartVisibility.hazardRank part.Restart)
                                    "and no more restart-durable than its least durable part"
                }

                test "a derived handler facet is never stronger than any arm it holds" {
                    // The same law one level up, over the real derivation rather
                    // than over synthetic triples — so a future arm added to the
                    // per-arm table cannot escape it.
                    for discipline in allDisciplines do
                        for performers in
                            [ PerformerFacets.none
                              PerformerFacets.none
                              |> PerformerFacets.declare "audit" IdempotencyFacet.Idempotent ] do
                            let handler =
                                { Name = "all"
                                  Stages = allEffects |> List.map Effect }

                            let whole = Facets.ofHandler discipline performers handler

                            for effect in allEffects do
                                let arm = Facets.ofEffect discipline performers effect

                                Expect.isTrue
                                    (not arm.Delivery.MayLose || whole.Delivery.MayLose)
                                    $"{ServerEffect.kind effect}: a losing arm makes a losing handler"

                                Expect.isTrue
                                    (not arm.Delivery.MayDuplicate || whole.Delivery.MayDuplicate)
                                    $"{ServerEffect.kind effect}: a duplicating arm makes a duplicating handler"
                }

                test "the strongest facet is reachable — and only where it is earned" {
                    let engineOwned =
                        { Name = "engine"
                          Stages =
                            [ Effect(ServerEffect.RunQuery("slot", Fuaran.Core.Embedded rows, []))
                              Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "x") ])
                              Effect(ServerEffect.Notify("ops", jstr "n")) ] }

                    Expect.equal
                        (Facets.ofHandler (durableDiscipline false) PerformerFacets.none engineOwned
                         |> Facets.narrowest
                         |> Option.map _.Delivery)
                        (Some DeliveryFacet.ExactlyOnceEffective)
                        "a handler that reaches nothing outside is exactly-once-effective under replay"

                    Expect.equal
                        (Facets.ofHandler PlacementDiscipline.Direct PerformerFacets.none engineOwned
                         |> Facets.narrowest
                         |> Option.map _.Delivery)
                        (Some DeliveryFacet.AtMostOnce)
                        "…and is NOT under the direct interpreter, which journals nothing"

                    let declared =
                        PerformerFacets.none
                        |> PerformerFacets.declare "audit" IdempotencyFacet.Idempotent

                    Expect.equal
                        (Facets.ofHandler (durableDiscipline false) declared refreshHandler
                         |> Facets.narrowest
                         |> Option.map _.Delivery)
                        (Some DeliveryFacet.ExactlyOnceEffective)
                        "a host call the host declares idempotent earns the same facet"
                }

                test "an undeclared performer is never flattered — the honest boundary, both ways" {
                    Expect.equal
                        (Facets.ofHandler (durableDiscipline false) PerformerFacets.none refreshHandler
                         |> Facets.narrowest
                         |> Option.map _.Delivery)
                        (Some DeliveryFacet.AtMostOnce)
                        "strict: the placement may LOSE the call rather than repeat it"

                    Expect.equal
                        (Facets.ofHandler (durableDiscipline true) PerformerFacets.none refreshHandler
                         |> Facets.narrowest
                         |> Option.map _.Delivery)
                        (Some DeliveryFacet.AtLeastOnce)
                        "accepting: it may DUPLICATE it — and neither policy may claim exactly-once"

                    Expect.isFalse
                        (allDisciplines
                         |> List.exists (fun d ->
                             Facets.ofHandler d PerformerFacets.none refreshHandler
                             |> Facets.narrowest
                             |> Option.map _.Delivery = Some DeliveryFacet.ExactlyOnceEffective))
                        "under NO configuration does an undeclared host call reach exactly-once"
                }

                test "a journal that dies with the process claims nothing" {
                    let services =
                        DurableServices.create
                        |> DurableServices.withJournal (Journal.inMemory ())
                        |> DurableServices.declaringPerformer "audit" IdempotencyFacet.Idempotent

                    Expect.equal
                        (Durable.guarantees services [ refreshHandler ] |> Facets.narrowest)
                        (Facets.ofHandler PlacementDiscipline.Direct services.Performers refreshHandler
                         |> Facets.narrowest)
                        "an in-memory journal derives the DIRECT interpreter's posture, exactly"
                } ]

          testList
              "the placement declaration, end to end"
              [ test "a derived declaration names the placement and the logic tree, and checks clean" {
                    let services =
                        DurableServices.create
                        |> DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ()))
                        |> DurableServices.declaringPerformer "audit" IdempotencyFacet.Idempotent

                    match Durable.declaration services logicTree [ refreshHandler ] with
                    | None -> failtest "the registration has an honest declaration and should have produced one"
                    | Some declaration ->
                        Expect.equal declaration.Placement PlacementId.durable "it names this placement"
                        Expect.equal declaration.LogicTree logicTree "and the logic tree it is about"

                        Expect.equal
                            declaration.Guarantees.Delivery
                            DeliveryFacet.ExactlyOnceEffective
                            "with the facet this phase exists to certify"

                        Expect.equal
                            (Durable.checkDeclaration services [ refreshHandler ] declaration)
                            []
                            "and a derived declaration is consistent with the registration it came from"

                        Expect.equal PlacementId.slot ProgramWire.logicTreeSlot "the slot id is the specification's own"
                }

                test "AN INFLATING DECLARATION GOES RED" {
                    // The negative test the acceptance names. Nothing else in
                    // this file would fail if `checkDeclaration` returned the
                    // empty list unconditionally.
                    let services =
                        DurableServices.create
                        |> DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ()))

                    let inflated =
                        { Placement = PlacementId.durable
                          LogicTree = logicTree
                          Guarantees =
                            { Delivery = DeliveryFacet.ExactlyOnceEffective
                              Idempotency = IdempotencyFacet.Idempotent
                              Restart = RestartVisibility.SurvivesRestart } }

                    let findings = Durable.checkDeclaration services [ refreshHandler ] inflated

                    Expect.contains
                        (findings |> List.map _.Code)
                        FacetCode.DeliveryInflated
                        "an undeclared host call cannot be exactly-once, and saying so is refused"

                    Expect.contains
                        (findings |> List.map _.Code)
                        FacetCode.IdempotencyInflated
                        "…nor intrinsically idempotent"

                    Expect.contains
                        (findings |> List.map _.Code)
                        FacetCode.RestartInflated
                        "…nor durable across a restart"

                    Expect.contains
                        (findings |> List.map _.Code)
                        FacetCode.UndeclaredPerformer
                        "and the report says WHY: the host declared nothing about the performer"

                    Expect.isTrue
                        (findings
                         |> List.filter Facets.isInflation
                         |> List.forall (fun f -> not (f.Detail.Contains "refreshed")))
                        "no finding echoes a handler's payload"
                }

                test "a declaration that promises LESS raises nothing" {
                    let services =
                        DurableServices.create
                        |> DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ()))
                        |> DurableServices.declaringPerformer "audit" IdempotencyFacet.Idempotent

                    let conservative =
                        { Placement = PlacementId.durable
                          LogicTree = logicTree
                          Guarantees =
                            { Delivery = DeliveryFacet.AtMostOnce
                              Idempotency = IdempotencyFacet.NonIdempotent
                              Restart = RestartVisibility.LostOnRestart } }

                    Expect.equal
                        (Durable.checkDeclaration services [ refreshHandler ] conservative
                         |> List.filter Facets.isInflation)
                        []
                        "promising less than you can keep costs only the promise"
                }

                test "a placement id this package does not serve is reported, not guessed" {
                    let services = DurableServices.create

                    let foreign =
                        { Placement = "somebody.else/engine"
                          LogicTree = logicTree
                          Guarantees =
                            { Delivery = DeliveryFacet.AtMostOnce
                              Idempotency = IdempotencyFacet.NonIdempotent
                              Restart = RestartVisibility.LostOnRestart } }

                    Expect.contains
                        (Durable.checkDeclaration services [ refreshHandler ] foreign |> List.map _.Code)
                        FacetCode.UnknownPlacement
                        "an id nobody here serves is a finding"

                    Expect.isNone (PlacementId.disciplineOf "somebody.else/engine") "and resolves to no discipline"
                }

                test "the mirrored vocabulary round-trips through its tags" {
                    // The tags ARE the cross-boundary agreement, so they are what
                    // a suite has to pin: a mirror nobody checks is a copy.
                    for facet in DeliveryFacet.all do
                        Expect.equal (DeliveryFacet.ofTag (DeliveryFacet.tag facet)) (Ok facet) "delivery"

                    for facet in IdempotencyFacet.all do
                        Expect.equal (IdempotencyFacet.ofTag (IdempotencyFacet.tag facet)) (Ok facet) "idempotency"

                    for facet in RestartVisibility.all do
                        Expect.equal (RestartVisibility.ofTag (RestartVisibility.tag facet)) (Ok facet) "restart"

                    Expect.equal
                        (DeliveryFacet.all |> List.map DeliveryFacet.tag)
                        [ "atMostOnce"; "atLeastOnce"; "exactlyOnceEffective" ]
                        "and the spellings are the agreed ones, pinned literally"
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
                } ] ]
