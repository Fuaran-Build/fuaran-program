# Fuaran.Program — API stability

**Status:** pre-1.0. The version is single-sourced from `<Version>` in `Directory.Build.props`, and
every package (`Fuaran.Program`, `.Bounded`, `.Runtime`, `.Server`, and since 0.6.0 the UI adapter
packages `.UI` and `.Server.UI`) shares it: a minor bump on an untouched package costs nothing, and a
per-package line would have to be right six times. This file
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

## 0.7.0 — DRAFT (untagged, unreleased) — what the second witness found (Phase 1967)

**Class: breaking**, for `Fuaran.Program.Bounded` and `Fuaran.Program.Server`, and for every
consumer that constructs a witness. `v0.6.0` is tagged, so this cannot ride it.

A second domain instantiated the generic tier: a handler that is a store-mutating VERB — read a
store, write and delete files, publish to a named target — run under the unmodified 0.6.0 fold with
its effects as the `'Op` of `ApplyOps`. It ran correctly and found four things it had to build beside
Program rather than get from it. `DECISIONS.md` D19 records what was decided about each; this entry
records what a consumer pays.

### What broke

- **The action view has a fifth shape.** `ActionView<'Action, 'Expr>` gains `Require of condition:
  'Expr`, the HALTING guard: the condition resolves against the store, the boolean `true` holds and
  changes nothing, and anything else halts the enclosing sequence with a refusal diagnostic. Every
  exhaustive `match` over the view in a consumer stops compiling until it names the shape; a witness's
  `View` need not produce it (the UI witness does not). A leaf's `Refuse` is unchanged and still does
  not halt.
- **`BoundedOutcome` gains `Halted: bool`.** Every full-literal construction of the outcome (FS0764)
  needs the member; `false` on every outcome the UI tier produces. `HandlerAnswer` is unchanged — an
  answered call never halts.
- **`OpWitness` gains `Reach: 'Op -> OpReach`** — the op's named arguments and its destination
  class — so every full-literal witness construction needs the member (FS0764). `OpReach.nothing` is
  the honest answer for an op that names nothing a policy can bound. The UI adapter's witness fills it
  with the nodes a tree op addresses.
- **The demanded document moves to version 5.** The server tier carries a `reach` member — present
  on every tier, `[]` where the reachable ops name nothing — and the reader reads version 5 ONLY, on
  the argument every earlier bump made. **Every signed envelope under version 4 reports `Unreadable`
  drift under this reader**, naming the version: verification is by recomputation, so a signature is
  bound to the document version as well as to the tree. Re-sign. (This repository's own K7 pin was
  re-signed; its tree-hash half is unchanged, byte for byte.)
- **`ServerArgumentPolicy.arguments` / `payloadBytes` / `check` take the op witness** as their first
  argument, because an op sequence now HAS arguments (its ops' reach) and a size (its ops' canonical
  bytes). An `AllowList` or a `Ceiling` declared on `ApplyOps` or `EmitPatch` binds, where before it
  passed vacuously. `DestinationArgument` (`"destination"`) is a reserved argument name: an op's
  destination class is allow-listed under it.
- **`Handler.run` keeps its signature; `Handler.runWith` sits beside it** and takes how the placement
  performs an op (`OpPerformance<'Op>`: `InMemory`, or `Performed of ('Op -> Result<unit, string>)`).
  `ServerServices` gains an `OpPerformance` member (`create` leaves it in memory) and the session's
  arm calls `runWith` with it. Under a registered op performer `ApplyOps` is a STAGED arm: its ops are
  applied in memory while planning and performed after the plan commits, one staged call per op in
  plan order beside the host calls; `Performed` names `ApplyOps` once per op PERFORMED, and a
  part-way failure is a `PerformFailed` under that capability with the prefix that ran reported. The
  durable interpreter runs in memory and does not journal a performed op; that is stated, not
  covered.
- **`ServerDemand` gains `Reach: OpReachDemand list`** (FS0764 on full-literal tiers), and
  `ServerDemanded.ofEffect` reads the witness.

### What did NOT change

- **The program wire.** No schema, no fixture byte and no rule of the program wire specification
  moved; the codec families and the `driver-semantics` scenarios pass byte for byte. The guard is a
  shape of the action algebra, which the specification references and does not spell. The one
  sentence of its §6.2 that a registered op performer reads past — "only `HostCall` is staged" —
  describes the in-memory placement every conformant host had and every UI host still has; carrying
  the performer case into the normative text is a specification act, deliberately not taken here
  (`docs/generic-tier.md` §6).
- **What a UI-tree program does.** Nothing at the UI witness halts (`ui_never_halts`, proved) and the
  UI adapter's ops are performed in memory, so every existing test asserts what it asserted. The UI
  demanded document moved for exactly one class of handler — one whose ops address a node — and
  moved deliberately: the reach rides the new member, at the new version.
- **The proofs.** `BoundedFold.fst` was restated over five shapes and re-proved BEFORE the port
  (D14): the four theorems keep their names and statements, the sequence homomorphism gains a halting
  clause that `fold_no_require_no_halt` makes vacuous for every view without a guard, and the UI
  corollaries are unconditional as before. `Staging.fst` models the op performer and re-proves every
  theorem over the same staged list.

### What a consumer does about it

- A consumer of UI trees takes the adapter as before; its signed envelopes are re-signed.
- A consumer with its own domain adds `Reach` to its op witness — `OpReach.nothing` is legal — names
  `ActionView.Require` in every match over the view (producing it is optional), and, if its ops reach
  the world, registers an `OpPerformance.Performed` and calls `Handler.runWith`. Its guards are
  expressions resolved against the state channel (K4); what a guard needs to read lives there.

### Rides the draft: a deny-list clause on the server argument policy (Phase 1975)

The third witness (a document pipeline under a server placement) found that a domain's LOCKED set had
no spelling in the argument policy except an allow-list over the names that existed before the run —
stale the moment a handler creates a block and then edits it. `docs/generic-tier.md` §3.5 records the
finding; this records what it costs.

- **`ServerConstraintClause` gains `DenyList of argument: string * refused: string list`** in
  `Fuaran.Program.Bounded`. An effect carrying a value under `argument` that the list names is refused
  as `ServerConstraintDefect.OffList argument` — the existing token, so `ServerArgumentPolicy.describe`
  and every refusal a host already reads are unchanged. Beside an `AllowList` on the same argument a
  name on both is refused, in either declaration order; clauses of different kinds are still checked
  in declaration order.
- **What a consumer pays.** Constructing and declaring clauses is additive: no existing clause,
  verdict or document byte moves. A consumer with an EXHAUSTIVE `match` over `ServerConstraintClause`
  stops compiling until it names the new case — a closed union gaining a case is a breaking change of
  its own, which is why this entry says so rather than calling the change additive. It rides this
  slot because the slot is already breaking and untagged: a change of no higher class than the draft
  carries does not advance it.
- **The demanded document carries it at version 5, without a move.** The clause encodes as
  `{"clause":"denyList","argument":…,"refused":[…]}` beside the others, with `refused` a sorted set. A
  move exists so a reader cannot take "predates the member" for "walked and empty"; no version-5
  producer before this clause could declare a deny-list, so a document's silence about one is true
  under every producer that wrote version 5, and a reader built before it refuses a document carrying
  one at the clause rather than misreading it. Envelopes signed under the draft need no re-sign.

### Rides the draft: the contract's three-way cut (Phase 1974)

**Class: breaking**, for every consumer that constructs a witness, registers an op performer, or
names the witness types — the same class this slot already carries, so it rides rather than
advancing. `v0.6.0` is the newest tag and nothing public pins `0.7.0`; a draft slot moves only for a
change of a HIGHER class than it carries. (The phase that filed this change expected `0.8.0`; the
draft-slot rule above is what decides, and it says ride.) The third witness (`docs/generic-tier.md`
§3.11) measured the contract and found its centre is a state with an op algebra over it, not the
tree; `DECISIONS.md` D20 records the cut.

- **`ProgramWitness` is `{ State; Walk; Dispatch }`, typed `ProgramWitness<'Node, 'Op, 'Walk,
  'Dispatch>`.** `StateWitness<'Node, 'Op>` (required — `Stream`, `Reach`, `AbsoluteTarget`,
  `Canonical`, `Diff`, `View`) replaces `OpWitness`; `WalkWitness<'Node>` (`Nodes`, `Traverse`,
  `Cost`, `QueryReaders`) and `DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>` (`Handlers`,
  `Events`, `Resolve`, `Action`, `Expr`, `Store`, `Effect`) replace `TreeWitness` and the top-level
  sub-records. An unfilled position holds `Unfilled`. `FullWitness<…>` names the all-three shape.
  `TreeWitness` and `OpWitness` are gone.
- **Every core signature names the axes it reads.** The walk readers (`Budget.treeCost`,
  `QuerySchema.readersOfTree`) take a composition with a `WalkWitness`; the tree readers that also
  read handlers (`Demanded.ofTree` / `check`, `Resolve.resolveTree`, `ServerDemanded.ofTreeAndHandlers`
  and the signing/verifying wrappers, `Replay.ofTreeAndHandlers`, `Harvest.ofProgram`,
  `ServerServices.Witness`) take a `FullWitness`; the action and client-effect codecs, `HandlerWire`,
  `Replay.admit` / `postureOf` / `withPostures` and `Harvest.ofRegistration` take a composition with a
  `DispatchWitness`; `BoundedActions.run` / `runInert`, `Budget.actionCascadeCost`,
  `Demanded.ofAction`, `Handler.run` / `runWith`, `Durable.run` and `ServerDemanded.ofHandler` /
  `ofHandlers` run under every composition, reading the dispatch position through the new
  `IDispatchPosition` (`DispatchPosition.fold`). `SignedEnvelope.sign` / `verify` / `treeHash` take
  the `StateWitness`; `ServerArgumentPolicy.*` takes the `StateWitness`.
- **The op-channel guard.** `OpView` (`Edit | Require`) and `StateWitness.View`; a guard is resolved
  against the planned state, never staged or performed, and its refusal is the `ApplyOps` arm's
  `Failed` with the domain's reason verbatim. `OpView.edits` fills `View` for a domain with none.
- **The performer is handed the state.** `OpPerformance<'Node, 'Op>`; `Performed of ('Node -> 'Op ->
  Result<unit, string>)`; `performedBy` takes the same. The state is the planned state with the op
  applied. `Performed` and the `PerformFailed` prefix report are unchanged.
- **A dispatch-less composition** runs handlers of `Handler<Nothing, 'Op>` over `ServerStore<'Node,
  unit>`; a landing slot there is refused while planning as `Handler.NoBindingChannel`
  (`"no-binding-channel"`), the one new halt reason.
- **The UI adapter** exposes `UiWitness.state`, `.walk` and `.dispatch` in place of `.ops` and
  `.tree`; `UiProgramWitness` is a `FullWitness`.

**What did NOT change:** no wire member, no fixture byte, no demanded-document byte for any
composition that fills the dispatch axis, no proof statement (the staging model gained three
theorems and restated its arm; every earlier theorem keeps its statement), and the UI tier's
behaviour, byte for byte through the parity suite and the Fable leg. No envelope needs re-signing.

**What a consumer does about it:** `docs/migrations/phase-1974.md` — one page, a diff per file.

The slot stays a DRAFT: tagging `v0.7.0` is the release gesture, a separate recorded act.

### Rides the draft: selection and bounded iteration (Phase 1976)

**Class: breaking**, for every consumer that matches exhaustively on `ActionView` or `OpView`,
constructs a `StoreWitness`, or names `OpView` in a signature — the class this slot already carries,
so it rides rather than advancing. `v0.6.0` is the newest tag and nothing public pins `0.7.0`. The
second witness's re-run found the core had sequence and abort but neither the selection nor the
bounded iteration D1 and D2 charter, and `DECISIONS.md` D21 records what was decided; this entry
records what a consumer pays.

- **`ActionView` has two more shapes:** `Choose of entry: 'Expr * whenTrue: 'Action * whenFalse:
  'Action * exit: 'Expr option` and `Repeat of bound: Bound<'Expr> * body: 'Action`, with
  `Bound<'Expr> = Literal of count: int | Parameter of count: 'Expr * lo: int * hi: int`. Every
  exhaustive match over the view gains two arms (the fold, the budget, the demanded projection and
  the replay classification in this repository; any consumer's own walk).
- **`OpView` is `OpView<'Op>`** and has two more shapes: `Choose of entry: 'Op * whenTrue: 'Op list
  * whenFalse: 'Op list * exit: 'Op option` and `Repeat of count: int * body: 'Op list`.
  `StateWitness.View` is `'Op -> OpView<'Op>`; `OpView.edits` still fills it for a domain with none,
  and `OpView.beneath` enumerates the ops under a flow op.
- **`StoreWitness` gains `Read: string -> 'Store -> JVal option`** — the one member a reversible run
  uses, and the only reader of it. `Map.tryFind` for a map-shaped store; the UI adapter reads its
  `State` key back through the binding resolver.
- **The fold's new halt reasons**, each a `BoundedDiagnostic.Refused` carrying the branch's or the
  repeat's own description: `the branch condition did not resolve to a value`, an errored condition's
  own text, `the exit assertion did not hold after the true arm`, `the exit assertion held after the
  false arm`, `the exit assertion did not resolve to a value`, `the exit assertion errored: …`,
  `the repeat's bound is outside its declared range`, `… did not resolve to a count`, `… did not
  resolve to a value`, `the repeat's bound is negative`. On the op channel the `ApplyOps` arm's
  `Failed` carries `the exit assertion did not hold after the true arm: <the assertion's refusal>`,
  `the exit assertion held after the false arm`, and `the repeat's count is negative`.
- **The budget prices the shapes:** a branch at `1 + max(arms)`, a repeat at `1 + bound × body` (a
  parameter bound at the top of its range), in the saturating arithmetic the module already used.
  `Budget.satAdd` / `satMul` are unchanged in meaning and now sit above `actionCascadeCost`.
- **The argument policy reads beneath a flow op:** `ServerArgumentPolicy.reachOfOp` returns the
  reach of the op AND every op beneath it, so a branch whose untaken arm reaches an off-list path is
  refused, and the demanded document's `reach` carries both arms.
- **The reversible fragment is new API on `BoundedActions`:** `runTraced` (the fold with its
  `Trace`), `reversible` (decided from the tree), `reverse` (the inverse of a run, a `Reversed`
  program) and `runReversed`; `Trace` and `Trace.restorable` sit in `Witness.fs`. `run` and
  `runInert` are unchanged in signature and behaviour.
- **The oracle differentials** map the two shapes (`tests/*/ProofOracleTests.fs`) and the toy
  corpus covers every arm and halt of both; a consumer that mirrors those hosts gains the same arms.

**What did NOT change:** no wire member, no fixture byte, no demanded-document byte for any program
that uses neither shape (the document stays at version 5), no envelope needs re-signing, and the UI
tier's behaviour, byte for byte through the parity suite and the Fable leg — it views nothing as
either shape, proved (`ui_view_no_flow`) and tested over the arm-complete corpus. Every earlier proof
statement keeps its form except Phase 1967's "only a guard halts", which is kept as a corollary for
a view without the new shapes and generalised as `fold_no_halting_shape_no_halt`.

**What a consumer does about it:** `docs/migrations/phase-1976.md` — one page, a diff per file.

## 0.6.0 — RELEASED (tagged `v0.6.0`, 2026-09-28) — the core becomes domain-generic (Phase 1896)

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
  `.OpStream` — the evaluator at the Core-Compute release 0.34.0, the rest at the 0.32.0 it
  declares. That is the substrate the UI tier this repository now pins (0.86.0) was built on, and
  it is a raise from the 0.21.0 the packages received through the UI tier before: `RunQuery` runs
  the 0.34.0 evaluator, which is linear where 0.21.0 was badly super-linear
  (`docs/runquery-benchmark.md`). A consumer that pins `Fuaran.Core.*` itself meets the substrate's
  own changes at these versions — `Transform.Limit` / `Sort` take slots, `ColExpr` gained `Now`,
  `EvalError` gained `UnpinnedClock` (`docs/dataframe-0.34-surface-diff.md`).
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
- **What a UI-tree program does**, with ONE exception below that comes from the UI tier, not from
  this change. Every other existing test runs, unchanged in what it asserts, through the UI
  instantiation. The signed effect envelope's tree hash moved from the UI tier's SHA-256 to Core's;
  an envelope signed before the move is pinned as a test and still verifies (K7).

### A behaviour a consumer of UI trees inherits from the UI tier raise

The UI tier (0.86.0) resolves a state binding with NO declared default, at a slot nothing has
written, as UNRESOLVED; until then it resolved to the empty value. The adapter follows the tier. On
the bounded path that shows in two places a tree can observe: a `WriteToClipboard` whose payload is
such a binding is now REFUSED (nothing copied, one `Refused` diagnostic) where it copied `""`, and a
`SetState` taking its `valueFrom` from such a binding now performs no write and is diagnosed
("valueFrom did not resolve to a value") where it wrote the empty value. Both are pinned by tests.
**What to do:** give the binding a default — `Binding.State(key, Some default)`
resolves to the default at an unwritten slot, and is copied.

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
`0.6.0` core is released without its adapter.** `0.6.0` is ONE version across all six packages —
`Fuaran.Program`, `.Bounded`, `.Runtime`, `.Server` and the two adapter packages
`Fuaran.Program.UI` and `Fuaran.Program.Server.UI` — single-sourced from `<Version>` like the rest,
packed by the same `-Pack` leg and published by the same tag.

**Class: additive**, for the two adapter packages (Phase 1897). Phase 1896 wrote their sources in
two non-packable projects under `tests/`; Phase 1897 moved them, file for file, to
`src/Fuaran.Program.UI/` and `src/Fuaran.Program.Server.UI/` and made them packable. Their public
surface is exactly what the existing suite, the scenario corpus and the sample already ran through,
so the move changes no behaviour; it rides this draft slot rather than advancing it. The core
boundary test covers the new neighbours: no core package may reference an adapter package, declared
or resolved, any more than it may reference the UI tier itself.

The slot was released as `v0.6.0` on 2026-09-28, all six packages at one version.

**Class: additive**, for `Fuaran.Program.Server` (Phase 1759). `ServerEffect.fs` gains a
`ReturnContract` record (`Name`, `Holds`) with `ReturnContract.describe` / `check`, and
`ServerEffectRegistry.registerChecked fn contract performer registry`, which registers
`ReturnContract.check contract performer` under `fn`. `ServerEffectRegistry` itself is unchanged —
no field, so every full-literal construction still compiles — and `register` is unchanged, so a
consumer that declares no contract sees no difference. It rides this draft slot. The behaviour it
adds is proved (`proofs/EffectGate.fst`, `return_contract`): a result the contract rejects reaches
the handler as `PerformFailed` naming the contract, never as a value.

