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

/// Phase 1759 — the effect gate is provably sufficient for a declared
/// policy: the performer boundary moves from trusted to constrained.
///
/// # What this module is
///
/// The gate half of `Handler.run`
/// (`src/Fuaran.Program.Server/Handler.fs`, `runEffect` and `perform`),
/// read against SCIO*'s split — an executable access-control check Π
/// run before every operation, a policy specification Σ over the ghost
/// trace, a monitor state with an `abstracts` relation to the trace,
/// and proofs that Π implies Σ and that every operation preserves the
/// abstraction. The perform path itself is NOT re-modelled here: Phase
/// 1717's `Staging.fst` already models `runEffect`, the phase boundary
/// and `perform` clause for clause with the performer abstract, and this
/// module `open`s it and proves over its definitions. What is new at
/// runtime is ONE definition — `check_return`, the return-contract wrapper
/// `ServerEffectRegistry.registerChecked` composes with a performer at
/// registration (`src/Fuaran.Program.Server/ServerEffect.fs`,
/// `ReturnContract.check`) — and the differential host
/// (`tests/Fuaran.Program.Server.Tests/ProofOracleTests.fs`, the
/// `Phase 1759` list) runs its extraction beside production.
///
/// Where SCIO*'s pieces land in this estate's shape:
///
///   * Π is `ServerEffectRegistry.Gate : string -> bool`, consulted on
///     the CAPABILITY (`ServerEffect.capability`) before anything else —
///     the first clause of `runEffect`, `plan_effect` in the model.
///   * Σ is `policy_spec`: a predicate over the ghost trace of the
///     capabilities that reached the interpreter's state or a performer.
///     Phase 1739's argument constraints are an instance; Phase 1744's
///     signed envelope is the declared Σ an operator signs.
///   * The monitor state is `monitor`, and it is EMPTY — see the finding
///     below.
///   * Traces stay ghost: `ghost_trace` is `noextract`, and nothing is
///     added at runtime beyond the wrapper.
///
/// # The finding the model records
///
/// Production's gate reads the capability name and NOTHING ELSE: no
/// history, no count, no state. SCIO*'s monitor state — what Π is
/// allowed to read, and what `abstracts` ties to the trace — is therefore
/// the trivial one here, `Stateless`, and `abstracts` holds of every
/// trace. The consequence is stated rather than hidden: a stateless Π is
/// sufficient for exactly the Σ that are INDUCTIVE under it — `Σ []`, and
/// `Σ tr /\ Π c ==> Σ (tr ++ [c])` (`sufficient_for`). "Only the effects
/// in envelope E" and "only to the endpoints in allow-list A" are of that
/// class; "at most B host calls per handler" is NOT, and no proof over
/// this gate can reach it. Phase 1716's budget bounds the FOLD's steps,
/// not the effects' count. The stronger statement — a gate that takes a
/// monitor state, `Π : m -> string -> bool`, with `abstracts m tr` and the
/// preservation obligation on every update — is proposed in
/// `proofs/README.md` and needs the production gate to take a state
/// before it can be proved of anything.
///
/// # The theorems
///
///   * `gate_before_perform` — one effect the gate refuses: the planned
///     accumulator is halted, carries EXACTLY ONE new diagnostic, the
///     denial naming the capability (and, by its type, nothing else), and
///     is EQUAL under every lookup, every performer behaviour, every
///     argument policy and every witness — the refusal is decided before
///     any of them is read. `gate_refusal_halts_run` lifts it to the
///     handler: a refused stage rolls the handler back to the entry
///     store, reports nothing performed, appends that one denial to the
///     diagnostics the stages before it produced, and the outcome is the
///     same whatever follows the refused stage and whatever the
///     performers would have answered.
///   * `policy_sufficient` — for a declared Σ with `sufficient_for Π Σ`,
///     every trace the handler produces satisfies Σ: the ghost trace of
///     everything that reached the state or a performer, and `Performed`
///     as the outcome reports it (the whole trace on commit, the
///     performed host calls on rollback). Quantified over every
///     performer, every witness, every program and every entry store —
///     the performer is an abstract parameter, and that quantification
///     is the point.
///   * `return_contract` — with a contract declared on a performer, a
///     result it answers is check_return against that contract BEFORE it
///     reaches the store (`perform_store`: the bindings are exactly the
///     landed results folded through `Assign`, and `landed_honour`: every
///     landed result honours its contract), and a result the contract
///     rejects is a typed refusal — `PerformFailed` naming the capability
///     and the contract's name, never the value — at that position, with
///     the handler rolled back and `Performed` exactly the calls before
///     it. SCIO*'s `import` wrapper, re-expressed as the F# check whose
///     sufficiency this is.
///
/// Every theorem is conditional on the witness and the registry arrows
/// exactly as `Staging.fst`'s are: they are the assumed rung, and the
/// theorems are quantified over them.

module EffectGate

open Staging

(* ───────────────────────────────────────────────────────────────────
   Π over a trace — the one boolean the policy theorem is stated in.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `List.forall registry.Gate` — every capability in the list is one
/// the gate admits. Extracted, so the differential host can assert the
/// instance of `policy_sufficient` against production's `Performed`
/// directly.
let rec admitted (gate: string -> bool) (xs: list string) : Tot bool (decreases xs) =
  match xs with
  | [] -> true
  | c :: rest -> gate c && admitted gate rest

(* ───────────────────────────────────────────────────────────────────
   The return contract — `ReturnContract` and `ReturnContract.check`,
   the one definition this module adds to the runtime.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `ReturnContract` — a host-declared post-condition on a
/// performer's RESULT: a name, which is the host's own vocabulary and
/// safe to surface, and the check.
noeq type contract (v: Type0) = {
  ct_name: string;
  ct_holds: v -> bool;
}

/// F#: `ReturnContract.describe` — the refusal's text. The NAME and
/// never the value: a rejected result is not echoed anywhere, on the
/// terms a denial carries the capability and never the payload.
let contract_reason (#v: Type0) (c: contract v) : string =
  strcat "return-contract:" c.ct_name

/// F#: `ReturnContract.check` — the wrapper `registerChecked` composes
/// with a performer. A result the contract rejects becomes `RErr`
/// carrying the contract's name; a raw refusal passes through unchanged.
let check_return (#v: Type0) (#p: Type0) (c: contract v) (perf: p -> v -> res v) : p -> v -> res v =
  fun tok args ->
    match perf tok args with
    | RErr reason -> RErr reason
    | ROk result -> if c.ct_holds result then ROk result else RErr (contract_reason c)

/// F#: a registry built with `registerChecked` for the functions `ct`
/// names and `register` for the rest — per-function contracts, keyed
/// here by the performer token because that is what the staged call
/// carries into the perform phase.
let checked_by (#v: Type0) (#p: Type0) (ct: p -> opt (contract v)) (perf: p -> v -> res v)
  : p -> v -> res v =
  fun tok args ->
    match ct tok with
    | ONone -> perf tok args
    | OSome c -> check_return c perf tok args

(* ───────────────────────────────────────────────────────────────────
   Σ, the monitor state and `abstracts` — SCIO*'s pieces, ghost.
   ─────────────────────────────────────────────────────────────────── *)

/// Σ: a policy over the ghost trace of capabilities. Phase 1739's
/// argument constraints and Phase 1744's envelope are instances.
[@@ noextract_to "FSharp"]
type policy_spec = list string -> prop

/// SCIO*'s monitor state, as production has it: NONE. `Gate` takes the
/// capability and nothing else, so the state Π may read is empty and
/// `abstracts` holds of every trace. This is the finding the module
/// header states, kept as a definition so the shape is visible where a
/// later stateful gate would widen it.
[@@ noextract_to "FSharp"]
type monitor =
  | Stateless : monitor

/// F#: there is no update — the gate reads no state between effects.
[@@ noextract_to "FSharp"]
let step (m: monitor) (c: string) : monitor = Stateless

/// The abstraction relation between the monitor state and the trace.
/// With no state, every trace is abstracted by the one state.
[@@ noextract_to "FSharp"]
let abstracts (m: monitor) (tr: list string) : prop = True

/// SCIO*'s preservation obligation — every operation keeps the state an
/// abstraction of the trace — discharged for the stateless monitor: it
/// is the whole content of that obligation here, and it is trivial
/// precisely because the state is.
let abstracts_preserved (m: monitor) (tr: list string) (c: string)
  : Lemma (requires abstracts m tr) (ensures abstracts (step m c) (app tr [c])) = ()

/// "Π implies Σ", in the only form a stateless Π can have: Σ holds of
/// the empty trace, and extending a trace Σ admits by a capability Π
/// admits keeps Σ. A Σ that counts — "at most B host calls" — is not
/// of this form, and `policy_sufficient` says nothing about it.
[@@ noextract_to "FSharp"]
let sufficient_for (gate: string -> bool) (sigma: policy_spec) : prop =
  sigma [] /\
  (forall (tr: list string) (c: string). {:pattern (sigma (app tr [c]))}
     (sigma tr /\ gate c) ==> sigma (app tr [c]))

/// The ghost trace: every capability that reached the interpreter's
/// state or a performer, in execution order — the plan phase's
/// capabilities in stage order, then the staged host calls the performer
/// answered. Mirrors `run`'s `final` accumulator; never materialised.
[@@ noextract_to "FSharp"]
let ghost_trace (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                (stages: list (stage a v o q)) (s: store t b)
  : list string =
  let planned = plan w reg node_id stages (start s) in
  let final = if planned.ac_halted then planned else perform w reg (rev planned.ac_staged) planned in
  app (rev final.ac_performed) (rev final.ac_externally)

(* ───────────────────────────────────────────────────────────────────
   Ghost measures of the perform phase — what landed, what refused.
   ─────────────────────────────────────────────────────────────────── *)

/// The results the performer answered `ROk` before its first `RErr`,
/// each with the token it answered for and the slot it lands in: the
/// values the perform phase hands to `Assign`.
[@@ noextract_to "FSharp"]
let rec landed (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p))
  : Tot (list (p & opt string & v)) (decreases calls) =
  match calls with
  | [] -> []
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> []
     | ROk result -> (c.sc_performer, c.sc_into, result) :: landed perf rest)

/// Every landed result whose token carries a contract honours it.
[@@ noextract_to "FSharp"]
let rec all_honour (#v: Type0) (#p: Type0) (ct: p -> opt (contract v)) (xs: list (p & opt string & v))
  : Tot bool (decreases xs) =
  match xs with
  | [] -> true
  | (tok, _, result) :: rest ->
    (match ct tok with
     | ONone -> all_honour ct rest
     | OSome c -> c.ct_holds result && all_honour ct rest)

/// `Assign` folded over the landed results that declared a slot — what
/// the perform phase does to the bindings, stated without `perform`.
[@@ noextract_to "FSharp"]
let rec fold_assign (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                    (w: witness t b v o q a eff d) (xs: list (p & opt string & v)) (bindings: b)
  : Tot b (decreases xs) =
  match xs with
  | [] -> bindings
  | (_, into, result) :: rest ->
    (match into with
     | ONone -> fold_assign w rest bindings
     | OSome key -> fold_assign w rest (w.w_assign key result bindings))

/// The first refusal, as the capability and the reason the perform
/// phase records — or nothing when every call was answered.
[@@ noextract_to "FSharp"]
let rec failure (#v: Type0) (#p: Type0) (perf: p -> v -> res v) (calls: list (staged_call v p))
  : Tot (opt (string & string)) (decreases calls) =
  match calls with
  | [] -> ONone
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr reason -> OSome (c.sc_capability, reason)
     | ROk _ -> failure perf rest)

/// The first result the RAW performer answers that its contract rejects,
/// before any raw refusal: its position (counted from `k0`), the
/// capability, and the contract's name. `ONone` when no such call exists.
[@@ noextract_to "FSharp"]
let rec violation (#v: Type0) (#p: Type0) (ct: p -> opt (contract v)) (perf: p -> v -> res v)
                  (calls: list (staged_call v p)) (k0: nat)
  : Tot (opt (nat & string & string)) (decreases calls) =
  match calls with
  | [] -> ONone
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> ONone
     | ROk result ->
       (match ct c.sc_performer with
        | ONone -> violation ct perf rest (k0 + 1)
        | OSome con ->
          if con.ct_holds result then violation ct perf rest (k0 + 1)
          else OSome (k0, c.sc_capability, con.ct_name)))

(* ───────────────────────────────────────────────────────────────────
   List lemmas — `admitted` over append and reverse.
   ─────────────────────────────────────────────────────────────────── *)

let rec admitted_app (gate: string -> bool) (xs: list string) (ys: list string)
  : Lemma (ensures admitted gate (app xs ys) == (admitted gate xs && admitted gate ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> admitted_app gate rest ys

let rec admitted_rev (gate: string -> bool) (xs: list string)
  : Lemma (ensures admitted gate (rev xs) == admitted gate xs) (decreases xs) =
  match xs with
  | [] -> ()
  | c :: rest ->
    admitted_rev gate rest;
    admitted_app gate (rev rest) [c]

/// Π implies Σ, lifted from one step to a whole admitted sequence.
let rec admitted_sigma (gate: string -> bool) (sigma: policy_spec) (tr: list string) (xs: list string)
  : Lemma (requires sufficient_for gate sigma /\ sigma tr /\ admitted gate xs)
          (ensures sigma (app tr xs))
          (decreases xs) =
  match xs with
  | [] -> app_nil tr
  | c :: rest ->
    assert (sigma (app tr [c]));
    app_assoc tr [c] rest;
    admitted_sigma gate sigma (app tr [c]) rest

(* ───────────────────────────────────────────────────────────────────
   The plan phase under Π — what it records is admitted.
   ─────────────────────────────────────────────────────────────────── *)

/// Planning ops under an admitted capability keeps the staged
/// capabilities admitted: every call `plan_ops` prepends carries that one
/// capability (Phase 1967), a guard prepends none and an edit one, each
/// staged from the state as of it (Phase 1974).
let rec plan_ops_admitted (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                          (w: witness t b v o q a eff d) (gate: string -> bool) (cap: string)
                          (stage: opt (t -> o -> (p & v)))
                          (ops: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma (requires gate cap /\ admitted gate (caps staged))
          (ensures
            (let r = plan_ops w cap stage ops tree staged in
             ROk? r ==> admitted gate (caps (snd (ROk?.value r)))))
          (decreases ops) =
  match ops with
  | [] -> plan_ops_nil w cap stage tree staged
  | op :: rest ->
    plan_ops_cons w cap stage op rest tree staged;
    (match w.w_apply op tree with
     | RErr _ -> ()
     | ROk tree' ->
       (match w.w_op_view op with
        | ORequire -> plan_ops_admitted w gate cap stage rest tree staged
        | OEdit ->
          (match stage with
           | ONone -> plan_ops_admitted w gate cap stage rest tree' staged
           | OSome f -> plan_ops_admitted w gate cap stage rest tree' (staged_from cap f tree' op :: staged))))

/// One stage keeps `ac_performed` and the staged capabilities admitted:
/// `plan_effect` extends either only after `r_gate` answered true.
let plan_stage_admitted (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                        (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                        (s: stage a v o q) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires admitted reg.r_gate acc.ac_performed /\ admitted reg.r_gate (caps acc.ac_staged))
      (ensures
        (let r = plan_stage w reg node_id s acc in
         admitted reg.r_gate r.ac_performed /\ admitted reg.r_gate (caps r.ac_staged))) =
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
            plan_ops_admitted w reg.r_gate cap reg.r_op_perform ops acc.ac_store.st_tree acc.ac_staged
          | HostCall fn _ into ->
            (match reg.r_lookup fn with
             | ONone -> ()
             | OSome _ ->
               (match slot_refused w into with
                | OSome _ -> ()
                | ONone -> ()))
          | EmitPatch _ -> ()
          | Notify _ _ -> ()))

let rec plan_admitted (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                      (stages: list (stage a v o q)) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires admitted reg.r_gate acc.ac_performed /\ admitted reg.r_gate (caps acc.ac_staged))
      (ensures
        (let r = plan w reg node_id stages acc in
         admitted reg.r_gate r.ac_performed /\ admitted reg.r_gate (caps r.ac_staged)))
      (decreases stages) =
  match stages with
  | [] -> ()
  | s :: rest ->
    if acc.ac_halted then plan_admitted w reg node_id rest acc
    else begin
      plan_stage_admitted w reg node_id s acc;
      plan_admitted w reg node_id rest (plan_stage w reg node_id s acc)
    end

(* ───────────────────────────────────────────────────────────────────
   The perform phase — what it records as run is admitted, what it lands
   is `landed`, and what it reports on refusal is `failure`.
   ─────────────────────────────────────────────────────────────────── *)

/// `caps` distributes over `rev` — the staged list is built reversed.
let rec caps_app (#v: Type0) (#p: Type0) (xs: list (staged_call v p)) (ys: list (staged_call v p))
  : Lemma (ensures caps (app xs ys) == app (caps xs) (caps ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> caps_app rest ys

let rec caps_rev (#v: Type0) (#p: Type0) (xs: list (staged_call v p))
  : Lemma (ensures caps (rev xs) == rev (caps xs)) (decreases xs) =
  match xs with
  | [] -> ()
  | c :: rest ->
    caps_rev rest;
    caps_app (rev rest) [c]

let rec perform_admitted (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                         (w: witness t b v o q a eff d) (reg: registry t v o q p)
                         (calls: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires admitted reg.r_gate (caps calls) /\ admitted reg.r_gate acc.ac_externally)
      (ensures admitted reg.r_gate (perform w reg calls acc).ac_externally)
      (decreases calls) =
  match calls with
  | [] -> ()
  | call :: rest ->
    (match reg.r_perf call.sc_performer call.sc_args with
     | RErr _ -> ()
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
       perform_admitted w reg rest landed)

/// The bindings the perform phase leaves are the landed results folded
/// through `Assign`, and nothing else reaches them: a result the
/// performer did not answer `ROk` is never assigned.
let rec perform_store (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (reg: registry t v o q p)
                      (calls: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Lemma
      (ensures
        (let r = perform w reg calls acc in
         r.ac_store.st_tree == acc.ac_store.st_tree /\
         r.ac_store.st_bindings == fold_assign w (landed reg.r_perf calls) acc.ac_store.st_bindings))
      (decreases calls) =
  match calls with
  | [] -> ()
  | call :: rest ->
    (match reg.r_perf call.sc_performer call.sc_args with
     | RErr _ -> ()
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
       perform_store w reg rest landed)

/// The diagnostics the perform phase appends: exactly the first refusal
/// as `PerformFailed`, or nothing.
let rec perform_diag (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (reg: registry t v o q p)
                     (calls: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Lemma
      (ensures
        (let r = perform w reg calls acc in
         rev r.ac_diagnostics ==
           app (rev acc.ac_diagnostics)
               (match failure reg.r_perf calls with
                | ONone -> []
                | OSome (cap, reason) -> [PerformFailed cap reason])))
      (decreases calls) =
  match calls with
  | [] -> app_nil (rev acc.ac_diagnostics)
  | call :: rest ->
    (match reg.r_perf call.sc_performer call.sc_args with
     | RErr _ -> ()
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
       perform_diag w reg rest landed)

(* ───────────────────────────────────────────────────────────────────
   The return contract — what the wrapper lands and what it refuses.
   ─────────────────────────────────────────────────────────────────── *)

/// Every result the WRAPPED performer lands honours its contract: the
/// wrapper answers `ROk` only when the raw performer did and the
/// contract, if any, holds.
let rec landed_honour (#v: Type0) (#p: Type0) (ct: p -> opt (contract v)) (perf: p -> v -> res v)
                      (calls: list (staged_call v p))
  : Lemma (ensures all_honour ct (landed (checked_by ct perf) calls)) (decreases calls) =
  match calls with
  | [] -> ()
  | c :: rest ->
    (match ct c.sc_performer with
     | ONone ->
       (match perf c.sc_performer c.sc_args with
        | RErr _ -> ()
        | ROk _ -> landed_honour ct perf rest)
     | OSome con ->
       (match perf c.sc_performer c.sc_args with
        | RErr _ -> ()
        | ROk result -> if con.ct_holds result then landed_honour ct perf rest else ()))

/// A result the contract rejects is where the wrapped performer refuses,
/// and with the contract's reason: the first violation at position `k`
/// (counted from `k0`) is `fails_at` `k - k0` for the wrapped performer,
/// and the recorded failure is that call's capability and
/// `return-contract:<name>`.
let rec violation_refuses (#v: Type0) (#p: Type0) (ct: p -> opt (contract v)) (perf: p -> v -> res v)
                          (calls: list (staged_call v p)) (k0: nat) (k: nat) (cap: string) (name: string)
  : Lemma
      (requires violation ct perf calls k0 == OSome (k, cap, name))
      (ensures
        k >= k0 /\
        fails_at (checked_by ct perf) calls (k - k0) /\
        failure (checked_by ct perf) calls == OSome (cap, strcat "return-contract:" name))
      (decreases calls) =
  match calls with
  | [] -> ()
  | c :: rest ->
    (match perf c.sc_performer c.sc_args with
     | RErr _ -> ()
     | ROk result ->
       (match ct c.sc_performer with
        | ONone -> violation_refuses ct perf rest (k0 + 1) k cap name
        | OSome con ->
          if con.ct_holds result then violation_refuses ct perf rest (k0 + 1) k cap name
          else ()))

(* ───────────────────────────────────────────────────────────────────
   The plan fold over append, and a halted plan is fixed.
   ─────────────────────────────────────────────────────────────────── *)

let rec plan_halted (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                    (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                    (stages: list (stage a v o q)) (acc: accumulator t b v o eff d p)
  : Lemma (requires acc.ac_halted) (ensures plan w reg node_id stages acc == acc) (decreases stages) =
  match stages with
  | [] -> ()
  | _ :: rest -> plan_halted w reg node_id rest acc

let rec plan_app (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                 (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                 (xs: list (stage a v o q)) (ys: list (stage a v o q)) (acc: accumulator t b v o eff d p)
  : Lemma (ensures plan w reg node_id (app xs ys) acc == plan w reg node_id ys (plan w reg node_id xs acc))
          (decreases xs) =
  match xs with
  | [] -> ()
  | s :: rest ->
    plan_app w reg node_id rest ys (if acc.ac_halted then acc else plan_stage w reg node_id s acc)

(* ───────────────────────────────────────────────────────────────────
   THE THEOREMS
   ─────────────────────────────────────────────────────────────────── *)

/// **`gate_before_perform`.** An effect the gate refuses: the planned
/// accumulator is halted, its diagnostics are EXACTLY ONE new entry —
/// the denial naming the capability, which by the type of `denial`
/// carries nothing else — over what was there, every other field is the
/// entry field, and the result is EQUAL under every performer lookup,
/// every performer behaviour, every argument policy and every witness:
/// none of them is read before the gate has answered. "Reaches no
/// performer" as an equation.
let gate_before_perform (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                        (w: witness t b v o q a eff d) (w': witness t b v o q a eff d)
                        (reg: registry t v o q p)
                        (lookup': string -> opt p) (perf': p -> v -> res v) (policy': server_effect v o q -> opt string)
                        (e: server_effect v o q) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires not (reg.r_gate (capability e)))
      (ensures
        (let r = plan_effect w reg e acc in
         r.ac_halted == true /\
         r.ac_diagnostics == Denied (GateRefused (capability e)) :: acc.ac_diagnostics /\
         r.ac_store == acc.ac_store /\
         r.ac_performed == acc.ac_performed /\
         r.ac_externally == acc.ac_externally /\
         r.ac_staged == acc.ac_staged /\
         r.ac_patches == acc.ac_patches /\
         r.ac_notifications == acc.ac_notifications /\
         r.ac_client_effects == acc.ac_client_effects /\
         r == plan_effect w' ({ reg with r_lookup = lookup'; r_perf = perf'; r_policy = policy' }) e acc)) = ()

/// **`gate_refusal_halts_run`.** The handler-level form: when the stages
/// before a refused effect planned without halting, the handler rolls
/// back to the entry store, commits nothing, reports NOTHING performed,
/// carries no patches, notifications or client effects, and its
/// diagnostics are what those stages produced followed by exactly one
/// denial naming the refused capability. And the outcome is the SAME
/// whatever stages follow the refused one and whatever the performers
/// would have answered: nothing after the refusal is planned, and no
/// performer is reached.
let gate_refusal_halts_run (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                           (w: witness t b v o q a eff d) (reg: registry t v o q p) (perf': p -> v -> res v) (node_id: string)
                           (prefix: list (stage a v o q)) (e: server_effect v o q)
                           (rest: list (stage a v o q)) (rest': list (stage a v o q)) (s: store t b)
  : Lemma
      (requires
        (let before = plan w reg node_id prefix (start s) in
         not before.ac_halted /\ not (reg.r_gate (capability e))))
      (ensures
        (let before = plan w reg node_id prefix (start s) in
         let out = run w reg node_id (app prefix (SEffect e :: rest)) s in
         out.oc_store == s /\
         out.oc_committed == false /\
         out.oc_performed == [] /\
         out.oc_patches == [] /\
         out.oc_notifications == [] /\
         out.oc_client_effects == [] /\
         out.oc_diagnostics == app (rev before.ac_diagnostics) [Denied (GateRefused (capability e))] /\
         out == run w ({ reg with r_perf = perf' }) node_id (app prefix (SEffect e :: rest')) s)) =
  let before = plan w reg node_id prefix (start s) in
  let denied = plan_stage w reg node_id (SEffect e) before in
  plan_pure w reg perf' node_id prefix (start s);
  plan_app w reg node_id prefix (SEffect e :: rest) (start s);
  plan_app w reg node_id prefix (SEffect e :: rest') (start s);
  plan_halted w reg node_id rest denied;
  plan_halted w reg node_id rest' denied;
  let reg' = { reg with r_perf = perf' } in
  plan_app w reg' node_id prefix (SEffect e :: rest') (start s);
  plan_halted w reg' node_id rest' denied

/// **`policy_sufficient`.** For a declared Σ the gate is sufficient for
/// — `Σ []`, and `Σ tr /\ Π c ==> Σ (tr ++ [c])` — every trace the
/// handler produces satisfies Σ: the ghost trace of everything that
/// reached the state or a performer, and `Performed` as the outcome
/// reports it. For every performer, every witness, every program and
/// every entry store: the performer is an abstract parameter here, and
/// nothing it answers can produce a capability the gate did not admit.
let policy_sufficient (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                      (stages: list (stage a v o q)) (s: store t b) (sigma: policy_spec)
  : Lemma
      (requires sufficient_for reg.r_gate sigma)
      (ensures
        (let out = run w reg node_id stages s in
         admitted reg.r_gate out.oc_performed /\
         admitted reg.r_gate (ghost_trace w reg node_id stages s) /\
         sigma out.oc_performed /\
         sigma (ghost_trace w reg node_id stages s))) =
  let planned = plan w reg node_id stages (start s) in
  let staged = rev planned.ac_staged in
  plan_pure w reg reg.r_perf node_id stages (start s);
  plan_admitted w reg node_id stages (start s);
  caps_rev planned.ac_staged;
  admitted_rev reg.r_gate (caps planned.ac_staged);
  admitted_rev reg.r_gate planned.ac_performed;
  let final = if planned.ac_halted then planned else perform w reg staged planned in
  (if planned.ac_halted then () else perform_spec w reg staged planned);
  (if planned.ac_halted then () else perform_admitted w reg staged planned);
  admitted_rev reg.r_gate final.ac_externally;
  admitted_app reg.r_gate (rev final.ac_performed) (rev final.ac_externally);
  admitted_sigma reg.r_gate sigma [] (app (rev final.ac_performed) (rev final.ac_externally));
  admitted_sigma reg.r_gate sigma [] (rev final.ac_externally)

/// **`return_contract`.** With contracts declared on performers — `reg`
/// carries the RAW behaviour, and `wrapped` is production's registry,
/// the behaviour composed with `checked_by` as `registerChecked`
/// composes it — three things hold of the handler. (1) The bindings it
/// leaves are exactly the landed results folded through `Assign`, and
/// (2) every landed result honours its contract: a result is check_return
/// BEFORE it re-enters the interpreter's state, and one the contract
/// rejects never does. (3) When the raw performer's first rejected
/// result is at position `k` — before any raw refusal — the handler
/// rolls back to the entry store, commits nothing, reports `Performed`
/// as exactly the first `k` staged capabilities, and its last diagnostic
/// is `PerformFailed` naming that call's capability and the contract's
/// NAME: a typed refusal, carrying the host's own vocabulary and never
/// the value.
let return_contract (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                    (w: witness t b v o q a eff d) (reg: registry t v o q p) (ct: p -> opt (contract v))
                    (node_id: string) (stages: list (stage a v o q)) (s: store t b)
  : Lemma
      (requires not (plan w reg node_id stages (start s)).ac_halted)
      (ensures
        (let wrapped = { reg with r_perf = checked_by ct reg.r_perf } in
         let planned = plan w wrapped node_id stages (start s) in
         let staged = rev planned.ac_staged in
         let out = run w wrapped node_id stages s in
         let lands = landed wrapped.r_perf staged in
         all_honour ct lands /\
         (out.oc_committed ==> out.oc_store.st_bindings == fold_assign w lands planned.ac_store.st_bindings) /\
         (match violation ct reg.r_perf staged 0 with
          | ONone -> True
          | OSome (k, cap, name) ->
            k < length staged /\
            out.oc_store == s /\
            out.oc_committed == false /\
            out.oc_performed == take k (caps staged) /\
            out.oc_patches == [] /\
            out.oc_notifications == [] /\
            out.oc_client_effects == [] /\
            out.oc_diagnostics ==
              app (rev planned.ac_diagnostics) [PerformFailed cap (strcat "return-contract:" name)]))) =
  let wrapped = { reg with r_perf = checked_by ct reg.r_perf } in
  plan_pure w reg wrapped.r_perf node_id stages (start s);
  let planned = plan w wrapped node_id stages (start s) in
  let staged = rev planned.ac_staged in
  landed_honour ct reg.r_perf staged;
  perform_store w wrapped staged planned;
  perform_diag w wrapped staged planned;
  (match violation ct reg.r_perf staged 0 with
   | ONone -> ()
   | OSome (k, cap, name) ->
     violation_refuses ct reg.r_perf staged 0 k cap name;
     residual_is_prefix w wrapped node_id stages s k)
