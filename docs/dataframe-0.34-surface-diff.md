# The Core-Compute evaluator (`Fuaran.Core.DataFrame` 0.34.0) beside the UI tier — surface diff

**Verdict (Phase 1896): the direct pin to the 0.34.0 evaluator is NOT taken. The core packages now
pin the substrate DIRECTLY — `Fuaran.Core.Wire`, `.Column`, `.DataFrame`, `.Tree`, `.OpStream` — at
0.21.0, the substrate the pinned UI tier (0.78.1) was built on. The evaluator raise travels with the
UI adapter's own UI-tier raise, as one act.** First measured in Phase 1894 (the .NET half, below);
re-measured and extended to the Fable half in Phase 1896, 2026-09-27.

## The question, and the premise Phase 1896 tested

`ServerEffect.RunQuery` calls `Fuaran.Core.DataFrame` directly. Until Phase 1896 the version arrived
through the UI tier; Phase 1894 found a direct 0.34.0 pin unsafe while the UI tier shared the graph.
Phase 1896 removes every UI-tier reference from the core packages, and its specification rested on
one premise: **once no core package references the UI tier, the conflict is gone.**

**That premise is false for this repository**, for two reasons that compound:

1. **One restore graph.** Central package management gives one version per package id, and the UI
   adapter — parked under `tests/` by Phase 1896, packaged by Phase 1897 — is compiled beside the
   core and references it. So the adapter's graph holds the core's substrate version whatever the
   core's own graph holds.
2. **Under Fable, the UI tier is compiled from SOURCE.** The .NET half of the question is binary
   compatibility — which Phase 1894 measured and which holds. The Fable half (the tier-parity leg,
   and the browser sample) is source compatibility, which is strictly stronger, and it fails.

## The .NET half — IL surface (Phase 1894, still true)

An IL reference scan (`System.Reflection.Metadata`): every `TypeRef` and `MemberRef` the 13
referencing assemblies (the eight UI-tier 0.78.1 assemblies and the five 0.21.0 substrate
assemblies that stay in the graph) make into `Fuaran.Core.{DataFrame,Column,Wire}`, resolved by
name and decoded signature against the 0.34.0 / 0.32.0 / 0.32.0 set. Probe validity both ways: the
0.21.0 set reports 0 problems, and a set with DataFrame 0.11.0 substituted reports 9.

| | 0.21.0 set | 0.34.0 / 0.32.0 / 0.32.0 set |
|---|---|---|
| TypeRefs into the three | 147 | 147 |
| MemberRefs into the three | 241 | 241 |
| Missing type / member / signature mismatch | 0 | **0** |
| Referenced unions whose case set changed | — | **2** |

`ColExpr` gained `Now of NowGrain` (tag 12) and `EvalError` gained `UnpinnedClock` (tag 8), both
appended. `EvalError` is matched in the UI tier only through a wildcard (safe). `ColExpr` is matched
EXHAUSTIVELY by the UI tier's decode-time admission walk for an `Expr` binding: run against 0.34.0, a
`Now` node falls into the default arm the compiler gave to `Col`, and is refused with a diagnostic
naming the wrong construct (`WRONG_TYPE`, "a `col` reference is not admitted …"). It fails closed,
but the report is false.

Phase 1896 confirmed the .NET half end to end: with the core pinned directly at DataFrame 0.34.0
(Column, Wire, Tree, OpStream at 0.32.0) and the UI tier at 0.78.1, the whole .NET suite was green —
128 / 39 / 46 / 214 cases, including the program wire conformance corpus.

## The Fable half — source compatibility (Phase 1896)

The tier-parity leg compiles the UI tier's packaged sources with Fable against whatever substrate the
graph resolves. With the 0.34.0 evaluator in the graph, UI-tier 0.78.1 does not compile:

- `Fuaran.UI.Ops` `JsonDecode.fs`: `Transform.Limit` now takes `Slot<int>` (a literal or a named
  parameter), so the tier's `Limit(n, offset)` construction is a type error.
- `Fuaran.UI.Renderer.Core` `BindingResolver.fs`: the evaluator's `NowGrain` cases `Date` and
  `Timestamp` capture the unqualified names the resolver uses for `Cell.Date` / `Cell.Timestamp`.
- `Fuaran.UI.OpStream.Abstractions` `HashChain.fs` (when `Fuaran.Core.OpStream` is raised with the
  rest of the substrate): a chain break's reason is a `ChainBreakReason`, not a string.

Every one is a source-level break a binary scan cannot see, and none is repairable from this
repository: they are in the UI tier's own sources.

## Is there a UI-tier release that clears both halves?

Checked against the public registry's index and each release's nuspec dependencies — all eight UI
ids are served at every version below:

| UI tier | Built on DataFrame | Sources compile against the 0.34.0 evaluator? | Program wire bytes unchanged? |
|---|---|---|---|
| 0.78.1 (pinned) | 0.21.0 | **no** (above) | yes |
| 0.79.0, 0.80.0 | 0.21.0 | no — same substrate as 0.78.1 | — |
| 0.81.0 | 0.22.0 | no — 0.22.0 predates `Slot` | — |
| 0.83.0 | 0.24.0 | built on a `Slot`-aware substrate; Fable compile not run | **no** — the .NET suite shows both moves below |
| 0.84.0 | 0.26.0 | built on a `Slot`-aware substrate; not run | not run; bracketed by 0.83.0 and 0.85.0, which both move |
| 0.85.0 (newest) | 0.28.0 | **yes** — the Fable leg compiled and ran 11/11 scenarios | **no** |

**No published release is built on DataFrame ≥ 0.34.0.** At 0.83.0 and at 0.85.0 two things move
that this phase may not move: the tree's canonical encoding (a `FileUpload` node now encodes its
`onSelect` slot as `"<closure>"`, so the scenario tree the corpus pins no longer matches the seed —
`coverage-floor-passthrough`), and the resolution of a clipboard payload bound to a state key
nothing has written (it is now refused where it was copied as the empty string). The first is a
program-wire byte; the second is a behaviour a test pins. Raising the UI tier is therefore its own
act, with its own evidence — not a side effect of pinning an evaluator.

## Consequence, and the route forward

- The core packages pin the substrate directly at **0.21.0**. That removes every transitive arrival
  (the point of Phase 1896's boundary test) without moving any version: the adapter's graph and the
  core's graph agree, the Fable leg compiles, and the corpus is byte-identical.
- The **evaluator raise travels with the UI adapter's UI-tier raise** (Phase 1897's adapter and
  consumer migration): one change-set that raises the UI family to a release whose sources compile
  against the target substrate, raises the direct substrate pins to match, and either keeps the tree
  encoding the corpus pins or moves it as a specification act.
- The mis-report above does not arise at 0.21.0 (there is no `ColExpr.Now`); it becomes a pinned,
  asserted behaviour in the adapter at the moment the adapter pairs a UI tier built below 0.34.0
  with the 0.34.0 evaluator — if it ever does.
- What the raise buys is measured, not assumed: `docs/runquery-benchmark.md`.

Re-run both halves against any candidate pair before a future raise: the IL scan for the .NET half
(a new case, member or signature on the referenced surface), and a Fable compile of the adapter
graph for the other.
