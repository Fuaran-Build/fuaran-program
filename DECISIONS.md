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
