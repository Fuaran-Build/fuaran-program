namespace Fuaran.Program.Server

open Fuaran.Program.Bounded

// ============================================================================
//  The undo posture, and the undo run (Phase 1977, DECISIONS.md D22).
//
//  `Replay.fs` beside this file answers "may this handler be re-run" from its
//  declared form. This file answers the other question a deployer asks before
//  a program runs: "if it runs, can it be undone — and up to where?" Four
//  answers, read off the stages and the state witness's `Undo` member:
//
//    reversible    every edit has an EXACT inverse, every compute stage is in
//                  the fold's reversible fragment, nothing reaches the world:
//                  the run can be undone to the byte;
//    compensable   every edit has an inverse or a DECLARED compensation (a
//                  saga): the run can be undone in effect, not in history;
//    one-way       a step has neither — a publish with no retraction, a host
//                  call, a notification — and the first such stage is named:
//                  everything before it can still be undone, nothing after;
//    unknown       a place this classification does not decide: a patch the
//                  host applies after return, a compute stage outside the
//                  reversible fragment.
//
//  ── Why reversible versus compensable is the line that matters ─────────────
//  Both can be undone. The difference is the LAW: an inverse obeys one (K9 —
//  applied to the state the op produced, it restores the state the op was
//  applied to, byte for byte), and a compensation obeys none. So a reversible
//  plan can be CHECKED before anything performs — the inverses are folded
//  against the recorded entry state, and a witness whose inverse breaks the
//  law is refused rather than performed — where a compensable plan reaches
//  whatever its compensations reach, and the posture says so by its name. A
//  deployer who reads `reversible` is being told the undo is verifiable; one
//  who reads `compensable` is being told it is declared.
//
//  ── The run is a HANDLER run ────────────────────────────────────────────────
//  The inverse plan is one `ApplyOps` effect run through `Handler.runWith`:
//  the same gate decides `ApplyOps`, the same argument policy bounds every
//  inverse's reach, the same registered performers perform them, and the
//  same two-phase staging holds — so a failed undo step reports how far it got
//  in the `PerformFailed` vocabulary because `residual_is_prefix` is a
//  theorem about this run too (`undo_residual_is_prefix`, proofs/Undo.fst).
//  Then the compute stages' binding writes are reversed by the fold's own
//  inverse (Phase 1976, `BoundedActions.reverse` over each stage's trace), in
//  reverse stage order. The posture's soundness — a plan read `reversible`
//  is undone to the entry state — is `undo_run_restores`.
//
//  ── What is deliberately NOT undone ─────────────────────────────────────────
//  A server read lands a table in a query slot of the binding store. It is a
//  read: it reaches nothing and the slot is the host's cache, which the
//  re-resolution of a restored tree still reads. The undo leaves it as the
//  run left it, and a read contributes no reason to the posture. A deployer
//  reading `reversible` reads it of the state axis, the world and the fold's
//  writes; a landed read is none of those.
// ============================================================================

/// WHY a handler is not provably reversible — a closed vocabulary of derived
/// facts, on the replay classification's terms: every arm is something the
/// walk demonstrated about a declared form, never a string a document
/// supplied. The model's `defect`.
[<RequireQualifiedAccess>]
type UndoDefect =
    /// An edit whose `Undo` is a declared compensation, not an exact inverse.
    | CompensatedOp
    /// An edit whose `Undo` is neither.
    | OneWayOp
    /// A host call: it commits somewhere this host does not own, and no
    /// inverse vocabulary exists for it.
    | OpaqueHostCall
    /// A notification: it shipped.
    | OutboundNotification
    /// A patch, emitted for the host to apply after return: whether it can be
    /// undone is the host's to say.
    | EmittedPatch
    /// A compute stage outside the fold's reversible fragment (Phase 1976): a
    /// branch with no exit assertion, a parameter bound, a call, a leaf.
    | ComputeOutsideFragment

/// The verdict a handler's reasons carry. The model's `verdict`.
[<RequireQualifiedAccess>]
type UndoVerdict =
    | Reversible
    | Compensable
    | OneWay
    | Unknown

/// One reason, positioned by the stage it was found at — an ORDINAL, never a
/// name, for the replay reason's reason.
type UndoReason = { Stage: int; Defect: UndoDefect }

module UndoCode =

    /// The plan holds a step with no inverse and no compensation: a one-way op,
    /// a host call, a notification. Refused before anything is undone.
    [<Literal>]
    let OneWayStep = "undo-one-way-step"

    /// The plan holds a step this run cannot decide: an emitted patch, or a
    /// compute stage outside the reversible fragment or whose trace cannot be
    /// restored. Refused before anything is undone.
    [<Literal>]
    let UndecidableStep = "undo-undecidable-step"

    /// A plan of exact inverses whose inverses do not fold back to the recorded
    /// entry state: the witness's inverse breaks its law. Refused rather than
    /// performed.
    [<Literal>]
    let InverseDrift = "undo-inverse-drift"

    /// A plan whose run rolled back: there is nothing to undo, and the residual
    /// a perform-phase failure left is the forward outcome's to report.
    [<Literal>]
    let UncommittedPlan = "undo-uncommitted-plan"

/// A refused undo: the code, the ordinal of the step in the plan it was
/// refused at, and the reason — a capability, or the domain's own one-way
/// reason, which is held to a reach's discipline. The model's `refusal`.
type UndoRefusal =
    { Code: string
      Step: int
      Reason: string }

module Undo =

    // ─── the classification ──────────────────────────────────────────────────

    /// The grade one defect forces. Three defects are PROOFS that a step cannot
    /// be undone at all; one that it can be undone only in effect; two are
    /// places this walk does not decide. The model's `grade`.
    let gradeOfDefect (defect: UndoDefect) : UndoVerdict =
        match defect with
        | UndoDefect.CompensatedOp -> UndoVerdict.Compensable
        | UndoDefect.OneWayOp
        | UndoDefect.OpaqueHostCall
        | UndoDefect.OutboundNotification -> UndoVerdict.OneWay
        | UndoDefect.EmittedPatch
        | UndoDefect.ComputeOutsideFragment -> UndoVerdict.Unknown

    /// One-way dominates unknown, which dominates compensable, which dominates
    /// reversible: a proof that a step cannot be undone outranks a place the
    /// walk could not decide, and either outranks a declared compensation. The
    /// model's `worst`.
    let worst (a: UndoVerdict) (b: UndoVerdict) : UndoVerdict =
        match a, b with
        | UndoVerdict.OneWay, _
        | _, UndoVerdict.OneWay -> UndoVerdict.OneWay
        | UndoVerdict.Unknown, _
        | _, UndoVerdict.Unknown -> UndoVerdict.Unknown
        | UndoVerdict.Compensable, _
        | _, UndoVerdict.Compensable -> UndoVerdict.Compensable
        | _ -> UndoVerdict.Reversible

    /// The verdict a defect list carries: no defect is the only proof of
    /// `Reversible`. The model's `verdict_of`.
    let verdictOfDefects (defects: UndoDefect list) : UndoVerdict =
        defects
        |> List.fold (fun acc d -> worst acc (gradeOfDefect d)) UndoVerdict.Reversible

    let verdictOfReasons (reasons: UndoReason list) : UndoVerdict =
        reasons |> List.map _.Defect |> verdictOfDefects

    /// The defects of an op sequence, read through the state witness's view to
    /// exhaustion: an edit's class, a guard nothing, a branch BOTH arms — an
    /// untaken arm still counts, because which arm runs is not decided from
    /// the form — a repeat its body once, an `Each` its LOWERED form (every
    /// element's substituted body, since an op's class may differ once the
    /// element is in it; Phase 1990). The model's `view_defects`.
    let rec defectsOfOps (state: StateWitness<'Node, 'Op>) (ops: 'Op list) : UndoDefect list =
        ops
        |> List.collect (fun op ->
            match state.View op with
            | OpView.Edit ->
                match state.Undo op with
                | UndoClass.Inverse _ -> []
                | UndoClass.Compensate _ -> [ UndoDefect.CompensatedOp ]
                | UndoClass.OneWay _ -> [ UndoDefect.OneWayOp ]
            | OpView.Require -> []
            | OpView.Choose(_, whenTrue, whenFalse, _) -> defectsOfOps state whenTrue @ defectsOfOps state whenFalse
            | OpView.Repeat(_, body) -> defectsOfOps state body
            | OpView.Each(Collection.Literal elements, placeholder, body) ->
                defectsOfOps state (StateWitness.lowered state elements placeholder body)
            // A store-bound collection's elements are not in the tree (Phase
            // 1991): its body is read once, with the placeholder standing —
            // the class of an op whose address is a placeholder is the
            // domain's answer for that shape, and the undo trail records every
            // lowered edit the run actually planned.
            | OpView.Each(Collection.Stored _, _, body) -> defectsOfOps state body
            // A value the state holds (Phase 2186) is not in the tree either:
            // its body is read once, with the placeholder standing, on the
            // store-bound collection's terms.
            | OpView.Let(_, _, body) -> defectsOfOps state body)

    /// The defects of one stage. A compute stage is read through Phase 1976's
    /// reversible fragment; a read lands a table and reaches nothing; the op
    /// arm reads the member; a host call and a notification reached the
    /// world; a patch is the host's to apply. The model's `stage_defects`.
    let defectsOfStage
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (stage: HandlerStage<'Action, 'Op>)
        : UndoDefect list =
        match stage with
        | Compute action ->
            if BoundedActions.reversible witness action then
                []
            else
                [ UndoDefect.ComputeOutsideFragment ]
        | Effect effect ->
            match effect with
            | ServerEffect.RunQuery _ -> []
            | ServerEffect.ApplyOps ops -> defectsOfOps witness.State ops |> List.distinct
            | ServerEffect.HostCall _ -> [ UndoDefect.OpaqueHostCall ]
            | ServerEffect.EmitPatch _ -> [ UndoDefect.EmittedPatch ]
            | ServerEffect.Notify _ -> [ UndoDefect.OutboundNotification ]

    /// Every reason a handler is not provably reversible, in stage order —
    /// distinct within a stage, as the replay reasons are. The model's
    /// `reasons`.
    let reasons
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handler: Handler<'Action, 'Op>)
        : UndoReason list =
        handler.Stages
        |> List.mapi (fun index stage ->
            defectsOfStage witness stage
            |> List.map (fun defect ->
                { UndoReason.Stage = index
                  Defect = defect }))
        |> List.concat

    /// A handler's undo posture, DERIVED from its declared form and the state
    /// witness's member, before it runs. Never declared beside the handler.
    /// The model's `posture`; `undo_run_restores` is what makes `Reversible`
    /// a claim about the run.
    let posture
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handler: Handler<'Action, 'Op>)
        : UndoVerdict =
        reasons witness handler |> verdictOfReasons

    /// The wire spelling of a verdict — a stable token, carried in the demanded
    /// document.
    let verdictTag (verdict: UndoVerdict) : string =
        match verdict with
        | UndoVerdict.Reversible -> "reversible"
        | UndoVerdict.Compensable -> "compensable"
        | UndoVerdict.OneWay -> "one-way"
        | UndoVerdict.Unknown -> "unknown"

    /// The wire spelling of a defect.
    let defectTag (defect: UndoDefect) : string =
        match defect with
        | UndoDefect.CompensatedOp -> "compensated-op"
        | UndoDefect.OneWayOp -> "one-way-op"
        | UndoDefect.OpaqueHostCall -> "opaque-host-call"
        | UndoDefect.OutboundNotification -> "outbound-notification"
        | UndoDefect.EmittedPatch -> "emitted-patch"
        | UndoDefect.ComputeOutsideFragment -> "compute-outside-fragment"

    // ─── the projection join ─────────────────────────────────────────────────

    /// One handler's undo posture, as the demanded-projection document carries
    /// it (version 6).
    let postureOf
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handler: Handler<'Action, 'Op>)
        : UndoPosture =
        let reasons = reasons witness handler

        { Handler = handler.Name
          Undo = verdictTag (verdictOfReasons reasons)
          Reasons =
            reasons
            |> List.map (fun r ->
                { UndoReasonDemand.Stage = r.Stage
                  Defect = defectTag r.Defect }) }

    /// Join these handlers' undo postures onto a projection's server tier, on
    /// `Replay.withPostures`'s terms: a projection with NO server tier is
    /// returned unchanged, because attaching a posture would turn "not asked"
    /// into "asked".
    let withPostures
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handlers: Handler<'Action, 'Op> seq)
        (projection: DemandedProjection)
        : DemandedProjection =
        match projection.Server with
        | None -> projection
        | Some server ->
            let postures = handlers |> Seq.map (postureOf witness) |> List.ofSeq

            Demanded.withServer
                { server with
                    Undo = server.Undo @ postures }
                projection

    /// The demanded projection for a tree and the handler registration behind
    /// it, with each reachable handler's replay AND undo postures joined on —
    /// both from the same reachability, so the document cannot describe one
    /// handler set in its capabilities and another in either posture.
    let ofTreeAndHandlers
        (witness: FullWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (handlers: Map<string, Handler<'Action, 'Op>>)
        (root: 'Node)
        : DemandedProjection =
        Replay.ofTreeAndHandlers witness handlers root
        |> withPostures witness (ServerDemanded.reachable witness handlers root)

    // ─── the undo run ────────────────────────────────────────────────────────

    /// The first step of the plan the undo cannot perform, with its ordinal: a
    /// one-way op, a step that reached the world, a compute stage outside the
    /// reversible fragment or whose trace cannot be restored, an emitted
    /// patch. `None` when every step can be undone. The model's
    /// `first_refused`.
    let firstRefused
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (steps: UndoStep<'Node, 'Op, 'Action> list)
        : UndoRefusal option =
        steps
        |> List.indexed
        |> List.tryPick (fun (k, step) ->
            match step with
            | UndoStep.Edit(_, op) ->
                match witness.State.Undo op with
                | UndoClass.OneWay reason ->
                    Some
                        { Code = UndoCode.OneWayStep
                          Step = k
                          Reason = reason }
                | UndoClass.Inverse _
                | UndoClass.Compensate _ -> None
            | UndoStep.Compute(action, trace) ->
                if BoundedActions.reversible witness action && Trace.restorable trace then
                    None
                else
                    Some
                        { Code = UndoCode.UndecidableStep
                          Step = k
                          Reason = "a compute stage outside the reversible fragment, or whose trace is not restorable" }
            | UndoStep.Reached capability ->
                Some
                    { Code = UndoCode.OneWayStep
                      Step = k
                      Reason = capability }
            | UndoStep.Emitted capability ->
                Some
                    { Code = UndoCode.UndecidableStep
                      Step = k
                      Reason = capability })

    /// The inverse PLAN: over the steps in REVERSE plan order — the last edit
    /// first — each edit's inverse or compensation computed from the pre-state
    /// recorded with it, concatenated. The one op sequence the undo performs.
    /// A one-way edit contributes nothing; `firstRefused` reaches it first.
    /// The model's `inverse_plan`.
    let inversePlan (state: StateWitness<'Node, 'Op>) (steps: UndoStep<'Node, 'Op, 'Action> list) : 'Op list =
        steps
        |> List.rev
        |> List.collect (fun step ->
            match step with
            | UndoStep.Edit(pre, op) ->
                match state.Undo op with
                | UndoClass.Inverse inverse -> inverse pre
                | UndoClass.Compensate compensation -> compensation pre
                | UndoClass.OneWay _ -> []
            | UndoStep.Compute _
            | UndoStep.Reached _
            | UndoStep.Emitted _ -> [])

    /// Each op applied in turn through the witness's apply — no gate, no view,
    /// no performer: the pure fold the drift check reads. The model's
    /// `apply_all`.
    let foldInverses (state: StateWitness<'Node, 'Op>) (ops: 'Op list) (tree: 'Node) : Result<'Node, string> =
        ops
        |> List.fold (fun acc op -> acc |> Result.bind (state.Stream.Apply op)) (Ok tree)

    /// Whether every edit of the plan has an EXACT inverse — the plan the
    /// drift check applies to. The model's `all_inverse`.
    let private allInverse (state: StateWitness<'Node, 'Op>) (steps: UndoStep<'Node, 'Op, 'Action> list) : bool =
        steps
        |> List.forall (fun step ->
            match step with
            | UndoStep.Edit(_, op) ->
                match state.Undo op with
                | UndoClass.Inverse _ -> true
                | UndoClass.Compensate _
                | UndoClass.OneWay _ -> false
            | UndoStep.Compute _
            | UndoStep.Reached _
            | UndoStep.Emitted _ -> true)

    /// The compute stages' binding writes reversed, in reverse stage order, by
    /// the fold's own inverse (Phase 1976): each stage's `reverse` over its
    /// trace, folded by `runReversed` on the bindings the later stages' undo
    /// left. The model's `restore`, whose restorer is this. A reversed fold's
    /// diagnostics are returned beside the bindings, so a reversal that
    /// halted is reported rather than silent.
    let restoreBindings
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (nodeId: string)
        (steps: UndoStep<'Node, 'Op, 'Action> list)
        (bindings: 'Store)
        : 'Store * ServerDiagnostic list =
        steps
        |> List.rev
        |> List.fold
            (fun (bindings, diagnostics) step ->
                match step with
                | UndoStep.Compute(action, trace) ->
                    let reversed = BoundedActions.reverse witness action trace
                    let outcome = BoundedActions.runReversed witness nodeId reversed bindings

                    outcome.Store, diagnostics @ (outcome.Diagnostics |> List.map ServerDiagnostic.Bounded)
                | UndoStep.Edit _
                | UndoStep.Reached _
                | UndoStep.Emitted _ -> bindings, diagnostics)
            (bindings, [])

    /// UNDO a committed run, through the same registry, performance and
    /// resolver the run went through, against the store it left. In this
    /// order: a plan that did not commit is refused (nothing to undo); the
    /// first step the undo cannot perform is refused, NAMED, before anything
    /// is undone; a plan of exact inverses is checked to fold back to the
    /// recorded entry state before anything performs; then the inverse plan
    /// runs as ONE `ApplyOps` effect through `Handler.runWith` — the gate, the
    /// argument policy, the registered performers and the two-phase staging
    /// the forward run went through — and, on commit, the compute stages'
    /// binding writes are reversed in reverse stage order. The outcome is the
    /// undo's own handler outcome: `Performed` is the inverses that ran, and a
    /// `PerformFailed` names the one that did not, with the ones before it
    /// run and not taken back — the perform-failure vocabulary, because this
    /// IS a handler run (`undo_residual_is_prefix`). The model's `undo_run`.
    let run
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (registry: ServerEffectRegistry)
        (performance: OpPerformance<'Node, 'Op>)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Compute.EvalError>)
        (nodeId: string)
        (plan: UndoPlan<'Node, 'Op, 'Action>)
        (post: ServerStore<'Node, 'Store>)
        : Result<HandlerOutcome<'Node, 'Store, 'Op, 'Effect>, UndoRefusal> =
        if not plan.Committed then
            Error
                { Code = UndoCode.UncommittedPlan
                  Step = 0
                  Reason = "the run rolled back" }
        else
            match firstRefused witness plan.Steps with
            | Some refusal -> Error refusal
            | None ->
                let state = witness.State
                let inverses = inversePlan state plan.Steps

                let drifted =
                    allInverse state plan.Steps
                    && (match foldInverses state inverses post.Tree with
                        | Error _ -> true
                        | Ok restored -> state.Canonical restored <> state.Canonical plan.Entry)

                if drifted then
                    Error
                        { Code = UndoCode.InverseDrift
                          Step = 0
                          Reason = "the declared inverses do not fold back to the recorded entry state" }
                else
                    let undoing: Handler<'Action, 'Op> =
                        { Name = plan.Handler
                          Stages = [ Effect(ServerEffect.ApplyOps inverses) ] }

                    let outcome =
                        Handler.runWith witness registry performance resolve nodeId undoing post

                    if outcome.Committed then
                        let bindings, diagnostics =
                            restoreBindings witness nodeId plan.Steps outcome.Store.Bindings

                        Ok
                            { outcome with
                                Store =
                                    { outcome.Store with
                                        Bindings = bindings }
                                Diagnostics = outcome.Diagnostics @ diagnostics }
                    else
                        Ok outcome

    /// Human-readable, log-safe rendering of a refusal: the code, the step's
    /// ordinal and the reason — a capability, or the domain's own reason.
    let describe (refusal: UndoRefusal) : string =
        refusal.Code + "@" + string refusal.Step + ": " + refusal.Reason
