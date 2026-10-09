# Server handlers — atomicity, replay, and schema coupling

Open questions surfaced by the server-logic placement spike (`Fuaran.Program.Server`), with the
answers that spike currently implements. **This is a design note, not a decision record.** Nothing
here binds the way [`DECISIONS.md`](../DECISIONS.md) binds; each section records what the spike does,
why, and what remains unanswered — so the wire cut for the handler family starts from a written
position rather than from whatever the code happened to do.

They are recorded because a spike's real output is what it *found*, and a question answered silently
in code is a question that gets re-answered differently by the next person.

> **Four of the questions below have since been DECIDED**, deliberately and before the wire cut, and
> the decisions bind where this note does not: the host-effect atomicity mode (§1 →
> [D8](../DECISIONS.md)), result-target ownership (§3's last open item → D9), where a nested call is
> recognised (§4's first finding → D7), and what a query's reader declares (§3 → D10). **§3's static
> schema check — the follow-on this note calls its most valuable — is now BUILT**, which is why D10 had
> to be taken. The open items around them — replay modes, the idempotency key, concurrency — are
> untouched and still open. The sections are kept as they were written, with a marker at each decided
> point, because the argument that led to a decision is worth more than the decision restated.

---

## 1. Transaction and atomicity semantics for a multi-stage handler

**The question.** A handler is an ordered stage list — a query, two op applications, a host call. The
third stage fails. Does the first op application stay? Does the op journal see one entry, three, or
none? And what does a client that already received a patch from stage two believe?

**What the spike does.** The **handler** is the unit of atomicity, not the stage. Stages thread a
value rather than mutating anything; a denial or a failure sets a halt flag, the remaining stages are
skipped, and the accumulated store, ops, client effects and notifications are all discarded in favour
of the state the handler started from. The diagnostics survive, because they are the entire record of
why nothing happened. The outcome carries `Committed`, so a caller is told which of the two it got
rather than having to infer it. The op sink is called **once**, after the handler, with the
re-resolution diff — never per stage.

A partially-applied handler is therefore unrepresentable in the returned value, which is a stronger
guarantee than "we roll back on error" because there is no code path that could forget to.

**What remains open.** Two things, and the first is the important one.

*Host effects are outside the transaction.* A `HostCall` in stage two has done whatever it does by
the time stage three fails, and no rollback in a pure fold reaches it. The same is true of any
`Notify` a host has already shipped if a host chooses to stream rather than batch. So the honest
statement is: **domain-state atomicity is total; host-effect atomicity is not, and cannot be without
a vocabulary the algebra does not have.** Closing it means one of three things, and the choice is a
wire-family decision:

- **Compensation.** A handler declares an undo stage per effect. Expressive, and it doubles the
  authoring burden while making a handler's correctness depend on an author writing a correct
  inverse — which is the failure mode compensating transactions are famous for.
- **Constrained host calls.** `HostCall` is restricted to *reads*, and every write exits through
  `ApplyOps`. Cheap, checkable before execution, and it forecloses the case the escape hatch exists
  for (D2's "genuinely unbounded computation"), which frequently *is* a write.
- **Two-phase staging.** Effects are collected and performed only after every stage has succeeded,
  so a failure happens before anything external runs. This is the most attractive of the three
  because it needs no new authoring vocabulary — but it forbids a later stage reading an earlier
  host call's result, which is a real handler shape.

The spike deliberately implements none of them: it performs effects in stage order and reports the
truth. Picking one without demand evidence would be exactly the premature commitment the spike
exists to avoid.

> **DECIDED — two-phase staging ([D8](../DECISIONS.md)).** Chosen on the arguments above: it needs no
> new authoring vocabulary, so every handler written before the decision gains the property. Only
> `HostCall` stages — the reads and the accumulating arms were never performed by the handler in the
> first place, and deferring a read would break the read → compute → mutate shape a stage list exists
> for. The stated cost (a later stage cannot read an earlier host call's result) is now structural.
> The residual the decision does **not** abolish — a performer failing in the perform phase leaves its
> predecessors run — is reported rather than absorbed: `Committed = false` with `Performed` naming the
> calls that did happen, under a `PerformFailed` diagnostic distinct from the planning-phase `Failed`.
>
> **Amended by [D19](../DECISIONS.md) (2026-10-01).** "Only `HostCall` stages" describes the
> in-memory placement. A placement that registers an op performer (`OpPerformance.Performed`) stages
> `ApplyOps` as well: its ops are applied in memory while planning and performed after the plan
> commits, one staged call per op, under the same law and the same residual report.
>
> **Amended by [D23](../DECISIONS.md) (2026-10-02).** The residual is now RESUMABLE under the
> durable interpreter for ops as it was for host calls: D12's journal covered "the one arm that
> reaches outside", and under a registered op performer that premise was false — a performed op
> reaches outside exactly as a host call does. `Durable.runWith` journals every op stage under
> `ApplyOps` at its ordinal in the one sequence the host calls share, with the op's content address
> as the entry's subject, so a plan interrupted after a performed op resumes without repeating it,
> a crash inside a performer leaves an indeterminate step the default replay refuses, and in memory
> nothing new is journaled. The discipline is proved over the staged list (`proofs/Staging.fst`,
> `durable_resume` and the seven beside it) before the code moved.
>
> **What an operator's revoke reaches — amended by [D26](../DECISIONS.md) (2026-10-02).** A revoke
> (`Controls.revoke`) withdraws a performer for the session, and now reaches every arm that commits
> outside. A HOST performer, named by its registration key, is removed from the registry, so its
> call reads as `Unregistered` while PLANNING and the handler performs nothing. The OP performer,
> named by the reserved key `OpPerformance.RegistrationKey` (`ApplyOps`), is refused at each op
> STAGE in the perform phase, at the stage's ordinal and before the performer is invoked, as a
> `PerformFailed` under `ApplyOps` naming `control-performer-revoked` — the same residual report
> as any perform-phase failure, so a host call staged before the op has run and is named in
> `Performed`. Under the durable interpreter a resumed run serves the stages its journal recorded
> and refuses the first it did not, and a refused stage leaves no journal entry. In memory there is
> no op performer and a revoke of the key withdraws nothing. The control record is
> `ControlRefusal.Revoked` for both.
>
> **What coverage under controls reports — amended by [D28](../DECISIONS.md) (2026-10-02) and
> [D37](../DECISIONS.md) (2026-10-08).** `Controls.coverage` / `DurableControls.coverage` read the
> placement's `OpPerformance` beside the registry, so the report agrees with the effect for both
> performer kinds, and both kinds of revoke read as ABSENCES, because a revoke is monotone and no
> policy lifts it. A revoked HOST performer is absent from the registry and reads as
> `UnregisteredServerFunction`. A revoked OP performer, under a registered op performer, is carried
> in `ServerCoverage.Withdrawn` — the registration fact for the arms that need no host function —
> and reads as `ServerCapabilityWithdrawn "ApplyOps"`, reported before the gate is asked; the gate
> is the registry's, untouched. A suspended session, which a `Resume` lifts, is the policy class
> and reads as `ServerGateRefusesCapability`. In memory nothing is withdrawn and `ApplyOps` stays
> covered. Coverage names the capability; who revoked it and why is on the control state and on the
> `ControlRefusal.Revoked` record a run produces.

*Concurrency is not addressed at all.* One session, one event, one handler. Two sessions running
handlers against the same domain tree is a question about where durable state actually lives, which
is a host concern today and may not stay one.

---

## 2. Idempotency on replay

**The question.** The op stream is replayable — that is much of the point of modelling behaviour as
data. Replaying a session that invoked a handler would re-run the handler: re-issuing its host calls,
re-evaluating its query, re-notifying its channels. That is not replay, it is re-execution.

**What the spike does.** It journals **ops, not invocations.** A handler's output — the re-resolution
diff — reaches `OnApply`; the handler invocation itself is recorded nowhere. Replay therefore
reconstructs state by applying ops, and is effect-free by construction: there is no recorded artefact
from which a replaying host *could* re-issue a host call even if it wanted to. This is not a new
seam; it is the seam both other placements already have, used deliberately.

> **Still true under the durable interpreter, and checked against it ([D12](../DECISIONS.md),
> [D23](../DECISIONS.md)).** The durable interpreter's EFFECT journal is a different record from the
> op stream this section is about: a step record, keyed by ordinal within an invocation, written
> before and after each performer so a resumed run can serve the steps that ran rather than perform
> them twice. It records host-call steps (D12) and, since D23, performed op stages — under
> `ApplyOps`, with the op's content address as the subject. Neither is an invocation journaled as a
> replayable record: a resume recomputes the invocation from its entry state and reads the journal
> only at the perform phase, and nothing in it is an artefact from which a host re-issues the
> invocation. Which is why the journal extension is not a specification act (D23 item 5).
>
> **And the plan's ENTRY READ, since [D39](../DECISIONS.md) (Phase 2165).** "Recomputes the
> invocation from its entry state" has a premise: that the plan reads nothing but the entry state. A
> verb whose plan reads the world — inside its state witness's `Apply`, before deciding what to
> perform — breaks it the moment its own perform phase moves what it read: killed after the op and
> re-entered, the re-plan reads the moved world and is refused by the domain before any journaled
> stage is reached. `Durable.runReading` hands the witness an `EntryReader` bound to the run: the
> read is journaled at its own ordinal, in the same cursor sequence as the staged calls, under
> `ReadEntry` with the read's name as its subject and the host's answer as its completed value, and a
> re-entry of the same invocation is SERVED it — the resumed plan observes what the dead run's entry
> saw, never the live world, so it stages the same calls and the journaled stages are served
> (`entry_read_served`, `resumed_plan_observes_entry` in `proofs/Staging.fst`). A resume whose plan
> would read something else at that ordinal is refused under `durable-entry-read-diverged` naming the
> read, before any stage. The exactly-once claim therefore covers what a plan READ as well as what it
> performed; it is still a step record and not a replayable invocation, and still not a
> specification act — the journal port gained no case, only a capability value. The op performer and
> its contracts are also handed the run's PREFIX — the pre-op state and the receipts of the earlier op
> stages, a served stage's recorded receipt among them on a resume — so a stage whose act is "the
> receipts before me" no longer reads them back from the host's own journal; the idempotency facet is
> declared per effect kind, so a vocabulary that mixes a content-addressed write with a push declares
> them apart; and several contracts over one performer are composed by Program, first declared first.
>
> **And a plan's read is TYPED, since [D41](../DECISIONS.md) (Phase 2175).** A read the plan makes
> through the entry reader can now answer `Read.Unavailable` — the store could not be read, or its own
> codec refused what it answered — as a case the plan must match, never as a default standing in for a
> read that did not happen. `Reads`, built per run over the run's entry reader, journals the raw read
> (the content, or the host's reason there was none) as the read's completed value, so an unavailable
> read is journaled like any answer: a resume meets the same unavailability rather than re-reading a
> world that has since moved, and a run that refused on it replays the same refusal. Its `Degraded`
> ledger is what makes a refusal before the first performed op a property of the plan rather than of
> the host: every op is performed after the plan completes (§1), so an `Apply` that refuses on
> `Refusal ()` refuses with nothing performed, naming each read it could not make. A read's decode is
> memoised for the run by the codec's name and a digest of the content read, never by metadata about
> the store, and the raw read is never memoised, so on a resume the memo can only serve a decode of the
> journaled bytes.

**What remains open.**

*Audit-replay and resume-replay are different modes and this note is the first place they are named.*
Replaying the recorded ops reconstructs what the session *did*, which is what an audit wants. It does
not re-derive a `RunQuery` result against current data, which is what a "resume this session" wants.
Both are legitimate; they cannot both be the default; and the wire family should name them rather
than letting a host discover the distinction the hard way.

*A retry is not a replay.* A client that loses a response and re-sends the same event produces a
**new invocation** — correctly, since the loop cannot tell a retry from a genuine second click.
Deduplicating it needs an idempotency key travelling with the event, and neither the event nor the
tree carries one. That is a wire question with a real cost either way: a key on every event is
overhead on the common case, and a key on none makes exactly-once impossible to offer.

*A handler that is idempotent by construction is checkable.* `ApplyOps` with absolute addressing and
`SetState` with a literal are idempotent; `HostCall` is opaque. So a validator could classify a
handler as replay-safe or not, before it runs, from its declaration alone — the same "it is data, so
check it first" move the rest of the domain makes. Worth doing at the wire cut, when handlers have a
declared form to check.

---

## 3. Schema coupling between demanded queries and target domain trees

**The question.** A `RunQuery` declares a pipeline over a source and lands a table in a slot. Some
node in the domain tree reads that slot. Nothing checks that the pipeline's output schema is what the
reader expects, and nothing checks the source's schema is what the pipeline assumes. Both are silent
until they are a runtime failure — or worse, until they are a wrong-looking screen.

**What the spike does.** It keeps the coupling a **runtime failure and says so.** A pipeline whose
schema assumptions fail produces an evaluation error, which halts the handler (§1), rolls it back,
and surfaces as a diagnostic. The diagnostic carries the error's *discriminator only* — not its
message — because the engine's messages quote column and parameter names taken from the pipeline, and
a pipeline is handler-declared today but wire-carried after the cut. A verbatim message would become
a payload leak the day that changes, which is a bad property to have to remember to fix.

The cost is real: `UnknownColumn` without the column name is a thinner error than a developer wants.
That is recorded here rather than argued away, and it is the strongest argument for the pre-execution
check below — an error you can raise *before* the untrusted tree is involved can afford to be
detailed.

**What remains open.**

*The check should be static, and it can be.* `Transform` is data, and its output schema is derivable
from its input schema without evaluating anything. So a validator family could reject a handler whose
query cannot satisfy its reader before the tree ever runs — the same posture the tree wire takes with
its pre-emit validator, applied to the compute layer. This is the most valuable single follow-on this
note identifies.

> **BUILT — the walk exists, and so does the detailed error.** `Schema.ofTransform` derives a
> pipeline's output schema over the closed verb set without evaluating anything, and a validator family
> checks it against the tree's readers at the server placement's opt-in strict construction. A handler
> whose query drops a column its reader needs is now refused *before* the untrusted tree runs, and the
> refusal names the column, the reader, and what the query does provide. **The cost this section
> recorded is repaid rather than removed:** the runtime diagnostic still carries the discriminator
> only — it is still the thing that runs while a wire-carried pipeline is in scope — and the detailed
> error now exists on the side where names cost nothing. The two are pinned by one test read twice.

*What the reader declares is undecided.* The check needs an expectation to compare against, and the
reading node does not declare one — the accessor is the only statement of what shape it wants. Either
the slot gains a declared schema (typed, checkable, more to author) or the expectation is inferred
from the accessor (no authoring cost, and inference across a wire boundary is exactly where inference
stops being cheap).

> **DECIDED — declared, and the declaration already existed ([D10](../DECISIONS.md)).** Neither option
> above ships. Inference from the accessor is not merely expensive across the wire, it is EMPTY there:
> the accessor is a closure and decodes to an identity projection, so it says nothing about columns on
> exactly the trees this check exists for. And a new declared slot schema is D9's problem again — a
> chart names its axes and a grid column names its field, in wire-carried strings, so a second
> declaration would be two mechanisms for one job with the new one free to drift. The expectation is
> therefore declared by the reading node's EXISTING fields and harvested rather than deduced; a
> closure-held projection makes it a lower bound, reported as such.

*A `Ref` source has no schema at validation time.* Its rows are host-side by design — the wire carries
the name, never the data. So a pipeline over a `Ref` either degrades the check to "unknown", or the
host declares source schemas as part of wiring its sources. The second is better and is not free.

> **DECIDED — both, in that order ([D10](../DECISIONS.md)).** The host declares source schemas at
> registration, beside the resolver that serves the rows; an undeclared source degrades to "unknown"
> and the report says which query it cost. The third option the paragraph does not name — resolve the
> source at validation time and read the schema off the table — was refused explicitly: a check that
> calls a host's data resolver to decide whether a handler may run has already run half the handler.

*The call action's result target is not honoured, and that is a schema question too.* The tree's call
action carries an optional result target, and this spike ignores it: the handler's own stages declare
where their results land. Two mechanisms for one job is one too many, and choosing between them is a
schema-coupling decision — does the **tree** say where a handler's answer goes (the reader's view,
which keeps the handler reusable), or does the **handler** (the writer's view, which keeps the
contract in one place)? The spike takes the second by omission, not by argument.

> **DECIDED — the handler declares, and the tree's target is REFUSED ([D9](../DECISIONS.md)).** The
> spike's answer survives, now by argument: a tree-declared landing slot would let an untrusted tree
> choose where a privileged handler's answer is written, which is the one thing the fixed capability
> envelope exists to prevent — and one target cannot in any case address a handler's several
> result-landing stages. Refusal rather than silent omission is the load-bearing half: the retired
> mechanism now says so out loud instead of looking alive to anyone reading the wire vocabulary. The
> reusability the reader's view bought is recovered by registering one stage list under two endpoints.

---

## 4. Smaller findings, recorded so they are not rediscovered

- **Handler recognition is top-level only.** A call action nested inside a chain remains the shared
  interpreter's documented no-op. Reaching into the action tree to find nested calls would mean
  matching on an action a second time, which is precisely what D1 forbids; doing it properly means
  the shared fold itself gaining a handler-effect arm, which is a change to the shared algebra and
  not a spike's decision. The wire cut should take it deliberately, in the fold, once.

  > **DECIDED — taken in the fold, once ([D7](../DECISIONS.md)).** The fold gained the handler-effect
  > arm and the placement supplies only the answer, so recognition is uniform at any depth and a
  > nested call is spliced in place. The guard the finding was worried about came out stronger, not
  > weaker: exactly one site in the domain interprets an action, and the server package matches on an
  > `Action` nowhere at all. One boundary is drawn deliberately — a call inside a *handler
  > stage* stays the no-op, which keeps handler composition a host act and keeps D2's totality
  > structural rather than budget-dependent.
- **A handler's body is host data; only its name comes off the wire.** That is what makes it safe to
  log a host-function name and unsafe to log an endpoint, and it is why the unregistered-handler
  diagnostic deliberately does not say which endpoint was named. When handlers gain a wire form,
  that asymmetry disappears and every string in a handler becomes untrusted — a consequence of the
  cut that is easy to miss because it changes no code, only what the code is allowed to say.
- **The tree can now change under the loop.** Both other placements hold a fixed base tree; a handler
  can edit it. The resource budget is therefore re-priced after a handler commits, and an
  over-budget result is carried unresolved so the *next* event is refused — rather than a mutation
  that already succeeded being un-done by a budget check that ran too late. Correct, and a shape to
  keep in mind: the budget bounds what happens next, not what just happened.
