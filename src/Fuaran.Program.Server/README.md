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

That is total for the state this placement owns, and **not** total for a host performer that already
ran. The open questions — this one, idempotency on replay, and the schema coupling between a query
and the tree that reads its result — are written down in
[`docs/server-handler-atomicity.md`](../../docs/server-handler-atomicity.md) as input to the wire
cut, rather than being settled silently in code.

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
dependency direction), D6 (the by-id reference vocabulary for placing a program tree).

## Licence

Apache-2.0.
