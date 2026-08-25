# Fuaran.Program.Server

The **server-logic placement** of the bounded program loop: a generated tree names a handler, and
the handler runs as data — read, compute, mutate, respond — with no hand-authored update function.

> **The handler now has a wire form** (`HandlerWire`), specified and conformance-tested against a
> corpus that lives outside this package. A handler is still host-registered: the tree carries only
> the endpoint that names one, and nothing here makes a received handler runnable — registration
> stays a host act. What the wire form changes is that a handler can now be *shipped, inspected,
> diffed and checked* somewhere it does not run.
>
> One consequence is easy to miss because it changes no code: **every string in a wire-carried
> handler is untrusted**. Before, a handler's body was host data and only the endpoint came off the
> wire — which is why it was safe to record a host-function name in a log and unsafe to record an
> endpoint. That asymmetry is gone, so a diagnostic carries the *derived* capability and never a name
> a document supplied.

## The shape

```
inbound event
  → validate                                   (the SAME gate as the other placements)
  → budget                                     (the SAME cost functions)
  → interpret the bounded action against the store   (the SHARED interpreter)
      … or, at a call whose endpoint names a handler, run the handler's stages
  → perform the handler's effects, gated        (this placement's own vocabulary)
  → re-resolve the base tree's bindings         (the SHARED resolution pass)
  → respond
```

Only the effect line is new. Everything else is imported from `Fuaran.Program.Bounded`, which is
what makes "one algebra, several placements" a property of the code rather than a claim in a
document — and what lets the tier-parity family assert that a tree naming **no** handler behaves
here exactly as it does in a browser.

## A handler is a stage list

```fsharp
{ Name = "refresh"
  Stages =
    [ Effect (ServerEffect.RunQuery("rows", source, pipeline))
      Compute (Action.SetState("status", Some (JStr "ready"), None))
      Effect (ServerEffect.ApplyOps ops)
      Effect (ServerEffect.Notify("audit", payload)) ] }
```

`Compute` is the bounded algebra, run by the shared interpreter — there is exactly one call site for
it in the package, and nothing else in the package matches on an action at all. `Effect` is one arm
of the closed server vocabulary. Sequencing is the core algebra's; the stage list only lets the two
vocabularies interleave, which is what a handler is for.

## The effect vocabulary is closed

| Arm | What it does |
|---|---|
| `RunQuery` | evaluate a declarative pipeline over a source; the table lands in a query slot |
| `ApplyOps` | **the only domain-state mutation** — a `TreeOp` sequence through the apply engine |
| `HostCall` | the named, registered, policy-gated escape to computation the total algebra cannot express |
| `EmitPatch` | ops shipped to a connected client, touching nothing durable |
| `Notify` | a host-channel message |

Extensibility is a host act — register a performer — never a widening of the DU. The gate is
consulted **before** anything runs: before a pipeline is evaluated, before an op reaches the apply
engine, and before a performer is looked up as a callable, so no side effect of any kind can precede
the policy decision. Every refusal is recorded, and a refusal carries the capability only, never the
payload it wanted to act on.

## Atomicity, stated honestly

The **handler** is the unit. Stages thread a value; nothing commits until the last stage succeeds; a
denial or a failure discards the store, the ops, the effects and the notifications and keeps only the
diagnostics that say why. A half-applied handler is unrepresentable in the returned value, and
`Committed` reports which happened.

A run has **two phases** (D8), which is what extends that guarantee past the state this placement
owns. The PLAN phase runs every stage, gating each host call, resolving its performer and checking its
landing slot but **not invoking it**; the PERFORM phase, reached only if the plan completed, invokes
the staged calls in declaration order. So a domain failure happens before anything external runs. The
price is stated: a later stage cannot read an earlier host call's result. The residual staging does
not abolish — a performer failing in the perform phase leaves its predecessors run — is reported, as
`Committed = false` with `Performed` naming exactly the calls that happened.

The question the note left open — **idempotency on replay** — is answered by the second interpreter
below, and answered with its boundary attached rather than in general. The note itself
([`docs/server-handler-atomicity.md`](../../docs/server-handler-atomicity.md)) remains the record of
how the trade was framed.

## A second interpreter: durable execution

The same algebra, a different discipline. `Durable.run` interprets the **same handler**, through the
**same stage fold**, under **deterministic replay over an effect journal**: re-running an invocation
from its entry state recomputes every step it took, and the one kind of step a re-run cannot
recompute — a `HostCall`, whose answer came from outside — is served from the journal instead of
being repeated.

It calls `Handler.run` rather than reproducing it. What it supplies is a registry whose performers
consult the journal first, which puts the entire difference between the two interpreters at the one
arm that commits outside. `ServerSession.stepWith` takes the arm as an argument, so the second
interpreter reaches the placement without a second copy of the validate → budget → fold →
re-resolve sequence.

A step is journaled **twice** — `Attempted` before the effect and `Completed`/`Refused` after — so a
crash has three readable outcomes rather than two:

| The journal says | What a replay does |
|---|---|
| nothing | run the step |
| attempted, and a result | **serve the result**; the performer is not invoked |
| attempted, no result | **indeterminate** — refused by default |

The third row is where "exactly once" genuinely ends, and nothing in this package closes it: the
effect commits in a system this host does not own, and the journal is a second system, so a crash can
land between them. `DurableServices.acceptingIndeterminateReplay` is the named opt-in that re-invokes
instead — taking the duplicate hazard rather than the loss — and it returns an override **record**,
on exactly the terms `Replay.admit`'s does.

A replay that reaches an ordinal holding a *different* capability from the recorded run is refused
(`durable-replay-divergence`) rather than served the wrong answer.

## The facet a placement may claim

`Facets` derives what a registration guarantees — a delivery facet, an idempotency facet and a
restart visibility — from the handler stages **and** the interpreter's discipline, because the same
stage list guarantees different things under the two. The vocabulary mirrors a composition
contract's, tag for tag, so the two sides agree on names without either taking a type dependency on
the other.

Delivery is deliberately **not ranked** — at-most-once may lose, at-least-once may duplicate, neither
dominates — so the conjunction runs over the hazard (may lose, may duplicate) where union is
associative, commutative, idempotent and has an identity. The composition is therefore never stronger
than any arm it holds, and where a registration proves *both* hazards the answer is that **no named
facet says it**, rather than whichever of the two reads better.

`ExactlyOnceEffective` is reachable and is not free:

- the four arms that never leave the interpreter earn it under replay, because they are recomputed
  rather than re-performed;
- a `HostCall` earns it only where the host has **declared** the performer idempotent, or declared it
  deduplicated by a store *and* configured re-invocation;
- an **undeclared** performer earns it under no configuration at all — strict refusal makes the
  placement at-most-once and re-invocation makes it at-least-once, and neither is exactly-once;
- a journal that does not survive a restart derives the direct interpreter's posture exactly.

`Facets.checkDeclaration` is the end-to-end check: from the registration, through the per-arm
derivation and the conjunction, to the triple a composition's logic-tree slot carries. A declaration
that promises **less** raises nothing; one that promises more is refused, per axis
(`facet-delivery-inflated` and its siblings), with an `facet-undeclared-performer` line saying why the
derivation was weak. `Durable.declaration` produces the honest declaration directly, naming the
placement (`PlacementId.durable`) and the logic tree it is about by the D6 by-id reference.

## Replay: two modes, and a classification that says why

A handler's replay safety is **derived from its declared form** — `safe`, `unsafe`, or `unknown`,
where the middle value is the point: only a proof is a finding, and an undecidable stage is reported
as undecided rather than guessed in either direction. `HandlerWire.replayReasons` is the primitive
and the classification is the verdict of its reasons, so the two cannot disagree; each reason names a
stage ORDINAL and a defect from a closed vocabulary — `relative-addressing`, `non-literal-write`,
`opaque-host-call` and the rest — never a string the handler document supplied.

`Replay.admit` is the decision that consumes it. A host names which replay it is in and gets what it
is admitted to do:

- **`Audit`** — apply the recorded ops and nothing else. The handler is not consulted at all, which
  is the code shape of "effect-free unconditionally": there is nothing here for a later edit to make
  conditional on a classification.
- **`Resume`** — re-evaluate read stages, and only those. An `unsafe` handler is **refused** with a
  typed code carrying its reasons; an `unknown` one proceeds, carrying its reasons, because refusing
  it would round "no proof available" up to a proof of harm. A host explicitly configured to accept
  re-execution is admitted and receives an override **record**, so a resume that overrode a refusal
  is afterwards distinguishable from one that never needed to.

This is the admission decision, not a resume engine: it answers whether a handler may be resumed and
what a resumer may re-evaluate, from data alone, before anything runs. Performing the re-evaluation
is the session's job.

`Replay.ofTreeAndHandlers` is the projection join — the capability envelope below, with each
reachable handler's posture on it, so a manifest can state replay posture per handler without
re-deriving it.

## Asking before running: the capability envelope, both placements at once

`ServerDemanded.ofTreeAndHandlers handlers tree` answers **what this program on this host can ever
ask for**, as one document spanning both placements: the tree's own client-tier demands, plus, for
every handler the tree can NAME, that handler's server-effect discriminators, the capabilities its
gate will be asked about, the host functions and channels it reaches, and the state namespaces its
landing slots write. Every one of those names is read off the effect value through the same two
functions the interpreter and the gate use, so a demanded capability and a gated one are the same
string by construction rather than by agreement.

It is exact for a stronger reason than the client tier's. A handler is host-registered data whose
stage list is fixed before any untrusted tree arrives, so the walk is not inferring an envelope — it
is reading one the host already wrote down.

`ServerSession.initStrict` is the opt-in construction path over it, and it reads its server-side
coverage off the effect registry the session was wired with rather than asking for it twice.
Unregistered and gate-refused stay **separate findings**, mirroring the runtime denial vocabulary: a
host function with no performer is absent whatever the policy says, and only the second is fixed by a
policy change. `init` remains the default — an uncoverable demand is still refused where it is made.

Three silences are deliberate. An endpoint with no registered handler contributes nothing and yields
no finding: that string is the one value in this subsystem that comes off the wire, and a
pre-execution finding naming it would be exactly the leak the payload-free denials avoid. A call
action inside a stage demands nothing, because it is the documented no-op. And a query's landing slot
is not a demand — the source it reads is.

## Refusing an unsatisfiable handler before the tree runs

The schema coupling between a query and the node that reads its result used to be a runtime failure,
and a deliberately thin one — the diagnostic carries the evaluation error's discriminator and never
its message, because the message quotes names taken from a pipeline that is host-declared today and
wire-carried after the cut.

`ServerSession.initStrict`, above, is the other half of that trade — its second half. Alongside the
coverage question it derives each pipeline's output schema statically (`Schema.ofTransform`) and checks
it against the readers' declared columns. The two ask different questions and their findings stay apart
(`ServerStrictFinding`): a host correcting the first edits its registry, and one correcting the second
edits a pipeline or a grid. The refusal names the column, the reader and what the query does provide — it can, because it
happens before the untrusted tree is involved. The runtime posture is unchanged.

`ServerSession.querySchemaReport` returns the same walk as data, including what it declined to decide:
a query whose output is not statically closed and a reader whose projection is closure-held are both
legitimate, so neither is a finding. `ServerServices.withSourceSchema` declares a named source's schema
beside the resolver that serves its rows; declaring buys the check, and not declaring costs only the
check. `init` remains the default, exactly as at the other placements.

## Defaults are closed

`ServerServices.create` denies every dispatch, registers no handler, permits no effect and resolves
no data source. `createPermissive` opens the two gates by name; it conjures no handlers, performers
or sources, because those are host acts. This loop runs emitted trees against durable state, which is
the most consequential thing in this domain — an open default would contradict the reason the bounded
path exists.

## Design commitments

See [`DECISIONS.md`](../../DECISIONS.md) — D1 (pipeline core; no second evaluator), D2 (total, not
Turing-complete, with host functions as the escape), D3 (closed per-placement effect vocabularies
behind a registered, policy-gated seam), D4/D5 (the UI-typed first instantiation and its one-way
dependency direction), D6 (the by-id reference vocabulary for placing a program tree), D7 (call
recognition is an arm of the shared fold, so this package matches on an `Action` nowhere), D8
(host-effect atomicity is two-phase staging), D9 (the handler declares where its results land; a
tree-declared result target is refused), D10 (a query reader's expectation is declared, by the fields
the reading node already carries), D11 (the evaluation suite lives outside this repository), D12 (the
durable interpreter journals the one arm that reaches outside, and the indeterminate window is
declared rather than closed).

## Licence

Apache-2.0.
