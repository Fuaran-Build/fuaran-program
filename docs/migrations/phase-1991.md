# Migration — Phase 1991: `Each` over a collection the store holds, on both axes

Rides the `0.8.0` draft slot (`STABILITY.md`). Breaking for every consumer that **constructs or
matches** an `ActionView.Each` or an `OpView.Each`, **annotates** an `OpView<'Op>`, **constructs** a
`HandlerArm`, a `ServerEffectRegistry` or a `DemandedProjection` as a full record literal, matches
`Trace` exhaustively, or **reads** a demanded document (its version moves from 6 to 7). A consumer
that only calls the UI adapter's aliases changes nothing: the UI tier views no action and no op as an
`Each`, and the adapter's `View = OpView.edits` still compiles.

## What changes

| Before (0.8.0 as Phase 1905 left it) | After |
|---|---|
| `ActionView.Each of collection: JVal list * placeholder * body` | `Each of collection: Collection<'Expr> * placeholder * body` — `Collection.Literal elements` is what the list was; `Collection.Stored (source, ceiling)` is new |
| `OpView<'Op>` | `OpView<'Node, 'Op>` — the node type, because a stored collection is read from the planned state: `OpView.Each of collection: Collection<StateCollection<'Node>> * placeholder * body` |
| — | `Collection<'Source>`, `StateCollection<'Node>` (`{ Name; Read }`, equal by name), `IterationDemand` (`{ Collection; Ceiling }`), `ExtentReader`, `ExtentReader.live`, `ExtentReader.Capability` (`"ReadExtent"`) |
| `Trace`: `… \| Each` | plus `EachOf of extent: JVal list * elements: Trace list` |
| `HandlerArm = { Answer }` | plus `ReadExtent: ExtentReader` (`HandlerArm.inert` reads live) |
| `ServerEffectRegistry`: five members | plus `ReadExtent: ExtentReader` (`denyAll` reads live; `withExtentReader`) |
| `DemandedProjection`: five fields | plus `Iterations: IterationDemand list`; the document gains `iterations` and moves to **version 7** |
| — | `OpView.iterations`, `StateWitness.iterations`, `ExprWitness.collectionName` |
| `Budget.actionCascadeCost` prices an `Each` as its lowered form | a stored one as one step plus the body at its ceiling |
| `Durable.runWith` journals host calls, queries and op stages | and extent reads, at their ordinal, under `ReadExtent` |

## The diff, per file

**A witness that constructs a literal `Each`** (the toy, the verb, the grid — any domain whose view
produced one since Phase 1990):

```diff
-    | ForEach(collection, placeholder, body) -> ActionView.Each(collection, placeholder, body)
+    | ForEach(collection, placeholder, body) -> ActionView.Each(Collection.Literal collection, placeholder, body)
```

```diff
-let view (op: CellOp) : OpView<CellOp> =
+let view (op: CellOp) : OpView<Grid, CellOp> =
-    | ForEach(collection, placeholder, body) -> OpView.Each(collection, placeholder, body)
+    | ForEach(collection, placeholder, body) -> OpView.Each(Collection.Literal collection, placeholder, body)
```

**A domain that offers a collection the state holds** (new; nothing to fill for one that does not):

```diff
+    | ForRegion(region, ceiling, placeholder, body) ->
+        OpView.Each(
+            Collection.Stored({ Name = region; Read = regionCollection region }, ceiling),
+            placeholder,
+            body
+        )
```

**A walk that matches `Each`** — the fold, the budget, the demanded projection, the replay
classification, the fragment, the inverse, a consumer's own:

```diff
-    | ActionView.Each(collection, placeholder, body) -> ... lowered collection ...
+    | ActionView.Each(Collection.Literal elements, placeholder, body) -> ... lowered elements ...
+    | ActionView.Each(Collection.Stored(source, ceiling), placeholder, body) -> ... the body once, with the placeholder standing; the ceiling ...
```

**A full-literal `HandlerArm`, `ServerEffectRegistry` or `DemandedProjection`** (FS0764 names the
missing member):

```diff
 { Answer = fun nodeId endpoint store placement -> ...
+  ReadExtent = ExtentReader.live }
```

```diff
   OnDenied = ignore
-  QueryEvaluator = None }
+  QueryEvaluator = None
+  ReadExtent = ExtentReader.live }
```

```diff
   OpaqueHandlers = []
+  Iterations = []
   Server = None }
```

**A reader of the demanded document** — a stored corpus, a manifest consumer: `"version":7` and the
member `"iterations":[...]` between `opaqueHandlers` and `server`. A document that uses no stored
collection carries `"iterations":[]`; nothing else in it moves. A corpus emitted at version 6 is
re-emitted (this repository's `conformance/demanded-effect-projection.json` was moved by hand to
exactly what the version-7 encoder emits, pending the UI tier's generator catching up at its raise).

**A `Trace` match**: add `| Trace.EachOf(_, elements) ->` beside `Trace.Each elements`.

## Verification

1. `pwsh ./run.ps1` — format, pins, build, every suite, the Fable parity leg.
2. `pwsh ./proofs/check.ps1` — the four restated models check, extract byte-identically to the
   committed oracles, and the differential hosts agree over the widened corpus.
3. A consumer's own tests: `tests/Fuaran.Program.Tests/StoredEachTests.fs` (the dispatch axis — a
   stored `Each` beside a literal one over the same extent, the ceiling refusal, the once-only read,
   the inverse over the record, the journaled read) and `GridWitnessTests.fs` class B (the op axis —
   a region iterated at run time, a row appended between runs, a replay served the record) are the
   shapes.

## Rollback

Revert the three commits of Phase 1991 together (proofs, feature, docs). The oracles under
`proofs/oracle/` are regenerated by the proof leg and must move with the models. The demanded
document returns to version 6, so a document signed at version 7 — the D15 envelope binds its hash —
no longer verifies against a version-6 recomputation and must be re-signed; a journal that recorded
a `ReadExtent` step cannot be replayed by the reverted interpreter (it serves the step to no caller
and diverges at the next ordinal), which is the one journal shape this phase introduced.
