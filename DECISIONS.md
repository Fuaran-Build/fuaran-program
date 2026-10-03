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

> **Amended by D18 (2026-09-27) — the direction clause.** Only the closing sentence, "the direction
> is re-examined at the D4 generic-tier cut", is superseded; D18 is that re-examination. Everything
> else in this entry stands as written, including "no `Fuaran.UI.*` package references
> `Fuaran.Program.*`".

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

> **Amended by D18 (2026-09-27) — the timing clause.** "cut only when a second domain instantiation
> materialises" is superseded: the cut is taken now, as a design decision. Everything else in this
> entry stands, including its warning, which D18 answers by naming each assumption it keeps.
>
> **Standing note, 2026-10-01 (Phase 1967).** A second domain instantiation now EXISTS, privately: a
> domain whose handler is a store-mutating verb, run under the unmodified 0.6.0 core. The contract
> admitted it without change and the run was correct, which is the half of this entry's warning that
> did not come true. The other half did: 25 of the contract's 31 members were vacuous or unfillable
> at those types, and three gaps forced the domain to build machinery beside Program that belongs in
> it — no member projected an op's reach, no shape of the view halted, and Program performed nothing
> of an op. D19 is the answer to each; `docs/generic-tier.md` §3.10 is the finding written out.

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

**Mechanism reference (Phase 1717).** The three clauses above are machine-checked in
`proofs/Staging.fst` — `plan_pure` (nothing external runs in the plan phase),
`residual_is_prefix` (on failure at position k the outcome is the entry state and `Performed` is
exactly the first k staged calls), `performed_in_order` and `commit_is_total_prefix` — over every
staged sequence and every point of failure, with the performer abstract; the differential host in
`tests/Fuaran.Program.Server.Tests/ProofOracleTests.fs` runs the extraction beside `Handler.run`.
The claims ladder in `proofs/README.md` says what that does and does not cover.

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

## D14 — When the witness-generic tier is cut, the model is written first; the evaluator is hand-written against it, and nothing extracted ships (2026-09-14)

**2026-09-14.**

D4 defers the witness-generic tier until a second domain instantiates the algebra. Phase 1715
brings this repository a `proofs/` kernel on the precedent the Core repository established — a
pinned F\* release, a model captioned clause for clause against the F#, the model extracted to F# and
run as a differential oracle beside production, and a README whose claims ladder is the only place
"formally verified" appears. That kernel is model-after-code, because the bounded fold it models
already exists. The generic tier is the one kernel in this domain that does not exist yet, so the
order of authorship is genuinely open, and it is decided here, before anyone writes a line of either.

**The model is written first.** When the generic tier is cut, its F\* model is authored from the
program wire specification's execution semantics and schemas before any evaluator code, and its
totality, its no-closure-invocation law and the `Chain` homomorphism are proved before the evaluator
is started. An algebra that is not total is cheapest to discover at that moment: the Core repository
found a validator hole with a machine-checked counterexample only after the code had shipped, and
had to file the fix as a separate phase. Here the theorem is the specification the evaluator is
written to meet.

**Nothing extracted ships, and the reason is performance, not purity.** The extracted model is the
oracle and only the oracle, exactly as the Core repository holds it. A model is shaped for the
prover: sets are lists walked by membership, recursion is fuelled by a list so termination is
manifest, there is no early exit and no tuned structure anywhere. That is right for a differential
host and wrong for this domain's evaluator, which is the inner loop of every server handler and every
client interaction, and which must also Fable-compile and run in the browser. Shipping it would also
delete the differential — with production equal to the extraction there is nothing left to compare,
and the F\* code generator, a second-class backend upstream, would join the product's trusted base
rather than the proof leg's. So the evaluator is hand-written against the model, the model is
extracted as the oracle, and the differential is what holds the two together.

**What this forecloses, deliberately.** No file under `proofs/oracle/` reaches a packed assembly; a
change that adds a reference from any `src/` package to the oracle project is refused on that ground
alone, whatever else it does. And the proof programme adds no cost on a production path: a theorem
whose closing would require restructuring the evaluator halts with the obstruction recorded in the
claims ladder, rather than reshaping the code it is about. The proofs exist to show the algebra is
robust, not to make the interpreter a chore to implement or slower to run.

## D15 — The signed effect envelope is verified by RECOMPUTATION; the signature attests the pairing, never the effects; the signer is the host's, by shape (2026-09-15)

**2026-09-15, Phase 1744.**

`Demanded.ofTree` and the server placement's `ofTreeAndHandlers` already answer "what can this
program ever ask for" as a document that can be stored where the tree never travels. Nothing signed
it, so a deployer handed a tree and an envelope held a claim, not evidence. `SignedEnvelope.sign`
signs the pair (canonical tree hash, demanded document bytes); `SignedEnvelope.verify` checks it.
Three choices in how, each made against the obvious alternative.

**The envelope is recomputed, never trusted from the signature.** A verifier decodes the tree,
re-derives the envelope through the same total walk, compares, and only then checks the signature —
over the preimage it just recomputed, never over the bytes it was handed. The alternative — check
the signature over the carried bytes and, if it holds, believe them — is what a signature usually
buys, and it is refused here because it would make the envelope's contents a matter of trust in the
signer's reading of the tree. They need not be: the walk is total and the vocabulary closed, so a
verifier can produce the envelope itself, and a document the verifier can produce is proof-carrying
data rather than testimony. The consequence is stated in the type: `verify` returns the RECOMPUTED
projection, and a consumer checking coverage afterwards does so against that, not against the
carried bytes.

**The signature attests pairing, not effect-safety.** What recomputation cannot give is that a named
key vouched for THIS tree paired with THIS envelope, and that is all the signature adds. It says
nothing about whether the demanded effects are acceptable; that remains `Demanded.check`'s question,
asked of a host's coverage, afterwards. A signed envelope naming a dangerous effect verifies
perfectly — the point is that the effect is NAMED, in a document the host can refuse on, rather than
discovered at dispatch. The three failures are therefore three facts: `EnvelopeDrift` (the document
does not describe this tree; a tree whose effects exceed its envelope is this, with the excess
enumerated), `BadSignature` (the document is exact, but the pair is not the one the signer made —
both tree hashes are carried so "the tree moved without moving a demand" reads differently from
"the bytes are forged"), and `ForeignKey` (the record names a key the verifier was not offered). A
verify with NO key is a fourth refusal, `NoKey`, and not a skip: a check that quietly degrades to
"the envelope matches" when no key is supplied is the check an operator believes they ran and did
not.

**The signer is the host's, consumed by shape, and no cryptography enters this repository.** Signing
goes through `Fuaran.Core.IAttestationSink`, the synchronous attestation seam the substrate already
carries — it signs an opaque string and answers with a key id and a signature, and the key never
crosses it. Verification takes the public key as the UI tier's `KeyDirectoryEntry` and the crypto
as its `IClaimSignatureVerifier`, whose ECDSA P-256 instance already exists there for .NET hosts.
The tree hash is the tier's Fable-clean SHA-256 over the tier's canonical encoding, so the preimage
is the same bytes on every runtime the interpreter runs on and `Fuaran.Program.Bounded` stays
Fable-clean with no gate. A new sink interface, a hash function of this repository's own, or a
`System.Security.Cryptography` reference were each the nearer thing to write and each a second
spelling of a seam that exists.

**Two smaller choices, recorded because each will be re-proposed.** The signed record carries the
demanded document's bytes VERBATIM as a string beside the signature, rather than splicing it in as a
nested object: the wire is unchanged, a consumer that ignores the signature hands `Envelope` to
`Demanded.decode` and reads what it read before, and the bytes the signature covers are the bytes in
the record with nothing re-rendered between. And drift is decided on DOCUMENTS rather than bytes: a
re-serialised copy that still says the same thing is the same document, and the signature — which is
over the recomputed bytes regardless — decides whether the pair was signed.

**What this forecloses, deliberately.** No trust decision about the KEY is made here. Whether the
offered key is one to believe — its lifecycle, its revocation, whose it is — is the host's key
directory's question, answered before the key is handed to `verify`; this repository checks a
signature under a key it was given, and nothing more. And the operator command that runs the check
lives with the tooling that has a tree and a key directory in hand, not here: this repository
delivers the two library functions it calls.

---

## D16 — The operator's controls are RECORDED OPS on a session stream, not imperative calls; a machine raiser and a person raise the same op (2026-09-19)

Suspend, throttle, revoke and resume are the three acts an operator reaches for first and the fourth
that undoes one of them. `InteractionBudget` refuses one costly interaction and D12's journal records
the one arm that reaches outside; neither can halt a session that is already running, slow one effect
kind, or withdraw a performer while a handler is live. This decision is how those four acts are
carried, and four things about it are decisions rather than implementation detail.

**The act is a record, and the state is a FOLD of it.** `Controls.fold` is a total function of the
entry list and of nothing else, so the state a resume reaches is the state any reader of the same
prefix reaches. The alternative — a flag on a session object, set by a method — was refused for the
reason D1 refuses a richer evaluator, read one level up: an imperative control cannot be replayed, so
a resumed session does not know it was ever stopped; it cannot be audited, so "what did the AI do"
omits the part where somebody stopped it; and it cannot be shown monotone, because there is no record
to be monotone over. Recording the act buys all three from one mechanism, and the cost is one port
and one fold. The consequence is that a control takes effect at the NEXT consultation and never
mid-effect: there is no way to express "stop this call", only "perform no more", which is what a
bounded, staged handler can actually honour.

**A machine-raised suspend and an operator-raised one are the SAME OP.** The raiser is a
`ControlActor` on the entry — `operator` or `machine` — and no rule below the record reads it. Two
op families would have meant two fold rules, two audit vocabularies, and two places for a fifth
control to be added to only one of; and the automated raiser is not a lesser authority, it is the
same authority with a different hand on it. So an automated detector that suspends a session (the
denial-pattern case) produces an entry indistinguishable to the fold from an operator's, and
distinguishable to an auditor, which is the only reader that should care.

**Each act is expressed in a vocabulary that already exists.** A suspend closes the session's own G1
dispatch gate, so a suspended step is refused through the same path and with the same
`ServerReject.Gate` shape as any other policy refusal; a throttle closes the effect gate over one
capability, so a breach is the ordinary structured `GateRefused` denial — which HALTS the handler in
the plan phase, and because every host call is staged to the perform phase (D8), a breach therefore
performs **none** of that handler's calls rather than some of them; and a revocation REMOVES the
performer, so it reads as `Unregistered` — the arm that says "the capability is absent from this
host" — which carries the withdrawal through `ServerCoverage` to the demanded-effect check with
nothing new to teach it. A new denial arm per control would have been the obvious design and would
have left every existing consumer of the two-arm distinction unable to see any of them.

**A `Resume` lifts the suspend and NOTHING else, and a revocation is never lifted at all.** The
monotonicity is a property of six lines — no arm of `Controls.step` removes a key from `Revoked` — and
not a promise about a lifetime, which is what makes it checkable over every prefix of a stream rather
than assertable in prose. Re-registering a withdrawn performer is a host act on a fresh session, and
that ceremony is the point. A resume that also cleared throttles and revocations would read as tidier
and would make the mildest word in the vocabulary the most consequential one.

**What this forecloses.** A throttle window is COUNTED, never timed: it is a per-invocation budget of
attempts, because a rate over wall-clock time folds to a different answer on every re-read and the
whole value of recording these acts is that it does not. A host that wants a time-based rate limit
owns that above this placement, where a clock is legitimate. The control stream is also keyed by
SESSION rather than by invocation, and is a second port beside `EffectJournal` rather than a field
added to it — the shapes agree, the keys do not, and filing a suspend under one invocation id would
make it invisible to the next one.

And the wire form is **deliberately a host document, not a specified one.** The program wire
specifies what a handler declares, what it may reach and what it reports; the control stream is what
a host's operator did to a session. Specifying it now would pin an encoding before a second host had
ever read one — the same posture the demanded-effect projection takes, for the same reason. It
round-trips canonically here so the acts are portable between this host's own stores in the meantime,
and a fifth control arm is refused at decode rather than admitted, so the closure is enforced and not
merely intended.

## D17 — The gate decides on ARGUMENTS as well as on the capability name; the policy is declared DATA, carried in the envelope, and refused through the vocabulary that already exists (2026-09-19)

**Decision.** A host declares an argument policy beside an effect registration — a closed set of
three clauses: an **allow-list** over one named argument, a **ceiling** on the declarative payload's
canonical bytes, and a **label** that is carried and decides nothing. `Handler.runEffect` checks the
effect's actual arguments against the clauses declared for its capability in the gate's own
position: after the capability is admitted by name, and still before a pipeline is evaluated, an op
reaches the apply engine, or a performer is looked up. A capability nobody constrained is
UNCONSTRAINED. The declared policy is joined onto the demanded document
(`ServerDemanded.ofTreeHandlersAndRegistry`) and is part of what `verifyWithRegistry` recomputes.

**Why the gate was not enough.** It decides about a NAME derived from the effect's own
discriminator, which is the whole decision for an effect whose arguments a host authored and not the
whole decision for one whose arguments carry a value that came off the wire. "`HostCall` allowed" is
not "`HostCall` to `api.example.com` allowed", and a permitted capability reaching an endpoint nobody
permitted is the confused deputy the escape-hatch inventory records as the residual on the host-call
hatch. The two halves compose in one direction only: a bound can narrow a capability the gate
admitted and can never widen one it refused, and a capability refused by name never has its arguments
examined at all.

**Why DATA rather than a second predicate.** `Gate` is a closure: it can be asked and it cannot be
read. A bound written down can be read back, carried in the demanded document, and put in front of a
deployer before anything runs — which is the difference between a record that says "HTTP" and one
that says "HTTP to api.example.com, ≤ 64 KB". It is also what makes the bound VERIFIABLE rather than
merely attestable: because the policy is recomputed from the registry, a host that has since widened
an allow-list, raised a ceiling or dropped a clause presents as drift. Signing the demand without the
policy would have left a verifier able to read what a host once claimed and unable to tell whether it
still held.

**Why the refusal reuses `Failed(capability, reason)` and adds NO denial arm.** The denial DU is
wire-specified — `Unregistered` and `GateRefused` are a closed `oneOf` in the program wire's outcome
schema — and neither is true here: the capability exists and the gate admitted it. A third arm would
have been a five-artefact change across two repositories (normative text, schemas, resident emitter,
manifest, host codec) to say something the existing halt already says exactly: the landing-slot
refusal beside it is the same class of check — a declarative bound on a declared argument, applied
while planning — and reports the same way. The `reason` member is an unconstrained string in the
schema, so `argument-not-allowed:<argument>` and `payload-over-ceiling:<limit>` are conformant bytes
on the day they ship.

**What a refusal may say.** The host's own DECLARATION and nothing measured from the payload: the
argument the host constrained, never the value found there, and the host's limit, never the size that
met it. A measured size is not the payload, but it is a fact about it, and an argument check is the
one place in this placement that reads a wire-supplied string — the seam to hold that line at, not the
one to concede it at.

**The document version moved to 4**, on the argument versions 2 and 3 already made rather than by
analogy to it. `constraints` is present on EVERY server tier from here on, `[]` where nothing was
constrained, so an ABSENT key says "this producer predates the policy" and an EMPTY one says "this was
read and nothing bounds these capabilities". For this member the collapse is not merely lossy: it
turns "I cannot see the bounds" into "there are none", which is the opposite of the safe reading.

**Two limits, stated rather than assumed.** A host call's arguments are read ONE LEVEL DEEP and
string-valued only, so a host whose performer reads a nested member cannot express an allow-list over
it — the bound belongs on the top-level argument the performer takes, or the performer belongs behind
a narrower registration. And a ceiling bounds a DECLARATIVE payload — a host call's arguments, a
notification's payload — never an op sequence: those are not a `JVal` at that point in compile order,
and a ceiling that silently meant one thing for three arms and another for two would be worse than one
that names the two it does not cover.

**An allow-list an effect names nothing under is VACUOUSLY satisfied**, and that is the correct
reading rather than a lenient one: a bound says what may be reached under that name, and an effect
naming nothing there reaches nothing there. A host wanting the argument to be mandatory is asking for
a clause it did not declare.

## D18 — The generic tier is cut now, as a decision; the core is parameterised by a witness whose centre is an ACTION VIEW; the UI binding becomes an adapter (2026-09-27)

**2026-09-27. Amends D4 (the timing clause) and D5 (the direction clause). Both entries stand
otherwise.** The evidence is in [`docs/generic-tier.md`](docs/generic-tier.md): the per-member
inventory, the contract written out as types, the two adapter homes, and the consumer measurement.
This entry records only what binds.

**The cut is taken without waiting for its trigger.** D4 said the generic tier would wait for a
second domain to instantiate the algebra. That has not happened. The dependency is being cut anyway,
because this domain and the UI tier are separate sovereignties, and D4's own opening paragraph
already made them so "from birth". A trigger that has not fired does not make the coupling right; it
only makes it cheaper to leave in place. D4's warning still holds: an abstraction with one witness
bakes that witness's assumptions in. The answer is not to wait. It is to **name every assumption the
contract keeps, with the evidence that would falsify it** (K1–K8 in the note), so none of them ends
up in the contract without a record.

**The contract.** `Fuaran.Program.Bounded`, `.Runtime` and `.Server` are parameterised by one record
of functions, `ProgramWitness`, in the style Core already uses for `NodeWitness` and `StreamWitness`.
It has six parts:

1. **tree**: Core's `NodeWitness<'Node, string>`, reused unchanged, plus per-node handlers,
   re-resolution, cost, query readers, binding uses, and a canonical encoding.
2. **action**: a total `View` onto four shapes: `Sequence`, `Assign`, `Call`, and `Leaf`.
3. **expression**: resolution against the store, with three outcomes.
4. **store**: assign a state key, land a query result, and a reserved-namespace predicate.
5. **op**: Core's `StreamWitness<'Op, 'Node, _>`, reused unchanged, plus `Diff` and `AbsoluteTarget`.
6. **effect and claim**: the effect's kind, destination and codec, and a claim verifier that is
   generic in the key.

Nothing is added to `Fuaran.Core.*`. Each of the three things Core lacks (a state store, expression
resolution, a claim verifier) has exactly one witness today. Promoting any of them into Core would
repeat D4's error one layer down.

**The fold interprets the view, and only the view.** It owns sequencing, the one store write, the
reserved-namespace refusal, D9's refusal of a declared result target, and D7's handler-effect arm. It
never recurses into a `Leaf`. A leaf lowers to **at most one** effect, or is refused, or is declined,
and it **never writes the store**. That is D1 exactly: control structure is the evaluator's, and
vocabulary lowers to it. Those constraints also keep `run_total`'s "at most one effect, at most one
key" true as stated. The budget, the demanded projection and the replay classification move from wire
tags onto the view, which removes their string coupling to `"Call"`, `"Chain"` and `"SetState"`. The
UI's fourteen-case totality check, together with its `#nowarn "44"` scope, moves into the adapter's
`View`, where the compiler still checks it.

**D14 applies to the new part.** The cut re-types an evaluator that already exists, so the model that
has to come first is the model of the view. Phase 1896 re-states `BoundedFold.fst` over the four view
shapes and re-proves totality, no-closure-invocation and the `Chain` homomorphism **before** it ports
`BoundedActions.fs`. No-closure-invocation becomes an obligation on the witness: the core holds no
closures, so only a witness's `View` or `Lower` could reach one. The differential oracle runs through
the UI adapter.

**D5's direction clause is resolved: no core package references `Fuaran.UI.*`, and a test enforces
that.** The three core packages take direct `Fuaran.Core.*` references in its place. They currently
reach `Fuaran.Core.Wire`, `.Column`, `.DataFrame` and `.OpStream` only transitively, through the UI
packages. The remainder of D5 holds unchanged: **no `Fuaran.UI.*` package references
`Fuaran.Program.*`.** An application in the UI tier that consumes this domain is a consumer, not a
package, and D5 never ruled that out.

**The adapter's home — A, ratified by the operator on 2026-09-27.** The adapter is two packages, split
along the core's own Fable/.NET line. One is Fable-clean and holds the UI witness plus the UI
transport loop (event validation, `DomPatch` lowering, the live connection, the client runtime). The
other is .NET-only and holds the server placement's UI event step. There are two candidate homes:

- **A, this repository** (`Fuaran.Program.UI`, `Fuaran.Program.Server.UI`). This keeps D5's direction.
  It releases core and adapter as one version in one commit. The existing suite and the scenario
  corpus stay here, run by project reference, so no skew is possible.
- **B, the UI tier** (an adapter package there that references this repository's core). This
  repository then names no UI package at all. But it reverses the surviving half of D5, ties the UI
  tier's lockstep releases to this domain's cadence, and forces the UI-typed half of this
  repository's suite and the client-effect family's certification to move out of the repository
  that owns the behaviour. Keeping them here beside a UI-tier adapter would compile the core twice,
  which is D5's skew class.

**Recommended, and ratified: A.** It is the smaller correct change, and moving later from A to B moves two leaf
packages, while moving from B back to A would move tests and a certified wire family. The
recommendation flips to B if "separate" means this repository names no UI package *at all*, rather
than only that its core does not. That was the operator's judgement; the operator ratified A on
2026-09-27 (the core must not reference UI; two leaf adapter packages here may). **Phase 1896 does not depend on the choice.** It needs only a test
witness. Phase 1897 builds the adapter wherever this clause lands.

**Consequences.**

- **Version.** All packages move to `0.6.0`, a pre-1.0 minor. It is breaking for the three core
  packages, whose public types gain type parameters. The adapter packages are new, and core and
  adapter release together. The adapter re-exposes today's names as closed aliases and partially
  applied modules, so for most consumers migration means changing one reference and one `open`.
- **Migration set.** It was measured across the consumers, not taken from the plan, and it is
  **five, not three**:
  - a composition consumer's effect-envelope verifier and its generated-application template
    (`Bounded`, and `Server` for the template). Its verifier opens UI namespaces it never declares,
    and this contract lets that verifier go UI-free, since the claim verifier is generic in the key
    and the tree walk arrives as a witness. Its composition root then declares the adapter;
  - the out-of-repository evaluation suite (D11) (`Bounded`, `Server`). It opens no UI namespace and
    needs the server adapter only because its corpus is UI trees;
  - a downstream application with a server placement (`Bounded`, `Server`). It raises as its own act;
  - two applications in the UI tier (`Runtime`; `Bounded` and `Runtime`).

  The consumers' current pins already disagree (`0.1.0-alpha.1`, `0.4.0`, `0.5.0`). Raising each one
  is that consumer's own act.
- **The program wire specification does not change.** No schema, fixture byte or rule changes. Its
  §3 already treats the action and tree-op algebras as referenced vocabularies "specified elsewhere",
  spliced byte-stably by their own canonical encoders. The envelopes around them are this domain's own
  and are UI-free today. The UI witness fills each referenced position with the same encoder as today.
  The evidence is the codec families passing byte-identically through the core and the adapter, and
  the twelve driver scenarios passing through the adapter's loop. **Any difference in a fixture byte
  falsifies this clause**, and it means the refactor changed behaviour, not only where the types
  live.

## D19 — The second witness's findings land IN Program: a halting guard in the view, an op's reach on the op witness, a registered op performer in the perform phase, and the op channel's rejection stays `string` (2026-10-01)

**2026-10-01. Phase 1967. Amends K2 of `docs/generic-tier.md` §3.7 and the D8 reading of which arm
is staged; D4's standing note records the instantiation this answers.**

The first second-domain instantiation of the generic tier — a handler that is a VERB, with store
reads, file writes and deletes, a commit and a push as the `'Op` of `ApplyOps` — ran under the
unmodified 0.6.0 `Handler.run` and was correct, and built three things beside Program to be so: its
own walk over its ops to say what the handler reaches, guards that rode the op channel as apply
errors so a refusal could halt, and a two-phase discipline of its own so a refused plan performed
nothing. Each is the drift D4 warned about, pointed the other way: a second demanded projection and
policy growing in parallel to Program's. This entry moves all three into Program, and records the
one thing it leaves as it was.

**1. The halting refusal is a shape of the view, `Require of condition: 'Expr` — option (a).**
Halting is control structure: "nothing after this runs" is a statement about the sequence, and D1
puts control structure in the evaluator and vocabulary beneath it. A handler-stage guard (option b)
would have halted too, but at the placement rather than in the algebra — a browser placement would
have had no guard at all — and it would have been a THIRD stage kind, which the program wire
specification's §4.2 closes at two and whose widening is a specification act across five artefacts.
A view shape is the action algebra's, which the specification references and does not spell; no
wire moves. The guard's condition resolves through `ExprWitness.Resolve` exactly as an `Assign`'s
`from` does: the boolean `true` holds, any other value, an unresolved condition and an errored one
halt, and an errored one's text is the halt's reason verbatim — so a domain's typed refusal reaches
the diagnostic through the arrow the contract already had. **A guard reads the state channel** (K4):
what a guard needs to see is where a verb's arguments and read results land, and a domain that keeps
its model in the tree exposes what its guards read through the channel. The fold's outcome says
`Halted`; the fold does not roll back — the store is the store as of the halt, and the placement that
rolls back is the handler, whose atomicity unit it is (D8). A leaf's `Refuse` keeps its non-halting
behaviour; nothing at the UI witness views as a guard, and `ui_never_halts` proves the UI tier
unchanged. K2 now reads: control structure is sequence + assign + call + require, and everything else
is a leaf. **D14 applied: `BoundedFold.fst` was restated over five shapes and re-proved before the
port** — the sequence homomorphism is the one law the shape changes, gaining a halting clause that is
vacuous for every view without a guard.

**2. An op's reach is a member of the op witness, `Reach: 'Op -> OpReach`, read by the argument
policy and the demanded projection.** `OpReach` is the op's named arguments — the `(argument, value)`
pairs `ServerArgumentPolicy.arguments` already returned for a host call — and its destination class
in the client effects' own `EffectDestination` vocabulary (W4: the reach-describing member sat on the
wrong channel; it now sits on both). `ServerArgumentPolicy.arguments` reads it for `ApplyOps` and
`EmitPatch`, so an `AllowList` on either arm binds, and the destination class is allow-listed under
the reserved `destination` argument, so a policy can bound WHERE an op may reach without knowing how
a domain names it. `payloadBytes` measures an op sequence as its ops' canonical bytes, so a `Ceiling`
binds too. The demanded document carries the reach as a server-tier member at **version 5** —
present and empty where the ops name nothing, on the argument versions 2, 3 and 4 each made — and
`ServerDemanded.ofEffect` reads it through the policy's own extraction, so the document and the
enforcement are one enumeration. The reach is DESCRIPTIVE, like the replay posture and the declared
policy: no coverage finding is computed from it; the argument policy is the enforcement. The UI
witness's reach is the nodes a tree op addresses, under the op's own member names, and deliberately
not every string member: a prop path, a binding slot and a prop value are what an op writes, and a
reach the document carries must never be a payload. A consequence stated rather than discovered: a
signed envelope is verified by recomputation, so every envelope signed under version 4 reports
`Unreadable` drift under this reader, naming the version — the honest refusal — and is re-signed.

**3. Program performs an op through a REGISTERED op performer, in the perform phase — the hook, not
the stated plan-only contract.** `OpPerformance<'Op>` is `InMemory` — the apply is the effect, the UI
tier's placement and every placement's default — or `Performed of ('Op -> Result<unit, string>)`.
Under a performer `ApplyOps` is a staged arm: its ops are applied in memory while planning, as always,
so a later stage reads the planned tree, and each op is staged as a call of its own — the registered
performer closed over the op, in the shape a staged host call carries — and performed after the plan
commits, in plan order beside the host calls. D8's law then covers ops with no new vocabulary:
`Performed` names `ApplyOps` once per op PERFORMED in execution order, a part-way failure is a
`PerformFailed` under that capability with the prefix that ran reported, and `Staging.fst` proves it
over the same staged list, its `plan_pure` gaining the one clause that matters — the plan phase reads
only whether a performer is registered and what it stages, never what it answers. The hook was
chosen over the documented plan-only contract because the contract would have widened `PerformFailed`
to describe a host-performed plan, which is an outcome-wire vocabulary change the specification
spells, while the hook changes no wire at all; and because that domain's placement had already written
the hook once, beside Program, which is the measure of where it belongs. The one specification
sentence a registered performer reads past — §6.2's "only `HostCall` is staged" — describes the
in-memory placement, which every conformant host had and every UI host still has; carrying the
performer case into the normative text is a specification act and is not taken here. The durable
interpreter (D12) runs in memory and journals no performed op; a verb under the durable tier is
stated as not covered rather than half-covered. `Handler.run` keeps its signature as `runWith` at
`InMemory`; `ServerServices` carries the performance and the session's arm passes it.

**4. The op channel's rejection stays `string` (W5).** A seventh type parameter for the rejection
would reach every public type that names the witness, the handler's halt vocabulary, the outcome wire
— where a reason is a string by specification — and the journal, to carry a type that crosses a
process boundary as text in every one of those places anyway. What a typed refusal does instead is
what the first second witness did: render it canonically (`Canon.render` over its own JSON) into the
string the op channel or the guard's `Errored` carries, and parse it back on the far side. The
in-repo second witness pins that crossing (`tests/Fuaran.Program.Tests/VerbWitnessTests.fs`, W5).

**What this forecloses.** A guard that recurses — a conditional with two non-refusal arms — is still
a new view case plus a model change under D14, never a leaf that threads the store (K2's falsifier
stands). An op performer is trusted on the terms a host function is; the gate and the policy decide
what reaches it, and nothing here says what it does when invoked. A reach that could carry a payload
is a witness defect, not a vocabulary one.

**The regression test is the second witness itself.** The first second witness's differential and adversaries
are the test for whether its parallel machinery can now be deleted; re-running it against 0.7.0 is
that domain's act. The in-repo second witness — a verb over an in-memory file map, with an adversary
per finding — is this repository's own, and runs in the ordinary gate.

## D20 — The contract is cut THREE ways: a required state axis, an optional walk axis, an optional dispatch axis; the guard and the performer move onto the state (2026-10-01)

**2026-10-01. Phase 1974. Supersedes D18's single `ProgramWitness` record and amends D19 decisions 1
and 3; rereads K4 of `docs/generic-tier.md` §3.7.**

Three witnesses have now measured the contract: the UI tier, which fills every member; a verb
(D19), which filled six of thirty-one meaningfully; and a document pipeline under a server placement
— a domain whose state is its tree — which filled fourteen of thirty-two. The members vacuous for
both non-UI witnesses are the same set. This entry cuts the contract along it.

**1. Three records, composed; the composition's type says which are filled.** `StateWitness<'Node,
'Op>` is REQUIRED — `Stream`, `Reach`, `AbsoluteTarget`, `Canonical`, `Diff`, `View`: the state the
ops apply to, what an op reaches, its canonical form, how a refusal crosses (D19 decision 4,
unchanged), and which ops are guards. `WalkWitness<'Node>` is OPTIONAL — `Nodes`, `Traverse`,
`Cost`, `QueryReaders`: the read-only walks, independent of dispatch, which is why it is an axis and
not half of the next one. `DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>` is OPTIONAL —
`Handlers`, `Events`, `Resolve` and the `Action`, `Expr`, `Store` and `Effect` sub-records: what an
event-driven fold over a tree needs, so a domain that fills it is saying it has events.
`ProgramWitness<'Node, 'Op, 'Walk, 'Dispatch>` composes them, an unfilled position holding
`Unfilled`. A core path names the axes it reads in its signature, so a path that reads an axis a
composition does not fill is a COMPILE error, never a runtime default standing in for a member nobody
wrote. The paths that must run under every composition — the fold, the server handler, the server
demanded projection — read the dispatch position through `IDispatchPosition`, which `Unfilled`
answers at action and effect types that have no values (`Nothing`) and a `unit` store: a handler
there cannot hold a compute stage, because there is nothing to put in one, and a landing slot has no
binding channel to land in, so it is refused while planning (`no-binding-channel`).

**2. Why the two-axis proposal was wrong, on two data points.** The verb suggested "tree optional,
ops required" because the verb had no tree. The document has a tree and fills six of its eleven tree
members faithfully — and nothing on its path reads them to any effect but the walk and the hash: no
handler is reached from a document node, nothing budgets one by its handlers, nothing re-resolves one
from a store. (a) That cut would have had the document fill a whole axis for nothing, because the
members it lacks — handlers, events, the action view, the store, the effects — are not the tree; they
are DISPATCH. (b) It would have left both capabilities the document actually lacked where they were:
the guard, which resolved against the store and could not see the tree, and the performer, which was
handed the op and never the state. Both are about the STATE, and the cut that puts them there is the
one that names a state axis.

**3. The guard moves onto the state: `OpView.Require` (F-GUARD; amends D19 decision 1).** D19 put the
halting refusal in the action view and had it read the state channel, saying a domain that keeps its
model in the tree exposes what its guards read through the channel. The document showed what that
costs: nothing moves the planned tree into the store, so a handler that planned a banned edit and then
required the pack committed and published it, while the same check as an op refused it. The op
channel now has a guard shape: an op the state witness views as `Require` is resolved through the
op's own apply against the state AS OF ITS POSITION IN THE PLAN — `Ok` holds and the state does not
move (the answer is discarded, so a guard cannot write), `Error reason` halts with `reason` verbatim as
the `ApplyOps` arm's existing `Failed`, the same W5 crossing — and it is never staged and never
performed. Its demanded-projection contribution is its reach under `ApplyOps`, as an edit's is, so
the argument policy binds a guard on the same terms. The fold's `Require` stays: it is the right guard
for a domain whose guards are over the binding store. No view shape, stage kind, effect arm or
diagnostic was added. `guard_holds_moves_nothing` and `guard_refusal_halts` in `Staging.fst`.

**4. The performer is handed the state (F-PERFORM; amends D19 decision 3).**
`OpPerformance.Performed` is `'Node -> 'Op -> Result<unit, string>`: each edit is staged with the
planned state with that edit applied. For a verb whose op is the act this is unused; for a tail that
persists what the plan produced — render the document, commit the ops — it is the difference between
being handed the plan and folding the ops again from the entry state in the trusted base, which is
the plan phase run twice, once inside Program and once outside it. `Performed`, the `PerformFailed`
prefix report and the two-phase discipline are unchanged; `performer_handed_the_plan` proves the last
edit's state is the plan's final state, and `plan_pure` still holds because the plan reads only what
the registered performer STAGES.

**5. K4 is a dispatch-axis fact.** One mutable state channel beside the tree is what an event-driven
fold writes; the verb has no such channel and the document keeps its bound values inside itself. The
channel lives on the dispatch axis and nowhere else.

**What a fourth domain reads, per axis.**

- **State — always.** Six members, every one meaningful for any domain with ops: apply / encode /
  decode, reach, absolute target (replay), canonical form (the signed envelope's hash), diff (the
  session loop's; an empty diff is legal and stated), and which ops are guards (`OpView.edits` if
  none). Obligations: the apply is total and pure; the encoder is canonical (K6); a typed refusal
  renders canonically into the reason and parses back (W5); a reach value is a name the op reaches,
  never what it writes (D19).
- **Walk — if the state is a tree you want priced, walked or projected.** Obligations: `Children` and
  `ReplaceChildren` invert each other; ids have a faithful string form (K1); `Cost` is the node's own
  data cost, and the saturating arithmetic is the core's.
- **Dispatch — only if nodes carry handlers that events dispatch.** Obligations: the view unfolds a
  finite tree and its laws are the fold's (K2, K3, K5, K8); a leaf writes no store; the reserved
  namespace is the witness's predicate. A domain without events fills `Unfilled`: its reads and
  guards are ops, and its tail is a performer handed the plan.

**D14 applied a third time:** `proofs/README.md` gained "The three axes" — each theorem's members and
so the axes it needs — before a `.fs` file moved; `Staging.fst` restated its `ApplyOps` arm as one
fold and gained the three theorems above, every earlier statement unchanged; `EffectGate.fst`
followed.

**What this forecloses.** A member added to the contract names its axis, and a new core path names
the axes it reads; a path that reads dispatch "if present" is the defect this entry removes. A guard
that must thread a store through sub-actions is still a view shape and a model change under D14 (K2's
falsifier stands), on the dispatch axis.

**Version.** Rides the `0.7.0` draft: the slot is untagged, publicly unpinned and already breaking,
and a draft moves only for a change of a higher class (`STABILITY.md`). No wire member moved
(`docs/generic-tier.md` §6). The in-repo witnesses are the regression test: the UI adapter
byte-identical through the parity suite, the verb filling the state axis only, and a document
pipeline filling state and walk, with the third instantiation's two guard tests inverted and its
performer's re-fold removed (`tests/Fuaran.Program.Tests/`).

## D21 — The core algebra gains selection and bounded iteration on BOTH axes, designed for reversal; the reversible fragment is decided from the tree and undone by trace (2026-10-01)

**2026-10-01. Phase 1976. Fulfils D1 and D2; takes the route D19 named for a conditional ("a new
view case … under D14"), for selection and bounded iteration together; amends K2 of
`docs/generic-tier.md` §3.7 a second time; leaves B2 (`OpReach.Destination`) single-valued, with the
reason below.**

D1 fixes the core as sequencing + typed branching + named effects and D2 permits bounded iteration,
yet the generic tier's view had sequence, a store write, a call, a halting guard and the domain's
leaves — one structured-programming construct, plus abort. It went unnoticed because the only
witness was the UI tier, which branches in the TREE and repeats through data binding. The second
witness's re-run — a domain whose handler is a store-mutating verb — met the gap at its first verb
with an arm that does not refuse, and had to carry its own two-arm construct beside Program: the
parallel-machinery drift D19 existed to remove. This entry closes it, under D14 (the model first,
`proofs/BoundedFold.fst` and `proofs/Staging.fst` restated and re-proved before a `.fs` moved), and
records what the model argued for.

**1. Two shapes, on both axes, MIRRORED rather than shared.** `ActionView` gains `Choose(entry,
whenTrue, whenFalse, exit)` and `Repeat(bound, body)`; `OpView<'Op>` gains `Choose(entry, whenTrue,
whenFalse, exit)` over ops and `Repeat(count, body)`. The phase's default was the op axis alone, so
a state-only domain reaches them; the model argued for both, because the second witness's branch is
over OPS while the reversal theorem the phase charters is about the STORE the dispatch fold writes
(`Assign` is the one destructive step, and the one that needs a trace). One shared definition was
considered and rejected: the condition channel differs per axis — an expression resolved against the
binding store to a value, versus a guard op applied for its answer — and the state axis has no value
channel a parameter bound could be read from, so a shared shape would offer a case one axis must
refuse. D20 set the precedent with two guards, one per axis, "the guards of two different states";
the two selections and the two repeats are the same thing. D1's "one core algebra" is kept where it
matters: one evaluator per axis, one law set, and `repeat_is_unrolling` / `repeat_plans_as_unrolling`
tie each repeat to the sequence of its body so no sequence law is restated.

**2. Conditions, and the exit assertion.** On the dispatch axis the entry condition resolves exactly
as a guard's: the boolean `true` takes the true arm, any other value the false arm, and an unresolved
or errored condition HALTS before either arm (a branch that cannot decide is a defect, not a default;
the phase's "argue against halting" was argued and lost — the alternative, a default arm, silently
turns a resolver fault into a chosen branch). On the op axis the channel has two answers and no
third: `Ok` is true, `Error` is false, so a domain's typed refusal is the false value and never a halt
— the shape the verb's "already migrated / not yet" arms want, and the one a guard's protocol already
reads. The EXIT assertion (Janus; Lutz and Yokoyama) is optional, resolves against the store the arm
left, and must hold after the true arm and fail after the false arm; violated, unresolved or errored
it halts AFTER the arm with the assertion named in the reason — the arm's writes and effects stand as
of the halt, the handler rolls back (D8). Forwards it costs a program nothing to omit; omitted, the
branch is outside the reversible fragment, because nothing then says which arm to undo.

**3. The bound, and no index.** On the dispatch axis `Bound<'Expr>` is a literal count or a
parameter resolved ONCE at entry to a `JInt` and checked against `[lo, hi]`, outside which the repeat
halts before its first iteration — the over-bound refusal — so the budget can price it at `hi`
without the store. On the op axis the count is a literal the view produces: D2's range check is the
domain's, at its codec, and a deployer's ceiling is the argument policy's, through a `count` argument
on the repeat's reach (the verb witness does exactly this, and an off-list count is refused before
anything performs). The body sees no index: its one channel to state is the store it writes, and an
index it could overwrite would not be a function of the bound alone, which running the inverse the
same number of times rests on. A negative count is refused on both axes — the one guard the model,
whose count is a `nat`, cannot express, so it is the code's and is said to be.

**4. The budget.** A branch is priced at one step (its condition) plus the MORE EXPENSIVE arm; a
repeat at one step (its bound) plus its body times the bound, a parameter bound at the top of its
range. `fold_steps_within_cost` proves a run never takes more steps than this prices, so the price
computed from the tree before the run bounds the work the run does — "safe to run untrusted" extended
to the two shapes.

**5. The demanded union, the policy, and B2.** A branch demands the union of its entry, BOTH arms
and its exit; a repeat its bound and its body once. `OpView.beneath` enumerates the ops under a flow
op, and `ServerArgumentPolicy.reachOfOp` reads an op's reach over itself and those, so an untaken
arm's reach is still reach and a sequence that reaches an off-list path in either arm is refused.
B2 — whether `OpReach.Destination` becomes a set because a branch's arms may reach two destinations
— is answered NO: a branch reaches nothing of its own, each op beneath it names one destination, and
the policy reads them one by one; a set on the branch would be a second enumeration of what the arms
already say. The verb's `Branch` reaches `OpReach.nothing`.

**6. Reversibility, designed in; `Assign` by trace.** The reversible fragment is SYNTACTIC —
`reversible` reads the tree and nothing else: sequence, assign, the guard (its own inverse), a branch
WITH an exit assertion, a repeat with a LITERAL bound; never a call or a leaf (effects are a property
of the op and a later phase's). `Assign` destroys the old value, so it joins the fragment BY TRACE
rather than by construction (the Bennett embedding): a reversible run records, at each write, the
value it overwrote, and the inverse restores it with an ordinary assignment. This is the one place
trace is preferred over construction, and the reason is that the alternative — a reversible
assignment, `x += e` — is a different store algebra than K4's keyed channel, which every witness
already fills. WHERE the trace lives: beside the outcome of `BoundedActions.runTraced`, which the
plan phase holds exactly as it holds the pre-state; the forward `run` records nothing and reads
nothing, because the two are ONE fold with a tracing flag (D1's one evaluating `match` is kept; the
model has `fold` and `fold_traced` and proves them equal in outcome). WHAT it costs the contract:
`StoreWitness.Read`, the one new member, called by the reversible run alone, with K4's
read-after-write law as the witness's obligation. WHAT it refuses: a key that was absent before the
run cannot be restored by an assignment (the store has no delete), so the trace says so and
`Trace.restorable` refuses the inverse rather than building it wrong — `reverse_run` is conditional
on it, and named as such in the ladder. The inverse of a run is a program the core folds through the
same evaluator (`Reversed`, `runReversed`): a sequence inverts to its members' inverses in reverse
order; a branch to the branch whose entry is the exit assertion and whose exit is the entry
condition, the arm that ran inverted and the other EMPTY (nothing was recorded for it, and the exit
assertion is what guarantees the inverse never takes it); a repeat to the SEQUENCE of its
iterations' inverses, because each iteration overwrote different values.

**7. What halts.** Three shapes now halt — a guard, a branch (an undecided condition, a violated
exit assertion), a repeat (an unreadable or over-bound count) — so Phase 1967's "only a guard halts"
is false over the widened view. `fold_no_halting_shape_no_halt` is the general statement and
`fold_no_require_no_halt` keeps its name as the corollary for a view without the new shapes, which
is every view the UI witness produces (`ui_view_no_flow`, proved; tested over the arm-complete
corpus) — so every UI theorem keeps its unconditional form and the UI tier is byte-identical through
the parity suite and the Fable leg.

**8. D14 applied a fourth time, and two findings for the next restatement.** The op view in
`Staging.fst` is now taken to exhaustion, as the action view has been since Phase 1898, so planning
terminates structurally and the finite-unfolding obligation sits on the witness's `View`. Two things
the restatement found about the prover, recorded so they are not re-found: inside a RECURSIVE lemma,
an application with many implicit type arguments (`plan_view w cap stage x tree staged`) does not get
its result's inversion — a `match` on it fails as "patterns are incomplete" — and a let-binding with
an explicit type repairs it; and an extractable definition keeps its tuple patterns (`ROk (tree',
staged')`) because `fst`/`snd` would extract to a runtime name the `Prims` shim deliberately does not
carry, while the lemma bodies that reason about it project with `fst`/`snd`.

**Version.** Rides the `0.7.0` draft: the slot is untagged, publicly unpinned and already breaking,
and this is breaking of the same class — every exhaustive match over `ActionView` or `OpView`,
`OpView` becoming `OpView<'Op>`, `StoreWitness` gaining `Read`. No wire member moved (`docs/
generic-tier.md` §6); the demanded document stays at version 5, carrying both arms' reach only when
a program uses a branch. `STABILITY.md` has the consumer's account and `docs/migrations/phase-1976.md`
the diff.

**What this forecloses, and what it leaves.** Whether an OP can be undone is a property of the op,
declared on the state witness by the phase that owns the undo posture; this entry gives the flow
algebra its inverse and nothing else. An op-axis parameter bound would be a value channel on the
state axis — a D20-class change, not a view case. The UI adapter adopts neither shape; a UI handler
that would genuinely benefit lands as a finding against `ui_view_no_flow`'s test, never as an
adoption here.


## D22 — The undo posture: a handler is classed reversible, compensable or one-way from its declared form before it runs, every edit's inverse is computed from the pre-state the plan holds, and the undo is a handler run through the same gate and performers (2026-10-01)

**2026-10-01. Phase 1977. Follows D21: 1976 gave the flow algebra its inverse and left whether an
OP can be undone "a property of the op, declared on the state witness by the phase that owns the
undo posture". This is that phase. Adds the seventh member of `StateWitness`, the trail a run
leaves for its undo, the classifier and the undo run; moves the demanded document to version 6;
adds K9 to `docs/generic-tier.md` §3.7.**

Program already answers one question of this shape before a handler runs: `Replay.fs` classes
each handler `safe | unsafe | unknown` from its declared form, and the demanded document carries
it, so a host decides whether a session may be RESUMED without running anything. The other
question a deployer asks of a verb that writes files and pushes a branch is "if it runs, can it be
undone — and up to where?", and nothing answered it: an undo was a rollback someone wrote by hand
against each domain's store, or nothing. This entry answers it on the replay posture's terms —
read before the run, carried in the document, enforced at the placement — and records what the
model (`proofs/Undo.fst`, D14 applied a fifth time: restated and proved before a `.fs` moved)
argued for.

**1. The member, and the shape the model argued for.** The phase's shape to argue against was
`Undo: 'Op -> 'Node -> UndoClass<'Op>` with `Inverse of 'Op | Compensate of 'Op | OneWay of
reason`, the class computed against the pre-state. The model argues for the CLASS as a function
of the op alone and the INVERSE as a function of the pre-state: `Undo: 'Op -> UndoClass<'Node,
'Op>` with `Inverse of ('Node -> 'Op list)`, `Compensate of ('Node -> 'Op list)`, `OneWay of
reason`. The reason is what `undo_run_restores` makes exact: the posture is read BEFORE the run,
from the declared form, where no pre-state exists — a document computed from a registration has
no state to compute a class against — and the theorem ties that static reading to the dynamic
trail only because the class the posture read of an op is the class the undo meets for the same
op whatever state it was applied to. A member whose case could depend on the state would need a
coherence assumption ("the class answered at the entry state is the class answered at every
intermediate state") that the type now carries for free. The inverse is a LIST because the UI
witness's inverse is a diff (`TreeOpDiff.diff` from the post-state back to the pre-state), and a
diff is a list; a domain whose inverse is one op answers a singleton. Only an op the witness
views as an EDIT is asked.

**2. Reversible versus compensable is the line that matters, and the reason is a LAW.** Both can
be undone. An exact inverse obeys K9 — applied to the state the op produced, the ops the member
answers for the state the op was applied to restore that state, byte for byte through
`Canonical` — and a compensation obeys nothing: a retraction after a publish undoes it in effect
and not in history. Two consequences, both mechanised. A reversible plan can be CHECKED before
anything performs: the undo run folds the inverses against the recorded entry state through
`Stream.Apply` alone and refuses a witness whose inverse breaks the law (`undo-inverse-drift`)
rather than performing it — the one place a broken law can be caught, and the verb's tests pin it
with a lying inverse. A compensable plan reaches whatever its compensations reach
(`undo_run_reaches_compensated`), and the posture says so by its name. So a deployer who reads
`reversible` is being told the undo is verifiable, and one who reads `compensable` that it is
declared. The verdict lattice is `one-way > unknown > compensable > reversible`: a proof that a
step cannot be undone outranks a place the walk could not decide, and either outranks a declared
compensation — the replay classification's rule that only a proof is a finding, applied to a
four-valued answer.

**3. The vocabulary.** Six defects, each a derived fact and never a string a document supplied:
`compensated-op`, `one-way-op` (the member's answer for an edit, both arms of a branch counted
because which arm runs is not decided from the form, a repeat's body once); `opaque-host-call` and
`outbound-notification` (the step reached the world and no inverse vocabulary exists for it);
`emitted-patch` (the host applies it after return, and the host's to undo); and
`compute-outside-fragment` (D21's reversible fragment, read through `BoundedActions.reversible` —
this is where 1976's check composes in, on the dispatch axis, whose inverse is a PROGRAM built
from the form and so needs the exit assertion). A read contributes nothing: it lands a table in
the binding store's query slot, which is the host's cache and which a restored tree's
re-resolution still reads; the undo leaves it, and the ladder says so.

**4. The trail, and where the pre-state lives.** The plan phase records, in plan order, every
EDIT with the state it was applied TO, every compute stage with the trace the fold recorded,
every host call and notification as a step that REACHED, every patch as a step EMITTED. The
pre-state is recorded because an inverse of a write needs the old bytes and the plan phase is the
one place that holds them — the same reason D20 handed the performer the planned state.
`Handler.runPlanned` answers the plan beside the outcome; `runWith` is it without the plan, so no
caller changes. The compute stage now runs the fold TRACED (`runTraced` with the inert arm):
`traced_agrees` says the outcome is the one `runInert` answered, and the UI tier's behaviour is
byte-identical through the parity suite and the Fable leg. In the model the trail is a second walk
over the same views (`trail_views`) and `trail_agrees` says the two walks reach one tree and refuse
alike; production threads it through its one fold, and the undo differential is where the two are
seen to coincide. `run` is unchanged; `run_is_run_planned` ties it to `run_planned`.

**5. The undo run is a handler run, and that is a theorem.** The inverse plan — every edit's
inverse or compensation computed from its recorded pre-state, in reverse plan order — runs as ONE
`ApplyOps` effect through `Handler.runWith` against the store the run left, under the same
registry, performance and resolver: the gate decides `ApplyOps`, the argument policy bounds every
inverse's reach (a deployer's allow-list binds the undo as it bound the run), the registered
performers perform them, and two-phase staging holds. `undo_residual_is_prefix` is
`residual_is_prefix` read for the undo, which is why a failed undo step reports how far it got in
the `PerformFailed` vocabulary rather than in a vocabulary of its own. Then the compute stages'
binding writes are reversed by D21's inverse (`reverse` over each stage's trace, `runReversed`) in
reverse stage order — the tree and the bindings are independent stores, so the two halves compose
exactly. Before any of it: a plan that rolled back is refused (nothing to undo); the FIRST step the
undo cannot perform is refused and NAMED by its ordinal, before anything is undone
(`one_way_position_exact`, `refused_before_anything`) — a one-way op with the domain's reason, a
reached step with its capability, an emitted patch, a compute stage outside the fragment or whose
trace is not restorable (D21's `Trace.restorable`, a run-time fact the static posture cannot see,
so a `reversible` posture is a claim about the form and `undo_run_restores` adds the restorer as
its one run-time hypothesis).

**6. What the phase premised, checked.** The phase as filed composed "1976's reversible-fragment check for
the flow structure" into the classifier "so an unasserted branch is `unknown`, not `reversible`".
Checked against the model: the op-channel branch needs no exit assertion to be undone, because the
undo of this phase is by TRAIL — the plan records the edits the taken arm applied, and the inverse
plan inverts those; D21's exit assertion is what lets an inverse PROGRAM pick the arm to undo
without a trace, which the dispatch axis's reversal does and the op undo does not. So an
op-channel branch without an exit assertion is classed by its arms (both, as reach and replay
count both), `undo_run_restores` carries no hypothesis about exits, and the premise holds exactly
where it was true all along: the DISPATCH axis, where a compute stage outside the fragment —
which includes an unasserted branch — reads `compute-outside-fragment`.

**7. The document.** Version 6 adds `undo`, per handler, beside `replay`, on the argument version
3 made and for the same member shape: present on every server tier, empty where no handler
contributed, so "this producer predates the posture" and "nothing here needs undoing" never share
a spelling. Every envelope signed under version 5 reports `Unreadable` drift naming the version;
the K7 pin was re-signed over the same tree with a fresh key, its tree-hash half still the pre-cut
value. `Harvest.ofProgram` and `ofRegistration` carry both postures from one reachability.

**8. D14 applied a fifth time, and one finding for the next restatement.** A quantified hypothesis
with four bound variables and an antecedent on a projection (`forall op pre post f. apply op pre
== ROk post /\ cls op == Inverse f ==> …`) is not reliably instantiated by the solver inside a
recursive lemma; the law was restated as a LEMMA-TYPED parameter (`inverse_law w cls` is an arrow
to `Lemma`, and `undo_run_restores` takes `law: inverse_law w cls`) and called explicitly at the
one step that needs it. This is the shape to reach for first when a theorem is conditional on a
witness obligation: the hypothesis is named in the signature, no pattern is guessed, and the
ladder's "conditional on" is literally the argument list. The module checks in ten seconds under
the pinned flags, alone and in context, with `--quake 3`.

**Version.** Rides the `0.7.0` draft: the slot is untagged, publicly unpinned and already breaking
of this class — `StateWitness` gains a member, every full-literal witness construction gains a
line; `Demanded.Version` is 6. `STABILITY.md` has the consumer's account and
`docs/migrations/phase-1977.md` the diff.

**What this forecloses, and what it leaves.** The undo of a run that ROLLED BACK after a
perform-phase failure — undoing the prefix `residual_is_prefix` reports — is not offered; the
record exists (the plan carries `Committed`) and a later phase that wants it has the trail. A
landed read is not undone, by decision (item 3), until the store has a member that forgets a
slot, which no witness has asked for. The class of an op is a function of the op; a domain whose
undoability genuinely depends on the state answers `Compensate` with a compensation that reads
the state, and reads `compensable` — the honest word for an inverse that cannot be checked
against a law.

## D23 — The durable interpreter journals EVERY arm that reaches outside: a performed op stage is a journaled step at its ordinal, its subject is the op's content address, the op performer is declared like a host performer, and the journal extension is not a specification act (2026-10-02)

D12 journaled "the ONE arm that reaches outside" and gave D8's reason: a query reads, an op edits an
in-memory tree the caller may discard, a patch and a notification accumulate as values the caller
performs after the handler returns — none performed by the handler, so none performable twice. D19
then gave ops a registered performer (`OpPerformance.Performed`): every op of a committed plan is
staged as a call of its own and performed in the perform phase beside the host calls. From that
commit D12's premise was false for any domain that registers one — a performed op reaches outside
exactly as a host call does — and D19 chose to say so ("the durable interpreter runs in memory and
journals no performed op; a verb under the durable tier is stated as not covered") rather than extend
the journal. This decision extends it, and restates D12's premise: **what is journaled is every arm
that reaches outside** — the host call, and the op stage where a performer is registered. Under
`InMemory` nothing is performed and nothing new is journaled, byte for byte.

**1. An op stage is a journaled step, through the same wrapper and the same cursor.** `Durable.runWith`
takes the placement's `OpPerformance` beside its registry and wraps the op performer exactly as it
wraps a host function: ONE `wrapAt`, one ordinal cursor, so an op stage and a host call take their
ordinals from the one sequence in perform order — the plan phase is deterministic, so a replay reaches
ordinal *n* holding the same staged call the recorded run held there, whichever arm it is. The
attempt / invoke / record order is unchanged: `Attempted` before the performer, `Completed` or
`Refused` after, the third readable state ("attempted, no result") declared rather than closed, its
replay refused by default, `acceptingIndeterminateReplay` the named opt-in with its override record,
and a performer declared `Idempotent` closing the window by its own shape. The alternative — a
second journal keyed by op, or a second cursor for op stages — was refused for D12's own reason read
once more: two ledgers over one perform phase is a second account of what ran, kept in step by hand.
`Durable.run` keeps its signature as `runWith` at `InMemory`, on the terms `Handler.run` kept its
(D19); `Durable.arm` passes the host's performance, so a session whose ops reach the world is covered
without a second arm. `DurableControls` gains the same `runWith` beside its `run`.

**2. The entry is filed under `ApplyOps` with the op's CONTENT ADDRESS as its subject.** A host call's
capability names its function; two ops share a capability, so the capability alone cannot say which
op a step was about, and the divergence check — the one thing that stands between a replay and
serving one call's answer to another — would pass on any op at a recorded op ordinal. `JournalEntry`
therefore gains `Subject: string option`: `None` for a host call, and for an op stage
`sha256:` + the digest of the op's canonical form, read off the state axis's own codec
(`StateWitness.Stream.Encode`, K6 — the shard named `StateWitness.Canonical`, which canonicalises the
STATE, and that is exactly the member this must not read: the state handed beside an op is the planned
state, recomputed on replay, not the thing performed). A hash rather than the form itself, for the
reason `Journal.describe` renders no value: an op can carry a payload, and the journal's identity for a
step must stay log-safe and fixed-size — the same shape, for the same reason, as the tree hash a
signed envelope binds. The divergence check compares the subject beside the capability; a replay that
reaches a recorded ordinal holding another op refuses with `durable-replay-divergence`, as it always
did for another arm.

**3. The op performer is DECLARED like a host performer, and the facet derivation reads the
registration as a fact.** `PerformerFacets` gains `OpPerformer: IdempotencyFacet option`
(`declareOpPerformer`; `DurableServices.declaringOpPerformer`): a domain whose op is a
content-addressed write declares it `Idempotent` and reaches `ExactlyOnceEffective`, one whose op is a
push cannot claim it without saying so, and undeclared reads `NonIdempotent` on the terms an
undeclared host function always has. The derivation (`Facets.ofEffect` and everything above it, and
`Durable.guarantees` / `declaration` / `checkDeclaration`) now takes the placement's `OpPerformance`:
under a registered performer `ApplyOps` is derived on a host call's terms — strict may LOSE, accepting
may DUPLICATE, exactly-once only where declared — and in memory it is the recomputed arm it always
was. The registration is PASSED rather than declared on the facets, deliberately: a host that
registers a performer and declares nothing about it must read as an undeclared performer (reported
under `ApplyOps` by `checkDeclaration`), never as the recomputed arm it no longer is. A declaration
that also asserted the registration would be a second source of the same fact, free to disagree.

**4. The discipline is PROVED before the code moved (D14), in the staging model.** `Staging.fst`
gains the durable discipline beside the handler it wraps, over the same staged list: the journal as a
SNAPSHOT read once at entry (`journal`), the wrapper's decision per ordinal (`decide`), the perform
phase through it (`replay`) and `durable_run`. Eight theorems: a completed prefix is served and only
the rest is performed (`replay_serves_completed`, `replay_unrun_is_perform`,
`resume_performs_only_the_rest`, and `durable_resume` at the handler); an undecided step under an
undeclared performer with the opt-in off is refused before it and everything after it
(`indeterminate_refused_by_default`); it is re-invoked in exactly the two named cases and nowhere
else (`indeterminate_reinvoked_only_by_name`); a recorded ordinal holding another identity is refused
(`divergence_refused` — the subject's reason, as a theorem); and with nothing recorded the durable
run's outcome IS the direct run's (`empty_journal_is_direct`). `plan_pure` and the prefix theorems
re-prove unchanged, because the wrapper changes `r_perf`'s behaviour and nothing the plan phase reads.
The journal's WRITES and its storage stay the host's port and out of the theorem. The differential
host (`DurableInterpreterTests`, "the proved durable replay as oracle") runs the extraction beside
`Durable.runWith` over every journal shape the crash fixtures leave — nothing recorded, interrupted
between stages, a crash inside a stage under each policy, a served refusal, a diverging op — and it
went red first, on a model staged list a comprehension had shortened to two calls, before it went
green.

**5. The specification act is DECIDED: the journal extension needs no specification change.** PROGRAM
WIRE §6.2 ("only `HostCall` is staged") and §6.4 (`PerformFailed` over host calls) describe the
in-memory placement, which every conformant host had and every UI host still has; D19 left carrying
the performer case into the normative text as a specification act, and this phase does not take it,
for three reasons that are each sufficient. (a) **The effect journal is not a wire artefact.** It is
a host-internal port (`EffectJournal`: append, read, a restart claim) with no document, no schema, no
fixture and no refusal class in the specification — the host-call steps it has journaled since D12
were never specified either, and an op stage's entry is the same record with one more member. (b)
**No specified member moves.** The outcome document is unchanged: `performed` carried `ApplyOps` once
per performed op from D19 on, under the hook that "changes no wire at all", and this phase changes
what is RECORDED about those stages, not what is reported. (c) **§7.1 is checked and holds.** "A
conformant host MUST NOT journal a handler invocation as a replayable record" — and nothing here
journals an invocation. A journaled op stage is a step record keyed to an ordinal within an
invocation, served so the step is not performed twice; it is not an artefact from which a host
re-issues the invocation, and the invocation-level marker this interpreter already writes is an
audit fact a replay never short-circuits on. §7.1's own words are "ops, not invocations", and an op
stage's entry is, to the letter, an op. The one thing this decision records for the specification's
own follow-on is unchanged from D19: carrying the performer case into §6.2 remains a specification
act across five artefacts, and the sentence a registered performer reads past is still §6.2's, not
§7's.

**Version.** Rides the `0.7.0` draft: the slot is untagged, publicly unpinned and already breaking of
this class. `JournalEntry` and `PerformerFacets` each gain a member (FS0764 on a full-literal
construction — a host's persistent journal that constructs entries, and nothing else in this
repository), and `Facets.ofEffect` / `ofHandler` / `ofHandlers` / `declare` / `checkDeclaration` and
`Durable.guarantees` / `declaration` / `checkDeclaration` take the performance. No wire member, no
fixture byte, and the UI tier byte for byte: `Durable.run` is unchanged in signature and behaviour.
`STABILITY.md` has the consumer's account.

**What this forecloses, and what it leaves.** A performed op's receipt — what the performer can say it
did, and a return contract over it — is Phase 1981's, and this decision assumes nothing about it: the
journaled value for an op stage is the inert object the staged call already answers, and a receipt
rides there when it exists. The operator controls (`Controls.revoke`) still name a host performer by
function and cannot withdraw the op performer, which has no name; that is a finding, not a decision.
The journal's writes are two acts to two systems and the indeterminate window stays declared, not
closed, exactly as D12 left it — what this decision adds is that the window is now the same window
for an op stage, read, refused and overridden by the same code.

## D24 — An op performer's claim is CHECKED: it answers a receipt, a contract over (planned state, op, receipt) is declared at registration in the `registerChecked` shape, the reach obligation runs both ways, and no specification act is taken (2026-10-02)

Phase 1759 gave host functions a checked return — `ReturnContract`, composed with a performer by
`ServerEffectRegistry.registerChecked`, proved by `return_contract` — and D19 (Phase 1967), landing
the same day, gave ops a registered performer and said it is "trusted on the terms a host function
is". It was trusted on weaker ones: `OpPerformance.Performed` answered `Result<unit, string>`, so a
performer could say only that it succeeded, and no contract could check what it did. D19 decision 2
stated the reach obligation in one direction only — a reach must never be a payload — and nothing
stated the other: that the reach covers what the performer touches, which is the direction a policy
enforced against reach (a lease, a deny-list, a worker grant — the plane's planned use,
roadmap-engine#703) actually depends on. This decision amends both. **The op performer is
constrained on the terms a host function is**, and **the reach obligation is stated both ways and is
checkable through the receipt where the domain can say how.**

**1. The performer answers a RECEIPT, and `Performed` is widened rather than doubled.**
`OpPerformance.Performed` carries `'Node -> 'Op -> Result<Fuaran.Core.JVal, string>`: the value a host
call's contract already reads, so one contract vocabulary serves both arms. The receipt is what the
performer says it did — the paths it wrote, the sha it committed, the blocks it rendered — in the
domain's own shape; it lands in no slot (`Into = None`, as an op always has), reaches no wire, and is
recorded by the durable journal as the step's completed value (D23: "a receipt rides there when it
exists"). A second `OpPerformance` case was refused for the reason D23 refused a second journal: the
contract is a WRAPPER over the performer, and composing it at registration — `performedChecked
contract perform = Performed (OpContract.check contract perform)` — is exactly the `registerChecked`
shape, so every match site (the handler's plan fold, the durable wrapper, the facet derivation, the
oracle bridge) keeps ONE arm and ONE perform loop, where a second case would have put the same
wrapper into each of them by hand. A performer with nothing to say registers through
`performedWithoutReceipt`, whose receipt is the inert empty object the staged call always answered.

**2. The contract is over (planned state, op, receipt), and is a return contract AT the op.**
`OpContract<'Node, 'Op>` is `{ Name; Holds: 'Node -> 'Op -> JVal -> bool }`; `OpContract.describe` is
`return-contract:<name>`, the ONE refusal vocabulary a return contract has — the op axis coins no
second prefix, so a reader of a `PerformFailed` reason learns which contract and not which arm, which
is what a denial has always told them. `OpContract.check` is `ReturnContract.check` with the state and
the op in hand, clause for clause: a raw refusal through unchanged, a receipt that holds through, a
receipt that does not become the contract's name and never the receipt. The three arguments are the
whole point: a contract that could not see the op could not know its reach, and one that could not
see the state could not check a receipt against what the plan produced.

**3. Proved first (D14), as a reading of `return_contract` made visible, plus the identity for the
uncontracted case.** `EffectGate.fst` gains `op_contract`, `check_op` (production's wrapper),
`op_at` (the return contract an op contract IS at one state and op), and the bridge
`op_contract_keyed`: in production the contract is composed INTO the closure the plan phase stages
as the op's token, and in the model that is the hypothesis that the token staged for (state, op)
carries the contract at (state, op). Four theorems: `check_op_is_check_return` (one check, two
keyings); `op_token_checked` (under the keying, the behaviour the perform phase sees for an op token
IS `check_op` over the raw behaviour); **`op_return_contract`** — the three clauses of
`return_contract` and two more in the op's own vocabulary: every landed receipt of an op stage
honours the contract at the state and the op it was staged from (`op_receipts_honour`), and when the
first rejected result is an op stage's, `PerformFailed` at that position names THIS contract, the
handler is rolled back and `Performed` is exactly the stages before it; and
**`uncontracted_is_direct`** — the handler's outcome under the wrapper that declares nothing is its
outcome under the raw registry, as an equation, which is "a performer with no contract behaves
exactly as today" as a theorem rather than a reading, and is why the staging and gate theorems
re-prove unchanged for it. The model identifies an op stage by its TOKEN, not by its capability
string, because the prover reasons about neither string content nor `strcat` inequalities
(`strcat "host:" fn =!= "ApplyOps"` is unprovable there), and a token is what the staged call
carries anyway. A go-red mutant — `check_op` accepting a rejected receipt — fails `op_token_checked`
as it should. The differential host runs the extracted `check_op` beside `OpContract.check` over 48
(state, op, behaviour, verdict) cases, with the (state, op) handed to the contract recorded and
compared on both sides.

**4. The reach obligation, both ways.** D19's direction stands: a reach is a NAME and never a payload.
The new direction is on the witness beside it: **an op's reach covers what its performer touches when
handed the op** (`docs/generic-tier.md` §3.5). The receipt is the domain's route to checking it — the
in-repo verb witness answers the paths and targets it touched and `Receipt.withinReach` checks each
against the reach's `path` and `target` arguments; its `Escaping` adversary, which writes a path the
op's reach does not name and says so, is refused with `return-contract:within-reach`, the op absent
from `Performed`, the escaped path in no diagnostic. What is checked is the performer's ACCOUNT, and
`docs/performer-boundary.md` says so: a performer that overreached and reported only what was within
reach passes, exactly as a host function whose result satisfies its contract passes whatever else it
did — which is why the obligation is stated on the witness and the theorem claims the check, not the
coverage. The document witness is unchanged: `Fuaran.Doc`'s sink has nothing to say beyond success,
and registers through `performedWithoutReceipt`.

**5. The durable tier records a refusal, never a false completion.** `Durable.runWith`'s wrapper
hands the op stage's performer through unchanged — the contract is inside the performer as registered
— so a receipt the contract rejects reaches the wrapper as `Error "return-contract:<name>"` and is
journaled as `Refused` at its ordinal: a decided step, served on replay as the same refusal, never a
`Completed` value and never the indeterminate window. `DurableInterpreterTests` pins the journal's
rendering for both the receipt (the completed value IS the receipt) and the refusal.

**6. The specification act is DECIDED: none.** Return contracts are host-declared and never specified
(Phase 1759 added none). A receipt reaches no wire member — it lands in no binding, appears in no
outcome document, and the journal that records it is the host-internal port D23 item 5(a) already
found outside the specification. Its contract's refusal rides PROGRAM WIRE §6.4's `PerformFailed`
`reason`, which already carries "the performer's own text": `return-contract:<name>` has been that
text for a host call since Phase 1759, and an op stage's `PerformFailed` under `ApplyOps` has been D19's
since Phase 1967. No specified member moves, no fixture byte moves, and the one sentence a registered
performer reads past is still §6.2's, on D19's and D23's terms.

**Version.** Rides the `0.7.0` draft: the slot is untagged, publicly unpinned and already breaking of
this class. `OpPerformance.Performed`'s payload type changes (every registered performer and every
`performedBy` caller — in this repository the two witnesses and three test suites); `OpContract`,
`OpPerformance.performedChecked` and `performedWithoutReceipt` are new. No wire member, no fixture
byte, and the UI tier byte for byte: `InMemory` is untouched and `Handler.run` unchanged.
`STABILITY.md` has the consumer's account.

**What this forecloses, and what it leaves.** A receipt that lands in a binding slot — the op arm
gaining an `Into` — is NOT taken: it would make `ApplyOps` a dispatch-axis consumer, and a domain that
wants a receipt in its state can write it as an op. The operator controls' inability to withdraw the
op performer (D23's finding) stands. A contract keyed by capability STRING in the model is foreclosed
by the prover (item 3); a model that distinguishes op tokens from host tokens by a token-side
predicate is the shape a stronger statement would take, and nothing yet needs it.

## D25 — The outcome names the arms it took, a state-only composition has a handler document, and a ceiling clause bounds a number; no specification act and no theorem statement moves (2026-10-02)

The second witness's third run (roadmap-engine#703) placed two store-mutating verbs on this domain
against the Phase 1976 build. It found three gaps that are generic to any domain rather than plane
vocabulary, and so this repository's under D18. The run worked round each one. None of them should
need working round.

**1. B3: the handler outcome carries its flow decisions, host-side.** `HandlerOutcome` gains
`Flow: FlowDecision list`, where `FlowDecision` is `Chose of tookTrue` or `Repeated of count`.
- **Order:** plan order and pre-order, so a branch's or a repeat's own decision comes before the
  decisions inside it.
- **Both axes feed it.** The op axis (`OpView.Choose` / `OpView.Repeat`) is threaded through
  `planOps` beside the undo trail. A compute stage's fold is read off the trace it already records
  (`FlowDecision.ofTrace`). There is one fold and no second walk.
- **The event tally carries it too.** `HandlerTally.Flow` holds the flows of the handlers an event
  invoked, in invocation order, so the session placement can report them.
- **On a rolled-back outcome** it names the decisions taken before the effect that halted, on the
  terms `Diagnostics` survives a rollback. An op effect whose plan was refused contributes nothing of
  its own, because its plan was refused whole.
- **Why the run needed it.** It mapped a verb's arm onto an exit code by reading the conditions a
  second time. That is sound only while no arm writes what a condition reads, and the outcome is now
  the reading.

Two things were decided rather than defaulted:
- **Host-side, not on the wire.** `HandlerReport` does not carry `Flow`, so the outcome document the
  specification describes is byte-identical, and the UI tier's outcomes are byte-identical because no
  UI handler uses either shape. Putting the arm trace on the wire would be a specification act
  across all five artefacts. The one consumer so far reads the outcome in-process, so nothing yet
  pays for that act.
- **No theorem statement moves.** The flow is an observation threaded beside the plan; it changes
  neither the store, the staged list, `Performed` nor a halt. `Staging.fst` and `BoundedFold.fst`
  say nothing about it, and the oracle bridge fills it empty and does not compare it. Its claim (it
  names the arms the plan took) is tested, not proved: the verb domain covers both arms, a nested
  repeat and a halt, and the toy domain covers a compute stage. Disabling the op-axis push turns
  three of those tests red. A model of it would be `trail_views`' shape, a second walk proved to
  agree with the fold, and it waits for a consumer that needs the claim proved.

**2. H1: the handler codecs run under every composition.** `HandlerWire` was typed at a full dispatch
axis, so a domain that fills only the state axis could not encode a handler as a Program document,
and so could not bind a signed envelope to one (D15).
- `HandlerWire`, `Replay` and `Harvest.ofRegistration` now read the dispatch position through
  `IDispatchPosition`, as the handler loop and the demand walk already did.
- `ProgramWire` gains `decodeActionIn` and `replayDefectsOfActionIn` over the action witness alone,
  and the full-axis functions delegate to them.
- **A compute stage decoded at such a composition** is refused on `malformed-referenced-value`: the
  action is a referenced value and there is no vocabulary to decode it. That class is the
  specification's own, so no refusal class is added.
- **A landing slot decodes**, because a composition without a binding channel has no reserved
  namespace to check it against. The handler refuses it while planning (`no-binding-channel`), which
  is still before anything external runs.
- `HandlerWire.contentAddress` is the specification's `sha256:` form over the canonical handler
  document.
- `SignedEnvelope.signAddressed` / `verifyAddressed` sign and verify a content address paired with a
  demanded projection. `sign` / `verify` are now those with the tree's hash and its walk, byte for
  byte as before.
- Verification stays recomputation: the caller recomputes the address and the projection from the
  registration it holds, and the carried bytes have no say. A changed handler whose demand did not
  move is a bad signature, and a relaxed policy is drift.

The in-repo verb domain gained an op decoder, `encodeOp`'s inverse, so its handlers round-trip.

**3. R2: `ServerConstraintClause.AtMost(argument, limit)`.** Every value under the argument must read
as an integer no greater than the limit.
- **What counts as an integer.** Only the canonical decimal spelling: an optional minus sign, no
  sign on zero, no leading zero, no whitespace, at most eighteen digits. Anything else is refused,
  because a second spelling of one number would pass the bound under one reading and reach the
  performer under another.
- **Refusal token:** `OffList argument`, the allow-list's, which names the argument and never the
  value. The clause is vacuously true where the effect names nothing under the argument, on the
  allow-list's reading.
- **Integer members are now arguments.** A host call's top-level integer members join its string
  members, as decimal text. Without that, an `AtMost` on a numeric argument would pass vacuously
  over exactly the value it bounds. It also tightens an existing allow-list on such a name, which
  used to pass over the number unseen and now sees it.
  This reverses a limit this repository had stated, "a top-level member whose value is a string",
  in the policy module's header and in an `EffectConstraintTests` case that pinned it. Both are
  amended. The limit was a statement of scope rather than an argument for it, and nesting stays
  out of scope.
- **Rides document version 6** on the argument `denyList` rode version 5: no producer of a version-6
  document before it could declare one, a reader built before it refuses a document that carries
  one at the clause, and the version belongs to an unreleased draft.
- **No proof moves.** `EffectGate.fst` takes the argument policy as an arbitrary function, and its
  theorems hold for every policy (the `plan_effect` congruence over `r_policy`).
- The demanded corpus (`conformance/demanded-effect-projection.json`, Phase 1978) carries the clause
  in its policy vector and is regenerated.

**What this does not do.** It records no flow on the wire, as item 1 says. It models no flow, also
item 1. It adds no `AtMost` for non-integer quantities: a byte size is `Ceiling`'s, and no other
quantity has a consumer.

## D26 — The op performer is revocable: a fixed reserved key names it, and a revoke of that key refuses every op stage at its ordinal, before the stage is attempted, in both interpreters (2026-10-02)

D23 closed with a finding, not a decision: "the operator controls (`Controls.revoke`) still name a
host performer by function and cannot withdraw the op performer, which has no name". A revoke is a
registry edit (`Controls.apply` removes the named function), the op performer is not a registry
member — it is the placement's `OpPerformance`, handed to the interpreter beside the registry — and
so an operator who revoked every host performer still had ops performed. `Durable.fs`'s controls
header claimed the opposite: that wrapping the registry left "no arm of the vocabulary a control can
be forgotten for". It was true of every arm but this one. This decision closes the finding.

**1. The identity is a FIXED, RESERVED key — `OpPerformance.RegistrationKey`, which is `"ApplyOps"` —
not a name the host declares at registration.** The phase left both open. A declared name was
weighed and refused on three grounds. (a) **It has nothing to distinguish.** A placement registers at
most one op performer, which is why `PerformerFacets.OpPerformer` is one slot rather than a key
(D23 §3), and a control stream is scoped to one session, so the key need only be told apart from the
host's own function names in that session — never from a second op performer. (b) **It would be a
second identity for an arm that already has one.** The op stage is gated, throttled
(`ThrottleWindow.Capability = "ApplyOps"` already throttles it), journaled (`Durable.OpStageCapability`)
and reported (`checkDeclaration`'s undeclared-performer line) under `ApplyOps`. A declared name
would be the one place the arm is called something else, free to drift from the rest. (c) **It costs
a breaking change for no capability.** Every registration function and every match on
`OpPerformance.Performed` would change — across the server package, the verb and document
witnesses, and the extraction tests — to carry a string nothing else reads. The rule in `Journal.fs`
("what a control record may carry") holds: the key is vocabulary, never a payload. **The cost, stated
rather than hidden:** a host function the host has itself registered under the name `ApplyOps` shares
the key, so one revoke withdraws both arms. That is the over-broad direction, which is the safe one for
a kill switch, and the alternative — a revoke of the key that skips the like-named host function —
would leave that function unrevocable. A future host with two op performers in one session (none
exists, and the type admits one) is the trigger to revisit, and the revisit is a declared name.

**2. A revoked op performer refuses at the op STAGE — at the stage's ordinal, in the perform phase,
before the performer is invoked — and not while planning.** A revoked host performer is refused while
planning: removed from the registry, its call reads as `Unregistered` and the handler performs
nothing. The op stage cannot be refused at that point, and should not be. The plan phase never looks
the op performer up (an edit is applied in memory and STAGED whatever is registered), so the stage is
the first point the performer would be reached; and refusing there is what lets a resumed durable run
SERVE the stages its journal recorded and refuse the first it did not, which a plan-time refusal
would deny — it would report a run whose recorded prefix reached the world as one that performed
nothing. The consequence is D8's residual, unchanged in shape: a host call staged BEFORE the op stage
has performed and is named in `Performed`, and the op stage fails as
`PerformFailed("ApplyOps", "control-performer-revoked")`. The control record is
`ControlRefusal.Revoked("ApplyOps", actor, reason)`, the shape a revoked host call's has; and
`describeRefusal` now renders a revocation as "refused — its performer was withdrawn by …", because
its old text ("reads as unregistered") was false of an op stage.

**3. Both interpreters, through one decision.** `Controls.opPerformerRevocation` reads the key out of
the folded `Revoked` map, and `Controls.opStageRefusal` turns it into the stage's refusal, recording
the `ControlRefusal` as it answers. The DIRECT interpreter takes it through
`Controls.performance`, beside `Controls.apply` for the registry: a withdrawn op performer is replaced
by one that answers the refusal, so the registered performer is never invoked. The DURABLE interpreter
takes it through a hook rather than through a replaced performer, and the difference is deliberate:
the hook is asked only for a stage the journal has NOT served, and before the `Attempted` record, so a
refused stage leaves the effect journal untouched. A replaced performer would have been invoked inside
the journal's attempt / invoke / record order, writing `Attempted` and `Refused` for a step that
reached nothing — and a later run would then SERVE that refusal from the effect journal instead of
re-deciding it from the control stream, which is where the act lives. `Durable.runGuarded` /
`armGuarded` are the uncontrolled interpreter with that one hook exposed (internal);
`Durable.runWith` / `run` / `arm` pass the refusal that never refuses and are unchanged in signature
and behaviour, and `DurableControls.runWith` / `arm` / `stepVia` pass the controls' refusal.

**4. Monotonicity is unchanged.** `Controls.step` gains no arm and no arm removes a key from
`Revoked`, so a withdrawn op performer stays withdrawn across every prefix of the stream; a `Resume`
lifts the suspend and nothing else; and because the refusal is re-decided from the control stream on
every run rather than journaled, a resumed run is refused exactly as the first was.

**5. In memory there is nothing to withdraw.** Under `OpPerformance.InMemory` the apply is the effect,
performed while planning, and nothing reaches outside; a revoke of the key changes nothing there, and
`Controls.performance` returns the value it was handed.

**6. The facets name the op performer by its key.** `checkDeclaration`'s undeclared-performer line
for the op performer is filed under `OpPerformance.RegistrationKey` and its detail names it, so a
finding names every performer by the key a revoke would use — `host:<key>` for a host function, the
key itself for the op performer. No member is added to the facet vocabulary: the key IS the
capability the line was already filed under, which is item 1's point (b) read from the other side.

**Version.** Rides the `0.7.0` draft as an additive change: new members
(`OpPerformance.RegistrationKey`, `Controls.opPerformerRevocation` / `opStageRefusal` / `performance`)
and a refusal path a session reaches only by recording a revoke of the key. No wire member, no fixture
byte, no control-stream byte and no proof statement moves; a session that revokes nothing runs byte
for byte as before, in memory and durably, journal entry for entry. `STABILITY.md` has the consumer's
account, including the one behaviour a host already recording revokes can observe.

**What this forecloses, and what it leaves.** It forecloses an op performer with a host-chosen name;
item 1 names the trigger to reopen it. It leaves the demanded-effect COVERAGE check unaware of a
withdrawn op performer: `Controls.coverage` reads the registry, which never held the op performer, so
a host whose op performer is revoked is still reported as covering `ApplyOps`. Coverage asks whether
the host HAS a capability, and in-memory ops are covered whatever is revoked, so the honest answer
needs the placement's performance beside the registry; that is a change to the coverage question,
not to the controls, and is left for the phase that asks it. And the staging model
(`proofs/Staging.fst`) is untouched: to the model a refused stage is a performer that answers a
refusal at its ordinal, which `r_perf` already quantifies over, and the journal's WRITES — the one
thing the hook does differently from such a performer — are outside the theorem (D23 §4), so nothing a
theorem states moves.

## D27 — The proof hosts stage an op token that CARRIES the planned state and the op, so `op_return_contract` runs differentially at the handler and `op_contract_keyed` is tested rather than assumed; production's types are unchanged (2026-10-02)

D24 proved `op_return_contract` for a whole handler run, conditional on the bridge `op_contract_keyed`:
the token the plan phase stages for (state, op) carries the op contract AT (state, op). Its
differential evidence stopped one level below the statement. The staging and effect-gate hosts in
`tests/Fuaran.Program.Server.Tests/ProofOracleTests.fs` instantiate the model's performer token as
production's own closure — `GateToken = string * Performer` for a host call, and for an op the
closure over the state as of the op — and the model's `checked_by` keys a contract on a token, which a
closure cannot be inspected for. So what ran was the wrapper (`OpContract.check` beside the extracted
`check_op`, 48 cases) and the verb witness end to end; the bridge was true by construction in
production and never exercised against the model. This decision records why the host's token now
differs from production's, and what that buys.

**1. The host's op token is `OpToken (state, op)` — test typing, not a production change.** The
op-contract host's `r_op_perform` answers, for each (state, op) the plan phase stages, a token that
carries that planned state and that op, beside a `HostToken (fn, performer)` for host calls; the
model's per-token contract keys `OpToken (s, o)` to `op_at contract s o`, which is the keying
`op_contract_keyed` states, and the raw behaviour applies the host's own op performer to the
token's (s, o). Production keeps its closure: `OpPerformance`, `OpContract`, `Handler.stagedOp` and
`Durable.runWith` are untouched, and nothing in `src/` moves. The two tokens differ ON PURPOSE. A
closure is what production needs — the contract composed into the performer before the plan phase
splits it, so every match site keeps one arm — and it is exactly what makes the bridge true by
construction and therefore untestable. Data is what the model needs: a contract can be keyed on it,
and a keying that is wrong can be written down and shown to lose. The host is where the two meet, so
the host is where the token is chosen, and it is chosen to carry what the hypothesis quantifies over.
Carrying the canonical forms instead (a state hash and the op's content address) was considered and
not taken: the model's contract is handed whatever the token holds, and the state itself is what
production's contract is handed, so carrying the value keeps the two contracts one predicate.

**2. What the differential compares, and how it can lose.** Forty cases — five plans (an op alone,
two ops in one stage, an op then a checked host call, a host call then an op, host call / op / host
call / op) at four op-performer behaviours (honest, foreign, honest then foreign, a raw refusal),
contracted and uncontracted — run the extracted `Staging.run` with the extracted `checked_by` beside
`Handler.runWith` under `OpPerformance.performedChecked`, comparing the outcome and the one
performer log on which host calls and every op's handed (state, op) are recorded. The honest receipt
names the planned state's hash and the op's content address, so a contract keyed on the wrong state
or the wrong op rejects it. The go-red keying is the right contract on the right op at the ENTRY
state: run against the main differential while the host was authored it lost ten of the forty
cases, and the committed GO RED case pins the loss. The durable reading runs the same cases under
`Durable.runWith` beside the extracted `durable_run`, comparing the ordinals and the decided steps —
production's journal records against the model's invoked ordinals with the staged call's capability,
subject (read off the token's op) and wrapped answer — and resumes both over the journal production
left. A contract-rejected receipt journals as `Refused` at its ordinal on both sides and is served
on resume without invoking.

**3. The ladder moves; no theorem does.** `proofs/README.md` moves `op_contract_keyed` from the
assumed rung to the differentially tested one and cites the host for `op_return_contract` at the
handler level; `proofs.json` gains `op-contract-keyed-agrees-at-the-handler` (tested);
`proofs/check.ps1` names the host under the effect-gate module's `HostSubject` and raises that list's
`HostMinCases` from 7 to 12. The hypothesis stays a hypothesis in `EffectGate.fst` — the token is
opaque in the model, which is the point of the model — and no `.fst`, no extraction, no wire member
and no fixture byte changes. The parity host (`tests/Fuaran.Program.Parity.Tests/ProofOracleTests.fs`)
stages no op token and is unchanged.

**What this forecloses, and what it leaves.** It forecloses reading "true by construction" as
evidence: a bridge hypothesis whose production side is a closure is now tested by a host that stages
data, and a later bridge of that shape should be tested the same way. It leaves the per-token
distinction in the model as D24 left it — op tokens and host tokens are told apart by the host's
keying, not by a token-side predicate in the model — and the host-call contract keyed by function
NAME (`effect-gate-bridge-assumed`) on the assumed rung, since `registerChecked` keys it that way and
no closure stands between the two.

## D28 — Control coverage reads the op performer: a revoked op performer closes coverage's gate over `ApplyOps`, `Controls.coverage` takes the placement's performance, and the durable form reads it from the host record (2026-10-02)

D26 closed with a clause it left open: "it leaves the demanded-effect COVERAGE check unaware of a
withdrawn op performer". `Controls.coverage` and `DurableControls.coverage` read the registry, the op
performer was never a member of it, and so a placement whose operator had revoked
`OpPerformance.RegistrationKey` refused every op stage at run time while its coverage still reported
`ApplyOps` covered. The revoke worked and the report said it had not. This decision closes the clause
(Phase 1986).

**1. Coverage reads the placement's `OpPerformance` beside the registry.** Under
`OpPerformance.Performed`, with the key in `Revoked`, coverage's gate refuses `ApplyOps`; in every
other case coverage is exactly the registry's under the controls, as before. The rule is the one
`Controls.performance` already applies to the run, so the report and the effect are decided from the
same two facts — the folded control state and the placement's performance — and cannot disagree.

**2. The withdrawal is carried by the GATE, so the finding is
`CoverageFinding.ServerGateRefusesCapability "ApplyOps"`, not an absence.** This is the call that
needed deciding, and it is decided against the reading that looks more natural. A withdrawn HOST
performer reads as `UnregisteredServerFunction` because the coverage vocabulary has a registration
fact for host functions (`ServerCoverage.HostFunctions`) and a revoke removes the function from it.
It has NO registration fact for the op arm: `ApplyOps` is one of the arms that need no performer and
"only ever meet the gate" (`Demanded.fs`, `ServerCoverage`), so the gate is the one fact coverage
holds about it, and a withdrawal of the arm can only be said there. Saying it as an absence would
need a new `ServerCoverage` member (an op-performer slot) and a new `CoverageFinding` arm — a record
gaining a field breaks every full-literal construction and a closed DU gaining a case breaks every
exhaustive match, both in `Fuaran.Program.Bounded`, whose coverage model is placement-generic and
knows nothing of an op performer. That is a larger change to a different package for a distinction
no consumer branches on today. **The cost, stated rather than hidden:** `CoverageFinding`'s note says
only the gate arm "is resolved by changing policy", and a revoke is monotone — no `Resume` lifts it.
The suspend already reports through this arm (a suspended session has the capability and refuses
it), so the gate finding already means "refused by policy OR by an operator's control", and the
op-performer revoke joins the suspend there. **The trigger to revisit** is a consumer that must tell
a revoked op performer from a policy refusal from the coverage report alone; the revisit is an
op-performer slot on `ServerCoverage` with its own finding, filed against `Fuaran.Program.Bounded`.

**3. Coverage carries no actor and no reason, for either kind of performer — the phase's premise
that a withdrawn host capability is reported "naming the actor and reason" was checked and is false
of coverage.** `ServerCoverage` is a vocabulary and a gate; a withdrawn host function's finding names
the function and nothing else. The actor and reason live in the control state coverage was read from
— `Controls.opPerformerRevocation` for the op performer, `ControlState.Revoked` for both — and on the
`ControlRefusal.Revoked` record a run produces. The tests assert both from one fold: the finding names
the key the revoke used, and the state it was computed from names who and why. Putting the actor into
`ServerCoverage` would be item 2's breaking change to `Fuaran.Program.Bounded` again, and for a host
function as much as for the op performer — a widening of the coverage question beyond this phase,
not a gap in its answer.

**4. `Controls.coverage` gains the performance as a parameter; `DurableControls.coverage` keeps its
signature and reads `host.OpPerformance`.** For the durable form the host record already carries the
performance, so the input arrives through a value the caller passes today and nothing breaks. For
`Controls.coverage` a second function beside the old one was weighed and refused: the old one's
contract is "coverage with the controls in force", and left registry-only it would go on answering
that question wrongly for every performing placement — a function that lies by omission is debt, and
this side's doctrine forbids it. Its one in-repository caller is `DurableControls.coverage`; a direct
caller passes `OpPerformance.InMemory` to reproduce the old answer exactly. The change rides the
`0.7.0` draft, which is already of the breaking class; `STABILITY.md` has the consumer's account.

**5. In memory there is nothing to withdraw, and coverage says so by covering `ApplyOps`.** Under
`OpPerformance.InMemory` the apply is the effect, performed while planning, and nothing reaches
outside (D26 §5); a revoke of the key changes nothing at run time, so coverage reports the arm
covered. The phase's wording ("coverage says so rather than claiming `ApplyOps`") is read in that
sense — coverage does not claim a withdrawal that did not happen — because reporting `ApplyOps`
withdrawn in memory would contradict the run, which is the defect this decision exists to remove.

**6. Monotonicity is inherited, not re-implemented.** The rule reads `Revoked`, which no
`Controls.step` arm shrinks, so a withdrawn op performer is reported withdrawn on every prefix of the
stream after the revoke, a `Resume` included; the tests assert it prefix by prefix. With the op
performer not revoked, coverage is the registry-only formula, compared capability by capability under
an empty stream, a host revoke, a suspend and a throttle of `ApplyOps`, for both performances.

**What this forecloses, and what it leaves.** It forecloses reporting the op performer's withdrawal
as an absence without a change to `Fuaran.Program.Bounded`; item 2 names the trigger. It leaves the
uncontrolled host coverage (`ServerSession`, read off the registry with no controls) as it was — with
no controls there is nothing withdrawn to report. No wire member, fixture byte, demanded-document
byte, control-stream byte or proof statement moves.

## D29 — `Each` over a literal collection is vocabulary that lowers to the core by SUBSTITUTION, on both axes; the substitution is the witness's and the fold runs the sequence; a placeholder is bound lexically by one `Each` and refused at validation; the specification does not move (2026-10-02)

**2026-10-02. Phase 1990. Fulfils the per-element case D1 and D2 charter and D21 could not express;
the first of three phases on per-element iteration (1991 extends the collection to one the store
holds, 1992 is the grid-shaped witness that exercises both). Under D14: `proofs/BoundedFold.fst`,
`proofs/Staging.fst`, `proofs/Undo.fst` and `proofs/EffectGate.fst` were restated and re-proved
before a `.fs` moved. Amends K2 of `docs/generic-tier.md` §3.7 a third time.**

D21 gave the core `Repeat(bound, body)` and deliberately gave its body no index: an index the body
could overwrite would not be a function of the bound alone, which running the inverse the same number
of times rests on. The consequence is that a loop whose body depends on WHICH iteration it is in
cannot be written. The first domain to need one is a spreadsheet's automation, whose importer resolves
`For i = 2 To 100: Cells(i, 3)` into a fixed array of cell identities; today that array can only be
run unrolled — one copy of the body per element, total, and growing the wire with the data. This
entry adds `Each(collection, placeholder, body)`, where the collection is a LITERAL list in the tree
and the body reads the current element through a placeholder, and records what the model argued for.

**1. The shape, on both axes, mirrored.** `ActionView` gains `Each of collection: JVal list *
placeholder: string * body: 'Action`; `OpView<'Op>` gains `Each of collection: JVal list *
placeholder: string * body: 'Op list`. Mirrored rather than shared, as D21 item 1 mirrored `Choose`
and `Repeat`, because what the placeholder stands in differs per axis: on the dispatch axis an
EXPRESSION of the domain's that reads the placeholder by name, resolved like any expression once the
element has been written over it; on the state axis an OPERAND of the domain's op — a path, a target,
an address. The collection is a list of `JVal`, the one value vocabulary the core owns, and a literal:
its length is the bound, fixed by the tree (D2), and no store is consulted to read it.

**2. It lowers by substitution, and the SUBSTITUTION IS THE WITNESS'S.** D1 says richer control
structure is vocabulary that lowers to the core, never a second evaluator. The core cannot see
inside an action or an op — that is the whole of D18 — so it cannot write an element over a
placeholder in one. What it can do is ask: `ActionWitness.Substitute: string -> JVal -> 'Action ->
'Action` and `StateWitness.Substitute: string -> JVal -> 'Op -> 'Op` are the two new arrows, each the
domain's own recursion over its own type, and each is called ONLY when an `Each` is met — a domain
with none fills them with the identity (the UI adapter does, and the toy's one op). The fold lowers
an `Each` to the body with each element substituted, in collection order, and folds the results as
it folds a `Sequence`'s members: the store threaded, the lists concatenated, the first halt the whole
answer. The handler plans an op-channel `Each` the same way, element by element. There is no new
evaluator, no new channel to state and no new case for anything downstream to price: the budget sums
the lowered elements' costs (no step for a bound, because a literal collection is not read), the
demanded projection and the replay classification read the lowered form, `OpView.beneath` lists the
substituted ops so the argument policy sees every address a placeholder stands for, the undo defects
read the substituted ops' classes (which the unsubstituted body cannot show — a publish to `{t}` is
compensable for one element and one-way for another), the trace records the elements that ran under
`Trace.Each` so a reader sees how many (`FlowDecision.Iterated`), and the inverse of an `Each` run
is the SEQUENCE of its elements' inverses in reverse order — each element overwrote different values,
exactly as D21 said of a repeat's iterations. Reversal is inherited, not re-proved.

**3. What the model owns, and what the witness does.** The model's `VEach act elements` and `OEach
elements` carry the elements ALREADY LOWERED — the body once per element, substituted — because the
model's action and op types are abstract and substitution into them is not a thing the model can
define; the witness's `View`, applied to exhaustion, is where the element meets the body, exactly as
it is where an action meets its shape. Over that view `each_is_lowering` (`BoundedFold.fst`) is an
EQUATION: an `Each` folds to the same outcome and placement as the sequence of its elements, traces
the same steps under its own constructor, is in the reversible fragment exactly when the sequence is,
and is priced as the sequence is; `each_reverse_is_sequence_reverse` says its inverse is the
sequence's; on the op axis `each_plans_as_lowered` (`Staging.fst`) says it plans as its elements
concatenated. Every existing theorem — totality, blindness, the reserved-key invariant, the halting
clause, `traced_agrees`, `reverse_run`, `fold_steps_within_cost`, the staging, gate and undo ladders
— extends over the shape as over a sequence, with no new lemma beyond the mechanical `_each` cases
that mirror the `_repeat` ones. **The obligation this leaves on the witness, stated rather than
assumed away:** `Substitute` preserves the body's SHAPE — the substituted action or op views as the
original does, with the placeholder replaced in every operand and nothing else moved; a nested `Each`
keeps its own collection and placeholder. It sits beside the obligation that `View` unfolds finitely,
and it is TESTED where the model cannot prove it: the differential hosts hand the model the elements
substituted through the production `Substitute` and run production, which substitutes as it folds,
beside it, over a toy corpus that covers an empty collection, one element, several, a halt inside an
element, nested iterations with distinct placeholders, and an `Each` inside the other two flow shapes;
a `Substitute` that moved the shape would show as a divergence there. A property the ladder also
records: on a tree containing no `Each` the lowering is never invoked — a witness whose `Substitute`
throws runs the Each-free corpus to the same answers through every walk.

**4. The element cannot be overwritten, so the case D21 refused an index for does not arise.** The
element is a value in the TREE — written over the placeholder before the body runs — and not a cell
in the store the body writes. A body that assigns to a key cannot change what the placeholder stood
for in its own or any later element; the inverse runs the elements' inverses in reverse order against
exactly the elements the forward run saw, because the elements are the collection's, fixed by the
tree. This is what D21's reversal argument required of an index and could not have from one.

**5. The binding rule, and where it is enforced.** An `Each` binds exactly ONE placeholder,
LEXICALLY, over its body. An `Each` nested inside another names its OWN placeholder, and a name an
enclosing `Each` already binds is REFUSED rather than shadowed — so no body ever reads a placeholder
two binders could mean, and the substitution order of nested lowerings (the inner lowered first, the
outer written over the result) can never change what an element stands for. A body that reads a
placeholder no enclosing `Each` binds is refused. Both are decided from the TREE alone: the core walks
the view with the set of bound names, and asks the witness only which placeholders a node's OWN
operands read (`ActionWitness.Placeholders`, `StateWitness.Placeholders` — the two other new arrows;
`[]` for a domain with none). The refusal is at VALIDATION, never at run time: `BoundedActions.run`
and `runTraced` refuse an ill-scoped program at entry, before its first step, as a halt naming the
placeholder with nothing run, written or emitted; `Handler.planOps` refuses an `ApplyOps` effect
before its first op plans, naming the placeholder as the arm's `Failed`. The static walks are public
(`BoundedActions.scopeDefects`, `StateWitness.scopeDefects`) so a host that validates a program at
registration reads the same answer. The run-time fallback — a placeholder reaching a resolver
unsubstituted — is a domain error the scope check exists to make unreachable, and the toy's resolver
says so if it ever is. Not modelled: the model's expressions and ops are abstract and read no names;
the ladder carries the refusal as tested.

**6. The specification does NOT move, and the phase's wire task is answered rather than performed.**
The phase as filed asked for `ProgramWire` and `HandlerWire` to encode `Each`, with the
specification's normative text, schemas, emitter, manifest and new fixtures in the same change-set.
Checked against `docs/generic-tier.md` §6: the program wire specification never spells a shape of the
action algebra — its §3 names the action and op vocabularies as referenced, "specified elsewhere", and
its rule 2 forbids restating their cases; a compute stage's `action` and an `ApplyOps` effect's ops are
carried by the DOMAIN's own codec (`ActionWitness.Encode`, `Stream.Encode`), spliced verbatim (K6).
`Choose` and `Repeat` moved no wire member for the same reason (D21: "no wire member moved"), and so
`Each` moves none: where the wire carries an `Each` compactly, it is the domain's codec that carries it
— the toy's and the verb's test codecs gain a case and round-trip it; the UI adapter's codec is the UI
specification's and the UI tier adopts no `Each`. `ProgramWire`'s one touch is the replay
classification, which is a walk over the view and not an encoding. No schema, fixture byte, refusal
class or rule of the specification moves, the five-artefact forward coupling is not engaged, and the
codec families and the driver scenarios pass byte for byte. A refusal class for the scope defect was
weighed — it would make the scope check a decode-time refusal across the specification's vocabulary —
and declined as a specification act this phase has no need of: the refusal lands at the fold's entry
and the handler's plan, through vocabularies that exist.

**7. D14 applied a sixth time, and what the restatement found.** The model's `VEach` and `OEach`
fold, trace, plan and walk as a sequence does — their cases in every lemma call the list lemma that
already exists — so termination stays structural (an element is a subterm of the list of elements)
and the four modules re-check in their usual time with no change to any `decreases` clause and no new
proof technique. The finding worth recording is the one that shaped item 3: a model that substituted
itself — a witness whose `w_resolve` pre-substitutes, or a syntactic `subst` over the view — was
designed first and set aside. Both need the fold's `VEach` case to recurse into a term that is not a
subterm (the substituted body), which costs either a size measure on every lemma in the module or a
commutation law for nested placeholders stated as a hypothesis; and both put into the model a
substitution the model's abstract `e` and `a` cannot define, so the obligation on the witness would
have been the same law under a longer proof. Carrying the lowered elements in the view puts the
obligation where D18 already puts `View`'s, and leaves the proofs saying exactly what they say.

**Version.** Rides the `0.7.0` draft: the slot is untagged, publicly unpinned and already breaking,
and this is breaking of the same class — a case on `ActionView` and on `OpView<'Op>`, two members on
`ActionWitness` and two on `StateWitness`, a case on `Trace` and on `FlowDecision`, and
`OpView.beneath` taking the substitution. No wire member, fixture byte or demanded-document byte
moves for any program that uses no `Each`; the demanded document stays at version 6. `STABILITY.md`
has the consumer's account and `docs/migrations/phase-1990.md` the diff.

**What this forecloses, and what it leaves.** The collection is LITERAL: a collection the store
holds — a region's extent read once, recorded in the trace, replayed from the record — is Phase 1991's,
under the operator's four conditions, and nothing here presumes its shape. A placeholder stands for a
VALUE: a placeholder in a key of the dispatch axis's store (`Assign`'s key is a string the view
carries) is not expressible, by design — on that axis an address-dependent write is a leaf or an op,
and the op axis carries it. Shadowing is refused rather than defined; a later need for it is a change
to item 5, with the substitution order then load-bearing. The UI adapter adopts neither shape; a UI
handler that would genuinely benefit lands as a finding against `ui_view_no_flow`'s test.

## D30 — Program certifies its wire format over a non-UI witness: the toy is a specification subject with a scenario family of its own, a second obligation lets a trace record a handler run, and a family selection says which families a run certifies (2026-10-03)

**2026-10-03. fuaran#2011, the first of the phases that move the UI adapters out of this repository
(fuaran#2012 moves them). Specification side: `PROGRAM_WIRE.md` §10.6, the four `toy-*` schemas, the
`driver-semantics-toy` scenarios and the checker's toy branch. No package moves: everything on this
side is in `tests/`, so `STABILITY.md` is untouched and no version advances.**

Until this phase Program could certify its own wire format only through the UI witness. The
specification's `driver-semantics` family records UI trees; the parity suite, the Fable leg, the
server suite and the proof host's corpus all ran them through UI-tier types. Moving the adapters
would have taken half of this repository's conformance evidence with them, and left a gate that
could not go green on its own format.

**1. The toy witness is a specification subject, and its vocabulary is OWNED there.** The toy of
D18 (`tests/Fuaran.Program.Tests/ToyDomain.fs`) fills all three axes and names every shape the fold
distinguishes, so it is the witness the specification now states in full: a tree (node, label
expression, handlers by event, children), the nine actions and five expressions, the `Relabel` op,
the state and the `Sound` effect (§10.6). Unlike the UI tree it is not a referenced vocabulary, so
the specification's §2 governs it without exception, a scenario's `tree.json` is its canonical
bytes, and a recorded tree is compared as an owned document — decoded, re-encoded, compared. The
effect alone adopts §5.2's envelope (a `kind` discriminator, the emitter's member order), because a
trace compares effects as emitted.

**2. The wire codec is written BESIDE the witness, not into it.** `ToyWire.fs` encodes and decodes
every toy document; the witness's own `Encode` members are left exactly as they were. Those members
are what the core hashes and the replay classification reads — an action's canonical form names only
what that classification needs — and widening them would have moved digests the genericity suite and
the proof hosts pin, for a reason that has nothing to do with either. Two encoders of one action is
the cost, and it is bounded: the op's two spellings are pinned equal by a test, and the action's are
two different documents for two different readers. A decoded `Ring` carries a closure that FAILS if
invoked, so a fold that recovered behaviour from the slot would be caught rather than read as inert.

**3. A trace can now record a handler run, through a second OBLIGATION rather than a new shape.**
The shard listed the argument-policy gate, staged perform and the op channel among the vocabulary the
family covers. Checked against the corpus, the premise did not hold for the UI family it named as the
model: no UI scenario records a handler run — the server suite registers a handler against
`server-handler-call` and asserts the outcome in F#, so that reading was never in the corpus. Nor
does the UI family reach `Choose`, `Repeat`, `Each` or `Require`: the UI witness views no action as
any of them. So the toy family covers the listed vocabulary by adding what was missing rather than
by mirroring the UI family. §10.2 already records the obligation a scenario presumes "so that a
scenario presuming something else enumerates rather than renumbers"; this phase is the first to use
that, with `handler-loop`. A handler-loop scenario names its handlers as `hostHandlers` — named,
never carried, on §10.3's reasoning about policies — and §10.6 registers one set, `toy-relabel`,
whose three handlers pin the op channel committing an edit, the argument policy refusing a plan, and
a staged host call declining after a complete plan. A placement with no handler registry is out of
scope for those scenarios, and the client legs say so by name.

**4. The legs.** The conformance leg is `ToyFamilyTests.fs` in `Fuaran.Program.Tests`, which
references no UI-tier package (`CoreBoundaryTests` names the harness files and reads the project's
declared, resolved and built references): every scenario at the client placement and at the generic
`ServerSession` under the handler set it names, the no-handler server reading against the client,
the count, and a mutated trace going red at the first step it touches. The Fable leg compiles the
same shared runner (`ToyScenarios.fs`) to JavaScript. The proof host folds every scripted action of
every toy scenario through the extraction and the ported core (`proofs.json`,
`toy-family-model-agrees-with-shipped-code`); a handler-loop scenario's call is compared unanswered
there, since answering it is the staging oracle's subject. Each leg prints the family's scenario
count. The seeds that generate the corpus are `ToySeeds.fs`, emitted by
`Fuaran.Program.Tests --emit-toy-scenarios`, which records a trace only when every placement in
scope agrees, and a test holds the corpus and the seeds equal.

**5. A family selection, and a record of who certifies what.** `FUARAN_PROGRAM_FAMILIES` (or
`run.ps1 -Families`) selects the families a run certifies; an undeclared name fails the run. Every
suite that reads a family consults it and skips a deselected family by name — except the server
suite, whose two UI-family legs read the parity loader unconditionally and are UI-typed, so they
leave with the adapters in fuaran#2012. That exception is RECORDED, not hidden: `ToyFamilyTests`
holds the table of which families each suite certifies and which suite ignores the selection, and
checks both against the suites' own sources, so a suite that starts or stops reading a family, or
consulting the selection, without the table saying so goes red. On a toy-alone run the gate is green,
the parity suite, the proof host's UI corpus and the Fable leg's UI half skip by name, and the server
suite still exercises the UI family — which is the one sense in which "the toy family alone" is not
yet the whole gate, and the one fuaran#2012 closes. (Amended by D31, fuaran#2017: those legs, and the
proof hosts and codec certification beside them, are no longer the only certification of anything this
repository keeps — the core's evidence is re-hosted at the toy witness, so what fuaran#2012 moves is the
UI adapter's own evidence and nothing Program must keep.)

**6. The UI family's loader reads its own family only.** `FixtureIo.scenarios` and the Fable leg's
`loadScenarios` took every manifest scenario; with a second family in the manifest they would have
handed a toy tree to the UI decoder. Both now filter by family, which is also what lets the server
suite stay unedited here.

**What this forecloses, and what it leaves.** It does not make the toy a product or a second
implementation of the loop (§11.3's deferral stands: one host still records both families). It does
not specify the toy's `exit` assertion, which no scenario exercises, nor a host policy for the toy,
whose one effect names no destination. The UI family is untouched, byte for byte, and moves to the UI
tier's repository with the adapters.

## D31 — The core's proof differentials and its codec certification are re-hosted at the toy witness, in the project that reaches no UI type; what stays behind the UI tier is evidence ABOUT the UI tier (2026-10-03)

**2026-10-03. fuaran#2017, between fuaran#2011 and fuaran#2012. Specification side: `PROGRAM_WIRE.md`
§10.7, four `toy-*` vector families with a `subject` member, `vocabularyFreeDocuments` in the manifest,
and toy models in all three emitters. No package moves and no `src/` file changes: everything on this
side is in `tests/`, `proofs.json` and `proofs/check.ps1`, so `STABILITY.md` is untouched and no
version advances. No `.fst` model and no proved statement changed.**

**Why.** fuaran#2012 was dispatched to move the UI adapters and their suites out, and stopped before
moving anything: eleven of the ladder's twelve tested claims, and the only certification of
`ProgramWire`/`HandlerWire` against the specification's vectors, were hosted in suites that reach the UI
tier. Their hosts compile `proofs/oracle`, which is not a package; moved, they would have left the core
uncompared with its models on any gate of its own, with both gates green (re-homing D7). The guard it
landed (`CoreBoundaryTests`, `uiHostedEvidence`) is what this decision shrinks.

**1. Every differential about the CORE runs at the toy witness, in `Fuaran.Program.Tests`.** That
project now references the oracle project (FSharp.Core-only, unpacked). New hosts, each a port of its
UI original with at least its case count and every go-red case kept:
`ToyStagingOracleTests.fs` (staging 8, effect gate 12 — including the op-contract wrapper and the keyed
handler host with its durable reading — and undo 6), `ToyDurableReplayTests.fs` (durable replay 13),
`ToyBoundedFoldOracleTests.fs` (the generic fold with the flow-shape, each and toy-family claims, 7).
The toy's apply is total and its witness has no guard, so the hosts COMPOSE what the UI corpus reached:
a strict apply refusing a relabel of an absent node, a view making check relabels `Require`, an `Undo`
classing them one-way and one relabel a declared compensation. The toy witness itself is unchanged —
its `Encode` shapes are digests other tests pin. Each claim in `proofs.json` names its toy host; the
UI copies still run where they were until fuaran#2012 moves them, and are no longer any claim's host.

**2. The proof leg runs several hosts per module.** `proofs/check.ps1` gives each module a list of
hosts, the toy host first; a module whose model also describes UI-adapter code keeps its UI host
second, and that entry leaves with the adapters.

**3. The codec is certified at a second subject the specification owns.** The vectors in the UI
vocabulary stay exactly as they were. The specification adds toy-subject vectors for every codec path
the UI vectors cover that the toy can reach — handler, server effect, the toy's one client effect, and
outcome — with their derived replay classification, reproduced by all three emitters; invocation and
logic-tree-ref documents carry no vocabulary and are certified by a host of either subject as they
stand. `ToyWireConformanceTests.fs` certifies the GENERIC codecs at a toy witness composed with the
toy's wire codec (`ToyWire`), with the same five properties as the UI host; the UI host now certifies
the vectors with no `subject`, and checks that the two subjects partition the manifest.

**4. What stays behind the UI tier, and why it may.** `uiHostedEvidence` is now the bounded fold over
the UI witness's fourteen arms, the budget's UI weighing and the UI BoundedDriver's G2 gate, and the
UI-typed codec certification — evidence about the UI adapter's code, which leaves with it. The core's
half of the budget claim is a new tested claim, `budget-generic-model-agrees-at-the-toy-witness`. A new
boundary test moves a re-hosted claim back onto a UI-reaching suite, directly and through a project
reference, and requires the guard to see it.

**Findings recorded, not fixed here.**
- **The budget model has no flow shapes.** `Budget.fst`'s `act` is `ALeaf | AChain`, so the model prices
  `Choose`, `Repeat` and `Each` as leaves, while `Budget.actionCascadeCost` prices them by their arms,
  bounds and elements. Invisible at the UI witness (no UI action views as a flow shape); real at the
  core. A named case in the toy budget host holds it and goes red when the model gains the shapes.
  Closing it is a model change.
- **§9.5's refusal is keyed on the referenced vocabulary.** `ProgramWire.refuseResultTarget` refuses a
  `Call` carrying `into`; a toy handler whose `Ring` is `targeted` decodes and is refused only by the
  fold (§10.6). The specification records that no toy vector pins the codec's reading; deciding it needs
  either a witness-level rule in the codec or a ruling in the text.
- **The text names no refusal class for a `Sound` with an extra member.** `ToyWire.decodeEffect`
  answers `missing-member`; §5.2's arms answer `undeclared-member`. §10.7 records it as open; no vector
  pins it.
- **A count in the ladder was wrong.** `flow-shapes-model-agrees-with-shipped-code` said nineteen flow
  cases; the corpus carries eighteen, now asserted by label.

**What this forecloses, and what it leaves.** It does not move anything out of the repository; that is
fuaran#2012's, and its precondition is now met for the core's evidence. The UI-witness claims above
still compile `proofs/oracle`, so moving their hosts is a decision fuaran#2012 has to take about the
UI half of the models; this decision does not take it.
