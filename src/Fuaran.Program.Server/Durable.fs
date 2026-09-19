namespace Fuaran.Program.Server

open Fuaran.Program.Bounded

// ============================================================================
//  The DURABLE-EXECUTION interpreter — the second interpreter of the SAME
//  server-placement algebra.
//
//  The claim the whole vertical rests on is "one algebra, several placements".
//  A second INTERPRETER is the sharper form of the same claim, and it is sharper
//  precisely because it is easy to fake: two interpreters that share a
//  vocabulary and nothing else prove nothing at all. So this one does not fork
//  the fold, and it does not fork the handler either.
//
//  ── What is second, and what is shared ──────────────────────────────────────
//  Shared, and not copied: the bounded fold (a `Compute` stage is interpreted by
//  the same `BoundedActions` every placement runs), the closed effect vocabulary
//  (`ServerEffect`), the default-deny gate, the two-phase staging that decides
//  what a handler commits (D8), and the stage fold itself — `Handler.run`. This
//  file CALLS that fold rather than reproducing it, which is why the parity
//  claim beside it is structural rather than a coincidence two implementations
//  are asked to maintain.
//
//  Second, and genuinely different: what a host call MEANS the second time the
//  same invocation runs. Under the direct interpreter it means "call the
//  performer". Under this one it means "read the journal; call the performer
//  only if the journal has nothing to say" — which is the durable-execution
//  contract, and the whole of what this file adds.
//
//  ── The contract, named; no engine, named ───────────────────────────────────
//  Deterministic replay plus effect journaling. Re-running an invocation from
//  its entry state recomputes every step it took, and the steps a re-run cannot
//  recompute — the ones whose answer came from outside — are served from the
//  journal instead of being repeated. Nothing here names a product, a protocol
//  or a hosted service, and nothing should: a host may satisfy the port in
//  `Journal.fs` with a table, a log file, or somebody else's workflow engine,
//  and this interpreter cannot tell which.
//
//  ── Why only the host call is journaled ─────────────────────────────────────
//  Because it is the only arm that reaches outside, and D8 already says so: a
//  query reads, an op edits an in-memory tree the caller may discard, and a
//  patch and a notification accumulate as values the caller performs after the
//  handler returns. None of the four can be "performed twice" by a re-run,
//  because none of them is performed by the handler at all — they are
//  RECOMPUTED, deterministically, from the entry state, which is what makes the
//  replay discipline worth having rather than an extra ledger to keep.
//
//  The consequence a reader should carry away is the one the facets state: this
//  interpreter's exactly-once claim is about what IT performs. What a caller
//  does with a returned notification is the caller's own delivery posture, and
//  the composition joins the two.
//
//  ── The step ordinal is DERIVED from the fold, not declared ─────────────────
//  A journal entry is addressed by the ordinal of the performer invocation
//  within the invocation — zero for the first host call the perform phase makes,
//  one for the next. That is well defined because the plan phase is
//  deterministic: the same handler against the same entry store stages the same
//  calls in the same order, so a replay reaches ordinal *n* holding the same
//  call the recorded run held there.
//
//  "Well defined because the plan phase is deterministic" is a premise, and a
//  premise a `RunQuery` can break — its rows come from outside and may have
//  moved, so a `Compute` stage branching on them could stage a different call
//  list. That is why every entry records its CAPABILITY and every replay checks
//  it: a replay that finds a different capability at an ordinal REFUSES rather
//  than serving one call's answer to another. Divergence is a real hazard of
//  every replay-based engine; what is not acceptable is discovering it by
//  delivering the wrong result.
// ============================================================================

module DurableCode =

    /// A step the journal shows as ATTEMPTED with no recorded result. The effect
    /// may have happened and may not, and nothing here can tell the difference —
    /// the effect and the record are not one transaction. Refused under the
    /// default policy.
    [<Literal>]
    let IndeterminateStep = "durable-indeterminate-step"

    /// The replay reached an ordinal holding a DIFFERENT capability from the one
    /// the recorded run held there. The recomputation diverged, so no recorded
    /// answer is safe to serve.
    [<Literal>]
    let ReplayDivergence = "durable-replay-divergence"

/// A step re-invoked despite the journal being unable to say whether it already
/// ran.
///
/// The host may be configured to accept that — and where it is, it MUST record
/// that it did. This record is that obligation as a value, so the recording is
/// something a caller RECEIVES rather than something it is trusted to remember:
/// an override that produced no record would be indistinguishable afterwards
/// from a step that had been safe all along. The same shape, for the same
/// reason, as the resume-replay override record beside it.
type DurableOverrideRecord =
    { Step: int
      Capability: string
      Idempotency: IdempotencyFacet }

/// What one durable run did, beyond the handler outcome itself.
///
/// The outcome is carried WHOLE rather than flattened into this record, because
/// it is the direct interpreter's own value and must stay comparable to one: the
/// parity claim is `Handler.run ... = (Durable.run ...).Outcome`, and a
/// reshaped outcome would make that claim unstateable.
type DurableOutcome =
    {
        /// The handler outcome — the same value, of the same type, the direct
        /// interpreter produces.
        Outcome: HandlerOutcome
        /// Ordinals SERVED from the journal: the performer was not invoked.
        /// This list is the certification's subject — a replay whose every
        /// recorded ordinal appears here performed no duplicate effect.
        Replayed: int list
        /// Ordinals whose performer this run actually invoked.
        Invoked: int list
        /// Ordinals refused because the journal could not decide whether they
        /// had already run.
        Indeterminate: int list
        /// Ordinals re-invoked under an explicit override of that refusal.
        Overrides: DurableOverrideRecord list
    }

/// The seams the durable interpreter adds to the direct one. Everything else it
/// needs — the gate, the performers, the data sources — it takes unchanged.
type DurableServices =
    {
        /// Where steps are recorded. Defaults to `Journal.none`, under which
        /// this interpreter degrades exactly to the direct one and the facets
        /// say so.
        Journal: EffectJournal
        /// What the host declares about its own performers. Read only where the
        /// journal cannot decide a step, and by the facet derivation.
        Performers: PerformerFacets
        /// Whether an INDETERMINATE step is re-invoked rather than refused.
        ///
        /// This is the one knob that decides which hazard the placement takes,
        /// and it cannot avoid both: re-invoking may duplicate, refusing may
        /// lose. It defaults to `false` for the reason every other default in
        /// this placement is closed — the permissive answer is the one a host
        /// should have to reach for.
        ReinvokeIndeterminate: bool
    }

module DurableServices =

    /// No journal, nothing declared, indeterminate steps refused.
    ///
    /// Deliberately a working configuration rather than an error: it is the
    /// durable interpreter with its durability switched off, it behaves exactly
    /// as the direct one, and `Durable.discipline` derives the direct one's
    /// facets for it. A host that wired nothing has promised nothing.
    let create: DurableServices =
        { Journal = Journal.none
          Performers = PerformerFacets.none
          ReinvokeIndeterminate = false }

    let withJournal (journal: EffectJournal) (services: DurableServices) : DurableServices =
        { services with Journal = journal }

    /// Declare what repeating a registered performer does.
    let declaringPerformer (fn: string) (facet: IdempotencyFacet) (services: DurableServices) : DurableServices =
        { services with
            Performers = PerformerFacets.declare fn facet services.Performers }

    /// **The named opt-in.** Re-invoke a step the journal cannot decide, taking
    /// the duplicate hazard rather than the loss. Naming it is the
    /// configuration, and it is what the override record attaches to.
    let acceptingIndeterminateReplay (services: DurableServices) : DurableServices =
        { services with
            ReinvokeIndeterminate = true }

module Durable =

    /// The discipline these services amount to — the value the facet derivation
    /// reads.
    ///
    /// The journal's own restart claim is carried through rather than assumed:
    /// a journal that dies with the process records nothing across the failure
    /// the guarantee is about.
    let discipline (services: DurableServices) : PlacementDiscipline =
        PlacementDiscipline.DeterministicReplay
            { JournalSurvivesRestart = services.Journal.SurvivesRestart
              ReinvokeIndeterminate = services.ReinvokeIndeterminate }

    /// **Run one handler under deterministic replay.**
    ///
    /// The plan phase, the gate, the staging and the rollback are `Handler.run`'s
    /// — unchanged, and reached through the same call every other caller makes.
    /// What this function supplies is a registry whose performers consult the
    /// journal first, which is the entire difference between the two
    /// interpreters and is exactly where it should be: at the one arm that
    /// commits outside.
    ///
    /// `invocation` identifies the run whose journal is being read and written.
    /// Two runs sharing an id are the SAME invocation — one crashed and one
    /// resuming it. Two runs with different ids are different invocations and
    /// share nothing, which is what a caller wants for two clicks of the same
    /// button.
    let run
        (services: DurableServices)
        (invocation: string)
        (registry: ServerEffectRegistry)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (nodeId: string)
        (handler: Handler)
        (store: ServerStore)
        : DurableOutcome =
        let recorded = services.Journal.Read invocation

        // The ordinal of the next performer invocation. Mutable because the
        // fold it is threaded through does not know this interpreter exists —
        // which is the price of not forking the fold, and a price worth paying:
        // the alternative is a second stage fold that has to be kept in step
        // with the first by hand.
        let mutable cursor = 0

        let replayed = ResizeArray<int>()
        let invoked = ResizeArray<int>()
        let indeterminate = ResizeArray<int>()
        let overrides = ResizeArray<DurableOverrideRecord>()

        let append (step: int) (capability: string) (phase: JournalPhase) =
            services.Journal.Append
                { Invocation = invocation
                  Step = step
                  Capability = capability
                  Phase = phase }

        /// Attempt, invoke, record. The order is the whole contract: the
        /// `Attempted` record is written BEFORE the performer, so a process that
        /// dies inside the performer leaves the step readable as indeterminate
        /// rather than as never-attempted.
        let invoke
            (step: int)
            (capability: string)
            (performer: Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>)
            (args: Fuaran.Core.JVal)
            : Result<Fuaran.Core.JVal, string> =
            append step capability JournalPhase.Attempted
            // Nothing below this line runs if the performer does not return.
            // That is not an omission — it is the crash this interpreter exists
            // to survive, and the journal is what it leaves behind.
            let result = performer args
            invoked.Add step

            append
                step
                capability
                (match result with
                 | Ok value -> JournalPhase.Completed value
                 | Error reason -> JournalPhase.Refused reason)

            result

        let wrap (fn: string) (performer: Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>) =
            fun (args: Fuaran.Core.JVal) ->
                let step = cursor
                cursor <- step + 1
                let capability = "host:" + fn

                match Journal.capabilityOf recorded step with
                | Some previous when previous <> capability ->
                    // The recomputation reached a different call here than the
                    // recorded run did. Serving the recorded answer would be
                    // delivering one call's result to another; refusing is the
                    // only safe reading, and the handler rolls back around it.
                    Error DurableCode.ReplayDivergence
                | _ ->
                    match Journal.stepOf recorded step with
                    | JournaledStep.Value value ->
                        replayed.Add step
                        Ok value
                    | JournaledStep.Refusal reason ->
                        replayed.Add step
                        Error reason
                    | JournaledStep.Unrun -> invoke step capability performer args
                    | JournaledStep.Indeterminate _ ->
                        let declared = PerformerFacets.facetOf fn services.Performers

                        if declared = IdempotencyFacet.Idempotent then
                            // Re-invoking costs nothing by the performer's own
                            // declared shape, so the window closes without a
                            // policy and without an override to record.
                            invoke step capability performer args
                        elif services.ReinvokeIndeterminate then
                            overrides.Add
                                { Step = step
                                  Capability = capability
                                  Idempotency = declared }

                            invoke step capability performer args
                        else
                            indeterminate.Add step
                            Error DurableCode.IndeterminateStep

        let journalling =
            { registry with
                HostFunctions = registry.HostFunctions |> Map.map wrap }

        let outcome = Handler.run journalling resolve nodeId handler store

        // An audit fact, not a short circuit. A completed invocation is
        // REPLAYED rather than skipped — the outcome of a handler is a tree and
        // a store, recomputed deterministically and never stored — so this
        // marker says only that a replay of this invocation will reach no
        // performer. Written once: the snapshot read at entry is what decides.
        if not (Journal.isComplete recorded) then
            append
                Journal.InvocationStep
                Journal.InvocationCapability
                (JournalPhase.Completed(Fuaran.Core.JStr(if outcome.Committed then "committed" else "uncommitted")))

        { Outcome = outcome
          Replayed = List.ofSeq replayed
          Invoked = List.ofSeq invoked
          Indeterminate = List.ofSeq indeterminate
          Overrides = List.ofSeq overrides }

    /// This interpreter's answer to a call action the shared fold recognised —
    /// the direct placement's arm, with the durable interpreter behind it.
    ///
    /// One EVENT may reach several call actions, and each is its own invocation:
    /// they are numbered in fold order under the event's id, which is
    /// deterministic for the same reason the step ordinals are. A caller
    /// resuming an event therefore resumes each of its handlers against its own
    /// journal, rather than against a shared one where the second handler's
    /// steps would be read as the first's.
    let arm (services: DurableServices) (invocation: string) (host: ServerServices) : HandlerArm<HandlerTally> =
        let mutable index = 0

        { Answer =
            fun nodeId endpoint bindings tally ->
                match Map.tryFind endpoint host.Handlers with
                | None ->
                    Some
                        { Store = bindings
                          Effects = []
                          Diagnostics = []
                          Placement =
                            { tally with
                                Diagnostics = tally.Diagnostics @ [ ServerDiagnostic.HandlerUnregistered ] } }
                | Some handler ->
                    let ordinal = index
                    index <- ordinal + 1

                    let durable =
                        run
                            services
                            (sprintf "%s/%d" invocation ordinal)
                            host.Effects
                            host.Sources
                            nodeId
                            handler
                            { Tree = tally.Tree
                              Bindings = bindings }

                    let outcome = durable.Outcome

                    Some
                        { Store = outcome.Store.Bindings
                          Effects = outcome.ClientEffects
                          Diagnostics = []
                          Placement =
                            { Tree = outcome.Store.Tree
                              Committed = tally.Committed && outcome.Committed
                              Performed = tally.Performed @ outcome.Performed
                              Patches = tally.Patches @ outcome.Patches
                              Notifications = tally.Notifications @ outcome.Notifications
                              Diagnostics = tally.Diagnostics @ outcome.Diagnostics } } }

    /// Step a server session with this interpreter behind its call actions.
    ///
    /// The loop is `ServerSession.step`'s — the same validate, the same budget,
    /// the same fold, the same re-resolution and diff. Only the arm differs,
    /// which is the shape the placement seam was cut for.
    let step
        (services: DurableServices)
        (invocation: string)
        (session: ServerSession)
        (ev: Fuaran.UI.ServerDriven.Validation.LiveEvent)
        : ServerSession * ServerStepOutput =
        ServerSession.stepWith (arm services invocation session.Services) session ev

    // ─── the placement declaration ───────────────────────────────────────────

    /// What this interpreter guarantees for a registration — derived, never
    /// authored. `None` where the derivation proves both delivery hazards and no
    /// facet says both.
    let guarantees (services: DurableServices) (handlers: Handler seq) : DerivedGuarantees =
        Facets.ofHandlers (discipline services) services.Performers handlers

    /// **The declaration a composition's logic-tree slot can carry** for this
    /// placement, derived from the registration it is about.
    let declaration
        (services: DurableServices)
        (logicTree: LogicTreeRef)
        (handlers: Handler seq)
        : PlacementDeclaration option =
        Facets.declare PlacementId.durable logicTree (discipline services) services.Performers handlers

    /// The end-to-end facet check for a declaration a composition already holds,
    /// against the registration this host actually runs.
    let checkDeclaration
        (services: DurableServices)
        (handlers: Handler seq)
        (declared: PlacementDeclaration)
        : FacetFinding list =
        Facets.checkDeclaration (discipline services) services.Performers handlers declared

// ============================================================================
//  The OPERATOR CONTROLS, wired to this interpreter.
//
//  The vocabulary and the fold are in `Journal.fs` beside the effect journal,
//  because they are a durability contract and not an interpreter. This section
//  is the other half: what a folded `ControlState` MEANS to a running session.
//
//  ── Nothing above this line changed ─────────────────────────────────────────
//  `Durable.run`, `Durable.arm` and `Durable.step` are untouched, and the
//  controls-aware forms below CALL them rather than reproducing them — the same
//  discipline D12 records for the two interpreters, one level down. A control
//  takes effect by wrapping the REGISTRY the run is handed, which is the one
//  place every effect already passes through, so there is no arm of the
//  vocabulary a control can be forgotten for.
//
//  ── Where each act lands ────────────────────────────────────────────────────
//  Three of the four land at the effect gate and are `Controls.apply`'s work.
//  The fourth — the suspend's "refuse every dispatch" — lands one level up, at
//  the session's own G1 gate, because a dispatch is refused before a handler is
//  reached and therefore before any registry is consulted. `stepControlled`
//  expresses it by closing `ServerServices.CanDispatch`, which is the gate the
//  loop already asks, so a suspended step is refused through the SAME path and
//  with the same `ServerReject.Gate` shape as any other policy refusal. No new
//  refusal type, and nothing for a host's existing rejection handling to learn.
//
//  A suspension refuses a DISPATCH. An event carrying no action dispatches
//  nothing, so it stays inert rather than being refused — that is the honest
//  reading of the word, and the alternative would have a suspended session
//  reporting refusals of things nobody asked it to do.
//
//  ── Why resume needs no resume ENGINE ───────────────────────────────────────
//  Because the suspend performed nothing. A refused dispatch leaves the session
//  value untouched, so the continuation after a `Resume` is the continuation the
//  uninterrupted session would have had — not reconstructed, the same value. The
//  case that DOES need replay is a suspend raised across a live invocation, and
//  that is the durable interpreter's existing job: the resumed run re-reads the
//  journal, serves every recorded step and re-invokes none of them. So the
//  controls add a decision and no second replay path, which is the whole reason
//  they were worth expressing as journal ops.
// ============================================================================

/// Where a session's control stream lives, and under which key.
///
/// A record rather than two parameters so a call site reads as a stream a host
/// wired rather than as a string somebody passed, and so a later control-side
/// question extends it without changing every signature — the same shape, for
/// the same reason, as `ReplayPolicy`.
type ControlServices =
    {
        Journal: ControlJournal
        /// The SESSION this stream is keyed by. Not an invocation id: see the
        /// scope note in `Journal.fs`.
        Scope: string
    }

module ControlServices =

    /// No stream, so no control can ever be in force. The default, and the
    /// configuration under which every controls-aware function below is exactly
    /// its uncontrolled counterpart.
    let create (scope: string) : ControlServices =
        { Journal = Controls.none
          Scope = scope }

    let withJournal (journal: ControlJournal) (services: ControlServices) : ControlServices =
        { services with Journal = journal }

/// What one controlled run did, beyond the durable outcome itself.
///
/// The durable outcome is carried WHOLE rather than flattened, for the reason
/// `DurableOutcome` carries the handler outcome whole: the parity claim is
/// stated against the uncontrolled value, and a reshaped one would make it
/// unstateable.
type ControlledOutcome =
    {
        /// The durable outcome — the same value, of the same type, an
        /// uncontrolled run produces.
        Durable: DurableOutcome
        /// The state the stream folded to at entry. The prefix this run was
        /// decided by, so a reader can say which acts were in force without
        /// re-reading the stream and hoping it has not moved.
        Controls: ControlState
        /// Every refusal the controls caused, in order. Empty on a session with
        /// no control recorded, which is the whole cost of the feature there.
        Refusals: ControlRefusal list
    }

/// One controlled step of a server session.
type ControlledStep =
    { Session: ServerSession
      Output: ServerStepOutput
      Controls: ControlState
      Refusals: ControlRefusal list }

module DurableControls =

    /// **Run one handler under deterministic replay, with the operator controls
    /// in force.**
    ///
    /// Exactly `Durable.run` against a registry `Controls.apply` has wrapped.
    /// That is the whole implementation, and it is worth saying why it can be:
    /// every effect this placement performs passes the gate first (`Handler.run`
    /// consults it before a pipeline is evaluated, before an op reaches the
    /// apply engine and before a performer is looked up), so a control expressed
    /// at the registry reaches every arm of the closed vocabulary without this
    /// file enumerating them.
    let run
        (services: DurableServices)
        (controls: ControlServices)
        (invocation: string)
        (registry: ServerEffectRegistry)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (nodeId: string)
        (handler: Handler)
        (store: ServerStore)
        : ControlledOutcome =
        let state = Controls.stateOf controls.Journal controls.Scope
        let refusals = ResizeArray<ControlRefusal>()
        let controlled = Controls.apply refusals.Add state registry

        { Durable = Durable.run services invocation controlled resolve nodeId handler store
          Controls = state
          Refusals = List.ofSeq refusals }

    /// This interpreter's answer to a call action, with the controls in force.
    ///
    /// `record` receives every control refusal the handlers this arm answers
    /// caused. It is a sink rather than a return value because an arm is a
    /// closure the fold calls — the same shape, and for the same reason, as the
    /// registry's own `OnDenied`.
    let arm
        (services: DurableServices)
        (controls: ControlServices)
        (invocation: string)
        (record: ControlRefusal -> unit)
        (host: ServerServices)
        : HandlerArm<HandlerTally> =
        let state = Controls.stateOf controls.Journal controls.Scope

        Durable.arm
            services
            invocation
            { host with
                Effects = Controls.apply record state host.Effects }

    /// **Step a server session with the controls in force.**
    ///
    /// A suspended session refuses the dispatch through its own G1 gate and
    /// leaves the session value untouched — see the header. An unsuspended one
    /// runs the durable interpreter against a controlled registry.
    let step
        (services: DurableServices)
        (controls: ControlServices)
        (invocation: string)
        (session: ServerSession)
        (ev: Fuaran.UI.ServerDriven.Validation.LiveEvent)
        : ControlledStep =
        let state = Controls.stateOf controls.Journal controls.Scope
        let refusals = ResizeArray<ControlRefusal>()

        match state.Suspended with
        | Some(actor, reason) ->
            // The G1 gate, closed. The refusal travels the loop's own path, so
            // the session is carried forward unchanged by the same code that
            // carries it forward under any other policy refusal — and the
            // session RETURNED is the caller's own, so a suspension leaves no
            // trace on the value a resume continues from.
            let closed =
                { session with
                    Services =
                        { session.Services with
                            CanDispatch = fun _ -> false } }

            let _, output =
                ServerSession.stepWith (Durable.arm services invocation closed.Services) closed ev

            { Session = session
              Output = output
              Controls = state
              Refusals =
                match output.Rejected with
                | Some _ -> [ ControlRefusal.Suspended(ControlCode.DispatchCapability, actor, reason) ]
                | None -> [] }
        | None ->
            let next, output =
                ServerSession.stepWith (arm services controls invocation refusals.Add session.Services) session ev

            { Session = next
              Output = output
              Controls = state
              Refusals = List.ofSeq refusals }

    /// The controls in force on a session, without running anything.
    let stateOf (controls: ControlServices) : ControlState =
        Controls.stateOf controls.Journal controls.Scope

    /// Record one act on a session's stream, returning the entry as it landed.
    ///
    /// The one write path, so a raiser never has to know the stream's key twice.
    /// A machine raiser and an operator reach it identically — the difference is
    /// the `ControlActor` inside `request`, and nothing here reads it.
    let record (controls: ControlServices) (request: ControlRequest) : ControlEntry =
        Controls.record controls.Journal controls.Scope request

    /// Suspend a session ACROSS whatever indeterminate window is open on these
    /// invocations — the mid-stage case, with the window read from the effect
    /// journal rather than asserted by the raiser.
    ///
    /// A suspend raised between invocations records no window, and that is a
    /// statement rather than an omission: it says the journal was asked and
    /// answered nothing, which is exactly the fact a resume needs and cannot
    /// recover later.
    let suspendMidStage
        (services: DurableServices)
        (controls: ControlServices)
        (invocations: string seq)
        (actor: ControlActor)
        (reason: string)
        : ControlEntry =
        Controls.midStageWindows services.Journal invocations
        |> Controls.suspendMidStage actor reason
        |> record controls

    /// This host's server-tier coverage with the controls in force — what a
    /// demanded-effect check is asked once a performer has been withdrawn.
    let coverage (controls: ControlServices) (host: ServerServices) : ServerCoverage =
        Controls.coverage (stateOf controls) host.Effects
