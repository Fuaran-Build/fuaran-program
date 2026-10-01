# The generic tier — inventory, witness contract, adapter home

**Status: design note for Phase 1895.** The binding parts are in [`DECISIONS.md`](../DECISIONS.md)
D18. This note holds the evidence behind them: the inventory the contract is sized against, the
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

The core is parameterised by one record, `ProgramWitness`. It is built from sub-records so that a
placement needing only part of it (the browser client never touches the handler codecs) can take
only that part. The type names below are the ones Phase 1896 introduces. The shapes are binding;
the spellings are the implementer's choice.

```fsharp
/// What the algebra asks of a domain, and nothing more.
type ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect> =
    { Tree:    TreeWitness<'Node, 'Action, 'Store>      // §3.1
      Action:  ActionWitness<'Action, 'Expr, 'Store, 'Effect>   // §3.2
      Expr:    ExprWitness<'Expr, 'Store>                // §3.3
      Store:   StoreWitness<'Store>                      // §3.4
      Op:      OpWitness<'Node, 'Op>                     // §3.5
      Effect:  EffectWitness<'Effect> }                  // §3.6
```

### 3.1 The tree

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

### 3.2 The action view — the centre of the contract

```fsharp
type ActionView<'Action, 'Expr> =
    | Sequence of 'Action list                               // the composition arm (today: Chain)
    | Assign   of key: string * value: JVal option * from: 'Expr option   // today: SetState
    | Call     of endpoint: string * declaresTarget: bool    // today: Call; a declared target is refused (D9)
    | Require  of condition: 'Expr                           // the HALTING guard (Phase 1967, D19); no UI arm
    | Leaf     of LeafDeclaration                            // every other domain act

type LeafDeclaration =                                       // static, for the demanded projection
    { EffectKinds: string list                               // client-effect kinds it may emit
      HostCalls:   HostCallDemand list }                     // program-owned (Demanded.fs)

type LeafOutcome<'Effect> =
    | Emit    of 'Effect                                     // at most ONE effect per leaf (K3)
    | Refuse  of reason: string                              // -> BoundedDiagnostic.Refused
    | Decline                                                // -> BoundedDiagnostic.UnsupportedOnBoundedPath

type ActionWitness<'Action, 'Expr, 'Store, 'Effect> =
    { View:     'Action -> ActionView<'Action, 'Expr>
      Lower:    nodeId: string -> 'Action -> 'Store -> LeafOutcome<'Effect>   // Leaf only
      Describe: 'Action -> string }                          // today: Validation.describeAction
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

The UI adapter's `View` is the total match over the closed 14-case `Action` DU. It carries the
`#nowarn "44"` scope that `BoundedActions.fs` holds today. Exhaustiveness is still checked by the
compiler; the check now sits in the adapter. `Confirm`, `Notify`, `AiTool`, `Invoke`, `Dispatch` and
`CommitLocal` become leaves whose `Lower` is `Decline`. `Navigate`, `WriteToClipboard`, `Print`,
`Focus` and `ReadFileBody` become leaves that `Emit` or `Refuse`. `Invoke`, `Notify` and `AiTool`
declare their host channels in `HostCalls`, so `Demanded` sees them exactly as it does today.

**Three walks move from the wire tags onto the view:**

- `Budget.actionCascadeCost` counts `Sequence`.
- `Demanded` reads `Assign`, `Call` and `LeafDeclaration`.
- The replay classification maps `Call` to no defect, `Sequence` to the distinct union of its
  parts, a literal `Assign` to no defect, `Assign … from` to `NonLiteralWrite`, and `Leaf` to
  `UndecidableAction`.

For every action the encoder can produce, that classification returns the same defects as today's
tag walk. Today's walk only ever runs on `encodeAction`'s output (`HandlerWire.fs`), and a `Chain`
whose `ops` is not an array cannot be encoded. The string coupling to `"Call"`, `"Chain"`/`"ops"` and
`"SetState"`/`"valueFrom"` then disappears.

### 3.3 Expressions

```fsharp
type Resolution = Resolved of JVal | NotResolved | Errored of string   // program-owned

type ExprWitness<'Expr, 'Store> =
    { Resolve: 'Store -> 'Expr -> Resolution }
```

Today's `Resolution` has a fourth case, `I18nUnresolved`. Only `TextSource` resolution produces it,
and only inside the UI leaves (`Navigate`, `WriteToClipboard`). The core never sees it, so the core's
type has three cases and the adapter maps the fourth inside its own `Lower`.

### 3.4 The store

```fsharp
type StoreWitness<'Store> =
    { Assign:     key: string -> JVal -> 'Store -> 'Store          // the State channel (SetState, handler landing)
      LandQuery:  slot: string -> Fuaran.Core.Table -> 'Store -> 'Store   // a server read's landing
      IsReserved: key: string -> bool                              // the reserved namespace (K5)
      ReservedPrefix: string }                                     // for the refusal's text
```

`Assign` takes a `JVal`. Converting it to the store's internal representation (the UI's
`JValObj.toObj`) is the store's own business.

### 3.5 Ops

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

**The rejection stays `string` (W5, decided in D19).** `StreamWitness`'s `'Rej` is fixed at `string`
here and stays so: a seventh type parameter would reach every public type that names the witness, the
handler's halt vocabulary and the outcome wire — where a reason is a string by specification — to
carry a type that crosses a process boundary as text in every one of those places anyway. A typed
refusal renders itself canonically (`Canon.render` over its own JSON) into the string the op channel
or a guard's `Errored` carries, and parses itself back on the far side; the in-repo second witness
pins that crossing.

### 3.6 Effects and claims

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
| K2 | **Control structure is sequence + assign + call + require, and everything else is a leaf.** _(Amended by Phase 1967: the second witness found halting missing — a verb's refusal must stop the sequence where a UI event handler's never needs to — and `Require` is the shape that halts.)_ | This is D1: control structure belongs to the evaluator, and vocabulary lowers to it. Today's fold already treats ten of the fourteen UI cases as leaves, and none of them as a guard. | A domain act that must thread the store through sub-actions, the way `Confirm`'s continuations would with a return leg, or a conditional with two non-refusal arms. That is a new view case plus a model change under D14, never a leaf that recurses. |
| K3 | **A leaf emits at most one effect, and it never writes the store.** | `run_total` in `proofs/README.md` proves "at most one client effect and … one key" per non-composite step. A leaf that could write, or emit a list, would make the theorem false without making it fail. | A leaf that needs two effects. It has to be written as a `Sequence` of two leaves. |
| K4 | **One mutable state channel plus one query-result channel.** | That is everything the fold and the handler write today (`SetState`; `RunQuery`'s landing; a handler's `into`). The other fields of `BindingSources` (filters, selections, i18n) are read only by UI resolution, which sits behind `ExprWitness`. Since Phase 1967 the channel is also what a GUARD reads: a verb's arguments and read results land there, and a domain that keeps its model in the tree exposes what its guards need through the channel. | A second domain that writes somewhere other than a keyed state channel. The first second witness used no state channel at all and so could not have had a guard; that is a reading of K4, not a falsification. |
| K5 | **A reserved key namespace exists, and the fold refuses to write into it.** | The refusal is a property of the program loop: "the tree is untrusted" does not depend on the domain. Only the namespace's *spelling* (`host.`) is the UI tier's policy, so the witness supplies the predicate and the core owns the refusal. | A domain with no reserved keys. It supplies `fun _ -> false`, which is legal. |
| K6 | **Referenced vocabularies have one canonical encoder, and the core splices its output verbatim.** | Rule 1 of the wire specification's §3. It is the whole basis for the invariance claim in §6. | A witness whose encoder is not canonical. The composite documents stop being byte-stable, and the codec corpus goes red. |
| K7 | **`Fuaran.Core`'s `Hash.sha256Hex` is byte-identical to `Fuaran.UI.Hashing.sha256Hex`.** | Both are SHA-256 over the UTF-8 bytes, rendered as lowercase hex. Taking Core's version removes a UI reference from `SignedEnvelope`. | An envelope signed before the cut that fails to verify after it. Phase 1896 pins one such envelope as a test before the swap. |
| K8 | **An event is dispatched by the transport, not by the algebra.** | `Validation.validate` (its gate, its reject reasons, `LiveEvent`) is UI transport. The algebra begins at the point where an action has already been chosen. | None. This is where the line between the core and the adapter is drawn. |

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
- **`Handler.runWith` takes an `OpPerformance<'Op>`** — `InMemory`, the apply is the effect, or
  `Performed of ('Op -> Result<unit, string>)`, under which `ApplyOps` is a staged arm performed after
  the plan commits, one staged call per op in plan order beside the host calls. `Handler.run` is
  `runWith` in memory; `ServerServices` carries the performance for the session loop. The durable
  interpreter runs in memory and journals no performed op — stated, not covered.
- **The demanded document is at version 5**, with `reach` on the server tier (§6).

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

---

## 4. The adapter's home — two options

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

### Recommendation: Option A — ratified by the operator, 2026-09-27

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
artefacts, and it is not taken here; it is recorded as the specification's own follow-on.
