# Fuaran.Program.Server

The **server-logic placement** of the bounded program loop: a generated tree names a handler, and
the handler runs as data — read, compute, mutate, respond — with no hand-authored update function.

> **This is a spike.** It exists to answer whether the same algebra reaches across the client/server
> boundary without being reimplemented. It commits **no wire form**: a handler is host-registered
> data, the tree carries only the endpoint that names one, and giving handlers a serialised form is
> a later, separate act.

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

The questions that remain open — idempotency on replay, and the schema coupling between a query and
the tree that reads its result — are written down in
[`docs/server-handler-atomicity.md`](../../docs/server-handler-atomicity.md) as input to the wire cut,
rather than being settled silently in code.

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
tree-declared result target is refused).

## Licence

Apache-2.0.
