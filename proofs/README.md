# proofs/ — the F\* models of the bounded path

`Fuaran.Program.Bounded` ships one interpreter and one budget, and running an untrusted
generated tree on shared infrastructure needs both. `BoundedActions.runBoundedActionWith`
(`src/Fuaran.Program.Bounded/BoundedActions.fs`) is the only place in this domain that
interprets an `Action`, and every placement runs it: the server driver in this package, the
browser client in `Fuaran.Program.Runtime`. One algebra, two placements, one fold. `Budget`
(`src/Fuaran.Program.Bounded/Budget.fs`) is what prices the tree and the cascade, and the
driver is what refuses them.

This directory holds a model of each, written in [F\*](https://www.fstar-lang.org/), together
with the leg that checks them and the seam that ties each model back to the code that ships.

- **[The bounded fold](#the-fold-theorem)** — `BoundedFold.fst`, four theorems (Phase 1715).
  Bounded CODE: a generated tree running through this fold has no arbitrary-code surface.
- **[The interaction budget](#the-budget-theorem)** — `Budget.fst`, five theorems (Phase 1716).
  Bounded COST: a generated tree cannot be priced cheaper than it is, and a breach changes
  nothing.

**"Formally verified" appears in this repository in exactly one place — the ladders below — and
it is spent on nine laws and nothing else.** What is proved is narrow and stated precisely;
what is not is stated just as precisely, because an unstated exclusion reads, to whoever finds
it later, as a claim that failed. `proofs.json` at the repository root is the same ladders as
data, for a reader that is a program.

## The fold theorem

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
  running an untrusted tree on shared infrastructure, and only the first is proved by
  `BoundedFold.fst`. Nothing about the budget follows from any law above, and reading this
  section as covering it would be the more dangerous of the two available mistakes. It is a
  separate model with a separate claim — [the budget theorem](#the-budget-theorem) below — and
  the two are joined by nothing but the file they sit beside.

## The budget theorem

WS6.1(c) is stated as "interpreter budget monotonicity / no-closure-invocation". The fold
theorem above proves the no-closure half. `Budget.fst` proves the budget half, on the
arithmetic that decides whether a tree is admitted at all.

The subject is `Budget.satAdd` / `satMul`, `Budget.actionCascadeCost`, `Budget.treeCost`, and
the G2 stage of `BoundedDriver.step` (`BoundedDriver.fs:201-241`) that consumes them. Five
headline lemmas.

### 1. `sat_monotone` — the saturating arithmetic cannot make a tree look cheap

Both operations are monotone in both arguments, neither answers below zero, neither answers
above the bound, and below the bound both are ordinary arithmetic.

This is the clause `Budget.fs`'s own comment stakes the budget on: a cost is a budget
comparand, and an overflow that wrapped NEGATIVE "would read as cheap and admit the very tree
the budget exists to refuse". Monotonicity is the other half of the same thought — a bigger
tree must not price smaller.

The bound is a PARAMETER of the model, not `Int32.MaxValue` baked in, so the lemma holds for
every bound at all and nothing proved here is an artefact of the width production happens to
have. See "the `Int32` bound" below for what that costs.

### 2. `treecost_exact_below_ceiling` — a within-budget answer is the exact cost

When the walk's answer is at or below the ceiling it equals what the same walk with no ceiling
would have returned.

`treeCost` stops descending the moment the cost passes the ceiling, which is what stops the
function whose job is to bound a tree's cost from becoming the unbounded work it exists to
refuse. This lemma is the price of that optimisation being zero in the case where the number is
used AS a number — the session records it as `NodeCount` and prices later events against it, so
"not more than the ceiling" would be a materially weaker contract than the one the field
carries.

### 3. `treecost_strict_above_ceiling` — the early stop never loses a breach

When the exact cost is above the ceiling, the walk's answer is above the ceiling too.

This is the direction the budget actually rests on. An optimisation that could turn a refusal
into an admission would not be an optimisation, and the asymmetry between this law and the one
above is deliberate: above the ceiling the answer is only ever "more than this", and that is
all a refusal needs.

### 4. `treecost_terminates` — the walk terminates, and the ceiling is its fuel

The walk terminates on every finite tree — the `decreases` clause on `walk`, discharged where
it is written rather than asserted afterwards — and the number of nodes it visits is bounded
BOTH by the size of the tree and by `ceiling + 1`.

The second bound is the interesting one, and it is `treeCost`'s own comment made checkable: a
tree ten thousand times over budget costs the same as one marginally over. It holds because
every node costs at least one, so every iteration moves the accumulator at least one closer to
the ceiling. Its side condition — that the ceiling is below the saturation bound — is not a
technicality: at `InteractionBudget.unlimited` the two coincide, the accumulator saturates at a
value the guard still admits, and walking the whole tree is the intended behaviour rather than
a bound anyone wanted.

### 5. `breach_pure` — a breach mutates nothing

A breached step returns the session it was given — not an equal one, THE one — with no patches,
no effects, and a named refusal carrying its reason.

"Never a throw" is carried by the `Tot` effect rather than by a lemma: the model has no
exceptions, so the alternative cannot be written. And the lemma is quantified over EVERY
admitted branch, which is what makes it a statement about the gate rather than about one
driver: whatever the driver would have done to the session had the budget admitted the event, a
breach does none of it.

### What the budget model does NOT own

Three things, and they are different in kind.

| Not owned | Why |
|---|---|
| **The saturation bound** | `Int32.MaxValue` is a parameter. F\*'s integers are unbounded, so the .NET width is something the host supplies and the theorems are quantified over. |
| **The per-node classification** | Which `NodeKind` is data-bearing, and which of its fields carries the data, reaches the model as a `cost_shape` the differential host projects. `NodeKind` has scores of arms and `nodeCost` reads four of them; a model that carried the union would be modelling the tree vocabulary. |
| **The admitted branch of `step`** | What the driver does with an event the budget admits is the fold theorem's subject and the renderer's. Modelling it here would be modelling it twice. |

**This is what "discharged at the combinator layer" means, and what remains sampled.** The
arithmetic, the walk, the stack order, the early stop and the gate are PROVED, for every tree
and every bound. Which kinds are weighted and by what is SAMPLED — the differential compares
production and the model over a corpus, so a production change that started weighting a fifth
kind would be caught only once the host's projection gained the kind too. The line is drawn
where it is because the arithmetic is where a defect is silent and the classification is where
it is loud: a mis-weighted kind prices visibly wrong on the first tree that contains one, and a
wrapping add prices wrong only on the trees nobody tries.

### The budget's claims ladder

#### Proved

1. **Saturating arithmetic is monotone and non-negative** — `sat_monotone`.
2. **An answer within the ceiling is exact** — `treecost_exact_below_ceiling`.
3. **An answer above the ceiling stays above it** — `treecost_strict_above_ceiling`.
4. **The walk terminates, bounded by the tree AND by the ceiling** — `treecost_terminates`,
   with the `Tot` effect and the `decreases` clause carrying termination itself.
5. **A breach mutates nothing** — `breach_pure`.

No `admit`, no `assume`; `--report_assumes error` is on for this module exactly as it is for
the fold.

#### Differentially tested

The extracted model agrees with production over three corpora:

- **the trees the bounded driver's own suite drives**, priced at every ceiling around their own
  exact cost;
- **generated trees** — fans, chains, every data-bearing kind, a payload at the counting cap,
  a payload past it, and a chart whose multiply saturates — at the same ceilings. The
  comparison is of the INTEGER and not the verdict, which is what says the model kept
  production's explicit stack in production's push order: above the ceiling the answer depends
  on which nodes were visited first;
- **the G2 gate itself** — `BoundedDriver.step` against the model's `step`, comparing the
  refusal's reason verbatim and requiring the session to come back by reference.

And a **go-red case**: a walk that accumulates with .NET's ordinary wrapping `+` instead of the
saturating add, committed deliberately, which the comparison is asserted to report AND whose
ceiling comparison is asserted to come out the wrong way — the tree admitted rather than
refused. That second assertion is the point: a divergence that was merely a different number
would not demonstrate the defect this law exists to exclude.

#### Assumed, and stated

- **The lazy-payload cap.** Production prices a `Binding.Static` payload with `Seq.truncate`
  over a `seq`, which may be lazy and may be unbounded. The model is over a LIST with a
  counting cap, so what is proved is that the cap is applied and what it bounds — never that a
  `seq` is finite. Mitigated by the differential, whose corpus includes a payload at the cap
  and one past it.
- **The `Int32` bound.** The model is over unbounded integers; production is over `Int32`,
  with the ceiling's upper limit at `Int32.MaxValue`. The two coincide on every input
  production can be handed — an `int64` sum and an `int64` product of two `Int32`s both fit
  with room to spare, which is exactly what production computes before narrowing — so the model
  is over a WIDER domain than production rather than a different one. `actionCascadeCost` is
  the one function with no saturation at all, in the model or in production: what bounds it
  there is the `Int32` range, and that is this rung and not the proved one.
- **The toolchain**, on the same terms as the fold theorem's.

#### Not claimed

- **Which kinds are data-bearing, and by what they are weighted.** Sampled, per the table
  above.
- **That the budget's ceilings are the RIGHT ceilings.** `MaxActions` and `MaxNodes` are
  policy. Nothing here says a number is well chosen, only that the tree is priced correctly
  against whatever number is set.
- **Wall-clock cost.** The budget is step- and size-based on purpose, so it bounds work and not
  time. A tree within budget can still be slow on a slow machine, and no law here says
  otherwise.

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

**Every `oracle/*.fs` but `Prims.fs` is generated — do not edit one.** Step 4 byte-compares
each against a fresh extraction, so a hand edit is a change the leg refuses rather than one it
absorbs. When a model changes legitimately, its two files move in the same commit and the
script prints the copy command. `Budget.fs` needed no addition to the `Prims` shim below: it
uses `string_of_int`, which was already there, and native operators on `Prims.int`.

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
