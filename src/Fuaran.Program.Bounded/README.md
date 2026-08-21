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

## Asking before running: the demanded-effect projection

`Demanded.ofTree` answers, for any program tree, **what that program can ever ask for** — the client
effects it can cause, the host calls it names, the state namespaces it touches — by a total static
walk over the closed action vocabulary. It needs no type inference and reaches no fixpoint: the
vocabulary is closed and the tree carries no code, so the walk enumerates rather than analyses. The
result is a self-describing document (`Demanded.encode`), not a view onto a tree, so it can be
emitted, stored and checked somewhere the tree never travels.

`Demanded.check coverage tree` turns that into a verdict against one host, naming every demand the
host cannot cover — before any event runs. It separates the two facts a host effect registry already
separates: an **unregistered** effect is absent from the host and no policy makes it reachable, while
a **gate-refused** one is present and declined. Only the second is a policy change.

**The default posture is unchanged, and strict mode is an opt-in on top.** `BoundedDriver.init` and
the client placement's `Program.mkBounded` still build a session for any tree, and an uncoverable
effect is refused at the moment it is dispatched, with the refusal recorded. That is deliberate: a
program naming one effect a host declines is still a program that works for everything else, and
refusing it wholesale would be stricter than the interpreter's own behaviour. A host that would
rather not start at all reaches for `BoundedDriver.initStrict` / `Program.mkBoundedStrict`, which
check first and return every finding rather than the first.

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
