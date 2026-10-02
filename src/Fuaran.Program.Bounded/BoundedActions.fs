namespace Fuaran.Program.Bounded

open Fuaran.Core

// ============================================================================
//  The bounded interpreter — the placement-neutral core of the program loop.
//
//  This is the interpreter that runs a program tree. It is deliberately
//  independent of WHERE the loop runs: the same fold drives a server session
//  and a browser client. "One algebra, two placements" holds by construction —
//  a single interpreter, a single no-closure-invocation invariant, two hosts.
//
//  The algebra's shape — pipeline core, richer control structure as vocabulary
//  atop it rather than as a second evaluator — is DECISIONS.md D1. Do not
//  re-decide it here.
//
//  Since Phase 1896 the fold is DOMAIN-GENERIC (DECISIONS.md D18): it reads an
//  action only through a witness's VIEW of it — `ActionView`'s eight shapes,
//  `Sequence` / `Assign` / `Call` / `Require` / `Choose` / `Repeat` / `Each`
//  / `Leaf` — and asks the witness what a leaf does. It is written to meet
//  `proofs/BoundedFold.fst`'s `fold`, which was restated over the view and
//  re-proved before this code (D14, Phase 1898), restated again over the
//  fifth shape — the halting guard the second witness found missing (Phase
//  1967, F1) — before the port that added it, and restated a third time over
//  the selection and the bounded iteration D1 and D2 charter (Phase 1976: the
//  second witness's re-run found the core had sequence and abort but neither)
//  before the port that added those, with the reversible fragment of the view
//  named and proved to undo itself in the same restatement; and a fourth time
//  over the eighth shape — per-element iteration over a literal collection,
//  which lowers to the core by substitution (Phase 1990, D29) — before the
//  port that added it, with `each_is_lowering` the equation that nothing
//  downstream learns a new shape.
//
//  The case this module serves is an **emitted, wire-decoded** tree, where
//  there is **no hand-authored `update` and no message type**. The "model" is
//  the tree's *state store* (the witness's `'Store`, whose state channel is the
//  one the fold writes); the "update" is *applying the bounded action set
//  against that store*.
//
//  ── The load-bearing safety property (STATED, PROVED AND TESTED) ────────────
//  Emitted trees are **bounded**: the wire format cannot carry arbitrary
//  closures, and a wire decoder substitutes inert placeholders for every
//  closure slot. So a generated tree carries no foreign code. THIS INTERPRETER
//  ENFORCES THE OTHER HALF: it holds no closure and **never invokes** one a
//  carried action holds — it reads an action only through the witness's
//  `View`, `Lower` and `Describe` (the model's `fold_blind`, unconditional).
//  Only a witness could reach a closure, so the witness carries the matching
//  obligation (the model's `blind_to`). The only state mutation is the
//  `Assign` shape's one write; the only outward effects are the at-most-one
//  effect each leaf lowers to (K3).
//  Together — bounded wire ⇒ no foreign code in the tree; this interpreter ⇒ no
//  closure ever called — running a *generated* app has **no arbitrary-code-
//  execution surface**, which is the invariant multi-tenant "platform runs your
//  app" hosting rests on. (Resource bounds — no arbitrary *cost* — are the other
//  half, enforced by the driver's per-interaction budget; bounded code + bounded
//  cost = safe to run untrusted on shared infra.)
//
//  ── The eight shapes ────────────────────────────────────────────────────────
//    - `Each(collection, placeholder, body)` → PER-ELEMENT ITERATION over a
//      literal collection (Phase 1990): the body once per element, with that
//      element substituted for the placeholder through the witness's
//      `Substitute`, composed as a sequence — vocabulary that lowers to the
//      core by substitution (D1). An ill-scoped placeholder is refused at the
//      fold's entry, before anything runs.
//    - `Choose(entry, whenTrue, whenFalse, exit)` → SELECTION (Phase 1976):
//      resolve the entry condition as a guard's; the boolean true takes the
//      true arm, anything else the false arm, unresolved or errored halts;
//      then the exit assertion, when carried, must hold after the true arm and
//      fail after the false arm, or the branch halts after its arm.
//    - `Repeat(bound, body)` → BOUNDED ITERATION (Phase 1976): the body that
//      many times, composed as a sequence; a parameter bound is resolved once
//      and range-checked first, and an over-bound one halts before the body.
//    - `Assign(key, value, from)` → write the state channel at `key` (the only
//      mutation), refusing a reserved key and an expression that does not
//      resolve to a value.
//    - `Sequence` → fold in order, threading the store + concatenating effects.
//    - `Call` → the HANDLER-EFFECT ARM (below). A placement that registers
//      nothing for the endpoint gets the documented no-op this arm has always
//      been; a placement that does gets its answer folded in place.
//    - `Require` → the HALTING guard (Phase 1967): resolve the condition
//      against the store; the boolean true holds, anything else halts the
//      enclosing sequence with a refusal diagnostic and `Halted = true`. The
//      one shape that halts, and the one the UI tier never produces.
//    - `Leaf` → whatever the witness's `Lower` answers: at most one effect, a
//      refusal with a reason, or a documented decline. The fold does not look
//      inside it.
//
//  ── The handler-effect arm (DECISIONS.md D7) ────────────────────────────────
//  Recognising a call action is the FOLD's job, at every depth, because the fold
//  is the only thing that knows where in a `Sequence` the call sits. A placement
//  that matched on the action itself to find nested calls would be a second
//  evaluator, which is what D1 forbids — so the recognition lives here once and
//  the placement supplies only the ANSWER, through `HandlerArm`. What a call
//  MEANS is placement-specific; WHERE it is recognised is not.
// ============================================================================

/// A readable diagnostic from the bounded interpreter — the "this did nothing,
/// on purpose" signal. A generated tree that *intended* a call or a host
/// channel would otherwise get silent nothing — a dead end for emission
/// debugging. The no-op is still correct (the no-arbitrary-code invariant is
/// unchanged); this makes it observable so introspection tools can surface it.
[<RequireQualifiedAccess>]
type BoundedDiagnostic =
    /// The action is a documented no-op on the bounded (generated-app) path —
    /// it has no form there. `action` is the action's log-safe description
    /// (never payload values).
    | UnsupportedOnBoundedPath of nodeId: string * action: string
    /// The action was REFUSED, not merely inert: a leaf the domain refused, or
    /// a state write addressing a reserved key. Distinct from
    /// `UnsupportedOnBoundedPath` because the two mean opposite things to
    /// whoever is debugging an emission: "this path does not implement that"
    /// versus "that was not allowed".
    | Refused of nodeId: string * action: string * reason: string

module BoundedDiagnostic =
    /// Human-readable, log-safe description for introspection / debugging.
    let describe (d: BoundedDiagnostic) : string =
        match d with
        | BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, action) ->
            sprintf
                "action '%s' on node '%s' is inert on the bounded path (no form for the generated-app loop)"
                action
                nodeId
        | BoundedDiagnostic.Refused(nodeId, action, reason) ->
            sprintf "action '%s' on node '%s' was refused: %s" action nodeId reason

/// The outcome of interpreting one bounded action: the (possibly-updated) store
/// + the closure-free effects to perform + the no-op diagnostics
/// (observability, never behaviour) + whether a guard HALTED the fold. The
/// store is returned (not mutated in place) so the loop threads it
/// functionally — one interaction, one new store value.
type BoundedOutcome<'Store, 'Effect> =
    {
        Store: 'Store
        Effects: 'Effect list
        Diagnostics: BoundedDiagnostic list
        /// A `Require` shape did not hold (Phase 1967): nothing after it in the
        /// enclosing sequence ran, and `Diagnostics` ends with the refusal that
        /// says why. `Store` is the store AS OF THE HALT — the fold never rolls
        /// back; a placement that does is the handler, whose atomicity unit it
        /// is (D8). `false` on every outcome the UI tier produces: none of its
        /// arms views as a guard, and the model proves it never halts
        /// (`ui_never_halts`).
        Halted: bool
    }

/// A placement's answer to a call action the fold recognised: the store as the
/// answer left it, the closure-free effects it produced, the diagnostics it
/// wants folded into the interpreter's own, and the placement's accumulation
/// threaded onward.
///
/// The three bounded fields are why an answer is folded IN PLACE rather than
/// reported for later: a call sitting between two writes in a `Sequence` must
/// see the first write and be seen by the second, which only holds if the fold
/// threads the answer's store into the rest of the sequence.
type HandlerAnswer<'Store, 'Effect, 'Placement> =
    { Store: 'Store
      Effects: 'Effect list
      Diagnostics: BoundedDiagnostic list
      Placement: 'Placement }

/// The shared fold's **handler-effect arm** — what a call action means at this
/// placement (DECISIONS.md D7).
///
/// `Answer nodeId endpoint store placement` returns `None` to DECLINE, which is
/// the documented no-op this arm has always been at every placement that
/// registers nothing: the store is untouched and the fold emits its usual
/// `UnsupportedOnBoundedPath` diagnostic. A placement that answers takes
/// responsibility for the whole arm, diagnostics included.
///
/// `'Placement` is opaque here on purpose. It is how a placement threads its
/// OWN accumulation — a domain tree, an audit trail, a staged effect list —
/// through a fold that must know nothing about any of it. Widening this module
/// to know what a handler is would put the server placement's vocabulary in the
/// package the browser placement also consumes.
type HandlerArm<'Store, 'Effect, 'Placement> =
    { Answer: string -> string -> 'Store -> 'Placement -> HandlerAnswer<'Store, 'Effect, 'Placement> option }

module HandlerArm =

    /// The arm that declines every call — the default, and the only arm a
    /// placement with no handler registry can honestly offer. Named rather than
    /// implied, so "this placement runs no handlers" is a statement in the code
    /// rather than an absence.
    let inert<'Store, 'Effect, 'Placement> : HandlerArm<'Store, 'Effect, 'Placement> =
        { Answer = fun _ _ _ _ -> None }

module BoundedActions =

    /// Empty-effect outcome that only carries the (unchanged or updated) store.
    /// The model's `store_only`.
    let private store (s: 'Store) : BoundedOutcome<'Store, 'Effect> =
        { Store = s
          Effects = []
          Diagnostics = []
          Halted = false }

    /// A documented-no-op outcome: unchanged store, no effects, one readable
    /// diagnostic naming the inert action. The model's `declined`.
    let private noOp (nodeId: string) (description: string) (s: 'Store) : BoundedOutcome<'Store, 'Effect> =
        { Store = s
          Effects = []
          Diagnostics = [ BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, description) ]
          Halted = false }

    /// A REFUSED outcome: unchanged store, no effects, one readable diagnostic
    /// naming what was refused and why. The model's `refused`.
    let private refused
        (nodeId: string)
        (description: string)
        (reason: string)
        (s: 'Store)
        : BoundedOutcome<'Store, 'Effect> =
        { Store = s
          Effects = []
          Diagnostics = [ BoundedDiagnostic.Refused(nodeId, description, reason) ]
          Halted = false }

    /// A HALTED outcome (Phase 1967): the same diagnostic a refusal carries,
    /// and the flag the enclosing sequence stops on. The model's `halted`.
    let private halted
        (nodeId: string)
        (description: string)
        (reason: string)
        (s: 'Store)
        : BoundedOutcome<'Store, 'Effect> =
        { Store = s
          Effects = []
          Diagnostics = [ BoundedDiagnostic.Refused(nodeId, description, reason) ]
          Halted = true }

    /// A halt that follows an ARM that ran (Phase 1976): the arm's store,
    /// effects and diagnostics kept, the halt's diagnostic appended, and the
    /// flag. A violated exit assertion is this — the arm's writes stand as of
    /// the halt (the fold never rolls back; the handler does, D8), and the
    /// outcome says which assertion failed. The model's `halted_after`.
    let private haltedAfter
        (outcome: BoundedOutcome<'Store, 'Effect>)
        (nodeId: string)
        (description: string)
        (reason: string)
        : BoundedOutcome<'Store, 'Effect> =
        { Store = outcome.Store
          Effects = outcome.Effects
          Diagnostics = outcome.Diagnostics @ [ BoundedDiagnostic.Refused(nodeId, description, reason) ]
          Halted = true }

    /// Compose two outcomes as a sequence composes its members: the second's
    /// store and halt, the lists concatenated in order. The model's
    /// `composed`.
    let private composed
        (first: BoundedOutcome<'Store, 'Effect>)
        (second: BoundedOutcome<'Store, 'Effect>)
        : BoundedOutcome<'Store, 'Effect> =
        { Store = second.Store
          Effects = first.Effects @ second.Effects
          Diagnostics = first.Diagnostics @ second.Diagnostics
          Halted = second.Halted }

    /// Interpret one bounded action against the store, through a domain's
    /// witness, with a placement-supplied **handler-effect arm** for the call
    /// shape and the placement's own accumulation threaded alongside
    /// (DECISIONS.md D7). The model's `run_action`: view, then fold.
    ///
    /// `nodeId` is the originating event's node (for node-addressed effects a
    /// leaf lowers to). **Never invokes a closure carried by the action** — see
    /// the safety property at the top of this file.
    ///
    /// `tracing` (Phase 1976) is whether this is a REVERSIBLE run: true records
    /// the Bennett trace the inverse is built from — the value each assignment
    /// overwrote, read through `Store.Read` — and false records nothing and
    /// reads nothing, so the forward `run` costs a program that never reverses
    /// nothing. ONE fold, not two: the model has `fold` and `fold_traced` and
    /// proves them equal in outcome (`traced_agrees`); the code keeps D1's one
    /// evaluating `match` and makes the trace a flag of it.
    ///
    /// THIS IS THE ONLY PLACE ANYTHING IN THIS DOMAIN INTERPRETS AN ACTION. One
    /// evaluating `match`, over the eight view shapes, in one file, reachable
    /// from every placement — which is D1's "no second evaluator" as a property
    /// a reader can check by grep rather than a claim they have to trust. (Two
    /// other walks read the same view without interpreting it: the resource
    /// budget's cascade cost and the demanded-effect projection. Neither
    /// performs, mutates or resolves anything, which is exactly the distinction
    /// D1 draws.)
    let rec private runFold
        (fold: DispatchFold<'Action, 'Expr, 'Store, 'Effect>)
        (arm: HandlerArm<'Store, 'Effect, 'Placement>)
        (tracing: bool)
        (nodeId: string)
        (action: 'Action)
        (s: 'Store)
        (placement: 'Placement)
        : BoundedOutcome<'Store, 'Effect> * 'Placement * Trace =
        // Compose members in order, threading the store AND the placement's
        // accumulation, concatenating effects + diagnostics — and stopping at
        // the first member that halts. Threading the placement here is what
        // makes a nested call behave exactly as a top-level one — the sequence
        // is the only structure that could have made them differ. Left to
        // right and iterative, which is the model's `fold_many` by its own
        // `sequence_homomorphism`: the store each member sees is the one its
        // predecessors left, the lists concatenate in order, and a member that
        // halted is the whole answer. A reversible run records the members
        // that ran, in order. Shared by the two shapes that ARE sequences — a
        // `Sequence`, and an `Each` over its lowered elements.
        let many (members: 'Action list) : BoundedOutcome<'Store, 'Effect> * 'Placement * Trace list =
            let outcome, p, traces =
                members
                |> List.fold
                    (fun (acc: BoundedOutcome<'Store, 'Effect>, p, traces) a ->
                        if acc.Halted then
                            acc, p, traces
                        else
                            let next, p', t = runFold fold arm tracing nodeId a acc.Store p
                            composed acc next, p', (if tracing then t :: traces else traces))
                    (store s, placement, [])

            outcome, p, List.rev traces

        match fold.Action.View action with
        // The one store mutation: write the state channel. The reserved key
        // namespace is closed on the bounded path too. This loop's whole
        // premise is that the tree is untrusted, so a generated tree writing a
        // reserved key is exactly the case the namespace exists for. Specified —
        // §4.3 rules the reservation a property of the NAMESPACE, binding every
        // placement, since untrustedness does not vary by which loop is running.
        // Only the refusal's shape is placement-specific: this is a legitimate
        // document whose action does nothing, not a decode failure, and per
        // §10.5 it leaves the step's event-level refusal unset. Which keys are
        // reserved is the witness's (K5); that the fold refuses them is not.
        //
        // A reversible run records, with the write, the value it overwrote
        // (`Trace.Wrote`); a refused write records nothing to undo.
        | ActionView.Assign(key, value, from) ->
            if fold.Store.IsReserved key then
                refused
                    nodeId
                    (fold.Action.Describe action)
                    (sprintf "State key '%s' is under the host-reserved '%s' namespace" key fold.Store.ReservedPrefix)
                    s,
                placement,
                Trace.Nothing
            else
                // `from` (value XOR from, decode-enforced) evaluates AT DISPATCH
                // TIME against the store itself. An unresolved / errored source
                // performs NO write and is diagnosed, never silent.
                let payload: Result<JVal option, string> =
                    match from with
                    | Some expr ->
                        (match fold.Expr.Resolve s expr with
                         | ExprResolution.Resolved jv -> Ok(Some jv)
                         | ExprResolution.NotResolved -> Ok None
                         | ExprResolution.Errored m -> Error m)
                    | None -> Ok value

                match payload with
                | Ok(Some jv) ->
                    let trace =
                        if tracing then
                            Trace.Wrote(fold.Store.Read key s)
                        else
                            Trace.Nothing

                    store (fold.Store.Assign key jv s), placement, trace
                | Ok None ->
                    refused
                        nodeId
                        (fold.Action.Describe action)
                        "valueFrom did not resolve to a value — no write performed"
                        s,
                    placement,
                    Trace.Nothing
                | Error m ->
                    refused
                        nodeId
                        (fold.Action.Describe action)
                        (sprintf "valueFrom errored: %s — no write performed" m)
                        s,
                    placement,
                    Trace.Nothing

        // A call that ALSO declares where its answer should land is REFUSED
        // rather than honoured or quietly ignored (DECISIONS.md D9). Result-target
        // ownership sits with the handler: its stages name landing slots, one per
        // result, and a tree-declared target is a second mechanism for the same
        // job that no placement in this domain honours. Refusing makes the
        // retirement observable; ignoring would leave an author believing an
        // answer lands somewhere it never does.
        //
        // The reason is log-safe: it names neither the endpoint nor the target,
        // both of which come off the wire.
        //
        // Otherwise the handler-effect arm decides what the call MEANS here;
        // declining is the documented no-op, and its diagnostic makes a
        // generated tree that *intended* a call observable rather than a silent
        // dead end. Nothing the call carries is invoked — the view carries the
        // endpoint and the target flag, and nothing else.
        | ActionView.Call(endpoint, declaresTarget) ->
            if declaresTarget then
                refused
                    nodeId
                    (fold.Action.Describe action)
                    "the call declares a result target; a handler declares where its own results land"
                    s,
                placement,
                Trace.Nothing
            else
                match arm.Answer nodeId endpoint s placement with
                | None -> noOp nodeId (fold.Action.Describe action) s, placement, Trace.Nothing
                | Some answer ->
                    // An answered call never halts: a handler is its own
                    // atomicity unit (D8), so one that failed rolled ITSELF
                    // back and the fold carries on — `HandlerAnswer` carries
                    // no halt, by its type.
                    { Store = answer.Store
                      Effects = answer.Effects
                      Diagnostics = answer.Diagnostics
                      Halted = false },
                    answer.Placement,
                    Trace.Nothing

        // The halting guard (Phase 1967). The condition resolves against the
        // store at dispatch — the SAME arrow an `Assign`'s `from` resolves
        // through — and the fold decides: the boolean true holds and changes
        // nothing; any other value, an unresolved condition and an errored one
        // halt, the last carrying the domain's own text as the reason, which
        // is how a typed refusal reaches the diagnostic. A guard writes nothing
        // and emits nothing, whichever way it goes. The model's `VRequire` arm;
        // `fold_total` proves it is the only non-composition shape whose step
        // can halt.
        | ActionView.Require condition ->
            (match fold.Expr.Resolve s condition with
             | ExprResolution.Resolved(JBool true) -> store s
             | ExprResolution.Resolved _ -> halted nodeId (fold.Action.Describe action) "the guard did not hold" s
             | ExprResolution.NotResolved ->
                 halted nodeId (fold.Action.Describe action) "the guard did not resolve to a value" s
             | ExprResolution.Errored m -> halted nodeId (fold.Action.Describe action) m s),
            placement,
            Trace.Nothing

        // SELECTION (Phase 1976). The entry condition resolves exactly as a
        // guard's does and picks the arm: the boolean true takes `whenTrue`,
        // any other value `whenFalse`; unresolved or errored halts, as a guard
        // would, before either arm runs. The arm runs as a member of a sequence
        // would (its halt is the branch's halt). Then the EXIT assertion, when
        // carried, resolves against the store the arm left: it must hold after
        // the true arm and fail after the false arm — the assertion the inverse
        // branch picks its arm by (`reverse`) — and violated, unresolved or
        // errored it halts AFTER the arm, keeping what the arm did, with the
        // failure named. The model's `VChoose` arm.
        | ActionView.Choose(entry, whenTrue, whenFalse, exit) ->
            match fold.Expr.Resolve s entry with
            | ExprResolution.Resolved jv ->
                let tookTrue = (jv = JBool true)

                let armOutcome, armPlacement, armTrace =
                    runFold fold arm tracing nodeId (if tookTrue then whenTrue else whenFalse) s placement

                let trace =
                    if tracing then
                        Trace.Choose(tookTrue, armTrace)
                    else
                        Trace.Nothing

                if armOutcome.Halted then
                    armOutcome, armPlacement, trace
                else
                    match exit with
                    | None -> armOutcome, armPlacement, trace
                    | Some assertion ->
                        match fold.Expr.Resolve armOutcome.Store assertion with
                        | ExprResolution.Resolved jv' ->
                            if (jv' = JBool true) = tookTrue then
                                armOutcome, armPlacement, trace
                            else
                                haltedAfter
                                    armOutcome
                                    nodeId
                                    (fold.Action.Describe action)
                                    (if tookTrue then
                                         "the exit assertion did not hold after the true arm"
                                     else
                                         "the exit assertion held after the false arm"),
                                armPlacement,
                                trace
                        | ExprResolution.NotResolved ->
                            haltedAfter
                                armOutcome
                                nodeId
                                (fold.Action.Describe action)
                                "the exit assertion did not resolve to a value",
                            armPlacement,
                            trace
                        | ExprResolution.Errored m ->
                            haltedAfter
                                armOutcome
                                nodeId
                                (fold.Action.Describe action)
                                (sprintf "the exit assertion errored: %s" m),
                            armPlacement,
                            trace
            | ExprResolution.NotResolved ->
                halted nodeId (fold.Action.Describe action) "the branch condition did not resolve to a value" s,
                placement,
                Trace.Nothing
            | ExprResolution.Errored m -> halted nodeId (fold.Action.Describe action) m s, placement, Trace.Nothing

        // BOUNDED ITERATION (Phase 1976). A literal bound runs the body that
        // many times; a parameter bound is resolved against the store ONCE, at
        // entry, read as a count, and checked against its declared range — an
        // over-bound repeat halts here, before the first iteration, which is
        // what lets the budget price it at the range's top without the store.
        // The body sees no index. Each iteration composes as a sequence member
        // does, stopping at the first halt: the model's `fold_repeat`, which
        // `repeat_is_unrolling` proves is the sequence of `count` bodies. A
        // negative count is the one refusal the model (whose count is a `nat`)
        // cannot express: refused here, with its own reason.
        | ActionView.Repeat(bound, body) ->
            let times (count: int) : BoundedOutcome<'Store, 'Effect> * 'Placement * Trace =
                let rec go
                    (remaining: int)
                    (acc: BoundedOutcome<'Store, 'Effect>)
                    (p: 'Placement)
                    (traces: Trace list)
                    =
                    if remaining <= 0 then
                        acc, p, traces
                    else
                        let next, p', t = runFold fold arm tracing nodeId body acc.Store p
                        let traces' = if tracing then t :: traces else traces
                        let acc' = composed acc next

                        if next.Halted then
                            acc', p', traces'
                        else
                            go (remaining - 1) acc' p' traces'

                let outcome, p, traces = go count (store s) placement []

                outcome,
                p,
                (if tracing then
                     Trace.Repeat(List.rev traces)
                 else
                     Trace.Nothing)

            match bound with
            | Bound.Literal count when count < 0 ->
                halted nodeId (fold.Action.Describe action) "the repeat's bound is negative" s, placement, Trace.Nothing
            | Bound.Literal count -> times count
            | Bound.Parameter(expr, lo, hi) ->
                match fold.Expr.Resolve s expr with
                | ExprResolution.Resolved(JInt count) when count >= 0 ->
                    if lo <= count && count <= hi then
                        times count
                    else
                        halted nodeId (fold.Action.Describe action) "the repeat's bound is outside its declared range" s,
                        placement,
                        Trace.Nothing
                | ExprResolution.Resolved _ ->
                    halted nodeId (fold.Action.Describe action) "the repeat's bound did not resolve to a count" s,
                    placement,
                    Trace.Nothing
                | ExprResolution.NotResolved ->
                    halted nodeId (fold.Action.Describe action) "the repeat's bound did not resolve to a value" s,
                    placement,
                    Trace.Nothing
                | ExprResolution.Errored m -> halted nodeId (fold.Action.Describe action) m s, placement, Trace.Nothing

        // A leaf is the domain's: lowered to at most one effect, refused with a
        // reason, or declined — each with a readable diagnostic, so "this action
        // is inert on the generated-app path" is observable. The fold does not
        // look inside it, and it never recurses into one. A leaf's refusal does
        // NOT halt: the sequence carries on past it, as it always has.
        | ActionView.Leaf _ ->
            (match fold.Action.Lower nodeId action s with
             | LeafOutcome.Emit effect ->
                 { Store = s
                   Effects = [ effect ]
                   Diagnostics = []
                   Halted = false }
             | LeafOutcome.Refuse reason -> refused nodeId (fold.Action.Describe action) reason s
             | LeafOutcome.Decline -> noOp nodeId (fold.Action.Describe action) s),
            placement,
            Trace.Nothing

        // Compose: the sequence of the members (`many`, above).
        | ActionView.Sequence actions ->
            let outcome, p, traces = many actions
            outcome, p, (if tracing then Trace.Seq traces else Trace.Nothing)

        // PER-ELEMENT ITERATION over a literal collection (Phase 1990, D29).
        // Vocabulary that lowers to the core by SUBSTITUTION: the witness
        // substitutes each element for the placeholder in the body — the fold
        // cannot see inside an action, so the substitution is the witness's
        // exactly as `View` and `Lower` are — and what runs is the sequence
        // of the results, member for member as a `Sequence` runs: the store
        // threaded, the lists concatenated, the first halt the whole answer.
        // The model's `VEach` carries the elements already lowered and its
        // `each_is_lowering` is the equation; the differential host runs this
        // fold (substituting as it goes) beside that model. A placeholder the
        // body reads outside any `Each` that binds it was refused at `run`'s
        // entry, before this arm could be reached. An empty collection runs
        // nothing. A reversible run records the elements that ran, under the
        // trace's own constructor, so a reader sees how many.
        | ActionView.Each(collection, placeholder, body) ->
            let outcome, p, traces =
                many (ActionWitness.lowered fold.Action collection placeholder body)

            outcome, p, (if tracing then Trace.Each traces else Trace.Nothing)

    /// The SCOPE check at the fold's entry (Phase 1990): a program whose body
    /// reads a placeholder no enclosing `Each` binds, or rebinds one an
    /// enclosing `Each` already binds, is REFUSED before its first step — a
    /// halt naming the placeholder, with nothing run, nothing written and
    /// nothing emitted. Validation, not run time: the defect is a property
    /// of the form, decided from the tree alone (`ActionWitness.scopeDefects`),
    /// and a run that reached the placeholder would meet it as an expression
    /// the domain cannot resolve, after earlier elements had already run.
    /// `None` for a well-scoped program, which is every program with no
    /// `Each` and every program the UI tier produces.
    let private scopeRefusal
        (fold: DispatchFold<'Action, 'Expr, 'Store, 'Effect>)
        (nodeId: string)
        (action: 'Action)
        (s: 'Store)
        : BoundedOutcome<'Store, 'Effect> option =
        match ActionWitness.scopeDefects fold.Action action with
        | [] -> None
        | defect :: _ -> Some(halted nodeId (fold.Action.Describe action) (ScopeDefect.describe defect) s)

    /// The scope defects of an action, decided from the tree (Phase 1990):
    /// `ActionWitness.scopeDefects` through a composition's dispatch
    /// position. A host that validates a program before registering it
    /// reads this; `run` and `runTraced` refuse on it at entry regardless.
    let scopeDefects
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (action: 'Action)
        : ScopeDefect list =
        ActionWitness.scopeDefects (DispatchPosition.fold witness.Dispatch).Action action

    /// `runFold` over a composition's dispatch position — the fold every
    /// placement runs (Phase 1974: the fold reads the DISPATCH axis and only
    /// it). Generic over the position, so a path that runs under every
    /// composition (the server handler) can call it; it is only ever handed
    /// an action, and only a composition that fills the dispatch axis has one.
    /// Records nothing: the model's `run_action`. Refuses an ill-scoped
    /// program at entry (Phase 1990), before its first step.
    let run
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (arm: HandlerArm<'Store, 'Effect, 'Placement>)
        (nodeId: string)
        (action: 'Action)
        (s: 'Store)
        (placement: 'Placement)
        : BoundedOutcome<'Store, 'Effect> * 'Placement =
        let fold = DispatchPosition.fold witness.Dispatch

        match scopeRefusal fold nodeId action s with
        | Some refused -> refused, placement
        | None ->
            let outcome, p, _ = runFold fold arm false nodeId action s placement
            outcome, p

    /// Interpret one bounded action against the store at a placement that runs
    /// NO handlers — the browser client and the server driver, neither of which
    /// has a handler registry to consult.
    ///
    /// Exactly `run witness HandlerArm.inert`, so it is the same fold rather
    /// than a simpler one: a placement without handlers differs from a
    /// placement with them in what it ANSWERS, never in how it interprets.
    let runInert
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (nodeId: string)
        (action: 'Action)
        (s: 'Store)
        : BoundedOutcome<'Store, 'Effect> =
        run witness HandlerArm.inert nodeId action s () |> fst

    // ─── The reversible fragment (Phase 1976) ───────────────────────────────
    //
    // Selection and iteration are where a reversible language needs structure
    // a forward-only one does not, so the two shapes were designed for reversal
    // from the start (the Janus model): a branch carries an exit assertion that
    // picks the arm to undo, and a repeat's bound is a function of the tree
    // alone. `Assign` is not reversible by construction — it destroys the old
    // value — so it joins the fragment BY TRACE: a reversible run records, at
    // each write, the value it overwrote, and the inverse restores it with an
    // ordinary assignment (the Bennett embedding). What is recorded lives in
    // the `Trace` the traced run returns beside its outcome — the plan phase
    // holds it exactly as it holds the pre-state — and the forward `run`
    // records nothing. `proofs/BoundedFold.fst` section 6: `reversible`,
    // `fold_traced`, `traced_agrees`, `reverse`, `reverse_run`.

    /// The same fold, RECORDING its trace (Phase 1976): the outcome and the
    /// placement `run` would answer — the model's `traced_agrees` — and the
    /// Bennett trace the inverse is built from. The one path that calls
    /// `Store.Read`. The model's `run_action_traced`. Refuses an ill-scoped
    /// program at entry exactly as `run` does, with an empty trace.
    let runTraced
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (arm: HandlerArm<'Store, 'Effect, 'Placement>)
        (nodeId: string)
        (action: 'Action)
        (s: 'Store)
        (placement: 'Placement)
        : BoundedOutcome<'Store, 'Effect> * 'Placement * Trace =
        let fold = DispatchPosition.fold witness.Dispatch

        match scopeRefusal fold nodeId action s with
        | Some refused -> refused, placement, Trace.Nothing
        | None -> runFold fold arm true nodeId action s placement

    /// Whether an action is in the REVERSIBLE FRAGMENT, decided from the tree
    /// alone: sequence, assign, the guard, a branch WITH an exit assertion, a
    /// repeat with a LITERAL bound, an `Each` whose lowered elements all are
    /// — never a call or a leaf (effects: a property of the op, declared on
    /// the state witness, not the flow algebra's). A branch without an exit
    /// assertion has nothing to say which arm to undo; a parameter bound is
    /// read from the store the body may have overwritten. An `Each` is read
    /// in its lowered form, as everything reads it: an `Each` over nothing
    /// does nothing and is in. The model's `reversible`.
    let reversible
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (action: 'Action)
        : bool =
        let fold = DispatchPosition.fold witness.Dispatch

        let rec go (a: 'Action) : bool =
            match fold.Action.View a with
            | ActionView.Sequence members -> members |> List.forall go
            | ActionView.Assign _
            | ActionView.Require _ -> true
            | ActionView.Choose(_, whenTrue, whenFalse, exit) -> Option.isSome exit && go whenTrue && go whenFalse
            | ActionView.Repeat(Bound.Literal _, body) -> go body
            | ActionView.Each(collection, placeholder, body) ->
                ActionWitness.lowered fold.Action collection placeholder body |> List.forall go
            | ActionView.Repeat(Bound.Parameter _, _)
            | ActionView.Call _
            | ActionView.Leaf _ -> false

        go action

    /// The inverse of a RUN (Phase 1976) — a program in the fragment, built from
    /// the program and its trace, that the core itself folds (`runReversed`):
    /// the inverse is not a domain action, because the core cannot construct
    /// one, so it is a tree the core views as it views any action, carrying
    /// the forward actions' descriptions for its diagnostics. The model's
    /// `reverse`, shape for shape:
    ///
    ///   - a sequence inverts to its members' inverses in REVERSE order;
    ///   - an assignment that wrote inverts to the assignment of the value it
    ///     overwrote (`Restore`); one that was refused inverts to nothing;
    ///   - a guard is its own inverse — it held at that store, and holds again;
    ///   - a branch inverts to the branch whose ENTRY is the exit assertion and
    ///     whose EXIT is the entry condition, with the arm that ran inverted
    ///     and the other arm EMPTY — nothing was recorded for the arm that did
    ///     not run, and the exit assertion is what guarantees the inverse never
    ///     takes it;
    ///   - a literal repeat inverts to the SEQUENCE of its iterations'
    ///     inverses in reverse order — not a repeat, because each iteration
    ///     overwrote different values and so has its own inverse body;
    ///   - an `Each` (Phase 1990) inverts to the SEQUENCE of its lowered
    ///     elements' inverses in reverse order, for the same reason — it IS
    ///     the sequence of its elements (`each_reverse_is_sequence_reverse`).
    [<RequireQualifiedAccess>]
    type Reversed<'Expr> =
        | Sequence of describe: string * members: Reversed<'Expr> list
        | Restore of describe: string * key: string * old: JVal
        | Require of describe: string * condition: 'Expr
        | Choose of
            describe: string *
            entry: 'Expr *
            whenTrue: Reversed<'Expr> *
            whenFalse: Reversed<'Expr> *
            exit: 'Expr

    module Reversed =
        /// The forward action's description, carried for the diagnostics.
        let describe (reversed: Reversed<'Expr>) : string =
            match reversed with
            | Reversed.Sequence(d, _)
            | Reversed.Restore(d, _, _)
            | Reversed.Require(d, _)
            | Reversed.Choose(d, _, _, _, _) -> d

        /// The inverse's view — the one the core folds it through.
        let view (reversed: Reversed<'Expr>) : ActionView<Reversed<'Expr>, 'Expr> =
            match reversed with
            | Reversed.Sequence(_, members) -> ActionView.Sequence members
            | Reversed.Restore(_, key, old) -> ActionView.Assign(key, Some old, None)
            | Reversed.Require(_, condition) -> ActionView.Require condition
            | Reversed.Choose(_, entry, whenTrue, whenFalse, exit) ->
                ActionView.Choose(entry, whenTrue, whenFalse, Some exit)

        /// A log-safe rendering of the inverse's SHAPE: which keys it restores
        /// and in what order, never the values (the trace holds those, and a
        /// value is payload a log must not carry).
        let rec encode (reversed: Reversed<'Expr>) : JVal =
            match reversed with
            | Reversed.Sequence(d, members) ->
                Canon.typed "Sequence" [ "of", JStr d; "members", JArr(members |> List.map encode) ]
            | Reversed.Restore(d, key, _) -> Canon.typed "Restore" [ "key", JStr key; "of", JStr d ]
            | Reversed.Require(d, _) -> Canon.typed "Require" [ "of", JStr d ]
            | Reversed.Choose(d, _, whenTrue, whenFalse, _) ->
                Canon.typed "Choose" [ "of", JStr d; "whenFalse", encode whenFalse; "whenTrue", encode whenTrue ]

    /// Build the inverse of a run from the action and the trace `runTraced`
    /// recorded for it. Total: a program and a trace that do not match (a
    /// call, a leaf, a trace from a different run) invert to the empty
    /// sequence, and `reverse_run` says nothing of them — its hypotheses
    /// (`reversible`, a run that did not halt, `Trace.restorable`) exclude
    /// them. The model's `reverse`.
    let reverse
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (action: 'Action)
        (trace: Trace)
        : Reversed<'Expr> =
        let fold = DispatchPosition.fold witness.Dispatch

        let rec go (a: 'Action) (t: Trace) : Reversed<'Expr> =
            let d = fold.Action.Describe a

            match fold.Action.View a, t with
            | ActionView.Sequence members, Trace.Seq steps -> Reversed.Sequence(d, many members steps)
            | ActionView.Assign(key, _, _), Trace.Wrote(Some old) -> Reversed.Restore(d, key, old)
            | ActionView.Require condition, _ -> Reversed.Require(d, condition)
            | ActionView.Choose(entry, whenTrue, _, Some exit), Trace.Choose(true, arm) ->
                Reversed.Choose(d, exit, go whenTrue arm, Reversed.Sequence(d, []), entry)
            | ActionView.Choose(entry, _, whenFalse, Some exit), Trace.Choose(false, arm) ->
                Reversed.Choose(d, exit, Reversed.Sequence(d, []), go whenFalse arm, entry)
            | ActionView.Repeat(Bound.Literal count, body), Trace.Repeat iterations ->
                Reversed.Sequence(d, many (List.replicate (max count 0) body) iterations)
            | ActionView.Each(collection, placeholder, body), Trace.Each steps ->
                Reversed.Sequence(d, many (ActionWitness.lowered fold.Action collection placeholder body) steps)
            | _ -> Reversed.Sequence(d, [])

        // The members' inverses in REVERSE order: the last member that ran is
        // undone first. The model's `reverse_many`.
        and many (members: 'Action list) (steps: Trace list) : Reversed<'Expr> list =
            match members, steps with
            | a :: rest, t :: ts -> many rest ts @ [ go a t ]
            | _ -> []

        go action trace

    /// Fold an inverse program: `run` over the inverse's own view, with the
    /// dispatch axis's expressions and store — so the inverse is interpreted
    /// by the ONE evaluator, exactly as the forward program was, and
    /// `reverse_run`'s statement is about this call. The inverse has no leaf
    /// and no call, so the lowering is never asked and the arm never answers.
    let runReversed
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (nodeId: string)
        (reversed: Reversed<'Expr>)
        (s: 'Store)
        : BoundedOutcome<'Store, 'Effect> =
        let fold = DispatchPosition.fold witness.Dispatch

        let inverseFold: DispatchFold<Reversed<'Expr>, 'Expr, 'Store, 'Effect> =
            { Action =
                { View = Reversed.view
                  Lower = fun _ _ _ -> LeafOutcome.Decline
                  Describe = Reversed.describe
                  Encode = Reversed.encode
                  Decode =
                    fun _ ->
                        Error
                            { Class = "malformed-referenced-value"
                              Detail = "an inverse program is built from a run, never decoded" }
                  // An inverse holds no `Each` — a run's inverse is built
                  // from its lowered elements — so it is never substituted
                  // into and reads no placeholder.
                  Substitute = fun _ _ reversed -> reversed
                  Placeholders = fun _ -> [] }
              Expr = fold.Expr
              Store = fold.Store }

        let outcome, _, _ = runFold inverseFold HandlerArm.inert false nodeId reversed s ()
        outcome
