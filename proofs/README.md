# proofs/ — the F\* model of the shared bounded fold

`Fuaran.Program.Bounded` ships one interpreter. `BoundedActions.runBoundedActionWith`
(`src/Fuaran.Program.Bounded/BoundedActions.fs`) is the only place in this domain that
interprets an `Action`, and every placement runs it: the server driver in this package, the
browser client in `Fuaran.Program.Runtime`. One algebra, two placements, one fold.

This directory holds a model of that fold written in [F\*](https://www.fstar-lang.org/) and
four theorems about it, together with the leg that checks them and the seam that ties the
model back to the code that ships.

**"Formally verified" appears in this repository in exactly one place — the ladder below — and
it is spent on four laws and nothing else.** What is proved is narrow and stated precisely;
what is not is stated just as precisely, because an unstated exclusion reads, to whoever finds
it later, as a claim that failed. `proofs.json` at the repository root is the same ladder as
data, for a reader that is a program.

## The theorem

The model is `BoundedFold.fst`. It is hand-written, it names its F# counterpart above every
definition, and it has four headline lemmas.

### 1. `run_total` — the fold is defined on every arm, and one step is characterised

The `Action` union is closed and has fourteen arms. The fold names all fourteen, with no
wildcard arm, no partial match and no throw; termination is structural on the action, carried
by the `decreases` clause rather than by a depth counter. In F\* that much is the `Tot` effect,
and `handled` states it a second time by naming every constructor again — so a fifteenth arm
fails to compile here exactly as it fails to compile in the fold, which is the property worth
having.

The lemma adds the part typing does not give. For one step that is neither the composition arm
nor a call the placement ANSWERED: the placement's own accumulation comes back untouched, the
store is either unchanged or written at exactly one key the host-reserved predicate rejects,
and at most one client effect and at most one diagnostic are emitted.

The two exclusions are the interesting part of the statement rather than fine print.
Composition is excluded because it is characterised by law 3 instead. An answered call is
excluded because its outcome is the PLACEMENT's — see "the placement seam" below.

### 2. `run_no_closure` — no closure a carried action holds is ever invoked

Two actions that differ ONLY in the closures they carry produce identical outcomes and
identical placement accumulations.

This is the safety property `BoundedActions.fs` states at the top of its own file, turned into
a theorem. Emitted trees are bounded: the wire format cannot carry arbitrary closures, and the
decoder substitutes an inert placeholder for every closure slot. That is half the property.
The other half is that the interpreter never invokes one — and a fold that did could not
satisfy this lemma, because its answer would depend on what the closure was.

In the model the closure slots have an abstract type parameter with no elimination form, so a
fold that invoked one cannot be written here at all. That is stronger than it sounds and
weaker than it sounds: stronger, because the absence is structural rather than an omission
somebody has to keep noticing; weaker, because it is a property of the MODEL, and carrying it
back to the code is the differential host's job, not the prover's. The go-red case below is
what says that carry actually happens.

### 3. `chain_homomorphism` — `Chain` is a composition, not a fifteenth special case

Running the concatenation of two operation lists equals running the first and then the second
against the store the first left, with the effect lists and the diagnostic lists concatenated
in order and the placement threaded through.

This is `DECISIONS.md` D7's splice property — a nested call sees the writes before it and is
seen by the writes after it — stated as an equation instead of as a sentence. It is why an
answer is folded IN PLACE rather than reported for later, and it is the law that would break
first if the `Chain` arm ever stopped threading the store.

`chain_action_homomorphism` is the same statement at the action level, which is the form the
`Chain` arm is read in.

### 4. `reserved_untouched` — host-reserved keys are not writable from a tree

The store the fold returns agrees with the store it was given at every host-reserved `State`
key. The only write the fold performs is the `SetState` arm's, and that arm refuses a reserved
key before reaching it.

This is the property multi-tenant hosting of an untrusted tree rests on, alongside law 2:
bounded code plus a closed host namespace. It carries one side condition, about the placement
seam, and `inert_preserves_reserved` discharges it for the two placements that run no
handlers — which is what keeps the theorem from being a conditional nobody has met.

## What the model does NOT own

Eight host-supplied pure functions are AXIOMS: total arrows the model takes as parameters
rather than definitions it writes.

| Axiom | Production |
|---|---|
| `resolve_jval` | `BindingResolver.resolveJVal`, with `JValObj.toObj`'s lowering folded in |
| `resolve_scalar` | `BindingResolver.resolveScalarText` |
| `i18n_has` / `resolve_text` | the i18n catalogue probe and `BindingResolver.resolveTextSource` |
| `sanitize_url` | `Sanitize.sanitizeUrl`, the tree wire specification's renderer URL floor |
| `is_reserved` / `reserved_prefix` | `StateKeys.isHostReserved` and the namespace it names |
| `route_path` | `ActionInvocation.routePath`, the log-safe route projection |

Modelling any of them would introduce a second implementation free to disagree with the
shipped one. The theorems are quantified over EVERY total arrow, which is the honest
statement: nothing proved here depends on what any of them answers, only on the fold composing
them where it does. The differential host wires each one to production's own implementation,
so the only thing that can disagree in the comparison is the fold.

**The placement seam is the fifth thing the model does not own, and it is the load-bearing
one.** `HandlerArm.Answer` decides what a call action MEANS at a placement; the model proves
only that WHERE a call is recognised is the fold, at every depth, and that an answer is
threaded in place. An arm returns a store, effects and diagnostics of its own, and the fold
neither constrains nor inspects them — which is why `run_total` names the answered case and
excludes it rather than quietly covering it, and why `reserved_untouched` carries the seam's
obligation as a stated hypothesis.

## The gap between model and production — the claims ladder

A proof about a model is a claim about the code only if something runs the two side by side.
`tests/Fuaran.Program.Parity.Tests/ProofOracleTests.fs` is that something: it runs the
EXTRACTION of `BoundedFold.fst` beside `BoundedActions.runBoundedActionWith` and requires the
store, the effect list and the diagnostics to agree — all three at once, because a fold that
got the store right and the diagnostics wrong is still a fold that disagrees.

Four rungs, and the distance between them is the point.

### Proved

1. **Totality over the closed union** — `run_total`, plus the `Tot` effect and the structural
   `decreases`.
2. **No closure invocation** — `run_no_closure`.
3. **`Chain` is the fold's homomorphism** — `chain_homomorphism`.
4. **Host-reserved keys untouched** — `reserved_untouched`, with `inert_preserves_reserved`
   discharging its hypothesis for the handler-free placements.

No `admit`, no `assume`. `check.ps1` passes `--report_assumes error`, so either would fail the
leg rather than quietly weaken a theorem, and that flag is not a strictness preference to be
relaxed later.

### Differentially tested

The extracted model agrees with production over two corpora and one seam:

- **the conformance corpus's driver-semantics family** — every scripted event resolved to the
  `Action` the trust boundary would hand the fold, run through both, threading the store from
  step to step;
- **an arm-complete corpus** naming all fourteen arms and every refusal path inside the three
  arms that have one (a reserved key, an unresolved `valueFrom`, an unsafe route, an
  unresolved route, an unwritten-`State` clipboard payload, a missing i18n key, a call
  declaring its own result target);
- **an answering placement**, including an answered call spliced between two other operations
  inside a chain, so the seam is exercised rather than assumed.

And a **go-red case**: a fold that INVOKES the closure a `Call` carries, committed deliberately,
which the comparison is asserted to fail on. A differential that cannot be made to lose is not
evidence of anything.

`check.ps1` step 6 asserts a CASE COUNT and not only an exit code, because an Expecto filter
that matches nothing prints a green.

### Assumed, and stated

- **The eight axioms above.** Total, pure, and the same answer for the same store. Mitigated
  by the differential wiring them to production's own implementations.
- **The placement seam.** An arm's store, effects and diagnostics are its own.
- **The toolchain.** The extractor and the F# compiler are trusted. The theorem is about the
  model; what runs in the differential is the extraction, and nothing verifies that extraction
  preserves semantics. Mitigated by the differential running the EXTRACTION, and by step 4's
  byte-diff, which holds the committed file identical to what the prover emitted.

### Not claimed

- **The durable journal**, **the per-connection session cells**, and **the browser
  placement's loop above the fold**. All three are placement concerns, and the fold is
  placement-neutral by construction; a model that reached into one would be modelling a
  placement and calling the result a property of the interpreter.
- **The per-interaction resource budget.** Bounded CODE and bounded COST are the two halves of
  running an untrusted tree on shared infrastructure, and only the first is proved here. The
  budget is the driver's. Reading this document as covering it is the more dangerous of the
  two available mistakes.

## Running it

```powershell
pwsh ./proofs/check.ps1              # the whole leg, once
pwsh ./proofs/check.ps1 -Runs 3      # three cold checks with --quake 3, as CI runs it
pwsh ./proofs/check.ps1 -SkipHost    # the proof half only; no solution build needed
```

Six steps, each refusing rather than warning: resolve the pinned prover, CHECK, EXTRACT,
BYTE-DIFF against the committed `oracle/*.fs`, BUILD the oracle project, RUN the differential
host.

**The toolchain is a large one-off download and is NOT a build dependency.** Nothing in
`run.ps1`, `dotnet build` or the ordinary CI matrix needs a prover — the extractions are
committed precisely so a contributor with no interest in proofs never installs one. `check.ps1`
is the only thing that does, and it fetches the pinned release itself into a gitignored
`proofs/.fstar/`, verifying its SHA-256 against `fstar-pin.json` and refusing to proceed on a
mismatch.

**`oracle/BoundedFold.fs` is generated — do not edit it.** Step 4 byte-compares it against a
fresh extraction, so a hand edit is a change the leg refuses rather than one it absorbs. When
the model changes legitimately, the two files move in the same commit and the script prints the
copy command.

### Why the pin, and why three runs

F\* releases weekly, and a proof is a claim about a specific prover. Proof hints no longer
exist — `--record_hints` and `--use_hints` were removed — so the usual way of pinning an SMT
search is unavailable. Three things stand in: the PIN (`fstar-pin.json` records the release,
its hash, and the bundled Z3 version, so the solver is pinned too), a MARGIN on `--z3rlimit`
well above what any query here needs, and `--quake`, which re-runs every query under different
seeds and fails unless it succeeds every time. `-Runs 3` repeats the whole check from a cold
cache three times with `--quake 3` — nine seeds per query — which is what the `proofs` CI job
does on `windows-latest`.

Cold every run is deliberate: a warm `.checked` file is the prover telling you it already
believed this, which is the one thing a repeat run exists not to take on trust.

### The `Prims` shim

The F# backend of the F\* extractor ships no runtime library — it emits code referencing a
`Prims` module and leaves producing it to the consumer. `oracle/Prims.fs` is that whole
runtime, and its size is the point: five names, nothing that could make the oracle agree with
production for a reason other than the model being right. The set is not chosen — it is exactly
what the extraction references, and step 4 re-derives it on every run, so a model that reached
for a further name would fail the byte-diff rather than silently compile against a shim someone
widened.

## Method precedent

The shape of this directory — a pinned release bundling Z3, `check.ps1` as the whole leg, an
oracle project holding the extracted model plus a `Prims` shim, a separate CI job, and a README
whose claims ladder is the only place "formally verified" appears — is the one the
[`fuaran-core`](https://github.com/Fuaran-Core/fuaran-core) repository established for its own
kernel algebras. Its findings are inherited here rather than rediscovered: the weekly release
cadence makes the pin load-bearing, the absence of hints makes `--quake` the reproducibility
tool, the F# backend ships no runtime, and the emitted layout needs `--strict-indentation-` on
the oracle project alone.

## Adding a model

One entry in `check.ps1`'s `$modules`: its source, its committed extraction, the Expecto list
that is its differential host, and the case-count floor that list declares. Nothing else in
that file names a module. Append the new extraction to `oracle/`'s project file, and append the
new claims to `proofs.json` — one manifest, never a second.
