# proofs/ — the F\* models of the bounded path

`Fuaran.Program.Bounded` ships one interpreter and one budget, and running an untrusted
generated tree on shared infrastructure needs both. `BoundedActions.run`
(`src/Fuaran.Program.Bounded/BoundedActions.fs`) is the only place in this domain that
interprets an action — through a domain's witness since Phase 1896 — and every placement runs
it: the server placement, and at the UI witness the bounded driver and the browser client (the
UI adapter's `BoundedActions.runBoundedActionWith` is `run` at that witness). One algebra, every
placement, one fold. `Budget`
(`src/Fuaran.Program.Bounded/Budget.fs`) is what prices the tree and the cascade, and the
driver is what refuses them.

This directory holds a model of each, written in [F\*](https://www.fstar-lang.org/), together
with the leg that checks them and the seam that ties each model back to the code that ships.

- **[The bounded fold](#the-fold-theorem)** — `BoundedFold.fst`, four theorems (Phase 1715),
  restated over the generic tier's ACTION VIEW and re-proved there before the generic core is
  written (Phase 1898; `DECISIONS.md` D14 and D18). Bounded CODE: a generated tree running through
  this fold has no arbitrary-code surface.
- **[The interaction budget](#the-budget-theorem)** — `Budget.fst`, five theorems (Phase 1716).
  Bounded COST: a generated tree cannot be priced cheaper than it is, and a breach changes
  nothing.

**"Formally verified" appears in this repository in exactly one place — the ladders below — and
it is spent on the laws in the two ladders and nothing else** — the fold's four, each stated over
the generic view and again at the UI witness, and the budget's five. What is proved is narrow and
stated precisely;
what is not is stated just as precisely, because an unstated exclusion reads, to whoever finds
it later, as a claim that failed. `proofs.json` at the repository root is the same ladders as
data, for a reader that is a program.

## The fold theorem

The model is `BoundedFold.fst`. It is hand-written, it names its F# counterpart — or, for the
generic tier, the `DECISIONS.md` D18 contract member — above every definition, and since Phase 1898
it has two layers in one file.

**The generic tier** is a model of the fold the generic core runs (`BoundedActions.run witness`,
which Phase 1896 wrote against this model — D14's "model first"): one `match` over the four shapes
of D18's `ActionView` — `Sequence`, `Assign`, `Call`, `Leaf` — parameterised by a WITNESS record
holding the fold-read members of `ProgramWitness` (`View`, `Lower`, `Describe`, `Resolve`,
`IsReserved`, `ReservedPrefix`). Its four theorems are over the view and quantified over every
witness. **The UI witness** is today's fourteen-arm `Action` union seen through that view — `ui_view`
is the adapter's total `View`, `ui_lower` its `Lower` — and `run` is the generic fold at that witness
with the exact signature Phase 1715 gave it. Phase 1715's five theorems are re-proved there, each as a
corollary of the generic one, keeping their names and their statements. The differential host runs
`run`, unchanged; the claim that carries is that the fourteen arms seen through the view are the
fourteen arms.

### Every parameter of the model is an assumption

The generic fold takes one witness and one placement arm, and each arrow is something the theorems
assume rather than prove. Stated once here, because a parameter nobody wrote down is the defect D4
warned about.

| Witness member | D18 | Assumed to be | Leaned on by |
|---|---|---|---|
| `w_view` | `ActionWitness.View` | a TOTAL arrow, taken to exhaustion. The model's view is a tree, so the obligation that the F# `View`, applied repeatedly, unfolds a finite tree is carried by this field's type: a witness whose `View` put an action inside its own `Sequence` cannot be written here. `ui_view` discharges it by being accepted as `Tot`. | every theorem — each is about `fold` over a finite view |
| `w_lower` | `ActionWitness.Lower` | a total arrow whose RESULT TYPE is K3: at most one effect, or a refusal, or a decline, and no store. A leaf that wrote the store or emitted a list cannot be expressed. | `fold_total`, `fold_reserved_untouched` |
| `w_describe` | `ActionWitness.Describe` | a total arrow; the diagnostics carry its answer verbatim | the no-closure half, through `same_shape` |
| `w_resolve` | `ExprWitness.Resolve` | a total pure arrow, the same answer for the same store; nothing depends on WHAT it answers | `fold_total`, `fold_reserved_untouched` |
| `w_is_reserved`, `w_reserved_prefix` | `StoreWitness.IsReserved` / `ReservedPrefix` | a total predicate and a constant (K5); `fold_reserved_untouched` is quantified over every such predicate | `fold_total`, `fold_reserved_untouched` |
| `answer` | `HandlerArm.Answer` | what a call MEANS at this placement; opaque, and its store, effects and diagnostics are its own | `fold_total` names the answered case and excludes it; `fold_reserved_untouched` carries `arm_preserves_reserved` as a hypothesis |

**The store is modelled concretely, on purpose.** D18 §3.4 makes the store abstract behind
`StoreWitness.Assign`; the model keeps a keyed association list and `write`, because
`fold_reserved_untouched` is a theorem ABOUT what is written, and a theorem about writes to an
abstract store would be a theorem about an obligation nobody had stated. What the model claims of a
domain's `Assign` is therefore K4 — one keyed state channel where an assignment writes one key — and
the differential host's projection of the domain store onto that channel is where it is checked.

### 1. `fold_total` / `run_total` — the fold is defined on every shape, and one step is characterised

The view has four shapes and the fold names all four, with no wildcard arm, no partial match and no
throw; termination is structural on the view, carried by the `decreases` clause rather than by a
depth counter. In F\* that much is the `Tot` effect, and `handled_view` states it a second time by
naming every constructor again — so a fifth shape fails to compile here exactly as it fails to
compile in the fold. At the UI witness the same holds of the fourteen arms: `ui_view` names each
with no wildcard, and `handled` names them once more. This is the exhaustiveness check D18 moves
into the adapter's `View`, checked here by the same means.

The lemma adds the part typing does not give. For one step that is neither `Sequence` nor a call
the placement ANSWERED: the placement's own accumulation comes back untouched, the store is either
unchanged or written at exactly one key the reserved predicate rejects, and at most one effect and
at most one diagnostic are emitted. For a leaf that is K3 made a theorem: whatever `Lower` answers,
this is the most it can do. `run_total` is `fold_total` at the UI witness, with Phase 1715's
statement word for word.

The two exclusions are the interesting part of the statement rather than fine print. Composition is
excluded because it is characterised by law 3 instead. An answered call is excluded because its
outcome is the PLACEMENT's — see "the placement seam" below.

### 2. `fold_blind` / `run_action_blind` / `run_no_closure` — no closure a carried action holds is ever invoked

D18 makes this an OBLIGATION ON THE WITNESS: the core holds no closures, and only a witness's `View`
or `Lower` could reach one. The model states it in exactly that shape, in three steps.

- **The core's half, held unconditionally — `fold_blind`.** Two views of the SAME SHAPE — the same
  constructors at every node, the same keys, values, expressions, endpoints and target flags, the
  carried actions describing alike and, at a leaf, lowering alike at every node id and store — fold
  to IDENTICAL outcomes and placements. The fold reads an action only through the witness and the
  shape the witness gave it, so it cannot have applied anything the witness did not; and in this
  model there is nothing to apply, since the view carries no closure and the action type has no
  elimination form the fold could reach for.
- **The witness's half, as a precondition — `blind_to`.** A witness is blind to a relation on
  actions when its `View` sends related actions to same-shaped views. `run_action_blind` is the
  theorem conditional on it: under any relation the witness is blind to, related actions run to
  identical outcomes. **This is the conditional theorem**, and the condition is the adapter's to
  meet.
- **The obligation discharged for the UI witness — `ui_blind_to_closures`.** Under
  `same_but_closures` (two actions differing ONLY in the closures they carry), `ui_view` produces
  same-shaped views: `describe_ignores_closures` and `ui_lower_ignores_closures` do the leaves, and
  one induction does `Chain`. So `run_no_closure` — Phase 1715's statement, word for word — is
  `run_action_blind` at a witness whose obligation is met, and is again unconditional.

The rest of Phase 1715's reading still holds. Emitted trees are bounded: the wire format cannot carry
arbitrary closures, and the decoder substitutes an inert placeholder for every closure slot. That is
half the property; the other half is that the interpreter never invokes one, and a fold that did
could not satisfy this lemma. It is a property of the MODEL, and carrying it back to the code is the
differential host's job, not the prover's — the go-red case below is what says that carry happens.

### 3. `sequence_homomorphism` / `chain_homomorphism` — `Sequence` is a composition, not a fifth special case

Running the concatenation of two operation lists equals running the first and then the second
against the store the first left, with the effect lists and the diagnostic lists concatenated in
order and the placement threaded through — now over `VSequence`, for every witness.

This is `DECISIONS.md` D7's splice property — a nested call sees the writes before it and is seen by
the writes after it — stated as an equation instead of as a sentence. It is why an answer is folded
IN PLACE rather than reported for later, and it is the law that would break first if the `Sequence`
arm ever stopped threading the store. `sequence_action_homomorphism` is the same statement at the
view level; `chain_homomorphism` is the action-level statement at the UI witness (Phase 1715's
`chain_action_homomorphism`, renamed to the headline), through `ui_view_list_app` — viewing a
concatenation is concatenating the views.

### 4. `fold_reserved_untouched` / `reserved_untouched` — reserved keys are not writable from a tree

The store the fold returns agrees with the store it was given at every reserved key. The only write
the fold performs is the `Assign` shape's, and that shape refuses a reserved key before reaching it;
a leaf has no store to return.

This is the property multi-tenant hosting of an untrusted tree rests on, alongside law 2: bounded
code plus a closed host namespace. It carries one side condition, about the placement seam
(`arm_preserves_reserved`, now over the witness's own predicate), and `inert_preserves_reserved`
discharges it — under EVERY predicate — for the placements that run no handlers, which is what keeps
the theorem from being a conditional nobody has met. `reserved_untouched` is the UI corollary, at
the host's `isHostReserved`.

### Phase 1715's lemmas, one by one

Nothing is dropped silently. Each Phase-1715 name is either kept with its statement, or restated
under a new name with the old one kept as its corollary.

| Phase 1715 | Phase 1898 |
|---|---|
| `run_total` | kept; a corollary of `fold_total` at `ui_witness` |
| `run_no_closure` | kept; `run_action_blind` at `ui_witness` with `ui_blind_to_closures` discharging the obligation |
| `run_no_closure_list` | restated as `fold_blind_list` (the generic list half); the UI list induction is `ui_view_same_shape_list` |
| `describe_ignores_closures` | kept; now one of the two lemmas that discharge the leaf cases of the obligation |
| `chain_homomorphism` (list level) | restated as `sequence_homomorphism` over the view |
| `chain_action_homomorphism` | restated as `chain_homomorphism` — the action-level statement is the headline at the UI witness |
| `reserved_untouched` | kept; a corollary of `fold_reserved_untouched` at `ui_witness` |
| `reserved_untouched_list` | restated as `fold_reserved_untouched_list` |
| `arm_preserves_reserved` | kept, now taking the reserved predicate rather than the UI axioms |
| `inert_preserves_reserved` | kept, now quantified over every predicate |
| `handled`, `answered`, `at_most_one`, `same_but_closures` (+ `_opt`, `_list`), `write_preserves_other`, `app_assoc`, `app_nil` | kept unchanged |

Retired: none.

## What the model does NOT own

Eight host-supplied pure functions are AXIOMS of the UI witness: total arrows the model takes as
parameters rather than definitions it writes.

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
neither constrains nor inspects them — which is why `fold_total` names the answered case and
excludes it rather than quietly covering it, and why `fold_reserved_untouched` carries the seam's
obligation as a stated hypothesis.

**And the generic witness is the sixth**, stated member by member in the table above. What the
generic theorems say of a domain is conditional on its witness being what that table assumes; the
UI witness is the one instance where every row is discharged in the model itself, and the
differential is what says the discharged instance is the shipped one.

## The gap between model and production — the claims ladder

A proof about a model is a claim about the code only if something runs the two side by side.
`tests/Fuaran.Program.Parity.Tests/ProofOracleTests.fs` is that something: it runs the
EXTRACTION of `BoundedFold.fst` beside `BoundedActions.runBoundedActionWith` — the generic core
at the UI witness, through the UI adapter package `src/Fuaran.Program.UI/` — and,
since Phase 1896, the extraction's generic `run_action` beside `BoundedActions.run` at a non-UI
test witness, and requires the
store, the effect list and the diagnostics to agree — all three at once, because a fold that
got the store right and the diagnostics wrong is still a fold that disagrees.

Four rungs, and the distance between them is the point.

### Proved

Over the generic tier's view, for every witness (Phase 1898):

1. **Totality over the view** — `fold_total`, plus the `Tot` effect and the structural
   `decreases`. Unconditional.
2. **The fold is blind to everything but the view** — `fold_blind`, the core's half of
   no-closure-invocation. Unconditional.
3. **`Sequence` is the fold's homomorphism** — `sequence_homomorphism`. Unconditional.
4. **Reserved keys untouched** — `fold_reserved_untouched`. **Conditional** on the placement seam's
   `arm_preserves_reserved`, which `inert_preserves_reserved` discharges for every predicate for the
   handler-free placements.
5. **No closure invocation, for any witness that meets its obligation** — `run_action_blind`.
   **Conditional** on `blind_to`: the witness's `View` sends related actions to same-shaped views.

At the UI witness (Phase 1715's five, each a corollary of the generic theorem above it):

6. **Totality over the closed union** — `run_total`.
7. **No closure invocation** — `run_no_closure`, with `ui_blind_to_closures` discharging the
   obligation, so the theorem is unconditional again.
8. **`Chain` is the fold's homomorphism** — `chain_homomorphism`.
9. **Host-reserved keys untouched** — `reserved_untouched`, conditional on the same seam
   hypothesis as 4, discharged the same way.

**The conditional theorems, named:** `fold_reserved_untouched` and `reserved_untouched` (on the
placement seam); `run_action_blind` (on the witness's blindness). Every other theorem is quantified
over the witness's members as total arrows — the table under "Every parameter of the model is an
assumption" — and on nothing else. A domain instantiating the generic tier inherits 1–3
outright, 4 once its arm preserves its reserved keys, and 5 once it proves — or differentially
tests, which is what Phase 1896's test witness does — that its `View` and `Lower` are blind to the
closures its actions carry.

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

**What the differential runs since Phase 1898 is the generic fold at the UI witness** — `run` is
`run_action (ui_witness ax)`, the fourteen arms seen through the view — so the same five cases that
tied Phase 1715's model to production now tie the view-level model to it, through the UI witness,
with no change to the host. That is the shape D18 §3.8 names: the oracle runs through the UI
adapter, which is where the fourteen arms live. Phase 1896 added the second differential, the
generic fold through a TEST witness (`tests/Fuaran.Program.Tests/ToyDomain.fs`, a domain with no UI
type in it) against the ported core, as a nested list inside the same host list, so step 6's case
count covers it; Phase 1897 moves the UI-arm differential with the adapter into its package. The
host's file citations move with the code in those commits.

### Assumed, and stated

- **The eight axioms above.** Total, pure, and the same answer for the same store. Mitigated
  by the differential wiring them to production's own implementations.
- **The generic witness's members** — `View` total and finite, `Lower` a leaf outcome, `Describe`
  and `Resolve` total and pure, `IsReserved` a total predicate — per the table under "Every
  parameter of the model is an assumption". Discharged in the model for the UI witness; for any
  other domain they are that domain's obligations.
- **The store is a keyed channel** (K4). `StoreWitness.Assign` is modelled as `write` on an
  association list, because `fold_reserved_untouched` is about what is written. A domain whose
  `Assign` did something other than write one key is outside the model. Mitigated by the
  differential host projecting the domain store onto that channel and comparing it key by key.
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
the G2 stage of `BoundedDriver.step` (`src/Fuaran.Program.UI/BoundedDriver.fs:203-242`,
the UI transport loop, in the UI adapter package since Phase 1897) that consumes them. Five
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

## The staging theorem

DECISIONS.md D8 chose two-phase staging for host-effect atomicity and states the law in prose:
nothing external runs in the plan phase; an uncommitted outcome equals the entry state EXCEPT
that `Performed` names exactly the prefix of staged host calls that ran; and `Performed` is
execution order. `Handler.run` (`src/Fuaran.Program.Server/Handler.fs`) keeps it with one `if`,
and `HandlerLoopTests` / `DurableInterpreterTests` pin instances of it. "At most a prefix"
quantifies over every staged sequence and every point of failure, which is what instances
cannot cover and what the next handler arm can silently break. `Staging.fst` proves it.

The subject is the plan phase (`runStage` / `runEffect` — the five effect arms and the
`Compute` arm), the phase boundary, the perform phase (`perform`) and the two outcome
constructors, modelled clause for clause with the PERFORMER abstract. Four headline lemmas and
one supporting clause.

### 1. `plan_pure` — nothing external runs in the plan phase

The plan phase's output is a function of the entry state, the program, the witness and the
registry's gate, policy and lookup alone: under every behaviour the performers could have, it
is EQUAL — the same staged list, the same store, the same diagnostics. Stated as an equation
rather than an inspection, because a plan that had invoked a performer could not be the same
under a performer that answers differently.

What makes it statable is the one place the model departs from production's shape. Production's
`HostFunctions` map goes from a name to a closure; the model goes from a name to an opaque
performer TOKEN (`r_lookup`) and, separately, from a token and an argument to a result
(`r_perf`). The plan phase captures the token; only the perform phase applies the behaviour.
In the differential host the token is production's own closure and the behaviour is
application, so nothing is lost in the split.

### 2. `residual_is_prefix` — on failure at position k, exactly the first k, and nothing else

When the plan completed and the performer fails at position `k` of the staged list — any list,
any position — the outcome's store is the ENTRY store, the handler is uncommitted, it carries
no patches, no notifications and no client effects, and `Performed` is exactly the first `k`
staged capabilities, in declaration order. Not "at most a prefix": the prefix, and which one.

### 3. `performed_in_order` — the general statement

For EVERY performer: when the plan completed, the handler commits exactly when every staged
call ran, and `Performed` is the plan phase's capabilities in stage order (on commit; nothing on
rollback) followed by the staged capabilities the performer answered, in DECLARATION order —
the first `ran` of them, where `ran` counts the calls answered before the first refusal. Lemmas
2 and 4 are corollaries read off this one, which is why it is the one `perform_spec` (the
whole behaviour of the perform phase, stated once by induction) feeds.

### 4. `commit_is_total_prefix` — on success, everything

When every staged call succeeds the handler commits, and `Performed` is the plan phase's
capabilities in stage order followed by the WHOLE staged list in declaration order.

### The supporting clause — `plan_halt_performs_nothing`

A handler that halted while planning rolls back to the entry store and reports nothing
performed, under every performer at once, because nothing reached the perform phase. This is
the plan-phase half of D8's "at most a prefix": the prefix is empty when the plan fails.

### What the staging model does NOT own

The handler is generic in its tree, bindings, values, ops, actions and client effects, and the
model keeps every one a type parameter; what production does WITH them reaches it through two
records of arrows, and both are the assumed rung:

- **the witness** — `LandQuery`, `Assign`, `IsReserved`, `ReservedPrefix`, the op stream's
  `Apply`, the shared fold's `runInert` (Phase 1715's subject, so opaque here), and the query
  evaluator. The evaluator and `LandQuery` arrive as ONE arrow, because one opaque composed with
  another is one opaque and the clause the handler owns is halt-or-land; the evaluator's error
  reaches the model already reduced to its discriminator, as `Handler.evalErrorKind` reduces it;
- **the registry** — the gate, the argument policy (with its defect already described), the
  lookup and the performer's behaviour.

The denial sink (`OnDenied`) is a unit-returning observer and is not modelled.

### The staging claims ladder

#### Proved

1. **Nothing external runs in the plan phase** — `plan_pure`.
2. **The residual is exactly the prefix that ran** — `residual_is_prefix`.
3. **`Performed` is execution order, for every performer** — `performed_in_order`, with
   `perform_spec` as the inductive core.
4. **Success performs the whole staged list** — `commit_is_total_prefix`.

No `admit`, no `assume`; `--report_assumes error` is on for this module exactly as it is for
the other two.

#### Differentially tested

The extracted model agrees with production over the staging cases the handler's own suites
drive — `HandlerLoopTests`' every-arm handler, its ordered handler, its landing slot, its
three-call half-performer, its plan-then-halt, its reads-too-early and its refused gate;
`DurableInterpreterTests`' refresh handler and its two-call handler — plus the plan-halt arms
those cases do not reach (an unregistered performer, a refused argument policy, a reserved
landing slot, an apply refusal, an unresolvable query). Each case runs with a SCRIPTED
performer that counts its invocations and refuses at one position, at every position of the
staged list and once with no refusal at all. Three things are compared: the two outcomes
(projected as the durable parity leg projects them), the two performers' logs of what they
were asked, and production's `Performed` against the performer's own log of what ran.

That third comparison is what the **go-red case** defeats: a performer that reports success for
a call it did not run. Production and the model still agree with each other — both believed
it — and the check that catches it is the claim against the log, which is the ground truth
every green case rests on. A comparison that could not lose would not be evidence.

#### Assumed, and stated

- **The performer is a function of the call.** The model's `r_perf` is pure, so a stateful
  production performer is represented by the verdicts it actually returned, in the order the
  perform phase asked for them. The perform phase asks each staged call once, in declaration
  order — a property the extraction inherits — so the differential's counter-driven performer
  exercises exactly the case the pure model cannot state, including a list that stages the same
  function twice.
- **The witness and the registry**, per the table above: what production does with a query, an
  op, a landing slot and a `Compute` stage is supplied to the model from production's own
  members, and the differential is over the extraction of everything else.
- **The toolchain**, on the same terms as the fold theorem's.

#### Not claimed

- **The durable journal.** D12's interpreter journals the one arm that reaches outside and
  replays over the journal; it CALLS `Handler.run` and does not fork it, so it sits above this
  theorem and is out of it, as it is out of the fold theorem's.
- **The indeterminate window D12 declines to close.** A step journaled as attempted and never
  decided may have happened and may not; the theorem says what `Performed` reports when the
  performer ANSWERS, and nothing about a performer that never does. That window is declared,
  not closed, and no proof here narrows it.
- **The performer's own effect.** What a host function does when invoked is the host's. The
  theorem bounds what the handler reports and when it stops asking; it cannot bound what an
  invocation did.

## Running it

```powershell
pwsh ./proofs/check.ps1              # the whole leg, once
pwsh ./proofs/check.ps1 -Runs 3      # three cold checks with --quake 3, as CI runs it
pwsh ./proofs/check.ps1 -SkipHost    # the proof half only; no solution build needed
```

Six steps, each refusing rather than warning: resolve the pinned prover, CHECK, EXTRACT,
BYTE-DIFF against the committed `oracle/*.fs`, BUILD the oracle project, RUN the differential
host. A module names the test project that hosts its differential: the fold's and the
budget's live in `Fuaran.Program.Parity.Tests`, the staging theorem's in
`Fuaran.Program.Server.Tests` beside the handler suites it re-declares — which means step 6 for
that module needs the conformance corpus the server suite loads at start-up
(`FUARAN_PROGRAM_SPEC`, or the sibling clone), exactly as `run.ps1` does.

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
uses `string_of_int`, which was already there, and native operators on `Prims.int`. `Staging.fs`
needed none either: `strcat` and the `Prims` type aliases are all it references.

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
