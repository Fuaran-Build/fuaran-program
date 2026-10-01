# Migration — Phase 1977: the undo posture, and the undo run

Rides the `0.7.0` draft slot (`STABILITY.md`). Breaking for every consumer that **constructs** a
`StateWitness` or a `ServerDemand` as a full literal, **reads** the demanded document, or
**verifies** a signed envelope. A consumer that only calls the UI adapter's aliases changes nothing
but its document version.

## What changes

| Before (0.7.0 as Phase 1976 shipped it) | After |
|---|---|
| `StateWitness` = `{ Stream; Reach; AbsoluteTarget; Canonical; Diff; View }` | plus `Undo: 'Op -> UndoClass<'Node, 'Op>` |
| — | `UndoClass<'Node, 'Op> = Inverse of ('Node -> 'Op list) \| Compensate of ('Node -> 'Op list) \| OneWay of reason: string` |
| `Demanded.Version = 5`, server tier `{ effects, capabilities, functions, channels, reach, replay, constraints }` | `6`, plus `undo: [{ handler, undo, reasons: [{ stage, defect }] }]` |
| `ServerDemand` = `{ …; Replay; Constraints }` | plus `Undo: UndoPosture list` |
| `Handler.runWith` → `HandlerOutcome` | unchanged; `Handler.runPlanned` → `HandlerOutcome * UndoPlan` beside it |
| — | `Undo.posture`, `Undo.reasons`, `Undo.withPostures`, `Undo.ofTreeAndHandlers`, `Undo.run`, `UndoRefusal`, `UndoCode` |
| `Harvest.ofProgram` / `ofRegistration` join the replay posture | join the replay AND undo postures, from one reachability |

## The diff, per file

**A witness that fills the state axis** (the one new member):

```diff
 State =
     { Stream = …
       Reach = …
       AbsoluteTarget = …
       Canonical = …
       Diff = …
-      View = OpView.edits }
+      View = OpView.edits
+      Undo =
+        fun op ->
+            match op with
+            | Write(path, _) ->
+                UndoClass.Inverse(fun pre ->
+                    match Map.tryFind path pre.Files with
+                    | Some old -> [ Write(path, old) ]      // the bytes the pre-state held
+                    | None -> [ Delete path ])              // or the file it did not
+            | Publish target when retractable target -> UndoClass.Compensate(fun _ -> [ Retract target ])
+            | Publish target -> UndoClass.OneWay(sprintf "a publish to %s is public" target)
+            | … }
```

The class reads the op; only the inverse reads the pre-state. An `Inverse` carries K9: applied to
the state the op produced, the ops it answers for the state the op was applied to restore it. A
witness whose state is a tree with a structural diff answers every op
`Inverse(fun pre -> match Apply op pre with Ok post -> Diff post pre | Error _ -> [])`, which is
what the UI adapter does.

**A full-literal `ServerDemand`** (tests and bridges that build a tier by hand):

```diff
       Reach = []
       Replay = []
+      Undo = []
       Constraints = [] }
```

**A pinned demanded document:**

```diff
-{"kind":"demanded","version":5,…,"server":{…,"reach":[],"replay":[],"constraints":[]}}
+{"kind":"demanded","version":6,…,"server":{…,"reach":[],"replay":[],"undo":[],"constraints":[]}}
```

**A signed envelope** pinned under version 5 reports `Unreadable` drift naming the version; sign it
again over the same tree.

**Reading the posture before a run, and undoing one after it** (new; nothing existing calls it):

```diff
+let verdict = Undo.posture witness handler            // Reversible | Compensable | OneWay | Unknown
+let reasons = Undo.reasons witness handler            // [{ Stage; Defect }], in stage order
+let outcome, plan = Handler.runPlanned witness registry performance resolve nodeId handler store
+match Undo.run witness registry performance resolve nodeId plan outcome.Store with
+| Ok undone -> …          // undone.Store.Tree = store.Tree for a reversible run; a PerformFailed names how far it got
+| Error refusal -> …      // undo-one-way-step@k, undo-undecidable-step@k, undo-inverse-drift, undo-uncommitted-plan
```

## Verification

1. `pwsh ./run.ps1` — format, pins, build, every suite, the Fable parity leg.
2. `pwsh ./proofs/check.ps1` — the five models check, extract byte-identical to `proofs/oracle/`,
   and the differential hosts agree (the undo host runs the staging corpus undone and ten undo
   cases, with the undo's performer refusing at every position).
3. A consumer's own suite: `FS0764` naming `Undo` on a `StateWitness` literal, or naming `Undo` on a
   `ServerDemand` literal, is the migration's list; a demanded-document pin that moved from
   version 5 is re-pinned at 6 with `"undo":[]`; an envelope reporting `Unreadable` drift at
   version 5 is re-signed.
4. Behaviour: a UI host's outcomes and fixtures are byte-identical; its demanded documents gain the
   version number and, where a server tier exists, `"undo":[…]`.

## Rollback

Pin the previous `0.6.0` release (tagged) — the 0.7.0 slot is a draft and never shipped. In this
repository, revert the Phase 1977 commits; the proof models and their extractions revert with them
(the `proofs/oracle/*.fs` byte-diff holds them together), and the K7 pin returns to its version-5
signature.
