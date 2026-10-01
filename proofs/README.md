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
  written (Phase 1898; `DECISIONS.md` D14 and D18), and restated again over the view's fifth shape —
  the halting guard — before the port that added it (Phase 1967; D19), with a fifth theorem saying
  only a guard halts. Bounded CODE: a generated tree running through this fold has no arbitrary-code
  surface.
- **[The interaction budget](#the-budget-theorem)** — `Budget.fst`, five theorems (Phase 1716).
  Bounded COST: a generated tree cannot be priced cheaper than it is, and a breach changes
  nothing.
- **[Two-phase staging](#the-staging-theorem)** — `Staging.fst`, four theorems (Phase 1717),
  re-proved over a staged list that holds ops as well as host calls since a placement can register
  an op performer (Phase 1967), and restated over an `ApplyOps` arm that carries the op-channel
  guard and hands the performer the planned state, with three theorems for those beside the four
  (Phase 1974). Bounded RESIDUE: what a handler that reaches outside can leave behind is exactly the
  prefix of staged calls that ran.
- **[The effect gate](#the-effect-gate-theorem)** — `EffectGate.fst`, three theorems (Phase 1759),
  proved over the staging model. A CONSTRAINED boundary: policy before the effect, contract on
  return, against an arbitrary performer.

**"Formally verified" appears in this repository in exactly one place — the ladders below — and
it is spent on the laws in the four ladders and nothing else** — the fold's four, each stated over
the generic view and again at the UI witness, the budget's five, the staging theorem's four with
the op-channel guard's two and the performer's one beside them (Phase 1974), and the effect gate's
three. What is proved is narrow and
stated precisely;
what is not is stated just as precisely, because an unstated exclusion reads, to whoever finds
it later, as a claim that failed. `proofs.json` at the repository root is the same ladders as
data, for a reader that is a program.

## The three axes — which theorem names which member (Phase 1974)

Phase 1974 cuts `ProgramWitness` into three records (`DECISIONS.md` D20): a required **state** axis
(`StateWitness` — `Stream`, `Reach`, `AbsoluteTarget`, `Canonical`, `Diff`, and `View`, the
op-channel guard's classification), an optional **walk** axis (`WalkWitness` — `Nodes`, `Traverse`,
`Cost`, `QueryReaders`) and an optional **dispatch** axis (`DispatchWitness` — `Handlers`, `Events`,
`Resolve`, and the `Action`, `Expr`, `Store` and `Effect` sub-records). D14 says the model comes
first; this section is that restatement, written before a `.fs` file moved. It answers one question
per theorem: which members does it name, and so which axes must a witness fill for it to say
anything about that witness.

| Model | Theorems | Members the theorems name (model field → F# member) | Axis |
|---|---|---|---|
| `BoundedFold.fst` | `fold_total`, `fold_blind`, `run_action_blind`, `sequence_homomorphism`, `fold_reserved_untouched`, `fold_no_require_no_halt`, and their UI corollaries | `w_view` → `Action.View`; `w_lower` → `Action.Lower`; `w_describe` → `Action.Describe`; `w_resolve` → `Expr.Resolve`; `w_is_reserved` / `w_reserved_prefix` → `Store.IsReserved` / `ReservedPrefix`; `answer` → the placement's `HandlerArm` (not a witness member) | **dispatch** only |
| `Budget.fst` | `sat_monotone` | none — the saturating arithmetic | none |
| `Budget.fst` | `treecost_exact_below_ceiling`, `treecost_strict_above_ceiling`, `treecost_terminates` | `kids` → `Nodes.Children`; `cost_shape` → `Cost` (the host's projection of it) | **walk** |
| `Budget.fst` | `breach_pure` | the UI driver's `step` over `action_cascade_cost` → `Action.View` and the walk above | **walk + dispatch** (the UI adapter's driver) |
| `Staging.fst` | `plan_pure`, `residual_is_prefix`, `performed_in_order`, `commit_is_total_prefix`, `plan_halt_performs_nothing` | `w_apply` → `Stream.Apply`, `w_op_view` → `View` (**state**); `w_compute` → the fold over `Action` / `Expr` / `Store`, `w_query` → `Store.LandQuery`, `w_assign` → `Store.Assign`, `w_slot_refused` → `Store.IsReserved` / `ReservedPrefix` (**dispatch**) | **state**, with the dispatch members as hypotheses a dispatch-less witness discharges (below) |
| `Staging.fst` | `guard_holds_moves_nothing`, `guard_refusal_halts` (the op-channel guard, F-GUARD) | `w_apply`, `w_op_view` | **state** only |
| `Staging.fst` | `performer_handed_the_plan` (F-PERFORM) | `w_apply`, `w_op_view`, and the registry's `r_op_perform` | **state** only |
| `EffectGate.fst` | `gate_before_perform`, `gate_refusal_halts_run`, `policy_sufficient`, `return_contract` | the staging witness, quantified over every one, plus the registry | **state**, on the staging theorem's terms |

**A witness filling ONLY the state axis satisfies every theorem that names no dispatch member.**
Read off the table: the budget's walk theorems do not apply to it (it has no walk to price, and the
budget is never asked about it), the fold's five do not apply (it has no action, so no fold runs),
and what remains is the staging model, the op-channel guard's two, the performer's one and the
effect gate's three. Each of those is quantified over EVERY staging witness, and a state-only
composition is one of them with its dispatch members fixed as follows, by the composition and not by
the domain: `w_compute` is unreachable, because a handler under a witness with no dispatch axis
holds no compute stage — its action type has no values (`Nothing`), so the stage cannot be written;
`w_query` answers the refusal `no-binding-channel` for every query, before the query is evaluated;
`w_slot_refused` answers the same refusal for every landing slot; and `w_assign` is unreachable,
because the only path to it is a landing slot the plan phase has already refused. Under those four
the staging theorems' statements are unchanged — a plan that halts on a refused landing is a plan
that halts (`plan_halt_performs_nothing`), and nothing else in the statements names a binding — so a
dispatch-less witness inherits all of them, and the op-channel guard and the performer's state are
the two capabilities it gains.

**A witness filling state + walk satisfies, in addition, every theorem that names no handler, event,
store or effect** — the three walk theorems of the budget, whose only members are the structural
children and the per-node cost. Nothing else changes for it: `breach_pure` names the UI driver,
which is a dispatch fact, and the fold's laws name the action view.

**The dispatch axis carries the fold, and only the fold.** Every member the fold's laws name is on
it, and none of them names the state or the walk: the fold never touches the tree (K4 is a dispatch
fact — the binding store the fold writes is the dispatch axis's `Store`, not the state the ops apply
to). That is why the fold's theorems are restated over nothing here: their statements and proofs are
over the same witness record, which is now one axis's members rather than a slice of the whole.

**What the restatement moved, and why it is the state axis.** Two capabilities the third witness
found missing are on the state axis, because the thing they read is the state:

- **The op-channel guard (F-GUARD).** `View op = Require` makes an op a guard: it is resolved against
  the state AS OF THAT POINT IN THE PLAN through the op's own `Apply` — `Ok` holds and the state does
  not move, `Error reason` halts the effect with `reason` verbatim — and it is never staged and never
  performed. The fold's `Require` resolves against the dispatch axis's store and cannot see a tree;
  this one resolves against the tree and cannot see a store, and each is the right guard for the
  domain whose guards are over that thing.
- **The performer is handed the state (F-PERFORM).** `r_op_perform` answers the token and argument
  to stage FROM the state as of the op — the planned state with that op applied — and the op. The
  last edit's staged state is the plan's final state, so a tail that persists the state the plan
  produced is handed it rather than re-deriving it.

Both are clauses of the ONE `ApplyOps` arm the staging model already had; neither adds a stage kind,
an effect arm or a diagnostic (a guard's refusal is the `Failed (ApplyOps, reason)` an apply refusal
already was), which is why no wire member moves.

## The fold theorem

The model is `BoundedFold.fst`. It is hand-written, it names its F# counterpart — or, for the
generic tier, the `DECISIONS.md` D18 contract member — above every definition, and since Phase 1898
it has two layers in one file.

**The generic tier** is a model of the fold the generic core runs (`BoundedActions.run witness`,
which Phase 1896 wrote against this model — D14's "model first"): one `match` over the five shapes
of D18's `ActionView` — `Sequence`, `Assign`, `Call`, `Require` (the halting guard, Phase 1967),
`Leaf` — parameterised by a WITNESS record holding the fold-read members of `ProgramWitness`
(`View`, `Lower`, `Describe`, `Resolve`, `IsReserved`, `ReservedPrefix`) plus the core's own truth
test as an arrow (`w_is_true`, because the model's value type is abstract). Its four theorems are
over the view and quantified over every witness, and a fifth — `fold_no_require_no_halt` — says a
view with no guard never halts, which is what keeps every pre-1967 statement unchanged at the UI
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
| `w_is_true` | the core's own `jv = JBool true` — NOT a witness member in F# | a total predicate; an arrow here only because the model's value type is abstract, exactly as its store is concrete where the core's is abstract. Nothing proved depends on what it answers. The differential host wires it to that comparison; the UI witness fills it with the fail-closed constant, and `ui_view_no_require` proves it is never reached there | `fold_total` (the guard clause), `fold_no_require_no_halt` |
| `answer` | `HandlerArm.Answer` | what a call MEANS at this placement; opaque, and its store, effects and diagnostics are its own | `fold_total` names the answered case and excludes it; `fold_reserved_untouched` carries `arm_preserves_reserved` as a hypothesis |

**The store is modelled concretely, on purpose.** D18 §3.4 makes the store abstract behind
`StoreWitness.Assign`; the model keeps a keyed association list and `write`, because
`fold_reserved_untouched` is a theorem ABOUT what is written, and a theorem about writes to an
abstract store would be a theorem about an obligation nobody had stated. What the model claims of a
domain's `Assign` is therefore K4 — one keyed state channel where an assignment writes one key — and
the differential host's projection of the domain store onto that channel is where it is checked.

### 1. `fold_total` / `run_total` — the fold is defined on every shape, and one step is characterised

The view has five shapes and the fold names all five, with no wildcard arm, no partial match and no
throw; termination is structural on the view, carried by the `decreases` clause rather than by a
depth counter. In F\* that much is the `Tot` effect, and `handled_view` states it a second time by
naming every constructor again — so a fifth shape fails to compile here exactly as it fails to
compile in the fold. At the UI witness the same holds of the fourteen arms: `ui_view` names each
with no wildcard, and `handled` names them once more. This is the exhaustiveness check D18 moves
into the adapter's `View`, checked here by the same means.

The lemma adds the part typing does not give. For one step that is neither `Sequence` nor a call
the placement ANSWERED: the placement's own accumulation comes back untouched, the store is either
unchanged or written at exactly one key the reserved predicate rejects, at most one effect and at
most one diagnostic are emitted, and ONLY A GUARD HALTS — a `Require` step writes nothing and emits
nothing, and no other shape's step can come back halted (Phase 1967). For a leaf that is K3 made a
theorem: whatever `Lower` answers, this is the most it can do. `run_total` is `fold_total` at the UI
witness, with Phase 1715's statement word for word.

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

Running the concatenation of two operation lists equals running the first and — if it did not halt
— then the second against the store the first left, with the effect lists and the diagnostic lists
concatenated in order and the placement threaded through; a first half that halted is the whole
answer, and the second half never runs. Over `VSequence`, for every witness.

This is `DECISIONS.md` D7's splice property — a nested call sees the writes before it and is seen by
the writes after it — stated as an equation instead of as a sentence. It is why an answer is folded
IN PLACE rather than reported for later, and it is the law that would break first if the `Sequence`
arm ever stopped threading the store. The halting clause is the one thing Phase 1967's guard changes
in the ladder (D19): a guard is control structure, so it is the evaluator's, and this is the law it
touches. `sequence_action_homomorphism` is the same statement at the view level; `chain_homomorphism`
is the action-level statement at the UI witness (Phase 1715's `chain_action_homomorphism`, renamed to
the headline), through `ui_view_list_app` — viewing a concatenation is concatenating the views — and
it keeps its UNCONDITIONAL form, because `ui_view_no_require` says no UI arm views as a guard and
`fold_no_require_no_halt` says such a view never halts.

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

### 5. `fold_no_halting_shape_no_halt` / `fold_no_require_no_halt` / `ui_never_halts` — which shapes halt (Phase 1967, widened by Phase 1976)

A view with no guard, no selection and no repeat in it, at its root or inside a sequence, an arm or
a body at any depth, folds to an outcome that is not halted, under every witness and every placement
arm (`fold_no_halting_shape_no_halt`). With `fold_total`'s clause that a halted non-composition step
is a `Require` step, this is the whole account of halting: a guard that does not hold, a branch
whose condition cannot be decided or whose exit assertion is violated, a repeat whose bound cannot be
read or is over its range — and nothing else. An answered call never halts either —
`handler_answer` carries no halt, by its type, because a handler is its own atomicity unit (D8) and
one that failed rolled ITSELF back while the fold carries on.

Phase 1967's statement, "a view with no guard never halts", is FALSE over the widened view (a branch
with no guard halts on an undecided condition), so `fold_no_require_no_halt` keeps its name as the
corollary for a view without the new shapes — the extra hypothesis is exactly what 1976 changed.

`ui_view_no_require` and `ui_view_no_flow` prove the UI witness's `View` produces no `VRequire`, no
`VChoose` and no `VRepeat` at any depth, and `ui_never_halts` is the corollary: the UI tier never
halts, so a leaf's refusal is a diagnostic and the chain carries on, exactly as before either shape
existed. It is these lemmas, not a convention, that let `run_total`, `run_no_closure`,
`chain_homomorphism` and `reserved_untouched` keep their pre-1967 statements word for word.

### 6. `reverse_run` / `reversible_run_undoes` — the reversible fragment undoes itself (Phase 1976)

For every program in the REVERSIBLE FRAGMENT — `reversible`, decided from the tree alone: sequence,
assign, the guard, a branch WITH an exit assertion, a repeat with a LITERAL bound; never a call or a
leaf — and every store it runs on (the traced run from that store does not halt, and its trace is
`restorable`: every write overwrote a PRESENT key), folding the inverse of the run from the store the
run left gives back the store it started from, and does not halt. In one line, `run (reverse
p) (run p s) = s`, with `reverse` applied to the RUN — the program and the trace `fold_traced`
recorded for it — which is what the Bennett embedding of `Assign` makes it: an assignment destroys
the value it overwrites, so a reversible run records that value and the inverse restores it with an
ordinary assignment (`write_restore`). `traced_agrees` says the traced fold IS the fold: the outcome
and the placement are equal at every view, which is what "the forward run records nothing" rests on.

**Conditional**, and named as such: on `restorable`. A key that was absent before the run cannot be
restored by an assignment — the store has no delete (K4) — so the theorem says nothing about such a
run, and the code refuses the inverse rather than building it wrong. Everything else is quantified
over the witness's members as total arrows.

### 7. `fold_steps_within_cost` — a run's work is bounded by the view's cost (Phase 1976)

The steps a run takes, read off its trace (one per non-composition step), never exceed `view_cost`
— `Budget.actionCascadeCost` over the view, exactly: a sequence sums, a selection is one step and the
MORE EXPENSIVE arm, a repeat is one step and its body times its bound (a parameter bound at the top
of its range, so the price needs no store). So the budget's price, computed from the tree before the
run, bounds the work the run does. Unconditional. The arithmetic is written over the count (`times`)
so nothing non-linear reaches the solver.

### 8. `repeat_is_unrolling` — a repeat is the sequence of its body (Phase 1976)

A literal repeat folds exactly as the sequence of `n` copies of its body — outcome and placement —
so `sequence_homomorphism` and every other sequence law is a law about repeats, and the inverse of a
repeat is the inverse of that sequence (which is why `reverse` answers a sequence for it: each
iteration overwrote different values and has its own inverse body). Unconditional.

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

Retired: none. Added by Phase 1967: `fold_no_require_no_halt` (+ `_list`), `has_require`,
`composed`, `ui_view_no_require` (+ `_list`), `ui_never_halts`; and `fold_total` gained its guard
clause, `sequence_homomorphism` its halting clause.

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
6. **Which shapes halt** — `fold_no_halting_shape_no_halt`, with `fold_total`'s guard clause
   (Phase 1967, widened by Phase 1976: a guard, a branch, a repeat, and nothing else);
   `fold_no_require_no_halt` kept as the corollary for a view without the new shapes.
   Unconditional.
7. **The reversible fragment undoes itself** — `reverse_run` / `reversible_run_undoes`, with
   `traced_agrees` (the traced fold is the fold) and `write_restore` (Phase 1976). **Conditional**
   on `restorable`: every key the run overwrote was present before it.
8. **A run's work is within the view's cost** — `fold_steps_within_cost` (Phase 1976).
   Unconditional.
9. **A repeat is the sequence of its body** — `repeat_is_unrolling` (Phase 1976). Unconditional.

At the UI witness (Phase 1715's five, each a corollary of the generic theorem above it):

10. **Totality over the closed union** — `run_total`.
11. **No closure invocation** — `run_no_closure`, with `ui_blind_to_closures` discharging the
    obligation, so the theorem is unconditional again.
12. **`Chain` is the fold's homomorphism** — `chain_homomorphism`.
13. **Host-reserved keys untouched** — `reserved_untouched`, conditional on the same seam
    hypothesis as 4, discharged the same way.
14. **The UI tier never halts** — `ui_never_halts`, through `ui_view_no_require` (Phase 1967) and
    `ui_view_no_flow` (Phase 1976: no UI arm views as a branch or a repeat). Unconditional; it is
    what keeps 10–13 at their pre-1967 statements.

**The conditional theorems, named:** `fold_reserved_untouched` and `reserved_untouched` (on the
placement seam); `run_action_blind` (on the witness's blindness); `reverse_run` and
`reversible_run_undoes` (on the trace's restorability). Every other theorem is quantified
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
- **the toy witness's corpus**, which since Phase 1967 names every way a guard halts — false,
  non-boolean, unresolved, errored — at the top level and inside a sequence, and compares the
  halt flag beside the store, the effects and the diagnostics;
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

**Since Phase 1967 the staged list may hold OPS.** A placement that registers an op performer
(`OpPerformance.Performed`; the model's `r_op_perform`) makes `ApplyOps` a staged arm: its ops are
applied in memory while planning, as always, and each is staged as a call of its own — the
performer closed over the op, no landing slot — and performed in the perform phase in plan order
beside the host calls. Nothing in the perform phase changed for it, which is why every theorem
below is stated over the staged list as before and re-proves unchanged; `plan_pure` gained the one
clause that matters, that the plan phase reads only WHETHER a performer is registered and what it
stages, never what it answers. Without one (`ONone`, the UI tier) the arm is exactly what it was.

**Since Phase 1974 the `ApplyOps` arm is one fold, `plan_ops`, over the state axis** (D20). Each op
is resolved through `w_apply` against the state the ops before it left. An op the state witness
views as a guard (`w_op_view` = `ORequire`) holds without moving the state and is never staged; an
edit moves it and, under a registered performer, is staged FROM THE STATE IT PRODUCED (`r_op_perform`
now takes the state and the op — production's `fun _ -> perform state op`). The landing-slot check
reads one arrow, `w_slot_refused`, so a composition with no binding channel refuses every slot
through the clause a reserved slot is refused through. The four theorems below are restated over
the arm as it now is and keep their statements; three more sit beside them (5–7).

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

### 5. `guard_holds_moves_nothing` — a guard that holds is invisible to the plan (Phase 1974)

For an op sequence with a guard at ANY position — whatever ops precede it, against the state they
left — if the guard holds there, the sequence plans exactly as the sequence without it: the ops after
it are planned against the same state, and nothing is staged for it. Holding is the guard's own
apply answering `ROk`, whatever state that apply answered — a guard's answer is discarded, so it
cannot write by construction rather than by the witness's discipline.

### 6. `guard_refusal_halts` — a guard that refuses, refuses the effect with its own reason

For a guard at any position that refuses against the state the ops before it PLANNED — the plan, not
the entry state — the `ApplyOps` effect comes back halted with exactly one new diagnostic, `Failed
ApplyOps reason`, the reason the guard's own, verbatim; every other field of the accumulator is the
one it was handed, whatever follows the guard. So a typed refusal rendered into the reason crosses
intact (D19's W5), and `plan_halt_performs_nothing` lifts it to the handler: rolled back, nothing
performed, under every performer.

### 7. `performer_handed_the_plan` — the last edit is staged from the final planned state

Under a registered performer, an op sequence that plans stages its last EDIT from the sequence's
final planned state: the head of the staged list is the call the performer staged from that state
and that op. So the state a performer is handed with the last op it performs is the state the plan
produced, and a tail that persists it — renders the document, commits the ops — folds nothing
itself. Each earlier edit is staged from the state as of it, by `plan_ops`'s own clause.

`plan_ops_app` (planning a concatenation is planning its parts in turn), `plan_ops_unstaged` (with no
performer nothing is staged), `plan_ops_no_edit` (a sequence of guards leaves state and staged list
alone) and `last_edit` (ghost) support them; `plan_ops_nil` / `plan_ops_cons` state one step of the
fold as equations the recursive lemmas call, because the solver does not unfold `plan_ops` itself
inside a recursive lemma (the same query succeeds outside one).

### The supporting clause — `plan_halt_performs_nothing`

A handler that halted while planning rolls back to the entry store and reports nothing
performed, under every performer at once, because nothing reached the perform phase. This is
the plan-phase half of D8's "at most a prefix": the prefix is empty when the plan fails.

### What the staging model does NOT own

The handler is generic in its tree, bindings, values, ops, actions and client effects, and the
model keeps every one a type parameter; what production does WITH them reaches it through two
records of arrows, and both are the assumed rung:

- **the witness** — `LandQuery`, `Assign`, the landing-slot refusal (`IsReserved` rendered with
  `ReservedPrefix`, or `no-binding-channel` for a composition with no dispatch axis — Phase 1974), the
  state axis's `Apply` and `View`, the shared fold's `runInert` (Phase 1715's subject, so opaque here), and the query
  evaluator. The evaluator and `LandQuery` arrive as ONE arrow, because one opaque composed with
  another is one opaque and the clause the handler owns is halt-or-land; the evaluator's error
  reaches the model already reduced to its discriminator, as `Handler.evalErrorKind` reduces it;
- **the registry** — the gate, the argument policy (with its defect already described), the
  lookup, the performer's behaviour, and — since Phase 1967 — the op performer's registration and
  what it stages for an op (`r_op_perform`), the token and argument the perform phase will apply.

The denial sink (`OnDenied`) is a unit-returning observer and is not modelled.

### The staging claims ladder

#### Proved

1. **Nothing external runs in the plan phase** — `plan_pure`.
2. **The residual is exactly the prefix that ran** — `residual_is_prefix`.
3. **`Performed` is execution order, for every performer** — `performed_in_order`, with
   `perform_spec` as the inductive core.
4. **Success performs the whole staged list** — `commit_is_total_prefix`.
5. **An op-channel guard that holds moves nothing and stages nothing** — `guard_holds_moves_nothing`.
6. **An op-channel guard that refuses halts the effect on its own reason, over the planned state** —
   `guard_refusal_halts`.
7. **The performer is handed the plan: the last edit of a FLAT sequence is staged from the final
   planned state** — `performer_handed_the_plan` (`last_edit` answers `ONone` on a sequence with a
   branch or a repeat in it, so the statement is vacuous there and 11 is the one that holds).
8. **A branch plans as the arm its entry condition picks, when its exit assertion is absent or
   agrees** — `choose_plans_the_taken_arm` (Phase 1976).
9. **A violated exit assertion refuses the effect, after the arm planned, with the assertion
   named** — `exit_violation_halts` (Phase 1976).
10. **A repeat plans as its body written out `count` times** — `repeat_plans_as_unrolling`
    (Phase 1976).
11. **Whatever the shapes, the head of the staged list was staged from the final planned state** —
    `staged_from_the_final_state` (Phase 1976): F-PERFORM over branches and repeats, whose last
    edit is the run's rather than the tree's.

Since Phase 1976 the op view is taken to EXHAUSTION (`op_view o`, `views`), as the action view has
been since Phase 1898: `plan_ops` plans the views (`plan_views` / `plan_view` / `plan_repeat`), so
planning terminates structurally and the obligation that `View` unfolds finitely sits on the
witness. Every earlier theorem keeps its statement over the op sequence.

No `admit`, no `assume`; `--report_assumes error` is on for this module exactly as it is for
the other two.

#### Differentially tested

The extracted model agrees with production over the staging cases the handler's own suites
drive — `HandlerLoopTests`' every-arm handler, its ordered handler, its landing slot, its
three-call half-performer, its plan-then-halt, its reads-too-early and its refused gate;
`DurableInterpreterTests`' refresh handler and its two-call handler — plus the plan-halt arms
those cases do not reach (an unregistered performer, a refused argument policy, a reserved
landing slot, an apply refusal, an unresolvable query), and since Phase 1967 the OP-PERFORMER
cases: one handler run in memory and again under a registered op performer, two ops in one stage
beside a landing host call, a plan that halts after ops were staged, and an op sequence the policy
refuses — each at every failure position, counted across ops and host calls together; and since
Phase 1974 the OP-CHANNEL GUARD cases, at a composition of the UI witness whose state witness views
`UpdateStyle` as a guard (a style no corpus node carries, so a guard that wrote would be seen): a guard
that holds under a performer and in memory, and one that refuses after an edit removed the node it
names. Each run also compares the STATE the op performer was handed with each op, canonically encoded,
across production and the model. Two go-red checks were run while the cases were written and not
committed: production's guard clause edited to move the state lost the comparison, and so did its
performer handed the pre-op state. Each case runs with a SCRIPTED
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

## The effect-gate theorem

`ServerEffectRegistry.Gate` is a boolean over a capability name, consulted before anything else
in `Handler.runEffect` (`src/Fuaran.Program.Server/Handler.fs`). What that buys was stated in
prose — the policy gate is consulted before the performer, so no performer side effect can
precede the policy decision — and the Broch trust ledger's row read "host performers are
TRUSTED; what the Sandbox buys is a bounded blast radius, not a proof of the performer". The
policy an operator actually means is a predicate over the TRACE of performed effects, and nothing
connected the boolean to the predicate. `EffectGate.fst` connects them, on SCIO\*'s split: an
executable check Π run before every operation, a policy specification Σ over the ghost trace, a
monitor state with an `abstracts` relation to the trace, and proofs that Π implies Σ. The perform
path is not re-modelled: the module `open`s `Staging` (Phase 1717) and proves over its
`plan_effect`, `plan`, `perform` and `run`. Three headline theorems, one runtime definition.

**Where the perform path actually is.** The shard that filed this theorem placed the gate's
consultation in `ServerEffect.fs`; that file holds the registry record, the gate, the argument
policy and — since this phase — the return contract, and the consultation itself is
`Handler.runEffect`, which 1717 modelled. That is why this module is proved over 1717's
definitions rather than beside them.

### 1. `gate_before_perform` — a refused capability reaches no performer, and lands one denial

An effect the gate refuses: the planned accumulator is halted, its diagnostics are EXACTLY ONE
new entry over what was there — the denial naming the capability, which by the type of `denial`
carries nothing else — every other field is the entry field, and the result is EQUAL under every
performer lookup, every performer behaviour, every argument policy and every witness. "Reaches no
performer" as an equation: none of those is read before the gate has answered.
`gate_refusal_halts_run` lifts it to the handler: when the stages before a refused effect planned
without halting, the handler rolls back to the entry store, commits nothing, reports nothing
performed, carries no patches, notifications or client effects, its diagnostics are what those
stages produced followed by that one denial — and the outcome is the SAME whatever stages follow
the refused one and whatever the performers would have answered.

### 2. `policy_sufficient` — the gate is sufficient for every policy inductive under it

For a declared Σ with `sufficient_for Π Σ` — `Σ []`, and `Σ tr /\ Π c ==> Σ (tr ++ [c])` —
every trace the handler produces satisfies Σ: the ghost trace of everything that reached the
state or a performer, and `Performed` as the outcome reports it (the whole trace on commit, the
performed host calls on rollback). Quantified over every performer, every witness, every program
and every entry store. The performer is an abstract parameter, and that quantification is the
point: nothing it answers can put a capability the gate did not admit into the trace.

**The finding this theorem records.** Production's gate reads the capability and NOTHING ELSE —
no history, no count. SCIO\*'s monitor state is therefore the trivial one here (`monitor` has
one value, `abstracts` holds of every trace, and `abstracts_preserved` is discharged by `()`),
and a stateless Π is sufficient for exactly the Σ that are inductive under it. "Only the effects
in envelope E" (Phase 1744) and "only to the endpoints in allow-list A" (Phase 1739) are of that
class. "At most B host calls per handler" is NOT, and no proof over this gate can reach it —
Phase 1716's budget bounds the fold's steps, not the effects' count. The stronger statement is
stated here rather than proved: a gate that takes a monitor state, `Π : m -> string -> bool`,
with `abstracts m tr` and the preservation obligation on every update. It needs the production
gate to take a state before it is a theorem about anything that ships, and widening the gate's
signature is a contract change that rides no draft slot by accident.

### 3. `return_contract` — a performer's result is checked before it re-enters the state

The one runtime definition this module adds. A `ReturnContract` (`ServerEffect.fs`) is a
host-declared post-condition on a performer's RESULT — a name, which is the host's own
vocabulary, and the predicate — and `ServerEffectRegistry.registerChecked` composes
`ReturnContract.check` with the performer at registration: SCIO\*'s `import` wrapper, re-expressed
as an F# check. The model's `check_return` is that wrapper clause for clause, and the theorem,
for a registry whose raw behaviour is wrapped by `checked_by`, says three things. The bindings
the perform phase leaves are exactly the landed results folded through `Assign`
(`perform_store`), and every landed result honours its contract (`landed_honour`) — so a result
is checked BEFORE it re-enters the interpreter's state, and one the contract rejects never does.
And when the raw performer's first rejected result is at position `k`, before any raw refusal,
the handler rolls back to the entry store, commits nothing, reports `Performed` as exactly the
first `k` staged capabilities, and its last diagnostic is `PerformFailed` naming that call's
capability and the contract's NAME — a typed refusal, never a silent accept, and never the value
(`violation_refuses`, `perform_diag`, with 1717's `residual_is_prefix` for the rollback).

### What the effect-gate model does NOT own

The same two records of arrows as the staging model — the witness and the registry's gate,
argument policy and lookup — and they are the assumed rung on the same terms. The denial sink
is a unit-returning observer, not modelled; the differential host compares its log against the
`Denied` diagnostics on both sides, which is the only place the sink's behaviour is checked.

### The effect-gate claims ladder

#### Proved

1. **A refused capability reaches no performer and lands exactly one denial** —
   `gate_before_perform`, `gate_refusal_halts_run`.
2. **The gate is sufficient for every policy inductive under it** — `policy_sufficient`, with
   `admitted_sigma` lifting one gated step to a whole trace.
3. **A performer's result is checked against its contract before it enters the state, and a
   rejected one is a typed refusal** — `return_contract`, with `perform_store`, `landed_honour`
   and `violation_refuses`.

No `admit`, no `assume`; `--report_assumes error` is on for this module exactly as for the
other three. "Formally verified" is spent on these three and nothing else in this section.

#### Differentially tested

The extracted model agrees with production over the `ServerEffectTests` registry shapes (the
default refuses every kind; registration does not permit; permission does not register; a host
function named like a built-in arm is still `host:<fn>`) and over 441 generated (capability,
gate, performer) triples — seven capabilities including an unregistered host function, three
programs (alone, after a checked host call, after an unchecked and a checked one), seven gate
shapes including two argument-policy narrowings, three performer behaviours (answers text,
answers a number the contract rejects, refuses). The model's registry wraps the RAW performer
with the EXTRACTED `checked_by`; production's wraps it with `registerChecked`. Four things are
compared per triple: the outcomes (projected as the staging host projects them), the performers'
logs, and the DENIAL STREAM three ways — production's `OnDenied` sink, production's `Denied`
diagnostics, the model's. Then the theorems as instances against production: every performer
invocation in the event log is preceded by the gate's decision on its capability; `Performed`
passes the extracted `admitted` under the triple's own gate; a refused capability lands exactly
one `GateRefused` naming it with nothing performed; a rejected result is `PerformFailed` naming
`host:audit` and `return-contract:text`, with the store the entry store and the perform phase
stopped at it.

The **go-red case** is the one the shard names: a model whose `HostCall` arm consults the lookup
BEFORE the gate, with every other clause the extraction's. It agrees with production whenever
the gate admits — so it is the order it gets wrong, not the arm — and loses the comparison on an
unregistered function the gate refuses: production and the honest model land `GateRefused`, the
mutant lands `Unregistered`, because it asked about the performer before asking whether it may.
A second probe, run while the host was built and not committed: production's wrapper edited to
accept every result lost three of the seven cases.

#### Assumed, and stated

- **The witness and the registry's gate, policy and lookup**, on the staging theorem's terms. In
  the host the gate and the policy are production's own members, and a contract is keyed by the
  function NAME, which is how `registerChecked` keys it.
- **The performer is a function of the call**, as the staging theorem assumes, with the same
  mitigation.
- **The toolchain**, on the same terms as the fold theorem's.

#### Not claimed

- **The performer's own correctness.** The theorems bound what reaches it (the gate, the
  argument policy) and what it can hand back (the return contract); what it DOES when invoked is
  the host's, as the staging ladder says.
- **The honesty of an allow-listed endpoint.** Phase 1739's bound narrows where a call may go;
  nothing here says the endpoint on the list behaves.
- **History-dependent policies.** A Σ that counts or sequences is outside `sufficient_for`,
  because the gate is stateless — the finding above, with the stronger statement proposed.
- **The state discipline.** SecRef\*'s `private` / `shareable` / `encapsulated` labels are read
  against this seam in [`docs/performer-boundary.md`](../docs/performer-boundary.md): a performer
  is handed VALUES and no reference, so the rule is trivially statable at the handler seam and
  not statable for the extension hook, which stays host-trust territory. The ledger says so
  rather than claiming a label model.

**What the trust ledger says now.** The Broch plan's row "host performers, native companions and
the extension hook are trusted" moves, for performers, to: performers are CONSTRAINED at the
boundary — policy before the effect (`gate_before_perform`, `policy_sufficient`), contract on
return (`return_contract`), values in and no reference — and the interpreter's theorems hold
against an arbitrary performer; this phase is the mechanism reference. Native companions and the
extension hook are unchanged by it. It is still not a proof of the performer, and the ladder
above says so.

## Running it

```powershell
pwsh ./proofs/check.ps1              # the whole leg, once
pwsh ./proofs/check.ps1 -Runs 3      # three cold checks with --quake 3, as CI runs it
pwsh ./proofs/check.ps1 -SkipHost    # the proof half only; no solution build needed
```

Six steps, each refusing rather than warning: resolve the pinned prover, CHECK, EXTRACT,
BYTE-DIFF against the committed `oracle/*.fs`, BUILD the oracle project, RUN the differential
host. A module names the test project that hosts its differential: the fold's and the
budget's live in `Fuaran.Program.Parity.Tests`, the staging theorem's and the effect gate's in
`Fuaran.Program.Server.Tests` beside the handler suites they re-declare — which means step 6 for
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
needed none either: `strcat` and the `Prims` type aliases are all it references. `EffectGate.fs`
needed none: it references `Prims.strcat` and `Staging`'s own types.

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
