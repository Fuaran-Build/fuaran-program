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

## 0.7.1 — RELEASED (tagged `v0.7.1`, 2026-10-03) — what the second witness found (Phase 1967)

**Released as 0.7.1, not 0.7.0.** `0.7.0` was this slot's draft number, and drafts of it were packed and resolved while its public surface was still moving (the Phase 1967 work, then the raise onto the newest released lines). A released version names one contract, so the release takes the next patch; `0.7.0` was never tagged and never published, and no consumer should pin it. Everything below describes `0.7.1`.

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

### Rides the draft: the undo posture (Phase 1977)

**Class: breaking**, for every consumer that constructs a `StateWitness` as a full literal, and
for every consumer that reads the demanded document or verifies a signed envelope — the class this
slot already carries, so it rides rather than advancing. `v0.6.0` is the newest tag and nothing
public pins `0.7.0`. `DECISIONS.md` D22 records what was decided; this entry records what a consumer
pays.

- **`StateWitness` gains `Undo: 'Op -> UndoClass<'Node, 'Op>`**, with `UndoClass = Inverse of
  ('Node -> 'Op list) | Compensate of ('Node -> 'Op list) | OneWay of reason: string`. A
  full-literal construction gains one line (`FS0764` names it). What to answer: an exact inverse
  computed from the pre-state for an edit that has one, a declared compensation for one that can
  be undone in effect only, and `OneWay` with the domain's reason for a push. The UI adapter
  answers every tree op an exact inverse through `TreeOpDiff.diff`; the obligation an `Inverse`
  carries is K9 (`docs/generic-tier.md` §3.7), and the undo run refuses a witness that breaks it.
- **The demanded document moves to version 6:** the server tier carries `undo`, one posture per
  reachable handler (`{"handler":…,"undo":"reversible"|"compensable"|"one-way"|"unknown",
  "reasons":[{"stage":n,"defect":…}]}`), present and empty where no handler contributed.
  `Demanded.decodableVersions` is `[6]`: a version-5 document is refused naming the version, never
  read through this lens. Every envelope signed under version 5 reports `Unreadable` drift naming
  the version; a re-sign is the remedy, and this repository's K7 pin was re-signed over the same
  tree.
- **`ServerDemand` gains `Undo: UndoPosture list`**, so a full-literal construction of a server
  tier gains one line; `UndoPosture` and `UndoReasonDemand` are new in `Demanded.fs`.
- **New API on the server placement:** `Handler.runPlanned` (the outcome beside the `UndoPlan` a
  run leaves — `UndoStep.Edit | Compute | Reached | Emitted`), `Undo.posture` / `reasons` /
  `postureOf` / `withPostures` / `ofTreeAndHandlers`, and `Undo.run` with its `UndoRefusal`
  (`undo-one-way-step`, `undo-undecidable-step`, `undo-inverse-drift`, `undo-uncommitted-plan`).
  `Harvest.ofProgram` and `ofRegistration` join the undo posture beside the replay posture.
- **`Handler.runWith` and `run` are unchanged in signature**; the compute stage now runs the fold
  traced, which `traced_agrees` and the parity suite say answers the same outcome.
- **The oracle project gains `Undo.fs`** (generated from `proofs/Undo.fst`) and the proof leg a
  fifth module; `Staging.fs` is re-extracted for the trail.

**What did NOT change:** no wire member of the program wire specification, no fixture byte, and
the UI tier's behaviour, byte for byte through the parity suite and the Fable leg. A demanded
document for a program with no server tier changes only its version number; one with a server tier
gains `"undo":[]` where no handler contributed. Every earlier proof statement keeps its form.

**What a consumer does about it:** `docs/migrations/phase-1977.md` — one page, a diff per file.

### Rides the draft: a performed op is journaled like a host call (Phase 1980)

**Class: breaking**, for a consumer that constructs a `JournalEntry` or a `PerformerFacets` as a
full literal (a host's persistent effect journal; nothing in this repository outside the two files
that define them), and for every caller of the facet derivation — the class this slot already
carries, so it rides rather than advancing. `v0.6.0` is the newest tag and nothing public pins
`0.7.0`. `DECISIONS.md` D23 records what was decided and why the specification does not move; this
entry records what a consumer pays.

- **`JournalEntry` gains `Subject: string option`** — `None` for a host call; for a performed op
  stage the content address (`sha256:` + digest) of the op's canonical form, off the state axis's
  `Stream.Encode`. A host whose journal persists entries stores and returns the member; `Journal.none`
  and `Journal.inMemory` already do. `Journal.subjectOf` reads it beside `capabilityOf`, and
  `Journal.describe` renders it between the capability and the phase tag for an op stage only, so
  every host-call line a reader pinned reads as it did.
- **`PerformerFacets` gains `OpPerformer: IdempotencyFacet option`**, with
  `PerformerFacets.declareOpPerformer` / `isOpPerformerDeclared` / `opPerformerFacet` and
  `DurableServices.declaringOpPerformer`. Undeclared reads `NonIdempotent`, as an undeclared host
  function does.
- **`Durable.runWith` sits beside `Durable.run`** and takes the placement's `OpPerformance` after its
  registry; `run` is `runWith` in memory, unchanged in signature and behaviour. Under a registered op
  performer every op stage is journaled under `ApplyOps` at its ordinal in the ONE sequence the host
  calls share — attempted before the performer, decided after — served on replay, refused undecided
  by default, re-invoked only under `acceptingIndeterminateReplay` (an `Overrides` record under
  `ApplyOps`) or a performer declared `Idempotent`, and refused as `durable-replay-divergence` where
  a recorded ordinal holds another op. `Durable.arm` passes the host's `ServerServices.OpPerformance`,
  so a session's durable step covers its ops with no change at the call site. `DurableControls.runWith`
  is the same beside `DurableControls.run`. `Durable.opSubject` and `Durable.OpStageCapability` are
  new and public, so a host can compute the subject it will find in its journal.
- **The facet derivation takes the performance:** `Facets.ofEffect` / `ofHandler` / `ofHandlers` /
  `declare` / `checkDeclaration` and `Durable.guarantees` / `declaration` / `checkDeclaration` gain
  an `OpPerformance<'Node, 'Op>` argument before the handler(s). Pass `OpPerformance.InMemory` to
  keep every answer a caller had; pass the registered performance and `ApplyOps` is derived on a host
  call's terms — strict may lose, accepting may duplicate, exactly-once only where the op performer
  is declared idempotent — with `checkDeclaration` reporting an undeclared op performer as
  `facet-undeclared-performer` under `ApplyOps`. `Facets.undeclaredOpPerformer` is new.

**What did NOT change:** no wire member, no fixture byte, no demanded-document byte, no envelope
needs re-signing, and the UI tier byte for byte through the parity suite and the Fable leg — in memory
nothing is performed and nothing new is journaled, which `DurableInterpreterTests` pins against the
journal's own rendering. The program wire specification is unchanged (D23 item 5: the journal is a
host port, not a wire artefact; §7.1 is checked and holds). Every earlier proof statement keeps its
form; `Staging.fst` gains the durable discipline — eight theorems over the same staged list — and its
extraction is re-emitted for them.

**What a consumer does about it:** a host with a persistent journal adds `Subject` to its entry
construction (`None` where it has only ever journaled host calls); a domain whose ops reach the world
calls `Durable.runWith` with the performance it already hands `Handler.runWith`, declares its op
performer's idempotency, and passes the same performance to the facet derivation; a UI-tier consumer
changes nothing.

### Rides the draft: an op performer's claim is checked (Phase 1981)

**Class: breaking**, for every consumer that registers an op performer — the payload of
`OpPerformance.Performed` changes type — which is the class this slot already carries, so it rides
rather than advancing. `v0.6.0` is the newest tag and nothing public pins `0.7.0`. `DECISIONS.md` D24
records what was decided and why the specification does not move; this entry records what a consumer
pays.

- **`OpPerformance.Performed` carries `'Node -> 'Op -> Result<Fuaran.Core.JVal, string>`** — the
  performer answers a RECEIPT, what it says it did, instead of `unit`. `OpPerformance.performedBy`
  takes the receipt-answering shape. A performer with nothing to say registers through the new
  `OpPerformance.performedWithoutReceipt`, whose receipt is the inert empty object the staged call
  always answered — the one-line migration for every existing caller.
- **`OpContract<'Node, 'Op>` is new** — `{ Name; Holds: 'Node -> 'Op -> JVal -> bool }` — with
  `OpContract.describe` (`return-contract:<name>`, the vocabulary `ReturnContract.describe` has) and
  `OpContract.check`. **`OpPerformance.performedChecked contract perform`** registers a performer with
  its contract composed, on `ServerEffectRegistry.registerChecked`'s terms: a receipt the contract
  rejects is `PerformFailed("ApplyOps", "return-contract:<name>")`, the handler rolled back and the op
  absent from `Performed`.
- **The durable journal records the receipt** as an op stage's `Completed` value, and a
  contract-rejected receipt as `Refused` at its ordinal; a host reading its journal's values for op
  stages sees the receipt where it saw `{}`. `Durable.runWith`'s signature is unchanged.
- **`Handler.runWith` / `Handler.run` / `Durable.run` / the facet derivation** are unchanged in
  signature; the facets read `Performed _` and are indifferent to the payload.

**What did NOT change:** no wire member, no fixture byte, no demanded-document byte, no envelope
needs re-signing, and the UI tier byte for byte through the parity suite and the Fable leg —
`InMemory` is untouched. The program wire specification is unchanged (D24 item 6). Every earlier proof
statement keeps its form; `EffectGate.fst` gains `op_contract` / `check_op` and the theorems
`check_op_is_check_return`, `op_token_checked`, `op_return_contract` and `uncontracted_is_direct`, and
its extraction is re-emitted for them.

**What a consumer does about it:** a domain whose op performer answered `Ok ()` wraps it in
`OpPerformance.performedWithoutReceipt` and changes nothing else; a domain whose policy is enforced
against reach has its performer answer what it touched and registers through `performedChecked` with
a contract that reads the receipt against the op's reach (the in-repo verb witness's
`Receipt.withinReach` is the pattern); a UI-tier consumer changes nothing.

### Rides the draft: the outcome names its arms, a state-only handler document, and a numeric ceiling (Phase 1982)

**Class: breaking**, the class this slot already carries, so it rides rather than advancing: `v0.6.0`
is the newest tag and nothing public pins `0.7.0`. `DECISIONS.md` D25 records what was decided and
why neither the specification nor any theorem statement moves.

- **`HandlerOutcome` gains `Flow: FlowDecision list`**, and `HandlerTally` gains the same member, so
  every full-literal construction of either needs it (FS0764). `[]` is the honest value for a
  handler that uses no branch and no repeat. `FlowDecision` (`Chose of bool` | `Repeated of int`) and
  `FlowDecision.ofTrace` are new.
- **`ServerConstraintClause` gains `AtMost of argument: string * limit: int`**, so every exhaustive
  match over the clause stops compiling until it names the case. The demanded document carries it as
  `{"clause":"atMost","argument":…,"limit":…}` at version 6 (no version move).
- **`ServerArgumentPolicy.arguments` reads a host call's integer members** as decimal text, beside its
  string members. An allow-list or deny-list declared on such a name now binds where it passed
  vacuously before.
- **`HandlerWire`'s codecs, `Replay`'s functions and `Harvest.ofRegistration` take the dispatch
  position at any composition** (`#IDispatchPosition`) where they took `DispatchWitness`. Every
  existing caller compiles unchanged.
- **New public members:**
  - `HandlerWire.contentAddress`;
  - `SignedEnvelope.signAddressed` / `verifyAddressed`;
  - `ProgramWire.decodeActionIn` / `replayDefectsOfActionIn`.
- **`SignedEnvelope.sign` / `verify`** keep their signatures and their bytes. `verify` now
  evaluates the walk before the key checks rather than after, which a pure walk cannot observe.

**What did NOT change:**
- no wire member, no fixture byte, and no outcome-document byte (`HandlerReport` does not carry
  `Flow`);
- no demanded-document byte for any registry that declares no `AtMost`;
- no envelope needs re-signing, and no proof statement moves;
- the UI tier, byte for byte through the parity suite and the Fable leg.

**What a consumer does about it:**
- A full-literal `HandlerOutcome` or `HandlerTally` gains `Flow = []`.
- An exhaustive match over `ServerConstraintClause` gains the `AtMost` arm.
- A domain that fills only the state axis can now encode, address and sign its handlers. It gives
  its `Stream.Decode` the inverse of `Stream.Encode` if it had none.
- A UI-tier consumer changes nothing.

### Rides the draft: the op performer is revocable (Phase 1983)

**Class: additive** — new members, and one refusal path that a session reaches only by recording a
revoke of the reserved key. It rides the draft rather than advancing it: `v0.6.0` is the newest tag
and nothing public pins `0.7.0`. `DECISIONS.md` D26 records what was decided and why.

- **`OpPerformance.RegistrationKey`** (`"ApplyOps"`) is new: the reserved key an operator's revoke
  names the op performer by — `Controls.revoke actor reason OpPerformance.RegistrationKey`. No
  registration shape changes; `performedBy` / `performedChecked` / `performedWithoutReceipt` and the
  `OpPerformance` cases are as Phase 1981 left them.
- **`Controls.opPerformerRevocation`, `Controls.opStageRefusal` and `Controls.performance`** are new.
  `Controls.performance record state performance` is the direct interpreter's half beside
  `Controls.apply`: a host running `Handler.runWith` under its own controls passes its op performance
  through it, as it passes its registry through `apply`.
- **`DurableControls.runWith` / `arm` / `stepVia` refuse an op stage once the op performer is
  revoked** — at the stage's ordinal, before it is attempted, as
  `PerformFailed("ApplyOps", "control-performer-revoked")` with the prefix that ran reported, and a
  `ControlRefusal.Revoked("ApplyOps", actor, reason)` in `Refusals`. A refused stage writes no
  effect-journal entry; a resumed run serves its recorded prefix and refuses the first unrecorded
  op stage. Their signatures are unchanged, and so are `Durable.runWith` / `run` / `arm`.
- **A behaviour a host already recording revokes can observe:** a session whose stream ALREADY holds
  a revoke of `"ApplyOps"` (a host function of that name, before this phase) now also has its op
  stages refused. One revoke withdraws both arms; that is the over-broad direction, deliberately.
- **`Controls.describeRefusal` renders a revocation as "'<capability>' refused — its performer was
  withdrawn by …"** where it said "reads as unregistered": true of a host call, false of an op stage,
  so the one rendering now says what holds of both. A log-safe string, not a wire member.

**What did NOT change:** no wire member, no fixture byte, no demanded-document byte, the control
stream's encoding, `Controls.step` (no arm removes a key from `Revoked`; monotonicity unchanged), no
proof statement, and the UI tier byte for byte — in memory there is no op performer to withdraw. A
session that records no revoke of the key runs byte-identically, in memory and durably, journal
entry for entry.

**What a consumer does about it:** nothing, unless it means to withdraw ops. An operator does so with
`Controls.revoke actor reason OpPerformance.RegistrationKey`. A host that drives the direct
interpreter under its own controls adds `Controls.performance` beside its `Controls.apply`.

### Rides the draft: coverage reads the op performer (Phase 1986)

**Class: breaking, for one member** — `Controls.coverage` gains a parameter. It rides the draft
rather than advancing it: the draft is already of the breaking class (see the head of this entry),
`v0.6.0` is the newest tag and nothing public pins `0.7.0`. `DECISIONS.md` D28 records what was
decided and why, including why the parameter was preferred to a second function.

- **`Controls.coverage state registry performance`** — the placement's `OpPerformance` is the new
  third argument. A call site that passed two arguments stops compiling until it names how its
  placement performs ops; `OpPerformance.InMemory` reproduces the old answer exactly.
- **`DurableControls.coverage controls host` keeps its signature** and reads `host.OpPerformance`
  from the host record it already took.
- **A behaviour a host can observe:** under `OpPerformance.Performed`, once the op performer is
  revoked (`Controls.revoke actor reason OpPerformance.RegistrationKey`), coverage's gate refuses
  `ApplyOps`, so a demanded-effect check over a handler that carries an op reports
  `CoverageFinding.ServerGateRefusesCapability "ApplyOps"` where it reported nothing. That is the
  revoke's effect being reported, not a new effect: the op stage was already refused at run time
  (Phase 1983). Under `OpPerformance.InMemory` nothing changes — the apply is the effect and there
  is nothing to withdraw.

**What did NOT change:** `ServerCoverage`, `CoverageFinding` and `Demanded.checkProjection` (no
member, no arm), no wire member, no fixture byte, no demanded-document byte, the control stream,
`Controls.step` and its monotonicity, no proof statement. With the op performer not revoked, coverage
is the registry's coverage under the controls exactly as before, whatever else is in force.

**What a consumer does about it:** a direct caller of `Controls.coverage` passes its placement's
op performance as the third argument (`OpPerformance.InMemory` if it performs none). A consumer that
branches on coverage findings should expect `ApplyOps` in a gate finding when an operator has
withdrawn the op performer; D28 says why it is a gate finding rather than an absence.

### Rides the draft: `Each` over a literal collection (Phase 1990)

**Class: breaking**, for every consumer that matches exhaustively on `ActionView`, `OpView`, `Trace`
or `FlowDecision`, constructs an `ActionWitness` or a `StateWitness`, or calls `OpView.beneath` — the
class this slot already carries, so it rides rather than advancing. `v0.6.0` is the newest tag and
nothing public pins `0.7.0`. A loop whose body depends on which iteration it is in could not be
written (D21 gave `Repeat` no index, for the reversal argument); `DECISIONS.md` D29 records what was
decided; this entry records what a consumer pays.

- **`ActionView` has an eighth shape:** `Each of collection: JVal list * placeholder: string * body:
  'Action` — per-element iteration over a LITERAL collection, lowered by substitution: the body runs
  once per element with that element written over the placeholder, as a sequence. Every exhaustive
  match over the view gains an arm (the fold, the budget, the demanded projection, the replay
  classification, the fragment and the inverse in this repository; any consumer's own walk).
- **`OpView<'Op>` has a fifth shape:** `Each of collection: JVal list * placeholder: string * body:
  'Op list`. `OpView.edits` still fills `View` for a domain with none.
- **`ActionWitness` gains `Substitute: string -> JVal -> 'Action -> 'Action` and `Placeholders:
  'Action -> string list`; `StateWitness` gains `Substitute: string -> JVal -> 'Op -> 'Op` and
  `Placeholders: 'Op -> string list`** (FS0764 on every full-literal construction). `Substitute`
  writes an element over a placeholder throughout the action or op — the domain's own recursion — and
  is called only when an `Each` is met; `Placeholders` answers the names a node's OWN operands read,
  for the scope check. A domain with no placeholders fills them with `fun _ _ x -> x` and `fun _ ->
  []`, which is what the UI adapter does. The obligation on `Substitute` — the substituted action or
  op views as the original does, shape for shape — is stated in `proofs/README.md` and tested at the
  toy witness.
- **`OpView.beneath` takes the substitution:** `beneath view substitute op`, because the ops beneath
  an `Each` are its SUBSTITUTED elements — the policy must see every address a placeholder stands for.
  `ServerArgumentPolicy.reachOfOp` passes the state witness's; a consumer calling `beneath` directly
  adds the argument. `OpView.lowered` / `StateWitness.lowered` / `ActionWitness.lowered` are the
  lowered forms, public.
- **`Trace` gains `Each of elements: Trace list`** — the elements that ran, under their own
  constructor; `Trace.restorable` reads it; the inverse of an `Each` run is a `Reversed.Sequence`.
  **`FlowDecision` gains `Iterated of count: int`**, reported from both axes.
- **Two new refusals, both at validation and never mid-run**, each a `BoundedDiagnostic.Refused` (the
  fold, carrying the program's description) or the `ApplyOps` arm's `Failed` (the handler): `placeholder
  '<name>' is read outside any Each that binds it` and `placeholder '<name>' is already bound by an
  enclosing Each`. `BoundedActions.run` / `runTraced` refuse at entry with nothing run; `Handler`'s
  op planning refuses before its first op. `ScopeDefect` and the static walks
  (`BoundedActions.scopeDefects`, `StateWitness.scopeDefects`, `OpView.scopeDefects`,
  `ActionWitness.scopeDefects`) are public, for a host that validates at registration.
- **The budget prices an `Each` as its lowered form** — the body's cost once per element, summed, no
  step for a bound — so the driver's gate refuses an over-size `Each` before its first element.
- **The oracle differentials** map the shape lowered (`tests/*/ProofOracleTests.fs`); the toy corpus
  covers it; the verb domain carries placeholders as `{name}` tokens in its paths and the toy's
  expressions as `Hole`.

**What did NOT change:** no wire member, no fixture byte, no refusal class of the program wire
specification (D29 item 6: the specification references the action and op algebras and does not
spell their shapes; a compute stage's action and an effect's ops travel in the DOMAIN's codec), no
demanded-document byte for any program that uses no `Each` (the document stays at version 6), no
envelope needs re-signing, and the UI tier's behaviour, byte for byte through the parity suite and
the Fable leg — it views nothing as an `Each` (`ui_view_no_flow`, proved and tested over the
arm-complete corpus) and never substitutes. Every earlier proof statement keeps its form; `Each`
takes the sequence's case in each.

**What a consumer does about it:** `docs/migrations/phase-1990.md` — one page, a diff per file.

### Rides the draft: the substrate and the UI tier at their newest releases (2026-10-03)

**Class: breaking, at the dependency floor.** The draft already carries that class, so this rides it.
A change to the contract a package was BUILT against counts here as a change to its own contract
(the versioning policy above). `v0.6.0` is the newest tag, and nothing public pins `0.7.0`.

**The pins move:**
- `Fuaran.UI.*`: `0.86.0` → `0.90.0`.
- `Fuaran.Core.*`: `0.32.0` → `0.34.0`.
- **The evaluator moves to its new id.** `Fuaran.Core.DataFrame` `0.34.0` becomes
  `Fuaran.Compute.DataFrame` `0.37.0`, the id and namespace Core-Compute took at `0.36.0` (its
  DECISIONS D4).

These are the newest released lines, and they agree with each other: `Fuaran.UI` `0.90.0` is built
on exactly `Fuaran.Core` `0.34.0` and `Fuaran.Compute.DataFrame` `0.37.0`.

**What a consumer meets:**
- **The evaluator's types live in `Fuaran.Compute`.** That covers `ColExpr`, `Transform`,
  `EvalError` and `Slot` with their cases, and the `DataFrame` module itself. `DataSource`,
  `Table`, `Cell` and the JSON types stay in `Fuaran.Core`. A file that names them adds
  `open Fuaran.Compute`, or writes `Fuaran.Compute.` where it wrote `Fuaran.Core.`.
- **`ServerEffect.RunQuery` takes the new types.** Its pipeline is `Fuaran.Compute.Transform list`,
  and a query's failure is a `Fuaran.Compute.EvalError`.
- **`ColExpr` gained `Quotient` and `Rounded`** (Core-Compute `0.36.0`). The static query-schema
  walk reads their operands' columns. A consumer's own exhaustive match over `ColExpr` names both.
- **A consumer of the UI adapters raises the UI tier to `0.90.0`**, since the adapters are built
  against it.

**What did NOT change:** no wire member, no fixture byte, no demanded-document byte, no proof
statement, and no envelope needs re-signing.

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

