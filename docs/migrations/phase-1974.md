# Migration — Phase 1974: the witness becomes three axes

Rides the `0.7.0` draft slot (`STABILITY.md`). Breaking for every consumer that **constructs** a
witness, registers an **op performer**, or **names** `ProgramWitness` / `TreeWitness` / `OpWitness`
in a signature. A consumer that only calls the UI adapter's aliases changes nothing.

## What changes

| Before (0.7.0 as Phase 1967 shipped it) | After |
|---|---|
| `ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>` = `{ Tree; Action; Expr; Store; Op; Effect }` | `ProgramWitness<'Node, 'Op, 'Walk, 'Dispatch>` = `{ State; Walk; Dispatch }`; `FullWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>` names the all-three shape |
| `OpWitness<'Node, 'Op>` | `StateWitness<'Node, 'Op>`, plus `Canonical` (from the tree) and `View: 'Op -> OpView` |
| `TreeWitness` | `WalkWitness<'Node>` (`Nodes`, `Traverse`, `Cost`, `QueryReaders`); `Handlers`, `Events`, `Resolve` move to `DispatchWitness` |
| `Action`, `Expr`, `Store`, `Effect` at the top | inside `DispatchWitness` |
| `OpPerformance<'Op>`, `Performed of ('Op -> …)` | `OpPerformance<'Node, 'Op>`, `Performed of ('Node -> 'Op -> …)` — the state as of the op comes first |
| `SignedEnvelope.sign/verify/treeHash (tree: TreeWitness …)` | `(state: StateWitness …)` — pass `witness.State` |
| `ServerArgumentPolicy.* (ops: OpWitness …)` | `(ops: StateWitness …)` — pass `witness.State` |
| `UiWitness.ops`, `UiWitness.tree` | `UiWitness.state`, `UiWitness.walk`, `UiWitness.dispatch` |

A domain with no events fills `Dispatch = Unfilled` (its handler is `Handler<Nothing, 'Op>`, its
store `unit`); a domain with no tree to walk fills `Walk = Unfilled`. A domain with no op-channel
guard fills `View = OpView.edits`.

## The diff, per file

**A full witness** (every domain that had one before keeps all three axes):

```diff
-let witness: ProgramWitness<N, A, E, S, O, F> =
-    { Tree = { Nodes = nodes; Traverse = t; Handlers = h; Events = ev; Resolve = r
-               Cost = c; QueryReaders = q; Canonical = canon }
-      Action = action; Expr = expr; Store = store; Op = { Stream = s; Diff = d; AbsoluteTarget = at; Reach = rc }
-      Effect = effect }
+let witness: FullWitness<N, A, E, S, O, F> =
+    { State = { Stream = s; Reach = rc; AbsoluteTarget = at; Canonical = canon; Diff = d; View = OpView.edits }
+      Walk = { Nodes = nodes; Traverse = t; Cost = c; QueryReaders = q }
+      Dispatch = { Handlers = h; Events = ev; Resolve = r; Action = action; Expr = expr; Store = store; Effect = effect } }
```

**A domain with no events** (its guards were actions over a store only so that something could halt):

```diff
-let witness: ProgramWitness<St, VerbAction, VerbExpr, VerbStore, Op, VerbEffect> = { …32 members… }
+let witness: ProgramWitness<St, Op, Unfilled, Unfilled> =
+    { State = { Stream = s; Reach = rc; AbsoluteTarget = at; Canonical = canon; Diff = d
+                View = fun op -> match op with Check _ -> OpView.Require | _ -> OpView.Edit }
+      Walk = Unfilled
+      Dispatch = Unfilled }
-Compute(Need condition)                       // a guard over the binding store
+Effect(ServerEffect.ApplyOps [ Check c ])     // a guard over the state, as planned
-{ Tree = t; Bindings = args }
+{ Tree = t; Bindings = () }
```

**An op performer:**

```diff
-OpPerformance.performedBy (fun op -> perform op)
+OpPerformance.performedBy (fun state op -> perform op)          // a verb ignores the state
+OpPerformance.performedBy (fun state op -> persist state op)    // a tail renders/commits what it is handed
```

**Signed envelopes and the argument policy:**

```diff
-SignedEnvelope.sign witness.Tree sink project root
+SignedEnvelope.sign witness.State sink project root
-ServerArgumentPolicy.check witness.Op registry effect
+ServerArgumentPolicy.check witness.State registry effect
```

## Verification

1. `pwsh ./run.ps1` — format, pins, build, every suite, the Fable parity leg.
2. `pwsh ./proofs/check.ps1` — the four models check, extract byte-identical to `proofs/oracle/`, and
   the differential hosts agree (the staging host now covers the op-channel guard and the state
   handed with each op).
3. A consumer's own suite: a compile error naming `Tree`, `Op` or a missing `State`/`Walk`/`Dispatch`
   field is the migration's whole list of edits. A compile error saying a composition is not a
   `WalkWitness` / `DispatchWitness` means the code calls a path that reads an axis the domain does
   not fill — which is the point.
4. Behaviour: a UI host's demanded documents, outcomes and fixtures are byte-identical; nothing to
   re-sign.

## Rollback

Pin the previous `0.6.0` release (tagged) — the 0.7.0 slot is a draft and never shipped. In this
repository, revert the Phase 1974 commits; the proof model and its extraction revert with them (the
`proofs/oracle/Staging.fs` byte-diff holds them together).
