# DECISIONS.md — fuaran-program

Standing design decisions for the program domain. Each is binding until explicitly superseded
here. (Decision provenance beyond this repo's scope is recorded at the maintainers' workspace
level.)

## D1 — Pipeline core; richer control structure is vocabulary, not evaluator (2026-07-31)

The algebra's core is minimal and fixed: **sequencing + typed branching + named effects**.
Statecharts (states / transitions / guards), workflows, and other control-structure idioms are
expressed as *vocabulary atop the core* — node kinds that lower to it — never as alternative
evaluators. Rationale: the core matches the handler decomposition every host actually performs
(validate → read → compute → mutate → respond); a second evaluator would fork the totality and
safety analysis. Deciding statechart-primary later would force a rewrite of every interpreter;
deciding pipeline-primary later would not, which is why the pipeline wins now.

## D2 — Total, not Turing-complete (2026-07-31)

The program language is **total**: structural recursion over finite data and bounded iteration
(a repeat whose count is a literal or a validated-range parameter) are permitted; general
(non-structural) recursion is forbidden. Genuinely unbounded computation exits through the
explicitly-effecting host-function tier (D3), which the effect signature segregates. Rationale:
totality is what makes a program tree checkable *before* execution, safe for machine emission,
and hostable on shared infrastructure — bounded code + bounded cost. Losing it would forfeit the
domain's reason to exist; no expressiveness argument outranks that.

## D3 — Closed per-placement effect vocabularies; host-seam extension only (2026-07-31)

Each placement of the program loop (a browser client, a server session, any future host) carries a
**closed DU** of effects its interpreter can emit. Extensibility comes from **registered,
policy-gated host performers** behind a default-deny gate — never from widening the wire
vocabulary ad hoc. Rationale: a closed vocabulary is what keeps the effect signature auditable, a
program's capability envelope declarable, and the default-deny gate meaningful. A program can
*name* only effects its host registered; a genuinely novel capability is a host act, and the
program's reach extends only after registration.

## D5 — The dependency runs one way: this domain consumes the UI tier, never the reverse (2026-08-15)

The bounded interpreter moved here **whole**: the interpreter, the binding re-resolution pass, and
the server placement of the loop that drives them all live in `Fuaran.Program.Bounded`. The
alternative — leaving the server loop in the UI tier's server-driven package and having that package
consume this one — was considered and **refused**.

The reason is not taste. The interpreter needs the UI tier's types, so it must depend on the UI tier
(D4). Had the UI tier's server-driven package then depended back on this one, that package would
carry, in a single compilation, a project reference to the UI types *and* a transitive package
reference to a differently-built copy of the same types — the version-skew class that surfaces as a
cast failure at runtime rather than an error at build time. It would also make every change to the
UI types a two-repo round trip (pack the UI tier, rebuild and pack this repo, rebuild the UI tier)
on the more actively developed of the two.

So: **`Fuaran.Program.*` consumes published `Fuaran.UI.*` packages; no `Fuaran.UI.*` package
references `Fuaran.Program.*`.** This repo stays buildable from a cold clone against published
packages alone, and the package graph stays acyclic.

The consequence is recorded honestly: the UI tier's server-driven package **no longer ships the
bounded interpreter or the bounded loop**. That is a removal from its public surface, taken as a
pre-1.0 minor bump, and consumers of the bounded path take a reference to this package instead. The
direction is re-examined at the D4 generic-tier cut, when the witness-generic contract may remove the
UI-type dependency altogether.

## D4 — First instantiation is UI-typed; the generic tier waits for a second domain (2026-07-31)

The domain is chartered now, as a design decision — its identity, name, wire-family destiny, and
package surface are sovereign from birth. Its **first instantiation is UI-typed**: the bounded
interpreter proven in the UI tier's server-driven packages moves here and is consumed against the
UI tier's types. The **witness-generic contract** (the abstraction that lets any Fuaran domain
instantiate the algebra over its own types) is cut only when a second domain instantiation
materialises — a single-witness abstraction bakes its witness's assumptions in, so generalisation
is timed empirically even though the domain's existence is not. Consequence: `Fuaran.Program.*`
packages may reference published `Fuaran.UI.*` packages during this stage; the dependency
direction is re-examined at the generic-tier cut.

## D6 — `fuaran.program/logic-tree` is the by-id reference vocabulary for placing a program tree (2026-08-22)

A composition surface that carries a program tree holds it in an **opaque slot under the namespaced
id `fuaran.program/logic-tree`** and refers to it by that id rather than by structural position, so
the two sides agree on a name without either taking a type dependency on the other.

## D7 — Call recognition is an arm of the shared fold, and a placement supplies only the answer (2026-08-22)

A **call action is recognised by the shared interpreter**, at whatever depth it appears, and the
placement supplies only what a call *means* there — through a `HandlerArm` the fold consults. The
server-logic placement's earlier shape, where the loop matched the top-level action itself and looked
up an endpoint before reaching the fold, is retired.

The reason is D1, read the other way round. Recognising a call nested inside a chain requires knowing
where in the chain it sits, and only the fold knows that; a placement that reached into the action
tree to find one would be matching on `Action` a second time, which is a second evaluator in
everything but name. So the choice was never "top-level or nested" — it was "one evaluator with a
seam, or two evaluators". Moving the arm into the fold also makes the property checkable rather than
asserted: exactly one site in the domain *interprets* an action, and the server package matches on an
`Action` nowhere at all — a test greps for it, so restoring a special case there means deleting an
assertion that says not to. (Two other walks match the same closed DU without interpreting it: the
resource budget's cost accounting and the demanded-effect projection's static enumeration. Neither
performs, mutates or resolves anything, which is the distinction D1 actually draws.)

The consequence to state plainly is that a nested call is now **spliced in place**: it sees the writes
declared before it and is seen by the writes after it, because the fold threads one store through
them all. That is the behaviour a reader would have assumed; before this decision it was silently a
no-op, which is the worse of the two failures because nothing announced it.

**What it forecloses.** A placement can no longer give a call action a meaning that depends on where
it sits — the fold decides *when* the arm is consulted, and hands it a store, a node id and an
endpoint, nothing else. A placement wanting "only the outermost call counts" would have to argue for
it here rather than implement it locally.

**The one boundary drawn deliberately:** a call action *inside a handler stage* runs against the
INERT arm, so it is the documented no-op. A handler's stages are host-registered data whose
capability envelope is fixed before any untrusted tree arrives, and handler composition is therefore
a host act — registering the stages you want — not something a call buried in a stage may smuggle in.
It is also what keeps D2 true structurally: were a stage's call to re-enter the registry, a handler
naming itself would not terminate, and totality would rest on a resource budget instead of on the
shape of the thing.

## D8 — Host-effect atomicity is TWO-PHASE STAGING (2026-08-22)

The design note left three ways to close the gap between the handler's total domain-state atomicity
and its non-total host-effect atomicity: compensation, constrained host calls, or two-phase staging.
**Two-phase staging is chosen.** A handler run is a PLAN phase, in which every stage executes and each
`HostCall` is gated, resolved to a performer and checked for a legal landing slot but **not invoked**,
followed by a PERFORM phase, reached only if the plan completed, in which the staged calls are invoked
in declaration order.

It is chosen on the note's own arguments. Compensation doubles the authoring burden and makes a
handler's correctness depend on an author writing a correct inverse, which is the failure mode
compensating transactions are known for. Constraining `HostCall` to reads forecloses the case the
escape hatch exists for (D2's genuinely unbounded computation), which frequently *is* a write —
paying for atomicity by deleting the capability. Staging needs no new authoring vocabulary at all: a
handler that was correct before this decision is correct after it, and the property arrives for free
at every existing handler rather than at the ones someone remembers to annotate.

**Only the arm that commits outside is staged, and that is the honest boundary.** Of the five server
effects, `RunQuery` reads, `ApplyOps` edits an in-memory tree the caller may discard, and `EmitPatch`
and `Notify` accumulate as values the host performs after the handler returns — none of them can be
"performed too early" because none of them is performed by the handler at all. `HostCall` is the only
arm that reaches outside, so it is the only one deferred. Deferring the reads as well would have been
a purer reading of the note's wording and a worse decision: it would break the read → compute → mutate
shape that is the entire reason a handler is a stage list.

**What it forecloses, and what it does not close.**

- **A later stage cannot read an earlier host call's result.** This is the cost the note named, and it
  is now structural rather than discouraged: at planning time there is no result to read. A handler
  needing that shape is two handlers, or a host function that does both halves.
- **A performer that fails in the PERFORM phase leaves its predecessors run.** Staging moves the
  boundary; it does not abolish it, and no fold can. What changed is the size and the reporting: the
  residual is now at most a prefix of the declared host calls, it can no longer be triggered by a
  domain failure, and the outcome names it — `Committed = false` with `Performed` listing exactly the
  calls that happened, plus a `PerformFailed` diagnostic distinct from the planning-phase `Failed`.
  An uncommitted handler reporting a non-empty `Performed` is that case and only that case.
- **`Performed` is execution order, not stage order.** A host call declared first appears last,
  because that is when it ran. Reading it as a stage list would be reading it as a declaration, and it
  is an audit trail.

The handler-as-atomicity-unit guarantee is unchanged: `Committed` still says which of the two
outcomes a caller got, a halt still discards the store, ops, patches, notifications and effects in
favour of the entry state, and the op sink is still called once, after the handler, never per stage.

## D9 — The HANDLER declares where its results land; a tree-declared result target is refused (2026-08-22)

A program tree's call action can carry a result target, and a handler's stages name their own landing
slots. Two mechanisms for one job is one too many. **The handler's wins**, and the tree's is not
merely ignored — the shared fold **refuses** a call action that declares one, at every placement, with
a log-safe diagnostic naming neither the endpoint nor the target. The demanded-effect projection stops
projecting it for the same reason.

The note framed the trade as the reader's view (the tree says where the answer goes, keeping the
handler reusable) against the writer's view (the handler says, keeping the contract in one place).
Two things decide it here, and neither is about taste.

**The tree is untrusted; the handler is not.** This whole placement rests on a session's capability
envelope being fixed before any generated tree arrives — the host registers the handlers, the tree can
only name one. A tree-declared landing slot punctures exactly that: it lets an emitted tree choose
where a privileged handler's answer is written, including into a slot some other part of the tree
reads. The host-reserved-namespace check would then be guarding a wire-carried string rather than a
host-declared one, which is a materially weaker position for the same code.

**And it is under-expressive besides.** A handler has several stages that land results — a query
names its slot, each host call names its own — so one target on the call action cannot address them.
The mechanism that lost could not have done the job even if it were safe.

The reusability the reader's view buys is recoverable and cheap: a host registers the same stage list
under two endpoints with different landing slots. That is a host act, which is where every other
capability decision in this domain already sits.

**What it forecloses.** A tree can no longer parameterise a handler at all — not its landing slot, and
by the same argument not anything else it might later have carried. A future case for tree-supplied
parameters is a case for a declared, validated parameter vocabulary with a host-side schema, and it
has to be made here; it cannot arrive as a field the fold quietly starts honouring.

**Refusal rather than silence is the load-bearing half.** Ignoring the target would leave an author
believing an answer lands somewhere it never does, and would leave the retired mechanism looking alive
to anyone reading the wire vocabulary. This costs nothing today, because no bounded placement ever
honoured the target: it has been inert since the interpreter was written, and refusing it merely says
so out loud.

## D10 — The reader's expectation is DECLARED, and the declaration already exists (2026-08-22)

A query lands a table in a named slot and some node reads it. To check the two against each other
before anything runs, the check needs an expectation to compare the derived output schema against.
The design note left two ways to get one: the reading slot gains a **declared schema** (typed,
checkable, more to author), or the expectation is **inferred from the accessor** (no authoring cost,
and inference across a wire boundary is where inference stops being cheap). **Neither of those two
ships**, and the reason is worth more than the answer.

**Inference from the accessor is not expensive across the wire; it is EMPTY across the wire.** A
query binding's accessor is a closure, and a closure does not survive decoding — the decoder
substitutes an identity projection, so on a decoded tree the accessor says nothing whatsoever about
columns. Inference would work only on a hand-authored tree, which is precisely the tree that does not
need this check: the check exists because a *generated* tree arrived and the host would like to know,
before running it, whether its own queries can serve it.

**A declared slot schema was refused for D9's reason: the tree already declares this.** A chart names
its `xField` and its `yFields`; a grid column names its `field`; a grid names its `rowKeyField`. Those
are ordinary wire-carried strings, not closures, and they are the whole of what the render vocabulary
reads from a row. Minting a second declaration beside them would be two mechanisms for one job — and
the new one would be the one free to drift, because nothing renders it.

**So the expectation is DECLARED, by the reading node's existing fields, and the walk HARVESTS that
declaration rather than inferring one.** The distinction is not pedantry: reading a string an author
wrote is a different act from deducing what an author meant, and only the first has a defined answer
when it is wrong. Where a reader's projection *is* closure-held — a grid column with no `field`, whose
content is the closure; a closure row key — the harvested expectation is a **lower bound**, reported as
such and never completed by a guess.

**The `Ref` half is the same shape one layer down: the host declares, at registration, beside the
resolver that serves the rows.** The tempting alternative was to *resolve* the source and read the
schema off the table. It is refused: this family exists to answer a question before anything external
runs, and a check that calls a host's data resolver to decide whether a handler may run has already run
half the handler. An undeclared source degrades to "unknown" — reported, never guessed, and never a
refusal, because refusing a handler over a schema nobody declared would punish a host for not answering
a question it was never asked.

**Only a PROOF is a finding.** The walk refuses on what it can demonstrate — a column the query
provably does not produce, a step reading a column its input provably lacks, a union whose two sides
provably disagree — and everything it merely cannot decide is DATA on the report: a query whose output
is not statically closed (a pivot names its value columns from the data; an undeclared `Ref` names
nothing), and a reader whose projection is closure-held. That is `OpaqueHandlers`' choice, taken again
for its reason: a finding that fires on ordinary correct trees is one people learn to scroll past, and
then the real ones go with it.

**The runtime posture is untouched.** The dispatch-time diagnostic still carries the evaluation error's
discriminator and nothing else, because it is still the thing that runs while a wire-carried pipeline is
in scope. What changed is that the detailed error now exists *somewhere* — before the untrusted tree is
involved, where names cost nothing.

**What it forecloses.** A reader can only ever state a column expectation the RENDER vocabulary spells.
A node that needs a column for something other than display has no way to say so, and giving it one is
the declared-slot-schema option arriving by another door — it has to be argued here, not added as a
field the walk quietly starts reading. Recorded as an open gap rather than a limitation resolved: a
binding that TRANSFORMS a query slot client-side is a reader whose expectation is this walk composed
with its own pipeline, and that composition is not attempted.

## D11 — The evaluation suite lives outside this repository

**2026-08-22.**

A companion **evaluation suite** for this domain exists: a corpus of
program-emission tasks put to a model, whose emissions are gated by this
repository's own shipped machinery — the handler wire decoder, the
demanded-coverage check, the replay classification — with the refusal class each
produces carried through unedited. It does **not** live here, and the reason is
worth recording rather than leaving a reader to wonder where the tests went.

An evaluation suite names things this repository cannot. It pins model
identifiers and provider vocabulary; it consumes packages that are not published;
its corpus and its stored result cells carry both freely. This repository is
Apache-2.0 and written for an outside reader, and none of that belongs beside a
licence that invites the world to read it. So the suite sits in a **sibling
repository**, and what crosses the boundary is one-way: it consumes the
`Fuaran.Program.*` packages, and nothing here references it. Its location is a
maintainers' workspace concern, like the other cross-repo conventions this file
declines to ship.

**What this repository owes it is a forward-coupling obligation, not a
dependency.** The suite's whole design rests on the gate being *this* domain's:
its corpus's repair tier hands a model whatever the shipped decoder says about a
broken emission, its census clusters on the specification's own refusal classes,
and its provenance stamps name the codec assembly's version. So a change to the
refusal vocabulary, to the closed effect vocabularies, or to what a coverage
finding says is a change to what that suite measures — visible to it immediately
and to nobody here. That is the correct direction (a measured thing should not
have to know about its instrument), and it is why the obligation is recorded as a
decision rather than assumed.

**Why the boundary is drawn at the repository and not at a folder.** The
structured-document domain in this family reached the same conclusion and could
implement it as a folder, because its estate directory already held several
sibling repositories with room beside them. This repository *is* its directory:
there is nowhere inside it that is not inside the publishable artefact. A sibling
is therefore the only shape that satisfies the boundary, not merely the tidiest.

## D12 — The durable interpreter journals the ONE arm that reaches outside, and the indeterminate window is DECLARED rather than closed (2026-08-25)

A second interpreter of the same server-placement algebra runs handlers under **deterministic replay
over an effect journal**. Three things about it are decisions rather than implementation detail, and
each forecloses something.

**It calls the stage fold; it does not fork it.** `Durable.run` supplies a registry whose performers
consult the journal and then invokes `Handler.run`. The alternative — a second fold that journals as
it goes — was refused for D1's reason read one level up: two folds over one stage vocabulary is a
second evaluator of the handler algebra, kept in step by hand, and the parity claim between the two
interpreters would then be a coincidence somebody has to maintain rather than a property of the code.
The consequence is that the durable interpreter cannot change *when* a stage runs, only what a host
call means the second time round. A discipline needing a different phase order would have to argue for
it here.

**Only `HostCall` is journaled, and that follows from D8 rather than from convenience.** Of the five
arms, four never leave the interpreter: a query reads, an op edits an in-memory tree the caller may
discard, and a patch and a notification accumulate as values the caller performs after the handler
returns. A re-run cannot perform any of them twice because it does not perform them at all — it
RECOMPUTES them, from the entry state, which is what makes deterministic replay worth having rather
than an extra ledger to keep. So the exactly-once claim this placement makes is about **what it
performs**; what a caller does with a returned notification is the caller's own delivery posture, and
the composition joins the two rather than this package asserting it.

**The indeterminate window is not closed, and no configuration hides it.** A step is journaled twice —
attempted before the effect, decided after — so a crash leaves three readable states, and the third is
"attempted, no result": the effect may have happened and may not. It cannot be resolved here, because
the effect commits in a system this host does not own and the journal is a second system, and no
ordering of two writes to two systems is one transaction. **The default is to REFUSE the replay of such
a step**, on the same argument `Replay.strict` makes; `acceptingIndeterminateReplay` is the named
opt-in that re-invokes instead, and it returns an override record so a resume that overrode is
afterwards distinguishable from one that never needed to.

What that buys is the honest facet. A host call reaches `ExactlyOnceEffective` only where the host has
DECLARED the performer idempotent (or deduplicated by a store, and configured re-invocation); an
undeclared performer reaches it under no configuration, because strict refusal may lose the call and
re-invocation may duplicate it and neither of those is exactly-once. **The conjunction is stated over
the delivery HAZARD** — may lose, may duplicate — rather than over the three named facets, because the
named set is not closed under combination: a registration can prove both hazards, and the honest answer
there is that no facet says it, not whichever of the two reads better. A declaration may promise less
than the derivation; a declaration that promises more is refused, per axis. That asymmetry is the rule,
not an omission — a conservative promise costs only the promise.

**What it forecloses.** The step ordinal is derived from the fold, so a journal entry addresses a
position and not a name. A replay whose recomputation stages a different call list therefore REFUSES at
the first ordinal whose capability disagrees, rather than serving one call's recorded answer to another.
Making replay robust to a genuinely divergent recomputation would need steps to carry stable identities
of their own, which is a wire question and has to be argued rather than added.

## D13 — An out-of-band tree edit is a SEPARATE, default-closed entry point, and its refusal is a type of its own (2026-09-02)

**2026-09-02.**

`BoundedConnection` makes a bounded session servable, and with a servable session comes a question
the loop had not had to answer: may a tool *outside* the interaction loop — a tree inspector or
editor — submit a `TreeOp` against the session's tree? Four sub-decisions, each of which had an
obvious alternative that is wrong for a stated reason.

**It is a separate entry point, not a widened event.** The inbound event type is a closed
*interaction* vocabulary whose meaning is "the user interacted with node X", which the loop resolves
to an action and folds over the store. A tree op is categorically different: it mutates structure
directly, bypassing that fold. Carrying it as an event would have meant either a payload that can
encode arbitrary structure — collapsing the closure-free portable subset the whole inbound seam rests
on — or a second meaning smuggled into one type. `ApplyOutOfBand` is a distinct member, so nothing
about the interaction path changes shape.

**It exists on THIS loop and must not be added to a model-projection loop.** The bounded session's
`BaseTree` is fixed structural state, so an op applied to it re-resolves, diffs and lowers through
the ordinary path, and later interactions resolve against the edited tree. Where the tree is a
projection of a model, the next step's diff *reverts* the edit — so the same entry point there would
apply, appear to work, and silently snap back on the user's next click. That is a property of the two
loops rather than a gap in one, and the correct expression of an edit that must survive there is a
model change, which a tree op cannot express.

**Its refusal is its own type, not a case on `BoundedReject`.** The tempting move is to add a case,
and it is wrong twice. `BoundedReject` documents itself as the EVENT-level refusal and nothing else,
and an out-of-band edit is not an event; and it is a closed DU that a host matches exhaustively, so
widening it breaks every such match — a breaking change, taken to model something the type says it
does not model. `OutOfBandRefusal` is additive, and the two vocabularies stay legible as the
different facts they are.

**Its gate is a connection-level opt-in that fails closed, not a widened services record.** The
existing dispatch gate is typed over the interaction action and cannot express an attributed tree op,
so it could not have served; and adding a field to the services record would break every full-literal
construction of a public type. Installing a grant policy is therefore a member on the connection, and
an absent policy refuses everything — the same default `BoundedServices.create` takes for dispatch,
and for the same reason: this loop runs trees it does not trust, on infrastructure it shares.

**Attribution is carried and never believed.** The submitter's actor string is a claim made over a
channel the session did not authenticate. It is handed to the grant policy and echoed on the refusal
so a host can record it *beside* the principal it did authenticate, never in place of it. Anything
stronger would be the loop asserting an identity nothing established.

**What this forecloses, deliberately.** The refusal is returned to the caller rather than pushed to
the client, because the transport seam is push-frames outbound and fire-and-forget inbound: there is
no correlation id and no response envelope, so a refusal routed into a patch frame would be a
response smuggled through a broadcast. Delivering one to a *remote* submitter needs a correlated
response leg on the seam, which is the UI tier's to add and a wire change when it comes.
