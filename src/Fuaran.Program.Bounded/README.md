# Fuaran.Program.Bounded

The **bounded program interpreter** — a total fold of the wire-representable action set over a
program's state store — plus the server placement of the program loop that drives it.

## What this package is for

A program tree that arrived over the wire is data, not code. This package is what runs it:

- **`BoundedActions.runBoundedAction`** — the interpreter. One resolved action, one store, one
  outcome (updated store + closure-free effects + diagnostics). It is **placement-neutral**: the
  same fold drives a server session (`BoundedDriver`, here) and a browser client
  (`Fuaran.Program.Runtime`). "One algebra, two placements" holds by construction — a single
  interpreter, a single invariant, two hosts.
- **`Resolve.resolveTree`** — the binding re-resolution pass. Substitutes every resolvable binding
  with its resolved `Binding.Static` value so a state change is visible to a structural diff (a
  `Binding.State(key, default)` canonical-encodes identically whatever the store holds, so without
  this pass a state change produces no ops at all).
- **`BoundedDriver`** — the server placement: validate → interpret → re-resolve → diff → lower,
  under a per-interaction resource budget.

## The two invariants

**No foreign code.** The wire format cannot carry a closure; the decoder substitutes inert
placeholders for every closure slot. This interpreter enforces the other half — it **never invokes**
any closure an action carries. The only state mutation is the `SetState` write; the only outward
effects are the closure-free effect arms. Together: running an emitted program has no
arbitrary-code-execution surface.

**No arbitrary cost.** Bounded code is not bounded cost — a tree can still drive an enormous chain or
be pathologically large. `InteractionBudget` caps the action-cascade size and the tree's render cost
per interaction, and a breach is a structured refusal, never a hang and never a partial mutation.

Bounded code + bounded cost is what makes a program safe to run untrusted on shared infrastructure.

## Design commitments

The algebra's shape — a pipeline core, with richer control structure expressed as vocabulary atop it
rather than as a second evaluator — is [`DECISIONS.md`](../../DECISIONS.md) **D1**. The closed
per-placement effect vocabulary and its registered host-performer seam is **D3**. The UI-typed first
instantiation, and the dependency direction that follows from it, are **D4** and **D5**. This README
cites those decisions rather than restating them; `DECISIONS.md` is authoritative.

## Documented no-ops

Actions with no form on the bounded path (`Notify` / `AiTool` / `Invoke` / `Dispatch` / `Call` /
`CommitLocal`) are **documented no-ops that emit a readable diagnostic**, not silent nothings. A
generated tree that *intended* one of them is observable to whoever is debugging the emission. The
no-op is the correct behaviour; the diagnostic is what makes it debuggable.

## Licence

Apache-2.0.
