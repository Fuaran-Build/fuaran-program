namespace Fuaran.Program.Server

open Fuaran.Program.Bounded

// ============================================================================
//  The EFFECT JOURNAL — the durable-execution contract, as a port.
//
//  This file names a CONTRACT, never an engine. Durable execution is a
//  well-understood discipline with several implementations, and what they share
//  is exactly two obligations:
//
//    DETERMINISTIC REPLAY   re-running a program from its entry state
//                           reproduces every step it took.
//    EFFECT JOURNALING      the steps a re-run cannot reproduce — the ones
//                           whose result came from outside — are RECORDED, and
//                           a re-run reads the record instead of repeating the
//                           step.
//
//  Nothing below names a product, a protocol or a hosted service, and nothing
//  should be added that does: the whole value of writing the contract down is
//  that a host may satisfy it with a table, a log file, or somebody else's
//  workflow engine, and this placement cannot tell which.
//
//  ── Two records per step, not one ───────────────────────────────────────────
//  A step is journaled TWICE: `Attempted` before the effect runs and
//  `Completed` / `Refused` after it returns. One record would be enough if a
//  crash could not land between the effect and the write — and that is exactly
//  where a crash can land, because the effect commits in a system this host does
//  not own and the journal is a second system.
//
//  So the three readable states of a step are:
//
//    nothing recorded          the effect never ran. Run it.
//    `Attempted` + a result    the effect ran and its answer is known. SERVE the
//                              answer; do not run it again.
//    `Attempted` alone         INDETERMINATE. The effect may have run and may
//                              not, and no amount of engineering here can tell
//                              the difference — the record and the effect are
//                              not one transaction.
//
//  The third state is not a defect in this design; it is where "exactly once"
//  genuinely ends, and `Facets.fs` beside this file is the vocabulary for saying
//  so out loud rather than rounding it up.
//
//  ── What a journal entry may carry ──────────────────────────────────────────
//  The same rule the rest of this placement keeps: the CAPABILITY, never the
//  payload. An entry names the step's ordinal and its derived capability, and
//  carries the effect's own RESULT because a replay has to serve it — that value
//  is the host performer's answer, of the same class as the reason text a
//  `PerformFailed` already surfaces verbatim. A journal never records a
//  handler's arguments, a pipeline, or an endpoint.
// ============================================================================

/// How far one journaled step got.
///
/// Three arms and not two: `Attempted` is a state a step can be LEFT in, not
/// merely a moment it passes through, and collapsing it into "no record" would
/// turn the indeterminate window into a silent re-run.
[<RequireQualifiedAccess>]
type JournalPhase =
    /// The step is about to run. Written BEFORE the effect, so a crash during
    /// the effect leaves this record behind and the step is readable as
    /// indeterminate rather than as never-attempted.
    | Attempted
    /// The step ran and returned this value. Written AFTER.
    | Completed of value: Fuaran.Core.JVal
    /// The step ran and the performer refused. `reason` is the host performer's
    /// own text — the same value a `PerformFailed` diagnostic already carries.
    | Refused of reason: string

/// One journal record.
///
/// `Step` is the step's ORDINAL within the invocation, on exactly the terms
/// `ReplayReason.Stage` is an ordinal: a position addresses a step without
/// echoing any string a document chose. `Capability` is the derived capability
/// (`ServerEffect.capability`), recorded so a replay can prove it is serving the
/// step it thinks it is.
type JournalEntry =
    { Invocation: string
      Step: int
      Capability: string
      Phase: JournalPhase }

/// The durability port. Two functions and a declaration, and deliberately
/// nothing else — a host satisfies this with whatever it already trusts.
///
/// `SurvivesRestart` is the host's own claim about its storage, and it is a
/// FIELD rather than an assumption because the honest facet depends on it: a
/// journal that dies with the process cannot make anything exactly-once across a
/// restart, and a placement that assumed otherwise would publish a guarantee its
/// substrate does not provide. It defaults to `false` everywhere in this file;
/// claiming it is a deliberate act.
type EffectJournal =
    {
        /// Append one record. Append-only: nothing in this placement ever edits
        /// or removes an entry, because a journal a replay can rewrite is a
        /// journal a replay cannot trust.
        Append: JournalEntry -> unit
        /// Every record for one invocation, in append order.
        Read: string -> JournalEntry list
        /// Whether this journal's storage survives a process restart. The
        /// host's claim; nothing here can check it.
        SurvivesRestart: bool
    }

module JournalPhase =

    /// The phase's log-safe tag.
    let tag (phase: JournalPhase) : string =
        match phase with
        | JournalPhase.Attempted -> "attempted"
        | JournalPhase.Completed _ -> "completed"
        | JournalPhase.Refused _ -> "refused"

/// What the journal knows about one step, after the records for it are read
/// together. This is the three-state reading the header describes, as a value —
/// so the interpreter branches on a decided fact rather than re-deriving it from
/// a list at every call site.
[<RequireQualifiedAccess>]
type JournaledStep =
    /// No record: the step never ran.
    | Unrun
    /// The step ran and returned this value.
    | Value of value: Fuaran.Core.JVal
    /// The step ran and was refused, for this reason.
    | Refusal of reason: string
    /// `Attempted` with no result. The effect may or may not have happened, and
    /// the capability it was attempted under is carried so a caller can say
    /// WHICH step it cannot decide.
    | Indeterminate of capability: string

module Journal =

    /// A journal that records nothing and remembers nothing.
    ///
    /// Named rather than implied, because it is a legitimate configuration with
    /// an honest consequence: under it every replay re-runs every step, so the
    /// durable interpreter degrades exactly to the direct one and
    /// `Facets.fs` derives the direct one's guarantees for it. It is the DEFAULT
    /// for the same reason `denyAll` is — a host that wired nothing has promised
    /// nothing.
    let none: EffectJournal =
        { Append = ignore
          Read = fun _ -> []
          SurvivesRestart = false }

    /// An in-memory reference journal.
    ///
    /// It satisfies the contract for a replay WITHIN one process — which is
    /// precisely what a crash-and-replay fixture needs to observe, since the
    /// fixture's "crash" is a performer that does not return. It does NOT claim
    /// to survive a restart, and saying so is not modesty: an in-memory journal
    /// that declared otherwise would let a test certify a guarantee no
    /// deployment of it could keep.
    let inMemory () : EffectJournal =
        let entries = ResizeArray<JournalEntry>()

        { Append = entries.Add
          Read = fun invocation -> entries |> Seq.filter (fun e -> e.Invocation = invocation) |> List.ofSeq
          SurvivesRestart = false }

    /// The same journal, with the host's restart claim attached.
    ///
    /// The one way to set the flag, and it takes a whole journal rather than a
    /// boolean at a call site, so the claim is attached to the storage it is
    /// about.
    let declaringDurable (journal: EffectJournal) : EffectJournal = { journal with SurvivesRestart = true }

    /// What the journal says about `step` of `invocation`.
    ///
    /// The LAST result record wins where several exist. That cannot arise from
    /// this placement's own writes — it appends one result per step per run and
    /// serves a recorded step rather than re-running it — but a journal is
    /// shared storage, and "read the newest" is the only rule that is total
    /// against a host that also writes.
    let stepOf (entries: JournalEntry list) (step: int) : JournaledStep =
        let forStep = entries |> List.filter (fun e -> e.Step = step)

        let result =
            forStep
            |> List.rev
            |> List.tryPick (fun e ->
                match e.Phase with
                | JournalPhase.Completed value -> Some(JournaledStep.Value value)
                | JournalPhase.Refused reason -> Some(JournaledStep.Refusal reason)
                | JournalPhase.Attempted -> None)

        match result, forStep with
        | Some decided, _ -> decided
        | None, [] -> JournaledStep.Unrun
        | None, attempted -> JournaledStep.Indeterminate (List.head attempted).Capability

    /// The capability recorded against `step`, if the journal has seen it. Read
    /// by the replay-divergence check, which compares what a replay is about to
    /// do against what the recorded run did at the same ordinal.
    let capabilityOf (entries: JournalEntry list) (step: int) : string option =
        entries |> List.tryFind (fun e -> e.Step = step) |> Option.map _.Capability

    /// The ordinal reserved for the INVOCATION itself rather than for one of its
    /// steps.
    ///
    /// Negative, so it can never collide with a step ordinal, which is a
    /// zero-based position. The record it carries is an audit fact and nothing
    /// more: a completed invocation is REPLAYED rather than short-circuited,
    /// because the outcome of a handler is a tree and a store and is recomputed
    /// deterministically, never stored. A host reading this marker learns that a
    /// replay of that invocation will reach no performer.
    [<Literal>]
    let InvocationStep = -1

    /// The capability an invocation-level record is filed under.
    [<Literal>]
    let InvocationCapability = "Invocation"

    /// Whether `invocation` has a recorded completion.
    let isComplete (entries: JournalEntry list) : bool =
        match stepOf entries InvocationStep with
        | JournaledStep.Value _ -> true
        | _ -> false

    /// A log-safe rendering of one invocation's journal — ordinals, capabilities
    /// and phase tags, and no recorded value. The values are safe to SERVE (a
    /// performer's own answer) and needlessly wide to LOG, which is the same
    /// distinction `ServerDiagnostic` draws between a reason and a payload.
    let describe (entries: JournalEntry list) : string list =
        entries
        |> List.map (fun e -> sprintf "%d %s %s" e.Step e.Capability (JournalPhase.tag e.Phase))

// ============================================================================
//  OPERATOR CONTROLS — suspend, throttle, revoke and resume, as RECORDED OPS.
//
//  `InteractionBudget` refuses a single interaction that costs too much, and the
//  journal above records the one arm that reaches outside. Neither of them can
//  halt a session that is already running, slow one effect kind, or withdraw a
//  performer while a handler is live — the three acts an operator reaches for
//  first, and the three a kill-switch obligation asks for by name.
//
//  This section is those acts. What makes them cheap is that nothing new has to
//  be invented to carry them: a bounded, journalled program already records what
//  it did, so a suspension is a POSITION on a stream, a resumption is a replay
//  from it, and a revocation is a registry edit the gate already consults before
//  every performer.
//
//  ── Why ops and not calls ───────────────────────────────────────────────────
//  An imperative "suspend this session" leaves a running process in a different
//  state and nothing else. It cannot be replayed, so a resumed session does not
//  know it was ever stopped; it cannot be audited, so "what did the AI do" omits
//  the part where somebody stopped it; and it cannot be shown monotone, because
//  there is no record to be monotone over.
//
//  Recording the act instead buys all three from one mechanism. `Controls.fold`
//  below is a pure function of the stream, so the state a resume reaches is the
//  state any reader of the same prefix reaches; the stream is the audit trail;
//  and the revocation arm of `Controls.step` never removes from `Revoked`, which
//  is a property of six lines rather than a promise about a lifetime.
//
//  ── One op, two raisers ─────────────────────────────────────────────────────
//  A machine-raised suspend and an operator-raised one are THE SAME OP with a
//  different `ControlActor`. That is a deliberate constraint rather than a
//  convenience: two mechanisms would mean two fold rules, two audit vocabularies
//  and two places for a future control to be added to only one of. An automated
//  raiser — a denial-pattern detector, a spend guard — records
//  `ControlActor.machine`; a human records `ControlActor.operator`; every rule
//  below reads them identically, and only the record says which.
//
//  ── What the stream is scoped to ────────────────────────────────────────────
//  A SESSION, where an `EffectJournal` entry is scoped to an INVOCATION. The two
//  keys are kept apart rather than collapsed because an operator suspends a
//  session, not a handler call: filing a suspend under one invocation id would
//  make it invisible to the next one, which is the opposite of what a kill
//  switch is for. That is also why this is a second port beside `EffectJournal`
//  rather than a field added to it — the shapes agree, the keys do not, and a
//  host may satisfy one with storage it would not choose for the other.
//
//  ── What a control record may carry ─────────────────────────────────────────
//  The same rule as everything else in this placement: the CAPABILITY and the
//  host's own vocabulary, never a payload. A revoke names the performer (the
//  host's own registration key), a throttle names the capability, and the reason
//  is the raiser's own text of the same class as a `PerformFailed` reason. No
//  arguments, no pipeline, no endpoint.
// ============================================================================

/// Whether a control was raised by a person or by a machine.
///
/// A discriminator and not two op families: see the header. It exists so an
/// audit can answer "who stopped this" without the fold ever branching on the
/// answer.
[<RequireQualifiedAccess>]
type ControlActorKind =
    /// A human operator.
    | Operator
    /// An automated raiser — a detector, a guard, a policy runner.
    | Machine

/// Who raised a control. `Id` is the raiser's own identifier: an operator's
/// name, or the automated raiser's registered id. Host vocabulary, never
/// anything a generated tree supplied.
type ControlActor = { Kind: ControlActorKind; Id: string }

module ControlActor =

    let operator (id: string) : ControlActor =
        { Kind = ControlActorKind.Operator
          Id = id }

    let machine (id: string) : ControlActor =
        { Kind = ControlActorKind.Machine
          Id = id }

    /// The actor kind's log-safe tag.
    let tag (actor: ControlActor) : string =
        match actor.Kind with
        | ControlActorKind.Operator -> "operator"
        | ControlActorKind.Machine -> "machine"

/// A throttle's window: at most `MaxPerInvocation` attempts of `Capability`
/// within one handler invocation.
///
/// **The window is COUNTED, never timed, and that is a decision rather than a
/// simplification.** A rate over wall-clock time cannot be replayed — the same
/// stream re-read a second later would fold to a different answer, and the whole
/// value of recording these acts is that it does not. An invocation is the unit
/// this placement already prices, it is derived from the fold exactly as a
/// journal step ordinal is, and a replay recounts it to the same number.
///
/// It counts ATTEMPTS, not successes: a capability that fails is still a
/// capability the handler reached for, and a throttle that counted only the
/// successful ones would be free to breach by failing.
type ThrottleWindow =
    { Capability: string
      MaxPerInvocation: int }

/// The four operator acts.
///
/// Closed, and closed for D3's reason read one level up: a host extends what a
/// program can REACH by registering a performer, never by widening a vocabulary
/// the gate switches on. A fifth control is a decision, not a configuration.
[<RequireQualifiedAccess>]
type ControlOp =
    /// Refuse every dispatch until a `Resume`. The kill switch.
    | Suspend
    /// Refuse `window.Capability` past its per-invocation budget. A later
    /// throttle of the same capability REPLACES the earlier one.
    | Throttle of window: ThrottleWindow
    /// Withdraw a performer's registration for this session. Never lifted — see
    /// `Controls.step`.
    | Revoke of performer: string
    /// Lift the suspend. Deliberately lifts THAT and nothing else: a throttle is
    /// a standing limit and a revocation is monotone, so a resume that cleared
    /// them would make the mildest-sounding word in this vocabulary the most
    /// consequential.
    | Resume

module ControlOp =

    /// The op's log-safe tag, and the `$type` its wire form carries.
    let tag (op: ControlOp) : string =
        match op with
        | ControlOp.Suspend -> "Suspend"
        | ControlOp.Throttle _ -> "Throttle"
        | ControlOp.Revoke _ -> "Revoke"
        | ControlOp.Resume -> "Resume"

    /// The whole closed vocabulary, for host introspection.
    let tags: string list = [ "Suspend"; "Throttle"; "Revoke"; "Resume" ]

/// A step the effect journal showed as ATTEMPTED with no result at the moment a
/// control was recorded — the indeterminate window of D12, named on the record
/// that closed over it.
///
/// A suspend that lands mid-stage is the case the window is about: the effect
/// may have committed and may not, and no ordering of two writes to two systems
/// can decide it. Recording the window WITH the suspend is the difference
/// between a resume that knows it is resuming across an undecidable step and one
/// that discovers it afterwards — which, on the evidence of every replay engine,
/// it does not.
type MidStageWindow =
    { Invocation: string
      Step: int
      Capability: string }

/// One entry on the control stream.
///
/// `Sequence` is the entry's ORDINAL within the stream, on exactly the terms a
/// `JournalEntry.Step` is an ordinal: a position addresses an act without
/// echoing any string a document chose, and it is what makes a prefix of the
/// stream a well-defined thing to fold.
type ControlEntry =
    {
        Sequence: int
        Op: ControlOp
        Actor: ControlActor
        /// The raiser's own text. Every op carries one, `Resume` included: lifting
        /// a suspension is as much a decision as raising it, and an audit that
        /// recorded why a session stopped and not why it restarted answers half
        /// the question.
        Reason: string
        /// The indeterminate windows open when this act was recorded. Empty for a
        /// control raised between invocations, which is the ordinary case.
        MidStage: MidStageWindow list
    }

/// What a raiser supplies. The sequence is absent because it is the STREAM's,
/// derived at record time — two raisers cannot disagree about an ordinal neither
/// of them chose.
type ControlRequest =
    { Op: ControlOp
      Actor: ControlActor
      Reason: string
      MidStage: MidStageWindow list }

/// The control stream's port. The same shape as `EffectJournal`, keyed by
/// SESSION rather than by invocation — see the header for why the two are not
/// one port.
type ControlJournal =
    {
        /// Append one entry to a session's stream. Append-only: nothing here
        /// edits or removes an act, because a kill switch a later act can
        /// rewrite is a kill switch nobody can audit.
        Append: string -> ControlEntry -> unit
        /// Every entry for one session, in append order.
        Read: string -> ControlEntry list
        /// Whether this stream's storage survives a process restart. The host's
        /// claim; nothing here can check it. A suspension that dies with the
        /// process is not a suspension, so a host that means it says so.
        SurvivesRestart: bool
    }

/// What the stream folds to. DERIVED, never stored: this is a function of the
/// entries and of nothing else, which is the whole of what "replayable" means
/// here.
type ControlState =
    {
        /// The actor and reason of the suspend in force, if any.
        Suspended: (ControlActor * string) option
        /// The standing throttles, by capability.
        Throttles: Map<string, ThrottleWindow>
        /// The withdrawn performers, by registration key, with who withdrew each
        /// and why. **Monotone**: no op in this vocabulary removes from it.
        Revoked: Map<string, ControlActor * string>
        /// How many entries were folded to reach this state — the prefix this
        /// value is about.
        Folded: int
    }

/// Why a control refused something. Each names the capability and the act that
/// refused it; none carries a payload.
[<RequireQualifiedAccess>]
type ControlRefusal =
    /// The session is suspended, so this capability was refused with everything
    /// else.
    | Suspended of capability: string * actor: ControlActor * reason: string
    /// The capability's per-invocation window is spent. `attempt` is the
    /// one-based ordinal of the attempt that breached it, so a reader sees the
    /// window AND where it was crossed rather than only that it was.
    | Throttled of capability: string * window: ThrottleWindow * attempt: int
    /// The performer behind this capability was withdrawn. The effect itself
    /// reads as `ServerEffectDenial.Unregistered`, which is the honest report to
    /// the gate; this record is the reason behind it.
    | Revoked of capability: string * actor: ControlActor * reason: string

module ControlCode =

    /// The refusal a suspended session raises at the gate.
    [<Literal>]
    let SessionSuspended = "control-session-suspended"

    /// The refusal a spent throttle window raises.
    [<Literal>]
    let CapabilityThrottled = "control-capability-throttled"

    /// The refusal a withdrawn performer raises.
    [<Literal>]
    let PerformerRevoked = "control-performer-revoked"

    /// The pseudo-capability a suspended DISPATCH is refused under.
    ///
    /// Deliberately not an arm of `ServerEffect.kinds`: a suspension is refused
    /// one level above the effect gate, before any effect is named, so naming it
    /// after an effect would be a lie about where the refusal happened. It is a
    /// literal rather than an inline string so a reader's grep finds the refusal
    /// and its one producer together.
    [<Literal>]
    let DispatchCapability = "Dispatch"

module Controls =

    // ─── raising ─────────────────────────────────────────────────────────────

    /// Halt the session. `actor` is what distinguishes an operator's hand from a
    /// detector's; nothing downstream reads it.
    let suspend (actor: ControlActor) (reason: string) : ControlRequest =
        { Op = ControlOp.Suspend
          Actor = actor
          Reason = reason
          MidStage = [] }

    /// Halt the session across a known indeterminate window — the mid-stage
    /// case. Separate from `suspend` so that recording no window is a statement
    /// ("none was open") rather than an omission.
    let suspendMidStage (actor: ControlActor) (reason: string) (windows: MidStageWindow list) : ControlRequest =
        { suspend actor reason with
            MidStage = windows }

    let throttle (actor: ControlActor) (reason: string) (window: ThrottleWindow) : ControlRequest =
        { Op = ControlOp.Throttle window
          Actor = actor
          Reason = reason
          MidStage = [] }

    let revoke (actor: ControlActor) (reason: string) (performer: string) : ControlRequest =
        { Op = ControlOp.Revoke performer
          Actor = actor
          Reason = reason
          MidStage = [] }

    let resume (actor: ControlActor) (reason: string) : ControlRequest =
        { Op = ControlOp.Resume
          Actor = actor
          Reason = reason
          MidStage = [] }

    // ─── the port's reference implementations ────────────────────────────────

    /// A stream that records nothing and remembers nothing.
    ///
    /// The DEFAULT, and a legitimate configuration with an honest consequence: a
    /// host that wired no control stream cannot be suspended, and the controls
    /// cost it exactly one empty read. The durable interpreter is unchanged
    /// under it by construction rather than by a branch.
    let none: ControlJournal =
        { Append = fun _ _ -> ()
          Read = fun _ -> []
          SurvivesRestart = false }

    /// An in-memory reference stream. Satisfies the contract within one process,
    /// and says plainly that it does not survive a restart.
    let inMemory () : ControlJournal =
        let entries = ResizeArray<string * ControlEntry>()

        { Append = fun scope entry -> entries.Add(scope, entry)
          Read = fun scope -> entries |> Seq.filter (fun (s, _) -> s = scope) |> Seq.map snd |> List.ofSeq
          SurvivesRestart = false }

    /// The same stream, with the host's restart claim attached.
    let declaringDurable (journal: ControlJournal) : ControlJournal = { journal with SurvivesRestart = true }

    /// Record one act, returning the entry as it landed.
    ///
    /// The sequence is read off the stream rather than supplied, so an act is
    /// ordered by what was already there. Two raisers appending concurrently to
    /// one host-side stream can therefore land on the same ordinal — which is a
    /// property of the host's storage and is where it must be resolved, exactly
    /// as it is for the effect journal above.
    let record (journal: ControlJournal) (scope: string) (request: ControlRequest) : ControlEntry =
        let entry =
            { Sequence = (journal.Read scope) |> List.length
              Op = request.Op
              Actor = request.Actor
              Reason = request.Reason
              MidStage = request.MidStage }

        journal.Append scope entry
        entry

    /// The indeterminate windows open across these invocations, right now — what
    /// a mid-stage suspend records.
    let midStageWindows (journal: EffectJournal) (invocations: string seq) : MidStageWindow list =
        invocations
        |> Seq.collect (fun invocation ->
            let entries = journal.Read invocation

            entries
            |> List.map _.Step
            |> List.distinct
            |> List.filter (fun step -> step <> Journal.InvocationStep)
            |> List.sort
            |> List.choose (fun step ->
                match Journal.stepOf entries step with
                | JournaledStep.Indeterminate capability ->
                    Some
                        { Invocation = invocation
                          Step = step
                          Capability = capability }
                | _ -> None))
        |> List.ofSeq

    // ─── the fold ────────────────────────────────────────────────────────────

    /// Nothing recorded: not suspended, nothing throttled, nothing withdrawn.
    let initial: ControlState =
        { Suspended = None
          Throttles = Map.empty
          Revoked = Map.empty
          Folded = 0 }

    /// One entry's effect on the state.
    ///
    /// **`Revoked` is only ever added to.** That is the monotonicity claim, and
    /// it is a property of this function rather than a promise about the
    /// vocabulary: there is no arm here that removes a key, so no sequence of
    /// acts can produce a state in which a withdrawn performer is registered
    /// again. Re-registering one is a host act on a fresh session, which is
    /// exactly the ceremony it should be.
    let step (state: ControlState) (entry: ControlEntry) : ControlState =
        let folded = { state with Folded = state.Folded + 1 }

        match entry.Op with
        | ControlOp.Suspend ->
            { folded with
                Suspended = Some(entry.Actor, entry.Reason) }
        | ControlOp.Resume -> { folded with Suspended = None }
        | ControlOp.Throttle window ->
            { folded with
                Throttles = Map.add window.Capability window folded.Throttles }
        | ControlOp.Revoke performer ->
            { folded with
                Revoked = Map.add performer (entry.Actor, entry.Reason) folded.Revoked }

    /// Fold a stream — or any PREFIX of one, which is the same function and is
    /// why a resume reaches the state the record says it should.
    let fold (entries: ControlEntry list) : ControlState = entries |> List.fold step initial

    /// The state of one session's controls.
    let stateOf (journal: ControlJournal) (scope: string) : ControlState = fold (journal.Read scope)

    /// Whether a session is suspended.
    let isSuspended (state: ControlState) : bool = state.Suspended.IsSome

    // ─── the effect at the gate ──────────────────────────────────────────────

    /// The capability a withdrawn performer is reached through.
    let private revokedCapability (performer: string) = "host:" + performer

    /// **The controls in force, as a registry.**
    ///
    /// This is where the four acts MEAN something, and each one is expressed in
    /// the registry's existing vocabulary rather than in a new one:
    ///
    ///   SUSPEND   closes the gate over every capability. A suspended session
    ///             performs nothing, and every refusal is a `GateRefused` — the
    ///             arm that says "this host has the capability and refused this
    ///             use of it", which is exactly true of a suspension.
    ///   THROTTLE  closes the gate over one capability once its window is spent.
    ///             Also a `GateRefused`, for the same reason, with the window
    ///             named on the control record beside it.
    ///   REVOKE    REMOVES the performer. The effect then reads as
    ///             `Unregistered` — the arm that says "the capability is absent
    ///             from this host" — which is the honest report, and is what
    ///             carries the withdrawal through to `ServerCoverage` and so to
    ///             the demanded-effect check, with no second vocabulary to teach
    ///             it.
    ///   RESUME    is the ABSENCE of a suspend and needs nothing here.
    ///
    /// The counter is per CALL of this function, which is what makes a throttle
    /// window per-invocation: the durable interpreter wraps a registry once per
    /// invocation, so a fresh call is a fresh window. `record` receives every
    /// refusal the controls caused and only those — a capability the host's own
    /// gate refuses is not this function's news.
    let apply
        (record: ControlRefusal -> unit)
        (state: ControlState)
        (registry: ServerEffectRegistry)
        : ServerEffectRegistry =
        let mutable attempts = Map.empty<string, int>

        let revokedByCapability =
            state.Revoked
            |> Map.toList
            |> List.map (fun (performer, who) -> revokedCapability performer, who)
            |> Map.ofList

        let gate (capability: string) =
            match state.Suspended with
            | Some(actor, reason) ->
                record (ControlRefusal.Suspended(capability, actor, reason))
                false
            | None ->
                match Map.tryFind capability state.Throttles with
                | None -> registry.Gate capability
                | Some window ->
                    let attempt = (attempts |> Map.tryFind capability |> Option.defaultValue 0) + 1
                    attempts <- Map.add capability attempt attempts

                    if attempt > window.MaxPerInvocation then
                        record (ControlRefusal.Throttled(capability, window, attempt))
                        false
                    else
                        registry.Gate capability

        let onDenied (denial: ServerEffectDenial) =
            match denial with
            | ServerEffectDenial.Unregistered capability ->
                match Map.tryFind capability revokedByCapability with
                | Some(actor, reason) -> record (ControlRefusal.Revoked(capability, actor, reason))
                | None -> ()
            | ServerEffectDenial.GateRefused _ -> ()

            registry.OnDenied denial

        { registry with
            HostFunctions =
                state.Revoked
                |> Map.fold (fun functions performer _ -> Map.remove performer functions) registry.HostFunctions
            Gate = gate
            OnDenied = onDenied }

    /// This host's server-tier coverage **with the controls in force** — what a
    /// demanded-effect check is asked, so a withdrawn performer is reported as
    /// `CoverageFinding.UnregisteredServerFunction` and a suspended session as a
    /// gate refusal.
    ///
    /// It wraps a throwaway registry, so asking the question never spends a
    /// throttle window. A capability that is throttled but not yet spent reads
    /// as COVERED, which is the honest answer: a throttle is a limit on a
    /// capability the host has, not the absence of one.
    let coverage (state: ControlState) (registry: ServerEffectRegistry) : ServerCoverage =
        ServerDemanded.coverageOfRegistry (apply ignore state registry)

    // ─── the wire ────────────────────────────────────────────────────────────
    //
    //  A control entry's canonical form, under the same JSON discipline the rest
    //  of this placement's documents keep: `$type` first, members Ordinal-
    //  ordered, no JSON null, an absent optional member omitted.
    //
    //  It is deliberately a HOST document and not a specified one. The program
    //  wire specifies what a handler declares, what it may reach and what it
    //  reports; the control stream is what a host's operator did to a session,
    //  and specifying it now would pin an encoding before a second host has ever
    //  read one — the same posture the demanded-effect projection takes, and for
    //  the same reason. Round-tripping it here is what makes the acts portable
    //  between this host's own stores in the meantime.

    let private requireInt (name: string) (value: Fuaran.Core.JVal) : Result<int, WireRefusal> =
        match ProgramWire.tryMember name value with
        | None -> ProgramWire.refuse RefusalClass.MissingMember ("required member '" + name + "' is absent")
        | Some(Fuaran.Core.JInt i) -> Ok i
        | Some _ -> ProgramWire.refuse RefusalClass.MissingMember ("member '" + name + "' is not an integer")

    let private requireNonEmpty (name: string) (value: Fuaran.Core.JVal) : Result<string, WireRefusal> =
        ProgramWire.requireString name value
        |> Result.bind (fun text ->
            if text = "" then
                ProgramWire.refuse RefusalClass.EmptyName ("member '" + name + "' is empty")
            else
                Ok text)

    let private encodeActor (actor: ControlActor) : Fuaran.Core.JVal =
        Fuaran.Core.JObj
            [ "id", Fuaran.Core.JStr actor.Id
              "kind", Fuaran.Core.JStr(ControlActor.tag actor) ]

    let private decodeActor (value: Fuaran.Core.JVal) : Result<ControlActor, WireRefusal> =
        ProgramWire.declaredOnly [ "id"; "kind" ] value
        |> Result.bind (fun () -> requireNonEmpty "id" value)
        |> Result.bind (fun id ->
            ProgramWire.requireString "kind" value
            |> Result.bind (fun kind ->
                match kind with
                | "operator" -> Ok(ControlActor.operator id)
                | "machine" -> Ok(ControlActor.machine id)
                | other -> ProgramWire.refuse RefusalClass.UnknownEffectArm ("'" + other + "' is not an actor kind")))

    let private encodeWindow (window: ThrottleWindow) : Fuaran.Core.JVal =
        Fuaran.Core.JObj
            [ "capability", Fuaran.Core.JStr window.Capability
              "maxPerInvocation", Fuaran.Core.JInt window.MaxPerInvocation ]

    let private decodeWindow (value: Fuaran.Core.JVal) : Result<ThrottleWindow, WireRefusal> =
        ProgramWire.declaredOnly [ "capability"; "maxPerInvocation" ] value
        |> Result.bind (fun () -> requireNonEmpty "capability" value)
        |> Result.bind (fun capability ->
            requireInt "maxPerInvocation" value
            |> Result.bind (fun limit ->
                if limit < 0 then
                    ProgramWire.refuse RefusalClass.ImpossibleOutcome "a throttle window cannot be negative"
                else
                    Ok
                        { Capability = capability
                          MaxPerInvocation = limit }))

    let private encodeMidStage (window: MidStageWindow) : Fuaran.Core.JVal =
        Fuaran.Core.JObj
            [ "capability", Fuaran.Core.JStr window.Capability
              "invocation", Fuaran.Core.JStr window.Invocation
              "step", Fuaran.Core.JInt window.Step ]

    let private decodeMidStage (value: Fuaran.Core.JVal) : Result<MidStageWindow, WireRefusal> =
        ProgramWire.declaredOnly [ "capability"; "invocation"; "step" ] value
        |> Result.bind (fun () -> requireNonEmpty "capability" value)
        |> Result.bind (fun capability ->
            requireNonEmpty "invocation" value
            |> Result.bind (fun invocation ->
                requireInt "step" value
                |> Result.map (fun step ->
                    { Invocation = invocation
                      Step = step
                      Capability = capability })))

    /// One entry's canonical document.
    let encode (entry: ControlEntry) : Fuaran.Core.JVal =
        let common =
            [ "actor", encodeActor entry.Actor
              "reason", Fuaran.Core.JStr entry.Reason
              "sequence", Fuaran.Core.JInt entry.Sequence ]

        let midStage =
            match entry.MidStage with
            | [] -> []
            | windows -> [ "midStage", Fuaran.Core.JArr(windows |> List.map encodeMidStage) ]

        let specific =
            match entry.Op with
            | ControlOp.Suspend
            | ControlOp.Resume -> []
            | ControlOp.Throttle window -> [ "window", encodeWindow window ]
            | ControlOp.Revoke performer -> [ "performer", Fuaran.Core.JStr performer ]

        Fuaran.Core.Canon.typed (ControlOp.tag entry.Op) (common @ midStage @ specific)

    /// The canonical rendering of one entry.
    let render (entry: ControlEntry) : string = ProgramWire.render (encode entry)

    let decode (value: Fuaran.Core.JVal) : Result<ControlEntry, WireRefusal> =
        let common (declared: string list) (op: Fuaran.Core.JVal -> Result<ControlOp, WireRefusal>) =
            ProgramWire.declaredOnly ([ "$type"; "actor"; "midStage"; "reason"; "sequence" ] @ declared) value
            |> Result.bind (fun () -> requireInt "sequence" value)
            |> Result.bind (fun sequence ->
                ProgramWire.requireMember "actor" value
                |> Result.bind decodeActor
                |> Result.bind (fun actor ->
                    ProgramWire.requireString "reason" value
                    |> Result.bind (fun reason ->
                        (match ProgramWire.tryMember "midStage" value with
                         | None -> Ok []
                         | Some _ ->
                             ProgramWire.requireArray "midStage" value
                             |> Result.bind (ProgramWire.traverse decodeMidStage))
                        |> Result.bind (fun midStage ->
                            op value
                            |> Result.map (fun decoded ->
                                { Sequence = sequence
                                  Op = decoded
                                  Actor = actor
                                  Reason = reason
                                  MidStage = midStage })))))

        match ProgramWire.tag value with
        | None -> ProgramWire.refuse RefusalClass.MissingMember "required member '$type' is absent"
        | Some "Suspend" -> common [] (fun _ -> Ok ControlOp.Suspend)
        | Some "Resume" -> common [] (fun _ -> Ok ControlOp.Resume)
        | Some "Throttle" ->
            common [ "window" ] (fun v ->
                ProgramWire.requireMember "window" v
                |> Result.bind decodeWindow
                |> Result.map ControlOp.Throttle)
        | Some "Revoke" ->
            common [ "performer" ] (fun v -> requireNonEmpty "performer" v |> Result.map ControlOp.Revoke)
        | Some other ->
            ProgramWire.refuse
                RefusalClass.UnknownEffectArm
                ("'" + other + "' is not an arm of the closed operator-control vocabulary")

    // ─── reading ─────────────────────────────────────────────────────────────

    /// A log-safe rendering of one control refusal.
    let describeRefusal (refusal: ControlRefusal) : string =
        match refusal with
        | ControlRefusal.Suspended(capability, actor, reason) ->
            sprintf
                "%s: '%s' refused — the session is suspended by %s '%s' (%s)"
                ControlCode.SessionSuspended
                capability
                (ControlActor.tag actor)
                actor.Id
                reason
        | ControlRefusal.Throttled(capability, window, attempt) ->
            sprintf
                "%s: '%s' refused — attempt %d exceeds the window of %d per invocation"
                ControlCode.CapabilityThrottled
                capability
                attempt
                window.MaxPerInvocation
        | ControlRefusal.Revoked(capability, actor, reason) ->
            sprintf
                "%s: '%s' reads as unregistered — withdrawn by %s '%s' (%s)"
                ControlCode.PerformerRevoked
                capability
                (ControlActor.tag actor)
                actor.Id
                reason

    /// A log-safe rendering of one session's control stream — ordinals, op tags,
    /// actors and the raisers' own reasons, and no payload.
    let describe (entries: ControlEntry list) : string list =
        entries
        |> List.map (fun e ->
            sprintf "%d %s %s:%s %s" e.Sequence (ControlOp.tag e.Op) (ControlActor.tag e.Actor) e.Actor.Id e.Reason)
