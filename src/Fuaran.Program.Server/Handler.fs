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
//
//  ── Two more from the third witness (Phase 1974) ────────────────────────────
//  A document pipeline under a server placement — a domain whose state is its
//  tree — ran here too, and found both of the above on the wrong axis
//  (DECISIONS.md D20):
//
//    A GUARD OVER THE STATE. A compute stage's guard resolves against the
//    binding store, which cannot see the tree; a domain whose bound values
//    live in the state could not guard on them, and a handler that planned a
//    banned edit and then required a check passed it. The op channel now has
//    a guard shape (`OpView.Require`): resolved through the op's own apply
//    against the state as of its position in the plan, holding without
//    moving it, refusing with its own reason as the halt's, and never staged
//    or performed.
//
//    A PERFORMER HANDED THE STATE. `OpPerformance.Performed` now takes the
//    state as of each op beside the op, so a tail that persists what the plan
//    produced is handed it rather than folding the ops a second time itself.
//
//  And the handler runs under EVERY composition, filled axes or not: it reads
//  the STATE axis for ops, and the DISPATCH axis — through `IDispatchPosition`
//  — only for a compute stage and a landing slot. A composition with no
//  dispatch axis cannot hold a compute stage (its action type has no values),
//  and has no binding channel, so a landing slot is refused while planning
//  (`NoBindingChannel`).
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

/// One flow decision a handler's plan took (Phase 1982, the second witness's
/// B3): which arm a branch took, or how many times a repeat ran its body.
///
/// A placement that maps an outcome onto its own codes (a verb's arm onto an
/// exit code, say) used to have to read the branch conditions again to learn
/// which arm ran, which is sound only while no arm writes what a condition
/// reads. The outcome now says, and it is the audit record a reviewer of a
/// signed act wants beside `Performed`: not only what was done, but which way
/// the program went to do it.
[<RequireQualifiedAccess>]
type FlowDecision =
    /// A branch (`OpView.Choose` on the op axis, `ActionView.Choose` in a
    /// compute stage) decided: `true` took the true arm.
    | Chose of tookTrue: bool
    /// A repeat ran its body this many times.
    | Repeated of count: int
    /// A per-element iteration (Phase 1990) ran its body over this many
    /// elements — the ones that RAN, so a halted one reports fewer than its
    /// collection holds.
    | Iterated of count: int

module FlowDecision =
    /// The decisions a compute stage's fold took, read off the trace it
    /// recorded (`BoundedActions.runTraced`), in PRE-ORDER: a branch's or a
    /// repeat's own decision before the decisions inside it, which is the
    /// order the fold took them in. A repeat reports the iterations that RAN,
    /// so a halted repeat reports fewer than its bound.
    let rec ofTrace (trace: Trace) : FlowDecision list =
        match trace with
        | Trace.Nothing
        | Trace.Wrote _ -> []
        | Trace.Seq steps -> steps |> List.collect ofTrace
        | Trace.Choose(tookTrue, arm) -> FlowDecision.Chose tookTrue :: ofTrace arm
        | Trace.Repeat iterations ->
            FlowDecision.Repeated(List.length iterations)
            :: (iterations |> List.collect ofTrace)
        | Trace.Each elements
        | Trace.EachOf(_, elements) ->
            FlowDecision.Iterated(List.length elements)
            :: (elements |> List.collect ofTrace)

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
        /// The flow decisions the plan took (Phase 1982), in plan order and
        /// pre-order: each branch's arm and each repeat's count, from the op
        /// stages' `OpView.Choose` / `OpView.Repeat` and from the compute
        /// stages' folds alike. Empty for a handler that uses neither shape,
        /// which is every handler the UI tier registers.
        ///
        /// On an uncommitted outcome it names the decisions taken BEFORE the
        /// effect that halted, so the record of which way the program went
        /// survives the rollback exactly as `Diagnostics` does. An op effect
        /// whose plan was refused contributes none of its own, because its plan
        /// was refused whole.
        ///
        /// HOST-SIDE ONLY: `HandlerReport` does not carry it, so the outcome
        /// document the program wire specification describes is byte-identical
        /// (DECISIONS.md D25).
        Flow: FlowDecision list
    }

/// One step of the PLAN an undo reads (Phase 1977, DECISIONS.md D22),
/// recorded by the plan phase in plan order. The model's `step`
/// (`proofs/Staging.fst`).
[<RequireQualifiedAccess>]
type UndoStep<'Node, 'Op, 'Action> =
    /// An op the plan applied — in memory or staged for a performer — with
    /// the state it was applied TO. The pre-state is what an inverse is
    /// computed from, and the plan phase is the one place that holds it.
    | Edit of preState: 'Node * op: 'Op
    /// A compute stage's run, with the trace the fold recorded (Phase 1976):
    /// what undoes its binding writes is `BoundedActions.reverse` over the
    /// two, when the action is in the reversible fragment and the trace is
    /// restorable.
    | Compute of action: 'Action * trace: Trace
    /// A host call or a notification: it reached the world, under this
    /// capability, and no inverse vocabulary exists for it.
    | Reached of capability: string
    /// A patch, emitted for the host to apply after the handler returned:
    /// whether it can be undone is the host's to say, not this handler's.
    | Emitted of capability: string

/// The record a committed run leaves for its undo: the handler, whether it
/// committed, the state it was handed, and its steps in plan order. `Undo.run`
/// reads it; nothing else does.
type UndoPlan<'Node, 'Op, 'Action> =
    {
        Handler: string
        Committed: bool
        /// The entry state — the pre-state of the whole run, recorded so the
        /// undo of a plan of exact inverses can be checked against it before
        /// anything performs.
        Entry: 'Node
        Steps: UndoStep<'Node, 'Op, 'Action> list
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
        /// The flow decisions of every handler the event invoked (Phase 1982),
        /// each handler's in its own plan order, the handlers in invocation
        /// order.
        Flow: FlowDecision list
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
          Diagnostics = []
          Flow = [] }

module Handler =

    /// A host call the plan phase admitted and the perform phase will invoke
    /// (D8). Everything a call needs is captured here, so the perform phase
    /// decides nothing: the gate has already said yes, the performer has already
    /// been found, and the landing slot has already been checked. All that is
    /// left is the one irreversible act.
    ///
    /// Three arms stage calls: a host call, an op under a registered op
    /// performer, and a query under an evaluator its host could not declare a
    /// pure read (Phase 1905, D34). The answer says where it lands, and every
    /// slot it names was checked while planning.
    type private Landing =
        /// Nothing lands: an op stage's receipt, or a host call with no `into`.
        | Nowhere
        /// A host call's result, in its declared state slot.
        | StateSlot of key: string * value: Fuaran.Core.JVal
        /// A staged query's table, in its query slot.
        | QuerySlot of slot: string * table: Fuaran.Core.Table

    type private StagedCall =
        { Capability: string
          Perform: unit -> Result<Landing, string> }

    /// The state threaded through the stage fold. Lists accumulate reversed and
    /// are flipped once at the end, so a long handler does not quadratically
    /// re-append.
    type private Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action> =
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
            /// The undo trail (Phase 1977), reversed like every list here: the
            /// edits with their pre-states, the compute stages with their
            /// traces, and every step that reached or emitted. The model's
            /// `ac_trail`.
            Trail: UndoStep<'Node, 'Op, 'Action> list
            /// The flow decisions (Phase 1982), reversed like every list here.
            Flow: FlowDecision list
        }

    let private halt
        (capability: string)
        (reason: string)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action> =
        { acc with
            Halted = true
            Diagnostics = ServerDiagnostic.Failed(capability, reason) :: acc.Diagnostics }

    let private deny
        (denial: ServerEffectDenial)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action> =
        { acc with
            Halted = true
            Diagnostics = ServerDiagnostic.Denied denial :: acc.Diagnostics }

    /// The refusal a landing slot meets under a composition with no dispatch
    /// axis (Phase 1974): a `RunQuery` and a `HostCall` that declares an
    /// `into` both write the binding store, and such a composition has none.
    /// Refused while PLANNING, through the same halt a reserved slot is
    /// refused through, so a handler that would have needed one reaches
    /// nothing outside.
    [<Literal>]
    let NoBindingChannel = "no-binding-channel"

    /// A staged op's performer: the registered op performer closed over the
    /// state AS OF THE OP and the op (Phase 1974), in the shape a staged host
    /// call carries, so the perform phase runs ops and host calls through ONE
    /// loop. An op takes no declarative payload; the answer is the performer's
    /// RECEIPT (Phase 1981), already
    /// checked by the contract `OpPerformance.performedChecked` composed into
    /// `perform`, landing in no slot. The model's `staged_from`, with the token
    /// carrying the contract at this state and op (`op_contract_keyed`).
    let private stagedOp
        (perform: 'Node -> 'Op -> Result<Fuaran.Core.JVal, string>)
        (capability: string)
        (state: 'Node)
        (op: 'Op)
        : StagedCall =
        { Capability = capability
          Perform = fun () -> perform state op |> Result.map (fun _ -> Landing.Nowhere) }

    /// The `ApplyOps` arm's fold over its ops — the model's `plan_ops`
    /// (Phase 1974; `plan_views` since Phase 1976). Each op is VIEWED, then
    /// planned against the state the ops before it left, short-circuiting at
    /// the first refusal, whose text is the arm's halt reason verbatim.
    ///
    /// An op the state witness views as a GUARD (`OpView.Require`) is applied
    /// for its answer and holds without moving the state — the answer is
    /// discarded, so it cannot write — and is never staged:
    /// `guard_holds_moves_nothing`. Its refusal is the sequence's
    /// (`guard_refusal_halts`). An EDIT moves the state, and under a
    /// registered op performer is staged with the state it produced,
    /// prepended onto the reversed staged list exactly as a host call is, so
    /// the perform phase meets the ops in plan order; in memory, nothing is
    /// staged and the arm is the apply it always was. A BRANCH (Phase 1976)
    /// applies its entry condition for its answer — `Ok` takes the true arm,
    /// `Error` the false arm — plans that arm as a sequence, and then, when it
    /// carries an exit assertion, applies it against the state the arm left:
    /// it must hold after the true arm and fail after the false arm, or the
    /// whole effect is refused with the assertion named
    /// (`choose_plans_the_taken_arm`, `exit_violation_halts`). Neither
    /// condition is applied for its state, staged or performed. A REPEAT
    /// plans its body that many times, threaded as a sequence
    /// (`repeat_plans_as_unrolling`); a negative count is refused. A
    /// PER-ELEMENT ITERATION (Phase 1990) plans its body once per element of
    /// its literal collection, each op with that element substituted for the
    /// placeholder through the state witness's `Substitute`, threaded as a
    /// sequence (`each_plans_as_lowered`); an op sequence that reads a
    /// placeholder no enclosing `Each` binds is refused BEFORE its first op
    /// plans, with the placeholder named (`StateWitness.scopeDefects`).
    let private planOps
        (state: StateWitness<'Node, 'Op>)
        (performance: OpPerformance<'Node, 'Op>)
        (readExtent: ExtentReader)
        (capability: string)
        (ops: 'Op list)
        (tree: 'Node)
        (staged: StagedCall list)
        (trail: UndoStep<'Node, 'Op, 'Action> list)
        (flow: FlowDecision list)
        : Result<'Node * StagedCall list * UndoStep<'Node, 'Op, 'Action> list * FlowDecision list, string> =
        // The trail (Phase 1977) threads beside the staged list, reversed as
        // it is: every EDIT the plan applies is recorded with the state it was
        // applied to — in memory and performed alike, because an undo in
        // memory applies the inverses in memory. The model walks it a second
        // time over the same views (`trail_views`); `trail_agrees` is what
        // says one fold and two walks reach one answer.
        // The flow (Phase 1982) threads the same way, reversed: a branch's
        // decision is pushed before its arm is planned and a repeat's count
        // before its body, so the reversed list reads in pre-order.
        let rec go
            (ops: 'Op list)
            (tree: 'Node)
            (staged: StagedCall list)
            (trail: UndoStep<'Node, 'Op, 'Action> list)
            (flow: FlowDecision list)
            =
            match ops with
            | [] -> Ok(tree, staged, trail, flow)
            | op :: rest ->
                match one op tree staged trail flow with
                | Error code -> Error code
                | Ok(tree', staged', trail', flow') -> go rest tree' staged' trail' flow'

        and one
            (op: 'Op)
            (tree: 'Node)
            (staged: StagedCall list)
            (trail: UndoStep<'Node, 'Op, 'Action> list)
            (flow: FlowDecision list)
            =
            match state.View op with
            | OpView.Require ->
                match state.Stream.Apply op tree with
                | Error code -> Error code
                | Ok _ -> Ok(tree, staged, trail, flow)
            | OpView.Edit ->
                match state.Stream.Apply op tree with
                | Error code -> Error code
                | Ok tree' ->
                    let trail' = UndoStep.Edit(tree, op) :: trail

                    match performance with
                    | OpPerformance.InMemory -> Ok(tree', staged, trail', flow)
                    | OpPerformance.Performed perform ->
                        Ok(tree', stagedOp perform capability tree' op :: staged, trail', flow)
            | OpView.Choose(entry, whenTrue, whenFalse, exit) ->
                let tookTrue = Result.isOk (state.Stream.Apply entry tree)
                let arm = if tookTrue then whenTrue else whenFalse

                match go arm tree staged trail (FlowDecision.Chose tookTrue :: flow) with
                | Error code -> Error code
                | Ok(tree', staged', trail', flow') ->
                    match exit with
                    | None -> Ok(tree', staged', trail', flow')
                    | Some assertion ->
                        match state.Stream.Apply assertion tree' with
                        | Ok _ ->
                            if tookTrue then
                                Ok(tree', staged', trail', flow')
                            else
                                Error "the exit assertion held after the false arm"
                        | Error reason ->
                            if tookTrue then
                                Error(sprintf "the exit assertion did not hold after the true arm: %s" reason)
                            else
                                Ok(tree', staged', trail', flow')
            | OpView.Repeat(count, body) ->
                if count < 0 then
                    Error "the repeat's count is negative"
                else
                    let rec times
                        (remaining: int)
                        (tree: 'Node)
                        (staged: StagedCall list)
                        (trail: UndoStep<'Node, 'Op, 'Action> list)
                        (flow: FlowDecision list)
                        =
                        if remaining = 0 then
                            Ok(tree, staged, trail, flow)
                        else
                            match go body tree staged trail flow with
                            | Error code -> Error code
                            | Ok(tree', staged', trail', flow') -> times (remaining - 1) tree' staged' trail' flow'

                    times count tree staged trail (FlowDecision.Repeated count :: flow)
            // The lowered form, element by element: the body with that
            // element substituted, planned as a sequence from the state the
            // element before it left. The decision names the element count.
            | OpView.Each(collection, placeholder, body) ->
                let rec elements
                    (remaining: Fuaran.Core.JVal list)
                    (tree: 'Node)
                    (staged: StagedCall list)
                    (trail: UndoStep<'Node, 'Op, 'Action> list)
                    (flow: FlowDecision list)
                    =
                    match remaining with
                    | [] -> Ok(tree, staged, trail, flow)
                    | element :: rest ->
                        match go (body |> List.map (state.Substitute placeholder element)) tree staged trail flow with
                        | Error code -> Error code
                        | Ok(tree', staged', trail', flow') -> elements rest tree' staged' trail' flow'

                let over (extent: Fuaran.Core.JVal list) =
                    elements extent tree staged trail (FlowDecision.Iterated(List.length extent) :: flow)

                match collection with
                | Collection.Literal extent -> over extent
                // A collection the STATE holds (Phase 1991, D36): read ONCE,
                // here, from the state as of this position — through the
                // placement's reader, so a durable placement journals the
                // read and serves the record on replay — and refused before
                // the first element when its extent is over the ceiling.
                // Past the check the extent is the collection.
                | Collection.Stored(stored, ceiling) ->
                    if ceiling < 0 then
                        Error "the collection's ceiling is negative"
                    else
                        match readExtent stored.Name (fun () -> Ok(stored.Read tree)) with
                        | Error reason -> Error reason
                        | Ok extent when List.length extent > ceiling ->
                            Error "the collection's extent is over its declared ceiling"
                        | Ok extent -> over extent

        // Validation first (Phase 1990): a placeholder read where no `Each`
        // binds it is a defect of the FORM, refused with the placeholder named
        // before the first op is applied — never met mid-plan as an op the
        // domain cannot apply.
        match StateWitness.scopeDefects state ops with
        | defect :: _ -> Error(ScopeDefect.describe defect)
        | [] -> go ops tree staged trail flow

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
    /// performs after the handler returns. The fifth — `HostCall` — reaches
    /// outside, so it is staged (D8); and so is `ApplyOps` under a registered op
    /// performer (D19), one staged call per edit, in the same list.
    let private runEffect
        (state: StateWitness<'Node, 'Op>)
        (channel: StoreWitness<'Store> option)
        (registry: ServerEffectRegistry)
        (performance: OpPerformance<'Node, 'Op>)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Compute.EvalError>)
        (effect: ServerEffect<'Op>)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action> =
        let capability = ServerEffect.capability effect

        if not (registry.Gate capability) then
            let denial = ServerEffectDenial.GateRefused capability
            registry.OnDenied denial
            deny denial acc
        else
            // Two plan-time refusals, in this order, both reported as a HALT
            // rather than as a denial — and the choice is deliberate. The denial
            // vocabulary is a closed, wire-specified pair — "this host has no
            // such capability" and "this host's gate refused it" — and neither
            // is true here: the capability exists and the gate admitted it.
            //
            // FIRST, validation of the FORM (Phase 1990): an op sequence that
            // reads a placeholder no enclosing `Each` binds, or whose `Each`
            // rebinds an enclosing name, is refused with the placeholder named.
            // Ahead of the argument policy deliberately — an unsubstituted
            // placeholder is not an address, and a policy that read it as one
            // would refuse the effect for the wrong reason (an off-list argument)
            // or, on an allow-list that happened to spell the token, admit a form
            // that can never run.
            //
            // THEN the argument policy: what refused is a bound the host declared
            // about the arguments, which is what the halt names, through the
            // same capability-plus-reason shape the landing-slot refusal below
            // already uses for the same class of check. Nothing is `Performed`,
            // and `Halted` stops every later stage, so the handler reaches no
            // performer and commits nothing.
            let refusal: string option =
                let scope =
                    match effect with
                    | ServerEffect.ApplyOps ops -> StateWitness.scopeDefects state ops
                    | ServerEffect.RunQuery _
                    | ServerEffect.HostCall _
                    | ServerEffect.EmitPatch _
                    | ServerEffect.Notify _ -> []

                match scope with
                | defect :: _ -> Some(ScopeDefect.describe defect)
                | [] ->
                    match ServerArgumentPolicy.check state registry effect with
                    | Error defect -> Some(ServerArgumentPolicy.describe defect)
                    | Ok() -> None

            match refusal with
            | Some reason -> halt capability reason acc
            | None ->
                let performed =
                    { acc with
                        Performed = capability :: acc.Performed }

                match effect with
                | ServerEffect.RunQuery(name, source, pipeline) ->
                    match channel with
                    // No dispatch axis, no binding channel: the table has
                    // nowhere to land, so the read is refused before it runs.
                    | None -> halt capability NoBindingChannel acc
                    | Some store ->
                        // The host's evaluator, or the in-memory fold where the
                        // host registered none (Phase 1905, D34): the absent case
                        // is the fold exactly as it ran before the seam, halt
                        // reasons included.
                        let evaluator =
                            registry.QueryEvaluator |> Option.defaultValue QueryEvaluator.inMemory

                        if QueryEvaluator.isStaged evaluator then
                            // An evaluator its host could not declare a pure read
                            // is staged like a host call (D8): admitted here, asked
                            // only once the plan completed, its table landing in
                            // the slot then. `Performed` is not extended, on the
                            // host-call arm's terms, and the trail records that
                            // the run reached outside.
                            { acc with
                                Staged =
                                    { Capability = capability
                                      Perform =
                                        fun () ->
                                            evaluator.Evaluate source pipeline resolve
                                            |> Result.map (fun table -> Landing.QuerySlot(name, table))
                                            |> Result.mapError QueryFault.describe }
                                    :: acc.Staged
                                Trail = UndoStep.Reached capability :: acc.Trail }
                        else
                            match evaluator.Evaluate source pipeline resolve with
                            | Error fault -> halt capability (QueryFault.describe fault) acc
                            | Ok table ->
                                { performed with
                                    Store =
                                        { performed.Store with
                                            Bindings = store.LandQuery name table performed.Store.Bindings } }

                | ServerEffect.ApplyOps ops ->
                    // The only domain-state mutation. Folded with short-circuit: an
                    // op that fails leaves the whole handler uncommitted rather than
                    // applying its predecessors, which is what makes the atomicity
                    // claim above true of the tree and not merely of the store. A
                    // guard on the op channel halts here, on its own reason.
                    match
                        planOps
                            state
                            performance
                            registry.ReadExtent
                            capability
                            ops
                            performed.Store.Tree
                            acc.Staged
                            acc.Trail
                            acc.Flow
                    with
                    | Error code -> halt capability code acc
                    | Ok(tree, staged, trail, flow) ->
                        match performance with
                        // In memory: the apply IS the effect, and it is performed
                        // here, in the plan phase — the shape every placement had
                        // before Phase 1967, and the UI tier's still. `planOps`
                        // staged nothing.
                        | OpPerformance.InMemory ->
                            { performed with
                                Store = { performed.Store with Tree = tree }
                                Trail = trail
                                Flow = flow }
                        // Performed: the apply is a PLAN. The tree moves — a later
                        // stage reads the planned tree — but the capability is not
                        // recorded as performed; the staged calls are, one per edit,
                        // when the perform phase runs them. `Performed` is
                        // deliberately NOT extended here, on the host-call arm's
                        // terms: it is the audit trail of what happened, and at
                        // this point nothing has.
                        | OpPerformance.Performed _ ->
                            { acc with
                                Store = { acc.Store with Tree = tree }
                                Staged = staged
                                Trail = trail
                                Flow = flow }

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
                        let refusal =
                            match into, channel with
                            | None, _ -> None
                            // No dispatch axis: no binding channel to land in.
                            | Some _, None -> Some NoBindingChannel
                            | Some key, Some store when store.IsReserved key ->
                                Some(
                                    sprintf
                                        "landing slot is under the host-reserved '%s' namespace"
                                        store.ReservedPrefix
                                )
                            | Some _, Some _ -> None

                        match refusal with
                        | Some reason -> halt capability reason acc
                        | None ->
                            // Admitted, not performed. `Performed` is deliberately
                            // NOT extended here: it is the audit trail of what
                            // happened, and at this point nothing has.
                            { acc with
                                Staged =
                                    { Capability = capability
                                      Perform =
                                        fun () ->
                                            performer args
                                            |> Result.map (fun result ->
                                                match into with
                                                | Some key -> Landing.StateSlot(key, result)
                                                | None -> Landing.Nowhere) }
                                    :: acc.Staged
                                Trail = UndoStep.Reached capability :: acc.Trail }

                | ServerEffect.EmitPatch ops ->
                    { performed with
                        Patches = List.rev ops @ performed.Patches
                        Trail = UndoStep.Emitted capability :: performed.Trail }

                | ServerEffect.Notify(channel, payload) ->
                    { performed with
                        Notifications = (channel, payload) :: performed.Notifications
                        Trail = UndoStep.Reached capability :: performed.Trail }

    /// Run one stage.
    let private runStage
        (state: StateWitness<'Node, 'Op>)
        (channel: StoreWitness<'Store> option)
        (compute: string -> 'Action -> 'Store -> BoundedOutcome<'Store, 'Effect> * Trace)
        (registry: ServerEffectRegistry)
        (performance: OpPerformance<'Node, 'Op>)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Compute.EvalError>)
        (nodeId: string)
        (stage: HandlerStage<'Action, 'Op>)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action> =
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
            // The fold with its TRACE (Phase 1977): the same outcome `runInert`
            // answers — the model's `traced_agrees` — and the Bennett trace an
            // undo's reversal of this stage is built from, recorded on the
            // trail with the action.
            let outcome, trace = compute nodeId action acc.Store.Bindings

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
                    @ acc.Diagnostics
                Trail = UndoStep.Compute(action, trace) :: acc.Trail
                Flow = List.rev (FlowDecision.ofTrace trace) @ acc.Flow }
        | Effect effect -> runEffect state channel registry performance resolve effect acc

    /// PERFORM the staged host calls, in declaration order, stopping at the
    /// first failure (D8). This is the only code in the handler that reaches
    /// outside, and it runs only after the plan phase completed — so a handler
    /// that was going to fail on its own terms has already failed, silently and
    /// for free, before any of this.
    let rec private perform
        (channel: StoreWitness<'Store> option)
        (staged: StagedCall list)
        (acc: Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action>)
        : Accumulator<'Node, 'Store, 'Op, 'Effect, 'Action> =
        match staged with
        | [] -> acc
        | call :: rest ->
            match call.Perform() with
            | Error reason ->
                { acc with
                    Halted = true
                    Diagnostics = ServerDiagnostic.PerformFailed(call.Capability, reason) :: acc.Diagnostics }
            | Ok landing ->
                let recorded =
                    { acc with
                        Externally = call.Capability :: acc.Externally }

                let landed =
                    match landing, channel with
                    | Landing.Nowhere, _ -> recorded
                    | Landing.StateSlot(key, result), Some store ->
                        { recorded with
                            Store =
                                { recorded.Store with
                                    Bindings = store.Assign key result recorded.Store.Bindings } }
                    | Landing.QuerySlot(slot, table), Some store ->
                        { recorded with
                            Store =
                                { recorded.Store with
                                    Bindings = store.LandQuery slot table recorded.Store.Bindings } }
                    // Unreachable: a slot with no binding channel was refused
                    // while planning, so it was never staged.
                    | Landing.StateSlot _, None
                    | Landing.QuerySlot _, None -> invalidOp "a landing slot was staged with no binding channel"

                perform channel rest landed

    /// Run a handler's stages in order against `store`, committing only if every
    /// stage planned and every staged call — host call or, under a registered
    /// op performer, op — then performed, and answer beside the outcome the
    /// PLAN the run leaves for its undo (Phase 1977): every edit with the
    /// state it was applied to, every compute stage with its trace, every
    /// step that reached the world, in plan order. `runWith` below is this
    /// without the plan. `nodeId` is the originating event's node, threaded
    /// into the shared interpreter exactly as the other placements thread it.
    /// `performance` says how this placement performs an op (Phase 1967).
    ///
    /// Runs under every composition (Phase 1974): it reads the witness's
    /// STATE axis for ops, and its dispatch position only for a compute stage
    /// — which a composition with no dispatch axis cannot hold — and for a
    /// landing slot, which such a composition has no channel for. The
    /// model's `run_planned`.
    let runPlanned
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (registry: ServerEffectRegistry)
        (performance: OpPerformance<'Node, 'Op>)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Compute.EvalError>)
        (nodeId: string)
        (handler: Handler<'Action, 'Op>)
        (store: ServerStore<'Node, 'Store>)
        : HandlerOutcome<'Node, 'Store, 'Op, 'Effect> * UndoPlan<'Node, 'Op, 'Action> =
        let start =
            { Store = store
              Halted = false
              Performed = []
              Externally = []
              Staged = []
              Patches = []
              Notifications = []
              ClientEffects = []
              Diagnostics = []
              Trail = []
              Flow = [] }

        let channel =
            (witness.Dispatch :> IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>).Fold
            |> Option.map (fun fold -> fold.Store)

        // The ONE fold, traced: the outcome `runInert` answers (the model's
        // `traced_agrees`), and the trace the undo reverses the stage by.
        // The arm declines every call (a compute stage answers none) and
        // reads a store-bound collection's extent through the registry's
        // reader (Phase 1991), so a durable placement journals that read
        // exactly as it journals an op stage's.
        let computeArm: HandlerArm<'Store, 'Effect, unit> =
            { HandlerArm.inert with
                ReadExtent = registry.ReadExtent }

        let compute (nodeId: string) (action: 'Action) (bindings: 'Store) =
            let outcome, (), trace =
                BoundedActions.runTraced witness computeArm nodeId action bindings ()

            outcome, trace

        let planned =
            handler.Stages
            |> List.fold
                (fun acc stage ->
                    if acc.Halted then
                        acc
                    else
                        runStage witness.State channel compute registry performance resolve nodeId stage acc)
                start

        // The phase boundary. Nothing external has run above this line, and
        // nothing below it can be undone — which is the entire content of the
        // atomicity decision, in one `if`.
        let final =
            if planned.Halted then
                planned
            else
                perform channel (List.rev planned.Staged) planned

        let plan: UndoPlan<'Node, 'Op, 'Action> =
            { Handler = handler.Name
              Committed = not final.Halted
              Entry = store.Tree
              Steps = List.rev final.Trail }

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
              Diagnostics = List.rev final.Diagnostics
              Flow = List.rev final.Flow },
            plan
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
              Diagnostics = List.rev final.Diagnostics
              Flow = List.rev final.Flow },
            plan

    /// `runPlanned` without the plan: the outcome alone. The signature every
    /// placement called before Phase 1977, unchanged.
    let runWith
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (registry: ServerEffectRegistry)
        (performance: OpPerformance<'Node, 'Op>)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Compute.EvalError>)
        (nodeId: string)
        (handler: Handler<'Action, 'Op>)
        (store: ServerStore<'Node, 'Store>)
        : HandlerOutcome<'Node, 'Store, 'Op, 'Effect> =
        fst (runPlanned witness registry performance resolve nodeId handler store)

    /// `runWith` at `OpPerformance.InMemory`: ops are performed by being
    /// applied, which is every placement before Phase 1967 and the UI tier
    /// still. A placement whose ops reach the world registers a performer and
    /// calls `runWith`.
    let run
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (registry: ServerEffectRegistry)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Compute.EvalError>)
        (nodeId: string)
        (handler: Handler<'Action, 'Op>)
        (store: ServerStore<'Node, 'Store>)
        : HandlerOutcome<'Node, 'Store, 'Op, 'Effect> =
        runWith witness registry OpPerformance.InMemory resolve nodeId handler store
