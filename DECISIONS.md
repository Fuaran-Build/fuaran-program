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
