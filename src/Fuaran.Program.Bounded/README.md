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

**The document has two tiers, because a session has two placements.** A tree is only half of what a
program can ask for: at the server placement its call action reaches a host-registered handler whose
stage list names effects, host functions and channels of its own, and no walk over a tree can see
them. So the projection carries an optional **server tier** — its shapes live here beside the client
tier's, since a wire document with half its vocabulary in another package is not one, while the walk
that fills the tier lives with the placement whose vocabulary it reads and reaches this package only
through `Demanded.ofAction` / `union` / `withServer`.

**The document reads back: `Demanded.decode` is the pinned target for anyone consuming one.** A
consumer that derives the envelope for itself is re-deriving a shape this package is the only
authority on, and a reader that gets it subtly wrong produces a projection that looks like a
projection — so the coverage verdict computed from it is wrong in a way nothing downstream can see.
`decode` is total over its input, refuses a version it does not read rather than reading it through
another version's lens, never reads a document partially, and carries every discriminator through
unchanged rather than dropping one it does not recognise. Both directions of the round trip are
pinned by tests.

Two consequences worth knowing. `None` and an EMPTY tier are different facts — `None` says no server
walk was performed, an empty tier says one ran and found nothing — so a consumer cannot read "not
asked" as "asked, and the answer was nothing". And a host that declared no server coverage is never
told it failed to serve one: the tier is checked only where both a demand and a declaration exist,
because most hosts have no server placement at all.

## Asking before running, the other half: the query-schema walk

A handler declares a query as a source plus an ordered `Transform` pipeline, and lands the result in a
named slot that some node reads. `Schema.ofTransform` derives that pipeline's **output schema from its
input schema without evaluating anything** — the same move the demanded projection makes, one layer
down, and possible for the same reason: the verb set is a closed DU, the expression algebra is a closed
DU, and neither carries code.

`QuerySchema` turns that into a verdict. It harvests each reading node's declared column names — a
chart's axes, a grid column's field — and reports the columns a query provably fails to produce, the
steps that read a column their input provably lacks, and the unions whose sides provably disagree. A
placement wires it into an opt-in strict construction (the server placement's `initStrict`), so an
unsatisfiable handler is refused **before** the untrusted tree runs.

**It refuses only what it can prove, and is explicit about the rest.** `SchemaKnowledge` distinguishes a
closed column set — the only one from which "that column is absent" follows — from one that is merely
known to *contain* certain columns. A derived column's type comes from the data, a pivot's value columns
are *named* by the data, and a named source's schema is whatever the host declared. Each of those is
reported as data on the report, never as a finding: a check that fired on ordinary correct trees is one
people learn to scroll past. Which side of that line the expectation and the source schemas fall, and
why, is [`DECISIONS.md`](../../DECISIONS.md) **D10**.

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
