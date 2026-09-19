namespace Fuaran.Program.Server

open Fuaran.Program.Bounded

// ============================================================================
//  Denial-pattern detection — the intrusion signal this placement already
//  records.
//
//  A program that repeatedly demands effects outside its own envelope is
//  behaving exactly as a breach does: probing for a way through. Every one of
//  those demands is ALREADY recorded — `ServerEffectRegistry.OnDenied` fires for
//  every refusal, at both arms, payload-free — so the signal exists and nothing
//  was watching it. This file is the watcher, and it is deliberately nothing
//  else: it adds no wire vocabulary, no package or project dependency, and no
//  second denial stream. It consumes the sink, and it acts through the control
//  vocabulary `Journal.fs` already ships.
//
//  ── The class that makes the signal worth acting on ─────────────────────────
//  A single refused effect is not an intrusion. A program that legitimately
//  names one capability this host declines to serve is a program meeting a
//  policy, which is the system working. What is NOT ordinary is a burst of
//  denials naming capabilities the program's own demanded envelope never
//  claimed — because the envelope is what the program said it would ask for,
//  signed and verified before load (D15), and reaching past it is a statement
//  about intent that no single denial makes.
//
//  So denials are classified against the session's `DemandedProjection`:
//
//    ENVELOPE-OUTSIDE  the capability is absent from the demanded document's
//                      server tier. Counted against the threshold.
//    ORDINARY          the capability IS in the envelope and the host refused
//                      this use of it. Counted for context, never a trigger.
//
//  ── Two shapes of "no envelope", and both read as ORDINARY ──────────────────
//  A detector wired with no envelope at all classifies every denial as
//  ordinary, and so does one wired with a projection whose `Server` tier is
//  `None`. The second is the load-bearing one: `None` there means NO SERVER WALK
//  WAS PERFORMED — the document describes a tree and nothing else — and an empty
//  tier means a walk ran and found nothing. Reading the first as "demands
//  nothing, therefore everything is outside" would make every denial on every
//  client-tier document an intrusion signal, which is precisely the reading
//  `DemandedProjection.Server`'s own note forbids: "not asked" is not "asked,
//  and the answer was nothing". A detector that cannot tell what the program
//  claimed reports nothing rather than reporting everything.
//
//  ── Report before act, and suspension is opted INTO ─────────────────────────
//  The report is emitted before any action, every time, so a host that wires
//  only a log sees the pattern whether or not the session is stopped — and a
//  host running in report-only mode sees exactly what a suspending one would
//  have acted on. `create` is report-only; `suspending` is the deliberate act
//  that gives the detector a hand. That direction is the same default-deny
//  posture the registry itself takes: the consequential behaviour is named, not
//  inherited.
//
//  ── The window is CONSUMED by a breach ──────────────────────────────────────
//  Firing resets the envelope-outside count. A detector that latched instead
//  would be silent forever after its first breach, so a session resumed by an
//  operator (`ControlOp.Resume`) could probe indefinitely without tripping it
//  again. Consuming the window means a SECOND burst is a second breach. The
//  ordinary count is not reset: it is cumulative context on the report, and it
//  never triggers anything.
//
//  ── What this detector does NOT claim ───────────────────────────────────────
//  The suspension takes effect where every control does — at the next dispatch,
//  through the session's own G1 gate (D16) — so the invocation that breached is
//  not unwound. Stages already planned in it continue to meet the host's own
//  gate and are still counted.
//
//  And the counter lives in the registry `watching` returns, so its window is
//  the lifetime of that value. A host wires one detector per SESSION — which is
//  what `ControlServices.Scope` already names — and a wrapped registry shared
//  across sessions would be sharing a threshold between them.
//
//  A capability name is safe to record here for the reason `ServerEffect.fs`
//  states for the denial itself: a handler's stages are registered by the HOST,
//  so a capability, a host-function name included, is the host's own vocabulary
//  and never a string a generated tree supplied.
// ============================================================================

/// How a denial was classified against the session's demanded envelope.
[<RequireQualifiedAccess>]
type DenialClass =
    /// The capability is one the program's own demanded envelope never named —
    /// the class a burst of which is the intrusion signal.
    | EnvelopeOutside
    /// The capability IS in the envelope; this host refused this use of it. A
    /// program meeting a policy, which is not news.
    | Ordinary

module DenialClass =

    /// The class's log-safe tag.
    let tag (denialClass: DenialClass) : string =
        match denialClass with
        | DenialClass.EnvelopeOutside -> "envelope-outside"
        | DenialClass.Ordinary -> "ordinary"

/// The pattern a breach names: which session, how many, against what threshold,
/// and which capabilities were reached for.
///
/// Always about the `EnvelopeOutside` class — that is what a threshold is
/// counted over — so it carries no class member. `Ordinary` travels beside it as
/// context: a reader deciding whether this is a probe or a misconfigured program
/// wants to know whether the session was also being refused things it did claim.
type DenialPattern =
    {
        /// The session the denials were counted for — `ControlServices.Scope`.
        Session: string
        /// The threshold this crossed, carried so the report is readable without
        /// the configuration that produced it.
        Threshold: int
        /// Envelope-outside denials counted in the window that breached.
        EnvelopeOutside: int
        /// Ordinary policy denials seen in this session so far. Cumulative
        /// context, never a trigger.
        Ordinary: int
        /// The capabilities the envelope-outside denials named, distinct and
        /// sorted, so two reports of the same pattern read identically.
        Capabilities: string list
    }

module DenialPattern =

    /// The pattern as one log-safe line. This is also what is recorded as the
    /// suspend's REASON, so the act on the control stream names the pattern that
    /// caused it rather than merely citing a detector.
    let describe (pattern: DenialPattern) : string =
        sprintf
            "denial pattern: %d envelope-outside denial(s) on session '%s' against a threshold of %d (%d ordinary policy denial(s) alongside); capabilities outside the envelope: %s"
            pattern.EnvelopeOutside
            pattern.Session
            pattern.Threshold
            pattern.Ordinary
            (String.concat ", " pattern.Capabilities)

/// What the detector does once it has reported a breach.
[<RequireQualifiedAccess>]
type DenialResponse =
    /// Report, and never suspend. The default.
    | ReportOnly
    /// Report, and then suspend the session through the control stream.
    | Suspend

/// A detector over one session's denial sink.
///
/// Declarative configuration, on the same terms as the registry it watches: the
/// counting state is not here but in the registry `DenialDetector.watching`
/// returns, so this value can be built, read back and compared without holding
/// anything a run mutates.
type DenialDetector =
    {
        /// The session's control stream and scope. The scope is also the session
        /// the threshold is counted for, so the two can never disagree.
        Controls: ControlServices
        /// What the program claimed it would ask for. `None` — and a projection
        /// whose `Server` tier is `None` — classifies every denial as ordinary;
        /// see the header.
        Envelope: DemandedProjection option
        /// How many envelope-outside denials constitute a pattern. Advisory and
        /// tunable; a value below 1 is read as 1, since a threshold of 0 would
        /// breach before any denial arrived.
        Threshold: int
        /// Who the suspend is recorded as. A machine raiser by default, which is
        /// exactly the distinction D16's actor exists to carry.
        Actor: ControlActor
        /// Whether a breach suspends. Report-only unless deliberately changed.
        Response: DenialResponse
        /// The report sink, fired before any action.
        OnPattern: DenialPattern -> unit
        /// The indeterminate windows open when a breach is recorded, asked for
        /// at the moment of the act rather than asserted in advance.
        ///
        /// The default answers `[]`, which records the same entry a plain
        /// suspend does. A host holding an effect journal supplies
        /// `Controls.midStageWindows` over its live invocations — the detector
        /// fires from inside an invocation, so a window CAN be open, and D12's
        /// whole point is that recording it with the suspend is the difference
        /// between a resume that knows it is crossing an undecidable step and
        /// one that finds out afterwards.
        MidStage: unit -> MidStageWindow list
    }

module DenialDetector =

    /// The raiser a detector records its acts as, unless a host names another.
    let defaultActor: ControlActor = ControlActor.machine "denial-patterns"

    /// Three, and the reason is the estate's own: two of anything is a
    /// coincidence, three is a pattern. Advisory — a host with a noisier program
    /// or a sharper appetite moves it with `withThreshold`.
    [<Literal>]
    let DefaultThreshold = 3

    /// A report-only detector over a session's controls, with no envelope and so
    /// nothing it can call outside one. Both halves are supplied deliberately:
    /// `withEnvelope` is what gives it a class to detect, and `suspending` is
    /// what gives it a hand.
    let create (controls: ControlServices) : DenialDetector =
        { Controls = controls
          Envelope = None
          Threshold = DefaultThreshold
          Actor = defaultActor
          Response = DenialResponse.ReportOnly
          OnPattern = ignore
          MidStage = fun () -> [] }

    /// Give the detector the document the program's demands were checked
    /// against — the same projection the session was admitted on.
    let withEnvelope (projection: DemandedProjection) (detector: DenialDetector) : DenialDetector =
        { detector with
            Envelope = Some projection }

    let withThreshold (threshold: int) (detector: DenialDetector) : DenialDetector =
        { detector with
            Threshold = max 1 threshold }

    let withActor (actor: ControlActor) (detector: DenialDetector) : DenialDetector = { detector with Actor = actor }

    /// Set the report sink.
    let onPattern (sink: DenialPattern -> unit) (detector: DenialDetector) : DenialDetector =
        { detector with OnPattern = sink }

    /// Supply the indeterminate windows a breach should close over.
    let withMidStage (windows: unit -> MidStageWindow list) (detector: DenialDetector) : DenialDetector =
        { detector with MidStage = windows }

    /// Report a breach AND suspend the session. The deliberate act.
    let suspending (detector: DenialDetector) : DenialDetector =
        { detector with
            Response = DenialResponse.Suspend }

    /// Report a breach and nothing else — the default, named so a host can
    /// return to it explicitly.
    let reportOnly (detector: DenialDetector) : DenialDetector =
        { detector with
            Response = DenialResponse.ReportOnly }

    /// The capability a denial names. Both arms carry one, and it is the only
    /// thing either carries.
    let private capabilityOf (denial: ServerEffectDenial) : string =
        match denial with
        | ServerEffectDenial.Unregistered capability
        | ServerEffectDenial.GateRefused capability -> capability

    /// What the envelope claimed, as the gate-facing capability set — or `None`
    /// when no server walk stands behind it.
    ///
    /// Read through `ServerDemanded.demandedCapabilities`, the same function the
    /// argument policy is joined by, so a capability this detector calls
    /// "outside" and one the document reports as demanded cannot be two
    /// enumerations of one vocabulary.
    let private declaredCapabilities (projection: DemandedProjection option) : Set<string> option =
        projection
        |> Option.bind _.Server
        |> Option.map (ServerDemanded.demandedCapabilities >> Set.ofList)

    /// Classify one denial against this detector's envelope.
    let classify (detector: DenialDetector) (denial: ServerEffectDenial) : DenialClass =
        match declaredCapabilities detector.Envelope with
        | None -> DenialClass.Ordinary
        | Some declared ->
            if declared.Contains(capabilityOf denial) then
                DenialClass.Ordinary
            else
                DenialClass.EnvelopeOutside

    /// **Watch a registry's denials.**
    ///
    /// Returns the registry with its sink COMPOSED, never replaced: the host's
    /// own `OnDenied` is called first and unchanged, so wiring a detector can
    /// never cost a host the logging it already had. The same shape, and for the
    /// same reason, as `Controls.apply`'s own wrapping.
    let watching (detector: DenialDetector) (registry: ServerEffectRegistry) : ServerEffectRegistry =
        let threshold = max 1 detector.Threshold
        let mutable outside = 0
        let mutable ordinary = 0
        // Reverse order, deduplicated only when a pattern is built — a denial
        // arrives one at a time and the report is built once.
        let mutable reached: string list = []

        let onDenied (denial: ServerEffectDenial) =
            registry.OnDenied denial

            match classify detector denial with
            | DenialClass.Ordinary -> ordinary <- ordinary + 1
            | DenialClass.EnvelopeOutside ->
                outside <- outside + 1
                reached <- capabilityOf denial :: reached

                if outside >= threshold then
                    let pattern =
                        { Session = detector.Controls.Scope
                          Threshold = threshold
                          EnvelopeOutside = outside
                          Ordinary = ordinary
                          Capabilities = reached |> List.distinct |> List.sort }

                    // The report, before any action and under either response —
                    // warn first, so report-only and suspending modes agree
                    // about what was seen and differ only in what was done.
                    detector.OnPattern pattern

                    match detector.Response with
                    | DenialResponse.ReportOnly -> ()
                    | DenialResponse.Suspend ->
                        detector.MidStage()
                        |> Controls.suspendMidStage detector.Actor (DenialPattern.describe pattern)
                        |> DurableControls.record detector.Controls
                        |> ignore

                    // The window is consumed, not latched — see the header.
                    outside <- 0
                    reached <- []

        { registry with OnDenied = onDenied }
