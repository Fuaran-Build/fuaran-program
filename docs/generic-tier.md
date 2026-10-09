# The generic tier — inventory, witness contract, adapter home

**Status: design note for Phase 1895, amended by Phases 1967 and 1974; the fourth witness (§3.12) added by Phase 1992, its class-B fixtures by Phase 1991.** The binding parts are in
[`DECISIONS.md`](../DECISIONS.md) D18, D19 and D20; §3 is written around the three axes D20 cut. This note holds the evidence behind them: the inventory the contract is sized against, the
contract itself member by member, why each assumption it keeps is kept, the two adapter homes with
their costs, and the consumers the change reaches. Phase 1896 cuts the core against §3; Phase 1897
builds the adapter against §4 and migrates the consumers in §5.

D4 deferred this cut until a second domain instantiated the algebra, because an abstraction with
one witness carries that witness's assumptions without anyone noticing. No second domain has
arrived. The cut is being taken now anyway, as a design decision, and D4's risk has not gone away
because of that. So this note does the thing D4 was afraid would be skipped: **it names every
assumption the contract takes from the UI witness, and says why each one is kept.** An assumption
kept on purpose is a design choice. One nobody wrote down is the defect D4 was describing.

---

## 1. What the inventory measured

This was checked against the tree at `0201c6f` before anything was designed, because the shard's
figures came from before the work.

- **The shard says "about 18 files: Bounded 9, Server 7, Runtime 2." The real number is 19.** Bounded
  has 9 and Runtime has 2, as stated. Server has **8**. `Durable.fs` opens no UI namespace but names
  `Fuaran.UI.ServerDriven.Validation.LiveEvent` fully qualified in two public signatures
  (`Durable.step` and `DurableControls.step`), so a search for `open` statements misses it. Another
  three Server files — `Journal.fs`, `Facets.fs`, `DenialDetector.fs` — use no UI type at all, and
  nothing they touch in Bounded is UI-typed.
- **The core never needed a UI type for its values.** `JVal` is `Fuaran.Core.JVal`, from
  `Fuaran.Core.Wire`, and so are `Canon`, `Json` and `Row`. The columnar types come from
  `Fuaran.Core.Column` and `Fuaran.Core.DataFrame`, and `IAttestationSink` comes from
  `Fuaran.Core.OpStream`. None of them is a UI type, but every one reaches this repository
  **transitively through the UI packages**. No project here has a direct `Fuaran.Core.*` reference.
  So removing the UI references means adding direct Core references. That is a precondition, not a
  side effect.
- **The program wire specification is already generic in its text.** Its §3 lists five "referenced
  vocabularies": an action, a tree-op, `ops`/`patches`, a tabular source or pipeline, and the
  `Bounded` diagnostic. Each is "specified elsewhere" and spliced in byte-stably. A scenario's
  `tree.json` is a referenced *document* on the same terms. The specification never restates a UI
  shape. The UI dependency lives in the implementation, not in the wire (§6).

### 1.1 Per file, per member

Roles: **T** tree shape · **O** tree ops (apply, diff) · **B** binding and state store · **R**
canonical encoding, hashing, claim signatures · **A** action vocabulary · **W** wire codec of a
referenced vocabulary · **S** the UI server-driven transport (events, validation, patches, frames,
channels) · **X** host policy (URL sanitising, the reserved state-key namespace). A member marked
*public* has a UI type in its signature, so porting it is a consumer-visible change.

**`Fuaran.Program.Bounded`** (Fable-packed)

| File | Member | UI identifiers | Roles |
|---|---|---|---|
| `BoundedActions.fs` | `BoundedStore` (public alias) | `BindingSources` | B |
| | `BoundedOutcome`, `HandlerAnswer<'P>` (public) | `BoundedStore`, `ClientEffect` | B S |
| | `HandlerArm<'P>` (public) | `BoundedStore` | B |
| | `runBoundedActionWith`, `runBoundedAction` (public) | all 14 `Action` cases; `TextSource`, `FileReadEncoding`; `BindingResolver.resolveJVal` / `resolveScalarText` / `resolveTextSource` and the four `Resolution` cases; `JValObj.toObj`; `StateKeys.isHostReserved` / `HostReservedPrefix`; `Sanitize.sanitizeUrl`; `ClientEffect` (5 cases); `Validation.describeAction` | A B S X |
| `Budget.fs` | `actionCascadeCost` (public) | `Action`, `Action.Chain` | A |
| | `treeCost` (public), `nodeCost` (private) | `Node`, `Introspect.getChildren`, `NodeKind.Chart` / `DataGrid` / `Map` / `Sparkline`, `Binding.Static` | T B |
| `Resolve.fs` | `resolveTree` (public) | `BindingSources`, `Node`, `getChildren`, `withChildren`, `BindingResolver.resolve`, `Resolution`, `TextSource`, the bound fields of 16 `NodeKind` cases | T B |
| `Schema.fs` | `QuerySchema.readersOfTree` (public), `readerOf` / `slotOf` (private) | `Node`, `descendantNodes`, `NodeKind.DataGrid` / `Chart`, `Binding.Query` | T B |
| | every other member | none (Core `ColumnType`, `Schema`, `DataSource`, `Transform`, `ColExpr`) | — |
| `Demanded.fs` | `ofAction`, `ofTree`, `check` (public) | `Action`, `Node`, `descendantNodes` | A T |
| | `effectKindOf`, `demandsOfAction` (private) | all 14 `Action` cases; `ClientEffect.kind` over canonical samples; `NavigateTarget.Self` | A S |
| | `hostCallsOfBinding`, `readsOfBinding` (private) | `Binding<JVal>`, `BindingWalk.usesOfBinding`, `BindingUse.Query` / `.State` | B |
| | `wireSurvivableActions`, `opaqueHandler` (private) | `NodeKind.Button.OnClick`, `Form.OnSubmit`, `Modal.OnDismiss`; `Validation.legitimateEvents` | T A S |
| | the projection types and its codec | none (Core `JVal`) | — |
| `ProgramWire.fs` | `encodeAction` / `decodeAction` (public) | `Action`, `Generated.encodeActionJson`, `JsonDecode.decodeNodeObj`, `NodeKind.Button` carrier | A W |
| | `encodeOp` / `decodeOp` (public) | `TreeOp`, `CanonicalJson.encodeOp`, `JsonDecode.decodeOp` | O R W |
| | `encodeClientEffect` / `decodeClientEffect` (public) | `ClientEffect` (8 cases), `NavigateTarget` | S W |
| | `replayDefectsOfAction`, `replayDefectsOfOp` | no type; they read the wire tags `Call`, `Chain`/`ops`, `SetState`/`valueFrom`, and the op member `target` | W (by string) |
| | envelopes, refusals, logic-tree reference, invocation, source/pipeline codecs | none | — |
| `SignedEnvelope.fs` | `treeHash`, `sign`, `verify` (public) | `Node`, `Hashing.sha256Hex`, `CanonicalJson.encodeNode`, `IClaimSignatureVerifier`, `KeyDirectoryEntry` | T R |
| `BoundedDriver.fs` | `BoundedServices`, `BoundedSession`, `BoundedReject`, `BoundedStepOutput`, `init`, `initStrict`, `step` (all public) | `Action`, `Node`, `TreeOp`, `WireTree.reify`, `RejectReason`, `DomPatch`, `ClientEffect`, `LiveEvent`, `ValidatedEvent`, `Validation.validate`, `TreeOpDiff.diff`, `Lowering.lower`, `BoundedStore` | T O S A B |
| `BoundedConnection.fs` | `OutOfBandRequest` / `Outcome`, `BoundedConnection` and every member (public) | `TreeOp`, `DomPatch`, `Frame`, `IFuaranLiveChannel`, `LiveConnectionDefaults`, `LiveEvent`, `Apply.apply`, `ApplyError`, `TreeOpDiff.diff`, `Lowering.lower` | O S |

**`Fuaran.Program.Runtime`** (Fable-packed)

| File | Member | UI identifiers | Roles |
|---|---|---|---|
| `EffectRegistry.fs` | `ClientEffectDestination.destinationOf`, `EffectRegistry` + `register` / `decide` / `perform` / `performAll` (public) | `ClientEffect` (8 cases), `ClientEffect.kind` | S |
| | `EgressPolicy.classify` | `Sanitize.sanitizeUrl` | X |
| | the egress, destination and denial types | none | — |
| `Program.fs` | `IClientLiveChannel`, `ProgramServices`, `ProgramReject`, `StepOutput`, `Program`, `mkBounded` / `mkBoundedStrict`, `handleEvent`, `applyPushed` (public) | `TreeOp`, `Action`, `Node`, `RejectReason`, `ClientEffect`, `WireTree.reify`, `LiveEvent`, `Validation.validate`, `ValidatedEvent`, `TreeOpDiff.diff`, `Apply.apply`; `BoundedStore` indirectly | T O S A B |

**`Fuaran.Program.Server`** (.NET only)

| File | Member | UI identifiers | Roles |
|---|---|---|---|
| `ServerEffect.fs` | `ServerEffect.ApplyOps` / `EmitPatch` (public) | `TreeOp` | O |
| | registry, denial, constraint and argument-policy members | none | — |
| `Handler.fs` | `ServerStore`, `HandlerStage.Compute`, `HandlerOutcome`, `HandlerTally` (public) | `Node`, `Action`, `TreeOp`, `ClientEffect`, `BoundedStore` | T A O S B |
| | `runEffect`, `perform` (private) | `Apply.apply`, `ApplyError`, `StateKeys.isHostReserved` / `HostReservedPrefix`, the store's `QueryResults` and `State` fields, `JValObj.toObj` | O X B |
| `ServerDemanded.fs` | `reachable`, `ofTreeAndHandlers`, `ofTreeHandlersAndRegistry`, `sign` / `signWithRegistry`, `verify` / `verifyWithRegistry` (public) | `Node`, `IClaimSignatureVerifier`, `KeyDirectoryEntry` | T R |
| `ServerSession.fs` | `ServerServices`, `ServerReject`, `ServerSession`, `ServerStepOutput`, `init`, `initStrict`, `querySchemaReport`, `stepWith`, `step` (public) | `Action`, `TreeOp`, `Node`, `RejectReason`, `ClientEffect`, `WireTree.reify`, `LiveEvent`, `Validation.validate`, `ValidatedEvent`, `TreeOpDiff.diff`, `BoundedStore` | T O S A B |
| `HandlerWire.fs` | `HandlerReport` (public) | `TreeOp` | O |
| | `decodeEffect` | `StateKeys.isHostReserved` / `HostReservedPrefix` | X |
| | the stage, effect and report codecs | indirect, through `ProgramWire`'s action and op codecs | W |
| `Replay.fs` | `ofTreeAndHandlers` (public) | `Node` | T |
| `Harvest.fs` | `ofProgram` (public) | `Node` | T |
| `Durable.fs` | `Durable.step`, `DurableControls.step` (public) | `LiveEvent`, fully qualified | S |
| | `run`, `arm`, `ControlledStep`, `DurableOutcome` | indirect, through `ServerStore` / `HandlerOutcome` / `HandlerArm` | — |
| `Journal.fs`, `Facets.fs`, `DenialDetector.fs` | — | none | — |

### 1.2 What the inventory reduces to

There are about forty distinct UI identifiers. They fall into **six things the algebra asks of a
domain**, plus **one transport** that is not part of the algebra.

1. **A tree** of nodes addressed by string ids. The algebra walks it, reads which actions a node
   carries, re-resolves its bound fields against the store, prices it, and hashes its canonical
   form. (T, and B for re-resolution)
2. **An action algebra** that the fold interprets. Four of its fourteen cases are control structure
   the fold owns: `Chain`, `SetState`, `Call`, and the refusal of a `Call` that declares a result
   target. The other ten are domain acts that either lower to a client effect or are no-ops. (A)
3. **An expression language** evaluated against a store. It is `Binding<JVal>` and `TextSource`,
   resolved by `BindingResolver`. (B)
4. **A store** with one state channel that `SetState` writes and one query-result channel that a
   server read lands in. (B)
5. **A tree-op algebra**: apply one op, and diff two trees into ops. (O)
6. **Canonical codecs** for the referenced vocabularies, a canonical tree encoding for hashing, a
   client-effect vocabulary, and a claim verifier. (W, R, and S for the effects)

Plus **the UI server-driven transport**: `LiveEvent`, `Validation.validate`, `RejectReason`,
`DomPatch`, `Lowering.lower`, `Frame`, `IFuaranLiveChannel` and `LiveConnectionDefaults`. The loop
*uses* this transport; the algebra does not need it. It moves out whole (§4.3).

---

## 2. What `Fuaran.Core.*` already provides, and what it does not

**The rule for this section: reuse a Core seam wherever one exists, and add a program-owned seam
only where Core has nothing.** Core already has the pattern, a record of functions over the domain's
own types (`IdWitness`, `NodeWitness`, `StreamWitness`, `ProjectionWitness`). The contract below is
written in that same style so that it reads as a sibling of those witnesses, not as a new idiom.

| Need | Core provides | Used as |
|---|---|---|
| JSON value, parser, canonical form | `Fuaran.Core.Wire`: `JVal`, `Json`, `Canon` | unchanged; this is how every program envelope is already encoded |
| Tree walking | `Fuaran.Core.Tree`: `NodeWitness<'Node,'Id>` (`Id`, `KindTag`, `Children`, `ReplaceChildren`) and `module Tree` | **reused as-is** for §3.1, with `'Id = string` |
| Hashing | `Fuaran.Core.Tree`: `Hash.sha256Hex` | replaces `Fuaran.UI.Hashing.sha256Hex` (assumption K7) |
| Apply an op, encode it, decode it | `Fuaran.Core.OpStream`: `StreamWitness<'Op,'State,'Rej>` (`Apply: 'Op -> 'State -> Result<…>`, `Encode`, `Decode`) | **reused as-is** for §3.5, with `'State = 'Node` |
| Signing | `Fuaran.Core.OpStream`: `IAttestationSink` | already what `SignedEnvelope.sign` takes; unchanged |
| Columnar source, pipeline, schema | `Fuaran.Core.Column`, `Fuaran.Core.DataFrame` | unchanged |
| **A key/value state store** | **nothing.** Core has only `KeyIndex`, an idempotency index. | program-owned seam, §3.4 |
| **Resolving an expression against a store** | **nothing.** | program-owned seam, §3.3 |
| **A claim verifier or key directory** | **nothing.** Core has only the signing sink. | program-owned seam, §3.6, generic in the key |
| **Diffing two trees into ops** | **nothing generic.** `Fuaran.Core.Ops` has a skeleton op algebra, but the UI's diff emits UI `TreeOp`s. | one member on the op witness, §3.5 |

Nothing is proposed for Core. Each of the three gaps has exactly one witness today. Moving any of
them into Core would repeat D4's error one layer down, where it would cost more to undo.

---

## 3. The witness contract

**Since Phase 1974 (D20) the contract is three records, composed.** Three witnesses have now measured
it — the UI tier, a store-mutating verb, a document pipeline whose state is its tree — and what they
share is not the tree: it is a **state** with an op algebra over it, a canonical form, a reach and a
refusal. What only the UI tier has is **dispatch from the tree**: handlers and events on nodes, a
binding store beside the tree the fold writes into, client effects. And what a domain with a tree
but no events has is a **walk**: the read-only passes over its structure. So:

```fsharp
type StateWitness<'Node, 'Op> =                     // REQUIRED — §3.5
    { Stream: StreamWitness<'Op, 'Node, string>     // Core, reused: Apply / Encode / Decode
      Reach: 'Op -> OpReach
      AbsoluteTarget: 'Op -> string option
      Canonical: 'Node -> string
      Diff: 'Node -> 'Node -> 'Op list
      View: 'Op -> OpView }                          // Edit | Require — the op-channel guard

type WalkWitness<'Node> =                            // optional — §3.1
    { Nodes: NodeWitness<'Node, string>; Traverse: 'Node -> 'Node list
      Cost: 'Node -> int; QueryReaders: 'Node -> QueryReader list }

type DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect> =   // optional — §3.2–§3.4, §3.6
    { Handlers: 'Node -> (string * 'Action) list; Events: 'Node -> string list
      Resolve: 'Store -> 'Node -> 'Node
      Action: ActionWitness<'Action, 'Expr, 'Store, 'Effect>
      Expr: ExprWitness<'Expr, 'Store>; Store: StoreWitness<'Store>; Effect: EffectWitness<'Effect> }

type ProgramWitness<'Node, 'Op, 'Walk, 'Dispatch> =
    { State: StateWitness<'Node, 'Op>; Walk: 'Walk; Dispatch: 'Dispatch }   // 'Walk / 'Dispatch: the axis, or Unfilled
```

**The type says which axes a domain filled, and a core path's signature says which it reads.** A
function that reads the walk takes `ProgramWitness<'Node, 'Op, WalkWitness<'Node>, 'Dispatch>`; one
that reads handlers on nodes takes `FullWitness<…>` (all three); one that reads only ops takes the
composition at any `'Walk` and `'Dispatch`. Handing a composition that does not fill an axis to a
path that reads it is a **compile error**, not a runtime default. The paths that must run under
EVERY composition — the fold (`BoundedActions.run`), the server handler (`Handler.run` /
`runWith`), the server tier of the demanded projection (`ServerDemanded.ofHandler`) — read the
dispatch position through `IDispatchPosition`, which `DispatchWitness` answers with its fold
sub-records and `Unfilled` answers with nothing, at action and effect types that have **no values**
(`Nothing`) and a `unit` store. So a handler under a dispatch-less composition cannot hold a compute
stage (there is nothing to put in one), and its one reachable consequence is that there is no
binding channel: a landing slot (`RunQuery`, a `HostCall`'s `into`) is refused while planning as
`no-binding-channel`.

**Since Phase 1982 (H1) the handler codecs join that list.** `HandlerWire` (encode, decode,
`contentAddress`, the replay walk), `Replay` and `Harvest.ofRegistration` read the dispatch position
the same way, so a domain that fills only the state axis encodes its handlers as Program documents,
harvests its registration's full demanded document (reach, replay and undo postures), and signs the
pair of the two (`SignedEnvelope.signAddressed` over `HandlerWire.contentAddress`, verified by
recomputation through `verifyAddressed`). At such a composition a document carrying a compute stage is
refused on `malformed-referenced-value`: the action is a referenced value, and there is no action
vocabulary to decode it with. A landing slot decodes, and the handler refuses it while planning, as
above.

| Axis | Who fills it | Read by |
|---|---|---|
| **state** (required) | every domain | the handler's op arm, the argument policy, the server demanded projection's reach, the signed envelope's tree hash (`Canonical`), replay's op classification, the session diff |
| **walk** (optional) | a domain whose state is a tree it wants priced or walked — the UI tier, the document pipeline | the budget, the query-reader census, re-resolution's structure, the client demanded projection |
| **dispatch** (optional) | a domain with events — the UI tier, the toy | the fold, compute stages, handlers and events on nodes, re-resolution's per-node step, landing slots, the action and client-effect codecs |

The sub-sections below keep their Phase 1896 numbering. Each says which axis it now belongs to.

### 3.1 The tree — now the WALK axis, and three members of dispatch

_Phase 1974: `Nodes`, `Traverse`, `Cost` and `QueryReaders` are the walk axis; `Handlers`, `Events`
and `Resolve` read a node to DISPATCH from it and moved to the dispatch axis; `Canonical` is the
state's canonical form and moved to the state axis. The Phase 1896 shape, for the record:_

```fsharp
type TreeWitness<'Node, 'Action, 'Store> =
    { Nodes:        NodeWitness<'Node, string>                // Core, reused
      Handlers:     'Node -> (string * 'Action) list          // event name -> action carried
      Resolve:      'Store -> 'Node -> 'Node                  // re-resolve ONE node's own bound fields
      Cost:         'Node -> int                              // the node's own data cost, 0 if none
      QueryReaders: 'Node -> QueryReader list                 // program-owned record (Schema.fs)
      Uses:         'Node -> BindingUse list                  // program-owned: Query name | State key
      Canonical:    'Node -> string }                         // canonical encoding, for the tree hash
```

`Resolve` is per node, not per tree: the core walks the tree itself using `Nodes`. Doing so keeps the walk in one
place (Core's `Tree`), with only the per-node step supplied by the domain. That per-node split is
also the reason `Cost` and `QueryReaders` are per node. It is the same projection `proofs/README.md`
already takes for the budget, where "the per-node classification reaches the model as a
`cost_shape`". The model never carried the node vocabulary, so the contract does not either.

### 3.2 The action view — the centre of the DISPATCH axis

_Phase 1974: the action view is the dispatch axis's, and so is the fold. Its laws name no state and no
walk member (`proofs/README.md`, "The three axes"). It was called the centre of the contract when the
contract had one witness; three witnesses later the centre they share is the state axis, and the
view is the centre of the axis only an event-driven domain fills._

```fsharp
type ActionView<'Action, 'Expr> =
    | Sequence of 'Action list                               // the composition arm (today: Chain)
    | Assign   of key: string * value: JVal option * from: 'Expr option   // today: SetState
    | Call     of endpoint: string * declaresTarget: bool    // today: Call; a declared target is refused (D9)
    | Require  of condition: 'Expr                           // the HALTING guard (Phase 1967, D19); no UI arm
    | Choose   of entry: 'Expr * whenTrue: 'Action * whenFalse: 'Action * exit: 'Expr option   // SELECTION (Phase 1976, D21); no UI arm
    | Repeat   of bound: Bound<'Expr> * body: 'Action        // BOUNDED ITERATION (Phase 1976, D21); no UI arm
    | Each     of collection: JVal list * placeholder: string * body: 'Action   // PER-ELEMENT ITERATION over a literal collection (Phase 1990, D29); no UI arm
    | Leaf     of LeafDeclaration                            // every other domain act

type Bound<'Expr> =                                          // D2's bound (Phase 1976)
    | Literal   of count: int                                // known from the tree: the reversible fragment's
    | Parameter of count: 'Expr * lo: int * hi: int          // resolved once at entry, range-checked, else halts

type LeafDeclaration =                                       // static, for the demanded projection
    { EffectKinds: string list                               // client-effect kinds it may emit
      HostCalls:   HostCallDemand list                       // program-owned (Demanded.fs)
      Opaque:      OpaqueLeaf option }                       // an escape, named with its reason class (Phase 2130, D40)

type LeafOutcome<'Effect> =
    | Emit    of 'Effect                                     // at most ONE effect per leaf (K3)
    | Refuse  of reason: string                              // -> BoundedDiagnostic.Refused
    | Decline                                                // -> BoundedDiagnostic.UnsupportedOnBoundedPath

type ActionWitness<'Action, 'Expr, 'Store, 'Effect> =
    { View:         'Action -> ActionView<'Action, 'Expr>
      Lower:        nodeId: string -> 'Action -> 'Store -> LeafOutcome<'Effect>   // Leaf only
      Describe:     'Action -> string                        // today: Validation.describeAction
      Substitute:   placeholder: string -> JVal -> 'Action -> 'Action   // an Each's lowering (Phase 1990, D29)
      Placeholders: 'Action -> string list }                 // the names this level's own operands read
```

The fold keeps its current form. It is one `match` in one file, now over `ActionView`. It owns
sequencing, the one store write, the reserved-namespace refusal, the D9 refusal of a declared result
target, and the handler-effect arm (D7). **It never recurses into a `Leaf`.** A leaf is lowered to at
most one effect, or refused, or declined, and the domain makes that choice through `Lower`.

**`Require` is the fifth shape, added by Phase 1967 (D19) for the second witness's F1.** Its
condition resolves against the store through `ExprWitness.Resolve`, exactly as an `Assign`'s `from`
does. The boolean `true` holds and changes nothing; any other value, an unresolved condition and an
errored one HALT: nothing after the guard in the enclosing sequence runs, the outcome's `Halted` says
so, and an errored condition's text is the halt's reason verbatim, which is how a domain's typed
refusal reaches the diagnostic (§3.5). A guard writes nothing and emits nothing. It is distinct from a
leaf's `Refuse`, which is a diagnostic the sequence carries on past — every UI-tier refusal is of that
kind, and no UI arm views as a guard, so the UI tier never halts (`ui_never_halts`, proved). The fold
does not roll back on a halt: the store is the store as of the halt, and the placement that rolls
back is the handler, whose atomicity unit it is (D8). A guard reads the STATE CHANNEL (K4): a domain
whose guards need to see its model exposes what they read through the channel.

**`Choose` and `Repeat` are the sixth and seventh shapes, added by Phase 1976 (D21) — the
selection and the bounded iteration D1 and D2 charter, which the second witness's re-run found
missing and had to carry beside the core.** A branch's entry condition resolves exactly as a
guard's: the boolean `true` takes the true arm, any other value the false arm, and an unresolved or
errored condition HALTS before either arm. Its EXIT assertion, when carried, resolves against the
store the arm left and must hold after the true arm and fail after the false arm (the Janus
discipline that lets an inverse pick the arm to undo): violated, unresolved or errored it halts
AFTER the arm, with the assertion named; absent, the branch runs forwards the same and is outside the
reversible fragment. A repeat runs its body `bound` times as a sequence (`repeat_is_unrolling`), sees
no index, and halts before its first iteration when a parameter bound resolves outside `[lo, hi]` —
the over-bound refusal — or to no count. Both are COMPOSITION shapes, so `fold_total`'s structural
characterisation excludes them as it excludes a sequence, and three shapes now halt
(`fold_no_halting_shape_no_halt`). The REVERSIBLE FRAGMENT — sequence, assign, guard, a branch with
an exit, a literal repeat — is decided from the tree (`BoundedActions.reversible`); a reversible run
(`runTraced`) records each overwritten value through `StoreWitness.Read`, the forward run records
nothing, and the inverse of a run (`reverse`, `runReversed`) restores the starting store
(`reverse_run`, proved; `proofs/README.md` section 6). No UI arm views as either shape
(`ui_view_no_flow`, proved and tested).

**`Each` is the eighth shape, added by Phase 1990 (D29) — per-element iteration over a LITERAL
collection, the loop D21's index-free repeat could not express.** The body runs once per element of
the collection, in order, with that element substituted for the placeholder — an expression of the
domain's that reads it by name — through the witness's `Substitute`. It is VOCABULARY that lowers to
the core by substitution (D1): what runs is the sequence of the substituted bodies (`each_is_lowering`,
proved), so no walk learns a new shape — the budget sums the lowered elements' costs, the demanded
projection and the replay classification read the lowered form, the trace records the elements that
ran (`Trace.Each`), and the inverse of an `Each` run is the sequence of its elements' inverses. The
substitution is the WITNESS's, because the core cannot see inside an action; the obligation that it
preserves the body's shape sits beside the obligation that `View` unfolds finitely, and the differential
host checks it at the toy witness. The bound is the collection's length, fixed by the tree (D2); the
element is a value in the tree and not a cell in the store, so the body cannot overwrite it, which is
the case D21 refused an index for. A placeholder is bound lexically by exactly one `Each`; a body that
reads one no enclosing `Each` binds, or an `Each` that rebinds a name an enclosing one binds, is
refused at VALIDATION — `BoundedActions.run` refuses at entry, before its first step, naming the
placeholder (`ActionWitness.Placeholders` says which names a node's own operands read;
`BoundedActions.scopeDefects` is the static walk). An empty collection runs nothing. No UI arm views
as it (`ui_view_no_flow`, unchanged).

The UI adapter's `View` is the total match over the closed 14-case `Action` DU. It carries the
`#nowarn "44"` scope that `BoundedActions.fs` holds today. Exhaustiveness is still checked by the
compiler; the check now sits in the adapter. `Confirm`, `Notify`, `AiTool`, `Invoke`, `Dispatch` and
`CommitLocal` become leaves whose `Lower` is `Decline`. `Navigate`, `WriteToClipboard`, `Print`,
`Focus` and `ReadFileBody` become leaves that `Emit` or `Refuse`. `Invoke`, `Notify` and `AiTool`
declare their host channels in `HostCalls`, so `Demanded` sees them exactly as it does today.

**Three walks move from the wire tags onto the view:**

- `Budget.actionCascadeCost` counts `Sequence`; since Phase 1976 it prices a `Choose` at one step
  plus the dearer arm and a `Repeat` at one step plus its body times its bound (a parameter bound at
  the top of its range), and `fold_steps_within_cost` proves a run stays within the price; since
  Phase 1990 an `Each` at its lowered elements' costs summed, no step for a bound.
- `Demanded` reads `Assign`, `Call`, `Require` and `LeafDeclaration`; since Phase 1976 a `Choose`
  demands the union of its entry, BOTH arms and its exit, and a `Repeat` its bound and its body once;
  since Phase 1990 an `Each` the union over its lowered elements. Since Phase 2130 an opaque leaf is named in the
  document's `opaqueLeaves`, and coverage refuses it until the host accepts its reason class (D40).
- The replay classification maps `Call` to no defect, `Sequence` to the distinct union of its
  parts, a literal `Assign` to no defect, `Assign … from` to `NonLiteralWrite`, `Require` to
  `UndecidableAction`, and `Leaf` to `UndecidableAction`; since Phase 1976 a `Choose` to
  `UndecidableAction` beside both arms' defects, and a `Repeat` to its body's (plus
  `UndecidableAction` for a parameter bound); since Phase 1990 an `Each` to the distinct union of its
  lowered elements' defects.

For every action the encoder can produce, that classification returns the same defects as today's
tag walk. Today's walk only ever runs on `encodeAction`'s output (`HandlerWire.fs`), and a `Chain`
whose `ops` is not an array cannot be encoded. The string coupling to `"Call"`, `"Chain"`/`"ops"` and
`"SetState"`/`"valueFrom"` then disappears.

### 3.3 Expressions — dispatch axis

```fsharp
type Resolution = Resolved of JVal | NotResolved | Errored of string   // program-owned

type ExprWitness<'Expr, 'Store> =
    { Resolve: 'Store -> 'Expr -> Resolution }
```

Today's `Resolution` has a fourth case, `I18nUnresolved`. Only `TextSource` resolution produces it,
and only inside the UI leaves (`Navigate`, `WriteToClipboard`). The core never sees it, so the core's
type has three cases and the adapter maps the fourth inside its own `Lower`.

### 3.4 The store — dispatch axis

_Phase 1974: the binding store is K4's state channel, and K4 turned out to be a dispatch-axis fact
(§3.7): a verb has no binding store, and a document keeps its bound values inside the document. A
composition with no dispatch axis has no store at all — its `ServerStore` bindings are `unit`._

```fsharp
type StoreWitness<'Store> =
    { Assign:     key: string -> JVal -> 'Store -> 'Store          // the State channel (SetState, handler landing)
      LandQuery:  slot: string -> Fuaran.Core.Table -> 'Store -> 'Store   // a server read's landing
      IsReserved: key: string -> bool                              // the reserved namespace (K5)
      ReservedPrefix: string }                                     // for the refusal's text
```

`Assign` takes a `JVal`. Converting it to the store's internal representation (the UI's
`JValObj.toObj`) is the store's own business.

### 3.5 Ops — the STATE axis (required)

_Phase 1974: `OpWitness` became `StateWitness`, gaining `Canonical` (from the tree) and `View` (the
op-channel guard below). It is the one record every domain fills._

```fsharp
type OpReach =                                              // what one op REACHES (Phase 1967, D19)
    { Arguments:   (string * string) list                   // author-declared names: a path, a remote, a node id
      Destination: EffectDestination }                      // Local | Remote host | … ; Absent for a tree op

type OpWitness<'Node, 'Op> =
    { Stream:         StreamWitness<'Op, 'Node, string>   // Core, reused: Apply / Encode / Decode
      Diff:           'Node -> 'Node -> 'Op list          // today: TreeOpDiff.diff
      AbsoluteTarget: 'Op -> string option                // replay: an op naming its target is re-runnable
      Reach:          'Op -> OpReach }                     // the argument policy and the demanded projection read it
```

`Stream.Encode` has to be the referenced vocabulary's own **canonical** encoder (K6). The core
splices its output into the program envelopes and never re-encodes it.

**`Reach` is what the second witness's W3 and W4 asked for.** Before it, `ServerArgumentPolicy` and
`ServerDemanded` read the five server-effect arms and reported, for the two that carry ops, the
discriminator and nothing else — a handler that wrote files and pushed a branch projected as the word
`ApplyOps`, and no policy could bound which path or which remote. `Reach` answers the op's named
arguments in exactly the `(argument, value)` shape the policy already reads off a host call, so an
`AllowList` on `ApplyOps` or `EmitPatch` binds; and its destination class in the client effects' own
`EffectDestination` vocabulary (the member W4 found on the wrong channel), allow-listed under the
reserved `destination` argument, so a policy can bound WHERE an op reaches without knowing how a
domain names it. A `Ceiling` on either arm measures the ops' canonical bytes through `Stream.Encode`.
The demanded document carries the reach as a server-tier member, at version 5 (§6). A reach value is a
NAME the op reaches, never its payload: the UI witness answers the nodes a tree op addresses and
deliberately not its prop paths, binding slots or values.

**A reach can be bounded from either side (Phase 1975).** `ServerConstraintClause` was `AllowList |
Ceiling | Label`: a policy could say which values an argument MAY carry, and not which it may NOT. A
domain whose own gate is a lock — a set of names nothing may touch, everything else writable — then
had one spelling for it, an allow-list over every name that existed when the policy was written. The
third witness (a document pipeline under a server placement) found what that costs: the universe is
stale the moment a handler creates a block and then edits it, so an insert-then-edit the domain's gate
admitted was refused here, and an op addressing a block that does not exist — the apply engine's
question — was refused at the policy instead of rejected as an op. Both are the one cause. `DenyList
of argument * refused` is the missing half: an effect carrying a value the list names is refused as
`OffList argument`, the same closed token, naming the argument and never the value; an effect naming
nothing under the argument passes, on the reading the allow-list takes. Beside an allow-list on the
same argument it composes as the domain composes its lock over its writable set — the lock wins, so a
name on both is refused — and since every clause must admit every value, that verdict does not depend
on which of the two a host declared first. Clauses of different kinds are still checked in
declaration order, so a value over a `Ceiling` and on a `DenyList` is reported under whichever came
first. The demanded document carries it as `{"clause":"denyList","argument":…,"refused":[…]}` beside
the other clauses, a sorted set like a permitted list, and does so at version 5 without a move: a
document's silence about deny-lists is true under every producer that ever wrote version 5, and a
reader built before the clause refuses a document carrying one at the clause rather than misreading
it.

**The reach obligation runs BOTH ways (Phase 1981, D24).** D19 stated one direction: a reach is a
NAME and must never be a payload. The other direction is the one a policy enforced against reach
depends on, and it is now stated: **an op's reach covers what its performer touches when handed the
op.** A domain that admits an op because its reach is within a lease, a deny-list or a worker grant is
admitting what the performer will DO, and the declaration is only as good as that coverage. The
obligation sits on the witness, as the first direction does, and it is checkable where the domain can
say how: the op performer answers a **receipt** (`OpPerformance.Performed` is
`'Node -> 'Op -> Result<JVal, string>`) — what it says it did, in the domain's own vocabulary, landing
in no slot and reaching no wire — and an **`OpContract`** declared at registration
(`OpPerformance.performedChecked contract perform`) is a predicate over the planned state, the op and
the receipt that the perform phase checks before the op is reported as performed. The route is the
domain's to choose: a verb's receipt names the paths it wrote and the targets it published, and the
contract checks each against the reach's `path` and `target` arguments (the in-repo verb witness,
`Receipt.withinReach`); a commit's receipt names the resulting sha; a document pipeline's names the
blocks it rendered. A receipt the contract rejects is a typed `PerformFailed` under `ApplyOps` naming
`return-contract:<name>` — the contract's name, never the receipt — with the handler rolled back and
`Performed` exactly the stages before it, on the terms a host call's return contract has had since
Phase 1759 (`op_return_contract` in `proofs/EffectGate.fst`; `uncontracted_is_direct` says a performer
with no contract runs as it always did). What the contract checks is the performer's ACCOUNT: a
performer that overreached and said nothing is outside what any contract can see, which is the
boundary [`performer-boundary.md`](performer-boundary.md) records, and the reason the obligation is
stated on the witness rather than claimed by the theorem.

**The rejection stays `string` (W5, decided in D19).** `StreamWitness`'s `'Rej` is fixed at `string`
here and stays so: a seventh type parameter would reach every public type that names the witness, the
handler's halt vocabulary and the outcome wire — where a reason is a string by specification — to
carry a type that crosses a process boundary as text in every one of those places anyway. A typed
refusal renders itself canonically (`Canon.render` over its own JSON) into the string the op channel
or a guard's `Errored` carries, and parses itself back on the far side; the in-repo second witness
pins that crossing.

**The op-channel guard (Phase 1974, D20 — the third witness's F-GUARD).** `View: 'Op -> OpView` says
which ops are guards. `OpView.Require` makes an op one: the handler resolves it through the op's own
`Stream.Apply` against the state AS OF ITS POSITION IN THE PLAN — the state the ops before it
produced, not the entry state. `Ok` holds and the state does NOT move (the answer is discarded, so a
guard cannot write); `Error reason` halts the handler with `reason` verbatim as the `ApplyOps`
arm's `Failed`, which is how a domain's typed refusal reaches the halt through the same W5 crossing
an apply refusal takes. A guard is never staged and never performed. Its demanded-projection
contribution is its reach, under `ApplyOps`, exactly as an edit's: the names it reads (a pack, a
path), which the argument policy binds on the same terms, so the document and the enforcement stay
one enumeration. The fold's `ActionView.Require` stays for a domain whose guards are over the
binding store; the two are not alternatives but the guards of two different states, and a domain
uses the one over the thing its guards read. Proved: `guard_holds_moves_nothing`,
`guard_refusal_halts` (`Staging.fst`).

**The op-channel branch and repeat (Phase 1976, D21).** `OpView<'Op>` has two more shapes.
`Choose(entry, whenTrue, whenFalse, exit)`: the entry condition is an op applied for its ANSWER,
exactly as a guard is — `Ok` takes the true arm, `Error` the false arm, so on this channel a domain's
typed refusal is the false value and never a halt (the channel has two answers and no third); the
arm plans as the ops of an `ApplyOps` effect plan; the exit assertion, when carried, is applied
against the state the arm left and must hold after the true arm and fail after the false arm, or the
whole effect is refused with the assertion named, after the arm planned. Neither condition is applied
for its state, staged or performed. `Repeat(count, body)` plans its body `count` times as a sequence;
the count is a literal the view produces — the state axis has no value channel to read a parameter
from, so D2's range check is the domain's at its codec, and a deployer's ceiling is the argument
policy's, through a `count` argument on the repeat's reach. The demanded projection and the argument
policy read an op's reach over itself AND every op beneath it (`OpView.beneath`), so an untaken arm's
reach is still reach and the document and the enforcement stay one enumeration. Proved:
`choose_plans_the_taken_arm`, `exit_violation_halts`, `repeat_plans_as_unrolling`,
`staged_from_the_final_state` (`Staging.fst`).

**The op-channel `Each` (Phase 1990, D29).** `OpView<'Op>` has a fifth shape, `Each(collection,
placeholder, body)`: the body plans once per element of a LITERAL collection, in order, each op with
that element substituted for the placeholder in its operands through the state witness's
`Substitute: string -> JVal -> 'Op -> 'Op` — the loop a spreadsheet's automation writes, one fixed
array of addresses and one body. What plans is the sequence of the substituted bodies
(`each_plans_as_lowered`, proved), so the arm learns no new shape: `OpView.beneath` lists the
substituted ops, and the argument policy and the demanded document therefore see EVERY address a
placeholder stands for — an element off an allow-list refuses the effect before anything performs;
the undo defects read the substituted ops' classes; the replay classification reads them too; the
outcome reports `FlowDecision.Iterated` with the element count. A placeholder is bound lexically by
exactly one `Each`; `StateWitness.Placeholders` answers the names an op's own operands read, and the
handler refuses an `ApplyOps` effect whose ops read one no enclosing `Each` binds — or whose `Each`
rebinds an enclosing name — BEFORE its first op plans, naming the placeholder
(`StateWitness.scopeDefects`). A domain with no placeholders fills the two members with the identity
and `[]`.

**The performer is handed the state (Phase 1974 — F-PERFORM).** `OpPerformance.Performed` is
`'Node -> 'Op -> Result<JVal, string>` (a receipt since Phase 1981; `unit` when this was written):
each edit is staged with the planned state WITH THAT EDIT
APPLIED, and the state handed with the last edit performed is the state the plan produced
(`performer_handed_the_plan`). A tail that renders or commits what the plan produced is handed it,
rather than folding the ops a second time from the entry state in the trusted base. `Performed` and
the `PerformFailed` prefix report are unchanged in shape.

**The undo posture (Phase 1977, D22).** `Undo: 'Op -> UndoClass<'Node, 'Op>` is the seventh member,
with `UndoClass = Inverse of ('Node -> 'Op list) | Compensate of ('Node -> 'Op list) | OneWay of
reason`. The CLASS is a function of the op alone, because the posture is read before anything runs,
from the declared form, where no pre-state exists; the INVERSE is a function of the pre-state, which
the plan phase holds with every edit and nowhere else — an inverse of a write needs the old bytes.
Only an op the witness views as an EDIT is asked. `Undo.posture` reads a handler `reversible`
(every edit an exact inverse, every compute stage in D21's reversible fragment, nothing reaching the
world), `compensable` (every edit an inverse or a declared compensation — a saga), `one-way` (a step
with neither: a one-way op, a host call, a notification — with the first such stage named, after
which nothing can be undone) or `unknown` (an emitted patch, a compute stage outside the fragment),
with reasons in stage order from a closed vocabulary (`compensated-op`, `one-way-op`,
`opaque-host-call`, `outbound-notification`, `emitted-patch`, `compute-outside-fragment`); the
demanded document carries it per handler at version 6, beside the replay posture (§6). The line that
matters is the LAW: an `Inverse` obeys K9 (§3.7) and a compensation obeys none, so a reversible plan
is CHECKED against the recorded entry state before anything performs and a compensable one reaches
whatever its compensations reach. `Handler.runPlanned` answers beside the outcome the plan a run
leaves — every edit with the state it was applied to, every compute stage with its trace, every step
that reached or emitted — and `Undo.run` performs the inverses in reverse plan order as ONE `ApplyOps`
effect through the same gate, argument policy and registered performers, then reverses the compute
stages' binding writes by D21's inverse; the first step it cannot perform is refused and named before
anything is undone, and a failed undo step reports how far it got in the `PerformFailed` vocabulary,
because the undo is a handler run. A landed read is not undone and contributes no reason: a read
reaches nothing, and the query slot is the host's cache. Proved: `undo_run_restores`,
`undo_run_reaches_compensated`, `one_way_position_exact`, `refused_before_anything`,
`undo_residual_is_prefix`, `run_planned_chain` (`proofs/Undo.fst`).

### 3.6 Effects and claims — dispatch axis

```fsharp
type EffectWitness<'Effect> =
    { Kind:        'Effect -> string                       // the gate's capability name (D3)
      Destination: 'Effect -> EffectDestination            // program-owned (EffectRegistry.fs)
      Encode:      'Effect -> JVal                         // the client-effect wire family
      Decode:      JVal -> Result<'Effect, WireRefusal> }

type ClaimVerifier<'Key> =                                  // program-owned; generic in the key
    { KeyId:  'Key -> string
      Verify: payload: string -> signature: string -> 'Key -> Async<bool> }
```

`SignedEnvelope.verify` reads nothing from a key except its id, so the core does not need to know
what a key is. The UI adapter builds a `ClaimVerifier<KeyDirectoryEntry>` out of
`IClaimSignatureVerifier`. A consumer holding its own key type builds one with no UI type in it
(§5, C1).

### 3.7 Assumptions kept from the one witness, and why

These are the assumptions D4 warned would get baked in without notice. Each is stated here along
with the evidence that would show it to be wrong.

| # | Assumption kept | Why it is kept | What would falsify it |
|---|---|---|---|
| K1 | **Node ids are strings.** | The wire fixes it. `invocation.nodeId`, a scenario event's `nodeId`, `ClientEffect.ReadFileBody`'s node and every diagnostic carry a string. A generic `'Id` would be converted to a string at every one of those boundaries and buy nothing. | A second domain whose ids have no faithful string form. Core's `IdWitness` is then where the conversion goes. |
| K2 | **Control structure is sequence + assign + call + require + choose + repeat + each, and everything else is a leaf.** _(Amended by Phase 1967: the second witness found halting missing — a verb's refusal must stop the sequence where a UI event handler's never needs to — and `Require` is the shape that halts. Amended again by Phase 1976: the second witness's re-run found the selection and the bounded iteration D1 and D2 charter missing, and carried a two-arm branch beside the core; `Choose` and `Repeat` are the shapes, on both axes, designed for reversal — D21. Amended a third time by Phase 1990: a loop whose body depends on which iteration it is in could not be written, because D21 gave `Repeat` no index for the reversal argument; `Each` over a literal collection is the shape, on both axes, lowered by the witness's substitution to the sequence of its elements — D29.)_ | This is D1: control structure belongs to the evaluator, and vocabulary lowers to it. Today's fold already treats ten of the fourteen UI cases as leaves, and none of them as a guard. | A domain act that must thread the store through sub-actions, the way `Confirm`'s continuations would with a return leg, or a conditional with two non-refusal arms. That is a new view case plus a model change under D14, never a leaf that recurses. |
| K3 | **A leaf emits at most one effect, and it never writes the store.** | `run_total` in `proofs/README.md` proves "at most one client effect and … one key" per non-composite step. A leaf that could write, or emit a list, would make the theorem false without making it fail. | A leaf that needs two effects. It has to be written as a `Sequence` of two leaves. |
| K4 | **One mutable state channel plus one query-result channel.** | That is everything the fold and the handler write today (`SetState`; `RunQuery`'s landing; a handler's `into`). The other fields of `BindingSources` (filters, selections, i18n) are read only by UI resolution, which sits behind `ExprWitness`. Since Phase 1967 the channel is also what a GUARD reads: a verb's arguments and read results land there, and a domain that keeps its model in the tree exposes what its guards need through the channel. | A second domain that writes somewhere other than a keyed state channel. The first second witness used no state channel at all and so could not have had a guard; that is a reading of K4, not a falsification. |
| K5 | **A reserved key namespace exists, and the fold refuses to write into it.** | The refusal is a property of the program loop: "the tree is untrusted" does not depend on the domain. Only the namespace's *spelling* (`host.`) is the UI tier's policy, so the witness supplies the predicate and the core owns the refusal. | A domain with no reserved keys. It supplies `fun _ -> false`, which is legal. |
| K6 | **Referenced vocabularies have one canonical encoder, and the core splices its output verbatim.** | Rule 1 of the wire specification's §3. It is the whole basis for the invariance claim in §6. | A witness whose encoder is not canonical. The composite documents stop being byte-stable, and the codec corpus goes red. |
| K7 | **`Fuaran.Core`'s `Hash.sha256Hex` is byte-identical to `Fuaran.UI.Hashing.sha256Hex`.** | Both are SHA-256 over the UTF-8 bytes, rendered as lowercase hex. Taking Core's version removes a UI reference from `SignedEnvelope`. | An envelope signed before the cut that fails to verify after it. Phase 1896 pins one such envelope as a test before the swap. |
| K8 | **An event is dispatched by the transport, not by the algebra.** | `Validation.validate` (its gate, its reject reasons, `LiveEvent`) is UI transport. The algebra begins at the point where an action has already been chosen. | None. This is where the line between the core and the adapter is drawn. |
| K9 | **An op's declared EXACT inverse restores the state it was applied to.** _(Phase 1977, D22.)_ `StateWitness.Undo` answers `Inverse f` for an edit; applied to the state the op produced, `f pre` — computed from the state `pre` the op was applied to — restores `pre`, byte for byte through `Canonical`. | The undo run is a theorem about a witness that keeps it (`undo_run_restores`, `proofs/Undo.fst`) and nothing else; a compensation obeys no law and is the honest word for an inverse that cannot be checked. | A witness whose inverse does not fold back: the undo run folds a plan of exact inverses against the recorded entry state before anything performs and refuses it (`undo-inverse-drift`), so the falsifier is caught at the one place it can be. |

**Read against three witnesses (Phase 1974).** The table above was written with one witness; the
verb and the document pipeline have read it since. What each assumption is now a fact about:

| # | Reading after three witnesses |
|---|---|
| K1 | **Holds, and is a walk/state fact.** The document pipeline's ids are TYPED and have a faithful string form through the domain's own id witness; the verb names files by path. Neither is the falsifier. |
| K2 | **Holds as amended (1967, 1976, 1990), on both axes.** Control structure in the ACTION view is the fold's; since Phase 1976 the op channel has the same three structures (`OpView.Require`, `.Choose`, `.Repeat`), mirrored rather than shared because the two axes' condition channels differ (D21), and since Phase 1990 the same fourth (`.Each`), mirrored because what the placeholder stands in differs per axis — an expression there, an operand here (D29). A domain with no actions meets K2 on the op channel alone. |
| K3 | **Dispatch fact.** Not reached by either non-UI witness — neither has leaves. |
| K4 | **A dispatch-axis fact, not a Program fact.** The verb uses no binding store; the document pipeline keeps its bound values INSIDE the document. The state channel is what an event-driven fold writes beside its tree, and only a domain that fills the dispatch axis has one. The 1967 sentence "a domain that keeps its model in the tree exposes what its guards read through the channel" is withdrawn: such a domain now guards on the op channel, against the state itself. |
| K5 | **Dispatch fact.** The reserved namespace is a namespace of the binding store; a composition without one has nothing reserved and refuses every landing slot instead. |
| K6 | **Holds, state fact.** Both non-UI witnesses' op bytes splice into the envelopes verbatim. |
| K7 | **Not reached** by either. |
| K8 | **Holds trivially**, and is the dispatch axis's boundary: a domain without events has no transport to dispatch them. |
| K9 | **A state fact, kept by all four witnesses** (Phase 1977): the UI tier's inverse is its structural diff, the toy's a relabel back, the verb's the write of the bytes a write overwrote, the document's the write of the text an edit replaced — and the verb's tests pin that a witness which breaks it is refused, not performed. |

**Deliberately not kept:** the fourteen-case `Action` DU, `NodeKind`, `BindingSources` as a type,
`TreeOp`, `ClientEffect`, `LiveEvent`, `DomPatch`, the UI's four-case `Resolution`, `WireTree`, and
the UI's `KeyDirectoryEntry`.

### 3.9 The contract as built (Phase 1896)

The code (`src/Fuaran.Program.Bounded/Witness.fs`) meets the shapes above with five differences,
each forced by keeping behaviour where it was rather than chosen:

- **`TreeWitness` gains `Traverse` and `Events`, and has no node-level `Uses`.** The UI tree has two
  child surfaces and the code walked each where it walked it: the budget and re-resolution use the
  STRUCTURAL surface (`Nodes`, paired with its `ReplaceChildren`), while the demanded projection and
  the query readers enumerate the whole traversal surface, structural children and the other
  positions a node holds (`Traverse`). `Events` is what the demanded projection's opaque-handler
  list reads (a node that accepts an event but carries no wire-surviving action). Nothing reads a
  node's own binding uses, so that member was not added.
- **`ExprWitness` gains `Uses`.** What an `Assign`'s `from` expression reads — a state key, or a
  query slot — is what the demanded projection states; §3.2 names the `Assign` shape as its input,
  and its reads are only reachable through the expression.
- **`ActionWitness` gains `Encode` and `Decode`.** §6 names the action codec as the witness's
  (`encodeActionJson` / `decodeNodeObj`); the handler codec splices it into a compute stage.
- **`EffectWitness.Encode` answers a string, not a `JVal`.** The client-effect family is the
  specification's one envelope exception (a `kind` discriminator, declaration-ordered members), so
  the shipped bytes are carried as bytes; re-rendering them canonically would change them.
- **The names:** the resolution is `ExprResolution` and the view `ActionView` (both
  `RequireQualifiedAccess`), so neither captures the UI tier's own `Resolved` / `Sequence` spellings
  in code that opens both.

And four more since 0.7.0 (Phase 1967, D19), each forced by the second witness rather than chosen:

- **`ActionView` has the `Require` shape and `BoundedOutcome` a `Halted` member** (§3.2). The fold's
  sequence stops at the first member that halts; `HandlerAnswer` carries no halt, because an answered
  call is its own atomicity unit.
- **`OpWitness` has `Reach`** (§3.5), and `ServerArgumentPolicy.arguments` / `payloadBytes` / `check`
  take the op witness, because an op sequence now has arguments and a size.
- **`Handler.runWith` takes an `OpPerformance<'Node, 'Op>`** — `InMemory`, the apply is the effect, or
  `Performed of ('Node -> 'Op -> Result<JVal, string>)` (handed the planned state since Phase 1974,
  answering a receipt since Phase 1981), under which `ApplyOps` is a staged arm performed after the
  plan commits, one staged call per op in plan order beside the host calls. `Handler.run` is
  `runWith` in memory; `ServerServices` carries the performance for the session loop. Since Phase
  1980 (D23) the durable interpreter journals a performed op stage exactly as a host call — at its
  ordinal in the one sequence, under the op's content address — and since Phase 1981 (D24) the
  performer's receipt is checked by an `OpContract` declared at registration
  (`OpPerformance.performedChecked`) before the op is reported as performed.
- **The demanded document is at version 5**, with `reach` on the server tier (§6).

And the cut itself (Phase 1974, D20), forced by the third witness:

- **`ProgramWitness` is `{ State; Walk; Dispatch }`**, with the walk and dispatch positions typed by
  what fills them (§3). Counted as 0.7.0's census counted (`Stream`, `Nodes` and every sub-record
  member by member), its 32 members plus the new `View` are now 8 required (state: the three
  `Stream` members, `Reach`, `AbsoluteTarget`, `Canonical`, `Diff`, `View` — six top-level fields), 7
  optional on the walk axis and 18 optional on the dispatch axis. `FullWitness` abbreviates the
  all-three shape.
- **`StateWitness` gains `Canonical` and `View`; `OpPerformance` gains the state** (§3.5). The signed
  envelope's tree hash reads `State.Canonical` and nothing else of the witness, so `SignedEnvelope`
  takes the state axis — a correction to the phase's own statement, which named it a walk reader.
- **The landing-slot refusal for a dispatch-less composition** (`no-binding-channel`) is the one new
  halt reason. No wire member moved.

And the flow algebra itself (Phase 1976, D21), forced by the second witness's re-run:

- **`ActionView` has `Choose` and `Repeat`, with `Bound<'Expr>`; `OpView<'Op>` has `Choose` and
  `Repeat` over ops** (§3.2, §3.5). Mirrored per axis, not shared: the condition channels differ and
  the state axis has no value channel for a parameter bound.
- **`StoreWitness` gains `Read`**, the one member a reversible run uses; the forward fold never calls
  it. `BoundedActions.runTraced` / `reversible` / `reverse` / `runReversed` and `Trace` are the
  reversible fragment's surface; `run` and `runInert` are unchanged.
- **`OpView.beneath`** enumerates the ops under a flow op, and `ServerArgumentPolicy.reachOfOp`
  reads an op's reach over itself and those — both arms. `OpReach.Destination` stays single-valued
  (D21's B2 answer): the arms name their own destinations.
- **The budget prices the shapes** (one step plus the dearer arm; one step plus bound times body),
  and `fold_steps_within_cost` proves a run stays within the price. No wire member moved; the
  demanded document stays at version 5.

And per-element iteration (Phase 1990, D29), forced by the first loop whose body depends on its
iteration — a spreadsheet importer's fixed array of addresses:

- **`ActionView` has `Each`; `OpView<'Op>` has `Each`** (§3.2, §3.5), each over a literal
  `JVal list` with a named placeholder. Mirrored per axis: the placeholder stands in an expression on
  one and an operand on the other.
- **`ActionWitness` gains `Substitute` and `Placeholders`; `StateWitness` gains the same two.** The
  lowering is the witness's — the core cannot see inside an action or an op — and is called only when
  an `Each` is met; the scope check asks which names a node's own operands read. The UI adapter fills
  all four with the identity and `[]`.
- **`OpView.beneath` takes the substitution**, so the policy and the demanded document see every
  element's address. `Trace` gains `Each`, `FlowDecision` gains `Iterated`, and `ScopeDefect` with the
  static walks (`BoundedActions.scopeDefects`, `StateWitness.scopeDefects`) is the one new vocabulary.
- **The budget prices an `Each` as its lowered elements summed**, with no step for a bound; the
  demanded projection, the replay classification, the undo defects and the inverse all read the
  lowered form. No wire member moved; the demanded document stays at version 6.

### 3.8 How D14 applies to this cut

D14 says that when the generic tier is cut, its model comes first. The fold's model already exists:
`BoundedFold.fst` covers the fourteen-arm action DU, written after the code. The cut does not write
a new evaluator. It re-types the existing one. **D14 still applies to the part that is new, which is
the view.** Phase 1896 re-states `BoundedFold.fst` over `ActionView` and re-proves `run_total`,
`run_no_closure` and the `Chain` homomorphism there, before it ports `BoundedActions.fs`.

With the view in place, `run_no_closure` becomes an obligation on the witness. The core holds no
closures, and only the UI adapter's `View` / `Lower` can reach one: `Call`'s `onResult` or
`ReadFileBody`'s `onRead`. The differential oracle in `proofs/oracle/` therefore runs through the UI
adapter, which is where the fourteen arms now live. The claims ladder's file citations move with the
code in the same commit.

D14 applied a second time at Phase 1967: the halting guard changes the view, so `BoundedFold.fst` was
restated over five shapes and re-proved — `run_total`, `run_no_closure` and the `Chain` homomorphism
keep their names and statements; the generic sequence homomorphism gains a halting clause that
`fold_no_require_no_halt` makes vacuous for every view without a guard, which is how the UI corollary
stays unconditional — BEFORE the port that added the shape. `Staging.fst` was extended the same way
for the op performer, before the handler was.

D14 applied a third time at Phase 1974. `proofs/README.md` gained "The three axes" — which members
each theorem names, and so which axes a witness must fill for it to say anything — before a `.fs`
file moved; `Staging.fst` restated the `ApplyOps` arm as one fold (`plan_ops`) carrying the guard and
the performer's state, and gained `guard_holds_moves_nothing`, `guard_refusal_halts` and
`performer_handed_the_plan`, with every earlier statement unchanged; `EffectGate.fst` followed.

### 3.10 The second witness (Phase 1967)

D4 said the generic tier would wait for a second domain because one witness carries its assumptions
unnoticed. D18 cut the tier anyway and named the assumptions. The second domain then arrived,
privately: a handler that is a store-mutating VERB — read a store, write and delete files, commit,
push — with those effects as the `'Op` of `ApplyOps`, run under the unmodified 0.6.0 `Handler.run`.
It was correct (byte-identical to the verb it replaced across thirty-five fixtures), and the contract
admitted it without change. That is the half of D4's warning that did not come true.

The other half did. Twenty-five of the contract's thirty-one members were vacuous or unfillable at
those types — the tree half is UI-shaped and a verb is not reached from a node tree, so `Action`,
`Expr` and `Effect` were each one inert case and `Tree` was ten-elevenths empty. That vacuity is a
finding and not a defect: it says the contract's centre is the fold and the op channel, and that a
verb lives at that centre. And three gaps forced the domain to build, beside Program, what belongs in
it:

| Finding | What the domain built beside Program | What Program now has (D19) |
|---|---|---|
| **F1** — no halting refusal outside the op channel: a leaf's `Refuse` is a diagnostic and the next stage's effect runs | guards as ops whose apply error halts | the `Require` shape of the view (§3.2) |
| **F2** — Program performs nothing of an op: `ApplyOps` folds `Apply` while planning and the perform phase runs staged host calls only | a two-phase discipline of its own, performing the plan after `Handler.run` commits | `OpPerformance.Performed`, under which `ApplyOps` is a staged arm (§3.9) |
| **W3 / W4** — no member projects an op's reach, and the reach-describing member sits on client effects | its own envelope walk and policy over `'Op` | `OpWitness.Reach`, read by the argument policy and the demanded document (§3.5) |
| **W5** — the op channel's rejection is `string` | a canonical-JSON crossing for its typed refusal | stays `string`; the crossing is the documented answer (§3.5) |

Each of the three is one witness member or one view shape, which is the size D4 predicted a real
assumption would be. What D4 did not predict is which direction the drift ran: not an assumption of
the UI tier baked into the contract, but a CAPABILITY the UI tier never needed and so never asked
for, which a second domain had to grow beside the core. A second demanded projection and a second
two-phase discipline, parallel to Program's, is the shape D4 warned about with the arrow reversed,
and it is why the findings landed here rather than staying in that domain.

The in-repo witness that stands for that domain — a verb over an in-memory file map, with an
adversary per finding — is `tests/Fuaran.Program.Tests/VerbDomain.fs`, in the project that references
no UI package. That domain's own differential and adversaries are the regression test for whether its
parallel machinery can now be deleted; re-running it is that domain's act, not this repository's.

### 3.11 The third witness (Phase 1974)

The third instantiation was the opposite corner from the verb: a document pipeline under a server
placement — a domain whose state is its tree. A full typed tree with a fourteen-case op algebra,
typed ids, a typed op rejection, a default-deny block gate, and no action algebra at all; stages
that read, guard, mutate, render and commit. It ran under the unmodified 0.7.0 `Handler.runWith`,
byte-identical to the hand-composed pipeline over its whole corpus, and Program's gate decided what
the domain's gate decided. Its census of the contract:

| Witness | Meaningful | Vacuous | Unfillable |
|---|---|---|---|
| UI tier | 32 | 0 | 0 |
| verb (1967) | 6 | 24 | 1 |
| document pipeline | 14 | 17 | 1 |
| grid (Phase 1992, §3.12) | 9 of the state axis's 9 | 0 | 0 — walk and dispatch unfilled by composition (D20), not vacuous |

The tree half went from one meaningful member (the verb) to six (the document) — ids, children,
replace-children, traverse, canonical — and nothing on the pipeline's path read them to any effect
but the walk and the hash. **The members vacuous for both non-UI witnesses are the same set, and it
is not the tree: it is dispatch.** That is why the two-axis proposal the verb suggested — tree
optional, ops required — was wrong, on two data points: it would have had the document fill a whole
axis for nothing, and it would have left the two capabilities the document actually lacked where
they were.

| Finding | What the domain built beside Program | What Program now has (D20) |
|---|---|---|
| **F-GUARD** — `Require` resolves against the binding store, and nothing moves the planned tree into it: a handler that planned a banned edit and then required the pack committed and published the term | its guard as an op whose apply refuses | `OpView.Require`: a guard on the op channel, over the state as planned (§3.5) |
| **F-PERFORM** — the op performer was handed the op and never the planned state | a performer that folds the ops again from the entry document, in the trusted base | the state as of each op, handed with it (§3.5) |
| **F-DENY** — no deny-list clause | an allow-list over an id universe that an insert makes stale | `DenyList` (Phase 1975, §3.5) |
| **F-NODE / F-STATE** — one `'Node` for the walked tree and the op state; the state is in the tree, so K4's channel is empty | — | the axes themselves; K4 reread as a dispatch fact (§3.7) |

The in-repo witness that stands for this domain is `tests/Fuaran.Program.Tests/DocDomain.fs`: a tree
whose bound values live in the document, typed ids with a faithful string form, a typed rejection
crossing as canonical JSON, no events and no handlers, a pack-style guard, and a render-and-commit
tail. It fills the state and walk axes. Its two guard tests are the third instantiation's,
inverted — the op-channel guard refuses the planned banned edit — and its performer's re-fold is gone:
the sink holds no copy of the document and its rendering is of the document it was handed
(`DocWitnessTests.fs`). The verb (`VerbDomain.fs`) now fills the state axis only: six members (seven since Phase 1977, with `Undo`), where
0.7.0 asked it for thirty-two.

**What a fourth domain reads first.** Fill `StateWitness` — seven members since Phase 1977, every one meaningful for any
domain with ops; a `Stream.Decode` that inverts `Stream.Encode` is what lets your handlers travel as
Program documents and be signed (Phase 1982). Fill `WalkWitness` if the state is a tree you want priced, walked or projected
client-side. Fill `DispatchWitness` only if nodes carry handlers that events dispatch; if they do not,
your guards are ops (`OpView.Require`), your reads are ops, and your tail is a performer handed the
plan. The signatures tell you, before a line is written, which Program functions your composition
can reach.

### 3.12 The fourth witness — a grid, for per-element iteration (Phase 1992)

Phase 1990 gave both axes `Each` over a literal collection, for a consumer that had not arrived: a
spreadsheet's automation. Rather than wait for that domain to pay for the construct's gaps, a fourth
witness measures it here, inside Program's own suite: `tests/Fuaran.Program.Tests/GridDomain.fs`, a
grid of cells keyed by PERMANENT identities (`r2c3`, never a position an insert would move), ops that
set, clear and copy a cell, named regions that grow by a row, a guard on a cell being filled, and
three flow ops the state witness views as the core's — an `If <cell> <> ""` as `Choose`, a
`For Each` as `Each`, and a `For i = a To b` as an `Each` over the range's integers. Its composition
is `ProgramWitness<Grid, CellOp, Unfilled, Unfilled>`: a grid's automation is a handler over its
cells, reached by no event and walking no tree.

**Its census is the first with nothing vacuous.** All nine state-axis members are meaningful,
`Diff` included — the verb leaves it empty, and a grid's state IS its cells, so a test holds
`apply (Diff a b) a = b` in both directions and from the empty grid. Every edit has an exact inverse
(an append's inverse is a drop of the last row; a region is never held empty, so the pair folds back
byte for byte), so every grid handler's undo posture is `reversible`. The walk and dispatch axes are
unfilled by composition, which D20 made a declaration rather than a cost.

**The class-A fixtures** (`GridWitnessTests.fs`) are the loops a spreadsheet importer
importer resolves at import time, each RUN, REVERSED through `Undo.run` back to the seed, and REPLAYED
from a durable journal with every step served and no performer invoked: a column of fixed identities;
a two-variable grid as a nested `Each` (row-major, the outer element fixed across the inner loop, the
flow reported in pre-order); a copy-values loop over a range, where an empty source clears its target
as a spreadsheet's copy does; and an `If` per element, whose guard reads the plan as of its position.
A `For i = 2 To 100` copy loop reaches both cells of all ninety-nine rows in the demanded document,
and an allow-list over two cells refuses it before anything performs.

**The finding list.** Every member of the contract the witness had to fill vacuously, and every
construct it needed and did not find:

| Finding | What the witness did | Routed |
|---|---|---|
| **G1 — no value channel on the state axis.** An op's operands are literals of the tree and the placeholder's element; nothing carries a value one op READ to a later op that WRITES it, and the dispatch axis's `ExprWitness` resolves against the binding store, never the planned state. | The copy-values loop is ONE fused domain op, `Copy(source, target)`, that reads and writes in a single apply. That covers the copy; a computed write (`Cells(i, 4) = Cells(i, 3) * 2`) would need the domain to carry an expression inside its op, evaluated against the plan, with no shared vocabulary for it. | An open decision on the roadmap: whether an op-axis expression channel is Program's or each domain's. One witness needs it, so the rule of three says the domain's for now. |
| **G2 — a region cannot be iterated at RUN time.** `Each` takes a literal collection, so `For Each r In Range("orders")` is resolved at import, and a row appended after the import is invisible to every later run of that handler. | Was pinned as a baseline test: the region read once into a literal, a row appended by a later handler, and the next run still iterating the two rows the import froze. | **CLOSED by Phase 1991 (D36).** `ForRegion(region, ceiling, placeholder, body)` views as `OpView.Each (Collection.Stored ({ Name; Read }, ceiling), …)`: the region as the grid holds it when the loop is entered, read once through the state axis. The class-B fixtures replaced the baseline (`GridWitnessTests` class B): a run marks the two rows; a row is appended; the next run marks three; a REPLAY of the first invocation, over the grown grid, is served the two the journal recorded (`ReadExtent` is its first ordinal) and performs nothing; a ceiling of one refuses the two-row region before anything performs; the demanded document names `orders` and the ceiling; an undo after the region grew runs exactly the two recorded inverses. |
| **G3 — `For i = a To b` needed no construct.** | The domain's `View` resolves a range to an `Each` over its integers; substitution, scope, reach, the argument policy, undo and replay all read the lowered elements. | Nothing to route: a negative result. The bound is the range's length, fixed by the tree (D2), as for any literal collection. |
| **Vacuous members — none.** | Every state-axis member is exercised by a test. | Nothing to route. |

The grid stands beside the verb (§3.10) and the document pipeline (§3.11) in the measurement table
above.

---

## 4. The adapter's home — two options

> **Superseded by DECISIONS.md D32 (fuaran#2012, 2026-10-04).** Option A, ratified below on 2026-09-27,
> was reversed: the two adapter packages and their UI-typed suites moved to the UI tier's repository,
> which depends on this core's released packages, and this repository pins, declares and names no
> UI-tier package. The analysis below is kept as the record of the choice as it stood; D32 says what
> changed — D30 and D31 first moved every piece of evidence about the CORE onto a non-UI witness, which
> removed option B's main cost before the move rather than paying it.

The UI binding has three parts:

- (a) the UI witness, which is the view, the leaf lowering, the codecs, the store and the claim
  verifier;
- (b) the UI transport loop, which is today's `BoundedDriver` step plus `Lowering`, `DomPatch` and
  `LiveEvent`, the whole of `BoundedConnection.fs`, and `Program.fs`'s client runtime;
- (c) the server placement's UI instantiation, which is `ServerSession`'s event step and
  `Durable.step`.

Parts (a) and (b) are Fable-clean. Part (c) is .NET only, because `Fuaran.Program.Server` is not
Fable-packed. A single adapter package therefore cannot serve both the browser and the server. Under
either option the adapter is **two packages**, split along the same Fable/.NET line as the core.

### Option A — in this repository: `Fuaran.Program.UI` and `Fuaran.Program.Server.UI`

The adapter packages reference `Fuaran.Program.*` by project and `Fuaran.UI.*` by package, which is
the direction D5 already chose.

- **For:**
  - D5's surviving clause holds unchanged: no `Fuaran.UI.*` package references `Fuaran.Program.*`,
    and the UI tier's package graph stays free of anything from this domain.
  - The core and its adapter release **in one commit and one version**, which is the "never a core
    without its adapter" condition in Phase 1897, met by construction.
  - The current test suite and the scenario corpus (`driver-semantics`, whose `tree.json` files are
    UI trees) run through the adapter **in this repository** by project reference. No package copy
    of the core is involved, so D5's skew class cannot happen here.
  - The client-effect wire family belongs to this domain's specification, and it keeps being
    certified here.
- **Against:**
  - This repository still pins `Fuaran.UI.*`, only in two leaf projects instead of all three core
    ones. A UI release that changes a type the adapter uses is still a two-repository trip (pack the
    UI tier, rebuild the adapter here), though now for the adapter only.
  - The separation is enforced at the package line, not at the repository line.

### Option B — in the UI tier: an adapter package there that references this repository's core packages

- **For:**
  - This repository pins no `Fuaran.UI.*` package at all, which is the operator's "separate" in its
    strongest form.
  - A change to a UI type and the adapter change it forces land in one commit, in the more actively
    developed repository.
- **Against:**
  - It **reverses the part of D5 that is still true.** A `Fuaran.UI.*`-tier package would reference
    `Fuaran.Program.*`. There is no cycle, because the core is UI-free, but the UI tier's release
    now depends on this domain's cadence. The UI packages move in lockstep at 0.78.x; this domain is
    at 0.5.x.
  - The current UI-typed test suite, the scenario corpus and the client-effect family would have to
    be certified from the UI tier. They cannot stay here: tests here that referenced the UI-tier
    adapter would compile the core twice, once by project and once transitively by package through
    the adapter. That is exactly D5's skew class, the same failure pointing the other way. About
    half of this repository's tests would move out of the repository that owns the behaviour they
    test.
  - "Release together" becomes a cross-repository ordering rule (core, then adapter), not a single
    act.

### Recommendation: Option A — ratified by the operator, 2026-09-27 (superseded by D32, 2026-10-04: option B)

Option A is the smaller correct change. It keeps the dependency direction D5 chose, confined to two
leaf packages, and it keeps the behaviour's tests beside the behaviour. Phase 1896's gate test is
what makes the separation real under A: **no core project references `Fuaran.UI.*`, and a test
fails if one comes back.** Moving later from A to B is cheap, because it moves two leaf packages.
Moving from B back to A would mean moving tests and a certified wire family back across a repository
boundary.

The recommendation changes to B if the operator's intent is that **this repository names no UI
package at all**, and not only that the core does. That is a judgement about what "separate" means,
not about engineering cost. The operator ratified A on 2026-09-27: the core must not reference UI,
and the two leaf adapter packages in this repository may.

---

## 5. Consequences

### 5.1 Version

The version moves from `0.5.0` to **`0.6.0`**, a pre-1.0 minor. It is breaking for all three core
packages, because their public types gain type parameters (the §1.1 *public* rows). The adapter
packages are new at `0.6.0`, and the core and adapter release as one version. This repository has
**no `STABILITY.md` today**, so Phase 1896's "`STABILITY.md` entry" task creates the file rather than
appending to it.

**Ergonomics:** the adapter keeps the old names as closed aliases and partially applied modules,
for example `type ServerStore = ServerStore<Node<obj>, Action<obj>, …>` and
`BoundedActions.runBoundedAction = BoundedActions.run uiWitness`. For most consumers, migrating means
changing one package reference and one `open`, not rewriting call sites.

### 5.2 The migration set

This was measured across the workspace, not taken from the shard. The shard named three consumers;
there are five.

| # | Consumer | References today | Opens UI namespaces itself? | Migrates to |
|---|---|---|---|---|
| C1 | a composition consumer: its effect-envelope verifier, and the generated-application template it writes into every generated app | `Bounded` 0.5.0 (the template also opens `Server`) | the verifier opens `Fuaran.UI.OpStream.Abstractions`, and its tests open `Fuaran.UI` / `Fuaran.UI.Types`, **with no UI reference declared**, so all of it arrives transitively through `Bounded` | the generic core for the verifier (§5.3); the adapter packages for the template |
| C2 | the out-of-repository evaluation suite (D11) | `Bounded`, `Server` 0.1.0-alpha.1 | **no** | the generic core plus `Fuaran.Program.Server.UI`, and only because its corpus is UI trees. Its gate machinery (the handler decoder, coverage, replay classification) is witness-generic. |
| C3 | a downstream application with a server placement | `Bounded`, `Server` 0.4.0 | yes, in four of six files | `Fuaran.Program.Server.UI`; it raises as its own act |
| C4 | a live-editing application in the UI tier | `Runtime` 0.4.0 | yes | `Fuaran.Program.UI` |
| C5 | a demonstration host in the UI tier | `Bounded`, `Runtime` 0.4.0 | yes, heavily | `Fuaran.Program.UI` |

**C4 and C5 are applications, not packages.** They are UI-tier consumers of this domain, and D5
never prohibited that: it prohibits a UI-tier **package** referencing this domain. They do not argue
for Option B. They are simply consumers of Option A's adapter.

The pins already disagree with each other (0.1.0-alpha.1, 0.4.0, 0.5.0). Moving everyone to 0.6.0
is each consumer's own raise, not something this phase does.

### 5.3 Consumer C1's undeclared UI dependency

The verifier uses exactly two UI things: `KeyDirectoryEntry`, and `SignedEnvelope.verify` with
`Demanded.ofTree` over a UI tree. Under this contract the signature half needs no UI type, because
`ClaimVerifier<'Key>` is generic in the key (§3.6). The tree half needs a `TreeWitness`. So the
**preferred resolution, (a) in Phase 1897, is reachable**: write the verifier generic over the
witness and let its composition root supply the UI adapter. The one project that supplies it then
**declares** the adapter reference, which is (b)-shaped, but only at that single leaf. A verifier
that insists on naming UI trees directly has only (b) available.

---

## 6. The wire specification does not change

The claim is that **no schema, no fixture byte and no rule in the program wire specification
changes.** It rests on three facts, and each can be checked.

1. **The specification never spells a UI shape.** Its §3 names the action and tree-op algebras only
   as referenced vocabularies "specified elsewhere", and its rule 2 forbids restating their cases.
   There is nothing in it that could be UI-typed, so there is nothing in it to change.
2. **The envelopes are already this domain's own.** Handler, stages, server-effect, outcome,
   invocation and the logic-tree reference are encoded by `ProgramWire` / `HandlerWire` over
   `Fuaran.Core.JVal`, and none of their encoders touches a UI type (§1.1). They move into the
   generic core unchanged.
3. **Every referenced position is filled by the same encoder as today.** The UI witness's
   `Op.Stream.Encode` is the UI's `CanonicalJson.encodeOp`. Its action codec is today's
   `encodeActionJson` / `decodeNodeObj` pair, and its effect codec is today's client-effect codec
   (K6).

**The evidence:** Phase 1896 runs the codec families (handler, server-effect, client-effect,
outcome, invocation, cross-layer) through the generic core and the UI adapter, and the output must be
byte-identical. It runs the twelve `driver-semantics` scenarios through the adapter's loop, because a
`tree.json` is a referenced document and the adapter is the thing that reads UI trees. **Any
difference in a fixture byte falsifies the claim**, and it means the refactor changed behaviour, not
only where the types live.

Two artefacts belong to this repository and are not part of the specification: the demanded
projection and the signed effect envelope. They are pinned the same way. The demanded projection's
host-channel spellings (`Query`, `Call`, `Invoke`, `Notify`, `AiTool`) come from the adapter's
`LeafDeclaration`s and from the core's `Call` channel constant, and the existing projection tests pin
them.

**Phase 1967 moved the first of those two, deliberately, and not the specification.** The demanded
document is at version 5: its server tier carries `reach` — what the handlers' ops reach, read
through the op witness — present and empty where the ops name nothing, on the argument versions 2, 3
and 4 each made. The UI documents whose bytes moved are exactly those of a handler whose ops address
a node, and the new bytes are pinned (`ServerDemandedTests`, `VerbWitnessTests`); the client-only
document is unchanged but for the version number. Because a signed envelope is verified by
recomputing the document, every envelope signed under version 4 now reports `Unreadable` drift naming
the version; the repository's own K7 pin was re-signed over the same tree, its tree-hash half still
the pre-cut value, and the test says so. Nothing in the program wire specification moved: the guard is
a shape of the action algebra, which the specification references and does not spell; the op
performer changes no wire; and the codec families and the twelve driver scenarios pass byte for byte.
The one sentence of the specification a registered op performer reads past — §6.2's "only `HostCall`
is staged" — describes the in-memory placement every conformant host had and every UI host still has.
Carrying the performer case into the normative text would be a specification act across all five
artefacts, and it is not taken here; it is recorded as the specification's own follow-on. Two later
phases re-asked the question and each decided it the same way on its own evidence: the durable
journal's op stage is a host-internal record and not a wire artefact (Phase 1980, D23 item 5), and
an op performer's receipt lands in no slot and reaches no wire member, its contract's refusal riding
§6.4's `PerformFailed` reason as "the performer's own text" exactly as a host call's return contract
has since Phase 1759 (Phase 1981, D24).

**Phase 1974 moved nothing at all.** The three-way cut is a cut of the package surface: no schema,
no fixture byte, no rule of the program wire specification and no byte of the demanded document
moved. The op-channel guard is a reading of the domain's own op through its own apply, and its
refusal is the `Failed` an apply refusal always was; the performer's state is an argument to host
code, not to any wire; and the one new halt reason (`no-binding-channel`) is reachable only under a
composition with no dispatch axis, which no UI host is. The UI adapter's codec families, its
driver scenarios, the parity suite and the Fable leg pass byte for byte.

**Phase 1977 moved the demanded document a second time, deliberately, and not the specification.**
The document is at version 6: its server tier carries `undo` — each reachable handler's undo
posture, read through the state witness's `Undo` member — present and empty where no handler
contributed, on the argument versions 3 and 5 each made. The UI documents whose bytes moved are
exactly those with a server tier (they gain `"undo":[…]`); a client-only document changes its
version number alone. Because a signed envelope is verified by recomputing the document, every
envelope signed under version 5 reports `Unreadable` drift naming the version; the repository's K7
pin was re-signed over the same tree, its tree-hash half still the pre-cut value. Nothing in the
program wire specification moved: the member is a reading of the domain's own ops through its own
witness, the plan a run leaves is an argument to host code, and the undo is an `ApplyOps` effect the
specification already names. The codec families, the driver scenarios, the parity suite and the
Fable leg pass byte for byte.

**The demanded document's own conformance vectors live in this repository, not in the
specification** (Phase 1978). `conformance/demanded-effect-projection.json` pairs each document with
what this tier's pinned reader makes of it, so a reader in another repository can certify without a
package dependency. The specification does not spell the demanded document, so its forward coupling
does not reach these vectors, and regenerating them changes this repository alone. They were stale for
two versions because the script that wrote them ran in no gate. The corpus is now
`DemandedCorpus.emit` in the server suite, and `tools/emit-demanded-conformance.fsx` wraps it.

**Phase 1982 moved neither the specification nor the document's version.** The handler outcome's
flow decisions (B3) are a host-side member of `HandlerOutcome` that `HandlerReport` does not carry,
so the outcome document is byte-identical; carrying the arm trace onto the wire would be a
specification act across all five artefacts, taken only when a consumer across a process boundary
needs it (D25). The handler codecs now run at a composition with no dispatch axis (H1), and refuse a
compute stage there on a refusal class the specification already names. The `atMost` policy clause
(R2) rides the demanded document's version 6 on the argument `denyList` rode version 5: no producer
of a version-6 document before it could declare one, and a reader built before it refuses a document
that carries one at the clause.
`DemandedCorpusTests` fails, naming the stale vector's line, whenever the committed file differs from
what the codec emits.

**Phase 1990 moved nothing in the specification, and the phase that filed it had asked it to.** The
phase's task list named `ProgramWire`, `HandlerWire`, the specification's text, schemas, emitter,
manifest and new fixtures as one change-set. Checked against the three facts at the head of this
section: `Each` is a shape of the action algebra and of the op algebra, which the specification
references and does not spell (its §3, rule 2); a compute stage's action and an `ApplyOps` effect's
ops are carried by the DOMAIN's own codec, spliced verbatim (K6); and the UI adapter's codec — the
only codec the specification's corpus exercises — is the UI specification's, whose tier adopts no
`Each`. So the construct reaches the wire exactly as `Choose` and `Repeat` do: inside the referenced
vocabulary's own encoding, which here the toy's and the verb's test codecs gain a case for and
round-trip. `ProgramWire`'s only touch is the replay classification, a walk over the view. No schema,
fixture byte, refusal class or rule moved; the demanded document stays at version 6 and no byte of
it moves for a program that uses no `Each`; the codec families, the driver scenarios, the parity
suite and the Fable leg pass byte for byte. A scope-defect refusal class was weighed and declined as a
specification act this phase does not need (D29 item 6).
