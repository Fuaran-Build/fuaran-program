# The Core-Compute evaluator (`Fuaran.Core.DataFrame` 0.34.0) beside the UI tier — surface diff

**Verdict (Phase 1896): PINNED. The core packages name the substrate directly —
`Fuaran.Core.DataFrame` at 0.34.0 and `Wire`, `Column`, `Tree`, `OpStream` at 0.32.0 — and the UI
tier is pinned at 0.86.0, the release built on exactly those versions.** The .NET half of this
question was first measured in Phase 1894; Phase 1896 added the Fable half, found no UI release that
cleared both at the time, and took the raise when UI-tier 0.86.0 was published. Measured 2026-09-27
and 2026-09-28.

## Why the evaluator could not move on its own

`ServerEffect.RunQuery` calls `Fuaran.Core.DataFrame` directly. Phase 1896 removes every UI-tier
reference from the core packages, and its specification assumed that this alone would free the
evaluator to move. It does not, for two reasons that compound:

1. **One restore graph.** Central package management gives one version per package id, and the UI
   adapter — parked under `tests/` by Phase 1896, packaged by Phase 1897 — is compiled beside the
   core and references it. The adapter's graph holds the core's substrate version whatever the core's
   own graph holds.
2. **Under Fable, the UI tier is compiled from SOURCE.** The .NET half is binary compatibility; the
   Fable half (the tier-parity leg, and the browser sample) is source compatibility, which is strictly
   stronger.

So the evaluator and the UI tier move together, and the pair has to clear both halves.

## The .NET half — IL surface (Phase 1894)

An IL reference scan (`System.Reflection.Metadata`) of every `TypeRef` / `MemberRef` the UI-tier
0.78.1 assemblies and the 0.21.0 substrate make into `Fuaran.Core.{DataFrame,Column,Wire}`, resolved
against the 0.34.0 / 0.32.0 / 0.32.0 set: 0 missing members or signature mismatches, but two
referenced closed unions gained an appended case — `ColExpr.Now` and `EvalError.UnpinnedClock`. The
UI tier's decode-time admission walk matched `ColExpr` exhaustively, so a 0.78.1 build run against
0.34.0 refused a `now` expression with a diagnostic naming the wrong construct.

**At the pinned pair this no longer arises**: UI-tier 0.86.0 is compiled against DataFrame 0.34.0
itself, so its walk knows `Now`, and there is no version gap between what the tier was built on and
what it runs with. The scan was not re-run for that reason — it measures a gap, and there is none.

## The Fable half — source compatibility (Phase 1896)

With the 0.34.0 evaluator in the graph, UI-tier 0.78.1's own sources do not compile under Fable:
`Transform.Limit` takes `Slot<int>` (the tier's `JsonDecode.fs`), the evaluator's `NowGrain` cases
`Date` / `Timestamp` capture names the tier's `BindingResolver.fs` uses for `Cell`, and a hash-chain
break's reason is a `ChainBreakReason` (the tier's `HashChain.fs`, once `OpStream` is raised). No
binary scan can see a source break, and none is repairable from this repository.

## The releases checked

Against the public registry's index and each release's nuspec dependencies (all eight UI ids served
at every version below):

| UI tier | Built on DataFrame | Fable leg against the 0.34.0 evaluator | Result |
|---|---|---|---|
| 0.78.1 | 0.21.0 | does not compile (above) | the round-1 state: substrate pinned at 0.21.0 |
| 0.79.0, 0.80.0 | 0.21.0 | same substrate as 0.78.1 | not a candidate |
| 0.81.0 | 0.22.0 | predates `Slot` | not a candidate |
| 0.83.0, 0.84.0, 0.85.0 | 0.24.0 – 0.28.0 | 0.85.0 compiled and ran 11/11 scenarios | built below 0.34.0 — the IL gap above remains |
| **0.86.0** | **0.34.0** (substrate 0.32.0) | **compiles; 11/11 scenarios** | **pinned** |

## What the raise changed, and what it did not

- **No conformance-corpus byte changed.** Round 1 of Phase 1896 read the scenario-tree mismatch at
  0.83.0 / 0.85.0 as the tier re-encoding the tree. That reading was wrong: the committed scenario
  fixture carries a file upload's `"onSelect":"<closure>"`, and it was this repository's own SEED
  that stopped emitting it, because the tier's default file upload no longer carries a selection
  handler. The fix is in the seed (`tests/Fuaran.Program.Parity/Seeds.fs` names its handler), and the
  corpus is untouched.
- **One behaviour follows the UI tier, by ruling.** The tier's resolver now answers a state binding
  with NO declared default, at a slot nothing has written, as unresolved (where it answered the empty
  value). The adapter follows the tier rather than special-casing around it, so a clipboard write of
  such a binding is refused, and one with a declared default copies the default. Pinned by two tests;
  recorded in `STABILITY.md` under 0.6.0.
- **Source adaptations the substrate forced:** `ColExpr.Now` reads no column in the query-schema walk
  (pinned by a test), `EvalError.UnpinnedClock` has its error-kind name, `Limit` and `Sort` take slots
  in the tests and the benchmark, and two grid fixtures name the two new `DataGridSpec` fields.
- **What it bought:** `docs/runquery-benchmark.md` — linear where 0.21.0 was badly super-linear, four
  orders of magnitude faster at 100,000 rows.

Before any future raise of either side, re-run both halves against the candidate pair: the IL scan
where the UI tier was built on a different substrate than it will run with, and a Fable compile of
the adapter graph always.
