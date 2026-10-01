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
///     `IsReserved` / `ReservedPrefix`, the op witness's `Apply`, the
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
/// modelled; the durable journal (D12) sits above `Handler.run` and is
/// out of the theorem, as `proofs.json` records.
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

/// F#: the members of `ProgramWitness` the handler reads, plus the
/// query evaluator it calls.
noeq type witness (t: Type0) (b: Type0) (v: Type0) (o: Type0) (q: Type0) (a: Type0) (eff: Type0) (d: Type0) = {
  /// `BoundedActions.runInert witness nodeId action bindings` (Phase 1715).
  w_compute: string -> a -> b -> bounded_outcome b eff d;
  /// `DataFrame.evalSource` / `evalPipelineWith`, then `StoreWitness.LandQuery`
  /// on success; on failure the error's discriminator (`evalErrorKind`).
  w_query: string -> q -> b -> res b;
  /// `OpWitness.Stream.Apply`, one op against the tree.
  w_apply: o -> t -> res t;
  /// `StoreWitness.Assign`.
  w_assign: string -> v -> b -> b;
  /// `StoreWitness.IsReserved`.
  w_is_reserved: string -> bool;
  /// `StoreWitness.ReservedPrefix`, for the refusal's text.
  w_reserved_prefix: string;
}

/// F#: `ServerEffectRegistry`, with `HostFunctions` split into the LOOKUP
/// (name to an opaque performer token) and the BEHAVIOUR (token and
/// argument to a result) — the split the header explains.
noeq type registry (v: Type0) (o: Type0) (q: Type0) (p: Type0) = {
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
  /// order, exactly as host calls are.
  r_op_perform: opt (o -> (p & v));
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

/// F#: `Handler.Accumulator`. Lists accumulate reversed, as there.
type accumulator (t: Type0) (b: Type0) (v: Type0) (o: Type0) (eff: Type0) (d: Type0) (p: Type0) = {
  ac_store: store t b;
  ac_halted: bool;
  ac_performed: list string;
  ac_externally: list string;
  ac_staged: list (staged_call v p);
  ac_patches: list o;
  ac_notifications: list (string & v);
  ac_client_effects: list eff;
  ac_diagnostics: list (diagnostic d);
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

/// F#: `Handler.runEffect`'s `ApplyOps` fold — `List.fold` with
/// `Result.bind` over `Apply`, short-circuiting at the first refusal.
let rec apply_all (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                  (w: witness t b v o q a eff d) (ops: list o) (tree: t)
  : Tot (res t) (decreases ops) =
  match ops with
  | [] -> ROk tree
  | op :: rest ->
    (match w.w_apply op tree with
     | RErr code -> RErr code
     | ROk tree' -> apply_all w rest tree')

/// F#: `List.map ServerDiagnostic.Bounded`.
let rec map_bounded (#d: Type0) (ds: list d) : Tot (list (diagnostic d)) (decreases ds) =
  match ds with
  | [] -> []
  | x :: rest -> Bounded x :: map_bounded rest

/// F#: the `Some key when witness.Store.IsReserved key` guard on the
/// `HostCall` arm — a landing slot under the host-reserved namespace.
let reserved_slot (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                  (w: witness t b v o q a eff d) (into: opt string) : bool =
  match into with
  | OSome key -> w.w_is_reserved key
  | ONone -> false

/// F#: the `List.fold` in `Handler.runEffect`'s `ApplyOps` arm that
/// STAGES each op under a registered op performer (Phase 1967): one
/// staged call per op, carrying the arm's capability, the token and
/// argument the performer answered for the op, and no landing slot —
/// an op lands nothing. Prepended in op order onto the reversed staged
/// list, exactly as a host call is, so the perform phase meets them in
/// plan order.
let rec stage_ops (#v: Type0) (#o: Type0) (#p: Type0)
                  (cap: string) (stage: o -> (p & v)) (ops: list o) (staged: list (staged_call v p))
  : Tot (list (staged_call v p)) (decreases ops) =
  match ops with
  | [] -> staged
  | op :: rest ->
    let (tok, args) = stage op in
    stage_ops cap stage rest
      ({ sc_capability = cap; sc_performer = tok; sc_args = args; sc_into = ONone } :: staged)

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
                (w: witness t b v o q a eff d) (reg: registry v o q p)
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
         (match apply_all w ops performed.ac_store.st_tree with
          | RErr code -> halt cap code acc
          | ROk tree ->
            (match reg.r_op_perform with
             // In memory: the apply IS the effect, and it is performed
             // here, in the plan phase — the shape every placement had
             // before Phase 1967, and the UI tier's still.
             | ONone -> { performed with ac_store = { performed.ac_store with st_tree = tree } }
             // Performed: the apply is a PLAN. The tree moves — a later
             // stage reads the planned tree — but the capability is not
             // recorded as performed; the staged calls are, one per op,
             // when the perform phase runs them.
             | OSome stage ->
               { acc with
                 ac_store = { acc.ac_store with st_tree = tree };
                 ac_staged = stage_ops cap stage ops acc.ac_staged }))
       | HostCall fn args into ->
         (match reg.r_lookup fn with
          | ONone -> deny (Unregistered cap) acc
          | OSome performer ->
            if reserved_slot w into then
              halt cap
                (strcat "landing slot is under the host-reserved '"
                  (strcat w.w_reserved_prefix "' namespace"))
                acc
            else
              { acc with
                ac_staged =
                  { sc_capability = cap; sc_performer = performer; sc_args = args; sc_into = into }
                  :: acc.ac_staged })
       | EmitPatch ops ->
         { performed with ac_patches = app (rev ops) performed.ac_patches }
       | Notify channel payload ->
         { performed with ac_notifications = (channel, payload) :: performed.ac_notifications })

/// F#: `Handler.runStage`. The `Compute` arm is the shared fold with the
/// inert arm — opaque here, Phase 1715's subject.
let plan_stage (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
               (w: witness t b v o q a eff d) (reg: registry v o q p) (node_id: string)
               (s: stage a v o q) (acc: accumulator t b v o eff d p)
  : accumulator t b v o eff d p =
  match s with
  | SCompute action ->
    let out = w.w_compute node_id action acc.ac_store.st_bindings in
    { acc with
      ac_store = { acc.ac_store with st_bindings = out.bo_store };
      ac_client_effects = app (rev out.bo_effects) acc.ac_client_effects;
      ac_diagnostics = app (map_bounded (rev out.bo_diagnostics)) acc.ac_diagnostics }
  | SEffect e -> plan_effect w reg e acc

/// F#: the `List.fold` in `Handler.run` — every stage in order, each
/// skipped once the accumulator has halted.
let rec plan (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
             (w: witness t b v o q a eff d) (reg: registry v o q p) (node_id: string)
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
                (w: witness t b v o q a eff d) (reg: registry v o q p)
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
    ac_diagnostics = [] }

/// F#: `Handler.run`. The phase boundary is the one `if`: nothing
/// external has run above it, and nothing below it can be undone. A halt
/// rolls back to the entry store, keeps the diagnostics, and reports
/// `ac_externally` — NOT emptied, because a perform-phase failure leaves
/// its predecessors run and reporting `[]` there would be the one lie
/// this design exists to avoid.
let run (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
        (w: witness t b v o q a eff d) (reg: registry v o q p) (node_id: string)
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
                    (w: witness t b v o q a eff d) (reg: registry v o q p) (perf': p -> v -> res v)
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
            (match apply_all w ops acc.ac_store.st_tree with
             | RErr _ -> ()
             | ROk _ ->
               (match reg.r_op_perform with
                | ONone -> ()
                | OSome _ -> ()))
          | HostCall fn _ into ->
            (match reg.r_lookup fn with
             | ONone -> ()
             | OSome _ -> if reserved_slot w into then () else ())
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
                  (w: witness t b v o q a eff d) (reg: registry v o q p) (perf': p -> v -> res v)
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
                     (w: witness t b v o q a eff d) (reg: registry v o q p)
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
                       (w: witness t b v o q a eff d) (reg: registry v o q p) (node_id: string)
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
                       (w: witness t b v o q a eff d) (reg: registry v o q p) (node_id: string)
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
                           (w: witness t b v o q a eff d) (reg: registry v o q p) (node_id: string)
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
                               (w: witness t b v o q a eff d) (reg: registry v o q p) (node_id: string)
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
