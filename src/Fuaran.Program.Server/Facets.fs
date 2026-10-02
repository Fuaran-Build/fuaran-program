namespace Fuaran.Program.Server

open Fuaran.Program.Bounded

// ============================================================================
//  RUNTIME FACETS — what this placement guarantees, said honestly.
//
//  A composition surface that holds a logic tree wants one sentence about it:
//  how many times may an invocation's effect be observed, is repeating it safe,
//  and what does a restart do to work in flight. This file is that sentence,
//  DERIVED from the handler registration and the interpreter's discipline rather
//  than declared by anyone.
//
//  ── The vocabulary is MIRRORED, not imported ────────────────────────────────
//  The three closed sets and their wire tags below are the composition
//  contract's runtime-guarantees vocabulary, mirrored here field for field and
//  tag for tag. That is deliberate and it is the standing posture for a
//  cross-boundary agreement of this kind: the two sides agree on NAMES without
//  either taking a type dependency on the other, so neither can drag the other's
//  release cycle behind it. What keeps a mirror honest is that it is
//  CHECKABLE — the tags are the whole of the agreement, they are spelled once
//  here, and a suite pins them.
//
//  ── Delivery has no rank, so the combination is stated over the HAZARD ───────
//  At-most-once may lose; at-least-once may duplicate; neither dominates the
//  other, and a rank over them would manufacture an ordering the world does not
//  have. So the combination below runs over a two-flag hazard — may it lose, may
//  it duplicate — which IS a lattice (set union), with the empty hazard as its
//  identity. The named facets are then three of the four points of that lattice,
//  and the fourth has no name.
//
//  **That unnamed fourth point is the reason this is a hazard union and not a
//  three-value table.** A derivation that proves both hazards is a derivation no
//  declaration can honestly carry, and the check below says exactly that rather
//  than picking whichever of the two named values happens to read better.
//
//  ── Only a WEAKER declaration is admitted ───────────────────────────────────
//  The consistency rule is one sentence: a declared facet's hazards must be a
//  SUPERSET of the derived ones. A host may promise less than it can deliver —
//  that is conservative and costs only the promise. A host may not promise more,
//  and a declaration that does is the one defect this file exists to make
//  impossible to publish quietly.
// ============================================================================

/// How many times a logic tree's effect may be OBSERVED.
///
/// Deliberately NOT ordered — see the header. The tags are the cross-boundary
/// agreement; do not respell them at a call site.
[<RequireQualifiedAccess>]
type DeliveryFacet =
    /// Dispatched once; a failure loses it.
    | AtMostOnce
    /// Retried until it completes, so a duplicate is possible.
    | AtLeastOnce
    /// Duplicates may still arrive at the boundary, but the EFFECT is observed
    /// once. Named *effective* because that is what it is: a claim about
    /// deduplication at the receiver, never a property a transport supplies on
    /// its own.
    | ExactlyOnceEffective

/// Whether repeating an invocation is safe — and, when it is safe only because
/// something remembers, whether that something is DECLARED.
///
/// The middle value is the whole reason this is three and not a boolean. "Safe
/// to repeat because a journal dedupes it" is a claim about substrate that must
/// actually be present; folding it into `Idempotent` would make that claim
/// unfalsifiable.
[<RequireQualifiedAccess>]
type IdempotencyFacet =
    /// Repeating it changes nothing, intrinsically, from the shape of the
    /// operation.
    | Idempotent
    /// Repeating it is safe only because a declared store — here, the effect
    /// journal — remembers that it happened.
    | IdempotentWithStore
    /// Repeating it changes the result.
    | NonIdempotent

/// What a process restart does to work already in flight.
[<RequireQualifiedAccess>]
type RestartVisibility =
    /// In-flight work is dropped, silently.
    | LostOnRestart
    /// In-flight work is re-delivered after the restart: durable enough to
    /// retry, not durable enough to have completed.
    | RetriedAfterRestart
    /// The work is durable across the restart — it completed, or it is recorded
    /// such that it will.
    | SurvivesRestart

/// The triple a placement pins.
type RuntimeGuarantees =
    { Delivery: DeliveryFacet
      Idempotency: IdempotencyFacet
      Restart: RestartVisibility }

/// The two things a delivery posture can do wrong, as flags.
///
/// This is the lattice the combination actually runs over. It is separate from
/// `DeliveryFacet` because it is TOTAL under union and the facet set is not:
/// three of its four points have names and one does not.
type DeliveryHazards = { MayLose: bool; MayDuplicate: bool }

/// A DERIVED posture: the same triple, with the delivery axis carried as its
/// hazards so that combining a set of them is total.
type DerivedGuarantees =
    { Delivery: DeliveryHazards
      Idempotency: IdempotencyFacet
      Restart: RestartVisibility }

/// What the HOST declares about the performers it registered.
///
/// Keyed by function name, and the key space is the host's own registry rather
/// than anything a tree supplied. An UNDECLARED performer reads as
/// `NonIdempotent`: an unknown performer is assumed to change the result, never
/// assumed to be safe, because over-claiming is the failure this whole file
/// exists to prevent.
///
/// `OpPerformer` (Phase 1980) is the same declaration for the registered OP
/// performer — the one `OpPerformance.Performed` carries, which performs every
/// op of a committed plan as a staged call of its own. One slot rather than a
/// key, because a placement registers at most one. `None` is undeclared and
/// reads as `NonIdempotent` on the same terms: a domain whose op is a
/// content-addressed write may declare it `Idempotent` and reach exactly-once;
/// one whose op is a push cannot claim it without saying so. Read only where
/// ops are actually performed — in memory the arm is recomputed, and a
/// declaration about a performer that is not registered changes nothing. The
/// slot's identity is `OpPerformance.RegistrationKey` (Phase 1983): what a
/// finding about it names, and what an operator's revoke withdraws it by.
type PerformerFacets =
    { Declared: Map<string, IdempotencyFacet>
      OpPerformer: IdempotencyFacet option }

/// Which interpreter is running the handlers, and under what settings. The
/// facet derivation is a function of THIS as much as of the handler — the same
/// stage list guarantees different things under the two interpreters, which is
/// the entire point of there being two.
type ReplayDiscipline =
    {
        /// Whether the journal's own storage survives a process restart. The
        /// host's claim, carried from `EffectJournal.SurvivesRestart`.
        JournalSurvivesRestart: bool
        /// Whether an INDETERMINATE step — attempted, no recorded result — is
        /// re-invoked rather than refused. This is the single knob that decides
        /// whether the placement may lose an effect or may duplicate one, and it
        /// cannot avoid both.
        ReinvokeIndeterminate: bool
    }

/// The interpreter a logic tree's handlers run under.
[<RequireQualifiedAccess>]
type PlacementDiscipline =
    /// The two-phase staging interpreter: nothing is journaled, so a process
    /// that dies mid-handler leaves no record and a resumed session starts from
    /// whatever it last durably held.
    | Direct
    /// The durable-execution interpreter: deterministic replay over a journaled
    /// effect log.
    | DeterministicReplay of ReplayDiscipline

/// A finding from the end-to-end facet check.
///
/// `Capability` is a DERIVED capability or a host-registered function name —
/// never an endpoint, never a payload — on the same terms as every other
/// diagnostic this placement emits.
type FacetFinding =
    { Code: string
      Capability: string option
      Detail: string }

module FacetCode =

    /// The declared delivery admits FEWER hazards than the derivation proves —
    /// the composition has been told the placement is stronger than it is.
    [<Literal>]
    let DeliveryInflated = "facet-delivery-inflated"

    /// The derivation proves BOTH hazards, which no named facet spells. Not an
    /// inflation by any particular declaration — a statement that no honest
    /// declaration exists for this configuration.
    [<Literal>]
    let DeliveryUnspellable = "facet-delivery-unspellable"

    /// The declared idempotency is stronger than the derived one.
    [<Literal>]
    let IdempotencyInflated = "facet-idempotency-inflated"

    /// The declared restart visibility is stronger than the derived one.
    [<Literal>]
    let RestartInflated = "facet-restart-inflated"

    /// A handler names a host function whose idempotency the host has not
    /// declared, so the derivation used the conservative default.
    ///
    /// Reported even when nothing is inflated, because it is usually the whole
    /// EXPLANATION of a weak derived facet: a host reading "at-least-once" and
    /// wondering why is looking for exactly this line. It is a fact about the
    /// registration, never a refusal.
    [<Literal>]
    let UndeclaredPerformer = "facet-undeclared-performer"

    /// The declaration names a placement id this package does not serve.
    [<Literal>]
    let UnknownPlacement = "facet-unknown-placement"

[<RequireQualifiedAccess>]
module DeliveryFacet =

    let tag (facet: DeliveryFacet) : string =
        match facet with
        | DeliveryFacet.AtMostOnce -> "atMostOnce"
        | DeliveryFacet.AtLeastOnce -> "atLeastOnce"
        | DeliveryFacet.ExactlyOnceEffective -> "exactlyOnceEffective"

    let ofTag (tag: string) : Result<DeliveryFacet, string> =
        match tag with
        | "atMostOnce" -> Ok DeliveryFacet.AtMostOnce
        | "atLeastOnce" -> Ok DeliveryFacet.AtLeastOnce
        | "exactlyOnceEffective" -> Ok DeliveryFacet.ExactlyOnceEffective
        | other -> Error("unknown delivery facet: " + other)

    /// The facet's hazards, over the EFFECT.
    ///
    /// `ExactlyOnceEffective` has none, and the distinction is worth stating
    /// because the same value admits a duplicate at the BOUNDARY: something
    /// dedupes, which is exactly what "effective" claims. This axis is about
    /// what is observed, not about what arrives.
    let hazards (facet: DeliveryFacet) : DeliveryHazards =
        match facet with
        | DeliveryFacet.AtMostOnce -> { MayLose = true; MayDuplicate = false }
        | DeliveryFacet.AtLeastOnce -> { MayLose = false; MayDuplicate = true }
        | DeliveryFacet.ExactlyOnceEffective ->
            { MayLose = false
              MayDuplicate = false }

    /// The named facet for a hazard set, or `None` where the set has no name.
    ///
    /// `None` is a real answer and must not be defaulted at a call site: it says
    /// the configuration may lose AND may duplicate, and choosing either name
    /// would suppress half of that.
    let ofHazards (h: DeliveryHazards) : DeliveryFacet option =
        match h.MayLose, h.MayDuplicate with
        | false, false -> Some DeliveryFacet.ExactlyOnceEffective
        | true, false -> Some DeliveryFacet.AtMostOnce
        | false, true -> Some DeliveryFacet.AtLeastOnce
        | true, true -> None

    let all: DeliveryFacet list =
        [ DeliveryFacet.AtMostOnce
          DeliveryFacet.AtLeastOnce
          DeliveryFacet.ExactlyOnceEffective ]

[<RequireQualifiedAccess>]
module IdempotencyFacet =

    let tag (facet: IdempotencyFacet) : string =
        match facet with
        | IdempotencyFacet.Idempotent -> "idempotent"
        | IdempotencyFacet.IdempotentWithStore -> "idempotentWithStore"
        | IdempotencyFacet.NonIdempotent -> "nonIdempotent"

    let ofTag (tag: string) : Result<IdempotencyFacet, string> =
        match tag with
        | "idempotent" -> Ok IdempotencyFacet.Idempotent
        | "idempotentWithStore" -> Ok IdempotencyFacet.IdempotentWithStore
        | "nonIdempotent" -> Ok IdempotencyFacet.NonIdempotent
        | other -> Error("unknown idempotency facet: " + other)

    /// Strictly increasing in how much a repeat costs. Combination takes the max.
    let hazardRank (facet: IdempotencyFacet) : int =
        match facet with
        | IdempotencyFacet.Idempotent -> 0
        | IdempotencyFacet.IdempotentWithStore -> 1
        | IdempotencyFacet.NonIdempotent -> 2

    let all: IdempotencyFacet list =
        [ IdempotencyFacet.Idempotent
          IdempotencyFacet.IdempotentWithStore
          IdempotencyFacet.NonIdempotent ]

[<RequireQualifiedAccess>]
module RestartVisibility =

    let tag (visibility: RestartVisibility) : string =
        match visibility with
        | RestartVisibility.LostOnRestart -> "lostOnRestart"
        | RestartVisibility.RetriedAfterRestart -> "retriedAfterRestart"
        | RestartVisibility.SurvivesRestart -> "survivesRestart"

    let ofTag (tag: string) : Result<RestartVisibility, string> =
        match tag with
        | "lostOnRestart" -> Ok RestartVisibility.LostOnRestart
        | "retriedAfterRestart" -> Ok RestartVisibility.RetriedAfterRestart
        | "survivesRestart" -> Ok RestartVisibility.SurvivesRestart
        | other -> Error("unknown restart visibility: " + other)

    /// Strictly increasing in how much a restart costs. Combination takes the max.
    let hazardRank (visibility: RestartVisibility) : int =
        match visibility with
        | RestartVisibility.SurvivesRestart -> 0
        | RestartVisibility.RetriedAfterRestart -> 1
        | RestartVisibility.LostOnRestart -> 2

    let all: RestartVisibility list =
        [ RestartVisibility.LostOnRestart
          RestartVisibility.RetriedAfterRestart
          RestartVisibility.SurvivesRestart ]

module PerformerFacets =

    /// Nothing declared. The DEFAULT, and its consequence is stated rather than
    /// hidden: every registered performer then reads as `NonIdempotent`, so a
    /// handler that calls one cannot be certified exactly-once until the host
    /// says something.
    let none: PerformerFacets =
        { Declared = Map.empty
          OpPerformer = None }

    /// Declare what repeating this performer does.
    let declare (fn: string) (facet: IdempotencyFacet) (facets: PerformerFacets) : PerformerFacets =
        { facets with
            Declared = Map.add fn facet facets.Declared }

    /// Whether the host said anything about this performer.
    let isDeclared (fn: string) (facets: PerformerFacets) : bool = Map.containsKey fn facets.Declared

    /// What repeating this performer does — `NonIdempotent` where the host has
    /// not said. Conservative by construction; see the type's own note.
    let facetOf (fn: string) (facets: PerformerFacets) : IdempotencyFacet =
        Map.tryFind fn facets.Declared
        |> Option.defaultValue IdempotencyFacet.NonIdempotent

    /// Declare what repeating the registered OP performer does (Phase 1980).
    let declareOpPerformer (facet: IdempotencyFacet) (facets: PerformerFacets) : PerformerFacets =
        { facets with OpPerformer = Some facet }

    /// Whether the host said anything about the op performer.
    let isOpPerformerDeclared (facets: PerformerFacets) : bool = facets.OpPerformer.IsSome

    /// What repeating the op performer does — `NonIdempotent` where the host
    /// has not said, on `facetOf`'s terms.
    let opPerformerFacet (facets: PerformerFacets) : IdempotencyFacet =
        facets.OpPerformer |> Option.defaultValue IdempotencyFacet.NonIdempotent

/// The placement ids a composition's logic-tree slot can name.
module PlacementId =

    /// The slot itself, from the cross-layer reference vocabulary. Derived from
    /// the specification's own pair rather than respelled, so the two cannot
    /// drift.
    let slot: string = ProgramWire.logicTreeSlot

    /// The two-phase staging interpreter.
    let direct: string = ProgramWire.LogicTreeNamespace + "/server-direct"

    /// The durable-execution interpreter: deterministic replay over a journaled
    /// effect log.
    let durable: string = ProgramWire.LogicTreeNamespace + "/server-durable"

    let all: string list = [ direct; durable ]

    /// The discipline a placement id names, at its DEFAULT settings.
    ///
    /// `None` for an id this package does not serve — reported as a finding
    /// rather than defaulted, because guessing which interpreter a stranger's id
    /// meant is how a declaration ends up describing something nobody runs.
    let disciplineOf (id: string) : PlacementDiscipline option =
        if id = direct then
            Some PlacementDiscipline.Direct
        elif id = durable then
            Some(
                PlacementDiscipline.DeterministicReplay
                    { JournalSurvivesRestart = true
                      ReinvokeIndeterminate = false }
            )
        else
            None

/// What a composition's logic-tree slot carries about the placement behind it.
///
/// The reference is by ID (`LogicTreeRef`), never by structural position, so the
/// two sides agree on a name without either taking a type dependency on the
/// other — the same reference vocabulary the demand projection already uses.
type PlacementDeclaration =
    {
        /// Which interpreter runs this logic tree's handlers.
        Placement: string
        /// The logic tree this declaration is about.
        LogicTree: LogicTreeRef
        /// What it guarantees.
        Guarantees: RuntimeGuarantees
    }

module Facets =

    // ─── the combination ─────────────────────────────────────────────────────

    /// The identity: nothing runs, so nothing can be lost or duplicated.
    let noHazards: DeliveryHazards =
        { MayLose = false
          MayDuplicate = false }

    /// Union. Associative, commutative, idempotent, with `noHazards` as its
    /// two-sided identity — and a set union is all four of those for free, which
    /// is the argument for stating the combination on this axis rather than on
    /// the facet names.
    let combineHazards (a: DeliveryHazards) (b: DeliveryHazards) : DeliveryHazards =
        { MayLose = a.MayLose || b.MayLose
          MayDuplicate = a.MayDuplicate || b.MayDuplicate }

    /// The posture of a thing that holds no effects at all.
    let neutral: DerivedGuarantees =
        { Delivery = noHazards
          Idempotency = IdempotencyFacet.Idempotent
          Restart = RestartVisibility.SurvivesRestart }

    /// **The conjunction rule.** The posture of the thing that holds both.
    ///
    /// Delivery unions the hazards; idempotency and restart are genuine chains,
    /// so a composition is only as safe as its least safe member and those take
    /// the max hazard rank. There is no arm here that can make a composition
    /// STRONGER than one of its parts, which is the property the whole file is
    /// for and the one the suite pins exhaustively.
    let combine (a: DerivedGuarantees) (b: DerivedGuarantees) : DerivedGuarantees =
        let worse rank x y = if rank x >= rank y then x else y

        { Delivery = combineHazards a.Delivery b.Delivery
          Idempotency = worse IdempotencyFacet.hazardRank a.Idempotency b.Idempotency
          Restart = worse RestartVisibility.hazardRank a.Restart b.Restart }

    /// Folded from `neutral`; the order does not matter.
    let combineAll (gs: DerivedGuarantees seq) : DerivedGuarantees = Seq.fold combine neutral gs

    /// The narrowest HONEST named triple for a derived posture, or `None` where
    /// the delivery hazards have no name.
    let narrowest (derived: DerivedGuarantees) : RuntimeGuarantees option =
        DeliveryFacet.ofHazards derived.Delivery
        |> Option.map (fun delivery ->
            { Delivery = delivery
              Idempotency = derived.Idempotency
              Restart = derived.Restart })

    /// A named triple, read back as hazards — the direction the consistency
    /// check compares in.
    let derivedOf (guarantees: RuntimeGuarantees) : DerivedGuarantees =
        { Delivery = DeliveryFacet.hazards guarantees.Delivery
          Idempotency = guarantees.Idempotency
          Restart = guarantees.Restart }

    // ─── the per-arm derivation ──────────────────────────────────────────────

    /// The direct interpreter's posture, for any arm.
    ///
    /// Nothing is journaled, so a process that dies mid-handler leaves no record
    /// that anything was attempted: the invocation is lost and a restart cannot
    /// find it. The idempotency axis still varies by arm, because it is a
    /// property of what the arm DOES and needs no substrate at all.
    let private directPosture (idempotency: IdempotencyFacet) : DerivedGuarantees =
        { Delivery = DeliveryFacet.hazards DeliveryFacet.AtMostOnce
          Idempotency = idempotency
          Restart = RestartVisibility.LostOnRestart }

    /// Whether this placement PERFORMS its ops — a registered op performer
    /// (Phase 1967) makes `ApplyOps` an arm that reaches outside, on a host
    /// call's terms. A fact about the registration, passed in rather than
    /// declared, so a host that registers a performer and declares nothing
    /// about it reads as the undeclared performer it is rather than as the
    /// recomputed arm it no longer is.
    let private performsOps (performance: OpPerformance<'Node, 'Op>) : bool =
        match performance with
        | OpPerformance.InMemory -> false
        | OpPerformance.Performed _ -> true

    /// What repeating an arm does, independent of any interpreter.
    ///
    /// A read repeats freely. `ApplyOps` is the placement's one domain-state
    /// mutation and `Notify` ships a message, so both change the result when
    /// repeated — and under a registered op performer `ApplyOps` repeats
    /// whatever the host declared of that performer (Phase 1980), on exactly a
    /// host call's terms. `EmitPatch` pushes ops the caller applies to a tree it
    /// already holds; repeating it is not provably a no-op, so it takes the
    /// mutating answer rather than the flattering one.
    let private intrinsicIdempotency
        (performers: PerformerFacets)
        (performance: OpPerformance<'Node, 'Op>)
        (effect: ServerEffect<'Op>)
        : IdempotencyFacet =
        match effect with
        | ServerEffect.RunQuery _ -> IdempotencyFacet.Idempotent
        | ServerEffect.ApplyOps _ when performsOps performance -> PerformerFacets.opPerformerFacet performers
        | ServerEffect.ApplyOps _
        | ServerEffect.EmitPatch _
        | ServerEffect.Notify _ -> IdempotencyFacet.NonIdempotent
        | ServerEffect.HostCall(fn, _, _) -> PerformerFacets.facetOf fn performers

    /// The posture of an arm that COMMITS OUTSIDE under deterministic replay —
    /// a host call, or a performed op — given what repeating its performer does
    /// and whether the policy re-invokes an undecided step. It cannot avoid both
    /// hazards; see `ofEffect`.
    let private reachingOutside (intrinsic: IdempotencyFacet) (replay: ReplayDiscipline) : DerivedGuarantees =
        match intrinsic, replay.ReinvokeIndeterminate with
        | IdempotencyFacet.Idempotent, _ ->
            { Delivery = DeliveryFacet.hazards DeliveryFacet.ExactlyOnceEffective
              Idempotency = IdempotencyFacet.Idempotent
              Restart = RestartVisibility.SurvivesRestart }
        | IdempotencyFacet.IdempotentWithStore, true ->
            { Delivery = DeliveryFacet.hazards DeliveryFacet.ExactlyOnceEffective
              Idempotency = IdempotencyFacet.IdempotentWithStore
              Restart = RestartVisibility.SurvivesRestart }
        | _, true ->
            { Delivery = DeliveryFacet.hazards DeliveryFacet.AtLeastOnce
              Idempotency = intrinsic
              Restart = RestartVisibility.RetriedAfterRestart }
        | _, false ->
            { Delivery = DeliveryFacet.hazards DeliveryFacet.AtMostOnce
              Idempotency = intrinsic
              Restart = RestartVisibility.LostOnRestart }

    /// **What ONE arm guarantees under ONE interpreter.**
    ///
    /// Two divisions decide every answer, and both are rulings this repository
    /// already made rather than choices taken here.
    ///
    /// **The arms that never reach outside the interpreter** (D8 says so in as
    /// many words: a query reads, an op edits an in-memory tree, and a patch and
    /// a notification accumulate as values the caller performs after the handler
    /// returns) are recomputed from the entry state on every re-run under
    /// deterministic replay and land exactly once — not because anything dedupes
    /// them, but because there was never a second act to dedupe. Their
    /// idempotency is `IdempotentWithStore` and not `Idempotent`, and that is
    /// the honest half: repeating them is safe BECAUSE the journal makes the
    /// recomputation deterministic, which is a claim about substrate.
    ///
    /// **The arms that commit outside — `HostCall`, and `ApplyOps` under a
    /// registered op performer (D23)** — are the ones whose facet depends on the
    /// policy, and they cannot avoid both hazards:
    ///
    ///   * a performer the host declares `Idempotent` closes the indeterminate
    ///     window by its own shape — re-invoking costs nothing — so the arm is
    ///     exactly-once-effective and survives a restart;
    ///   * a performer declared `IdempotentWithStore` closes it too, at the
    ///     receiver, and only where the policy actually re-invokes;
    ///   * otherwise the policy decides which hazard the placement takes.
    ///     Re-invoking may DUPLICATE (`AtLeastOnce`); refusing may LOSE
    ///     (`AtMostOnce`). There is no third option, and a claim of
    ///     exactly-once here would be the inflation this file exists to prevent.
    ///
    /// And a journal that does not survive a restart makes every one of those
    /// distinctions moot: nothing is recorded across the failure the guarantee is
    /// about, so the whole derivation falls back to the direct interpreter's.
    let ofEffect
        (discipline: PlacementDiscipline)
        (performers: PerformerFacets)
        (performance: OpPerformance<'Node, 'Op>)
        (effect: ServerEffect<'Op>)
        : DerivedGuarantees =
        let intrinsic = intrinsicIdempotency performers performance effect

        match discipline with
        | PlacementDiscipline.Direct -> directPosture intrinsic
        | PlacementDiscipline.DeterministicReplay replay when not replay.JournalSurvivesRestart ->
            directPosture intrinsic
        | PlacementDiscipline.DeterministicReplay replay ->
            match effect with
            | ServerEffect.RunQuery _ ->
                { Delivery = DeliveryFacet.hazards DeliveryFacet.ExactlyOnceEffective
                  Idempotency = IdempotencyFacet.Idempotent
                  Restart = RestartVisibility.SurvivesRestart }
            | ServerEffect.ApplyOps _ when performsOps performance -> reachingOutside intrinsic replay
            | ServerEffect.ApplyOps _
            | ServerEffect.EmitPatch _
            | ServerEffect.Notify _ ->
                { Delivery = DeliveryFacet.hazards DeliveryFacet.ExactlyOnceEffective
                  Idempotency = IdempotencyFacet.IdempotentWithStore
                  Restart = RestartVisibility.SurvivesRestart }
            | ServerEffect.HostCall _ -> reachingOutside intrinsic replay

    /// The effects one handler declares, in stage order. A `Compute` stage
    /// reaches no effect vocabulary at all — it is the shared fold, against the
    /// binding store — so it contributes nothing here.
    let effectsOf (handler: Handler<'Action, 'Op>) : ServerEffect<'Op> list =
        handler.Stages
        |> List.choose (fun stage ->
            match stage with
            | Effect effect -> Some effect
            | Compute _ -> None)

    /// What ONE handler guarantees. `performance` is how the placement performs
    /// an op (Phase 1980): the registration fact the `ApplyOps` arm's answer
    /// turns on.
    let ofHandler
        (discipline: PlacementDiscipline)
        (performers: PerformerFacets)
        (performance: OpPerformance<'Node, 'Op>)
        (handler: Handler<'Action, 'Op>)
        : DerivedGuarantees =
        effectsOf handler
        |> List.map (ofEffect discipline performers performance)
        |> combineAll

    /// **What a whole registration guarantees** — the composition's honest facet.
    let ofHandlers
        (discipline: PlacementDiscipline)
        (performers: PerformerFacets)
        (performance: OpPerformance<'Node, 'Op>)
        (handlers: Handler<'Action, 'Op> seq)
        : DerivedGuarantees =
        handlers |> Seq.map (ofHandler discipline performers performance) |> combineAll

    // ─── the declaration, and the check ──────────────────────────────────────

    /// The honest declaration for a registration: derived, never authored.
    ///
    /// `None` where the derivation proves both hazards, because there is then no
    /// triple that says the truth — and returning a plausible one would be
    /// precisely the quiet inflation `checkDeclaration` exists to catch in
    /// somebody else's hand-written value.
    let declare
        (placement: string)
        (logicTree: LogicTreeRef)
        (discipline: PlacementDiscipline)
        (performers: PerformerFacets)
        (performance: OpPerformance<'Node, 'Op>)
        (handlers: Handler<'Action, 'Op> seq)
        : PlacementDeclaration option =
        ofHandlers discipline performers performance handlers
        |> narrowest
        |> Option.map (fun guarantees ->
            { Placement = placement
              LogicTree = logicTree
              Guarantees = guarantees })

    /// Every host function the registration names whose idempotency the host has
    /// not declared, distinct and sorted.
    let undeclaredPerformers (performers: PerformerFacets) (handlers: Handler<'Action, 'Op> seq) : string list =
        handlers
        |> Seq.collect effectsOf
        |> Seq.choose (fun effect ->
            match effect with
            | ServerEffect.HostCall(fn, _, _) when not (PerformerFacets.isDeclared fn performers) -> Some fn
            | _ -> None)
        |> Seq.distinct
        |> Seq.sort
        |> List.ofSeq

    /// Whether the registration performs ops through a performer the host has
    /// not declared (Phase 1980): an op performer is registered, a handler
    /// reaches `ApplyOps`, and nothing is said about repeating it.
    let undeclaredOpPerformer
        (performers: PerformerFacets)
        (performance: OpPerformance<'Node, 'Op>)
        (handlers: Handler<'Action, 'Op> seq)
        : bool =
        performsOps performance
        && not (PerformerFacets.isOpPerformerDeclared performers)
        && (handlers
            |> Seq.collect effectsOf
            |> Seq.exists (fun effect ->
                match effect with
                | ServerEffect.ApplyOps _ -> true
                | _ -> false))

    /// **The end-to-end consistency check.**
    ///
    /// From the handler registration, through the per-arm derivation and the
    /// conjunction, to the triple a composition's logic-tree slot carries: does
    /// the declaration say anything the placement cannot keep.
    ///
    /// Every finding is an INFLATION or a fact about the registration. A
    /// declaration that promises LESS than the derivation raises nothing, and
    /// that asymmetry is the rule rather than an omission: a conservative
    /// promise costs only the promise, and a check that nagged about one would
    /// be a check people configure away.
    ///
    /// The discipline is an argument rather than read from the declaration's own
    /// placement id, because the two are different questions — what a host says
    /// it runs, and what it is actually running — and the check is worth most
    /// when they disagree. `UnknownPlacement` reports the id it cannot resolve
    /// and the check continues against the discipline it was handed.
    let checkDeclaration
        (discipline: PlacementDiscipline)
        (performers: PerformerFacets)
        (performance: OpPerformance<'Node, 'Op>)
        (handlers: Handler<'Action, 'Op> seq)
        (declaration: PlacementDeclaration)
        : FacetFinding list =
        let derived = ofHandlers discipline performers performance handlers
        let declared = derivedOf declaration.Guarantees

        let unknown =
            if List.contains declaration.Placement PlacementId.all then
                []
            else
                [ { Code = FacetCode.UnknownPlacement
                    Capability = None
                    Detail = "the declaration names a placement this package does not serve" } ]

        let delivery =
            match DeliveryFacet.ofHazards derived.Delivery with
            | None ->
                [ { Code = FacetCode.DeliveryUnspellable
                    Capability = None
                    Detail = "the registration may lose AND may duplicate; no delivery facet says both" } ]
            | Some honest ->
                let hides hazard =
                    hazard derived.Delivery && not (hazard declared.Delivery)

                if hides _.MayLose || hides _.MayDuplicate then
                    [ { Code = FacetCode.DeliveryInflated
                        Capability = None
                        Detail =
                          "declared "
                          + DeliveryFacet.tag declaration.Guarantees.Delivery
                          + ", derived "
                          + DeliveryFacet.tag honest } ]
                else
                    []

        let idempotency =
            if IdempotencyFacet.hazardRank declared.Idempotency < IdempotencyFacet.hazardRank derived.Idempotency then
                [ { Code = FacetCode.IdempotencyInflated
                    Capability = None
                    Detail =
                      "declared "
                      + IdempotencyFacet.tag declared.Idempotency
                      + ", derived "
                      + IdempotencyFacet.tag derived.Idempotency } ]
            else
                []

        let restart =
            if RestartVisibility.hazardRank declared.Restart < RestartVisibility.hazardRank derived.Restart then
                [ { Code = FacetCode.RestartInflated
                    Capability = None
                    Detail =
                      "declared "
                      + RestartVisibility.tag declared.Restart
                      + ", derived "
                      + RestartVisibility.tag derived.Restart } ]
            else
                []

        let undeclared =
            undeclaredPerformers performers handlers
            |> List.map (fun fn ->
                { Code = FacetCode.UndeclaredPerformer
                  Capability = Some("host:" + fn)
                  Detail =
                    "no idempotency declared, so the derivation used "
                    + IdempotencyFacet.tag IdempotencyFacet.NonIdempotent })

        // The op performer, on the same terms (Phase 1980): reported under the
        // capability its stages are journaled under, so a host reading
        // "at-most-once" over a handler of nothing but ops finds the line.
        // That capability IS the op performer's registration key (Phase 1983,
        // D26) — the name an operator's revoke withdraws it by — so a finding
        // names every performer by the key a revoke would use: `host:<key>`
        // for a host function, the key itself for the op performer.
        let undeclaredOp =
            if undeclaredOpPerformer performers performance handlers then
                [ { Code = FacetCode.UndeclaredPerformer
                    Capability = Some OpPerformance.RegistrationKey
                    Detail =
                      "no idempotency declared for the registered op performer (registration key '"
                      + OpPerformance.RegistrationKey
                      + "'), so the derivation used "
                      + IdempotencyFacet.tag IdempotencyFacet.NonIdempotent } ]
            else
                []

        unknown @ delivery @ idempotency @ restart @ undeclared @ undeclaredOp

    /// Whether a check found anything that is a REFUSAL rather than a note. The
    /// undeclared-performer line is information; everything else is a promise
    /// the placement cannot keep.
    let isInflation (finding: FacetFinding) : bool =
        finding.Code <> FacetCode.UndeclaredPerformer

    /// Log-safe rendering.
    let describe (finding: FacetFinding) : string =
        match finding.Capability with
        | Some capability -> finding.Code + " (" + capability + ") — " + finding.Detail
        | None -> finding.Code + " — " + finding.Detail

    /// Log-safe rendering of a triple, in the tags that are the cross-boundary
    /// agreement.
    let render (guarantees: RuntimeGuarantees) : string =
        DeliveryFacet.tag guarantees.Delivery
        + "/"
        + IdempotencyFacet.tag guarantees.Idempotency
        + "/"
        + RestartVisibility.tag guarantees.Restart
