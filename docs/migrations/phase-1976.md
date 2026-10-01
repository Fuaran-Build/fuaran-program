# Migration — Phase 1976: the view gains selection and bounded iteration, on both axes

Rides the `0.7.0` draft slot (`STABILITY.md`). Breaking for every consumer that **matches
exhaustively** on `ActionView` or `OpView`, **constructs** a `StoreWitness`, or **names** `OpView`
in a signature. A consumer that only calls the UI adapter's aliases changes nothing: the UI tier
views no action as either shape.

## What changes

| Before (0.7.0 as Phase 1974 shipped it) | After |
|---|---|
| `ActionView`: `Sequence`, `Assign`, `Call`, `Require`, `Leaf` | plus `Choose of entry: 'Expr * whenTrue: 'Action * whenFalse: 'Action * exit: 'Expr option` and `Repeat of bound: Bound<'Expr> * body: 'Action` |
| — | `Bound<'Expr> = Literal of count: int \| Parameter of count: 'Expr * lo: int * hi: int` |
| `OpView` = `Edit \| Require` | `OpView<'Op>` = `Edit \| Require \| Choose of entry: 'Op * whenTrue: 'Op list * whenFalse: 'Op list * exit: 'Op option \| Repeat of count: int * body: 'Op list` |
| `StateWitness.View: 'Op -> OpView` | `'Op -> OpView<'Op>` (`OpView.edits` still fills it) |
| `StoreWitness` = `{ Assign; LandQuery; IsReserved; ReservedPrefix }` | plus `Read: string -> 'Store -> JVal option` |
| `Budget.actionCascadeCost`: a sequence sums, else 1 | plus a branch at `1 + max(arms)`, a repeat at `1 + bound × body` |
| `ServerArgumentPolicy.reachOfOp`: one op's reach | the op's reach and every op beneath it (`OpView.beneath`) |
| — | `BoundedActions.runTraced` / `reversible` / `reverse` / `runReversed`; `Trace`, `Trace.restorable` |

## The diff, per file

**A witness that fills the store** (the one new member; a map-shaped store reads with `Map.tryFind`):

```diff
 Store =
     { Assign = Map.add
+      Read = Map.tryFind
       LandQuery = …
       IsReserved = …
       ReservedPrefix = … }
```

**A state witness whose ops include a branch or a repeat** (a domain with neither changes nothing —
`View = OpView.edits` still compiles, now at `OpView<'Op>`):

```diff
 View =
     fun op ->
         match op with
         | Check _ -> OpView.Require
+        | Branch(entry, whenTrue, whenFalse, exit) ->
+            OpView.Choose(Check entry, whenTrue, whenFalse, exit |> Option.map Check)
+        | Times(count, body) -> OpView.Repeat(count, body)
         | _ -> OpView.Edit
```

**An action witness whose actions include a branch or a repeat:**

```diff
 let view (action: MyAction) : ActionView<MyAction, MyExpr> =
     match action with
     | Seq items -> ActionView.Sequence items
+    | Pick(entry, whenTrue, whenFalse, exit) -> ActionView.Choose(entry, whenTrue, whenFalse, exit)
+    | Times(bound, body) -> ActionView.Repeat(bound, body)
     | …
```

**Any exhaustive walk over the view** (a consumer's own budget, projection or classification):

```diff
 match view action with
 | ActionView.Sequence xs -> …
 | ActionView.Assign _ | ActionView.Call _ | ActionView.Require _ | ActionView.Leaf _ -> …
+| ActionView.Choose(entry, whenTrue, whenFalse, exit) -> …   // both arms: an untaken arm still counts
+| ActionView.Repeat(bound, body) -> …                         // the body once, the bound as a factor
```

**A reversible run** (new; nothing existing calls it):

```diff
+let outcome, placement, trace = BoundedActions.runTraced witness arm nodeId action store placement
+if BoundedActions.reversible witness action && not outcome.Halted && Trace.restorable trace then
+    let inverse = BoundedActions.reverse witness action trace
+    let back = BoundedActions.runReversed witness nodeId inverse outcome.Store   // back.Store = store
```

## Verification

1. `pwsh ./run.ps1` — format, pins, build, every suite, the Fable parity leg.
2. `pwsh ./proofs/check.ps1` — the four models check, extract byte-identical to `proofs/oracle/`,
   and the differential hosts agree (the toy corpus now covers every arm and halt of both shapes).
3. A consumer's own suite: a compile error `FS0025` naming `Choose` or `Repeat` is the migration's
   list of walks to extend; `FS0764` on `StoreWitness` is the one member to fill; a type error on
   `View` is `OpView` becoming generic, and the fix is nothing but recompiling against it.
4. Behaviour: a UI host's demanded documents, outcomes and fixtures are byte-identical; nothing to
   re-sign. A program that uses neither shape folds exactly as before (`fold_no_require_no_halt`,
   kept as a corollary).

## Rollback

Pin the previous `0.6.0` release (tagged) — the 0.7.0 slot is a draft and never shipped. In this
repository, revert the Phase 1976 commits; the proof models and their extractions revert with them
(the `proofs/oracle/*.fs` byte-diff holds them together).
