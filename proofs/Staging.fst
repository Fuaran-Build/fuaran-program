(*
   Copyright 2026 Diametrical Ltd

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*)

/// Phase 1717 — two-phase staging, modelled in F* and proved: the
/// residual of a failed handler is at most a prefix of its host calls.
///
/// # What this module is
///
/// A hand-written model of `Handler.run`
/// (`src/Fuaran.Program.Server/Handler.fs`) — the PLAN phase (`runStage`
/// / `runEffect`, the five effect arms and the `Compute` arm), the phase
/// boundary (the one `if`), the PERFORM phase (`perform`) and the two
/// outcome constructors — clause for clause, with the PERFORMER left
/// abstract. Every definition below names its F# counterpart in the
/// comment above it. The differential host
/// (`tests/Fuaran.Program.Server.Tests/ProofOracleTests.fs`) runs the
/// EXTRACTION of this module beside production over the staging cases
/// the handler's own suites drive, with a scripted performer that fails
/// at every position of every staged list, and requires the two to
/// return the same outcome — that host is the only thing that says this
/// model is about the code that ships.
///
/// Since Phase 1967 the staged list may hold OPS as well as host calls:
/// a placement that registers an op performer (`r_op_perform`,
/// `OpPerformance.Performed` in F#) makes `ApplyOps` a staged arm, its
/// ops applied in memory while planning and performed one staged call
/// per op in plan order. Nothing in the perform phase changed for it —
/// an op's staged call is a staged call with no landing slot — which is
/// why every theorem below is stated over the staged list as before and
/// re-proves unchanged; `plan_pure` gained the one clause that matters,
/// that the plan phase reads only WHETHER an op performer is registered
/// and what it stages, never what it answers. Without one (`ONone`, the
/// UI tier) the arm is exactly what it was.
///
/// Since Phase 1974 (the third witness, DECISIONS.md D20) the `ApplyOps`
/// arm reads two things more of the STATE axis, and both are clauses of
/// that one arm (`plan_ops`). The op witness VIEWS each op (`w_op_view`):
/// an op viewed as `ORequire` is the op-channel GUARD — resolved through
/// its own apply against the state as of that point in the plan, holding
/// without moving the state and refusing with its reason verbatim, and
/// never staged (F-GUARD). And a registered op performer stages each
/// edit FROM THE STATE AS OF THAT OP — the planned state with the op
/// applied — so the performer is handed the state the plan produced
/// rather than re-deriving it (F-PERFORM). The landing-slot check reads
/// one arrow, `w_slot_refused`, so a composition with no binding channel
/// (no dispatch axis) refuses every slot through the clause a reserved
/// slot is refused through. Every earlier theorem is restated over the
/// arm as it now is and keeps its statement; `guard_holds_moves_nothing`,
/// `guard_refusal_halts` and `performer_handed_the_plan` are new.
///
/// Since Phase 1976 the op view has FOUR shapes, and is taken to
/// exhaustion as the dispatch axis's view is: an op the state witness
/// views as a BRANCH (`OChoose`) carries two arms of ops and an entry
/// condition that is itself an op resolved through `w_apply` — `ROk`
/// takes the true arm, `RErr` the false arm, so a domain's typed refusal
/// is the false value, not a halt — and an optional EXIT assertion
/// resolved against the state the arm left, which must hold after the
/// true arm and fail after the false arm, or the arm's plan is refused
/// with the assertion named; an op viewed as a REPEAT (`ORepeat`) plans
/// its body `count` times. Neither condition is ever applied for its
/// state, staged, or performed: a branch plans exactly as the arm it
/// takes (`choose_plans_the_taken_arm`), a repeat exactly as its
/// unrolling (`repeat_plans_as_unrolling`), and the earlier theorems
/// keep their statements over the widened arm.
///
/// Since Phase 1980 the DURABLE DISCIPLINE (DECISIONS.md D12, restated
/// by D23) is modelled beside the handler, at the foot of this module:
/// `Durable.runWith` wraps every performer the perform phase reaches —
/// a host call's and, under a registered op performer, the op's — so a
/// staged call's ordinal is its position in the one staged list, and
/// what the wrapper does at an ordinal is decided by a journal SNAPSHOT
/// read once at entry (`journal`, `decide`): serve a recorded answer,
/// invoke the performer, or refuse. `replay` is the perform phase run
/// through that wrapper and `durable_run` is `run` with it; an op
/// stage's entry carries the op's content address as its SUBJECT, read
/// off the state axis, so two ops at one ordinal are told apart by the
/// divergence check. The plan phase is untouched by all of it, which is
/// `plan_pure` once more: the wrapper changes `r_perf`'s behaviour and
/// nothing the plan phase reads, so every theorem above re-proves
/// unchanged over the same staged list.
///
/// DECISIONS.md D8 states the law in prose: nothing external runs in the
/// plan phase; an uncommitted outcome equals the entry state EXCEPT that
/// `Performed` names exactly the prefix of staged host calls that ran;
/// and `Performed` is execution order. `HandlerLoopTests` pins instances
/// of it. "At most a prefix" quantifies over every staged sequence and
/// every point of failure, which is what instances cannot cover and what
/// the next handler arm can silently break — so it is proved here.
///
/// # What is opaque, and why
///
/// The handler is generic in its tree, its bindings, its values, its
/// ops, its actions and its client effects, and this model keeps every
/// one of them a type parameter — `t`, `b`, `v`, `o`, `q`, `a`, `eff`,
/// `d` — because nothing the theorems say depends on what any of them
/// is. What production does WITH them reaches the model through two
/// records of arrows:
///
///   * the `witness` — the store witness's `LandQuery` / `Assign` /
///     `IsReserved` / `ReservedPrefix` (the last two as one slot-refusal
///     arrow since Phase 1974), the state witness's `Apply` and `View`, the
///     shared fold's `runInert` (Phase 1715's subject, so opaque here),
///     and the query evaluator. The evaluator and `LandQuery` arrive as
///     ONE arrow, `w_query`, because one opaque composed with another is
///     one opaque and the clause the handler owns is halt-or-land;
///   * the `registry` — the gate, the argument policy, the performer
///     LOOKUP and the performer's BEHAVIOUR, split in two. Production's
///     `HostFunctions` map goes from a name to a closure; the model goes
///     from a name to an opaque token `p` and, separately, from a token
///     and an argument to a result. The plan phase captures the token
///     and the perform phase applies the behaviour, and keeping the two
///     apart is what lets `plan_pure` be STATED: the plan phase's output
///     is the same under every behaviour the performers could have.
///
/// The performer is the one genuinely effectful seam, and it is the
/// assumed rung: the model's `r_perf` is a pure function of the call, so
/// a stateful production performer is represented by the verdicts it
/// actually returned, in the order the perform phase asked for them. The
/// perform phase asks each staged call exactly once, in declaration
/// order — which is a property the EXTRACTION inherits, so the
/// differential host's counter-driven performer exercises exactly the
/// case the pure model cannot state.
///
/// The denial sink (`OnDenied`) is a unit-returning observer and is not
/// modelled. The durable journal's READ side (D12) is modelled since
/// Phase 1980 — a snapshot of decided ordinals, below — and its WRITES
/// and its storage are the host's port and out of the theorem, as
/// `proofs.json` records.
///
/// # The theorems
///
///   * `plan_pure` — the plan phase's output is a function of the entry
///     state, the program, the witness and the registry's gate, policy
///     and lookup ALONE: replacing the performers' behaviour by any
///     other leaves it equal. This is D8's "nothing external runs in the
///     plan phase" as an equation.
///   * `residual_is_prefix` — when the plan completed and the performer
///     fails at position `k` of the staged list, the outcome's store is
///     the entry store, it is uncommitted, it carries no patches, no
///     notifications and no client effects, and `Performed` is EXACTLY
///     the first `k` staged capabilities, in order.
///   * `performed_in_order` — the general statement both of the above
///     and `commit_is_total_prefix` are corollaries of: for EVERY
///     performer, `Performed` is the plan phase's capabilities in stage
///     order (on commit; nothing on rollback) followed by the staged
///     capabilities that ran, in declaration order, and the handler
///     commits exactly when all of them ran.
///   * `commit_is_total_prefix` — when every staged call succeeds the
///     handler commits and `Performed` ends with the WHOLE staged list.
///
/// `plan_halt_performs_nothing` is the supporting clause: a handler that
/// halted while planning reports nothing performed at all, because
/// nothing reached the perform phase.
///
/// Beside them (Phase 1974):
///
///   * `guard_holds_moves_nothing` — an op-channel guard that holds is
///     invisible to the plan: the ops after it are planned against the
///     SAME state, and nothing is staged for it.
///   * `guard_refusal_halts` — an op-channel guard that refuses refuses
///     the whole op sequence with its own reason, verbatim, whatever
///     follows it; the effect halts on that reason and stages nothing
///     (`plan_halt_performs_nothing` lifts it to the handler).
///   * `performer_handed_the_plan` — under a registered op performer, the
///     last staged edit of a planned op sequence was staged from the
///     sequence's FINAL planned state: the performer is handed what the
///     plan produced and needs to fold nothing itself.
///
/// And beside those (Phase 1976, the two flow shapes on the op axis):
///
///   * `choose_plans_the_taken_arm` — a branch whose exit assertion is
///     absent or agrees with the arm taken plans exactly as that arm: the
///     conditions move nothing and stage nothing.
///   * `exit_violation_halts` — a branch whose exit assertion disagrees
///     with the arm it took refuses the whole op sequence with the
///     assertion named, after the arm planned; the effect halts on that
///     reason and nothing is performed (`plan_halt_performs_nothing`).
///   * `repeat_plans_as_unrolling` — a repeat plans exactly as its body
///     written out `count` times, so every sequence law covers it.
///   * `staged_from_the_final_state` — `performer_handed_the_plan`'s
///     guarantee over EVERY shape: whenever a planned sequence staged
///     anything, the head of the staged list was staged from the
///     sequence's final planned state. (`last_edit`, which the older
///     theorem is stated through, answers `ONone` on a sequence with a
///     branch or a repeat in it, so that theorem is vacuous there and this
///     one is not.)
///
/// And at the foot of the module (Phase 1980, the durable discipline,
/// over the staged list with a journal snapshot `dur` and an ordinal `k`):
///
///   * `replay_unrun_is_perform` — over calls the journal has nothing
///     for, the replay IS the perform phase, and the performer is invoked
///     at exactly the ordinals the perform phase asks.
///   * `replay_serves_completed` — a prefix the journal records as
///     completed is served: landed from the record, no performer asked,
///     its ordinals reported as replayed.
///   * `resume_performs_only_the_rest` — the two together: a completed
///     prefix is served and only the rest is performed.
///   * `indeterminate_refused_by_default` — an undecided step under an
///     undeclared performer with the opt-in off is refused, before it
///     and everything after it, under `durable-indeterminate-step`.
///   * `indeterminate_reinvoked_only_by_name` — it is re-invoked in
///     exactly two cases, a declared-idempotent performer (no override)
///     or the opt-in (an override recorded), and nowhere else.
///   * `divergence_refused` — an ordinal whose recorded identity is not
///     this call's is refused under `durable-replay-divergence`; the
///     subject is what tells two ops at one ordinal apart.
///   * `durable_resume` — the discipline at the handler: served prefix,
///     invoked rest, committed exactly when the rest ran.
///   * `empty_journal_is_direct` — with nothing recorded, the durable
///     run's outcome is the direct run's.

module Staging

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns — declared here rather than taken
   from `FStar.Pervasives.Native` or `FStar.List.Tot` so the extraction
   references `Prims` and nothing else, exactly as `BoundedFold.fst` and
   `Budget.fst` do. `oracle/Prims.fs` is that whole runtime.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `Result<'a, string>` — what a performer answers, what the apply
/// engine answers, and what the query evaluator answers once its error
/// has been reduced to its discriminator.
type res (a: Type0) =
  | ROk : value: a -> res a
  | RErr : reason: string -> res a

/// F#: `List.append` / `xs @ ys`.
let rec app (#a: Type0) (xs: list a) (ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: app rest ys

/// F#: `List.rev`. The accumulator's lists are built reversed and flipped
/// once at the end, exactly as production does.
let rec rev (#a: Type0) (xs: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> []
  | x :: rest -> app (rev rest) [x]

(* ───────────────────────────────────────────────────────────────────
   The vocabulary — `ServerStore`, `ServerEffect`, `HandlerStage`, the
   two denial arms and the four diagnostic arms.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `ServerStore<'Node, 'Store>`.
type store (t: Type0) (b: Type0) = {
  st_tree: t;
  st_bindings: b;
}

/// F#: `ServerEffect<'Op>`. A query's name, source and pipeline reach the
/// model as the name and an opaque query `q`; the evaluator that reads
/// the source and the pipeline is in the witness.
type server_effect (v: Type0) (o: Type0) (q: Type0) =
  | RunQuery : name: string -> query: q -> server_effect v o q
  | ApplyOps : ops: list o -> server_effect v o q
  | HostCall : fn: string -> args: v -> into: opt string -> server_effect v o q
  | EmitPatch : patch: list o -> server_effect v o q
  | Notify : channel: string -> payload: v -> server_effect v o q

/// F#: `HandlerStage<'Action, 'Op>`.
type stage (a: Type0) (v: Type0) (o: Type0) (q: Type0) =
  | SCompute : action: a -> stage a v o q
  | SEffect : eff: server_effect v o q -> stage a v o q

/// F#: `ServerEffectDenial`.
type denial =
  | Unregistered : capability: string -> denial
  | GateRefused : capability: string -> denial

/// F#: `ServerDiagnostic`. The `Bounded` arm carries the shared fold's
/// diagnostic, which is opaque here (`d`) for the reason the header
/// gives.
type diagnostic (d: Type0) =
  | Bounded : inner: d -> diagnostic d
  | Denied : why: denial -> diagnostic d
  | Failed : capability: string -> reason: string -> diagnostic d
  | PerformFailed : capability: string -> reason: string -> diagnostic d

/// F#: `ServerEffect.capability`. Four arms are named by their
/// discriminator; a `HostCall` is namespaced by its function name.
let capability (#v: Type0) (#o: Type0) (#q: Type0) (e: server_effect v o q) : string =
  match e with
  | RunQuery _ _ -> "RunQuery"
  | ApplyOps _ -> "ApplyOps"
  | HostCall fn _ _ -> strcat "host:" fn
  | EmitPatch _ -> "EmitPatch"
  | Notify _ _ -> "Notify"

(* ───────────────────────────────────────────────────────────────────
   The two records of arrows — what the handler reads of the witness,
   and what it reads of the registry.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `BoundedOutcome<'Store, 'Effect>` — what `BoundedActions.runInert`
/// answers for a `Compute` stage.
type bounded_outcome (b: Type0) (eff: Type0) (d: Type0) = {
  bo_store: b;
  bo_effects: list eff;
  bo_diagnostics: list d;
}

/// F#: `OpView<'Op>` (Phase 1974; widened to four shapes and taken TO
/// EXHAUSTION by Phase 1976) — what one op IS to the handler: an edit
/// that moves the state, the op-channel guard, a branch over two arms of
/// ops, or a bounded repeat of a body of ops. The F# view is one level —
/// a branch's arms are `'Op list` — and the handler re-views each op as
/// it reaches it; this model views the whole tree first, exactly as
/// `BoundedFold.action_view` does for the dispatch axis, so that
/// planning terminates structurally and the obligation that `View`
/// unfolds finitely sits on `w_op_view`'s `Tot` type. `OEdit` and
/// `ORequire` carry the op the handler holds in hand (the one `w_apply`
/// is given); a branch's entry condition and exit assertion are ops
/// applied through `w_apply` for their ANSWER only — never viewed, never
/// applied for their state, never staged.
type op_view (o: Type0) =
  | OEdit : op: o -> op_view o
  | ORequire : op: o -> op_view o
  | OChoose : entry: o -> when_true: list (op_view o) -> when_false: list (op_view o) -> exit: opt o -> op_view o
  | ORepeat : count: nat -> body: list (op_view o) -> op_view o

/// F#: the members of `ProgramWitness` the handler reads, plus the
/// query evaluator it calls. Since Phase 1974 they sit on two axes:
/// `w_apply` and `w_op_view` on the STATE axis; `w_compute`, `w_query`,
/// `w_assign` and `w_slot_refused` on the DISPATCH axis, which a
/// composition with no dispatch axis fills with its refusals (proofs/
/// README.md, "The three axes").
noeq type witness (t: Type0) (b: Type0) (v: Type0) (o: Type0) (q: Type0) (a: Type0) (eff: Type0) (d: Type0) = {
  /// `BoundedActions.runInert witness nodeId action bindings` (Phase 1715).
  w_compute: string -> a -> b -> bounded_outcome b eff d;
  /// `DataFrame.evalSource` / `evalPipelineWith`, then `StoreWitness.LandQuery`
  /// on success; on failure the error's discriminator (`evalErrorKind`).
  w_query: string -> q -> b -> res b;
  /// `StateWitness.Stream.Apply`, one op against the tree.
  w_apply: o -> t -> res t;
  /// `StateWitness.View` (Phase 1974), applied to exhaustion (Phase
  /// 1976): an op viewed `ORequire` is a guard, resolved through
  /// `w_apply` and never staged; one viewed `OChoose` or `ORepeat` is
  /// control over its arms, which are themselves views.
  w_op_view: o -> op_view o;
  /// `StoreWitness.Assign`.
  w_assign: string -> v -> b -> b;
  /// The landing-slot refusal: `OSome reason` refuses the slot while
  /// planning. With a dispatch axis it is `StoreWitness.IsReserved`
  /// rendered with `ReservedPrefix` into the refusal's text; without one
  /// every slot is refused, because there is no binding channel to land
  /// in (Phase 1974).
  w_slot_refused: string -> opt string;
  /// What undoes a compute stage's binding writes (Phase 1977), answered
  /// at plan time against the bindings the stage is about to read: `OSome
  /// restore` is the fold's own reversal — production's
  /// `BoundedActions.reverse` over the trace `runTraced` recorded, folded
  /// by `runReversed` (Phase 1976, `reverse_run` in BoundedFold.fst) —
  /// and `ONone` says the stage cannot be undone: its action is outside
  /// the reversible fragment, or its trace is not restorable. Opaque here
  /// for the reason `w_compute` is: the fold is Phase 1715's subject. In
  /// production the ONE traced run answers both arrows; the model asks
  /// two, and the undo differential is what says they agree.
  w_undo_compute: string -> a -> b -> opt (b -> b);
}

/// F#: `ServerEffectRegistry`, with `HostFunctions` split into the LOOKUP
/// (name to an opaque performer token) and the BEHAVIOUR (token and
/// argument to a result) — the split the header explains.
noeq type registry (t: Type0) (v: Type0) (o: Type0) (q: Type0) (p: Type0) = {
  /// `Gate`.
  r_gate: string -> bool;
  /// `ServerArgumentPolicy.check`, with its defect already `describe`d:
  /// `OSome reason` is a refusal.
  r_policy: server_effect v o q -> opt string;
  /// `Map.tryFind fn registry.HostFunctions`.
  r_lookup: string -> opt p;
  /// The performer itself: what `HostFunctions.[fn] args` answers.
  r_perf: p -> v -> res v;
  /// The OP performer (Phase 1967, the second witness's F2):
  /// `OpPerformance.InMemory` is `ONone` — the in-memory apply is the
  /// whole effect, the UI tier's placement — and `OpPerformance.Performed
  /// perform` is `OSome`, answering for each op the TOKEN the perform
  /// phase will apply and the ARGUMENT it will be applied to. In
  /// production the token is the closure `fun _ -> perform op` and the
  /// argument is inert; what matters here is the split: the plan phase
  /// reads only whether a performer is registered and what it stages,
  /// never what it answers, so `plan_pure` still holds with `r_perf`
  /// replaced. A registered op performer makes `ApplyOps` a STAGED arm:
  /// its ops are applied in memory while planning, as always, and
  /// performed in the perform phase, one staged call per op in plan
  /// order, exactly as host calls are. Since Phase 1974 it stages FROM
  /// the state as of the op — the planned state with that op applied —
  /// and the op (F-PERFORM): production's `fun _ -> perform state op`.
  r_op_perform: opt (t -> o -> (p & v));
}

(* ───────────────────────────────────────────────────────────────────
   The accumulator and the outcome — `Handler.Accumulator` and
   `HandlerOutcome`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Handler.StagedCall`.
type staged_call (v: Type0) (p: Type0) = {
  sc_capability: string;
  sc_performer: p;
  sc_args: v;
  sc_into: opt string;
}

/// F#: `UndoStep<'Node, 'Store, 'Op>` (Phase 1977) — one step of the
/// PLAN an undo reads, recorded while planning, in plan order. An EDIT
/// carries the state it was applied to (the pre-state: an inverse of a
/// write needs the old bytes, which the plan phase holds here and
/// nowhere else) and the op. A COMPUTE stage carries what undoes its
/// binding writes, or `ONone` when nothing can (`w_undo_compute`). A
/// host call and a notification REACHED the world and have no inverse
/// vocabulary; a patch was EMITTED for the host to apply after the
/// handler returned, so whether it can be undone is not this handler's
/// to decide. `Undo.fst` reads the trail; the handler only writes it.
noeq type step (t: Type0) (b: Type0) (o: Type0) =
  | TEdit : pre: t -> op: o -> step t b o
  | TCompute : undo: opt (b -> b) -> step t b o
  | TReached : capability: string -> step t b o
  | TEmitted : capability: string -> step t b o

/// F#: `Handler.Accumulator`. Lists accumulate reversed, as there.
/// `noeq` since Phase 1977: the trail holds a compute stage's restorer.
noeq type accumulator (t: Type0) (b: Type0) (v: Type0) (o: Type0) (eff: Type0) (d: Type0) (p: Type0) = {
  ac_store: store t b;
  ac_halted: bool;
  ac_performed: list string;
  ac_externally: list string;
  ac_staged: list (staged_call v p);
  ac_patches: list o;
  ac_notifications: list (string & v);
  ac_client_effects: list eff;
  ac_diagnostics: list (diagnostic d);
  /// The undo trail (Phase 1977), reversed like every other list here.
  ac_trail: list (step t b o);
}

/// F#: `HandlerOutcome`.
type outcome (t: Type0) (b: Type0) (v: Type0) (o: Type0) (eff: Type0) (d: Type0) = {
  oc_store: store t b;
  oc_committed: bool;
  oc_performed: list string;
  oc_patches: list o;
  oc_notifications: list (string & v);
  oc_client_effects: list eff;
  oc_diagnostics: list (diagnostic d);
}

/// F#: `Handler.halt`.
let halt (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
         (cap: string) (reason: string) (acc: accumulator t b v o eff d p)
  : accumulator t b v o eff d p =
  { acc with ac_halted = true; ac_diagnostics = Failed cap reason :: acc.ac_diagnostics }

/// F#: `Handler.deny`. The `OnDenied` sink it also fires is a
/// unit-returning observer and is not modelled.
let deny (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
         (why: denial) (acc: accumulator t b v o eff d p)
  : accumulator t b v o eff d p =
  { acc with ac_halted = true; ac_diagnostics = Denied why :: acc.ac_diagnostics }

/// F#: `Handler.stagedOp` — one edit staged under a registered op
/// performer: the arm's capability, the token and argument the performer
/// answered for the state AS OF THE OP and the op, and no landing slot —
/// an op lands nothing (Phase 1974, F-PERFORM).
let staged_from (#t: Type0) (#v: Type0) (#o: Type0) (#p: Type0)
                (cap: string) (f: t -> o -> (p & v)) (tree: t) (op: o)
  : staged_call v p =
  let (tok, args) = f tree op in
  { sc_capability = cap; sc_performer = tok; sc_args = args; sc_into = ONone }

/// `List.map w.w_op_view`: the ops of an `ApplyOps` effect, viewed to
/// exhaustion (Phase 1976).
let rec views (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
              (w: witness t b v o q a eff d) (ops: list o)
  : Tot (list (op_view o)) (decreases ops) =
  match ops with
  | [] -> []
  | op :: rest -> w.w_op_view op :: views w rest

/// The undo TRAIL of an op sequence (Phase 1977): the edits the plan
/// applies, each with the state it was applied to, in plan order — a
/// walk over the same views, through the same apply, taking the same
/// arm of a branch and refusing where the plan refuses, that records a
/// `(pre, op)` pair at every edit and stages nothing. Production threads
/// the trail through its ONE fold (`Handler.planOps` answers the tree,
/// the staged list and the trail together); the model walks a second
/// time over the same views from the same state, and `trail_agrees` is
/// what says the two walks reach one tree and refuse alike. Appended
/// with `app` rather than accumulated reversed, so a lemma over the
/// trail reads it in plan order.
let rec trail_views (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                    (w: witness t b v o q a eff d) (vs: list (op_view o)) (tree: t)
  : Tot (res (t & list (t & o))) (decreases %[vs; 1; 0]) =
  match vs with
  | [] -> ROk (tree, [])
  | x :: rest ->
    (match trail_view w x tree with
     | RErr code -> RErr code
     | ROk (tree', first) ->
       (match trail_views w rest tree' with
        | RErr code -> RErr code
        | ROk (tree'', more) -> ROk (tree'', app first more)))

and trail_view (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
               (w: witness t b v o q a eff d) (x: op_view o) (tree: t)
  : Tot (res (t & list (t & o))) (decreases %[x; 0; 0]) =
  match x with
  | ORequire op ->
    (match w.w_apply op tree with
     | RErr code -> RErr code
     | ROk _ -> ROk (tree, []))
  | OEdit op ->
    (match w.w_apply op tree with
     | RErr code -> RErr code
     | ROk tree' -> ROk (tree', [(tree, op)]))
  | OChoose entry when_true when_false exit ->
    let took_true = ROk? (w.w_apply entry tree) in
    let armed =
      if took_true then trail_views w when_true tree
      else trail_views w when_false tree
    in
    (match armed with
     | RErr code -> RErr code
     | ROk (tree', recorded) ->
       (match exit with
        | ONone -> ROk (tree', recorded)
        | OSome assertion ->
          (match w.w_apply assertion tree' with
           | ROk _ ->
             if took_true then ROk (tree', recorded)
             else RErr "the exit assertion held after the false arm"
           | RErr reason ->
             if took_true then RErr (strcat "the exit assertion did not hold after the true arm: " reason)
             else ROk (tree', recorded))))
  | ORepeat count body -> trail_repeat w body count tree

and trail_repeat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                 (w: witness t b v o q a eff d) (body: list (op_view o)) (n: nat) (tree: t)
  : Tot (res (t & list (t & o))) (decreases %[body; 2; n]) =
  if n = 0 then ROk (tree, [])
  else
    (match trail_views w body tree with
     | RErr code -> RErr code
     | ROk (tree', first) ->
       (match trail_repeat w body (n - 1) tree' with
        | RErr code -> RErr code
        | ROk (tree'', more) -> ROk (tree'', app first more)))

/// The trail of an `ApplyOps` effect's ops, as steps, or nothing where
/// the plan refused them — the refusal is the plan's to report.
let trail_ops (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
              (w: witness t b v o q a eff d) (ops: list o) (tree: t)
  : list (t & o) =
  match trail_views w (views w ops) tree with
  | RErr _ -> []
  | ROk (_, recorded) -> recorded

/// `(pre, op)` pairs as trail steps.
let rec edits (#t: Type0) (#b: Type0) (#o: Type0) (xs: list (t & o)) : Tot (list (step t b o)) (decreases xs) =
  match xs with
  | [] -> []
  | (pre, op) :: rest -> TEdit pre op :: edits rest

/// The plan over VIEWS (Phase 1976) — one `match` over the four op
/// shapes, which is what `Handler.planOps` runs one level at a time.
/// `plan_views` threads the state and the staged list through a list of
/// views, short-circuiting at the first refusal; `plan_view` is one
/// shape; `plan_repeat` is a body `count` times. Termination is the
/// three-place structural measure `BoundedFold.fold` uses: an arm is a
/// subterm of its branch, a body of its repeat, and the remaining count
/// is the last place.
let rec plan_views (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                   (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                   (vs: list (op_view o)) (tree: t) (staged: list (staged_call v p))
  : Tot (res (t & list (staged_call v p))) (decreases %[vs; 1; 0]) =
  match vs with
  | [] -> ROk (tree, staged)
  | x :: rest ->
    (match plan_view w cap stage x tree staged with
     | RErr code -> RErr code
     | ROk (tree', staged') -> plan_views w cap stage rest tree' staged')

and plan_view (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
              (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
              (x: op_view o) (tree: t) (staged: list (staged_call v p))
  : Tot (res (t & list (staged_call v p))) (decreases %[x; 0; 0]) =
  match x with
  // The guard: resolved through its own apply against the state as of
  // its position; its answer is discarded, so it cannot write, and it is
  // never staged. Its refusal is the sequence's.
  | ORequire op ->
    (match w.w_apply op tree with
     | RErr code -> RErr code
     | ROk _ -> ROk (tree, staged))
  // The edit: the state moves, and under a registered op performer it is
  // staged from the state AS OF THAT OP — the state it produced — and the
  // op, prepended onto the reversed staged list exactly as a host call
  // is, so the perform phase meets the ops in plan order.
  | OEdit op ->
    (match w.w_apply op tree with
     | RErr code -> RErr code
     | ROk tree' ->
       (match stage with
        | ONone -> ROk (tree', staged)
        | OSome f -> ROk (tree', staged_from cap f tree' op :: staged)))
  // The branch: the entry condition is an op applied for its answer —
  // `ROk` takes the true arm, `RErr` the false arm (a typed refusal is
  // the false value here, not a halt) — and the state it was applied to
  // is the state the arm starts from. After the arm, the exit assertion
  // (when carried) is applied against the state the arm left: it must
  // hold after the true arm and fail after the false arm, or the plan is
  // refused with the assertion named.
  | OChoose entry when_true when_false exit ->
    let took_true = ROk? (w.w_apply entry tree) in
    let armed =
      if took_true then plan_views w cap stage when_true tree staged
      else plan_views w cap stage when_false tree staged
    in
    (match armed with
     | RErr code -> RErr code
     | ROk (tree', staged') ->
       (match exit with
        | ONone -> ROk (tree', staged')
        | OSome assertion ->
          (match w.w_apply assertion tree' with
           | ROk _ ->
             if took_true then ROk (tree', staged')
             else RErr "the exit assertion held after the false arm"
           | RErr reason ->
             if took_true then RErr (strcat "the exit assertion did not hold after the true arm: " reason)
             else ROk (tree', staged'))))
  // The repeat: the body, `count` times, threaded like a sequence.
  | ORepeat count body -> plan_repeat w cap stage body count tree staged

and plan_repeat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                (body: list (op_view o)) (n: nat) (tree: t) (staged: list (staged_call v p))
  : Tot (res (t & list (staged_call v p))) (decreases %[body; 2; n]) =
  if n = 0 then ROk (tree, staged)
  else
    (match plan_views w cap stage body tree staged with
     | RErr code -> RErr code
     | ROk (tree', staged') -> plan_repeat w cap stage body (n - 1) tree' staged')

/// F#: `Handler.planOps` — the `ApplyOps` arm's fold over its ops: view
/// them, then plan the views. The signature Phase 1974 gave it, so every
/// theorem stated over an op sequence keeps its statement.
let plan_ops (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
             (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
             (ops: list o) (tree: t) (staged: list (staged_call v p))
  : res (t & list (staged_call v p)) =
  plan_views w cap stage (views w ops) tree staged

/// F#: `List.map ServerDiagnostic.Bounded`.
let rec map_bounded (#d: Type0) (ds: list d) : Tot (list (diagnostic d)) (decreases ds) =
  match ds with
  | [] -> []
  | x :: rest -> Bounded x :: map_bounded rest

/// F#: the landing-slot check on the `HostCall` arm — a slot under the
/// host-reserved namespace, or any slot at all when the composition has
/// no binding channel (Phase 1974). `OSome reason` refuses it.
let slot_refused (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                 (w: witness t b v o q a eff d) (into: opt string) : opt string =
  match into with
  | OSome key -> w.w_slot_refused key
  | ONone -> ONone

(* ───────────────────────────────────────────────────────────────────
   THE PLAN PHASE — `Handler.runEffect`, `Handler.runStage`, and the
   `List.fold` in `Handler.run` that skips every stage after a halt.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Handler.runEffect`. The gate is consulted FIRST, then the
/// argument policy, and only then is the arm examined — production's
/// order, and the order in which nothing precedes the policy decision.
/// Four arms complete here; `HostCall` is STAGED: the token is captured,
/// nothing is invoked, and `ac_performed` is deliberately not extended.
let plan_effect (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                (w: witness t b v o q a eff d) (reg: registry t v o q p)
                (e: server_effect v o q) (acc: accumulator t b v o eff d p)
  : accumulator t b v o eff d p =
  let cap = capability e in
  if not (reg.r_gate cap) then
    deny (GateRefused cap) acc
  else
    match reg.r_policy e with
    | OSome defect -> halt cap defect acc
    | ONone ->
      let performed = { acc with ac_performed = cap :: acc.ac_performed } in
      (match e with
       | RunQuery name query ->
         (match w.w_query name query performed.ac_store.st_bindings with
          | RErr kind -> halt cap kind acc
          | ROk bindings ->
            { performed with ac_store = { performed.ac_store with st_bindings = bindings } })
       | ApplyOps ops ->
         (match plan_ops w cap reg.r_op_perform ops performed.ac_store.st_tree acc.ac_staged with
          | RErr code -> halt cap code acc
          | ROk (tree, staged) ->
            // The edits the plan applied, each with its pre-state, onto
            // the trail (Phase 1977) — in memory and performed alike,
            // because an undo in memory applies the inverses in memory.
            let trail = app (rev (edits (trail_ops w ops performed.ac_store.st_tree))) acc.ac_trail in
            (match reg.r_op_perform with
             // In memory: the apply IS the effect, and it is performed
             // here, in the plan phase — the shape every placement had
             // before Phase 1967, and the UI tier's still. `plan_ops`
             // staged nothing (`plan_ops_unstaged`).
             | ONone ->
               { performed with
                 ac_store = { performed.ac_store with st_tree = tree };
                 ac_trail = trail }
             // Performed: the apply is a PLAN. The tree moves — a later
             // stage reads the planned tree — but the capability is not
             // recorded as performed; the staged calls are, one per
             // edit, when the perform phase runs them.
             | OSome _ ->
               { acc with
                 ac_store = { acc.ac_store with st_tree = tree };
                 ac_staged = staged;
                 ac_trail = trail }))
       | HostCall fn args into ->
         (match reg.r_lookup fn with
          | ONone -> deny (Unregistered cap) acc
          | OSome performer ->
            (match slot_refused w into with
             | OSome reason -> halt cap reason acc
             | ONone ->
              { acc with
                ac_staged =
                  { sc_capability = cap; sc_performer = performer; sc_args = args; sc_into = into }
                  :: acc.ac_staged;
                ac_trail = TReached cap :: acc.ac_trail }))
       | EmitPatch ops ->
         { performed with
           ac_patches = app (rev ops) performed.ac_patches;
           ac_trail = TEmitted cap :: performed.ac_trail }
       | Notify channel payload ->
         { performed with
           ac_notifications = (channel, payload) :: performed.ac_notifications;
           ac_trail = TReached cap :: performed.ac_trail })

/// F#: `Handler.runStage`. The `Compute` arm is the shared fold with the
/// inert arm — opaque here, Phase 1715's subject.
let plan_stage (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
               (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
               (s: stage a v o q) (acc: accumulator t b v o eff d p)
  : accumulator t b v o eff d p =
  match s with
  | SCompute action ->
    let out = w.w_compute node_id action acc.ac_store.st_bindings in
    { acc with
      ac_store = { acc.ac_store with st_bindings = out.bo_store };
      ac_client_effects = app (rev out.bo_effects) acc.ac_client_effects;
      ac_diagnostics = app (map_bounded (rev out.bo_diagnostics)) acc.ac_diagnostics;
      ac_trail = TCompute (w.w_undo_compute node_id action acc.ac_store.st_bindings) :: acc.ac_trail }
  | SEffect e -> plan_effect w reg e acc

/// F#: the `List.fold` in `Handler.run` — every stage in order, each
/// skipped once the accumulator has halted.
let rec plan (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
             (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
             (stages: list (stage a v o q)) (acc: accumulator t b v o eff d p)
  : Tot (accumulator t b v o eff d p) (decreases stages) =
  match stages with
  | [] -> acc
  | s :: rest ->
    plan w reg node_id rest (if acc.ac_halted then acc else plan_stage w reg node_id s acc)

(* ───────────────────────────────────────────────────────────────────
   THE PERFORM PHASE — `Handler.perform`: the staged calls in
   declaration order, stopping at the first failure.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Handler.perform`. The only place the performer's behaviour is
/// read. A failure halts and records which call, and nothing after it is
/// asked; a success is recorded in `ac_externally` and its result landed
/// in the declared slot.
let rec perform (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                (w: witness t b v o q a eff d) (reg: registry t v o q p)
                (staged: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Tot (accumulator t b v o eff d p) (decreases staged) =
  match staged with
  | [] -> acc
  | call :: rest ->
    (match reg.r_perf call.sc_performer call.sc_args with
     | RErr reason ->
       { acc with
         ac_halted = true;
         ac_diagnostics = PerformFailed call.sc_capability reason :: acc.ac_diagnostics }
     | ROk result ->
       let recorded = { acc with ac_externally = call.sc_capability :: acc.ac_externally } in
       let landed =
         (match call.sc_into with
          | ONone -> recorded
          | OSome key ->
            { recorded with
              ac_store =
                { recorded.ac_store with
                  st_bindings = w.w_assign key result recorded.ac_store.st_bindings } }) in
       perform w reg rest landed)

(* ───────────────────────────────────────────────────────────────────
   THE HANDLER — `Handler.run`: plan, the phase boundary, perform, and
   the two outcome constructors.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: the `start` accumulator in `Handler.run`.
let start (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
          (s: store t b)
  : accumulator t b v o eff d p =
  { ac_store = s;
    ac_halted = false;
    ac_performed = [];
    ac_externally = [];
    ac_staged = [];
    ac_patches = [];
    ac_notifications = [];
    ac_client_effects = [];
    ac_diagnostics = [];
    ac_trail = [] }

/// F#: `Handler.runPlanned` (Phase 1977) — `run`, answering beside the
/// outcome the TRAIL the plan phase recorded, in plan order, which is
/// what an undo reads (`Undo.fst`). The phase boundary is the one `if`:
/// nothing external has run above it, and nothing below it can be
/// undone. A halt rolls back to the entry store, keeps the diagnostics,
/// and reports `ac_externally` — NOT emptied, because a perform-phase
/// failure leaves its predecessors run and reporting `[]` there would be
/// the one lie this design exists to avoid.
let run_planned (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                (stages: list (stage a v o q)) (s: store t b)
  : (outcome t b v o eff d & list (step t b o)) =
  let planned = plan w reg node_id stages (start s) in
  let final = if planned.ac_halted then planned else perform w reg (rev planned.ac_staged) planned in
  if final.ac_halted then
    ({ oc_store = s;
       oc_committed = false;
       oc_performed = rev final.ac_externally;
       oc_patches = [];
       oc_notifications = [];
       oc_client_effects = [];
       oc_diagnostics = rev final.ac_diagnostics },
     rev final.ac_trail)
  else
    ({ oc_store = final.ac_store;
       oc_committed = true;
       oc_performed = app (rev final.ac_performed) (rev final.ac_externally);
       oc_patches = rev final.ac_patches;
       oc_notifications = rev final.ac_notifications;
       oc_client_effects = rev final.ac_client_effects;
       oc_diagnostics = rev final.ac_diagnostics },
     rev final.ac_trail)

/// F#: `Handler.run`. The phase boundary is the one `if`: nothing
/// external has run above it, and nothing below it can be undone. A halt
/// rolls back to the entry store, keeps the diagnostics, and reports
/// `ac_externally` — NOT emptied, because a perform-phase failure leaves
/// its predecessors run and reporting `[]` there would be the one lie
/// this design exists to avoid.
let run (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
        (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
        (stages: list (stage a v o q)) (s: store t b)
  : outcome t b v o eff d =
  let planned = plan w reg node_id stages (start s) in
  let final = if planned.ac_halted then planned else perform w reg (rev planned.ac_staged) planned in
  if final.ac_halted then
    { oc_store = s;
      oc_committed = false;
      oc_performed = rev final.ac_externally;
      oc_patches = [];
      oc_notifications = [];
      oc_client_effects = [];
      oc_diagnostics = rev final.ac_diagnostics }
  else
    { oc_store = final.ac_store;
      oc_committed = true;
      oc_performed = app (rev final.ac_performed) (rev final.ac_externally);
      oc_patches = rev final.ac_patches;
      oc_notifications = rev final.ac_notifications;
      oc_client_effects = rev final.ac_client_effects;
      oc_diagnostics = rev final.ac_diagnostics }

(* ───────────────────────────────────────────────────────────────────
   THE THEOREMS

   Everything from here on is ghost or a lemma: the measures carry
   `noextract_to "FSharp"` and the lemmas are erased by the extractor,
   so the oracle the differential host runs is exactly the definitions
   above.
   ─────────────────────────────────────────────────────────────────── *)

[@@ noextract_to "FSharp"]
let rec length (#a: Type0) (xs: list a) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | _ :: rest -> 1 + length rest

/// The first `k` elements, or all of them when there are fewer.
[@@ noextract_to "FSharp"]
let rec take (#a: Type0) (k: nat) (xs: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> []
  | x :: rest -> if k = 0 then [] else x :: take (k - 1) rest

/// The staged capabilities, in the order of the list.
[@@ noextract_to "FSharp"]
let rec caps (#v: Type0) (#p: Type0) (calls: list (staged_call v p)) : Tot (list string) (decreases calls) =
  match calls with
  | [] -> []
  | c :: rest -> c.sc_capability :: caps rest

/// How many staged calls the performer answers `ROk` before its first
/// `RErr` — the whole list when it never refuses. This is the measure
/// every theorem below is stated in.
[@@ noextract_to "FSharp"]
let rec ran (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p))
  : Tot nat (decreases calls) =
  match calls with
  | [] -> 0
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> 0
     | ROk _ -> 1 + ran perf rest)

/// "The performer fails at position `k`": the first `k` calls are
/// answered `ROk` and the `k`-th (zero-based) is answered `RErr`. The
/// literal shape of the shard's hypothesis; `fails_at_ran` relates it to
/// the measure.
[@@ noextract_to "FSharp"]
let rec fails_at (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p)) (k: nat)
  : Tot bool (decreases calls) =
  match calls with
  | [] -> false
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> k = 0
     | ROk _ -> k > 0 && fails_at perf rest (k - 1))

/// "Every staged call succeeds."
[@@ noextract_to "FSharp"]
let rec all_ok (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p))
  : Tot bool (decreases calls) =
  match calls with
  | [] -> true
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> false
     | ROk _ -> all_ok perf rest)

// ─── list lemmas ─────────────────────────────────────────────────────

let rec app_nil (#a: Type0) (xs: list a)
  : Lemma (ensures app xs [] == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> app_nil rest

let rec app_assoc (#a: Type0) (xs: list a) (ys: list a) (zs: list a)
  : Lemma (ensures app (app xs ys) zs == app xs (app ys zs)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> app_assoc rest ys zs

let rec ran_le_length (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p))
  : Lemma (ensures ran perf calls <= length calls) (decreases calls) =
  match calls with
  | [] -> ()
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> ()
     | ROk _ -> ran_le_length perf rest)

let rec caps_length (#v: Type0) (#p: Type0) (calls: list (staged_call v p))
  : Lemma (ensures length (caps calls) == length calls) (decreases calls) =
  match calls with
  | [] -> ()
  | _ :: rest -> caps_length rest

let rec take_all (#a: Type0) (xs: list a)
  : Lemma (ensures take (length xs) xs == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> take_all rest

let rec fails_at_ran (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p)) (k: nat)
  : Lemma (requires fails_at perf calls k)
          (ensures ran perf calls == k /\ k < length calls)
          (decreases calls) =
  match calls with
  | [] -> ()
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> ()
     | ROk _ -> fails_at_ran perf rest (k - 1))

let rec all_ok_ran (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p))
  : Lemma (requires all_ok perf calls)
          (ensures ran perf calls == length calls)
          (decreases calls) =
  match calls with
  | [] -> ()
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> ()
     | ROk _ -> all_ok_ran perf rest)

// ─── the plan phase touches no performer and no `ac_externally` ─────

/// One stage, under two registries that differ only in the performers'
/// behaviour, plans identically — and leaves `ac_externally` alone.
let plan_stage_pure (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                    (w: witness t b v o q a eff d) (reg: registry t v o q p) (perf': p -> v -> res v)
                    (node_id: string) (s: stage a v o q) (acc: accumulator t b v o eff d p)
  : Lemma
      (ensures
        (plan_stage w reg node_id s acc == plan_stage w ({ reg with r_perf = perf' }) node_id s acc /\
         (plan_stage w reg node_id s acc).ac_externally == acc.ac_externally)) =
  match s with
  | SCompute _ -> ()
  | SEffect e ->
    let cap = capability e in
    if not (reg.r_gate cap) then ()
    else
      (match reg.r_policy e with
       | OSome _ -> ()
       | ONone ->
         (match e with
          | RunQuery name query ->
            (match w.w_query name query acc.ac_store.st_bindings with
             | RErr _ -> ()
             | ROk _ -> ())
          | ApplyOps ops ->
            (match plan_ops w cap reg.r_op_perform ops acc.ac_store.st_tree acc.ac_staged with
             | RErr _ -> ()
             | ROk _ ->
               (match reg.r_op_perform with
                | ONone -> ()
                | OSome _ -> ()))
          | HostCall fn _ into ->
            (match reg.r_lookup fn with
             | ONone -> ()
             | OSome _ ->
               (match slot_refused w into with
                | OSome _ -> ()
                | ONone -> ()))
          | EmitPatch _ -> ()
          | Notify _ _ -> ()))

/// **`plan_pure`.** The plan phase's output is a function of the entry
/// state, the program, the witness and the registry's gate, policy and
/// lookup alone: under every behaviour the performers could have, it is
/// EQUAL — the same staged list, the same store, the same diagnostics.
/// This is D8's first clause, "nothing external runs in the plan phase",
/// as an equation rather than an inspection: a plan that had invoked a
/// performer could not be the same under a performer that answers
/// differently. And `ac_externally` — the record of what ran outside —
/// is untouched by planning, which is what `run` reads it as.
let rec plan_pure (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                  (w: witness t b v o q a eff d) (reg: registry t v o q p) (perf': p -> v -> res v)
                  (node_id: string) (stages: list (stage a v o q)) (acc: accumulator t b v o eff d p)
  : Lemma
      (ensures
        (plan w reg node_id stages acc == plan w ({ reg with r_perf = perf' }) node_id stages acc /\
         (plan w reg node_id stages acc).ac_externally == acc.ac_externally))
      (decreases stages) =
  match stages with
  | [] -> ()
  | s :: rest ->
    if acc.ac_halted then plan_pure w reg perf' node_id rest acc
    else begin
      plan_stage_pure w reg perf' node_id s acc;
      plan_pure w reg perf' node_id rest (plan_stage w reg node_id s acc)
    end

// ─── the perform phase: exactly the prefix that ran, in order ────────

/// The whole behaviour of `perform`, stated once: it halts exactly when
/// the performer refused before the end of the list; what it records as
/// run is the prefix of the staged capabilities the performer answered,
/// in declaration order, appended to whatever was recorded before; and
/// it touches nothing else the outcome reads.
let rec perform_spec (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (reg: registry t v o q p)
                     (calls: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Lemma
      (ensures
        (let r = perform w reg calls acc in
         let k = ran reg.r_perf calls in
         r.ac_halted == (acc.ac_halted || (k < length calls)) /\
         rev r.ac_externally == app (rev acc.ac_externally) (take k (caps calls)) /\
         r.ac_performed == acc.ac_performed /\
         r.ac_staged == acc.ac_staged /\
         r.ac_patches == acc.ac_patches /\
         r.ac_notifications == acc.ac_notifications /\
         r.ac_client_effects == acc.ac_client_effects))
      (decreases calls) =
  match calls with
  | [] -> app_nil (rev acc.ac_externally)
  | call :: rest ->
    (match reg.r_perf call.sc_performer call.sc_args with
     | RErr _ -> app_nil (rev acc.ac_externally)
     | ROk result ->
       let recorded = { acc with ac_externally = call.sc_capability :: acc.ac_externally } in
       let landed =
         (match call.sc_into with
          | ONone -> recorded
          | OSome key ->
            { recorded with
              ac_store =
                { recorded.ac_store with
                  st_bindings = w.w_assign key result recorded.ac_store.st_bindings } }) in
       perform_spec w reg rest landed;
       app_assoc (rev acc.ac_externally) [call.sc_capability] (take (ran reg.r_perf rest) (caps rest)))

/// **`performed_in_order`.** For EVERY performer: when the plan
/// completed, the handler commits exactly when every staged call ran,
/// and `Performed` is the plan phase's capabilities in stage order (on
/// commit; nothing on rollback) followed by the staged capabilities the
/// performer answered, in DECLARATION order — the first `ran` of them,
/// which is the whole list on commit and a proper prefix on rollback.
/// D8's third clause, "`Performed` is execution order", and the general
/// form the two corollaries below read off.
let performed_in_order (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                       (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                       (stages: list (stage a v o q)) (s: store t b)
  : Lemma
      (requires not (plan w reg node_id stages (start s)).ac_halted)
      (ensures
        (let planned = plan w reg node_id stages (start s) in
         let staged = rev planned.ac_staged in
         let k = ran reg.r_perf staged in
         let out = run w reg node_id stages s in
         out.oc_committed == (k = length staged) /\
         out.oc_performed ==
           app (if out.oc_committed then rev planned.ac_performed else []) (take k (caps staged)))) =
  let planned = plan w reg node_id stages (start s) in
  let staged = rev planned.ac_staged in
  plan_pure w reg reg.r_perf node_id stages (start s);
  perform_spec w reg staged planned;
  ran_le_length reg.r_perf staged

/// **`residual_is_prefix`.** D8's second clause, quantified: when the
/// plan completed and the performer fails at position `k` of the staged
/// list — any list, any position — the outcome's store is the ENTRY
/// store, the handler is uncommitted, it carries no patches, no
/// notifications and no client effects, and `Performed` is EXACTLY the
/// first `k` staged capabilities, in declaration order. Not "at most a
/// prefix": the prefix, and which one.
let residual_is_prefix (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                       (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                       (stages: list (stage a v o q)) (s: store t b) (k: nat)
  : Lemma
      (requires
        (let planned = plan w reg node_id stages (start s) in
         not planned.ac_halted /\ fails_at reg.r_perf (rev planned.ac_staged) k))
      (ensures
        (let planned = plan w reg node_id stages (start s) in
         let staged = rev planned.ac_staged in
         let out = run w reg node_id stages s in
         k < length staged /\
         out.oc_store == s /\
         out.oc_committed == false /\
         out.oc_performed == take k (caps staged) /\
         out.oc_patches == [] /\
         out.oc_notifications == [] /\
         out.oc_client_effects == [])) =
  let planned = plan w reg node_id stages (start s) in
  let staged = rev planned.ac_staged in
  fails_at_ran reg.r_perf staged k;
  performed_in_order w reg node_id stages s

/// **`commit_is_total_prefix`.** When every staged call succeeds the
/// handler commits, and `Performed` is the plan phase's capabilities in
/// stage order followed by the WHOLE staged list in declaration order:
/// the residual, on success, is everything.
let commit_is_total_prefix (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                           (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                           (stages: list (stage a v o q)) (s: store t b)
  : Lemma
      (requires
        (let planned = plan w reg node_id stages (start s) in
         not planned.ac_halted /\ all_ok reg.r_perf (rev planned.ac_staged)))
      (ensures
        (let planned = plan w reg node_id stages (start s) in
         let staged = rev planned.ac_staged in
         let out = run w reg node_id stages s in
         out.oc_committed == true /\
         out.oc_performed == app (rev planned.ac_performed) (caps staged))) =
  let planned = plan w reg node_id stages (start s) in
  let staged = rev planned.ac_staged in
  all_ok_ran reg.r_perf staged;
  caps_length staged;
  take_all (caps staged);
  performed_in_order w reg node_id stages s

/// The supporting clause: a handler that halted while PLANNING rolls
/// back to the entry store and reports nothing performed, because
/// nothing reached the perform phase — under every performer at once.
let plan_halt_performs_nothing (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                               (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                               (stages: list (stage a v o q)) (s: store t b)
  : Lemma
      (requires (plan w reg node_id stages (start s)).ac_halted)
      (ensures
        (let out = run w reg node_id stages s in
         out.oc_store == s /\
         out.oc_committed == false /\
         out.oc_performed == [] /\
         out.oc_patches == [] /\
         out.oc_notifications == [] /\
         out.oc_client_effects == [])) =
  plan_pure w reg reg.r_perf node_id stages (start s)

(* ───────────────────────────────────────────────────────────────────
   THE OP-CHANNEL GUARD AND THE PERFORMER'S STATE (Phase 1974), AND THE
   TWO FLOW SHAPES OF THE OP AXIS (Phase 1976)

   Stated over the op sequence an `ApplyOps` effect carries, at every
   position, through the views the state witness gives its ops: a guard
   anywhere in it, the last edit of it, a branch anywhere in it, a repeat.
   ─────────────────────────────────────────────────────────────────── *)

/// Planning `ys` from where an earlier `plan_ops` left the state and the
/// staged list — or that earlier refusal, unchanged. Ghost.
[@@ noextract_to "FSharp"]
let plan_after (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
               (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
               (r: res (t & list (staged_call v p))) (ys: list o)
  : res (t & list (staged_call v p)) =
  match r with
  | RErr code -> RErr code
  | ROk (tree, staged) -> plan_ops w cap stage ys tree staged

/// The same, over views. Ghost.
[@@ noextract_to "FSharp"]
let plan_views_after (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                     (r: res (t & list (staged_call v p))) (ys: list (op_view o))
  : res (t & list (staged_call v p)) =
  match r with
  | RErr code -> RErr code
  | ROk (tree, staged) -> plan_views w cap stage ys tree staged

/// One step of `views`, `plan_views`, `plan_view` and `plan_repeat`, as
/// equations. The recursive lemmas below CALL these rather than leave
/// the SMT solver to unfold the definitions itself: inside a recursive
/// lemma the solver does not unfold them (the same query succeeds in a
/// non-recursive lemma), and an equation it is handed is a step it
/// cannot miss.
let views_cons (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
               (w: witness t b v o q a eff d) (op: o) (rest: list o)
  : Lemma (views w (op :: rest) == w.w_op_view op :: views w rest) = ()

let plan_views_nil (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                   (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                   (tree: t) (staged: list (staged_call v p))
  : Lemma (plan_views w cap stage [] tree staged == ROk (tree, staged)) = ()

let plan_views_cons (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                    (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                    (x: op_view o) (rest: list (op_view o)) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (plan_views w cap stage (x :: rest) tree staged ==
       (match plan_view w cap stage x tree staged with
        | RErr code -> RErr code
        | ROk r -> plan_views w cap stage rest (fst r) (snd r))) = ()

let plan_view_require (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                      (op: o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (plan_view w cap stage (ORequire op) tree staged ==
       (match w.w_apply op tree with
        | RErr code -> RErr code
        | ROk _ -> ROk (tree, staged))) = ()

let plan_view_edit (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                   (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                   (op: o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (plan_view w cap stage (OEdit op) tree staged ==
       (match w.w_apply op tree with
        | RErr code -> RErr code
        | ROk tree' ->
          (match stage with
           | ONone -> ROk (tree', staged)
           | OSome f -> ROk (tree', staged_from cap f tree' op :: staged)))) = ()

/// The reason a violated exit assertion refuses with: the assertion's own
/// refusal after the true arm, or the fact of its holding after the false.
let exit_violation_reason (#t: Type0) (took_true: bool) (answer: res t) : string =
  match answer with
  | ROk _ -> "the exit assertion held after the false arm"
  | RErr reason -> strcat "the exit assertion did not hold after the true arm: " reason

let plan_view_choose (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                     (entry: o) (when_true: list (op_view o)) (when_false: list (op_view o)) (exit: opt o)
                     (tree: t) (staged: list (staged_call v p))
  : Lemma
      (plan_view w cap stage (OChoose entry when_true when_false exit) tree staged ==
       (let took_true = ROk? (w.w_apply entry tree) in
        let armed =
          if took_true then plan_views w cap stage when_true tree staged
          else plan_views w cap stage when_false tree staged
        in
        match armed with
        | RErr code -> RErr code
        | ROk r ->
          (match exit with
           | ONone -> ROk r
           | OSome assertion ->
             (match w.w_apply assertion (fst r) with
              | ROk _ ->
                if took_true then ROk r
                else RErr "the exit assertion held after the false arm"
              | RErr reason ->
                if took_true then RErr (strcat "the exit assertion did not hold after the true arm: " reason)
                else ROk r)))) = ()

let plan_view_repeat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                     (n: nat) (body: list (op_view o)) (tree: t) (staged: list (staged_call v p))
  : Lemma (plan_view w cap stage (ORepeat n body) tree staged == plan_repeat w cap stage body n tree staged) = ()

let plan_repeat_zero (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                     (body: list (op_view o)) (tree: t) (staged: list (staged_call v p))
  : Lemma (plan_repeat w cap stage body 0 tree staged == ROk (tree, staged)) = ()

let plan_repeat_step (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                     (body: list (op_view o)) (n: nat) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires n > 0)
      (ensures
        plan_repeat w cap stage body n tree staged ==
        (match plan_views w cap stage body tree staged with
         | RErr code -> RErr code
         | ROk r -> plan_repeat w cap stage body (n - 1) (fst r) (snd r))) = ()

/// The Phase-1974 equations over an op sequence, now read through the
/// view: one op is one view, planned, and the rest from where it left.
let plan_ops_nil (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                 (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                 (tree: t) (staged: list (staged_call v p))
  : Lemma (plan_ops w cap stage [] tree staged == ROk (tree, staged)) = ()

let plan_ops_cons (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                  (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                  (op: o) (rest: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (plan_ops w cap stage (op :: rest) tree staged ==
       (match plan_view w cap stage (w.w_op_view op) tree staged with
        | RErr code -> RErr code
        | ROk r -> plan_ops w cap stage rest (fst r) (snd r))) = ()

let app_cons (#a: Type0) (x: a) (xs: list a) (ys: list a)
  : Lemma (app (x :: xs) ys == x :: app xs ys) = ()

let rec views_app (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                  (w: witness t b v o q a eff d) (xs: list o) (ys: list o)
  : Lemma (ensures views w (app xs ys) == app (views w xs) (views w ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> views_app w rest ys

/// `plan_views` over a concatenation is `plan_views` over the first part,
/// then over the second from where the first left the state and the
/// staged list.
let rec plan_views_app (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                       (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                       (xs: list (op_view o)) (ys: list (op_view o)) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (ensures
        plan_views w cap stage (app xs ys) tree staged ==
        plan_views_after w cap stage (plan_views w cap stage xs tree staged) ys)
      (decreases xs) =
  match xs with
  | [] -> plan_views_nil w cap stage tree staged
  | x :: rest ->
    app_cons x rest ys;
    plan_views_cons w cap stage x (app rest ys) tree staged;
    plan_views_cons w cap stage x rest tree staged;
    let s : res (t & list (staged_call v p)) = plan_view w cap stage x tree staged in
    (match s with
     | RErr _ -> ()
     | ROk r -> plan_views_app w cap stage rest ys (fst r) (snd r))

/// `plan_ops` over a concatenation is `plan_ops` over the first part, then
/// over the second from where the first left the state and the staged
/// list. Phase 1974's statement, through `views_app`.
let plan_ops_app (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                 (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                 (xs: list o) (ys: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (ensures
        plan_ops w cap stage (app xs ys) tree staged ==
        plan_after w cap stage (plan_ops w cap stage xs tree staged) ys) =
  views_app w xs ys;
  plan_views_app w cap stage (views w xs) (views w ys) tree staged

/// With no op performer registered (the in-memory placement), planning
/// stages nothing, whatever the shapes — which is why the `ONone` arm of
/// `plan_effect` keeps the staged list it was handed.
let rec plan_views_unstaged (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                            (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                            (vs: list (op_view o)) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires ONone? stage)
      (ensures
        (let r = plan_views w cap stage vs tree staged in
         ROk? r ==> snd (ROk?.value r) == staged))
      (decreases %[vs; 1; 0]) =
  match vs with
  | [] -> plan_views_nil w cap stage tree staged
  | x :: rest ->
    plan_views_cons w cap stage x rest tree staged;
    plan_view_unstaged w cap stage x tree staged;
    let s : res (t & list (staged_call v p)) = plan_view w cap stage x tree staged in
    (match s with
     | RErr _ -> ()
     | ROk r -> plan_views_unstaged w cap stage rest (fst r) (snd r))

and plan_view_unstaged (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                       (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                       (x: op_view o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires ONone? stage)
      (ensures
        (let r = plan_view w cap stage x tree staged in
         ROk? r ==> snd (ROk?.value r) == staged))
      (decreases %[x; 0; 0]) =
  match x with
  | ORequire op -> plan_view_require w cap stage op tree staged
  | OEdit op -> plan_view_edit w cap stage op tree staged
  | OChoose entry when_true when_false exit ->
    plan_view_choose w cap stage entry when_true when_false exit tree staged;
    let e : res t = w.w_apply entry tree in
    if ROk? e then plan_views_unstaged w cap stage when_true tree staged
    else plan_views_unstaged w cap stage when_false tree staged
  | ORepeat n body ->
    plan_view_repeat w cap stage n body tree staged;
    plan_repeat_unstaged w cap stage body n tree staged

and plan_repeat_unstaged (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                         (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                         (body: list (op_view o)) (n: nat) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires ONone? stage)
      (ensures
        (let r = plan_repeat w cap stage body n tree staged in
         ROk? r ==> snd (ROk?.value r) == staged))
      (decreases %[body; 2; n]) =
  if n = 0 then plan_repeat_zero w cap stage body tree staged
  else begin
    plan_repeat_step w cap stage body n tree staged;
    plan_views_unstaged w cap stage body tree staged;
    let s : res (t & list (staged_call v p)) = plan_views w cap stage body tree staged in
    (match s with
     | RErr _ -> ()
     | ROk r -> plan_repeat_unstaged w cap stage body (n - 1) (fst r) (snd r))
  end

let plan_ops_unstaged (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                      (ops: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires ONone? stage)
      (ensures
        (let r = plan_ops w cap stage ops tree staged in
         ROk? r ==> snd (ROk?.value r) == staged)) =
  plan_views_unstaged w cap stage (views w ops) tree staged

/// **`guard_holds_moves_nothing`.** An op-channel guard that HOLDS at its
/// position — whatever ops precede it, against the state they left — is
/// invisible to the plan: the sequence plans exactly as the sequence
/// without it, so the ops after it are planned against the SAME state and
/// nothing is staged for it. Holding is resolving through the op's own
/// apply to `ROk`, whatever state that apply answered: a guard's answer is
/// discarded, so a guard cannot write, by construction rather than by the
/// witness's discipline.
let guard_holds_moves_nothing (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                              (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                              (prefix: list o) (guard: o) (suffix: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires
        w.w_op_view guard == ORequire guard /\
        (let r = plan_ops w cap stage prefix tree staged in
         ROk? r ==> ROk? (w.w_apply guard (fst (ROk?.value r)))))
      (ensures
        plan_ops w cap stage (app prefix (guard :: suffix)) tree staged ==
        plan_ops w cap stage (app prefix suffix) tree staged) =
  plan_ops_app w cap stage prefix (guard :: suffix) tree staged;
  plan_ops_app w cap stage prefix suffix tree staged;
  let r0 : res (t & list (staged_call v p)) = plan_ops w cap stage prefix tree staged in
  (match r0 with
   | RErr _ -> ()
   | ROk r ->
     plan_ops_cons w cap stage guard suffix (fst r) (snd r);
     plan_view_require w cap stage guard (fst r) (snd r))

/// **`guard_refusal_halts`.** An op-channel guard that REFUSES at its
/// position — against the state the ops before it left, which is the
/// planned state and not the entry state — refuses the whole `ApplyOps`
/// effect with ITS OWN REASON, verbatim, whatever follows it: the
/// accumulator comes back halted with exactly one new diagnostic,
/// `Failed ApplyOps reason`, and every other field — the store, the
/// staged list, `ac_performed` — is the one it was handed. So a typed
/// refusal rendered into the reason crosses intact (D19's W5), and
/// `plan_halt_performs_nothing` lifts this to the handler: rolled back,
/// nothing performed, under every performer.
let guard_refusal_halts (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                        (w: witness t b v o q a eff d) (reg: registry t v o q p)
                        (prefix: list o) (guard: o) (suffix: list o) (reason: string)
                        (acc: accumulator t b v o eff d p)
  : Lemma
      (requires
        reg.r_gate "ApplyOps" /\
        ONone? (reg.r_policy (ApplyOps (app prefix (guard :: suffix)))) /\
        w.w_op_view guard == ORequire guard /\
        (let r = plan_ops w "ApplyOps" reg.r_op_perform prefix acc.ac_store.st_tree acc.ac_staged in
         ROk? r /\ w.w_apply guard (fst (ROk?.value r)) == RErr reason))
      (ensures
        plan_effect w reg (ApplyOps (app prefix (guard :: suffix))) acc == halt "ApplyOps" reason acc) =
  plan_ops_app w "ApplyOps" reg.r_op_perform prefix (guard :: suffix) acc.ac_store.st_tree acc.ac_staged;
  let r0 : res (t & list (staged_call v p)) = plan_ops w "ApplyOps" reg.r_op_perform prefix acc.ac_store.st_tree acc.ac_staged in
  (match r0 with
   | RErr _ -> ()
   | ROk r ->
     plan_ops_cons w "ApplyOps" reg.r_op_perform guard suffix (fst r) (snd r);
     plan_view_require w "ApplyOps" reg.r_op_perform guard (fst r) (snd r))

/// Whether every view in a sequence is an edit or a guard — no branch, no
/// repeat — which is what makes "the last edit" a fact of the TREE rather
/// than of a run. Ghost.
[@@ noextract_to "FSharp"]
let rec flat (#o: Type0) (vs: list (op_view o)) : Tot bool (decreases vs) =
  match vs with
  | [] -> true
  | x :: rest -> (OEdit? x || ORequire? x) && flat rest

[@@ noextract_to "FSharp"]
let rec last_edit_flat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                       (w: witness t b v o q a eff d) (ops: list o)
  : Tot (opt o) (decreases ops) =
  match ops with
  | [] -> ONone
  | op :: rest ->
    (match last_edit_flat w rest with
     | OSome e -> OSome e
     | ONone ->
       (match w.w_op_view op with
        | OEdit e -> OSome e
        | _ -> ONone))

/// The last op of a FLAT sequence the state witness views as an EDIT —
/// the last one a registered performer is handed — and `ONone` for a
/// sequence with a branch or a repeat anywhere in it, whose last edit is
/// the run's and not the tree's (`staged_from_the_final_state` is the
/// statement for those). Ghost: the theorem below names it, the oracle
/// does not need it.
[@@ noextract_to "FSharp"]
let last_edit (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
              (w: witness t b v o q a eff d) (ops: list o)
  : opt o =
  if flat (views w ops) then last_edit_flat w ops else ONone

/// A FLAT sequence with no edit in it leaves the state and the staged
/// list as they were, whenever it plans at all: only guards, and a guard
/// moves nothing.
let rec plan_ops_no_edit (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                         (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                         (ops: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires flat (views w ops) /\ ONone? (last_edit w ops))
      (ensures
        (let r = plan_ops w cap stage ops tree staged in
         ROk? r ==> (fst (ROk?.value r) == tree /\ snd (ROk?.value r) == staged)))
      (decreases ops) =
  match ops with
  | [] -> plan_ops_nil w cap stage tree staged
  | op :: rest ->
    plan_ops_cons w cap stage op rest tree staged;
    views_cons w op rest;
    (match w.w_op_view op with
     | ORequire g ->
       plan_view_require w cap stage g tree staged;
       let e : res t = w.w_apply g tree in
       (match e with
        | RErr _ -> ()
        | ROk _ -> plan_ops_no_edit w cap stage rest tree staged)
     | OEdit _ -> ()
     | _ -> ())

/// **`performer_handed_the_plan`.** Under a registered op performer, a
/// FLAT op sequence that plans stages its LAST EDIT from the sequence's
/// FINAL planned state: the head of the staged list `plan_ops` answers is
/// the call the performer staged from that state and that op. So the
/// state a performer is handed with the last op it performs is the state
/// the plan produced — the document to render, the tree to commit — and a
/// tail that persists it needs to fold nothing itself (F-PERFORM). Every
/// earlier edit is staged from the state as of IT, by `plan_view`'s own
/// clause; this is the one that says the hand-over reaches the end.
/// (`last_edit` is `ONone` on a sequence with a branch or a repeat in it,
/// so this theorem says nothing there; `staged_from_the_final_state` is
/// the same guarantee over every shape.)
let rec performer_handed_the_plan (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                  (w: witness t b v o q a eff d) (cap: string) (f: t -> o -> (p & v))
                                  (ops: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (ensures
        (let r = plan_ops w cap (OSome f) ops tree staged in
         let e = last_edit w ops in
         (ROk? r /\ OSome? e) ==>
         (let staged' = snd (ROk?.value r) in
          Cons? staged' /\ Cons?.hd staged' == staged_from cap f (fst (ROk?.value r)) (OSome?.item e))))
      (decreases ops) =
  match ops with
  | [] -> plan_ops_nil w cap (OSome f) tree staged
  | op :: rest ->
    plan_ops_cons w cap (OSome f) op rest tree staged;
    views_cons w op rest;
    (match w.w_op_view op with
     | ORequire g ->
       plan_view_require w cap (OSome f) g tree staged;
       let e : res t = w.w_apply g tree in
       (match e with
        | RErr _ -> ()
        | ROk _ -> performer_handed_the_plan w cap f rest tree staged)
     | OEdit e ->
       plan_view_edit w cap (OSome f) e tree staged;
       let applied : res t = w.w_apply e tree in
       (match applied with
        | RErr _ -> ()
        | ROk tree' ->
          let staged1 = staged_from cap f tree' e :: staged in
          (match last_edit_flat w rest with
           | OSome _ -> performer_handed_the_plan w cap f rest tree' staged1
           | ONone ->
             if flat (views w rest) then plan_ops_no_edit w cap (OSome f) rest tree' staged1 else ()))
     | _ -> ())

(* ───────────────────────────────────────────────────────────────────
   The two flow shapes of the op axis (Phase 1976).
   ─────────────────────────────────────────────────────────────────── *)

/// **`choose_plans_the_taken_arm`.** A branch whose exit assertion is
/// absent, or agrees with the arm the entry condition picked, plans
/// EXACTLY as that arm: the entry condition and the exit assertion move
/// nothing and stage nothing, and the state the arm starts from is the
/// state the condition was applied to. So a branch is its arm, and every
/// law about a sequence of ops is a law about the arm a branch took.
let choose_plans_the_taken_arm (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                               (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                               (entry: o) (when_true: list (op_view o)) (when_false: list (op_view o)) (exit: opt o)
                               (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires
        (match exit with
         | ONone -> True
         | OSome assertion ->
           (let took_true = ROk? (w.w_apply entry tree) in
            match (if took_true then plan_views w cap stage when_true tree staged
                   else plan_views w cap stage when_false tree staged) with
            | RErr _ -> True
            | ROk r -> (ROk? (w.w_apply assertion (fst r)) <==> took_true))))
      (ensures
        plan_view w cap stage (OChoose entry when_true when_false exit) tree staged ==
        (if ROk? (w.w_apply entry tree) then plan_views w cap stage when_true tree staged
         else plan_views w cap stage when_false tree staged)) =
  plan_view_choose w cap stage entry when_true when_false exit tree staged

/// **`exit_violation_halts`.** A branch whose exit assertion DISAGREES
/// with the arm it took — held after the false arm, or refused after the
/// true arm — refuses the whole `ApplyOps` effect, after the arm planned,
/// with the assertion named: the accumulator comes back halted on
/// `exit_violation_reason` (the assertion's own refusal text after the
/// true arm), and `plan_halt_performs_nothing` lifts it to the handler —
/// rolled back, nothing performed. A violated exit assertion is a defect
/// in the program, caught, never ignored.
let exit_violation_halts (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                         (w: witness t b v o q a eff d) (reg: registry t v o q p)
                         (prefix: list o) (branch: o) (suffix: list o)
                         (acc: accumulator t b v o eff d p)
  : Lemma
      (requires
        reg.r_gate "ApplyOps" /\
        ONone? (reg.r_policy (ApplyOps (app prefix (branch :: suffix)))) /\
        (match w.w_op_view branch with
         | OChoose entry when_true when_false (OSome assertion) ->
           (match plan_ops w "ApplyOps" reg.r_op_perform prefix acc.ac_store.st_tree acc.ac_staged with
            | ROk r0 ->
              let took_true = ROk? (w.w_apply entry (fst r0)) in
              (match (if took_true then plan_views w "ApplyOps" reg.r_op_perform when_true (fst r0) (snd r0)
                      else plan_views w "ApplyOps" reg.r_op_perform when_false (fst r0) (snd r0)) with
               | ROk r1 -> not (ROk? (w.w_apply assertion (fst r1)) = took_true)
               | RErr _ -> False)
            | RErr _ -> False)
         | _ -> False))
      (ensures
        (match w.w_op_view branch with
         | OChoose entry when_true when_false (OSome assertion) ->
           (match plan_ops w "ApplyOps" reg.r_op_perform prefix acc.ac_store.st_tree acc.ac_staged with
            | ROk r0 ->
              let took_true = ROk? (w.w_apply entry (fst r0)) in
              (match (if took_true then plan_views w "ApplyOps" reg.r_op_perform when_true (fst r0) (snd r0)
                      else plan_views w "ApplyOps" reg.r_op_perform when_false (fst r0) (snd r0)) with
               | ROk r1 ->
                 plan_effect w reg (ApplyOps (app prefix (branch :: suffix))) acc ==
                 halt "ApplyOps" (exit_violation_reason took_true (w.w_apply assertion (fst r1))) acc
               | RErr _ -> True)
            | RErr _ -> True)
         | _ -> True)) =
  plan_ops_app w "ApplyOps" reg.r_op_perform prefix (branch :: suffix) acc.ac_store.st_tree acc.ac_staged;
  let planned : res (t & list (staged_call v p)) = plan_ops w "ApplyOps" reg.r_op_perform prefix acc.ac_store.st_tree acc.ac_staged in
  (match planned with
   | RErr _ -> ()
   | ROk r0 ->
     plan_ops_cons w "ApplyOps" reg.r_op_perform branch suffix (fst r0) (snd r0);
     (match w.w_op_view branch with
      | OChoose entry when_true when_false exit ->
        plan_view_choose w "ApplyOps" reg.r_op_perform entry when_true when_false exit (fst r0) (snd r0)
      | _ -> ()))

/// `n` copies of a body, concatenated: the unrolling of a repeat. Ghost.
[@@ noextract_to "FSharp"]
let rec unroll (#o: Type0) (n: nat) (body: list (op_view o)) : Tot (list (op_view o)) (decreases n) =
  if n = 0 then [] else app body (unroll (n - 1) body)

/// **`repeat_plans_as_unrolling`.** A repeat plans exactly as its body
/// written out `count` times — state and staged list — so every law about
/// a sequence of ops (`plan_views_app`, `plan_views_unstaged`,
/// `staged_from_the_final_state`) is a law about a repeat.
let rec repeat_plans_as_unrolling (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                  (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                                  (body: list (op_view o)) (n: nat) (tree: t) (staged: list (staged_call v p))
  : Lemma (ensures plan_repeat w cap stage body n tree staged == plan_views w cap stage (unroll n body) tree staged)
          (decreases n) =
  if n = 0 then begin plan_repeat_zero w cap stage body tree staged; plan_views_nil w cap stage tree staged end
  else begin
    plan_repeat_step w cap stage body n tree staged;
    plan_views_app w cap stage body (unroll (n - 1) body) tree staged;
    let s : res (t & list (staged_call v p)) = plan_views w cap stage body tree staged in
    (match s with
     | RErr _ -> ()
     | ROk r -> repeat_plans_as_unrolling w cap stage body (n - 1) (fst r) (snd r))
  end

/// What `staged_from_the_final_state` says of one planned step: either it
/// moved nothing and staged nothing, or the head of the staged list it
/// answers was staged from the state it answers. Ghost.
[@@ noextract_to "FSharp"]
let handed (#t: Type0) (#v: Type0) (#o: Type0) (#p: Type0)
           (cap: string) (f: t -> o -> (p & v)) (tree: t) (staged: list (staged_call v p))
           (r: res (t & list (staged_call v p))) : prop =
  match r with
  | RErr _ -> True
  | ROk r ->
    (snd r == staged /\ fst r == tree) \/
    (Cons? (snd r) /\ (exists (op: o). Cons?.hd (snd r) == staged_from cap f (fst r) op))

let rec handed_views (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (cap: string) (f: t -> o -> (p & v))
                     (vs: list (op_view o)) (tree: t) (staged: list (staged_call v p))
  : Lemma (ensures handed cap f tree staged (plan_views w cap (OSome f) vs tree staged))
          (decreases %[vs; 1; 0]) =
  match vs with
  | [] -> plan_views_nil w cap (OSome f) tree staged
  | x :: rest ->
    plan_views_cons w cap (OSome f) x rest tree staged;
    handed_view w cap f x tree staged;
    let s : res (t & list (staged_call v p)) = plan_view w cap (OSome f) x tree staged in
    (match s with
     | RErr _ -> ()
     | ROk r -> handed_views w cap f rest (fst r) (snd r))

and handed_view (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                (w: witness t b v o q a eff d) (cap: string) (f: t -> o -> (p & v))
                (x: op_view o) (tree: t) (staged: list (staged_call v p))
  : Lemma (ensures handed cap f tree staged (plan_view w cap (OSome f) x tree staged))
          (decreases %[x; 0; 0]) =
  match x with
  | ORequire op -> plan_view_require w cap (OSome f) op tree staged
  | OEdit op ->
    plan_view_edit w cap (OSome f) op tree staged;
    let e : res t = w.w_apply op tree in
    (match e with
     | RErr _ -> ()
     | ROk tree' -> assert (Cons?.hd (staged_from cap f tree' op :: staged) == staged_from cap f tree' op))
  | OChoose entry when_true when_false exit ->
    plan_view_choose w cap (OSome f) entry when_true when_false exit tree staged;
    let e : res t = w.w_apply entry tree in
    let armed : res (t & list (staged_call v p)) =
      if ROk? e then plan_views w cap (OSome f) when_true tree staged
      else plan_views w cap (OSome f) when_false tree staged
    in
    (if ROk? e then handed_views w cap f when_true tree staged
     else handed_views w cap f when_false tree staged);
    (match armed with
     | RErr _ -> ()
     | ROk r ->
       (match exit with
        | ONone -> ()
        | OSome assertion ->
          let answer : res t = w.w_apply assertion (fst r) in
          (match answer with
           | ROk _ -> ()
           | RErr _ -> ())))
  | ORepeat n body ->
    plan_view_repeat w cap (OSome f) n body tree staged;
    handed_repeat w cap f body n tree staged

and handed_repeat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                  (w: witness t b v o q a eff d) (cap: string) (f: t -> o -> (p & v))
                  (body: list (op_view o)) (n: nat) (tree: t) (staged: list (staged_call v p))
  : Lemma (ensures handed cap f tree staged (plan_repeat w cap (OSome f) body n tree staged))
          (decreases %[body; 2; n]) =
  if n = 0 then plan_repeat_zero w cap (OSome f) body tree staged
  else begin
    plan_repeat_step w cap (OSome f) body n tree staged;
    handed_views w cap f body tree staged;
    let s : res (t & list (staged_call v p)) = plan_views w cap (OSome f) body tree staged in
    (match s with
     | RErr _ -> ()
     | ROk r -> handed_repeat w cap f body (n - 1) (fst r) (snd r))
  end

/// **`staged_from_the_final_state`.** Under a registered op performer, an
/// op sequence of ANY shape that plans and stages anything new stages
/// its LAST staged edit from the sequence's FINAL planned state: the
/// head of the staged list `plan_ops` answers was staged from the state
/// it answers. `performer_handed_the_plan`'s guarantee — the performer
/// is handed what the plan produced — over branches and repeats, whose
/// last edit is the run's rather than the tree's, which is why this one
/// names the state rather than the op.
let staged_from_the_final_state (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                (w: witness t b v o q a eff d) (cap: string) (f: t -> o -> (p & v))
                                (ops: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (ensures
        (let r = plan_ops w cap (OSome f) ops tree staged in
         ROk? r ==>
         (let (tree', staged') = ROk?.value r in
          (staged' == staged /\ tree' == tree) \/
          (Cons? staged' /\ (exists (op: o). Cons?.hd staged' == staged_from cap f tree' op))))) =
  handed_views w cap f (views w ops) tree staged

(* ───────────────────────────────────────────────────────────────────
   THE DURABLE DISCIPLINE (Phase 1980) — `Durable.runWith`
   (`src/Fuaran.Program.Server/Durable.fs`), modelled BESIDE the handler
   it wraps and over the same staged list.

   Not ghost: the definitions down to `durable_run` are extracted, and
   the differential host in `DurableInterpreterTests` runs them beside
   production. The lemmas after them are erased, as every lemma above is.

   What is modelled is the READ side of D12's discipline: the journal as
   a snapshot taken once at entry (production reads
   `services.Journal.Read invocation` once and lets that snapshot
   decide), and what the wrapped performer does at each ordinal of the
   perform phase — serve a recorded answer, invoke the performer, or
   refuse. Since Phase 1980 a staged call may be an OP STAGE as well as
   a host call, and the discipline is the same for both: the ordinal is
   the position in the one staged list, and the identity a replay
   checks is the capability with, for an op stage, the op's content
   address (`d_subject`). The journal's WRITES — the attempt before the
   performer and the decision after — and its storage are the host's
   port and stay out of the theorem, as the ladder records.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `JournaledStep` — what the snapshot says about one ordinal once
/// its records are read together (`Journal.stepOf`).
type journaled (v: Type0) =
  | JUnrun : journaled v
  | JValue : value: v -> journaled v
  | JRefusal : reason: string -> journaled v
  | JIndeterminate : journaled v

/// F#: the snapshot `services.Journal.Read invocation`, decided per
/// ordinal — `Journal.stepOf`, and what was RECORDED there
/// (`Journal.capabilityOf` with, since Phase 1980, `Journal.subjectOf`):
/// the capability and, for an op stage, the op's content address.
noeq type journal (v: Type0) = {
  j_step: nat -> journaled v;
  j_recorded: nat -> opt (string & opt string);
}

/// F#: what `Durable.runWith`'s wrapper reads of `DurableServices` and of
/// the call in front of it. `d_subject` is the entry's SUBJECT — `ONone`
/// for a host call; for an op stage the content address of the op's
/// canonical form, read off the state axis (`StateWitness.Stream.Encode`,
/// K6) and never the state handed beside it. `d_idempotent` is
/// `PerformerFacets.facetOf fn = Idempotent` for a host call and the
/// op performer's own declaration for an op stage; `d_reinvoke` is
/// `ReinvokeIndeterminate`, the named opt-in.
noeq type durable (v: Type0) (p: Type0) = {
  d_journal: journal v;
  d_subject: staged_call v p -> opt string;
  d_idempotent: staged_call v p -> bool;
  d_reinvoke: bool;
}

/// F#: `DurableCode.IndeterminateStep`.
let indeterminate_step : string = "durable-indeterminate-step"

/// F#: `DurableCode.ReplayDivergence`.
let replay_divergence : string = "durable-replay-divergence"

/// What the wrapper decides for one ordinal BEFORE anything runs.
/// `Invoke overridden` says the performer is called, and whether an
/// override record is written for it (an undeclared performer re-invoked
/// under the opt-in); `Diverged` and `Undecided` are the two refusals.
type decision (v: Type0) =
  | Serve : answer: res v -> decision v
  | Invoke : overridden: bool -> decision v
  | Diverged : decision v
  | Undecided : decision v

/// F#: the body of `wrap` in `Durable.runWith`, clause for clause: the
/// divergence check first (a recorded identity that is not this call's),
/// then the three-state reading of the step.
let decide (#v: Type0) (#p: Type0) (dur: durable v p) (k: nat) (call: staged_call v p) : decision v =
  let identity = (call.sc_capability, dur.d_subject call) in
  let diverged =
    (match dur.d_journal.j_recorded k with
     | OSome recorded -> not (recorded = identity)
     | ONone -> false) in
  if diverged then Diverged
  else
    (match dur.d_journal.j_step k with
     | JValue x -> Serve (ROk x)
     | JRefusal r -> Serve (RErr r)
     | JUnrun -> Invoke false
     | JIndeterminate ->
       if dur.d_idempotent call then Invoke false
       else if dur.d_reinvoke then Invoke true
       else Undecided)

/// One staged call's success, recorded and landed — the `ROk` clause of
/// `perform`, named so the replay below can reuse it for a SERVED answer
/// as well as an invoked one.
let land (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
         (w: witness t b v o q a eff d) (call: staged_call v p) (result: v)
         (acc: accumulator t b v o eff d p)
  : accumulator t b v o eff d p =
  let recorded = { acc with ac_externally = call.sc_capability :: acc.ac_externally } in
  match call.sc_into with
  | ONone -> recorded
  | OSome key ->
    { recorded with
      ac_store =
        { recorded.ac_store with
          st_bindings = w.w_assign key result recorded.ac_store.st_bindings } }

/// F#: `DurableOutcome` less the handler outcome — the four ordinal lists
/// a durable run reports, beside the accumulator the perform phase left.
noeq type replay_result (t: Type0) (b: Type0) (v: Type0) (o: Type0) (eff: Type0) (d: Type0) (p: Type0) = {
  rp_acc: accumulator t b v o eff d p;
  /// `Replayed`: ordinals SERVED from the journal; no performer ran.
  rp_replayed: list nat;
  /// `Invoked`: ordinals whose performer this run called.
  rp_invoked: list nat;
  /// `Indeterminate`: ordinals refused because the journal could not decide.
  rp_indeterminate: list nat;
  /// `Overrides`: ordinals re-invoked under the opt-in, by step.
  rp_overrides: list nat;
}

/// F#: `Handler.perform` run through `Durable.runWith`'s wrapper — the
/// perform phase with the cursor as `k`. Production keeps the cursor in a
/// mutable cell the fold does not know about; the model threads it, and
/// the differential host is what says the two agree. Each arm is the
/// production wrapper's: a served value or refusal is landed or halts
/// exactly as a performed one would, an invoked performer's answer is
/// recorded as invoked whether it answered or refused, and a refusal of
/// the wrapper's own halts the handler under the call's capability with
/// the code as the reason.
let rec replay (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
               (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
               (k: nat) (staged: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Tot (replay_result t b v o eff d p) (decreases staged) =
  match staged with
  | [] -> { rp_acc = acc; rp_replayed = []; rp_invoked = []; rp_indeterminate = []; rp_overrides = [] }
  | call :: rest ->
    let failed (reason: string) : accumulator t b v o eff d p =
      { acc with
        ac_halted = true;
        ac_diagnostics = PerformFailed call.sc_capability reason :: acc.ac_diagnostics } in
    (match decide dur k call with
     | Diverged ->
       { rp_acc = failed replay_divergence; rp_replayed = []; rp_invoked = []; rp_indeterminate = []; rp_overrides = [] }
     | Undecided ->
       { rp_acc = failed indeterminate_step; rp_replayed = []; rp_invoked = []; rp_indeterminate = [k]; rp_overrides = [] }
     | Serve (RErr reason) ->
       { rp_acc = failed reason; rp_replayed = [k]; rp_invoked = []; rp_indeterminate = []; rp_overrides = [] }
     | Serve (ROk result) ->
       let r = replay w reg dur (k + 1) rest (land w call result acc) in
       { r with rp_replayed = k :: r.rp_replayed }
     | Invoke overridden ->
       let overrides = if overridden then [k] else [] in
       (match reg.r_perf call.sc_performer call.sc_args with
        | RErr reason ->
          { rp_acc = failed reason; rp_replayed = []; rp_invoked = [k]; rp_indeterminate = []; rp_overrides = overrides }
        | ROk result ->
          let r = replay w reg dur (k + 1) rest (land w call result acc) in
          { r with rp_invoked = k :: r.rp_invoked; rp_overrides = app overrides r.rp_overrides }))

/// F#: the two outcome constructors of `Handler.runWith`, as `run` writes
/// them, over whatever accumulator the perform phase left.
let finish (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
           (s: store t b) (final: accumulator t b v o eff d p)
  : outcome t b v o eff d =
  if final.ac_halted then
    { oc_store = s;
      oc_committed = false;
      oc_performed = rev final.ac_externally;
      oc_patches = [];
      oc_notifications = [];
      oc_client_effects = [];
      oc_diagnostics = rev final.ac_diagnostics }
  else
    { oc_store = final.ac_store;
      oc_committed = true;
      oc_performed = app (rev final.ac_performed) (rev final.ac_externally);
      oc_patches = rev final.ac_patches;
      oc_notifications = rev final.ac_notifications;
      oc_client_effects = rev final.ac_client_effects;
      oc_diagnostics = rev final.ac_diagnostics }

/// F#: `DurableOutcome`.
type durable_outcome (t: Type0) (b: Type0) (v: Type0) (o: Type0) (eff: Type0) (d: Type0) = {
  do_outcome: outcome t b v o eff d;
  do_replayed: list nat;
  do_invoked: list nat;
  do_indeterminate: list nat;
  do_overrides: list nat;
}

/// F#: `Durable.runWith`. The plan phase is `run`'s, unchanged and
/// reached the same way; the perform phase is `replay` from ordinal 0.
/// That the plan phase is the same under the wrapped performers as under
/// the bare ones is `plan_pure` — the wrapper changes only `r_perf`'s
/// behaviour, which the plan phase never reads.
let durable_run (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                (node_id: string) (stages: list (stage a v o q)) (s: store t b)
  : durable_outcome t b v o eff d =
  let planned = plan w reg node_id stages (start s) in
  if planned.ac_halted then
    { do_outcome = finish s planned; do_replayed = []; do_invoked = []; do_indeterminate = []; do_overrides = [] }
  else
    let r = replay w reg dur 0 (rev planned.ac_staged) planned in
    { do_outcome = finish s r.rp_acc;
      do_replayed = r.rp_replayed;
      do_invoked = r.rp_invoked;
      do_indeterminate = r.rp_indeterminate;
      do_overrides = r.rp_overrides }

// ─── the durable theorems (ghost from here) ──────────────────────────

/// The ordinals `k`, `k+1`, … `k+n-1`.
[@@ noextract_to "FSharp"]
let rec ordinals (k: nat) (n: nat) : Tot (list nat) (decreases n) =
  if n = 0 then [] else k :: ordinals (k + 1) (n - 1)

/// How many staged calls the performer is ASKED before the perform phase
/// stops: `ran` plus the one it refused, when it refused one. The measure
/// `Invoked` is stated in, because a performer that refused was invoked.
[@@ noextract_to "FSharp"]
let rec asked (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p))
  : Tot nat (decreases calls) =
  match calls with
  | [] -> 0
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> 1
     | ROk _ -> 1 + asked perf rest)

/// "The journal records each of these calls as COMPLETED, under the
/// identity the replay will hold at its ordinal", counting from `k` — the
/// journal a run leaves behind the steps it decided.
[@@ noextract_to "FSharp"]
let rec completed_at (#v: Type0) (#p: Type0) (dur: durable v p) (k: nat) (calls: list (staged_call v p))
  : Tot bool (decreases calls) =
  match calls with
  | [] -> true
  | c :: rest ->
    (match dur.d_journal.j_step k, dur.d_journal.j_recorded k with
     | JValue _, OSome recorded ->
       recorded = (c.sc_capability, dur.d_subject c) && completed_at dur (k + 1) rest
     | _, _ -> false)

/// "The journal has NOTHING at these calls' ordinals", counting from `k`
/// — the steps a run never reached, or the whole list under `Journal.none`.
[@@ noextract_to "FSharp"]
let rec unrun_from (#v: Type0) (#p: Type0) (dur: durable v p) (k: nat) (calls: list (staged_call v p))
  : Tot bool (decreases calls) =
  match calls with
  | [] -> true
  | _ :: rest ->
    (match dur.d_journal.j_recorded k, dur.d_journal.j_step k with
     | ONone, JUnrun -> unrun_from dur (k + 1) rest
     | _, _ -> false)

/// What SERVING a completed prefix does to the accumulator: each recorded
/// value is landed exactly as a performed one would be, and nothing is
/// asked of any performer.
[@@ noextract_to "FSharp"]
let rec serve (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
              (w: witness t b v o q a eff d) (dur: durable v p)
              (k: nat) (calls: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Tot (accumulator t b v o eff d p) (decreases calls) =
  match calls with
  | [] -> acc
  | c :: rest ->
    (match dur.d_journal.j_step k with
     | JValue x -> serve w dur (k + 1) rest (land w c x acc)
     | _ -> acc)

let rec serve_keeps_halted (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                           (w: witness t b v o q a eff d) (dur: durable v p)
                           (k: nat) (calls: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Lemma (ensures (serve w dur k calls acc).ac_halted == acc.ac_halted) (decreases calls) =
  match calls with
  | [] -> ()
  | c :: rest ->
    (match dur.d_journal.j_step k with
     | JValue x -> serve_keeps_halted w dur (k + 1) rest (land w c x acc)
     | _ -> ())

/// **`replay_unrun_is_perform`.** Over calls the journal has nothing for,
/// the replay IS the perform phase: the same accumulator as `perform`,
/// nothing served, nothing refused, no override, and the performer
/// invoked at exactly the ordinals the perform phase asks — the prefix it
/// answers plus the one it refuses. This is `Journal.none` degrading the
/// durable interpreter to the direct one, as an equation; and it is the
/// tail of every resume.
let rec replay_unrun_is_perform (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                                (k: nat) (calls: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires unrun_from dur k calls)
      (ensures
        (let r = replay w reg dur k calls acc in
         r.rp_acc == perform w reg calls acc /\
         r.rp_replayed == [] /\
         r.rp_invoked == ordinals k (asked reg.r_perf calls) /\
         r.rp_indeterminate == [] /\
         r.rp_overrides == []))
      (decreases calls) =
  match calls with
  | [] -> ()
  | c :: rest ->
    (match reg.r_perf c.sc_performer c.sc_args with
     | RErr _ -> ()
     | ROk result -> replay_unrun_is_perform w reg dur (k + 1) rest (land w c result acc))

/// **`replay_serves_completed`.** A prefix the journal records as
/// completed is SERVED: the replay of `served @ rest` from `k` is the
/// replay of `rest` from `k + |served|`, over the accumulator serving left
/// — the recorded values landed, no performer asked — with the served
/// ordinals reported as replayed in front of whatever the rest reports.
let rec replay_serves_completed (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                                (k: nat) (served: list (staged_call v p)) (rest: list (staged_call v p))
                                (acc: accumulator t b v o eff d p)
  : Lemma
      (requires completed_at dur k served)
      (ensures
        (let r = replay w reg dur k (app served rest) acc in
         let r' = replay w reg dur (k + length served) rest (serve w dur k served acc) in
         r.rp_acc == r'.rp_acc /\
         r.rp_replayed == app (ordinals k (length served)) r'.rp_replayed /\
         r.rp_invoked == r'.rp_invoked /\
         r.rp_indeterminate == r'.rp_indeterminate /\
         r.rp_overrides == r'.rp_overrides))
      (decreases served) =
  match served with
  | [] -> ()
  | c :: served' ->
    (match dur.d_journal.j_step k with
     | JValue x -> replay_serves_completed w reg dur (k + 1) served' rest (land w c x acc)
     | _ -> ())

/// **`resume_performs_only_the_rest`.** THE DISCIPLINE, over one staged
/// list from one ordinal: with a completed prefix recorded and nothing
/// recorded after it, a replay serves the prefix — every one of its
/// ordinals reported as replayed, none invoked — and performs only the
/// rest, exactly as the perform phase would from the served
/// accumulator; nothing is refused and nothing is overridden.
let resume_performs_only_the_rest (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                  (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                                  (k: nat) (served: list (staged_call v p)) (rest: list (staged_call v p))
                                  (acc: accumulator t b v o eff d p)
  : Lemma
      (requires completed_at dur k served /\ unrun_from dur (k + length served) rest)
      (ensures
        (let r = replay w reg dur k (app served rest) acc in
         r.rp_acc == perform w reg rest (serve w dur k served acc) /\
         r.rp_replayed == ordinals k (length served) /\
         r.rp_invoked == ordinals (k + length served) (asked reg.r_perf rest) /\
         r.rp_indeterminate == [] /\
         r.rp_overrides == [])) =
  replay_serves_completed w reg dur k served rest acc;
  replay_unrun_is_perform w reg dur (k + length served) rest (serve w dur k served acc);
  app_nil (ordinals k (length served))

/// **`indeterminate_refused_by_default`.** A step the journal shows as
/// attempted and undecided, under a performer the host has NOT declared
/// idempotent and with the opt-in off, is REFUSED: the completed prefix
/// before it is served, no performer is invoked at it or after it, the
/// handler halts under the call's capability with
/// `durable-indeterminate-step` as the reason, and the ordinal is
/// reported as indeterminate. D12's "refuse by default", as an equation.
let indeterminate_refused_by_default (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                     (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                                     (k: nat) (served: list (staged_call v p)) (c: staged_call v p) (rest: list (staged_call v p))
                                     (acc: accumulator t b v o eff d p)
  : Lemma
      (requires
        completed_at dur k served /\
        dur.d_journal.j_recorded (k + length served) == OSome (c.sc_capability, dur.d_subject c) /\
        dur.d_journal.j_step (k + length served) == JIndeterminate /\
        not (dur.d_idempotent c) /\
        not dur.d_reinvoke)
      (ensures
        (let r = replay w reg dur k (app served (c :: rest)) acc in
         let base = serve w dur k served acc in
         r.rp_acc ==
           { base with
             ac_halted = true;
             ac_diagnostics = PerformFailed c.sc_capability indeterminate_step :: base.ac_diagnostics } /\
         r.rp_replayed == ordinals k (length served) /\
         r.rp_invoked == [] /\
         r.rp_indeterminate == [k + length served] /\
         r.rp_overrides == [])) =
  replay_serves_completed w reg dur k served (c :: rest) acc;
  app_nil (ordinals k (length served))

/// **`indeterminate_reinvoked_only_by_name`.** The same undecided step is
/// re-invoked in exactly two cases, and nowhere else: the performer is
/// declared idempotent (no override to record — its own shape closes the
/// window), or the opt-in is on (the override is RECORDED at that
/// ordinal). Either way the replay from there is the perform phase over
/// the step and what follows it.
let indeterminate_reinvoked_only_by_name (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                         (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                                         (k: nat) (served: list (staged_call v p)) (c: staged_call v p) (rest: list (staged_call v p))
                                         (acc: accumulator t b v o eff d p)
  : Lemma
      (requires
        completed_at dur k served /\
        dur.d_journal.j_recorded (k + length served) == OSome (c.sc_capability, dur.d_subject c) /\
        dur.d_journal.j_step (k + length served) == JIndeterminate /\
        (dur.d_idempotent c || dur.d_reinvoke) /\
        unrun_from dur (k + length served + 1) rest)
      (ensures
        (let r = replay w reg dur k (app served (c :: rest)) acc in
         let base = serve w dur k served acc in
         r.rp_acc == perform w reg (c :: rest) base /\
         r.rp_replayed == ordinals k (length served) /\
         r.rp_invoked == ordinals (k + length served) (asked reg.r_perf (c :: rest)) /\
         r.rp_indeterminate == [] /\
         r.rp_overrides == (if dur.d_idempotent c then [] else [k + length served]))) =
  replay_serves_completed w reg dur k served (c :: rest) acc;
  app_nil (ordinals k (length served));
  let base = serve w dur k served acc in
  (match reg.r_perf c.sc_performer c.sc_args with
   | RErr _ -> ()
   | ROk result -> replay_unrun_is_perform w reg dur (k + length served + 1) rest (land w c result base))

/// **`divergence_refused`.** An ordinal whose recorded identity is not the
/// one the replay holds there — a different capability, or the same
/// capability over a different op — is refused before anything is served
/// or invoked at it: the completed prefix is served, the handler halts
/// under the call's capability with `durable-replay-divergence`, and
/// nothing after it is reached. The subject is what makes this
/// statement true of an op stage: two ops at one ordinal share a
/// capability, and only their content addresses tell them apart.
let divergence_refused (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                       (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                       (k: nat) (served: list (staged_call v p)) (c: staged_call v p) (rest: list (staged_call v p))
                       (recorded: string & opt string) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires
        completed_at dur k served /\
        dur.d_journal.j_recorded (k + length served) == OSome recorded /\
        recorded =!= (c.sc_capability, dur.d_subject c))
      (ensures
        (let r = replay w reg dur k (app served (c :: rest)) acc in
         let base = serve w dur k served acc in
         r.rp_acc ==
           { base with
             ac_halted = true;
             ac_diagnostics = PerformFailed c.sc_capability replay_divergence :: base.ac_diagnostics } /\
         r.rp_replayed == ordinals k (length served) /\
         r.rp_invoked == [] /\
         r.rp_indeterminate == [] /\
         r.rp_overrides == [])) =
  replay_serves_completed w reg dur k served (c :: rest) acc;
  app_nil (ordinals k (length served))

/// **`durable_resume`.** The discipline at the HANDLER: when the plan
/// completed and its staged list is a completed prefix the journal holds
/// followed by calls it has nothing for — the journal a run interrupted
/// between two steps leaves — the durable run reports the prefix as
/// replayed, invokes the performer at exactly the ordinals after it that
/// the perform phase asks, refuses and overrides nothing, and commits
/// exactly when every call after the prefix ran. The resumed run performs
/// only what the recorded run did not.
let durable_resume (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                   (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                   (node_id: string) (stages: list (stage a v o q)) (s: store t b)
                   (served: list (staged_call v p)) (rest: list (staged_call v p))
  : Lemma
      (requires
        (let planned = plan w reg node_id stages (start s) in
         not planned.ac_halted /\
         rev planned.ac_staged == app served rest /\
         completed_at dur 0 served /\
         unrun_from dur (length served) rest))
      (ensures
        (let r = durable_run w reg dur node_id stages s in
         r.do_replayed == ordinals 0 (length served) /\
         r.do_invoked == ordinals (length served) (asked reg.r_perf rest) /\
         r.do_indeterminate == [] /\
         r.do_overrides == [] /\
         r.do_outcome.oc_committed == (ran reg.r_perf rest = length rest))) =
  let planned = plan w reg node_id stages (start s) in
  let base = serve w dur 0 served planned in
  resume_performs_only_the_rest w reg dur 0 served rest planned;
  perform_spec w reg rest base;
  serve_keeps_halted w dur 0 served planned;
  ran_le_length reg.r_perf rest

/// **`empty_journal_is_direct`.** With nothing recorded for any staged
/// call — `Journal.none`, or a first run — the durable run's outcome IS
/// the direct run's, and it reports nothing served, nothing refused and
/// no override: the durable interpreter with its durability switched off
/// is the direct interpreter, as an equation rather than a parity test.
let empty_journal_is_direct (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                            (w: witness t b v o q a eff d) (reg: registry t v o q p) (dur: durable v p)
                            (node_id: string) (stages: list (stage a v o q)) (s: store t b)
  : Lemma
      (requires
        (let planned = plan w reg node_id stages (start s) in
         planned.ac_halted \/ unrun_from dur 0 (rev planned.ac_staged)))
      (ensures
        (let r = durable_run w reg dur node_id stages s in
         r.do_outcome == run w reg node_id stages s /\
         r.do_replayed == [] /\
         r.do_indeterminate == [] /\
         r.do_overrides == [])) =
  let planned = plan w reg node_id stages (start s) in
  if planned.ac_halted then ()
  else replay_unrun_is_perform w reg dur 0 (rev planned.ac_staged) planned
