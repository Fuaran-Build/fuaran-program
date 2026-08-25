namespace Fuaran.Program.Server

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
