namespace Fuaran.Program.Server

open Fuaran.UI.Types
open Fuaran.Program.Bounded

// ============================================================================
//  Replay modes, and what each admits.
//
//  The derivation beside this file answers "is this handler provably
//  re-runnable, and if not, why". That is a fact about a handler. This file is
//  the DECISION that consumes it: given a mode a host has named and a handler it
//  would like to resume, what is it admitted to do.
//
//  ── Why a mode is named rather than inferred ────────────────────────────────
//  Reconstructing what a session DID and re-deriving reads so a session can
//  CONTINUE are both legitimate, they cannot both be the default, and state
//  reconstructed under one may legitimately differ from the other. So a host
//  says which it is in, and a consumer is never handed a document produced
//  under one mode as though it came from the other.
//
//  ── What is enforced here, and what is not ──────────────────────────────────
//  This is the admission decision, not a resume engine. It decides — before
//  anything runs, from data alone — whether a handler may be resumed and what
//  the resumer is permitted to re-evaluate. Performing the re-evaluation is the
//  session's job and is not modelled here, deliberately: the obligation the
//  algebra owns is the REFUSAL and the mode distinction, and a decision function
//  that also ran things could not be used to ask the question without answering
//  it.
//
//  Two properties are worth stating because they are what the tests pin:
//
//    AUDIT NEVER CONSULTS A HANDLER. `admit` in audit mode does not read the
//    handler it is passed, at all. That is not an optimisation — an audit replay
//    is effect-free UNCONDITIONALLY, whatever the handlers involved declared,
//    and a decision that consulted a classification would be one a future edit
//    could make conditional. There is nothing here for such an edit to reach.
//
//    ONLY `unsafe` IS REFUSED. `unknown` resumes, carrying its reasons. Refusing
//    it would round an honest "no proof available" up to a proof of harm, which
//    is precisely the move the three-valued classification exists to prevent —
//    and it would fire on ordinary correct handlers, until the refusal became
//    something people configure away. A host that wants to be stricter has the
//    reasons in hand and can be; the algebra does not decide that for it.
// ============================================================================

/// Which replay a host is in. Named, never inferred.
[<RequireQualifiedAccess>]
type ReplayMode =
    /// Reconstruct what happened: apply the recorded ops, in recorded order, and
    /// nothing else. Effect-free unconditionally.
    | Audit
    /// Re-derive reads so a session can continue against current data.
    | Resume

module ReplayCode =

    /// The one refusal this decision raises. One code and not a family: the
    /// obligation is a single rule about a single value, and a second code for
    /// `unknown` would be a refusal the rule does not authorise.
    [<Literal>]
    let ResumeUnsafeHandler = "resume-replay-unsafe-handler"

/// A refused resume, typed.
///
/// The code is a stable token; the reasons say which stages forced it. Neither
/// carries a string the handler document supplied, so a refusal is as log-safe
/// as the classification behind it.
type ReplayRefusal =
    { Code: string
      Safety: ReplaySafety
      Reasons: ReplayReason list }

/// A resume that proceeded on a handler the classification refused.
///
/// The host may be configured to accept re-execution — and where it is, it MUST
/// record that it did. This record is that obligation as a value, so the
/// recording is something a caller RECEIVES rather than something it is trusted
/// to remember: an override that produced no record would be indistinguishable
/// afterwards from a handler that had been safe all along, which is the one
/// reading this whole classification exists to prevent.
type ReplayOverrideRecord =
    { Handler: string
      Safety: ReplaySafety
      Reasons: ReplayReason list }

/// What a host is admitted to do.
[<RequireQualifiedAccess>]
type ReplayAdmission =
    /// Apply the recorded ops and nothing else. No handler runs, no read is
    /// evaluated, no host call is issued, no notification ships, no patch is
    /// emitted that was not itself recorded.
    | OpsOnly
    /// Apply the recorded ops, and re-evaluate this handler's READ stages —
    /// those and nothing else. The reasons are the classification's, carried
    /// through: empty for a `safe` handler, non-empty for an `unknown` one and
    /// for one admitted under an override.
    | ReEvaluateReads of ReplayReason list
    /// The resume is refused.
    | Refused of ReplayRefusal

/// The whole answer: what is admitted, and the record an override obliges.
///
/// `Record` is `Some` exactly when a refusal was overridden. It is a separate
/// field rather than a case of the admission because the two are independent
/// facts — what the resumer may do, and what the host must write down — and
/// folding them together would let a caller act on the first while pattern
/// matching past the second.
type ReplayDecision =
    { Admission: ReplayAdmission
      Record: ReplayOverrideRecord option }

/// Whether the host has been explicitly configured to accept re-execution of a
/// handler the classification refuses.
///
/// A record with one field rather than a bare boolean, so that a call site reads
/// as a policy a host declared rather than as a flag someone passed, and so that
/// a later policy question extends it without changing every signature.
type ReplayPolicy = { AcceptUnsafeResume: bool }

module Replay =

    /// The default policy: an `unsafe` handler is not resumed. Explicit
    /// configuration is the whole of what makes the other one legitimate, so the
    /// permissive value is never the one a caller gets by saying nothing.
    let strict: ReplayPolicy = { AcceptUnsafeResume = false }

    /// The explicitly-configured policy. Naming it is the configuration —
    /// reaching for this value is the deliberate act the obligation to record
    /// attaches to.
    let acceptingUnsafeResume: ReplayPolicy = { AcceptUnsafeResume = true }

    /// What a host in `mode` is admitted to do with `handler`.
    ///
    /// Total, allocation-light, and decided entirely from the declared form: no
    /// store is read, no effect is performed, and the handler is not run.
    let admit (mode: ReplayMode) (policy: ReplayPolicy) (handler: Handler) : ReplayDecision =
        match mode with
        // The handler is deliberately NOT consulted — see the header.
        | ReplayMode.Audit ->
            { Admission = ReplayAdmission.OpsOnly
              Record = None }
        | ReplayMode.Resume ->
            let reasons = HandlerWire.replayReasons handler
            let safety = ProgramWire.verdictOfReasons reasons

            match safety with
            | ReplaySafety.Safe
            | ReplaySafety.Unknown ->
                { Admission = ReplayAdmission.ReEvaluateReads reasons
                  Record = None }
            | ReplaySafety.Unsafe when policy.AcceptUnsafeResume ->
                { Admission = ReplayAdmission.ReEvaluateReads reasons
                  Record =
                    Some
                        { Handler = handler.Name
                          Safety = safety
                          Reasons = reasons } }
            | ReplaySafety.Unsafe ->
                { Admission =
                    ReplayAdmission.Refused
                        { Code = ReplayCode.ResumeUnsafeHandler
                          Safety = safety
                          Reasons = reasons }
                  Record = None }

    /// The decision for each of a set of handlers, paired with its registration
    /// key — what a host resuming a session asks once, before resuming any of
    /// them.
    let admitAll (mode: ReplayMode) (policy: ReplayPolicy) (handlers: Handler seq) : (string * ReplayDecision) list =
        handlers |> Seq.map (fun h -> h.Name, admit mode policy h) |> List.ofSeq

    /// Human-readable, log-safe rendering of a decision.
    ///
    /// Every string it can emit is a literal here or a token from the closed
    /// defect vocabulary. The handler's registration key appears only in the
    /// override record, where it is the whole point of the record, and it is an
    /// author-declared name of the same class as an endpoint.
    let describe (decision: ReplayDecision) : string =
        let reasonText (reasons: ReplayReason list) =
            reasons
            |> List.map (fun r -> "stage " + string r.Stage + ": " + ProgramWire.replayDefectTag r.Defect)
            |> String.concat "; "

        match decision.Admission with
        | ReplayAdmission.OpsOnly -> "audit-replay: recorded ops only"
        | ReplayAdmission.ReEvaluateReads [] -> "resume-replay: reads re-evaluated (safe)"
        | ReplayAdmission.ReEvaluateReads reasons ->
            match decision.Record with
            | Some record ->
                "resume-replay: reads re-evaluated under an explicit override of "
                + ProgramWire.replaySafetyTag record.Safety
                + " — "
                + reasonText reasons
            | None -> "resume-replay: reads re-evaluated, undecided — " + reasonText reasons
        | ReplayAdmission.Refused refusal ->
            refusal.Code
            + " ("
            + ProgramWire.replaySafetyTag refusal.Safety
            + ") — "
            + reasonText refusal.Reasons

    // ─── the projection join ─────────────────────────────────────────────────

    /// One handler's posture, as the demanded-projection document carries it.
    let postureOf (handler: Handler) : ReplayPosture =
        let reasons = HandlerWire.replayReasons handler

        { Handler = handler.Name
          Safety = ProgramWire.replaySafetyTag (ProgramWire.verdictOfReasons reasons)
          Reasons =
            reasons
            |> List.map (fun r ->
                { ReplayReasonDemand.Stage = r.Stage
                  Defect = ProgramWire.replayDefectTag r.Defect }) }

    /// Join these handlers' postures onto a projection's server tier.
    ///
    /// A projection with NO server tier is returned unchanged. That is not a
    /// silent drop: `None` on the tier means no server walk was performed, and
    /// attaching a posture would turn "not asked" into "asked" — the one reading
    /// the field's own contract forbids. Use `ofTreeAndHandlers` below, which
    /// walks and joins together and so cannot reach that state.
    let withPostures (handlers: Handler seq) (projection: DemandedProjection) : DemandedProjection =
        match projection.Server with
        | None -> projection
        | Some server ->
            let postures = handlers |> Seq.map postureOf |> List.ofSeq

            Demanded.withServer
                { server with
                    Replay = server.Replay @ postures }
                projection

    /// **The one call**: the demanded projection for a tree and the handler
    /// registration behind it, with each reachable handler's replay posture
    /// joined on.
    ///
    /// The posture set and the demand set are computed from the SAME
    /// reachability, through the same function, so the document cannot describe
    /// one set of handlers in its capabilities and another in its postures.
    let ofTreeAndHandlers (handlers: Map<string, Handler>) (root: Node<obj>) : DemandedProjection =
        ServerDemanded.ofTreeAndHandlers handlers root
        |> withPostures (ServerDemanded.reachable handlers root)
