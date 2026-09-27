# Fuaran.Program — API stability

**Status:** pre-1.0. The version is single-sourced from `<Version>` in `Directory.Build.props`, and
all four packages (`Fuaran.Program`, `.Bounded`, `.Runtime`, `.Server`) share it: a minor bump on an
untouched package costs nothing, and a per-package line would have to be right four times. This file
records, per version slot, what a consumer pays to adopt it and why.

## Versioning policy

- A change to a package's public contract — or to the contract it was BUILT against — ships on a
  version AHEAD of every tagged or publicly pinned one. A tagged slot is some consumer's contract
  and is never re-packed.
- An untagged, publicly unpinned `<Version>` is a **draft** slot: an additive change rides it; a
  change of a higher class advances it.
- A draft is not a release. Tagging `v<version>` is the release gesture — the publish workflow fires
  on the tag — and it is a separate, recorded act, never a side effect of a change landing.

This document starts at `0.6.0`. The slots before it are recorded where they were cut, in the
comments beside `<Version>` in `Directory.Build.props`, and are not restated here.

## 0.6.0 — DRAFT (untagged, unreleased) — the core becomes domain-generic (Phase 1896)

**Class: breaking**, for `Fuaran.Program.Bounded`, `Fuaran.Program.Runtime` and
`Fuaran.Program.Server`. `v0.5.0` is tagged, so this cannot ride it.

### What broke

- **The public types gain type parameters.** The three core packages are rebuilt over the witness
  contract of `DECISIONS.md` D18 (`docs/generic-tier.md` §3): one record, `ProgramWitness<'Node,
  'Action, 'Expr, 'Store, 'Op, 'Effect>`, supplies what the algebra asks of a domain, and every type
  that used to name a UI-tier type names a type parameter instead — `BoundedOutcome<'Store,
  'Effect>`, `HandlerArm<'Store, 'Effect, 'Placement>`, `ServerEffect<'Op>`, `Handler<'Action,
  'Op>`, `ServerStore<'Node, 'Store>`, `ServerSession<…>`, `ServerStepOutput<…>`,
  `EffectRegistry<'Effect>` and the rest. The functions over them take the witness (or the part of
  it they read) as their first argument: `BoundedActions.run witness arm nodeId action store
  placement`.
- **The UI tier leaves the dependency graph.** None of the three packages references a
  `Fuaran.UI.*` package any more, declared or transitive, and a test fails if one returns. They name
  the substrate they use directly instead: `Fuaran.Core.Wire`, `.Column`, `.DataFrame`, `.Tree` and
  `.OpStream`, pinned at 0.21.0 — the substrate the UI tier this repository pins was built on
  (`docs/dataframe-0.34-surface-diff.md` says why not the current evaluator).
- **The UI-typed transport leaves the core.** The bounded driver, its channel glue and the client
  runtime (`BoundedDriver`, `BoundedConnection`, `Program`), the client-effect destination map, the
  UI's URL floor in the egress classification, and the server session's and durable interpreter's
  EVENT step are UI transport rather than algebra (K8). The core session now starts at
  `ServerSession.dispatchWith` — an action already chosen — and the durable steps take the
  transport's step as a function (`Durable.stepVia`, `DurableControls.stepVia`).
- **The replay classification reads the view** (`ProgramWire.replayDefectsOfAction witness action`)
  instead of an encoded action's wire tags; for every action an encoder can produce, the verdicts
  are unchanged.
- `EffectDestination` is now declared in the core's contract file (same namespace,
  `Fuaran.Program.Runtime`); `EgressPolicy.classify` became `EgressPolicy.classifyWith floor`.

### What did NOT change

- **The program wire.** No schema, no fixture byte and no rule of the program wire specification
  moved: the conformance corpus's codec families and driver-semantics scenarios pass byte for byte,
  run through the generic core and the UI adapter.
- **What a UI-tree program does.** Every existing test runs, unchanged in what it asserts, through
  the UI instantiation. The signed effect envelope's tree hash moved from the UI tier's SHA-256 to
  Core's; an envelope signed before the move is pinned as a test and still verifies (K7).

### What a consumer does about it

- **A consumer of UI trees** takes the UI adapter — `Fuaran.Program.UI` (Fable-clean) and
  `Fuaran.Program.Server.UI` (.NET) — and adds `open Fuaran.Program.UI` (and
  `open Fuaran.Program.Server.UI`) AFTER its `open Fuaran.Program.*` lines. The adapter keeps the
  pre-0.6.0 names as closed aliases and partially applied modules (`BoundedStore`,
  `BoundedActions.runBoundedAction`, `ServerSession.step`, …), so most call sites do not change.
- **A consumer with its own domain** builds a `ProgramWitness` for it and calls the core directly.
  What each member is assumed to be, and which proved law leans on it, is `proofs/README.md`'s
  "Every parameter of the model is an assumption".

### The core and its adapter release together

The UI adapter is new at `0.6.0` and ships in the same version as the core it instantiates: **no
`0.6.0` core is released without its adapter.** At this slot's head the adapter's sources are parked,
non-packable, under `tests/Fuaran.Program.UI.Parked/` and `tests/Fuaran.Program.Server.UI.Parked/`;
Phase 1897 promotes them to the two packages. Until then this slot stays a draft.
