# Fuaran.Program.Runtime

The **client placement** of the bounded program loop: run a wire-decoded program tree interactively
in the browser with no hand-authored `update` function, no message type, and no server.

## The shape

```
inbound event
  → validate (the same G1 gate the server placement uses)
  → interpret the bounded action against the store   (the SHARED interpreter)
  → re-resolve the FIXED base tree's bindings        (the SHARED resolution pass)
  → render the resolved tree
  → perform the closure-free effects
```

The server placement does the same first three steps and then *diffs and lowers to DOM patches*,
because what will apply them is across a wire. Here the renderer is in the same process, so the
resolved tree goes straight to it.

**The interpreter is not reimplemented here.** It is `Fuaran.Program.Bounded`'s, and so is the
re-resolution pass and the resource budget. "One algebra, two placements" is therefore a property of
the code rather than a claim in a document — a change to the fold changes both placements at once,
and neither can drift from the other.

## Injected seams, not dependencies

The loop takes no dependency on a renderer, a transport, or a sink. `ProgramServices` carries:

| Seam | What a browser host wires | What a test wires |
|---|---|---|
| `Render` | the tier-1 renderer | a recorder |
| `PerformEffect` | the browser effect performer | a recorder |
| `OnApply` | an adapter onto the host's op-stream sink | a recorder |
| `CanDispatch` | the host's dispatch policy | allow / deny |
| `Channel` | a live channel, for the hybrid mode | a fake, or `None` |

That is what lets the identical loop run in a browser and under a headless test with nothing stubbed
out — the tests in this repo drive the real loop, not a test double of it.

`OnApply` is a plain `TreeOp<obj> list -> unit` callback rather than an op-stream sink interface,
matching the server placement's seam of the same name: sequence assignment and stream identity are
host policy, so a host adapts the callback onto whatever sink it keeps.

## Defaults are closed

`ProgramServices.create` **denies every dispatch** and performs no effects. The permissive posture is
reached by name (`createPermissive`), because this loop exists to run *emitted* trees — the tree is
untrusted by construction, and a default that assumed otherwise would contradict the reason the
bounded path exists. The per-interaction budget (`MaxActions`, `MaxNodes`) is inherited from the
shared budget module, so a breach means the same thing here as on a server session.

## Checking coverage before the first render

`Program.mkBoundedStrict` builds a program only if this host can cover everything the tree is able to
ask for, using the shared package's demanded-effect projection. The coverage is not passed in: it is
read off the effect registry the services already carry (`Program.coverageOf`), so a pre-execution
verdict and a dispatch-time one are computed from the same two facts — what is registered, and what
the gate permits — and cannot disagree.

A refusal happens **before** the initial render, so a refused program has not painted, subscribed or
performed anything, and it returns every finding rather than the first.

`mkBounded` remains the default. An uncoverable effect is otherwise refused where it is dispatched
and the denial is recorded, which keeps a mostly-serviceable program serviceable; strict mode is for
a host that would rather not start at all.

## Hybrid mode

Pure client-only operation is the default and needs no channel. Supplying an `IClientLiveChannel`
lets a program also receive server-pushed `TreeOp`s: pushed ops edit the **base** tree, and local
state re-resolves on top of the new structure rather than being overwritten by it. `dispose` releases
the subscription.

## Design commitments

See [`DECISIONS.md`](../../DECISIONS.md) — D2 (total, not Turing-complete), D3 (closed per-placement
effect vocabularies behind a registered, policy-gated host seam), D4/D5 (the UI-typed first
instantiation and its one-way dependency direction).

## Licence

Apache-2.0.
