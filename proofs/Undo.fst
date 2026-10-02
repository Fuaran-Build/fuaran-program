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

/// Phase 1977 — the undo posture, modelled in F* and proved: a handler
/// classed reversible from its declared form is undone to its entry
/// state by running the inverses of the plan in reverse plan order; one
/// classed compensable reaches the compensated state; and the first step
/// an undo cannot perform is named exactly, before anything is undone.
///
/// # What this module is
///
/// The undo half of the server placement, over Phase 1717's handler
/// model: `Undo.posture` (`src/Fuaran.Program.Server/Undo.fs`) read off
/// a handler's stages and the state witness's `Undo` member, and
/// `Undo.run`, which takes the TRAIL a committed `Handler.runPlanned`
/// recorded — every edit with the state it was applied to, every compute
/// stage with what restores its binding writes, every step that reached
/// the world — and performs the inverses (or the declared compensations)
/// through the SAME handler, the same gate, the same argument policy and
/// the same registered performers the forward run used. Nothing here
/// re-models the handler: this module `open`s `Staging` and proves over
/// its `run`, its `plan_ops` and its trail, exactly as `EffectGate.fst`
/// proves over its gate. What is new at runtime is the member, the
/// classifier, the trail and the one undo run; the differential host
/// (`tests/Fuaran.Program.Server.Tests/ProofOracleTests.fs`, the
/// `Phase 1977` list) runs the extraction of this module beside
/// production over the staging corpus with the scripted performer
/// refusing at every position of the undo's own staged list.
///
/// # The member the model argues for
///
/// The phase's shape to argue against was `Undo: 'Op -> 'Node ->
/// UndoClass<'Op>`, the class computed against the pre-state. The model
/// argues for the CLASS as a function of the op alone and the INVERSE
/// as a function of the pre-state — `Undo: 'Op -> UndoClass<'Node,
/// 'Op>` with `Inverse of ('Node -> 'Op list)` — for one reason the
/// theorems make exact: the posture is read BEFORE the run, from the
/// declared form, where no pre-state exists, and `undo_run_restores`
/// ties that static reading to the dynamic trail only because the class
/// the posture read of an op is the class the undo meets for the same
/// op, whatever state it was applied to. A member whose case could
/// depend on the state would need a coherence assumption the type now
/// carries for free. The inverse is a LIST because the UI witness's
/// inverse is a diff, and a diff is a list.
///
/// # What is opaque, and why
///
/// Everything `Staging.fst` keeps opaque stays so. The class arrow
/// (`cls`, production's `StateWitness.Undo`) is a parameter, and the one
/// law a reversible undo rests on is stated as a hypothesis and named in
/// every theorem that needs it: `inverse_law`, that an op's declared
/// inverse, computed from the state the op was applied to, applies to
/// the state the op produced and answers the state it was applied to.
/// That law is the witness's obligation (docs/generic-tier.md §3.5, K9),
/// as K4's read-after-write law is for `Read`. A compute stage's
/// restorer is opaque (`w_undo_compute`): what undoes the fold's binding
/// writes is BoundedFold's `reverse_run` (Phase 1976), proved there and
/// applied here, never re-proved.
///
module Undo

open Staging

(* ───────────────────────────────────────────────────────────────────
   The vocabulary — `UndoClass`, the undo defects and the verdict.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `UndoClass<'Node, 'Op>` — what an op IS to an undo. An EXACT
/// inverse, computed from the pre-state when the plan holds it; a
/// declared COMPENSATION, likewise (a saga's compensating action, which
/// may read the pre-state and obeys no law); or neither, with the
/// domain's reason.
noeq type undo_class (t: Type0) (o: Type0) =
  | Inverse : inverse: (t -> list o) -> undo_class t o
  | Compensate : compensation: (t -> list o) -> undo_class t o
  | OneWay : reason: string -> undo_class t o

/// F#: `UndoDefect` — WHY a handler is not provably reversible, a closed
/// vocabulary of derived facts, as the replay classification's is.
type defect =
  | CompensatedOp
  | OneWayOp
  | OpaqueHostCall
  | OutboundNotification
  | EmittedPatch
  | ComputeOutsideFragment

/// F#: `UndoVerdict`.
type verdict =
  | Reversible
  | Compensable
  | OneWayVerdict
  | Unknown

/// F#: `Undo.gradeOfDefect`. Two defects are PROOFS that an op or a
/// stage has a compensation and not an inverse, or neither; two say a
/// step reached the world with no inverse vocabulary at all; and two —
/// an emitted patch, a compute stage outside Phase 1976's reversible
/// fragment — are places this classification does not decide.
let grade (d: defect) : verdict =
  match d with
  | CompensatedOp -> Compensable
  | OneWayOp -> OneWayVerdict
  | OpaqueHostCall -> OneWayVerdict
  | OutboundNotification -> OneWayVerdict
  | EmittedPatch -> Unknown
  | ComputeOutsideFragment -> Unknown

/// F#: `Undo.worst`. One-way dominates unknown, which dominates
/// compensable, which dominates reversible: a proof that a step cannot
/// be undone outranks a place the walk could not decide, and either
/// outranks a declared compensation.
let worst (x: verdict) (y: verdict) : verdict =
  if OneWayVerdict? x || OneWayVerdict? y then OneWayVerdict
  else if Unknown? x || Unknown? y then Unknown
  else if Compensable? x || Compensable? y then Compensable
  else Reversible

/// F#: `Undo.verdictOfDefects`.
let rec verdict_of (ds: list defect) : Tot verdict (decreases ds) =
  match ds with
  | [] -> Reversible
  | d :: rest -> worst (grade d) (verdict_of rest)

(* ───────────────────────────────────────────────────────────────────
   THE CLASSIFIER — `Undo.posture`, read off the declared form.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: the `ApplyOps` arm of `Undo.defectsOfStage` — the defects of an op
/// sequence, to exhaustion: an edit's class, a guard nothing, a branch
/// BOTH arms (an untaken arm still counts, because which arm runs is not
/// decided from the form), a repeat its body once.
let rec view_defects (#t: Type0) (#o: Type0) (cls: o -> undo_class t o) (vs: list (op_view o))
  : Tot (list defect) (decreases %[vs; 1]) =
  match vs with
  | [] -> []
  | x :: rest -> app (view_defect cls x) (view_defects cls rest)

and view_defect (#t: Type0) (#o: Type0) (cls: o -> undo_class t o) (x: op_view o)
  : Tot (list defect) (decreases %[x; 0]) =
  match x with
  | OEdit op ->
    (match cls op with
     | Inverse _ -> []
     | Compensate _ -> [CompensatedOp]
     | OneWay _ -> [OneWayOp])
  | ORequire _ -> []
  | OChoose _ when_true when_false _ -> app (view_defects cls when_true) (view_defects cls when_false)
  | ORepeat _ body -> view_defects cls body
  // A per-element iteration (Phase 1990): the defects of its lowered
  // form — every element's body, since an op's class may differ once the
  // element is substituted into it.
  | OEach elements -> view_defects_each cls elements

and view_defects_each (#t: Type0) (#o: Type0) (cls: o -> undo_class t o) (elements: list (list (op_view o)))
  : Tot (list defect) (decreases %[elements; 1]) =
  match elements with
  | [] -> []
  | el :: rest -> app (view_defects cls el) (view_defects_each cls rest)

/// F#: `List.contains`.
let rec mem (d: defect) (ds: list defect) : Tot bool (decreases ds) =
  match ds with
  | [] -> false
  | x :: rest -> d = x || mem d rest

/// F#: `List.distinct` — the FIRST occurrence of each defect kept, in
/// order; reasons within one stage are distinct, as the replay
/// classification's are.
let rec distinct_from (seen: list defect) (ds: list defect) : Tot (list defect) (decreases ds) =
  match ds with
  | [] -> []
  | d :: rest -> if mem d seen then distinct_from seen rest else d :: distinct_from (d :: seen) rest

/// F#: `Undo.defectsOfStage`. A compute stage is read through Phase
/// 1976's reversible fragment (`reversible`, production's
/// `BoundedActions.reversible`, opaque here as the fold is); a read
/// lands a table and reaches nothing; the op arm reads the member; a
/// host call and a notification reached the world; a patch is the host's
/// to apply.
let stage_defects (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                  (w: witness t b v o q a eff d) (cls: o -> undo_class t o) (reversible: a -> bool)
                  (s: stage a v o q)
  : list defect =
  match s with
  | SCompute action -> if reversible action then [] else [ComputeOutsideFragment]
  | SEffect e ->
    (match e with
     | RunQuery _ _ -> []
     | ApplyOps ops -> distinct_from [] (view_defects cls (views w ops))
     | HostCall _ _ _ -> [OpaqueHostCall]
     | EmitPatch _ -> [EmittedPatch]
     | Notify _ _ -> [OutboundNotification])

/// One stage's defects, positioned at its ordinal.
let rec positioned (k: nat) (ds: list defect) : Tot (list (nat & defect)) (decreases ds) =
  match ds with
  | [] -> []
  | d :: rest -> (k, d) :: positioned k rest

/// F#: `Undo.reasons` — every defect, positioned by its stage ordinal, in
/// stage order.
let rec reasons_from (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                     (w: witness t b v o q a eff d) (cls: o -> undo_class t o) (reversible: a -> bool)
                     (stages: list (stage a v o q)) (k: nat)
  : Tot (list (nat & defect)) (decreases stages) =
  match stages with
  | [] -> []
  | s :: rest -> app (positioned k (stage_defects w cls reversible s)) (reasons_from w cls reversible rest (k + 1))

let reasons (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
            (w: witness t b v o q a eff d) (cls: o -> undo_class t o) (reversible: a -> bool)
            (stages: list (stage a v o q))
  : list (nat & defect) =
  reasons_from w cls reversible stages 0

let rec defects_of (rs: list (nat & defect)) : Tot (list defect) (decreases rs) =
  match rs with
  | [] -> []
  | (_, d) :: rest -> d :: defects_of rest

/// F#: `Undo.posture` — the verdict the reasons carry: no reason is the
/// only proof of `Reversible`.
let posture (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
            (w: witness t b v o q a eff d) (cls: o -> undo_class t o) (reversible: a -> bool)
            (stages: list (stage a v o q))
  : verdict =
  verdict_of (defects_of (reasons w cls reversible stages))

(* ───────────────────────────────────────────────────────────────────
   THE UNDO RUN — `Undo.run`: the refusal, the inverse plan, the one
   handler run, and the bindings restored.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `UndoRefusal`. The code, the step's ordinal in the plan, and the
/// reason — a capability, or the domain's own one-way reason.
type refusal = {
  rf_code: string;
  rf_step: nat;
  rf_reason: string;
}

let render (r: refusal) : string =
  strcat (strcat (strcat r.rf_code "@") (string_of_int r.rf_step)) (strcat ": " r.rf_reason)

/// F#: `Undo.firstRefused` — the first step of the plan the undo cannot
/// perform, with its ordinal: a one-way op, a step that reached the
/// world, a compute stage with no restorer, an emitted patch. `ONone`
/// when every step can be undone.
let rec first_refused (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (steps: list (step t b o)) (k: nat)
  : Tot (opt refusal) (decreases steps) =
  match steps with
  | [] -> ONone
  | TEdit _ op :: rest ->
    (match cls op with
     | OneWay reason -> OSome { rf_code = "undo-one-way-step"; rf_step = k; rf_reason = reason }
     | _ -> first_refused cls rest (k + 1))
  | TCompute ONone :: _ ->
    OSome { rf_code = "undo-undecidable-step"; rf_step = k;
            rf_reason = "a compute stage outside the reversible fragment, or whose trace is not restorable" }
  | TCompute (OSome _) :: rest -> first_refused cls rest (k + 1)
  | TReached cap :: _ -> OSome { rf_code = "undo-one-way-step"; rf_step = k; rf_reason = cap }
  | TEmitted cap :: _ -> OSome { rf_code = "undo-undecidable-step"; rf_step = k; rf_reason = cap }

/// The ops that undo one recorded edit: its inverse or its compensation
/// computed from the pre-state; nothing for a one-way op, which the
/// refusal above reaches first.
let undo_ops (#t: Type0) (#o: Type0) (cls: o -> undo_class t o) (pre: t) (op: o) : list o =
  match cls op with
  | Inverse f -> f pre
  | Compensate f -> f pre
  | OneWay _ -> []

/// F#: `Undo.inversePlan` — the inverse PLAN: over the trail in REVERSE
/// plan order (the accumulator's own order, the last edit first), each
/// edit's undo ops in turn. The one op sequence the undo run performs.
let rec inverse_plan (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (reversed: list (step t b o))
  : Tot (list o) (decreases reversed) =
  match reversed with
  | [] -> []
  | TEdit pre op :: rest -> app (undo_ops cls pre op) (inverse_plan cls rest)
  | _ :: rest -> inverse_plan cls rest

/// Whether every edit of the plan has an EXACT inverse — the plan the
/// drift check below applies to.
let rec all_inverse (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (steps: list (step t b o))
  : Tot bool (decreases steps) =
  match steps with
  | [] -> true
  | TEdit _ op :: rest -> Inverse? (cls op) && all_inverse cls rest
  | _ :: rest -> all_inverse cls rest

/// F#: `Undo.foldInverses` — each op applied in turn through the
/// witness's apply, with no gate, no view and no performer: the pure
/// fold the drift check reads and the theorems are stated in.
let rec apply_all (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                  (w: witness t b v o q a eff d) (ops: list o) (tree: t)
  : Tot (res t) (decreases ops) =
  match ops with
  | [] -> ROk tree
  | op :: rest ->
    (match w.w_apply op tree with
     | RErr code -> RErr code
     | ROk tree' -> apply_all w rest tree')

/// F#: `Undo.restoreBindings` — the compute stages' restorers applied in
/// reverse stage order to the bindings the undo's handler run left.
let rec restore (#t: Type0) (#b: Type0) (#o: Type0) (steps: list (step t b o)) (bindings: b)
  : Tot b (decreases steps) =
  match steps with
  | [] -> bindings
  | TCompute (OSome f) :: rest -> f (restore rest bindings)
  | _ :: rest -> restore rest bindings

/// F#: `Undo.run`. In this order: a plan that did not commit is refused
/// (a rollback left nothing to undo; a perform-phase residual is
/// `residual_is_prefix`'s to report, not this run's); the first step the
/// undo cannot perform is refused, NAMED, before anything is undone; a
/// plan whose every edit has an exact inverse is checked to fold back to
/// the recorded entry state before anything performs — a witness whose
/// inverse breaks its law is refused rather than performed; then the
/// inverse plan runs as ONE `ApplyOps` effect through `run` — the gate,
/// the argument policy and the registered performers the forward run
/// went through — and, on commit, the compute stages' restorers are
/// applied to the bindings in reverse stage order.
let undo_run (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
             (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
             (cls: o -> undo_class t o) (canonical: t -> string)
             (entry: t) (committed: bool) (steps: list (step t b o)) (post: store t b)
  : res (outcome t b v o eff d) =
  if not committed then
    RErr (render { rf_code = "undo-uncommitted-plan"; rf_step = 0; rf_reason = "the run rolled back" })
  else
    match first_refused cls steps 0 with
    | OSome r -> RErr (render r)
    | ONone ->
      let inv = inverse_plan cls (rev steps) in
      let drifted =
        all_inverse cls steps &&
        (match apply_all w inv post.st_tree with
         | RErr _ -> true
         | ROk restored -> not (canonical restored = canonical entry))
      in
      if drifted then
        RErr (render { rf_code = "undo-inverse-drift"; rf_step = 0;
                       rf_reason = "the declared inverses do not fold back to the recorded entry state" })
      else
        let out = run w reg node_id [SEffect (ApplyOps inv)] post in
        if out.oc_committed then
          ROk { out with oc_store = { out.oc_store with st_bindings = restore steps out.oc_store.st_bindings } }
        else ROk out

(* ───────────────────────────────────────────────────────────────────
   THE THEOREMS

   Everything from here on is ghost or a lemma.
   ─────────────────────────────────────────────────────────────────── *)

/// The tree a plan answers, or its refusal — what `trail_agrees` compares.
[@@ noextract_to "FSharp"]
let tree_of (#t: Type0) (#x: Type0) (r: res (t & x)) : res t =
  match r with
  | RErr code -> RErr code
  | ROk pair -> ROk (fst pair)

/// One step of `trail_views`, `trail_view` and `trail_repeat` as
/// equations, for the recursive lemmas to call (Staging's reason: inside
/// a recursive lemma the solver does not unfold these itself).
let trail_views_nil (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                    (w: witness t b v o q a eff d) (tree: t)
  : Lemma (trail_views w [] tree == ROk (tree, [])) = ()

let trail_views_cons (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                     (w: witness t b v o q a eff d) (x: op_view o) (rest: list (op_view o)) (tree: t)
  : Lemma
      (trail_views w (x :: rest) tree ==
       (match trail_view w x tree with
        | RErr code -> RErr code
        | ROk r1 ->
          (match trail_views w rest (fst r1) with
           | RErr code -> RErr code
           | ROk r2 -> ROk (fst r2, app (snd r1) (snd r2))))) = ()

let trail_view_require (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                       (w: witness t b v o q a eff d) (op: o) (tree: t)
  : Lemma
      (trail_view w (ORequire op) tree ==
       (match w.w_apply op tree with
        | RErr code -> RErr code
        | ROk _ -> ROk (tree, []))) = ()

let trail_view_edit (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                    (w: witness t b v o q a eff d) (op: o) (tree: t)
  : Lemma
      (trail_view w (OEdit op) tree ==
       (match w.w_apply op tree with
        | RErr code -> RErr code
        | ROk tree' -> ROk (tree', [(tree, op)]))) = ()

let trail_view_choose (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                      (w: witness t b v o q a eff d)
                      (entry: o) (when_true: list (op_view o)) (when_false: list (op_view o)) (exit: opt o) (tree: t)
  : Lemma
      (trail_view w (OChoose entry when_true when_false exit) tree ==
       (let took_true = ROk? (w.w_apply entry tree) in
        let armed =
          if took_true then trail_views w when_true tree
          else trail_views w when_false tree
        in
        (match armed with
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
                 else ROk r))))) = ()

let trail_view_repeat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                      (w: witness t b v o q a eff d) (n: nat) (body: list (op_view o)) (tree: t)
  : Lemma (trail_view w (ORepeat n body) tree == trail_repeat w body n tree) = ()

let trail_repeat_zero (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                      (w: witness t b v o q a eff d) (body: list (op_view o)) (tree: t)
  : Lemma (trail_repeat w body 0 tree == ROk (tree, [])) = ()

let trail_repeat_step (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                      (w: witness t b v o q a eff d) (body: list (op_view o)) (n: nat) (tree: t)
  : Lemma
      (requires n > 0)
      (ensures
        trail_repeat w body n tree ==
        (match trail_views w body tree with
         | RErr code -> RErr code
         | ROk r1 ->
           (match trail_repeat w body (n - 1) (fst r1) with
            | RErr code -> RErr code
            | ROk r2 -> ROk (fst r2, app (snd r1) (snd r2))))) = ()

let trail_view_each (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                    (w: witness t b v o q a eff d) (elements: list (list (op_view o))) (tree: t)
  : Lemma (trail_view w (OEach elements) tree == trail_each w elements tree) = ()

let trail_each_nil (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                   (w: witness t b v o q a eff d) (tree: t)
  : Lemma (trail_each w [] tree == ROk (tree, [])) = ()

let trail_each_cons (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                    (w: witness t b v o q a eff d) (el: list (op_view o)) (rest: list (list (op_view o))) (tree: t)
  : Lemma
      (trail_each w (el :: rest) tree ==
       (match trail_views w el tree with
        | RErr code -> RErr code
        | ROk r1 ->
          (match trail_each w rest (fst r1) with
           | RErr code -> RErr code
           | ROk r2 -> ROk (fst r2, app (snd r1) (snd r2))))) = ()

/// The plan's refusal, and its planned tree, are the trail's: the two
/// walks take the same arms, apply the same ops to the same states, and
/// refuse on the same reason. Over the views, every shape.
let rec trail_agrees_views (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                           (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                           (vs: list (op_view o)) (tree: t) (staged: list (staged_call v p))
  : Lemma (ensures tree_of (plan_views w cap stage vs tree staged) == tree_of (trail_views w vs tree))
          (decreases %[vs; 1; 0]) =
  match vs with
  | [] -> plan_views_nil w cap stage tree staged; trail_views_nil w tree
  | x :: rest ->
    plan_views_cons w cap stage x rest tree staged;
    trail_views_cons w x rest tree;
    trail_agrees_view w cap stage x tree staged;
    let s : res (t & list (staged_call v p)) = plan_view w cap stage x tree staged in
    let u : res (t & list (t & o)) = trail_view w x tree in
    (match s, u with
     | ROk r, ROk r' -> trail_agrees_views w cap stage rest (fst r) (snd r)
     | _ -> ())

and trail_agrees_view (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                      (x: op_view o) (tree: t) (staged: list (staged_call v p))
  : Lemma (ensures tree_of (plan_view w cap stage x tree staged) == tree_of (trail_view w x tree))
          (decreases %[x; 0; 0]) =
  match x with
  | ORequire op -> plan_view_require w cap stage op tree staged; trail_view_require w op tree
  | OEdit op -> plan_view_edit w cap stage op tree staged; trail_view_edit w op tree
  | OChoose entry when_true when_false exit ->
    plan_view_choose w cap stage entry when_true when_false exit tree staged;
    trail_view_choose w entry when_true when_false exit tree;
    let e : res t = w.w_apply entry tree in
    (if ROk? e then trail_agrees_views w cap stage when_true tree staged
     else trail_agrees_views w cap stage when_false tree staged)
  | ORepeat n body ->
    plan_view_repeat w cap stage n body tree staged;
    trail_view_repeat w n body tree;
    trail_agrees_repeat w cap stage body n tree staged
  | OEach elements ->
    plan_view_each w cap stage elements tree staged;
    trail_view_each w elements tree;
    trail_agrees_each w cap stage elements tree staged

and trail_agrees_each (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                      (elements: list (list (op_view o))) (tree: t) (staged: list (staged_call v p))
  : Lemma (ensures tree_of (plan_each w cap stage elements tree staged) == tree_of (trail_each w elements tree))
          (decreases %[elements; 1; 0]) =
  match elements with
  | [] -> plan_each_nil w cap stage tree staged; trail_each_nil w tree
  | el :: rest ->
    plan_each_cons w cap stage el rest tree staged;
    trail_each_cons w el rest tree;
    trail_agrees_views w cap stage el tree staged;
    let s : res (t & list (staged_call v p)) = plan_views w cap stage el tree staged in
    let u : res (t & list (t & o)) = trail_views w el tree in
    (match s, u with
     | ROk r, ROk r' -> trail_agrees_each w cap stage rest (fst r) (snd r)
     | _ -> ())

and trail_agrees_repeat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                        (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                        (body: list (op_view o)) (n: nat) (tree: t) (staged: list (staged_call v p))
  : Lemma (ensures tree_of (plan_repeat w cap stage body n tree staged) == tree_of (trail_repeat w body n tree))
          (decreases %[body; 2; n]) =
  if n = 0 then begin plan_repeat_zero w cap stage body tree staged; trail_repeat_zero w body tree end
  else begin
    plan_repeat_step w cap stage body n tree staged;
    trail_repeat_step w body n tree;
    trail_agrees_views w cap stage body tree staged;
    let s : res (t & list (staged_call v p)) = plan_views w cap stage body tree staged in
    let u : res (t & list (t & o)) = trail_views w body tree in
    (match s, u with
     | ROk r, ROk r' -> trail_agrees_repeat w cap stage body (n - 1) (fst r) (snd r)
     | _ -> ())
  end

/// **`trail_agrees`.** Planning an op sequence and walking its trail
/// refuse alike and answer the same tree, so the trail the plan phase
/// records is a trail OF the plan it made.
let trail_agrees (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                 (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                 (ops: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma (tree_of (plan_ops w cap stage ops tree staged) == tree_of (trail_views w (views w ops) tree)) =
  trail_agrees_views w cap stage (views w ops) tree staged

(* ───────────────────────────────────────────────────────────────────
   The chain: a trail is a sequence of applies, each from the state the
   one before it produced.
   ─────────────────────────────────────────────────────────────────── *)

/// `(pre, op)` pairs in PLAN order chain `tree` to `tree'`: each op
/// applies to its recorded pre-state, which is the state the previous
/// op produced, and the last produces `tree'`.
[@@ noextract_to "FSharp"]
let rec chain (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
              (w: witness t b v o q a eff d) (tree: t) (recorded: list (t & o)) (tree': t)
  : Tot prop (decreases recorded) =
  match recorded with
  | [] -> tree' == tree
  | (pre, op) :: rest ->
    pre == tree /\
    (match w.w_apply op pre with
     | RErr _ -> False
     | ROk next -> chain w next rest tree')

let rec chain_app (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                  (w: witness t b v o q a eff d) (x: t) (xs: list (t & o)) (y: t) (ys: list (t & o)) (z: t)
  : Lemma (requires chain w x xs y /\ chain w y ys z) (ensures chain w x (app xs ys) z) (decreases xs) =
  match xs with
  | [] -> ()
  | (pre, op) :: rest ->
    let e : res t = w.w_apply op pre in
    (match e with
     | RErr _ -> ()
     | ROk next -> chain_app w next rest y ys z)

/// The trail a walk records chains the state it started from to the
/// state it answers.
let rec trail_chain_views (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                          (w: witness t b v o q a eff d) (vs: list (op_view o)) (tree: t)
  : Lemma
      (ensures
        (let r = trail_views w vs tree in
         ROk? r ==> chain w tree (snd (ROk?.value r)) (fst (ROk?.value r))))
      (decreases %[vs; 1; 0]) =
  match vs with
  | [] -> trail_views_nil w tree
  | x :: rest ->
    trail_views_cons w x rest tree;
    trail_chain_view w x tree;
    let u : res (t & list (t & o)) = trail_view w x tree in
    (match u with
     | RErr _ -> ()
     | ROk r1 ->
       trail_chain_views w rest (fst r1);
       let u2 : res (t & list (t & o)) = trail_views w rest (fst r1) in
       (match u2 with
        | RErr _ -> ()
        | ROk r2 -> chain_app w tree (snd r1) (fst r1) (snd r2) (fst r2)))

and trail_chain_view (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                     (w: witness t b v o q a eff d) (x: op_view o) (tree: t)
  : Lemma
      (ensures
        (let r = trail_view w x tree in
         ROk? r ==> chain w tree (snd (ROk?.value r)) (fst (ROk?.value r))))
      (decreases %[x; 0; 0]) =
  match x with
  | ORequire op -> trail_view_require w op tree
  | OEdit op -> trail_view_edit w op tree
  | OChoose entry when_true when_false exit ->
    trail_view_choose w entry when_true when_false exit tree;
    let e : res t = w.w_apply entry tree in
    (if ROk? e then trail_chain_views w when_true tree
     else trail_chain_views w when_false tree)
  | ORepeat n body ->
    trail_view_repeat w n body tree;
    trail_chain_repeat w body n tree
  | OEach elements ->
    trail_view_each w elements tree;
    trail_chain_each w elements tree

and trail_chain_each (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                     (w: witness t b v o q a eff d) (elements: list (list (op_view o))) (tree: t)
  : Lemma
      (ensures
        (let r = trail_each w elements tree in
         ROk? r ==> chain w tree (snd (ROk?.value r)) (fst (ROk?.value r))))
      (decreases %[elements; 1; 0]) =
  match elements with
  | [] -> trail_each_nil w tree
  | el :: rest ->
    trail_each_cons w el rest tree;
    trail_chain_views w el tree;
    let u : res (t & list (t & o)) = trail_views w el tree in
    (match u with
     | RErr _ -> ()
     | ROk r1 ->
       trail_chain_each w rest (fst r1);
       let u2 : res (t & list (t & o)) = trail_each w rest (fst r1) in
       (match u2 with
        | RErr _ -> ()
        | ROk r2 -> chain_app w tree (snd r1) (fst r1) (snd r2) (fst r2)))

and trail_chain_repeat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                       (w: witness t b v o q a eff d) (body: list (op_view o)) (n: nat) (tree: t)
  : Lemma
      (ensures
        (let r = trail_repeat w body n tree in
         ROk? r ==> chain w tree (snd (ROk?.value r)) (fst (ROk?.value r))))
      (decreases %[body; 2; n]) =
  if n = 0 then trail_repeat_zero w body tree
  else begin
    trail_repeat_step w body n tree;
    trail_chain_views w body tree;
    let u : res (t & list (t & o)) = trail_views w body tree in
    (match u with
     | RErr _ -> ()
     | ROk r1 ->
       trail_chain_repeat w body (n - 1) (fst r1);
       let u2 : res (t & list (t & o)) = trail_repeat w body (n - 1) (fst r1) in
       (match u2 with
        | RErr _ -> ()
        | ROk r2 -> chain_app w tree (snd r1) (fst r1) (snd r2) (fst r2)))
  end

/// The trail an `ApplyOps` arm records, when its plan succeeded, chains
/// the state the arm was handed to the state the plan answers.
let trail_ops_chain (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                    (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                    (ops: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (ensures
        (let r = plan_ops w cap stage ops tree staged in
         ROk? r ==> chain w tree (trail_ops w ops tree) (fst (ROk?.value r)))) =
  trail_agrees w cap stage ops tree staged;
  trail_chain_views w (views w ops) tree

(* ───────────────────────────────────────────────────────────────────
   The reversed chain, over steps: what the accumulator's trail says.
   ─────────────────────────────────────────────────────────────────── *)

/// The accumulator's trail — REVERSED, the last step first — chains the
/// entry state to the state the accumulator holds: an edit at the head
/// was applied to its pre-state, which the rest of the trail chains the
/// entry state to, and produced the state held; a step that is not an
/// edit moved the tree not at all.
[@@ noextract_to "FStar"]
let rec rchain (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
               (w: witness t b v o q a eff d) (entry: t) (reversed: list (step t b o)) (tree: t)
  : Tot prop (decreases reversed) =
  match reversed with
  | [] -> tree == entry
  | TEdit pre op :: rest -> rchain w entry rest pre /\ w.w_apply op pre == ROk tree
  | _ :: rest -> rchain w entry rest tree

let rev_cons (#x: Type0) (y: x) (xs: list x)
  : Lemma (ensures rev (y :: xs) == app (rev xs) [y]) (decreases xs) = ()

/// Prepending a chained plan-order trail, reversed, onto a reversed
/// chain extends it.
let rec rchain_extend (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                      (w: witness t b v o q a eff d) (entry: t) (reversed: list (step t b o))
                      (tree: t) (recorded: list (t & o)) (tree': t)
  : Lemma
      (requires rchain w entry reversed tree /\ chain w tree recorded tree')
      (ensures rchain w entry (app (rev (edits recorded)) reversed) tree')
      (decreases recorded) =
  match recorded with
  | [] -> ()
  | (pre, op) :: rest ->
    let e : res t = w.w_apply op pre in
    (match e with
     | RErr _ -> ()
     | ROk next ->
       // One edit onto the reversed chain, then the rest.
       assert (rchain w entry (TEdit pre op :: reversed) next);
       rchain_extend w entry (TEdit pre op :: reversed) next rest tree';
       app_assoc (rev (edits rest)) [TEdit pre op] reversed)

/// One stage preserves the reversed chain.
let plan_stage_rchain (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                      (entry: t) (s: stage a v o q) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires rchain w entry acc.ac_trail acc.ac_store.st_tree)
      (ensures
        (let r = plan_stage w reg node_id s acc in
         rchain w entry r.ac_trail r.ac_store.st_tree)) =
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
            let r : res (t & list (staged_call v p)) =
              plan_ops w cap reg.r_op_perform ops acc.ac_store.st_tree acc.ac_staged in
            (match r with
             | RErr _ -> ()
             | ROk planned ->
               trail_ops_chain w cap reg.r_op_perform ops acc.ac_store.st_tree acc.ac_staged;
               rchain_extend w entry acc.ac_trail acc.ac_store.st_tree
                             (trail_ops w ops acc.ac_store.st_tree) (fst planned))
          | HostCall fn _ into ->
            (match reg.r_lookup fn with
             | ONone -> ()
             | OSome _ ->
               (match slot_refused w into with
                | OSome _ -> ()
                | ONone -> ()))
          | EmitPatch _ -> ()
          | Notify _ _ -> ()))

/// The plan phase preserves the reversed chain, stage by stage.
let rec plan_rchain (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                    (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                    (entry: t) (stages: list (stage a v o q)) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires rchain w entry acc.ac_trail acc.ac_store.st_tree)
      (ensures
        (let r = plan w reg node_id stages acc in
         rchain w entry r.ac_trail r.ac_store.st_tree))
      (decreases stages) =
  match stages with
  | [] -> ()
  | s :: rest ->
    if acc.ac_halted then plan_rchain w reg node_id entry rest acc
    else begin
      plan_stage_rchain w reg node_id entry s acc;
      plan_rchain w reg node_id entry rest (plan_stage w reg node_id s acc)
    end

/// The perform phase moves neither the tree nor the trail.
let rec perform_keeps (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (reg: registry t v o q p)
                      (calls: list (staged_call v p)) (acc: accumulator t b v o eff d p)
  : Lemma
      (ensures
        (let r = perform w reg calls acc in
         r.ac_store.st_tree == acc.ac_store.st_tree /\ r.ac_trail == acc.ac_trail))
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
       perform_keeps w reg rest landed)

/// `rev` is an involution.
let rec rev_app (#x: Type0) (xs: list x) (ys: list x)
  : Lemma (ensures rev (app xs ys) == app (rev ys) (rev xs)) (decreases xs) =
  match xs with
  | [] -> app_nil (rev ys)
  | y :: rest -> rev_app rest ys; app_assoc (rev ys) (rev rest) [y]

let rec rev_rev (#x: Type0) (xs: list x)
  : Lemma (ensures rev (rev xs) == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | y :: rest -> rev_app (rev rest) [y]; rev_rev rest

/// `run` is `run_planned`'s outcome.
let run_is_run_planned (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                       (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                       (stages: list (stage a v o q)) (s: store t b)
  : Lemma (run w reg node_id stages s == fst (run_planned w reg node_id stages s)) = ()

/// **`run_planned_chain`.** The trail a COMMITTED run answers, reversed,
/// chains the entry state to the state the outcome holds: every edit was
/// applied to the pre-state recorded with it, which the edits before it
/// produced from the entry state, and the last produced the committed
/// state. The record an undo reads is a record of the run it undoes.
let run_planned_chain (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                      (stages: list (stage a v o q)) (s: store t b)
  : Lemma
      (ensures
        (let (out, steps) = run_planned w reg node_id stages s in
         out.oc_committed ==> rchain w s.st_tree (rev steps) out.oc_store.st_tree)) =
  let planned = plan w reg node_id stages (start s) in
  plan_rchain w reg node_id s.st_tree stages (start s);
  if planned.ac_halted then ()
  else begin
    perform_keeps w reg (rev planned.ac_staged) planned;
    rev_rev planned.ac_trail
  end

(* ───────────────────────────────────────────────────────────────────
   The inverse law, and what the inverse plan folds to.
   ─────────────────────────────────────────────────────────────────── *)

/// THE WITNESS'S OBLIGATION (K9): an op's declared exact inverse,
/// computed from the state the op was applied to, applies to the state
/// the op produced and answers the state it was applied to. A
/// hypothesis of `undo_run_restores` and nothing else; a compensation
/// obeys no such law, which is the whole difference between the two
/// classes.
[@@ noextract_to "FSharp"]
let inverse_law (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                (w: witness t b v o q a eff d) (cls: o -> undo_class t o) : Type =
  (op: o) -> (pre: t) -> (post: t) -> (f: (t -> list o)) ->
    Lemma (requires w.w_apply op pre == ROk post /\ cls op == Inverse f)
          (ensures apply_all w (f pre) post == ROk pre)

let rec apply_all_app (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                      (w: witness t b v o q a eff d) (xs: list o) (ys: list o) (tree: t)
  : Lemma
      (ensures
        apply_all w (app xs ys) tree ==
        (match apply_all w xs tree with
         | RErr code -> RErr code
         | ROk tree' -> apply_all w ys tree'))
      (decreases xs) =
  match xs with
  | [] -> ()
  | op :: rest ->
    let e : res t = w.w_apply op tree in
    (match e with
     | RErr _ -> ()
     | ROk tree' -> apply_all_app w rest ys tree')

/// **`inverse_plan_restores`.** Over a reversed chain whose every edit
/// has an exact inverse, under the inverse law, the inverse plan folds
/// the state the chain reached back to the state it started from: the
/// last edit's inverse answers its pre-state, which is what the rest of
/// the chain reached, and so on down to the entry state.
let rec inverse_plan_restores (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                              (w: witness t b v o q a eff d) (cls: o -> undo_class t o) (law: inverse_law w cls)
                              (entry: t) (reversed: list (step t b o)) (tree: t)
  : Lemma
      (requires all_inverse cls reversed /\ rchain w entry reversed tree)
      (ensures apply_all w (inverse_plan cls reversed) tree == ROk entry)
      (decreases reversed) =
  match reversed with
  | [] -> ()
  | TEdit pre op :: rest ->
    let c : undo_class t o = cls op in
    (match c with
     | Inverse f ->
       law op pre tree f;
       apply_all_app w (f pre) (inverse_plan cls rest) tree;
       inverse_plan_restores w cls law entry rest pre
     | _ -> ())
  | TCompute _ :: rest -> inverse_plan_restores w cls law entry rest tree
  | TReached _ :: rest -> inverse_plan_restores w cls law entry rest tree
  | TEmitted _ :: rest -> inverse_plan_restores w cls law entry rest tree

(* ───────────────────────────────────────────────────────────────────
   The static posture reaches the dynamic trail.
   ─────────────────────────────────────────────────────────────────── *)

let app_nil_both (#x: Type0) (xs: list x) (ys: list x)
  : Lemma (requires app xs ys == []) (ensures xs == [] /\ ys == []) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: _ -> ()

/// Every edit a chain in plan order records has an exact inverse, when
/// the views it was walked through carry no defect.
[@@ noextract_to "FSharp"]
let rec all_inverse_recorded (#t: Type0) (#o: Type0) (cls: o -> undo_class t o) (recorded: list (t & o))
  : Tot bool (decreases recorded) =
  match recorded with
  | [] -> true
  | (_, op) :: rest -> Inverse? (cls op) && all_inverse_recorded cls rest

let rec all_inverse_recorded_app (#t: Type0) (#o: Type0) (cls: o -> undo_class t o) (xs: list (t & o)) (ys: list (t & o))
  : Lemma (requires all_inverse_recorded cls xs /\ all_inverse_recorded cls ys)
          (ensures all_inverse_recorded cls (app xs ys))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> all_inverse_recorded_app cls rest ys

let rec defects_clear_views (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                            (w: witness t b v o q a eff d) (cls: o -> undo_class t o)
                            (vs: list (op_view o)) (tree: t)
  : Lemma
      (requires view_defects cls vs == [])
      (ensures
        (let r = trail_views w vs tree in
         ROk? r ==> all_inverse_recorded cls (snd (ROk?.value r))))
      (decreases %[vs; 1; 0]) =
  match vs with
  | [] -> trail_views_nil w tree
  | x :: rest ->
    app_nil_both (view_defect cls x) (view_defects cls rest);
    trail_views_cons w x rest tree;
    defects_clear_view w cls x tree;
    let u : res (t & list (t & o)) = trail_view w x tree in
    (match u with
     | RErr _ -> ()
     | ROk r1 ->
       defects_clear_views w cls rest (fst r1);
       let u2 : res (t & list (t & o)) = trail_views w rest (fst r1) in
       (match u2 with
        | RErr _ -> ()
        | ROk r2 -> all_inverse_recorded_app cls (snd r1) (snd r2)))

and defects_clear_view (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                       (w: witness t b v o q a eff d) (cls: o -> undo_class t o)
                       (x: op_view o) (tree: t)
  : Lemma
      (requires view_defect cls x == [])
      (ensures
        (let r = trail_view w x tree in
         ROk? r ==> all_inverse_recorded cls (snd (ROk?.value r))))
      (decreases %[x; 0; 0]) =
  match x with
  | ORequire op -> trail_view_require w op tree
  | OEdit op ->
    trail_view_edit w op tree;
    let c : undo_class t o = cls op in
    (match c with
     | Inverse _ -> ()
     | Compensate _ -> ()
     | OneWay _ -> ())
  | OChoose entry when_true when_false exit ->
    app_nil_both (view_defects cls when_true) (view_defects cls when_false);
    trail_view_choose w entry when_true when_false exit tree;
    let e : res t = w.w_apply entry tree in
    (if ROk? e then defects_clear_views w cls when_true tree
     else defects_clear_views w cls when_false tree)
  | ORepeat n body ->
    trail_view_repeat w n body tree;
    defects_clear_repeat w cls body n tree
  | OEach elements ->
    trail_view_each w elements tree;
    defects_clear_each w cls elements tree

and defects_clear_each (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                       (w: witness t b v o q a eff d) (cls: o -> undo_class t o)
                       (elements: list (list (op_view o))) (tree: t)
  : Lemma
      (requires view_defects_each cls elements == [])
      (ensures
        (let r = trail_each w elements tree in
         ROk? r ==> all_inverse_recorded cls (snd (ROk?.value r))))
      (decreases %[elements; 1; 0]) =
  match elements with
  | [] -> trail_each_nil w tree
  | el :: rest ->
    app_nil_both (view_defects cls el) (view_defects_each cls rest);
    trail_each_cons w el rest tree;
    defects_clear_views w cls el tree;
    let u : res (t & list (t & o)) = trail_views w el tree in
    (match u with
     | RErr _ -> ()
     | ROk r1 ->
       defects_clear_each w cls rest (fst r1);
       let u2 : res (t & list (t & o)) = trail_each w rest (fst r1) in
       (match u2 with
        | RErr _ -> ()
        | ROk r2 -> all_inverse_recorded_app cls (snd r1) (snd r2)))

and defects_clear_repeat (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                         (w: witness t b v o q a eff d) (cls: o -> undo_class t o)
                         (body: list (op_view o)) (n: nat) (tree: t)
  : Lemma
      (requires view_defects cls body == [])
      (ensures
        (let r = trail_repeat w body n tree in
         ROk? r ==> all_inverse_recorded cls (snd (ROk?.value r))))
      (decreases %[body; 2; n]) =
  if n = 0 then trail_repeat_zero w body tree
  else begin
    trail_repeat_step w body n tree;
    defects_clear_views w cls body tree;
    let u : res (t & list (t & o)) = trail_views w body tree in
    (match u with
     | RErr _ -> ()
     | ROk r1 ->
       defects_clear_repeat w cls body (n - 1) (fst r1);
       let u2 : res (t & list (t & o)) = trail_repeat w body (n - 1) (fst r1) in
       (match u2 with
        | RErr _ -> ()
        | ROk r2 -> all_inverse_recorded_app cls (snd r1) (snd r2)))
  end

/// A distinct list is empty exactly when its source is.
let distinct_nil (seen: list defect) (ds: list defect)
  : Lemma (requires distinct_from seen ds == [] /\ seen == []) (ensures ds == []) (decreases ds) =
  match ds with
  | [] -> ()
  | d :: rest -> ()

/// What the trail of a sequence of steps says of its edits, step by step
/// (reversed, as the accumulator holds it): no compute stage without a
/// restorer, no step that reached, nothing emitted, every edit an exact
/// inverse — the whole of what `first_refused` reads.
[@@ noextract_to "FSharp"]
let rec clear (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (steps: list (step t b o))
  : Tot bool (decreases steps) =
  match steps with
  | [] -> true
  | TEdit _ op :: rest -> Inverse? (cls op) && clear cls rest
  | TCompute (OSome _) :: rest -> clear cls rest
  | TCompute ONone :: _ -> false
  | TReached _ :: _ -> false
  | TEmitted _ :: _ -> false

let rec clear_app (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (xs: list (step t b o)) (ys: list (step t b o))
  : Lemma (requires clear cls xs /\ clear cls ys) (ensures clear cls (app xs ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | TEdit _ _ :: rest -> clear_app cls rest ys
  | TCompute (OSome _) :: rest -> clear_app cls rest ys
  | _ -> ()

let rec clear_rev (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (xs: list (step t b o))
  : Lemma (requires clear cls xs) (ensures clear cls (rev xs)) (decreases xs) =
  match xs with
  | [] -> ()
  | TEdit _ _ :: rest -> clear_rev cls rest; clear_app cls (rev rest) [Cons?.hd xs]
  | TCompute (OSome _) :: rest -> clear_rev cls rest; clear_app cls (rev rest) [Cons?.hd xs]
  | _ -> ()

let rec clear_edits (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (recorded: list (t & o))
  : Lemma (requires all_inverse_recorded cls recorded) (ensures clear cls (edits #t #b #o recorded)) (decreases recorded) =
  match recorded with
  | [] -> ()
  | _ :: rest -> clear_edits #t #b #o cls rest

/// A reversible stage — one with no defect, and a compute stage whose
/// restorer the plan found — leaves the trail clear.
let plan_stage_clear (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                     (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                     (cls: o -> undo_class t o) (reversible: a -> bool)
                     (s: stage a v o q) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires
        clear cls acc.ac_trail /\
        stage_defects w cls reversible s == [] /\
        (match s with
         | SCompute action -> OSome? (w.w_undo_compute node_id action acc.ac_store.st_bindings)
         | _ -> True))
      (ensures clear cls (plan_stage w reg node_id s acc).ac_trail) =
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
            distinct_nil [] (view_defects cls (views w ops));
            let r : res (t & list (staged_call v p)) =
              plan_ops w cap reg.r_op_perform ops acc.ac_store.st_tree acc.ac_staged in
            (match r with
             | RErr _ -> ()
             | ROk _ ->
               defects_clear_views w cls (views w ops) acc.ac_store.st_tree;
               let u : res (t & list (t & o)) = trail_views w (views w ops) acc.ac_store.st_tree in
               (match u with
                | RErr _ -> ()
                | ROk recorded ->
                  clear_edits #t #b #o cls (snd recorded);
                  clear_rev cls (edits #t #b #o (snd recorded));
                  clear_app cls (rev (edits #t #b #o (snd recorded))) acc.ac_trail))
          | HostCall _ _ _ -> ()
          | EmitPatch _ -> ()
          | Notify _ _ -> ()))

/// Every compute stage the plan runs finds a restorer — the run-time
/// half of reversibility, which the static posture cannot see (a trace
/// is a fact of the run), stated of the stages against the bindings
/// each is planned over.
[@@ noextract_to "FSharp"]
let rec restorers_found (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                        (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                        (stages: list (stage a v o q)) (acc: accumulator t b v o eff d p)
  : Tot prop (decreases stages) =
  match stages with
  | [] -> True
  | s :: rest ->
    (acc.ac_halted \/
     (match s with
      | SCompute action -> OSome? (w.w_undo_compute node_id action acc.ac_store.st_bindings)
      | _ -> True)) /\
    restorers_found w reg node_id rest (if acc.ac_halted then acc else plan_stage w reg node_id s acc)

/// Every stage's defects are empty: the posture is `Reversible`.
let no_reasons (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                   (w: witness t b v o q a eff d) (cls: o -> undo_class t o) (reversible: a -> bool)
                   (stages: list (stage a v o q)) (k: nat)
  : Lemma
      (requires reasons_from w cls reversible stages k == [])
      (ensures
        (match stages with
         | [] -> True
         | s :: rest -> stage_defects w cls reversible s == [] /\ reasons_from w cls reversible rest (k + 1) == []))
      (decreases stages) =
  match stages with
  | [] -> ()
  | s :: rest ->
    app_nil_both (positioned k (stage_defects w cls reversible s)) (reasons_from w cls reversible rest (k + 1));
    (match stage_defects w cls reversible s with
     | [] -> ()
     | _ :: _ -> ())

let rec plan_clear (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                   (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                   (cls: o -> undo_class t o) (reversible: a -> bool)
                   (stages: list (stage a v o q)) (k: nat) (acc: accumulator t b v o eff d p)
  : Lemma
      (requires
        clear cls acc.ac_trail /\
        reasons_from w cls reversible stages k == [] /\
        restorers_found w reg node_id stages acc)
      (ensures clear cls (plan w reg node_id stages acc).ac_trail)
      (decreases stages) =
  match stages with
  | [] -> ()
  | s :: rest ->
    no_reasons w cls reversible stages k;
    if acc.ac_halted then plan_clear w reg node_id cls reversible rest (k + 1) acc
    else begin
      plan_stage_clear w reg node_id cls reversible s acc;
      plan_clear w reg node_id cls reversible rest (k + 1) (plan_stage w reg node_id s acc)
    end

/// Verdict `Reversible` is "no defect at all".
let verdict_reversible (ds: list defect)
  : Lemma (requires verdict_of ds == Reversible) (ensures ds == []) (decreases ds) =
  match ds with
  | [] -> ()
  | d :: rest -> ()

let defects_of_nil (rs: list (nat & defect))
  : Lemma (requires defects_of rs == []) (ensures rs == []) (decreases rs) =
  match rs with
  | [] -> ()
  | _ :: _ -> ()

/// A clear trail is one `first_refused` passes over whole.
let rec clear_not_refused (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (steps: list (step t b o)) (k: nat)
  : Lemma (requires clear cls steps) (ensures first_refused cls steps k == ONone) (decreases steps) =
  match steps with
  | [] -> ()
  | TEdit _ op :: rest ->
    let c : undo_class t o = cls op in
    (match c with
     | Inverse _ -> clear_not_refused cls rest (k + 1)
     | _ -> ())
  | TCompute (OSome _) :: rest -> clear_not_refused cls rest (k + 1)
  | _ -> ()

let rec clear_all_inverse (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (steps: list (step t b o))
  : Lemma (requires clear cls steps) (ensures all_inverse cls steps) (decreases steps) =
  match steps with
  | [] -> ()
  | TEdit _ _ :: rest -> clear_all_inverse cls rest
  | TCompute (OSome _) :: rest -> clear_all_inverse cls rest
  | _ -> ()

let rec all_inverse_app (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (xs: list (step t b o)) (ys: list (step t b o))
  : Lemma (requires all_inverse cls xs /\ all_inverse cls ys) (ensures all_inverse cls (app xs ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> all_inverse_app cls rest ys

let rec all_inverse_rev (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (xs: list (step t b o))
  : Lemma (requires all_inverse cls xs) (ensures all_inverse cls (rev xs)) (decreases xs) =
  match xs with
  | [] -> ()
  | x :: rest -> all_inverse_rev cls rest; all_inverse_app cls (rev rest) [x]

(* ───────────────────────────────────────────────────────────────────
   The undo's own handler run: one `ApplyOps` of edits plans as the
   pure fold, and commits when its performers answer.
   ─────────────────────────────────────────────────────────────────── *)

/// Every op the witness views as an EDIT of itself — what an inverse op
/// is required to be: a domain whose inverse of an edit were a guard or
/// a branch would be inverting into control, which no inverse law
/// speaks of.
[@@ noextract_to "FSharp"]
let rec all_edits (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                  (w: witness t b v o q a eff d) (ops: list o)
  : Tot prop (decreases ops) =
  match ops with
  | [] -> True
  | op :: rest -> w.w_op_view op == OEdit op /\ all_edits w rest

/// An op sequence of edits plans as the pure fold: the same tree, or the
/// same refusal.
let rec plan_edits_fold (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                        (w: witness t b v o q a eff d) (cap: string) (stage: opt (t -> o -> (p & v)))
                        (ops: list o) (tree: t) (staged: list (staged_call v p))
  : Lemma
      (requires all_edits w ops)
      (ensures tree_of (plan_ops w cap stage ops tree staged) == apply_all w ops tree)
      (decreases ops) =
  match ops with
  | [] -> plan_ops_nil w cap stage tree staged
  | op :: rest ->
    plan_ops_cons w cap stage op rest tree staged;
    plan_view_edit w cap stage op tree staged;
    let e : res t = w.w_apply op tree in
    (match e with
     | RErr _ -> ()
     | ROk tree' ->
       (match stage with
        | ONone -> plan_edits_fold w cap stage rest tree' staged
        | OSome f -> plan_edits_fold w cap stage rest tree' (staged_from cap f tree' op :: staged)))

/// The one-stage handler the undo runs: when the gate admits `ApplyOps`,
/// the policy admits the inverse effect, the inverses are edits and fold
/// to a state, and every staged call the performer is asked succeeds, the
/// run COMMITS to that state — through `commit_is_total_prefix`, which is
/// why "through the same gate and performers" is a theorem and not a
/// description.
let undo_handler_commits (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                         (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                         (inv: list o) (post: store t b)
  : Lemma
      (requires
        reg.r_gate "ApplyOps" /\
        ONone? (reg.r_policy (ApplyOps inv)) /\
        all_edits w inv /\
        ROk? (apply_all w inv post.st_tree) /\
        (let planned = plan w reg node_id [SEffect (ApplyOps inv)] (start post) in
         all_ok reg.r_perf (rev planned.ac_staged)))
      (ensures
        (let out = run w reg node_id [SEffect (ApplyOps inv)] post in
         out.oc_committed == true /\
         out.oc_store.st_tree == ROk?.value (apply_all w inv post.st_tree))) =
  let acc = start post in
  plan_edits_fold w "ApplyOps" reg.r_op_perform inv post.st_tree acc.ac_staged;
  let planned = plan w reg node_id [SEffect (ApplyOps inv)] acc in
  commit_is_total_prefix w reg node_id [SEffect (ApplyOps inv)] post;
  perform_keeps w reg (rev planned.ac_staged) planned

(* ───────────────────────────────────────────────────────────────────
   THE HEADLINE THEOREMS
   ─────────────────────────────────────────────────────────────────── *)

/// **`undo_run_restores`.** A handler the posture reads as REVERSIBLE —
/// no reason at all, from its declared form and the state witness's
/// member — whose run committed and whose every compute stage found its
/// restorer, is undone to its ENTRY STATE by the undo run: the inverses
/// computed from the recorded pre-states, performed in reverse plan
/// order through the same gate, policy and performers, commit the entry
/// tree back, and the restorers hand the bindings back. Conditional on
/// the witness's inverse law (K9), on the inverses being edits, on the
/// gate and policy admitting the inverse effect, and on the undo's
/// performers answering every staged call — each named here because
/// each is a parameter of the model, and none is the theorem's to prove.
let undo_run_restores (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                      (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                      (cls: o -> undo_class t o) (law: inverse_law w cls) (reversible: a -> bool) (canonical: t -> string)
                      (stages: list (stage a v o q)) (s: store t b)
  : Lemma
      (requires
        posture w cls reversible stages == Reversible /\
        restorers_found w reg node_id stages (start s) /\
        (let (out, steps) = run_planned w reg node_id stages s in
         out.oc_committed /\
         (let inv = inverse_plan cls (rev steps) in
          reg.r_gate "ApplyOps" /\
          ONone? (reg.r_policy (ApplyOps inv)) /\
          all_edits w inv /\
          all_ok reg.r_perf (rev (plan w reg node_id [SEffect (ApplyOps inv)] (start out.oc_store)).ac_staged))))
      (ensures
        (let (out, steps) = run_planned w reg node_id stages s in
         let undone = undo_run w reg node_id cls canonical s.st_tree out.oc_committed steps out.oc_store in
         ROk? undone /\
         (ROk?.value undone).oc_committed == true /\
         (ROk?.value undone).oc_store.st_tree == s.st_tree)) =
  let (out, steps) = run_planned w reg node_id stages s in
  let planned = plan w reg node_id stages (start s) in
  // The posture's reading reaches the trail: every step is clear.
  verdict_reversible (defects_of (reasons w cls reversible stages));
  defects_of_nil (reasons w cls reversible stages);
  plan_clear w reg node_id cls reversible stages 0 (start s);
  perform_keeps w reg (rev planned.ac_staged) planned;
  rev_rev planned.ac_trail;
  clear_rev cls planned.ac_trail;
  clear_not_refused cls steps 0;
  clear_all_inverse cls steps;
  all_inverse_rev cls steps;
  // The trail chains the entry state to the committed state, so the
  // inverse plan folds the committed state back to the entry state.
  run_planned_chain w reg node_id stages s;
  inverse_plan_restores w cls law s.st_tree (rev steps) out.oc_store.st_tree;
  // The undo's own run commits to what the fold answers.
  undo_handler_commits w reg node_id (inverse_plan cls (rev steps)) out.oc_store

/// The compensated state: what the declared compensations and inverses,
/// in reverse plan order, fold the committed state to. The definition of
/// "undone in effect", and what `undo_run_reaches_compensated` says the
/// run reaches.
[@@ noextract_to "FSharp"]
let compensated (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0)
                (w: witness t b v o q a eff d) (cls: o -> undo_class t o)
                (steps: list (step t b o)) (post: t)
  : res t =
  apply_all w (inverse_plan cls (rev steps)) post

/// **`undo_run_reaches_compensated`.** A committed run whose trail the
/// undo does not refuse — no one-way op, nothing that reached, nothing
/// emitted, every compute stage with a restorer: a REVERSIBLE or a
/// COMPENSABLE plan — is undone to the COMPENSATED STATE: the undo run
/// commits exactly the state the compensations and inverses, in reverse
/// plan order, fold the committed state to. No inverse law: a
/// compensation obeys none, and this is what "undone in effect but not
/// in history" means. For a plan of exact inverses the drift check reads
/// that fold against the entry state first; for one with a compensation
/// there is nothing to read it against.
let undo_run_reaches_compensated (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                                 (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                                 (cls: o -> undo_class t o) (canonical: t -> string)
                                 (entry: t) (steps: list (step t b o)) (post: store t b)
  : Lemma
      (requires
        first_refused cls steps 0 == ONone /\
        (let inv = inverse_plan cls (rev steps) in
         reg.r_gate "ApplyOps" /\
         ONone? (reg.r_policy (ApplyOps inv)) /\
         all_edits w inv /\
         ROk? (apply_all w inv post.st_tree) /\
         (all_inverse cls steps ==> canonical (ROk?.value (apply_all w inv post.st_tree)) = canonical entry) /\
         all_ok reg.r_perf (rev (plan w reg node_id [SEffect (ApplyOps inv)] (start post)).ac_staged)))
      (ensures
        (let undone = undo_run w reg node_id cls canonical entry true steps post in
         ROk? undone /\
         (ROk?.value undone).oc_committed == true /\
         ROk (ROk?.value undone).oc_store.st_tree == compensated w cls steps post.st_tree)) =
  undo_handler_commits w reg node_id (inverse_plan cls (rev steps)) post

/// The step at a position, and whether the undo can perform it.
[@@ noextract_to "FSharp"]
let rec nth (#x: Type0) (xs: list x) (k: nat) : Tot (opt x) (decreases xs) =
  match xs with
  | [] -> ONone
  | y :: rest -> if k = 0 then OSome y else nth rest (k - 1)

[@@ noextract_to "FSharp"]
let refusable (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (st: step t b o) : bool =
  match st with
  | TEdit _ op -> OneWay? (cls op)
  | TCompute ONone -> true
  | TCompute (OSome _) -> false
  | TReached _ -> true
  | TEmitted _ -> true

let nth_shift (#x: Type0) (y: x) (xs: list x) (k: nat)
  : Lemma (ensures nth (y :: xs) (k + 1) == nth xs k) = ()

/// A step the undo can perform is passed over.
let first_refused_step (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o)
                       (st: step t b o) (rest: list (step t b o)) (k: nat)
  : Lemma (requires not (refusable cls st))
          (ensures first_refused cls (st :: rest) k == first_refused cls rest (k + 1)) =
  match st with
  | TEdit _ op ->
    let c : undo_class t o = cls op in
    (match c with
     | Inverse _ -> ()
     | Compensate _ -> ()
     | OneWay _ -> ())
  | TCompute (OSome _) -> ()
  | TCompute ONone -> ()
  | TReached _ -> ()
  | TEmitted _ -> ()

/// Whether the undo can perform every step up to (and excluding) the
/// `n`-th, or every step when `n` is past the end.
[@@ noextract_to "FSharp"]
let passable_before (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o) (steps: list (step t b o)) (n: nat) : prop =
  forall (j: nat). j < n ==>
    (match nth steps j with
     | OSome st -> not (refusable cls st)
     | ONone -> True)

/// **`one_way_position_exact`.** The step the undo refuses on is the
/// FIRST step of the plan it cannot perform: the refusal's ordinal names
/// a step that is one-way, reached, emitted or unrestorable, and every
/// step before it is one the undo could have performed; and when nothing
/// is refused, every step can be performed. So "one-way after step k" is
/// exact, and everything before k is undoable.
let rec one_way_position_exact (#t: Type0) (#b: Type0) (#o: Type0) (cls: o -> undo_class t o)
                               (steps: list (step t b o)) (k: nat)
  : Lemma
      (ensures
        (match first_refused cls steps k with
         | ONone -> passable_before cls steps (length steps)
         | OSome r ->
           r.rf_step >= k /\
           (match nth steps (r.rf_step - k) with
            | OSome st -> refusable cls st
            | ONone -> False) /\
           passable_before cls steps (r.rf_step - k)))
      (decreases steps) =
  match steps with
  | [] -> ()
  | st :: rest ->
    one_way_position_exact cls rest (k + 1);
    if refusable cls st then
      (match st with
       | TEdit _ op ->
         let c : undo_class t o = cls op in
         (match c with
          | OneWay _ -> ()
          | _ -> ())
       | _ -> ())
    else begin
      first_refused_step cls st rest k;
      let r : opt refusal = first_refused cls rest (k + 1) in
      let bound : nat = (match r with ONone -> length steps | OSome r' -> r'.rf_step - k) in
      let shift (j: nat) : Lemma (requires j < bound)
                                 (ensures (match nth (st :: rest) j with
                                           | OSome s' -> not (refusable cls s')
                                           | ONone -> True)) =
        if j = 0 then () else nth_shift st rest (j - 1)
      in
      Classical.forall_intro (Classical.move_requires shift)
    end

/// **`refused_before_anything`.** When the plan holds a step the undo
/// cannot perform, the undo run answers the refusal naming the first
/// such step, and runs nothing: no handler, no gate, no performer — an
/// equation on `undo_run`, under every registry at once.
let refused_before_anything (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                            (w: witness t b v o q a eff d) (reg: registry t v o q p) (reg': registry t v o q p) (node_id: string)
                            (cls: o -> undo_class t o) (canonical: t -> string)
                            (entry: t) (steps: list (step t b o)) (post: store t b)
  : Lemma
      (requires OSome? (first_refused cls steps 0))
      (ensures
        undo_run w reg node_id cls canonical entry true steps post ==
          RErr (render (OSome?.item (first_refused cls steps 0))) /\
        undo_run w reg node_id cls canonical entry true steps post ==
          undo_run w reg' node_id cls canonical entry true steps post) = ()

/// **`undo_residual_is_prefix`.** When the undo's performer refuses at
/// position `k` of the undo's own staged list, the undo run reports
/// exactly the first `k` inverses performed, in order, and leaves the
/// store the committed run left: `residual_is_prefix`, read for the undo
/// — a failed undo step reports how far it got, in the perform-failure
/// vocabulary, because it IS a handler run.
let undo_residual_is_prefix (#t: Type0) (#b: Type0) (#v: Type0) (#o: Type0) (#q: Type0) (#a: Type0) (#eff: Type0) (#d: Type0) (#p: Type0)
                            (w: witness t b v o q a eff d) (reg: registry t v o q p) (node_id: string)
                            (cls: o -> undo_class t o) (canonical: t -> string)
                            (entry: t) (steps: list (step t b o)) (post: store t b) (k: nat)
  : Lemma
      (requires
        first_refused cls steps 0 == ONone /\
        (let inv = inverse_plan cls (rev steps) in
         (all_inverse cls steps ==>
            (match apply_all w inv post.st_tree with
             | ROk restored -> canonical restored = canonical entry
             | RErr _ -> False)) /\
         (let planned = plan w reg node_id [SEffect (ApplyOps inv)] (start post) in
          not planned.ac_halted /\ fails_at reg.r_perf (rev planned.ac_staged) k)))
      (ensures
        (let inv = inverse_plan cls (rev steps) in
         let planned = plan w reg node_id [SEffect (ApplyOps inv)] (start post) in
         let undone = undo_run w reg node_id cls canonical entry true steps post in
         ROk? undone /\
         (ROk?.value undone).oc_committed == false /\
         (ROk?.value undone).oc_store == post /\
         (ROk?.value undone).oc_performed == take k (caps (rev planned.ac_staged)))) =
  residual_is_prefix w reg node_id [SEffect (ApplyOps (inverse_plan cls (rev steps)))] post k
