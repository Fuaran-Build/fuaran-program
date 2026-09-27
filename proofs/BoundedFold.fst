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

/// Phase 1715 — the shared bounded fold, modelled in F* and proved.
/// Phase 1898 — the same fold restated over the ACTION VIEW of the
/// generic tier (DECISIONS.md D18, `docs/generic-tier.md` §3), written
/// before the code that will meet it (D14).
///
/// # What this module is
///
/// Two layers, one file.
///
/// **The generic tier** — a model of the fold the generic core will run,
/// `BoundedActions.run witness` (Phase 1896 writes it): one `match` over
/// the four shapes of D18's `ActionView`, parameterised by a WITNESS
/// record that is the fold-read members of `ProgramWitness`. There is no
/// F# for this layer yet; this model is the specification it meets.
/// Every definition is captioned with the D18 contract member it models
/// (§3.2 the action view, §3.3 expressions, §3.4 the store).
///
/// **The UI witness** — today's fourteen-arm `Action` union seen THROUGH
/// the view: `ui_view` is the adapter's total `View`, `ui_lower` its
/// `Lower`, and `run` is `run_action` at that witness, with the exact
/// signature Phase 1715 gave it. The differential host
/// (`tests/Fuaran.Program.Parity.Tests/ProofOracleTests.fs`) runs the
/// EXTRACTION of `run` beside `BoundedActions.runBoundedActionWith` over
/// the conformance corpus's driver-semantics family and an arm-complete
/// action corpus, and requires the store, the effect list and the
/// diagnostics to agree at every step — that host is the only thing that
/// says this model is about the code that ships. It runs unchanged on
/// this restatement, which is the claim that the fourteen arms seen
/// through the view ARE the fourteen arms.
///
/// # Every parameter of the model is an assumption
///
/// The generic fold takes one witness and one placement arm. What each
/// arrow is assumed to be, and which theorem leans on it:
///
///   * `w_view` (D18 `ActionWitness.View`) — a TOTAL arrow. Here the
///     view is taken to exhaustion (`action_view` is a tree), so the
///     obligation that the F# `View`, applied repeatedly, unfolds a finite
///     tree is carried by this field's type: a witness whose `View` put an
///     action inside its own `Sequence` could not be written here. The UI
///     witness discharges it by `ui_view` being accepted as `Tot`.
///     Every theorem below leans on it, since every theorem is about
///     `fold` over a finite view.
///   * `w_lower` (D18 `ActionWitness.Lower`) — a total arrow whose
///     RESULT TYPE is the kept assumption K3: at most one effect, or a
///     refusal, or a decline, and no store. A leaf that wrote the store
///     or emitted a list cannot be expressed. `fold_total` states what
///     that buys.
///   * `w_describe` (D18 `ActionWitness.Describe`) — a total arrow; the
///     diagnostics carry its answer verbatim.
///   * `w_resolve` (D18 `ExprWitness.Resolve`) — a total pure arrow, the
///     same answer for the same store. Nothing proved here depends on
///     what it answers, only on the fold consulting it where it does.
///   * `w_is_reserved` / `w_reserved_prefix` (D18 `StoreWitness`) — a
///     total predicate and a constant (K5). `fold_reserved_untouched` is
///     quantified over every such predicate.
///   * `answer` (the placement arm, `HandlerArm.Answer`) — what a call
///     MEANS at this placement, opaque. `fold_total` names the answered
///     case and excludes it; `fold_reserved_untouched` carries the seam's
///     obligation `arm_preserves_reserved` as a hypothesis, which
///     `inert_preserves_reserved` discharges for the arm that declines.
///
/// **No-closure-invocation is an obligation on the witness, stated as a
/// precondition (D18).** The core holds no closure: nothing in the view
/// is one, and the fold reads an action only through `w_describe`,
/// `w_lower` and the shape `w_view` gave it. `fold_blind` proves that
/// half UNCONDITIONALLY — two views of the same shape whose actions
/// describe and lower alike fold identically. `blind_to` states the
/// witness's half — a relation on actions under which `w_view` produces
/// same-shaped views — and `run_action_blind` is the theorem conditional
/// on it. `ui_blind_to_closures` discharges that obligation for the UI
/// witness under `same_but_closures`, so `run_no_closure` — Phase 1715's
/// statement, word for word — is again unconditional.
///
/// # The store is modelled concretely, on purpose
///
/// D18 §3.4 makes the store abstract behind `StoreWitness.Assign`. The
/// model keeps a concrete keyed channel (an association list, `write`),
/// because `fold_reserved_untouched` is a theorem ABOUT what is written,
/// and a theorem about writes to an abstract store would be a theorem
/// about an obligation nobody had stated. What the model claims of a
/// domain's `Assign` is therefore K4: one keyed state channel where an
/// assignment writes one key. A store whose `Assign` did something else
/// is outside this model, and the differential host's projection of the
/// domain store onto this channel is where that is checked.
///
/// # The theorems
///
///   Generic tier (over the view):
///   * `fold_total` — every view shape is named, with no wildcard; one
///     step that is neither `VSequence` nor an ANSWERED call leaves the
///     placement untouched, leaves the store identical or writes exactly
///     one key the reserved predicate rejects, and emits at most one
///     effect and at most one diagnostic (K3, as a theorem).
///   * `fold_blind` / `run_action_blind` — the core half and the
///     conditional whole of no-closure-invocation, above.
///   * `sequence_homomorphism` — `fold_many (app xs ys) s` is
///     `fold_many ys` applied to the store `fold_many xs s` left, effects
///     and diagnostics concatenated in order. DECISIONS.md D7's splice
///     property, as an equation, now over `VSequence`.
///   * `fold_reserved_untouched` — the output store agrees with the input
///     at every reserved key, for any arm that preserves them.
///
///   The UI witness (each a corollary of the generic theorem at
///   `ui_witness`, keeping its Phase-1715 name and statement):
///   * `run_total`, `run_no_closure`, `chain_homomorphism`,
///     `reserved_untouched`, `inert_preserves_reserved`.
///
/// Nothing about the browser placement, the durable journal, or the
/// per-connection session cells is modelled.

module BoundedFold

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns — declared here rather than taken
   from `FStar.Pervasives.Native` or `FStar.List.Tot` so the extraction
   references `Prims` and nothing else. `oracle/Prims.fs` is that whole
   runtime; a model reaching for a further name would fail the byte-diff
   in `check.ps1` rather than silently compile against a widened shim.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `List.append` / `xs @ ys`. Defined here for the reason above.
let rec app (#a: Type0) (xs: list a) (ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: app rest ys

(* ───────────────────────────────────────────────────────────────────
   The store — D18 §3.4 `StoreWitness`'s one state channel, today
   `BoundedActions.BoundedStore` (= `BindingSources`).`State`.

   Only the `State` map is written on this path, so only `State` is
   modelled: an association list over an abstract value type `v` (D18's
   `JVal`, which the fold never inspects). The other channels are host
   context the fold never writes and only ever reads THROUGH the
   witness's `Resolve`, so they reach the model as part of that arrow.
   ─────────────────────────────────────────────────────────────────── *)

type key = string

type store (v: Type0) = list (key & v)

/// F#: `Map.tryFind`.
let rec lookup (#v: Type0) (s: store v) (k: key) : Tot (opt v) (decreases s) =
  match s with
  | [] -> ONone
  | (k', x) :: rest -> if k' = k then OSome x else lookup rest k

/// D18 §3.4 `StoreWitness.Assign`, modelled concretely (see the header).
/// F#: `Map.add` — replace in place when the key is present, append
/// otherwise. Replacing rather than shadowing is what keeps `lookup` in
/// agreement with a map's semantics, and what lets the differential host
/// compare the two stores as key-ordered sequences.
let rec write (#v: Type0) (s: store v) (k: key) (x: v) : Tot (store v) (decreases s) =
  match s with
  | [] -> [(k, x)]
  | (k', y) :: rest -> if k' = k then (k, x) :: rest else (k', y) :: write rest k x

(* ═══════════════════════════════════════════════════════════════════
   THE GENERIC TIER — D18 §3.2 / §3.3 / §3.4, the members the fold reads.
   ═══════════════════════════════════════════════════════════════════ *)

/// D18 §3.3 `Resolution = Resolved of JVal | NotResolved | Errored of
/// string` — three cases. The UI's fourth (`I18nUnresolved`) never
/// reaches the core: the adapter folds it into `Errored` (see
/// `ui_resolve`), and the refusal text is unchanged by that.
type resolution (v: Type0) =
  | Resolved : value: v -> resolution v
  | NotResolved : resolution v
  | Errored : message: string -> resolution v

/// D18 §3.2 `ActionView<'Action, 'Expr>`, taken TO EXHAUSTION. The F#
/// `View` is one level — `Sequence of 'Action list` — and the F# fold
/// re-views each child as it reaches it. This model views the whole tree
/// first, so that termination of the fold is structural on the view and
/// the obligation that `View` unfolds finitely sits on the witness's
/// `w_view` field (a `Tot` arrow) rather than on a fuel the code does not
/// have. `act` is the action the F# fold holds in hand when it views it,
/// carried here so `w_describe` and `w_lower` can be given it.
type action_view (a: Type0) (e: Type0) (v: Type0) =
  /// `Sequence of 'Action list` — the composition arm (today: `Chain`).
  | VSequence : act: a -> ops: list (action_view a e v) -> action_view a e v
  /// `Assign of key * value: JVal option * from: 'Expr option` (today:
  /// `SetState`).
  | VAssign : act: a -> state_key: key -> value: opt v -> value_from: opt e -> action_view a e v
  /// `Call of endpoint: string * declaresTarget: bool` (today: `Call`; a
  /// declared target is refused, D9).
  | VCall : act: a -> endpoint: string -> declares_target: bool -> action_view a e v
  /// `Leaf of LeafDeclaration` — every other domain act. The declaration
  /// is the DEMANDED projection's business and the fold never reads it,
  /// so it is not carried; what the fold does with a leaf is `w_lower`.
  | VLeaf : act: a -> action_view a e v

/// D18 §3.2 `LeafOutcome<'Effect>`. Its shape IS the kept assumption
/// K3: a leaf emits at most ONE effect, or is refused, or is declined,
/// and there is no case that returns a store.
type leaf_outcome (eff: Type0) =
  | Emit : emitted: eff -> leaf_outcome eff
  | Refuse : reason: string -> leaf_outcome eff
  | Decline : leaf_outcome eff

/// The fold-read members of D18's `ProgramWitness`, as one record: the
/// three of `ActionWitness` (§3.2), `ExprWitness.Resolve` (§3.3), and
/// the two of `StoreWitness` the refusal needs (§3.4). `Assign` is
/// modelled concretely by `write` (header); `LandQuery` is the handler's
/// landing and never the fold's. Every field is a TOTAL arrow, and the
/// header says what each theorem assumes of it.
noeq type witness (a: Type0) (e: Type0) (v: Type0) (eff: Type0) = {
  /// `ActionWitness.View`, applied to exhaustion.
  w_view: a -> action_view a e v;
  /// `ActionWitness.Lower: nodeId -> 'Action -> 'Store -> LeafOutcome`.
  w_lower: string -> a -> store v -> leaf_outcome eff;
  /// `ActionWitness.Describe` (today: `Validation.describeAction`).
  w_describe: a -> string;
  /// `ExprWitness.Resolve`.
  w_resolve: store v -> e -> resolution v;
  /// `StoreWitness.IsReserved` (K5).
  w_is_reserved: key -> bool;
  /// `StoreWitness.ReservedPrefix`, for the refusal's text.
  w_reserved_prefix: string;
}

/// F#: `BoundedDiagnostic` — program-owned, so not generic. The action
/// is named by its log-safe description rather than carried, exactly as
/// production does — which is also what makes two runs that differ only
/// in carried closures produce EQUAL diagnostics.
type diagnostic =
  | DUnsupported : node_id: string -> action_name: string -> diagnostic
  | DRefused : node_id: string -> action_name: string -> reason: string -> diagnostic

/// F#: `BoundedOutcome`, generic in the effect the witness lowers to.
type bounded_outcome (v: Type0) (eff: Type0) = {
  o_store: store v;
  o_effects: list eff;
  o_diagnostics: list diagnostic;
}

/// F#: `HandlerAnswer<'Placement>`.
type handler_answer (v: Type0) (eff: Type0) (p: Type0) = {
  h_store: store v;
  h_effects: list eff;
  h_diagnostics: list diagnostic;
  h_placement: p;
}

/// F#: `HandlerArm<'Placement>` — what a call action MEANS at this
/// placement. Opaque: `ONone` DECLINES, which is the documented no-op
/// every placement with no handler registry gives.
noeq type handler_arm (v: Type0) (eff: Type0) (p: Type0) = {
  answer: string -> string -> store v -> p -> opt (handler_answer v eff p);
}

/// F#: `HandlerArm.inert` — the arm that declines every call.
let inert_arm (#v: Type0) (#eff: Type0) (#p: Type0) : handler_arm v eff p =
  { answer = (fun _ _ _ _ -> ONone) }

(* ───────────────────────────────────────────────────────────────────
   The three outcome constructors — `BoundedActions.store` / `noOp` /
   `refused`. They take the DESCRIPTION rather than the action: the
   generic fold has no action vocabulary to describe, only the witness's
   answer.
   ─────────────────────────────────────────────────────────────────── *)

let store_only (#v: Type0) (#eff: Type0) (s: store v) : bounded_outcome v eff =
  { o_store = s; o_effects = []; o_diagnostics = [] }

let declined (#v: Type0) (#eff: Type0) (node_id: string) (description: string) (s: store v)
  : bounded_outcome v eff =
  { o_store = s; o_effects = []; o_diagnostics = [ DUnsupported node_id description ] }

let refused (#v: Type0) (#eff: Type0)
            (node_id: string) (description: string) (reason: string) (s: store v)
  : bounded_outcome v eff =
  { o_store = s; o_effects = []; o_diagnostics = [ DRefused node_id description reason ] }

/// F#: the `Result<JVal option, string>` the `Assign` arm computes.
type jval_payload (v: Type0) =
  | POk : value: opt v -> jval_payload v
  | PErr : message: string -> jval_payload v

(* ───────────────────────────────────────────────────────────────────
   THE FOLD — `BoundedActions.run witness` (Phase 1896), one `match`
   over the four view shapes. It owns sequencing, the one store write,
   the reserved-namespace refusal (K5), D9's refusal of a declared result
   target, and D7's handler-effect arm. It never recurses into a leaf.

   Termination is structural on the view. `fold` and `fold_many` are
   mutually recursive with the tree/forest lexicographic measure: the
   list inside `VSequence` is a strict subterm of the view, and each
   element is a strict subterm of the list.
   ─────────────────────────────────────────────────────────────────── *)

let rec fold (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
             (w: witness a e v eff) (ar: handler_arm v eff p)
             (node_id: string) (x: action_view a e v) (s: store v) (pl: p)
  : Tot (bounded_outcome v eff & p) (decreases %[x; 0]) =
  match x with

  // The one store mutation: write the state channel. The reserved
  // namespace is closed on this path too — the loop's whole premise is
  // that the tree is untrusted.
  | VAssign act state_key value value_from ->
    (if w.w_is_reserved state_key then
       refused node_id (w.w_describe act)
         (strcat "State key '"
           (strcat state_key
             (strcat "' is under the host-reserved '" (strcat w.w_reserved_prefix "' namespace")))) s
     else
       let payload : jval_payload v =
         match value_from with
         | OSome expr ->
           (match w.w_resolve s expr with
            | Resolved jv -> POk (OSome jv)
            | NotResolved -> POk ONone
            | Errored m -> PErr m)
         | ONone -> POk value
       in
       match payload with
       | POk (OSome jv) -> store_only (write s state_key jv)
       | POk ONone ->
         refused node_id (w.w_describe act) "valueFrom did not resolve to a value — no write performed" s
       | PErr m ->
         refused node_id (w.w_describe act)
           (strcat "valueFrom errored: " (strcat m " — no write performed")) s),
    pl

  // A call that ALSO declares where its answer should land is REFUSED
  // rather than honoured or quietly ignored: result-target ownership
  // sits with the handler (D9). Otherwise the placement's arm decides
  // what the call MEANS here; declining is the documented no-op.
  | VCall act endpoint declares_target ->
    if declares_target then
      (refused node_id (w.w_describe act)
         "the call declares a result target; a handler declares where its own results land" s,
       pl)
    else
      (match ar.answer node_id endpoint s pl with
       | ONone -> (declined node_id (w.w_describe act) s, pl)
       | OSome ans ->
         ({ o_store = ans.h_store; o_effects = ans.h_effects; o_diagnostics = ans.h_diagnostics },
          ans.h_placement))

  // A leaf is the domain's: lowered to at most one effect, refused with
  // a reason, or declined. The fold does not look inside it.
  | VLeaf act ->
    (match w.w_lower node_id act s with
     | Emit emitted -> { o_store = s; o_effects = [ emitted ]; o_diagnostics = [] }
     | Refuse reason -> refused node_id (w.w_describe act) reason s
     | Decline -> declined node_id (w.w_describe act) s),
    pl

  // Compose: fold in order, threading the store AND the placement's
  // accumulation, concatenating effects and diagnostics.
  | VSequence _ ops -> fold_many w ar node_id ops s pl

and fold_many (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
              (w: witness a e v eff) (ar: handler_arm v eff p)
              (node_id: string) (ops: list (action_view a e v)) (s: store v) (pl: p)
  : Tot (bounded_outcome v eff & p) (decreases %[ops; 1]) =
  match ops with
  | [] -> store_only s, pl
  | x :: rest ->
    let (o1, p1) = fold w ar node_id x s pl in
    let (o2, p2) = fold_many w ar node_id rest o1.o_store p1 in
    { o_store = o2.o_store;
      o_effects = app o1.o_effects o2.o_effects;
      o_diagnostics = app o1.o_diagnostics o2.o_diagnostics },
    p2

/// The action-level entry the generic core exposes: view, then fold.
/// F#: `BoundedActions.run witness arm nodeId action store placement`.
let run_action (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
               (w: witness a e v eff) (ar: handler_arm v eff p)
               (node_id: string) (act: a) (s: store v) (pl: p)
  : bounded_outcome v eff & p =
  fold w ar node_id (w.w_view act) s pl

(* ───────────────────────────────────────────────────────────────────
   THE GENERIC THEOREMS

   Everything from here to the UI witness is ghost: the predicates carry
   `noextract_to "FSharp"` and the lemmas are erased by the extractor,
   so the oracle the differential host runs is exactly the definitions
   above.
   ─────────────────────────────────────────────────────────────────── *)

// ─── 1. Totality over the view ───────────────────────────────────────

/// Every view shape, named once more with NO wildcard. Its value is
/// uninteresting; its shape is the point — a fifth shape added to
/// `action_view` fails to compile HERE exactly as it fails to compile in
/// `fold`. (K2: control structure is sequence + assign + call, and
/// everything else is a leaf.)
[@@ noextract_to "FSharp"]
let handled_view (#a: Type0) (#e: Type0) (#v: Type0) (x: action_view a e v) : bool =
  match x with
  | VSequence _ _ -> true
  | VAssign _ _ _ _ -> true
  | VCall _ _ _ -> true
  | VLeaf _ -> true

[@@ noextract_to "FSharp"]
let at_most_one (#a: Type0) (l: list a) : bool =
  match l with
  | [] -> true
  | [_] -> true
  | _ -> false

/// The placement ANSWERED this call — the one shape whose outcome is
/// the placement's rather than the fold's, and therefore the one the
/// structural characterisation below cannot speak for. Naming it is how
/// the seam stays visible instead of being quietly assumed away.
[@@ noextract_to "FSharp"]
let answered_view (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
                  (ar: handler_arm v eff p) (node_id: string) (x: action_view a e v)
                  (s: store v) (pl: p) : bool =
  match x with
  | VCall _ endpoint false -> OSome? (ar.answer node_id endpoint s pl)
  | _ -> false

/// **`fold_total`.** The fold is defined on every shape of the view —
/// delivered by the `Tot` effect and the `decreases` clause on `fold`,
/// and by `handled_view` naming every constructor without a wildcard —
/// and one step that is neither the composition shape nor a call the
/// placement answered is characterised structurally: the placement is
/// untouched, the store is either unchanged or written at exactly one
/// key the reserved predicate rejects, and at most one effect and at
/// most one diagnostic are emitted. For a leaf that is K3 made a
/// theorem: whatever `w_lower` answers, this is the most it can do.
let fold_total (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
               (w: witness a e v eff) (ar: handler_arm v eff p)
               (node_id: string) (x: action_view a e v) (s: store v) (pl: p)
  : Lemma
      (requires (not (VSequence? x)) /\ (not (answered_view ar node_id x s pl)))
      (ensures
        (handled_view x /\
         (let (o, pl') = fold w ar node_id x s pl in
          pl' == pl /\
          at_most_one o.o_effects /\
          at_most_one o.o_diagnostics /\
          (o.o_store == s \/
           (VAssign? x /\
            (exists (jv: v). o.o_store == write s (VAssign?.state_key x) jv) /\
            not (w.w_is_reserved (VAssign?.state_key x))))))) =
  match x with
  | VAssign _ state_key value value_from ->
    if w.w_is_reserved state_key then ()
    else
      (match value_from with
       | OSome expr ->
         (match w.w_resolve s expr with
          | Resolved _ -> ()
          | _ -> ())
       | ONone -> (match value with | OSome _ -> () | ONone -> ()))
  | VCall _ endpoint declares_target ->
    if declares_target then ()
    else
      (match ar.answer node_id endpoint s pl with
       | ONone -> ()
       | OSome _ -> ())
  | VLeaf act ->
    (match w.w_lower node_id act s with
     | Emit _ -> ()
     | Refuse _ -> ()
     | Decline -> ())
  | VSequence _ _ -> ()

// ─── 2. The fold is blind to everything but the view ─────────────────

/// Two views have the SAME SHAPE under a witness: the same constructors
/// at every node, the same keys, values, expressions, endpoints and
/// target flags, the carried actions describing alike, and — at a leaf
/// — lowering alike at every node id and store. Nothing is required of
/// the carried actions beyond that: this is what "the fold reads an
/// action only through the witness" means, as a relation.
[@@ noextract_to "FSharp"]
let rec same_shape (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0)
                   (w: witness a e v eff) (x: action_view a e v) (y: action_view a e v)
  : Tot prop (decreases %[x; 0]) =
  match x, y with
  | VSequence _ xs, VSequence _ ys -> same_shape_list w xs ys
  | VAssign ax k1 v1 f1, VAssign ay k2 v2 f2 ->
    w.w_describe ax == w.w_describe ay /\ k1 == k2 /\ v1 == v2 /\ f1 == f2
  | VCall ax e1 t1, VCall ay e2 t2 ->
    w.w_describe ax == w.w_describe ay /\ e1 == e2 /\ t1 == t2
  | VLeaf ax, VLeaf ay ->
    w.w_describe ax == w.w_describe ay /\
    (forall (n: string) (st: store v). w.w_lower n ax st == w.w_lower n ay st)
  | _, _ -> False

and same_shape_list (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0)
                    (w: witness a e v eff)
                    (xs: list (action_view a e v)) (ys: list (action_view a e v))
  : Tot prop (decreases %[xs; 1]) =
  match xs, ys with
  | [], [] -> True
  | x1 :: r1, x2 :: r2 -> same_shape w x1 x2 /\ same_shape_list w r1 r2
  | _, _ -> False

/// **`fold_blind` — the core's half of no-closure-invocation, held
/// UNCONDITIONALLY.** Two same-shaped views fold to IDENTICAL outcomes
/// and identical placements. The fold cannot depend on anything in an
/// action the witness did not surface, so it cannot have applied a
/// closure the witness did not — and in this model there is no closure
/// to apply: the view carries none, and `a` has no elimination form the
/// fold could reach for.
let rec fold_blind (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
                   (w: witness a e v eff) (ar: handler_arm v eff p)
                   (node_id: string) (x: action_view a e v) (y: action_view a e v)
                   (s: store v) (pl: p)
  : Lemma (requires same_shape w x y)
          (ensures fold w ar node_id x s pl == fold w ar node_id y s pl)
          (decreases %[x; 0]) =
  match x, y with
  | VSequence _ xs, VSequence _ ys -> fold_blind_list w ar node_id xs ys s pl
  | VLeaf ax, VLeaf ay ->
    assert (w.w_lower node_id ax s == w.w_lower node_id ay s)
  | _, _ -> ()

and fold_blind_list (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
                    (w: witness a e v eff) (ar: handler_arm v eff p)
                    (node_id: string)
                    (xs: list (action_view a e v)) (ys: list (action_view a e v))
                    (s: store v) (pl: p)
  : Lemma (requires same_shape_list w xs ys)
          (ensures fold_many w ar node_id xs s pl == fold_many w ar node_id ys s pl)
          (decreases %[xs; 1]) =
  match xs, ys with
  | [], [] -> ()
  | x1 :: r1, x2 :: r2 ->
    fold_blind w ar node_id x1 x2 s pl;
    let (o1, p1) = fold w ar node_id x1 s pl in
    fold_blind_list w ar node_id r1 r2 o1.o_store p1
  | _, _ -> ()

/// **The witness obligation.** A witness is BLIND TO a relation on
/// actions when its `w_view` sends related actions to same-shaped views.
/// This is the half of no-closure-invocation that D18 places on the
/// witness: only a `View` or a `Lower` could reach a closure, so only a
/// `View` or a `Lower` could tell two actions apart by one — and this
/// says they do not.
[@@ noextract_to "FSharp"]
let blind_to (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0)
             (rel: a -> a -> prop) (w: witness a e v eff) : prop =
  forall (x: a) (y: a). rel x y ==> same_shape w (w.w_view x) (w.w_view y)

/// **`run_action_blind` — no-closure-invocation for the generic core,
/// CONDITIONAL on the witness obligation.** Under any relation the
/// witness is blind to, related actions run to identical outcomes and
/// placements. Instantiated at the UI witness and `same_but_closures`
/// below, where the obligation is discharged, it is Phase 1715's
/// `run_no_closure` again.
let run_action_blind (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
                     (rel: a -> a -> prop)
                     (w: witness a e v eff) (ar: handler_arm v eff p)
                     (node_id: string) (x: a) (y: a) (s: store v) (pl: p)
  : Lemma (requires blind_to rel w /\ rel x y)
          (ensures run_action w ar node_id x s pl == run_action w ar node_id y s pl) =
  fold_blind w ar node_id (w.w_view x) (w.w_view y) s pl

// ─── 3. `Sequence` is the fold's homomorphism ────────────────────────

let rec app_assoc (#a: Type0) (xs: list a) (ys: list a) (zs: list a)
  : Lemma (ensures app (app xs ys) zs == app xs (app ys zs)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> app_assoc rest ys zs

let rec app_nil (#a: Type0) (xs: list a)
  : Lemma (ensures app xs [] == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> app_nil rest

/// **`sequence_homomorphism`.** Running the concatenation of two
/// operation lists is running the first and then the second against the
/// store the first left, with the effects and the diagnostics
/// concatenated in order and the placement threaded through. This is
/// DECISIONS.md D7's splice property — a nested call sees the writes
/// before it and is seen by the writes after it — stated as an equation,
/// and it is what makes `Sequence` a composition rather than a fifth
/// special case.
let rec sequence_homomorphism (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
                              (w: witness a e v eff) (ar: handler_arm v eff p)
                              (node_id: string)
                              (xs: list (action_view a e v)) (ys: list (action_view a e v))
                              (s: store v) (pl: p)
  : Lemma
      (ensures
        (let (o1, p1) = fold_many w ar node_id xs s pl in
         let (o2, p2) = fold_many w ar node_id ys o1.o_store p1 in
         fold_many w ar node_id (app xs ys) s pl ==
           ({ o_store = o2.o_store;
              o_effects = app o1.o_effects o2.o_effects;
              o_diagnostics = app o1.o_diagnostics o2.o_diagnostics }, p2)))
      (decreases xs) =
  match xs with
  | [] ->
    let (o2, _) = fold_many w ar node_id ys s pl in
    app_nil o2.o_effects;
    app_nil o2.o_diagnostics
  | x :: rest ->
    let (ox, px) = fold w ar node_id x s pl in
    sequence_homomorphism w ar node_id rest ys ox.o_store px;
    let (o1r, p1r) = fold_many w ar node_id rest ox.o_store px in
    let (o2, _) = fold_many w ar node_id ys o1r.o_store p1r in
    app_assoc ox.o_effects o1r.o_effects o2.o_effects;
    app_assoc ox.o_diagnostics o1r.o_diagnostics o2.o_diagnostics

/// The same statement at the view level, which is the form the
/// `Sequence` arm is read in. The carried actions are irrelevant to it —
/// the fold reads none of them in this arm — so any three will do.
let sequence_action_homomorphism (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
                                 (w: witness a e v eff) (ar: handler_arm v eff p)
                                 (node_id: string) (act: a) (act1: a) (act2: a)
                                 (xs: list (action_view a e v)) (ys: list (action_view a e v))
                                 (s: store v) (pl: p)
  : Lemma
      (ensures
        (let (o1, p1) = fold w ar node_id (VSequence act1 xs) s pl in
         let (o2, p2) = fold w ar node_id (VSequence act2 ys) o1.o_store p1 in
         fold w ar node_id (VSequence act (app xs ys)) s pl ==
           ({ o_store = o2.o_store;
              o_effects = app o1.o_effects o2.o_effects;
              o_diagnostics = app o1.o_diagnostics o2.o_diagnostics }, p2))) =
  sequence_homomorphism w ar node_id xs ys s pl

// ─── 4. Reserved keys are untouched ──────────────────────────────────

/// The placement's arm preserves reserved keys. This is an ASSUMPTION
/// about the seam, not a claim about it: the arm returns a store of its
/// own, so the fold cannot bound what the placement wrote.
/// `inert_preserves_reserved` below discharges it for the placements
/// that run no handlers, which is why the theorem is not vacuous.
[@@ noextract_to "FSharp"]
let arm_preserves_reserved (#v: Type0) (#eff: Type0) (#p: Type0)
                           (is_reserved: key -> bool) (ar: handler_arm v eff p) : prop =
  forall (node_id: string) (endpoint: string) (s: store v) (pl: p).
    (match ar.answer node_id endpoint s pl with
     | ONone -> True
     | OSome ans ->
       (forall (kk: key). is_reserved kk ==> lookup ans.h_store kk == lookup s kk))

let rec write_preserves_other (#v: Type0) (s: store v) (k1: key) (x: v) (k2: key)
  : Lemma (requires ~(k1 == k2))
          (ensures lookup (write s k1 x) k2 == lookup s k2)
          (decreases s) =
  match s with
  | [] -> ()
  | (k', _) :: rest -> if k' = k1 then () else write_preserves_other rest k1 x k2

/// **`fold_reserved_untouched`.** The store the fold returns agrees with
/// the store it was given at every reserved key. The only write the fold
/// performs is the `Assign` shape's, and that shape refuses a reserved
/// key before reaching it; a leaf has no store to return (K3) — so the
/// output differs from the input only at non-reserved keys, which is the
/// property multi-tenant hosting of an untrusted tree rests on.
let rec fold_reserved_untouched (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
                                (w: witness a e v eff) (ar: handler_arm v eff p)
                                (node_id: string) (x: action_view a e v) (s: store v) (pl: p)
                                (kk: key)
  : Lemma (requires arm_preserves_reserved w.w_is_reserved ar /\ w.w_is_reserved kk)
          (ensures lookup (fst (fold w ar node_id x s pl)).o_store kk == lookup s kk)
          (decreases %[x; 0]) =
  match x with
  | VAssign _ state_key value value_from ->
    if w.w_is_reserved state_key then ()
    else
      (match value_from with
       | OSome expr ->
         (match w.w_resolve s expr with
          | Resolved jv -> write_preserves_other s state_key jv kk
          | _ -> ())
       | ONone ->
         (match value with
          | OSome jv -> write_preserves_other s state_key jv kk
          | ONone -> ()))
  | VSequence _ ops -> fold_reserved_untouched_list w ar node_id ops s pl kk
  | VCall _ _ _ -> ()
  | VLeaf act ->
    (match w.w_lower node_id act s with
     | Emit _ -> ()
     | Refuse _ -> ()
     | Decline -> ())

and fold_reserved_untouched_list (#a: Type0) (#e: Type0) (#v: Type0) (#eff: Type0) (#p: Type0)
                                 (w: witness a e v eff) (ar: handler_arm v eff p)
                                 (node_id: string) (ops: list (action_view a e v))
                                 (s: store v) (pl: p) (kk: key)
  : Lemma (requires arm_preserves_reserved w.w_is_reserved ar /\ w.w_is_reserved kk)
          (ensures lookup (fst (fold_many w ar node_id ops s pl)).o_store kk == lookup s kk)
          (decreases %[ops; 1]) =
  match ops with
  | [] -> ()
  | x :: rest ->
    fold_reserved_untouched w ar node_id x s pl kk;
    let (o1, p1) = fold w ar node_id x s pl in
    fold_reserved_untouched_list w ar node_id rest o1.o_store p1 kk

/// The inert arm preserves reserved keys under EVERY predicate — it
/// declines every call, so there is no store for it to have written.
/// This is what makes `fold_reserved_untouched` a statement about the
/// placements that run no handlers rather than a conditional nobody has
/// discharged.
let inert_preserves_reserved (#v: Type0) (#eff: Type0) (#p: Type0) (is_reserved: key -> bool)
  : Lemma (arm_preserves_reserved is_reserved (inert_arm #v #eff #p)) = ()

(* ═══════════════════════════════════════════════════════════════════
   THE UI WITNESS — today's fourteen arms seen through the view.

   What follows is the adapter Phase 1897 writes (`Fuaran.Program.UI`),
   modelled: the closed `Action` union, the UI's resolution outcomes, its
   client effects, the eight host arrows it composes, and `ui_view` /
   `ui_lower` / `ui_describe` / `ui_resolve` — the four `ActionWitness` +
   `ExprWitness` members — over them. `run` is `run_action` at that
   witness with Phase 1715's exact signature, which is what keeps the
   existing differential host running unchanged: the fourteen arms seen
   through the view are the fourteen arms.
   ═══════════════════════════════════════════════════════════════════ *)

(* ───────────────────────────────────────────────────────────────────
   Resolution outcomes — `BindingResolver.Resolution`, the UI's four.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `resolveJVal`'s result, with `JValObj.toObj` already applied to
/// the resolved value (the lowering is the axiom's, not the fold's).
type res (v: Type0) =
  | JResolved : value: v -> res v
  | JNotResolved : res v
  | JErrored : message: string -> res v
  | JI18nUnresolved : i18n_key: string -> res v

/// F#: `resolveScalarText`'s result. `SResolved ONone` is the
/// resolved-but-NULL value the tier answers for the unwritten-`State`
/// steady state — the one distinction the two `TextSource` arms below
/// treat differently, so it is carried rather than collapsed.
type res_text =
  | SResolved : value: opt string -> res_text
  | SNotResolved : res_text
  | SErrored : message: string -> res_text
  | SI18nUnresolved : i18n_key: string -> res_text

(* ───────────────────────────────────────────────────────────────────
   The vocabulary the action union ranges over — `Fuaran.UI.Types`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `TextSource`. `TBound`'s binding and `TI18n`'s argument map are
/// opaque: the adapter reads the i18n KEY and hands the rest to an axiom.
type text_source (b: Type0) =
  | TLiteral : text: string -> text_source b
  | TBound : binding: b -> text_source b
  | TI18n : i18n_key: string -> args: b -> text_source b

/// F#: `NavigateTarget`. Passed through untouched — it names the
/// browsing context, not a destination, so the URL floor has no opinion
/// on it.
type nav_target =
  | NSelf : nav_target
  | NBlank : nav_target

/// F#: `FileReadEncoding`.
type file_encoding =
  | FText : file_encoding
  | FBase64 : file_encoding
  | FDataUrl : file_encoding

/// F#: `CallResultTarget`. The view reads only its PRESENCE.
type call_target =
  | CTState : state_key: string -> call_target
  | CTQuery : query_name: string -> call_target

/// F#: `ClientEffect`, restricted to the arms this witness lowers to.
/// The host union is wider (`PushState` / `Download` / `Confirm`); their
/// absence here is the claim that the bounded fold cannot reach them,
/// and the differential host's total translation is where that claim is
/// checked.
type client_effect =
  | ENavigate : route: string -> target: nav_target -> client_effect
  | EClipboard : text: string -> client_effect
  | EPrint : client_effect
  | EFocus : node_id: string -> client_effect
  | EReadFileBody : node_id: string -> encoding: string -> client_effect

/// F#: `Action<'Msg>`, the closed union. Fourteen arms, named
/// one-for-one. `k` is a closure slot the wire decoder fills with an
/// inert sentinel; `b` is a host value handed to an axiom. Nothing in
/// this module can eliminate a `k`.
type action (v: Type0) (b: Type0) (k: Type0) =
  | AChain : ops: list (action v b k) -> action v b k
  | AWriteToClipboard : text: text_source b -> action v b k
  | ADispatch : msg: k -> action v b k
  | AInvoke : capability_id: string -> args: b -> action v b k
  | AReadFileBody : file_ref: string -> file_handle: k -> encoding: file_encoding -> on_read: k -> action v b k
  | ACall : endpoint: string -> on_result: k -> into: opt call_target -> action v b k
  | ANavigate : route: text_source b -> target: nav_target -> action v b k
  | ACommitLocal : node_id: string -> action v b k
  | ANotify : channel: string -> payload: b -> action v b k
  | ASetState : state_key: key -> value: opt v -> value_from: opt b -> action v b k
  | AAiTool : tool_name: string -> args: b -> action v b k
  | APrint : action v b k
  | AConfirm : prompt: text_source b -> on_confirm: action v b k -> on_cancel: opt (action v b k) -> action v b k
  | AFocus : node_id: string -> action v b k

/// The UI-typed outcome and arm — Phase 1715's names at Phase 1715's
/// arity, which is the surface the differential host is written to.
type outcome (v: Type0) = bounded_outcome v client_effect

type arm (v: Type0) (p: Type0) = handler_arm v client_effect p

/// The host-supplied pure functions the UI witness composes. Every one
/// is a TOTAL arrow and nothing below depends on what any of them
/// answers. Modelling any of them would introduce a second
/// implementation free to disagree with the host's; the theorems hold
/// for EVERY total arrow, which is the honest statement.
///
///   * `resolve_jval` / `resolve_scalar` / `i18n_has` / `resolve_text` —
///     `Fuaran.UI.Renderer.BindingResolver`'s resolution of a binding
///     against the store, with `JValObj.toObj`'s lowering folded into the
///     value the arrow returns.
///   * `sanitize_url` — the tree wire specification's renderer URL floor
///     (`Fuaran.UI.Renderer.Sanitize.sanitizeUrl`).
///   * `is_reserved` / `reserved_prefix` —
///     `Fuaran.UI.Renderer.StateKeys.isHostReserved` and the namespace it
///     names.
///   * `route_path` — the log-safe route projection the action
///     description uses (`ActionInvocation.routePath`).
noeq type axioms (v: Type0) (b: Type0) = {
  is_reserved: key -> bool;
  reserved_prefix: string;
  resolve_jval: store v -> b -> res v;
  resolve_scalar: store v -> b -> res_text;
  i18n_has: store v -> string -> bool;
  resolve_text: store v -> text_source b -> string;
  sanitize_url: string -> opt string;
  route_path: string -> string;
}

(* ───────────────────────────────────────────────────────────────────
   `ActionWitness.Describe` — `ActionInvocation.describe`, the log-safe
   projection `Validation.describeAction` forwards to. Modelled rather
   than axiomatised, because the diagnostics the fold emits carry it and
   the differential host compares them verbatim.
   ─────────────────────────────────────────────────────────────────── *)

let describe (#v: Type0) (#b: Type0) (#k: Type0) (ax: axioms v b) (a: action v b k) : string =
  match a with
  | ADispatch _ -> "Dispatch"
  | ACall endpoint _ _ -> strcat "Call(" (strcat endpoint ")")
  | ANotify channel _ -> strcat "Notify(" (strcat channel ")")
  | ANavigate route _ ->
    (match route with
     | TLiteral literal -> strcat "Navigate(" (strcat (ax.route_path literal) ")")
     | _ -> "Navigate(<bound>)")
  | ASetState k _ _ -> strcat "SetState(" (strcat k ")")
  | AAiTool tool_name _ -> strcat "AiTool(" (strcat tool_name ")")
  | AChain _ -> "Chain"
  | ACommitLocal node_id -> strcat "CommitLocal(" (strcat node_id ")")
  | AWriteToClipboard _ -> "WriteToClipboard"
  | APrint -> "Print"
  | AConfirm _ _ _ -> "Confirm"
  | AFocus node_id -> strcat "Focus(" (strcat node_id ")")
  | AReadFileBody _ _ _ _ -> "ReadFileBody"
  | AInvoke capability_id _ -> strcat "Invoke(" (strcat capability_id ")")

/// F#: the `Result<string, string>` the `Navigate` and clipboard leaves
/// compute.
type text_result =
  | ROk : value: string -> text_result
  | RErr : message: string -> text_result

let unresolved_i18n (k: string) : string = strcat "unresolved i18n key '" (strcat k "'")

(* ───────────────────────────────────────────────────────────────────
   `ActionWitness.View` — the adapter's total match over the closed
   fourteen-case union, taken to exhaustion. Four cases are control
   structure the fold owns; the other ten are leaves. Every constructor
   is named, with no wildcard: the exhaustiveness check D18 moves into
   the adapter is checked here by the same means.
   ─────────────────────────────────────────────────────────────────── *)

let rec ui_view (#v: Type0) (#b: Type0) (#k: Type0) (a: action v b k)
  : Tot (action_view (action v b k) b v) (decreases %[a; 0]) =
  match a with
  | AChain ops -> VSequence a (ui_view_list ops)
  | ASetState state_key value value_from -> VAssign a state_key value value_from
  | ACall endpoint _ into -> VCall a endpoint (OSome? into)
  | AWriteToClipboard _ -> VLeaf a
  | ADispatch _ -> VLeaf a
  | AInvoke _ _ -> VLeaf a
  | AReadFileBody _ _ _ _ -> VLeaf a
  | ANavigate _ _ -> VLeaf a
  | ACommitLocal _ -> VLeaf a
  | ANotify _ _ -> VLeaf a
  | AAiTool _ _ -> VLeaf a
  | APrint -> VLeaf a
  | AConfirm _ _ _ -> VLeaf a
  | AFocus _ -> VLeaf a

and ui_view_list (#v: Type0) (#b: Type0) (#k: Type0) (ops: list (action v b k))
  : Tot (list (action_view (action v b k) b v)) (decreases %[ops; 1]) =
  match ops with
  | [] -> []
  | x :: rest -> ui_view x :: ui_view_list rest

(* ───────────────────────────────────────────────────────────────────
   `ActionWitness.Lower` — what each LEAF does at this witness. The
   three control cases are never handed to it through the view
   (`ui_view` sends them elsewhere) and, being total, it declines them.
   ─────────────────────────────────────────────────────────────────── *)

let ui_lower (#v: Type0) (#b: Type0) (#k: Type0)
             (ax: axioms v b) (node_id: string) (a: action v b k) (s: store v)
  : leaf_outcome client_effect =
  match a with

  // An inherently-browser arm lowered to a closure-free effect. The
  // route resolves at DISPATCH time and the URL floor judges the
  // RESOLVED string; an unresolved route navigates NOWHERE rather than
  // degrading to the empty string, which is a real navigation.
  | ANavigate route target ->
    let resolved : text_result =
      match route with
      | TLiteral literal -> ROk literal
      | TBound binding ->
        (match ax.resolve_scalar s binding with
         | SResolved (OSome value) -> ROk value
         | SResolved ONone -> RErr "the route binding resolved to no value"
         | SNotResolved -> RErr "the route binding did not resolve to a value"
         | SErrored m -> RErr m
         | SI18nUnresolved kk -> RErr (unresolved_i18n kk))
      | TI18n kk _ ->
        if ax.i18n_has s kk then ROk (ax.resolve_text s route) else RErr (unresolved_i18n kk)
    in
    (match resolved with
     | RErr reason -> Refuse (strcat reason " — nothing was navigated to")
     | ROk r ->
       match ax.sanitize_url r with
       | OSome safe -> Emit (ENavigate safe target)
       | ONone -> Refuse "route is not a safe URL")

  // The clipboard payload resolves at DISPATCH time through the same
  // resolver. A resolved-but-null value is the unwritten-`State` steady
  // state and is legitimately copied as the empty string; a binding that
  // genuinely fails to resolve is REFUSED, because on a clipboard nobody
  // sees the gap.
  | AWriteToClipboard text ->
    let payload : text_result =
      match text with
      | TLiteral literal -> ROk literal
      | TBound binding ->
        (match ax.resolve_scalar s binding with
         | SResolved (OSome value) -> ROk value
         | SResolved ONone -> ROk ""
         | SNotResolved -> RErr "the payload binding did not resolve to a value"
         | SErrored m -> RErr m
         | SI18nUnresolved kk -> RErr (unresolved_i18n kk))
      | TI18n kk _ ->
        if ax.i18n_has s kk then ROk (ax.resolve_text s text) else RErr (unresolved_i18n kk)
    in
    (match payload with
     | ROk value -> Emit (EClipboard value)
     | RErr reason -> Refuse (strcat reason " — nothing was written to the clipboard"))

  // Payload-free, and lowered rather than refused: printing is an act of
  // the machine the document is READ on.
  | APrint -> Emit EPrint

  // The node id is a bare string the AUTHOR wrote, addressing a node in
  // this document: nothing to resolve and no floor to apply.
  | AFocus target_node_id -> Emit (EFocus target_node_id)

  // The `on_read` closure is the inert decode sentinel — NOT invoked
  // here (and, in this model, not invocable: `k` has no elimination
  // form). The emitted id is the node the EVENT came from.
  | AReadFileBody _ _ encoding _ ->
    let enc =
      match encoding with
      | FText -> "Text"
      | FBase64 -> "Base64"
      | FDataUrl -> "DataUrl"
    in
    Emit (EReadFileBody node_id enc)

  // Computational host arms with no store/DOM effect on the bounded
  // path, and the one the path has no return leg for. Documented
  // declines, each with a readable diagnostic so "this action is inert
  // here" is observable rather than silent.
  | AConfirm _ _ _ -> Decline
  | ANotify _ _ -> Decline
  | AAiTool _ _ -> Decline
  | AInvoke _ _ -> Decline
  | ADispatch _ -> Decline
  | ACommitLocal _ -> Decline

  // Control structure: never a leaf through `ui_view`. Total, so it
  // answers; it answers the no-op.
  | AChain _ -> Decline
  | ASetState _ _ _ -> Decline
  | ACall _ _ _ -> Decline

/// `ExprWitness.Resolve` at the UI witness: the four-case UI resolution
/// narrowed to D18's three. `I18nUnresolved` becomes an `Errored` whose
/// message is the one production already builds, so the refusal the
/// fold emits for it is byte-identical to Phase 1715's.
let ui_resolve (#v: Type0) (#b: Type0) (ax: axioms v b) (s: store v) (binding: b) : resolution v =
  match ax.resolve_jval s binding with
  | JResolved jv -> Resolved jv
  | JNotResolved -> NotResolved
  | JErrored m -> Errored m
  | JI18nUnresolved kk -> Errored (unresolved_i18n kk)

/// The UI witness, assembled: D18's `ProgramWitness` at the UI adapter,
/// restricted to the members the fold reads.
let ui_witness (#v: Type0) (#b: Type0) (#k: Type0) (ax: axioms v b)
  : witness (action v b k) b v client_effect =
  { w_view = ui_view;
    w_lower = ui_lower ax;
    w_describe = describe ax;
    w_resolve = ui_resolve ax;
    w_is_reserved = ax.is_reserved;
    w_reserved_prefix = ax.reserved_prefix }

/// **The fold at the UI witness** — `BoundedActions.runBoundedActionWith`
/// today, `BoundedActions.run uiWitness` after Phase 1897. Phase 1715's
/// signature, so the differential host is unchanged.
let run (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
        (ax: axioms v b) (ar: arm v p)
        (node_id: string) (a: action v b k) (s: store v) (pl: p)
  : outcome v & p =
  run_action (ui_witness ax) ar node_id a s pl

(* ───────────────────────────────────────────────────────────────────
   THE UI THEOREMS — Phase 1715's five, each a corollary of the generic
   theorem at `ui_witness`. Ghost from here on.
   ─────────────────────────────────────────────────────────────────── *)

// ─── 1. Totality over the closed union ───────────────────────────────

/// Every constructor, named once more with NO wildcard. Its value is
/// uninteresting; its shape is the point — a fifteenth arm added to
/// `action` fails to compile HERE exactly as it fails to compile in
/// `ui_view`, which is what "no wildcard, no throw" buys, stated as a
/// thing the prover checks rather than a thing a reader must notice.
[@@ noextract_to "FSharp"]
let handled (#v: Type0) (#b: Type0) (#k: Type0) (a: action v b k) : bool =
  match a with
  | AChain _ -> true
  | AWriteToClipboard _ -> true
  | ADispatch _ -> true
  | AInvoke _ _ -> true
  | AReadFileBody _ _ _ _ -> true
  | ACall _ _ _ -> true
  | ANavigate _ _ -> true
  | ACommitLocal _ -> true
  | ANotify _ _ -> true
  | ASetState _ _ _ -> true
  | AAiTool _ _ -> true
  | APrint -> true
  | AConfirm _ _ _ -> true
  | AFocus _ -> true

/// The placement ANSWERED this call, at the action level.
[@@ noextract_to "FSharp"]
let answered (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
             (ar: arm v p) (node_id: string) (a: action v b k) (s: store v) (pl: p) : bool =
  match a with
  | ACall endpoint _ ONone -> OSome? (ar.answer node_id endpoint s pl)
  | _ -> false

/// **`run_total`.** Phase 1715's statement, now `fold_total` at the UI
/// witness: the fold is defined on every arm of the closed union, and
/// one step that is neither the composition arm nor a call the placement
/// answered leaves the placement untouched, leaves the store unchanged
/// or written at exactly one non-reserved key, and emits at most one
/// effect and at most one diagnostic.
let run_total (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
              (ax: axioms v b) (ar: arm v p)
              (node_id: string) (a: action v b k) (s: store v) (pl: p)
  : Lemma
      (requires (not (AChain? a)) /\ (not (answered ar node_id a s pl)))
      (ensures
        (handled a /\
         (let (o, pl') = run ax ar node_id a s pl in
          pl' == pl /\
          at_most_one o.o_effects /\
          at_most_one o.o_diagnostics /\
          (o.o_store == s \/
           (ASetState? a /\
            (exists (x: v). o.o_store == write s (ASetState?.state_key a) x) /\
            not (ax.is_reserved (ASetState?.state_key a))))))) =
  match a with
  | AChain _ -> ()
  | AWriteToClipboard _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | ADispatch _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | AInvoke _ _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | AReadFileBody _ _ _ _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | ACall _ _ _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | ANavigate _ _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | ACommitLocal _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | ANotify _ _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | ASetState _ _ _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | AAiTool _ _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | APrint -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | AConfirm _ _ _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl
  | AFocus _ -> fold_total (ui_witness ax) ar node_id (ui_view a) s pl

// ─── 2. No closure is ever invoked ───────────────────────────────────

/// Two actions differ ONLY in the closures they carry. Every other
/// position — the keys, the endpoints, the bindings, the text sources,
/// the nested actions — is required equal; the `k`-typed positions are
/// required nothing at all.
[@@ noextract_to "FSharp"]
let rec same_but_closures (#v: Type0) (#b: Type0) (#k: Type0)
                          (x: action v b k) (y: action v b k) : Tot prop (decreases %[x; 0]) =
  match x, y with
  | AChain xs, AChain ys -> same_but_closures_list xs ys
  | AWriteToClipboard t1, AWriteToClipboard t2 -> t1 == t2
  | ADispatch _, ADispatch _ -> True
  | AInvoke c1 a1, AInvoke c2 a2 -> c1 == c2 /\ a1 == a2
  | AReadFileBody f1 _ e1 _, AReadFileBody f2 _ e2 _ -> f1 == f2 /\ e1 == e2
  | ACall e1 _ i1, ACall e2 _ i2 -> e1 == e2 /\ i1 == i2
  | ANavigate r1 t1, ANavigate r2 t2 -> r1 == r2 /\ t1 == t2
  | ACommitLocal n1, ACommitLocal n2 -> n1 == n2
  | ANotify c1 p1, ANotify c2 p2 -> c1 == c2 /\ p1 == p2
  | ASetState k1 v1 f1, ASetState k2 v2 f2 -> k1 == k2 /\ v1 == v2 /\ f1 == f2
  | AAiTool t1 a1, AAiTool t2 a2 -> t1 == t2 /\ a1 == a2
  | APrint, APrint -> True
  | AConfirm p1 c1 x1, AConfirm p2 c2 x2 ->
    p1 == p2 /\ same_but_closures c1 c2 /\ same_but_closures_opt x1 x2
  | AFocus n1, AFocus n2 -> n1 == n2
  | _, _ -> False

and same_but_closures_opt (#v: Type0) (#b: Type0) (#k: Type0)
                          (x: opt (action v b k)) (y: opt (action v b k))
  : Tot prop (decreases %[x; 1]) =
  match x, y with
  | ONone, ONone -> True
  | OSome a1, OSome a2 -> same_but_closures a1 a2
  | _, _ -> False

and same_but_closures_list (#v: Type0) (#b: Type0) (#k: Type0)
                           (xs: list (action v b k)) (ys: list (action v b k))
  : Tot prop (decreases %[xs; 2]) =
  match xs, ys with
  | [], [] -> True
  | a1 :: r1, a2 :: r2 -> same_but_closures a1 a2 /\ same_but_closures_list r1 r2
  | _, _ -> False

/// Two actions related by `same_but_closures` have the same log-safe
/// description — the description reads the constructor and the
/// author-declared name, never a closure.
let describe_ignores_closures (#v: Type0) (#b: Type0) (#k: Type0)
                              (ax: axioms v b) (x: action v b k) (y: action v b k)
  : Lemma (requires same_but_closures x y)
          (ensures describe ax x == describe ax y) =
  ()

/// Two actions related by `same_but_closures` lower alike at every node
/// id and store — `ui_lower` reads the route, the payload, the encoding
/// and the node id, never a closure slot.
let ui_lower_ignores_closures (#v: Type0) (#b: Type0) (#k: Type0)
                              (ax: axioms v b) (x: action v b k) (y: action v b k)
                              (node_id: string) (s: store v)
  : Lemma (requires same_but_closures x y)
          (ensures ui_lower ax node_id x s == ui_lower ax node_id y s) =
  ()

/// `ui_view` sends `same_but_closures` actions to same-shaped views —
/// the UI witness meets the obligation `blind_to` states, one action
/// pair at a time. Composition needs the induction; every other arm is
/// a leaf or a one-level shape, discharged by the two lemmas above.
let rec ui_view_same_shape (#v: Type0) (#b: Type0) (#k: Type0)
                           (ax: axioms v b) (x: action v b k) (y: action v b k)
  : Lemma (requires same_but_closures x y)
          (ensures same_shape (ui_witness ax) (ui_view x) (ui_view y))
          (decreases %[x; 0]) =
  describe_ignores_closures ax x y;
  match x, y with
  | AChain xs, AChain ys -> ui_view_same_shape_list ax xs ys
  | _, _ ->
    let aux (n: string) (st: store v)
      : Lemma (ui_lower ax n x st == ui_lower ax n y st) =
      ui_lower_ignores_closures ax x y n st
    in
    FStar.Classical.forall_intro_2 aux

and ui_view_same_shape_list (#v: Type0) (#b: Type0) (#k: Type0)
                            (ax: axioms v b) (xs: list (action v b k)) (ys: list (action v b k))
  : Lemma (requires same_but_closures_list xs ys)
          (ensures same_shape_list (ui_witness ax) (ui_view_list xs) (ui_view_list ys))
          (decreases %[xs; 1]) =
  match xs, ys with
  | [], [] -> ()
  | a1 :: r1, a2 :: r2 ->
    ui_view_same_shape ax a1 a2;
    ui_view_same_shape_list ax r1 r2
  | _, _ -> ()

/// **`ui_blind_to_closures` — the witness obligation, DISCHARGED for the
/// UI witness.** This is the half of no-closure-invocation D18 places on
/// the adapter, proved here for the adapter's model.
let ui_blind_to_closures (#v: Type0) (#b: Type0) (#k: Type0) (ax: axioms v b)
  : Lemma (blind_to (same_but_closures #v #b #k) (ui_witness ax)) =
  let aux (x: action v b k) (y: action v b k)
    : Lemma (same_but_closures x y ==> same_shape (ui_witness ax) (ui_view x) (ui_view y)) =
    FStar.Classical.move_requires (ui_view_same_shape ax x) y
  in
  FStar.Classical.forall_intro_2 aux

/// **`run_no_closure`.** Phase 1715's statement, word for word: two
/// actions differing only in the closures they carry produce IDENTICAL
/// outcomes and identical placements. It is now `run_action_blind` — the
/// core's unconditional half — at a witness whose obligation
/// `ui_blind_to_closures` has discharged, so the theorem is again
/// unconditional. The differential host is what carries the claim back
/// to production, and its go-red case commits a fold that invokes a
/// carried closure to show the comparison catches it.
let run_no_closure (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
                   (ax: axioms v b) (ar: arm v p)
                   (node_id: string) (x: action v b k) (y: action v b k) (s: store v) (pl: p)
  : Lemma (requires same_but_closures x y)
          (ensures run ax ar node_id x s pl == run ax ar node_id y s pl) =
  ui_blind_to_closures #v #b #k ax;
  run_action_blind same_but_closures (ui_witness ax) ar node_id x y s pl

// ─── 3. `Chain` is the fold's homomorphism ───────────────────────────

/// Viewing a concatenation is concatenating the views.
let rec ui_view_list_app (#v: Type0) (#b: Type0) (#k: Type0)
                         (xs: list (action v b k)) (ys: list (action v b k))
  : Lemma (ensures ui_view_list (app xs ys) == app (ui_view_list xs) (ui_view_list ys))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> ui_view_list_app rest ys

/// **`chain_homomorphism`.** Phase 1715's action-level statement:
/// running `AChain (app xs ys)` is running `AChain xs` and then
/// `AChain ys` against the store the first left, with the effects and
/// the diagnostics concatenated in order and the placement threaded
/// through. It is `sequence_homomorphism` at the UI witness, through
/// `ui_view_list_app`.
let chain_homomorphism (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
                       (ax: axioms v b) (ar: arm v p)
                       (node_id: string) (xs: list (action v b k)) (ys: list (action v b k))
                       (s: store v) (pl: p)
  : Lemma
      (ensures
        (let (o1, p1) = run ax ar node_id (AChain xs) s pl in
         let (o2, p2) = run ax ar node_id (AChain ys) o1.o_store p1 in
         run ax ar node_id (AChain (app xs ys)) s pl ==
           ({ o_store = o2.o_store;
              o_effects = app o1.o_effects o2.o_effects;
              o_diagnostics = app o1.o_diagnostics o2.o_diagnostics }, p2))) =
  ui_view_list_app xs ys;
  sequence_homomorphism (ui_witness ax) ar node_id (ui_view_list xs) (ui_view_list ys) s pl

// ─── 4. Host-reserved keys are untouched ─────────────────────────────

/// **`reserved_untouched`.** Phase 1715's statement: the store the fold
/// returns agrees with the store it was given at every host-reserved
/// key, for any arm that preserves them. It is `fold_reserved_untouched`
/// at the UI witness, whose reserved predicate is the host's
/// `is_reserved`.
let reserved_untouched (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
                       (ax: axioms v b) (ar: arm v p)
                       (node_id: string) (a: action v b k) (s: store v) (pl: p) (kk: key)
  : Lemma (requires arm_preserves_reserved ax.is_reserved ar /\ ax.is_reserved kk)
          (ensures lookup (fst (run ax ar node_id a s pl)).o_store kk == lookup s kk) =
  fold_reserved_untouched (ui_witness ax) ar node_id (ui_view a) s pl kk
