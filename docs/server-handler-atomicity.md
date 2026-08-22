# Server handlers — atomicity, replay, and schema coupling

Open questions surfaced by the server-logic placement spike (`Fuaran.Program.Server`), with the
answers that spike currently implements. **This is a design note, not a decision record.** Nothing
here binds the way [`DECISIONS.md`](../DECISIONS.md) binds; each section records what the spike does,
why, and what remains unanswered — so the wire cut for the handler family starts from a written
position rather than from whatever the code happened to do.

They are recorded because a spike's real output is what it *found*, and a question answered silently
in code is a question that gets re-answered differently by the next person.

> **Three of the questions below have since been DECIDED**, deliberately and before the wire cut, and
> the decisions bind where this note does not: the host-effect atomicity mode (§1 →
> [D8](../DECISIONS.md)), result-target ownership (§3's last open item → D9), and where a nested call
> is recognised (§4's first finding → D7). The open items around them — replay modes, the idempotency
> key, the static schema check, concurrency — are untouched and still open. The sections are kept as
> they were written, with a marker at each decided point, because the argument that led to a decision
> is worth more than the decision restated.

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

*What the reader declares is undecided.* The check needs an expectation to compare against, and the
reading node does not declare one — the accessor is the only statement of what shape it wants. Either
the slot gains a declared schema (typed, checkable, more to author) or the expectation is inferred
from the accessor (no authoring cost, and inference across a wire boundary is exactly where inference
stops being cheap).

*A `Ref` source has no schema at validation time.* Its rows are host-side by design — the wire carries
the name, never the data. So a pipeline over a `Ref` either degrades the check to "unknown", or the
host declares source schemas as part of wiring its sources. The second is better and is not free.

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
