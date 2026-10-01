namespace Fuaran.Program.Server

open Fuaran.Program.Bounded

// ============================================================================
//  A handler, as data — and the fold that runs one.
//
//  A handler is the decomposition every host already performs by hand
//  (validate → read → compute → mutate → respond), written down as a STAGE LIST
//  rather than as a function. Two stage kinds, and the split is the whole
//  design:
//
//    Compute a   the bounded algebra — interpreted by the SHARED fold, the same
//                one the browser placement and the server driver run. Not a
//                copy, not a variant. Since the handler arm moved into the fold
//                (DECISIONS.md D7) this package matches on an `Action`
//                NOWHERE AT ALL — the one place an action is INTERPRETED is the
//                shared fold, which is D1's "no second evaluator" as something a
//                grep settles rather than a claim to trust.
//
//    Effect e    one arm of this placement's closed effect vocabulary, run
//                through the default-deny gate beside it.
//
//  Sequencing is the core algebra's, not a new construct: a stage list is read
//  in order exactly as `Action.Chain` folds in order. What the list adds is the
//  ability to interleave the two vocabularies, which is precisely what a
//  handler is for and precisely what the client placement has no need of.
//
//  ── Where a handler comes from ──────────────────────────────────────────────
//  The HOST registers it. A generated tree can only NAME a handler, through an
//  `Action.Call` endpoint; it cannot carry one. That is deliberate at this
//  stage: it means every host function, pipeline and op sequence in this file
//  is host-authored data, so the capability envelope of a session is fixed
//  before any untrusted tree arrives. Giving a handler a wire form is a later,
//  separate act — see `docs/server-handler-atomicity.md`.
//
//  ── Atomicity, in two phases (DECISIONS.md D8) ──────────────────────────────
//  The HANDLER is the unit, not the stage. Stages thread a value; nothing is
//  committed until the last one succeeds; a denial or a failure discards the
//  accumulated store, the ops, the effects and the notifications, keeping only
//  the diagnostics that say why. A half-applied handler is therefore
//  unrepresentable in the returned value.
//
//  A run has two phases, and the split is what extends that guarantee past the
//  state this placement owns:
//
//    PLAN     every stage runs in order. A query evaluates, a compute folds, an
//             op applies to the in-memory tree, a patch and a notification
//             accumulate — and a `HostCall` is GATED, LOOKED UP, and its landing
//             slot checked, then STAGED rather than invoked. Everything this
//             phase does is either a read or a value the caller can discard.
//
//    PERFORM  reached only when the plan completed. The staged host calls are
//             invoked in declaration order and their results land.
//
//  So a domain failure — a denial, a bad op, an unresolvable source, a reserved
//  landing slot — happens BEFORE anything external runs, which is the property
//  the note's third option was chosen for. The price is stated rather than
//  hidden: a later stage cannot read an earlier host call's result, because at
//  planning time there is no result to read.
//
//  What remains, and is REPORTED rather than pretended away: a performer that
//  fails in the perform phase leaves its predecessors run. The outcome then
//  carries `Committed = false` with the store rolled back, and `Performed`
//  naming exactly the host calls that did happen — the one case where an
//  uncommitted handler reports having performed anything at all.
//
//  ── Two amendments from the second witness (Phase 1967) ────────────────────
//  A domain whose handler is a store-mutating VERB rather than a UI event
//  handler ran under this fold unchanged and found two things it had to build
//  beside it (DECISIONS.md D19):
//
//    A GUARD THAT HALTS. A `Compute` stage whose action meets the shared fold's
//    `Require` shape and does not hold comes back `Halted`, and the handler
//    treats it exactly as an effect that failed: the halt stops every later
//    stage, nothing reaches the perform phase, and the outcome rolls back with
//    the fold's own refusal diagnostic saying why. A leaf's refusal is still a
//    diagnostic the handler carries on past — the UI tier's refusals are all of
//    that kind.
//
//    OPS THAT ARE PERFORMED. Under `OpPerformance.InMemory` — the default, and
//    the UI tier — `ApplyOps` is what it always was: applied while planning,
//    and that apply is the effect. Under `OpPerformance.Performed`, the apply
//    is a PLAN: the tree moves so later stages read it, and each op is STAGED
//    as a call of its own, performed in the perform phase in plan order beside
//    the host calls. D8's law then covers the ops too, with no new vocabulary:
//    `Performed` names `ApplyOps` once per op that ran, a part-way failure is a
//    `PerformFailed` under that capability with the prefix that ran reported,
//    and `proofs/Staging.fst` proves it over the same staged list.
// ============================================================================

/// The state a handler runs against. Two channels, deliberately named apart:
/// `Tree` is DOMAIN state, mutated only by `ServerEffect.ApplyOps`; `Bindings`
/// is the session's binding store, written by the shared fold's `SetState` and
/// by the query/host-call landing slots. A reader who conflates them will
/// eventually claim the wrong thing about durability.
type ServerStore<'Node, 'Store> = { Tree: 'Node; Bindings: 'Store }

/// One stage of a handler.
type HandlerStage<'Action, 'Op> =
    /// Bounded algebra, run by the SHARED interpreter against `Bindings`.
    | Compute of 'Action
    /// One server effect, run through the gate.
    | Effect of ServerEffect<'Op>

/// A named, host-registered handler: an ordered stage list and nothing else.
/// No closure, no host reference, no captured state — a handler is data, which
/// is what lets it be inspected, diffed and (later) given a wire form.
type Handler<'Action, 'Op> =
    { Name: string
      Stages: HandlerStage<'Action, 'Op> list }

/// What a handler run produced, at the level a host cares about. Payload-free
/// where it is a record of a refusal, payload-bearing where it is work the host
/// must actually perform.
[<RequireQualifiedAccess>]
type ServerDiagnostic =
    /// A diagnostic from the shared interpreter, passed through unchanged so a
    /// `Compute` stage reports exactly what the same action reports at the other
    /// placements.
    | Bounded of BoundedDiagnostic
    /// An effect was refused at the gate or had no performer.
    | Denied of ServerEffectDenial
    /// An effect ran and failed. `reason` is log-safe: for a `HostCall` it is
    /// the host performer's own text, and for an engine failure it is the error
    /// DISCRIMINATOR only. The engine's full message quotes names taken from the
    /// pipeline or the op — handler-declared today, wire-carried tomorrow — and
    /// the moment that changes, a verbatim message becomes a payload leak. The
    /// cost is a thinner error, and it is recorded as an open question rather
    /// than pretended away.
    | Failed of capability: string * reason: string
    /// A STAGED host call failed in the perform phase (D8). Distinct from
    /// `Failed` because the two carry opposite news about the outside world: a
    /// `Failed` happened while planning, so nothing external ran; a
    /// `PerformFailed` happened after planning succeeded, so every host call
    /// declared before it DID run and cannot be taken back. `reason` is the host
    /// performer's own text, which is safe to surface verbatim.
    | PerformFailed of capability: string * reason: string
    /// The tree's `Action.Call` named an endpoint no handler is registered
    /// under.
    ///
    /// **It deliberately does not say which.** That string is the one value in
    /// this subsystem that comes off the wire, so echoing it into a host's logs
    /// would be exactly the leak the effect denials avoid — the same rule that
    /// keeps a refused `Navigate` from logging its route. A host debugging an
    /// emission reads its own registry instead, which is a closed list it
    /// already has.
    | HandlerUnregistered

/// The result of running one handler.
type HandlerOutcome<'Node, 'Store, 'Op, 'Effect> =
    {
        /// The store after the handler, or the store it started from when the
        /// handler did not commit.
        Store: ServerStore<'Node, 'Store>
        /// Whether the handler ran to completion. `false` means every value
        /// below is the entry state and the handler's declared work did not
        /// happen — except for the host calls `Performed` names, which is the
        /// whole residual two-phase staging leaves (D8).
        Committed: bool
        /// The capabilities performed, in EXECUTION order — the audit trail of
        /// what the handler actually did.
        ///
        /// Execution order is not stage order for a `HostCall`: staging defers
        /// every host call to the perform phase, so they appear after the
        /// capabilities the plan phase ran, however early they were declared.
        /// That is the honest reading — this list says what happened and when,
        /// not what the stage list said.
        ///
        /// On an uncommitted outcome it is empty, EXCEPT when the perform phase
        /// itself failed, where it names the host calls that ran before the
        /// failure and were not rolled back.
        Performed: string list
        /// Ops the handler asked to be shipped to the client, in order.
        /// Distinct from anything applied to the domain tree.
        Patches: 'Op list
        /// Host-channel messages the handler asked for, in order.
        Notifications: (string * Fuaran.Core.JVal) list
        /// Closure-free client effects the shared interpreter emitted from a
        /// `Compute` stage — the same values, from the same fold, as at the
        /// other placements.
        ClientEffects: 'Effect list
        Diagnostics: ServerDiagnostic list
    }

/// What the handlers invoked during ONE event contributed — the value this
/// placement threads through the shared fold as its `HandlerArm` placement
/// state (DECISIONS.md D7).
///
/// It exists because the fold owns the binding store and the client effects and
/// nothing else: the domain tree, the audit trail, the patches, the
/// notifications and the server diagnostics are this placement's business, and
/// the fold must be able to carry them without knowing what any of them are.
/// An event that reaches no call action produces the tally it started with.
type HandlerTally<'Node, 'Op> =
    {
        /// The domain tree as the last committed handler left it.
        Tree: 'Node
        /// `false` once ANY handler this event invoked failed to commit. A
        /// handler is the atomicity unit, so one that halted rolled ITSELF back
        /// and the rest of the fold carried on — this flag is how the event says
        /// that happened rather than implying the whole event was refused.
        Committed: bool
        Performed: string list
        Patches: 'Op list
        Notifications: (string * Fuaran.Core.JVal) list
        Diagnostics: ServerDiagnostic list
    }

module HandlerTally =

    /// The tally an event starts from: this tree, nothing performed, committed
    /// until proven otherwise.
    let start (tree: 'Node) : HandlerTally<'Node, 'Op> =
        { Tree = tree
          Committed = true
          Performed = []
          Patches = []
          Notifications = []
          Diagnostics = [] }

module Handler =

    /// A host call the plan phase admitted and the perform phase will invoke
    /// (D8). Everything a call needs is captured here, so the perform phase
    /// decides nothing: the gate has already said yes, the performer has already
    /// been found, and the landing slot has already been checked. All that is
    /// left is the one irreversible act.
    type private StagedCall =
        { Capability: string
          Performer: Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>
          Args: Fuaran.Core.JVal
          Into: string option }

    /// The state threaded through the stage fold. Lists accumulate reversed and
    /// are flipped once at the end, so a long handler does not quadratically
    /// re-append.
    type private Accumulator<'Node, 'Store, 'Op, 'Effect> =
        {
            Store: ServerStore<'Node, 'Store>
            Halted: bool
            Performed: string list
            /// The capabilities the PERFORM phase ran. Kept apart from `Performed`
            /// because they are the only ones that survive a rollback, and a
            /// single list would make "what actually happened" a question about
            /// string prefixes.
            Externally: string list
            Staged: StagedCall list
            Patches: 'Op list
            Notifications: (string * Fuaran.Core.JVal) list
            ClientEffects: 'Effect list
            Diagnostics: ServerDiagnostic list
        }

    /// The discriminator of a pipeline-evaluation failure. Deliberately not the
    /// full error: see `ServerDiagnostic.Failed`.
    let private evalErrorKind (error: Fuaran.Core.EvalError) : string =
        match error with
        | Fuaran.Core.UnknownColumn _ -> "UnknownColumn"
        | Fuaran.Core.TypeError _ -> "TypeError"
        | Fuaran.Core.AggError _ -> "AggError"
        | Fuaran.Core.JoinError _ -> "JoinError"
        | Fuaran.Core.ArityError _ -> "ArityError"
        | Fuaran.Core.UnresolvedSource _ -> "UnresolvedSource"
        | Fuaran.Core.OverflowError _ -> "OverflowError"
        | Fuaran.Core.UnboundParam _ -> "UnboundParam"
        // The evaluator's clock read with no pinned evaluation instant
        // (Core-Compute 0.34.0).
        | Fuaran.Core.UnpinnedClock _ -> "UnpinnedClock"

    let private halt
        (capability: string)
        (reason: string)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect> =
        { acc with
            Halted = true
            Diagnostics = ServerDiagnostic.Failed(capability, reason) :: acc.Diagnostics }

    let private deny
        (denial: ServerEffectDenial)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect> =
        { acc with
            Halted = true
            Diagnostics = ServerDiagnostic.Denied denial :: acc.Diagnostics }

    /// A staged op's performer: the registered op performer closed over the op,
    /// in the shape a staged host call carries, so the perform phase runs ops
    /// and host calls through ONE loop. The argument is inert (an op lands
    /// nothing and takes no declarative payload) and the empty object is the
    /// honest spelling of that, since the wire value has no null.
    let private stagedOp
        (perform: 'Op -> Result<Fuaran.Core.JVal, string>)
        (capability: string)
        (op: 'Op)
        : StagedCall =
        { Capability = capability
          Performer = fun _ -> perform op
          Args = Fuaran.Core.JObj []
          Into = None }

    /// PLAN one effect against the store. The gate is consulted FIRST — before a
    /// pipeline is evaluated, before an op reaches the apply engine, and before
    /// a performer is even looked up as a callable — so no side effect of any
    /// kind can precede the policy decision.
    ///
    /// **The gate is TWO decisions in that one position, and both precede
    /// everything.** The capability is decided first, by name; then, only if it
    /// was admitted, the effect's ARGUMENTS are decided against the policy the
    /// host declared for that capability. The order matters in one direction
    /// only — a refused capability never has its arguments examined, so a host
    /// learns nothing about a call it was never going to make — and the
    /// composition matters in the other: a permitted capability reaching an
    /// endpoint nobody permitted is the confused deputy, and a gate that decided
    /// on the name alone could not see it.
    ///
    /// Four of the five arms complete here, and can, because none of them
    /// commits anything outside the returned value: a query READS, an op edits
    /// an in-memory tree, and a patch and a notification are values the host
    /// performs after the handler returns. The fifth — `HostCall` — is the only
    /// arm that reaches outside, so it is the only one staged (D8).
    let private runEffect
        (witness: ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (registry: ServerEffectRegistry)
        (performance: OpPerformance<'Op>)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (effect: ServerEffect<'Op>)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect> =
        let capability = ServerEffect.capability effect

        if not (registry.Gate capability) then
            let denial = ServerEffectDenial.GateRefused capability
            registry.OnDenied denial
            deny denial acc
        else
            match ServerArgumentPolicy.check witness.Op registry effect with
            // Reported as a HALT rather than as a denial, and the choice is
            // deliberate. The denial vocabulary is a closed, wire-specified pair
            // — "this host has no such capability" and "this host's gate refused
            // it" — and neither is true here: the capability exists and the gate
            // admitted it. What refused is a bound the host declared about the
            // arguments, which is what the halt names, through the same
            // capability-plus-reason shape the landing-slot refusal below
            // already uses for the same class of check. Nothing is `Performed`,
            // and `Halted` stops every later stage, so the handler reaches no
            // performer and commits nothing.
            | Error defect -> halt capability (ServerArgumentPolicy.describe defect) acc
            | Ok() ->
                let performed =
                    { acc with
                        Performed = capability :: acc.Performed }

                match effect with
                | ServerEffect.RunQuery(name, source, pipeline) ->
                    let evaluated =
                        Fuaran.Core.DataFrame.evalSource resolve source
                        |> Result.bind (Fuaran.Core.DataFrame.evalPipelineWith resolve pipeline)

                    match evaluated with
                    | Error err -> halt capability (evalErrorKind err) acc
                    | Ok table ->
                        { performed with
                            Store =
                                { performed.Store with
                                    Bindings = witness.Store.LandQuery name table performed.Store.Bindings } }

                | ServerEffect.ApplyOps ops ->
                    // The only domain-state mutation. Folded with short-circuit: an
                    // op that fails leaves the whole handler uncommitted rather than
                    // applying its predecessors, which is what makes the atomicity
                    // claim above true of the tree and not merely of the store.
                    let applied =
                        ops
                        |> List.fold
                            (fun state op -> state |> Result.bind (witness.Op.Stream.Apply op))
                            (Ok performed.Store.Tree)

                    match applied with
                    | Error code -> halt capability code acc
                    | Ok tree ->
                        match performance with
                        // In memory: the apply IS the effect, and it is performed
                        // here, in the plan phase — the shape every placement had
                        // before Phase 1967, and the UI tier's still.
                        | OpPerformance.InMemory ->
                            { performed with
                                Store = { performed.Store with Tree = tree } }
                        // Performed: the apply is a PLAN. The tree moves — a later
                        // stage reads the planned tree — but the capability is not
                        // recorded as performed; the staged calls are, one per op,
                        // when the perform phase runs them. `Performed` is
                        // deliberately NOT extended here, on the host-call arm's
                        // terms: it is the audit trail of what happened, and at
                        // this point nothing has.
                        | OpPerformance.Performed perform ->
                            let performOne op =
                                perform op |> Result.map (fun () -> Fuaran.Core.JObj [])

                            { acc with
                                Store = { acc.Store with Tree = tree }
                                Staged =
                                    ops
                                    |> List.fold
                                        (fun staged op -> stagedOp performOne capability op :: staged)
                                        acc.Staged }

                | ServerEffect.HostCall(fn, args, into) ->
                    match Map.tryFind fn registry.HostFunctions with
                    | None ->
                        let denial = ServerEffectDenial.Unregistered capability
                        registry.OnDenied denial
                        deny denial acc
                    | Some performer ->
                        // The host-reserved namespace is closed here for the same
                        // reason the shared interpreter closes it: a landing slot is
                        // a write, and a write into the host's own namespace is the
                        // case the namespace exists for. Checked while PLANNING —
                        // the slot is declared, so nothing about the check needs the
                        // performer to have run, and refusing here means a handler
                        // with a bad slot never reaches the outside world at all.
                        match into with
                        | Some key when witness.Store.IsReserved key ->
                            halt
                                capability
                                (sprintf
                                    "landing slot is under the host-reserved '%s' namespace"
                                    witness.Store.ReservedPrefix)
                                acc
                        | _ ->
                            // Admitted, not performed. `Performed` is deliberately
                            // NOT extended here: it is the audit trail of what
                            // happened, and at this point nothing has.
                            { acc with
                                Staged =
                                    { Capability = capability
                                      Performer = performer
                                      Args = args
                                      Into = into }
                                    :: acc.Staged }

                | ServerEffect.EmitPatch ops ->
                    { performed with
                        Patches = List.rev ops @ performed.Patches }

                | ServerEffect.Notify(channel, payload) ->
                    { performed with
                        Notifications = (channel, payload) :: performed.Notifications }

    /// Run one stage.
    let private runStage
        (witness: ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (registry: ServerEffectRegistry)
        (performance: OpPerformance<'Op>)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (nodeId: string)
        (stage: HandlerStage<'Action, 'Op>)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect> =
        match stage with
        | Compute action ->
            // THE shared fold, with the INERT arm — deliberately, and this is
            // the one boundary D7 draws.
            //
            // A handler's stages are host-registered data whose capability
            // envelope is fixed before any untrusted tree arrives, so handler
            // composition is a host act (register the stages you want), not
            // something a call action buried in a stage should smuggle in. It is
            // also what keeps the domain TOTAL (D2): were a stage's call action
            // to re-enter the registry, a handler naming itself would not
            // terminate, and totality would rest on a budget rather than on the
            // shape of the thing. A call action in a handler stage is therefore
            // the documented no-op, exactly as at a placement with no registry.
            let outcome = BoundedActions.runInert witness nodeId action acc.Store.Bindings

            // A HALTED fold halts the handler (Phase 1967): the guard's own
            // refusal diagnostic is the record of why, carried through
            // `Bounded` exactly as every other fold diagnostic is, and the
            // flag is what stops every later stage and rolls the handler
            // back. The fold's store as of the halt is threaded in and then
            // discarded by the rollback, which is the fold's "store as left"
            // meeting D8's "nothing happened".
            { acc with
                Store =
                    { acc.Store with
                        Bindings = outcome.Store }
                Halted = acc.Halted || outcome.Halted
                ClientEffects = List.rev outcome.Effects @ acc.ClientEffects
                Diagnostics =
                    (outcome.Diagnostics |> List.rev |> List.map ServerDiagnostic.Bounded)
                    @ acc.Diagnostics }
        | Effect effect -> runEffect witness registry performance resolve effect acc

    /// PERFORM the staged host calls, in declaration order, stopping at the
    /// first failure (D8). This is the only code in the handler that reaches
    /// outside, and it runs only after the plan phase completed — so a handler
    /// that was going to fail on its own terms has already failed, silently and
    /// for free, before any of this.
    let rec private perform
        (witness: ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (staged: StagedCall list)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect> =
        match staged with
        | [] -> acc
        | call :: rest ->
            match call.Performer call.Args with
            | Error reason ->
                { acc with
                    Halted = true
                    Diagnostics = ServerDiagnostic.PerformFailed(call.Capability, reason) :: acc.Diagnostics }
            | Ok result ->
                let recorded =
                    { acc with
                        Externally = call.Capability :: acc.Externally }

                let landed =
                    match call.Into with
                    | None -> recorded
                    | Some key ->
                        { recorded with
                            Store =
                                { recorded.Store with
                                    Bindings = witness.Store.Assign key result recorded.Store.Bindings } }

                perform witness rest landed

    /// Run a handler's stages in order against `store`, committing only if every
    /// stage planned and every staged call — host call or, under a registered
    /// op performer, op — then performed. `nodeId` is the originating event's
    /// node, threaded into the shared interpreter exactly as the other
    /// placements thread it. `performance` says how this placement performs an
    /// op (Phase 1967); `run` below is this at `OpPerformance.InMemory`.
    let runWith
        (witness: ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (registry: ServerEffectRegistry)
        (performance: OpPerformance<'Op>)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (nodeId: string)
        (handler: Handler<'Action, 'Op>)
        (store: ServerStore<'Node, 'Store>)
        : HandlerOutcome<'Node, 'Store, 'Op, 'Effect> =
        let start =
            { Store = store
              Halted = false
              Performed = []
              Externally = []
              Staged = []
              Patches = []
              Notifications = []
              ClientEffects = []
              Diagnostics = [] }

        let planned =
            handler.Stages
            |> List.fold
                (fun acc stage ->
                    if acc.Halted then
                        acc
                    else
                        runStage witness registry performance resolve nodeId stage acc)
                start

        // The phase boundary. Nothing external has run above this line, and
        // nothing below it can be undone — which is the entire content of the
        // atomicity decision, in one `if`.
        let final =
            if planned.Halted then
                planned
            else
                perform witness (List.rev planned.Staged) planned

        if final.Halted then
            // Roll back to the entry state. The diagnostics survive: they are
            // the entire record of why nothing happened, and discarding them
            // would turn a refusal into a silence.
            //
            // `Performed` is NOT emptied: a perform-phase failure leaves its
            // predecessors run, and reporting `[]` there would be the one lie
            // this design exists to avoid. A plan-phase halt leaves it empty on
            // its own, because nothing ever reached the perform phase.
            { Store = store
              Committed = false
              Performed = List.rev final.Externally
              Patches = []
              Notifications = []
              ClientEffects = []
              Diagnostics = List.rev final.Diagnostics }
        else
            { Store = final.Store
              Committed = true
              // Plan-phase capabilities in stage order, then the host calls the
              // perform phase ran — execution order, which is what an audit
              // trail is for.
              Performed = List.rev final.Performed @ List.rev final.Externally
              Patches = List.rev final.Patches
              Notifications = List.rev final.Notifications
              ClientEffects = List.rev final.ClientEffects
              Diagnostics = List.rev final.Diagnostics }

    /// `runWith` at `OpPerformance.InMemory`: ops are performed by being
    /// applied, which is every placement before Phase 1967 and the UI tier
    /// still. A placement whose ops reach the world registers a performer and
    /// calls `runWith`.
    let run
        (witness: ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (registry: ServerEffectRegistry)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (nodeId: string)
        (handler: Handler<'Action, 'Op>)
        (store: ServerStore<'Node, 'Store>)
        : HandlerOutcome<'Node, 'Store, 'Op, 'Effect> =
        runWith witness registry OpPerformance.InMemory resolve nodeId handler store
