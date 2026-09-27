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
//  action only through a witness's VIEW of it — `ActionView`'s four shapes,
//  `Sequence` / `Assign` / `Call` / `Leaf` — and asks the witness what a leaf
//  does. It is written to meet `proofs/BoundedFold.fst`'s `fold`, which was
//  restated over the view and re-proved before this code (D14, Phase 1898).
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
//  ── The four shapes ─────────────────────────────────────────────────────────
//    - `Assign(key, value, from)` → write the state channel at `key` (the only
//      mutation), refusing a reserved key and an expression that does not
//      resolve to a value.
//    - `Sequence` → fold in order, threading the store + concatenating effects.
//    - `Call` → the HANDLER-EFFECT ARM (below). A placement that registers
//      nothing for the endpoint gets the documented no-op this arm has always
//      been; a placement that does gets its answer folded in place.
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
/// (observability, never behaviour). The store is returned (not mutated in
/// place) so the loop threads it functionally — one interaction, one new store
/// value.
type BoundedOutcome<'Store, 'Effect> =
    { Store: 'Store
      Effects: 'Effect list
      Diagnostics: BoundedDiagnostic list }

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
          Diagnostics = [] }

    /// A documented-no-op outcome: unchanged store, no effects, one readable
    /// diagnostic naming the inert action. The model's `declined`.
    let private noOp (nodeId: string) (description: string) (s: 'Store) : BoundedOutcome<'Store, 'Effect> =
        { Store = s
          Effects = []
          Diagnostics = [ BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, description) ] }

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
          Diagnostics = [ BoundedDiagnostic.Refused(nodeId, description, reason) ] }

    /// Interpret one bounded action against the store, through a domain's
    /// witness, with a placement-supplied **handler-effect arm** for the call
    /// shape and the placement's own accumulation threaded alongside
    /// (DECISIONS.md D7). The model's `run_action`: view, then fold.
    ///
    /// `nodeId` is the originating event's node (for node-addressed effects a
    /// leaf lowers to). **Never invokes a closure carried by the action** — see
    /// the safety property at the top of this file.
    ///
    /// THIS IS THE ONLY PLACE ANYTHING IN THIS DOMAIN INTERPRETS AN ACTION. One
    /// evaluating `match`, over the four view shapes, in one file, reachable
    /// from every placement — which is D1's "no second evaluator" as a property
    /// a reader can check by grep rather than a claim they have to trust. (Two
    /// other walks read the same view without interpreting it: the resource
    /// budget's cascade cost and the demanded-effect projection. Neither
    /// performs, mutates or resolves anything, which is exactly the distinction
    /// D1 draws.)
    let rec run
        (witness: ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (arm: HandlerArm<'Store, 'Effect, 'Placement>)
        (nodeId: string)
        (action: 'Action)
        (s: 'Store)
        (placement: 'Placement)
        : BoundedOutcome<'Store, 'Effect> * 'Placement =
        match witness.Action.View action with
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
        | ActionView.Assign(key, value, from) ->
            (if witness.Store.IsReserved key then
                 refused
                     nodeId
                     (witness.Action.Describe action)
                     (sprintf
                         "State key '%s' is under the host-reserved '%s' namespace"
                         key
                         witness.Store.ReservedPrefix)
                     s
             else
                 // `from` (value XOR from, decode-enforced) evaluates AT DISPATCH
                 // TIME against the store itself. An unresolved / errored source
                 // performs NO write and is diagnosed, never silent.
                 let payload: Result<JVal option, string> =
                     match from with
                     | Some expr ->
                         (match witness.Expr.Resolve s expr with
                          | ExprResolution.Resolved jv -> Ok(Some jv)
                          | ExprResolution.NotResolved -> Ok None
                          | ExprResolution.Errored m -> Error m)
                     | None -> Ok value

                 match payload with
                 | Ok(Some jv) -> store (witness.Store.Assign key jv s)
                 | Ok None ->
                     refused
                         nodeId
                         (witness.Action.Describe action)
                         "valueFrom did not resolve to a value — no write performed"
                         s
                 | Error m ->
                     refused
                         nodeId
                         (witness.Action.Describe action)
                         (sprintf "valueFrom errored: %s — no write performed" m)
                         s),
            placement

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
                    (witness.Action.Describe action)
                    "the call declares a result target; a handler declares where its own results land"
                    s,
                placement
            else
                match arm.Answer nodeId endpoint s placement with
                | None -> noOp nodeId (witness.Action.Describe action) s, placement
                | Some answer ->
                    { Store = answer.Store
                      Effects = answer.Effects
                      Diagnostics = answer.Diagnostics },
                    answer.Placement

        // A leaf is the domain's: lowered to at most one effect, refused with a
        // reason, or declined — each with a readable diagnostic, so "this action
        // is inert on the generated-app path" is observable. The fold does not
        // look inside it, and it never recurses into one.
        | ActionView.Leaf _ ->
            (match witness.Action.Lower nodeId action s with
             | LeafOutcome.Emit effect ->
                 { Store = s
                   Effects = [ effect ]
                   Diagnostics = [] }
             | LeafOutcome.Refuse reason -> refused nodeId (witness.Action.Describe action) reason s
             | LeafOutcome.Decline -> noOp nodeId (witness.Action.Describe action) s),
            placement

        // Compose: fold in order, threading the store AND the placement's
        // accumulation, concatenating effects + diagnostics. Threading the
        // placement here is what makes a nested call behave exactly as a
        // top-level one — the sequence is the only structure that could have
        // made them differ. Left to right and iterative, which is the model's
        // `fold_many` by its own `sequence_homomorphism`: the store each member
        // sees is the one its predecessors left, and the lists concatenate in
        // order.
        | ActionView.Sequence actions ->
            actions
            |> List.fold
                (fun (acc: BoundedOutcome<'Store, 'Effect>, p) a ->
                    let next, p' = run witness arm nodeId a acc.Store p

                    { Store = next.Store
                      Effects = acc.Effects @ next.Effects
                      Diagnostics = acc.Diagnostics @ next.Diagnostics },
                    p')
                (store s, placement)

    /// Interpret one bounded action against the store at a placement that runs
    /// NO handlers — the browser client and the server driver, neither of which
    /// has a handler registry to consult.
    ///
    /// Exactly `run witness HandlerArm.inert`, so it is the same fold rather
    /// than a simpler one: a placement without handlers differs from a
    /// placement with them in what it ANSWERS, never in how it interprets.
    let runInert
        (witness: ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (nodeId: string)
        (action: 'Action)
        (s: 'Store)
        : BoundedOutcome<'Store, 'Effect> =
        run witness HandlerArm.inert nodeId action s () |> fst
