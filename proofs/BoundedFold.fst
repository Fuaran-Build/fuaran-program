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
///
/// # What this module is
///
/// A hand-written model of `BoundedActions.runBoundedActionWith`
/// (`src/Fuaran.Program.Bounded/BoundedActions.fs`) — the one interpreter
/// every placement runs — clause for clause. Every definition below names
/// its F# counterpart in the comment above it. The differential host
/// (`tests/Fuaran.Program.Parity.Tests/ProofOracleTests.fs`) runs the
/// EXTRACTION of this module beside production over the conformance
/// corpus's driver-semantics family and an arm-complete action corpus,
/// and requires the store, the effect list and the diagnostics to agree
/// at every step — that host is the only thing that says this model is
/// about the code that ships.
///
/// # What is opaque, and why
///
/// Eight host-supplied pure functions are AXIOMS — total arrows this
/// model takes as parameters rather than re-implements. They are the
/// fold's inputs, not the fold:
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
///
/// Modelling any of them would introduce a second implementation free to
/// disagree with the host's; the theorems below hold for EVERY total
/// arrow, which is the honest statement. The placement seam `arm` is
/// opaque for the same reason: what a call MEANS is placement-specific,
/// and this model proves only that WHERE it is recognised is not.
///
/// Two payload positions are opaque TYPES, and that is the point of
/// law 2. `b` carries a host value the fold hands to an axiom and never
/// inspects (a binding, an i18n argument map, a notify payload). `k`
/// carries a CLOSURE the wire decoder substituted an inert sentinel for:
/// nothing in this module can eliminate a `k`, and `run_no_closure`
/// turns that absence into a theorem.
///
/// Nothing about the browser placement, the durable journal, or the
/// per-connection session cells is modelled.
///
/// # The theorems
///
///   * `run_total` — every arm of the closed union is named, with no
///     wildcard; one step that is neither composition nor an ANSWERED
///     call leaves the placement untouched, leaves the store identical
///     or writes exactly one non-host-reserved key, and emits at most
///     one effect and at most one diagnostic.
///   * `run_no_closure` — two actions differing only in the closures
///     they carry produce IDENTICAL outcomes. A fold that invoked a
///     carried closure could not satisfy this.
///   * `chain_homomorphism` — `run (AChain (app xs ys)) s` is
///     `run (AChain ys)` applied to the store `run (AChain xs) s` left,
///     with effects and diagnostics concatenated in order. DECISIONS.md
///     D7's splice property, as an equation.
///   * `reserved_untouched` — the output store agrees with the input at
///     every host-reserved key, for any arm that preserves them (and the
///     inert arm does, which `inert_preserves_reserved` discharges).

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
   The store — `BoundedActions.BoundedStore` (= `BindingSources`).

   Only the `State` map is written on this path, so only `State` is
   modelled: an association list over an abstract value type `v`. The
   other channels (`Filters` / `Selections` / `QueryResults` / `I18n` /
   `Locale`) are host context the fold never writes and only ever reads
   THROUGH an axiom, so they reach the model as part of the arrow the
   differential host supplies rather than as fields here.
   ─────────────────────────────────────────────────────────────────── *)

type key = string

type store (v: Type0) = list (key & v)

/// F#: `Map.tryFind`.
let rec lookup (#v: Type0) (s: store v) (k: key) : Tot (opt v) (decreases s) =
  match s with
  | [] -> ONone
  | (k', x) :: rest -> if k' = k then OSome x else lookup rest k

/// F#: `Map.add` — replace in place when the key is present, append
/// otherwise. Replacing rather than shadowing is what keeps `lookup` in
/// agreement with a map's semantics, and what lets the differential host
/// compare the two stores as key-ordered sequences.
let rec write (#v: Type0) (s: store v) (k: key) (x: v) : Tot (store v) (decreases s) =
  match s with
  | [] -> [(k, x)]
  | (k', y) :: rest -> if k' = k then (k, x) :: rest else (k', y) :: write rest k x

(* ───────────────────────────────────────────────────────────────────
   Resolution outcomes — `BindingResolver.Resolution`.
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
/// opaque: the fold reads the i18n KEY and hands the rest to an axiom.
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

/// F#: `CallResultTarget`. The fold reads only its PRESENCE.
type call_target =
  | CTState : state_key: string -> call_target
  | CTQuery : query_name: string -> call_target

/// F#: `ClientEffect`, restricted to the arms this fold emits. The host
/// union is wider (`PushState` / `Download` / `Confirm`); their absence
/// here is the claim that the bounded fold cannot reach them, and the
/// differential host's total translation is where that claim is checked.
type client_effect =
  | ENavigate : route: string -> target: nav_target -> client_effect
  | EClipboard : text: string -> client_effect
  | EPrint : client_effect
  | EFocus : node_id: string -> client_effect
  | EReadFileBody : node_id: string -> encoding: string -> client_effect

/// F#: `BoundedDiagnostic`. The action is named by its log-safe
/// description rather than carried, exactly as production does — which
/// is also what makes two runs that differ only in carried closures
/// produce EQUAL diagnostics (law 2).
type diagnostic =
  | DUnsupported : node_id: string -> action_name: string -> diagnostic
  | DRefused : node_id: string -> action_name: string -> reason: string -> diagnostic

/// F#: `Action<'Msg>`, the closed union. Fourteen arms, named
/// one-for-one. `k` is a closure slot the wire decoder fills with an
/// inert sentinel; `b` is a host value handed to an axiom.
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

(* ───────────────────────────────────────────────────────────────────
   The outcome, the placement seam, and the axioms.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `BoundedOutcome`.
type outcome (v: Type0) = {
  o_store: store v;
  o_effects: list client_effect;
  o_diagnostics: list diagnostic;
}

/// F#: `HandlerAnswer<'Placement>`.
type handler_answer (v: Type0) (p: Type0) = {
  h_store: store v;
  h_effects: list client_effect;
  h_diagnostics: list diagnostic;
  h_placement: p;
}

/// F#: `HandlerArm<'Placement>` — what a call action MEANS at this
/// placement. Opaque: `ONone` DECLINES, which is the documented no-op
/// every placement with no handler registry gives.
noeq type arm (v: Type0) (p: Type0) = {
  answer: string -> string -> store v -> p -> opt (handler_answer v p);
}

/// F#: `HandlerArm.inert` — the arm that declines every call.
let inert_arm (#v: Type0) (#p: Type0) : arm v p = { answer = (fun _ _ _ _ -> ONone) }

/// The host-supplied pure functions this fold composes. Every one is a
/// TOTAL arrow and nothing below depends on what any of them answers.
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
   The action description — `ActionInvocation.describe`, the log-safe
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

(* ───────────────────────────────────────────────────────────────────
   The three outcome constructors — `BoundedActions.store` / `noOp` /
   `refused`.
   ─────────────────────────────────────────────────────────────────── *)

let store_only (#v: Type0) (s: store v) : outcome v =
  { o_store = s; o_effects = []; o_diagnostics = [] }

let no_op (#v: Type0) (#b: Type0) (#k: Type0)
          (ax: axioms v b) (node_id: string) (a: action v b k) (s: store v) : outcome v =
  { o_store = s; o_effects = []; o_diagnostics = [ DUnsupported node_id (describe ax a) ] }

let refused (#v: Type0) (#b: Type0) (#k: Type0)
            (ax: axioms v b) (node_id: string) (a: action v b k) (reason: string) (s: store v) : outcome v =
  { o_store = s; o_effects = []; o_diagnostics = [ DRefused node_id (describe ax a) reason ] }

/// F#: the `Result<JVal option, string>` the `SetState` arm computes.
type jval_payload (v: Type0) =
  | POk : value: opt v -> jval_payload v
  | PErr : message: string -> jval_payload v

/// F#: the `Result<string, string>` the `Navigate` and clipboard arms
/// compute.
type text_result =
  | ROk : value: string -> text_result
  | RErr : message: string -> text_result

let unresolved_i18n (k: string) : string = strcat "unresolved i18n key '" (strcat k "'")

(* ───────────────────────────────────────────────────────────────────
   THE FOLD — `BoundedActions.runBoundedActionWith`.

   Termination is structural on the action. `run` and `run_many` are
   mutually recursive with the tree/forest lexicographic measure: the
   list inside `AChain` is a strict subterm of the action, and each
   element is a strict subterm of the list.
   ─────────────────────────────────────────────────────────────────── *)

let rec run (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
            (ax: axioms v b) (ar: arm v p)
            (node_id: string) (a: action v b k) (s: store v) (pl: p)
  : Tot (outcome v & p) (decreases %[a; 0]) =
  match a with

  // The one store mutation: write the `State` channel. The host-reserved
  // namespace is closed on this path too — the loop's whole premise is
  // that the tree is untrusted.
  | ASetState state_key value value_from ->
    (if ax.is_reserved state_key then
       refused ax node_id a
         (strcat "State key '"
           (strcat state_key
             (strcat "' is under the host-reserved '" (strcat ax.reserved_prefix "' namespace")))) s
     else
       let payload : jval_payload v =
         match value_from with
         | OSome binding ->
           (match ax.resolve_jval s binding with
            | JResolved jv -> POk (OSome jv)
            | JNotResolved -> POk ONone
            | JErrored m -> PErr m
            | JI18nUnresolved kk -> PErr (unresolved_i18n kk))
         | ONone -> POk value
       in
       match payload with
       | POk (OSome jv) -> store_only (write s state_key jv)
       | POk ONone -> refused ax node_id a "valueFrom did not resolve to a value — no write performed" s
       | PErr m -> refused ax node_id a (strcat "valueFrom errored: " (strcat m " — no write performed")) s),
    pl

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
     | RErr reason -> refused ax node_id a (strcat reason " — nothing was navigated to") s
     | ROk r ->
       match ax.sanitize_url r with
       | OSome safe -> { o_store = s; o_effects = [ ENavigate safe target ]; o_diagnostics = [] }
       | ONone -> refused ax node_id a "route is not a safe URL" s),
    pl

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
     | ROk value -> { o_store = s; o_effects = [ EClipboard value ]; o_diagnostics = [] }
     | RErr reason -> refused ax node_id a (strcat reason " — nothing was written to the clipboard") s),
    pl

  // Payload-free, and lowered rather than refused: printing is an act of
  // the machine the document is READ on.
  | APrint -> { o_store = s; o_effects = [ EPrint ]; o_diagnostics = [] }, pl

  // The node id is a bare string the AUTHOR wrote, addressing a node in
  // this document: nothing to resolve and no floor to apply.
  | AFocus target_node_id ->
    { o_store = s; o_effects = [ EFocus target_node_id ]; o_diagnostics = [] }, pl

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
    { o_store = s; o_effects = [ EReadFileBody node_id enc ]; o_diagnostics = [] }, pl

  // Compose: fold in order, threading the store AND the placement's
  // accumulation, concatenating effects and diagnostics.
  | AChain ops -> run_many ax ar node_id ops s pl

  // Computational host arms with no store/DOM effect on the bounded
  // path, and the one the path has no return leg for. Documented no-ops,
  // each with a readable diagnostic so "this action is inert here" is
  // observable rather than silent.
  | AConfirm _ _ _ -> no_op ax node_id a s, pl
  | ANotify _ _ -> no_op ax node_id a s, pl
  | AAiTool _ _ -> no_op ax node_id a s, pl
  | AInvoke _ _ -> no_op ax node_id a s, pl
  | ADispatch _ -> no_op ax node_id a s, pl
  | ACommitLocal _ -> no_op ax node_id a s, pl

  // A call that ALSO declares where its answer should land is REFUSED
  // rather than honoured or quietly ignored: result-target ownership
  // sits with the handler.
  | ACall _ _ (OSome _) ->
    refused ax node_id a
      "the call declares a result target; a handler declares where its own results land" s,
    pl

  // `on_result` is the inert decode sentinel — never invoked. The
  // placement's arm decides what the call MEANS here; declining is the
  // documented no-op.
  | ACall endpoint _ ONone ->
    (match ar.answer node_id endpoint s pl with
     | ONone -> no_op ax node_id a s, pl
     | OSome ans ->
       { o_store = ans.h_store; o_effects = ans.h_effects; o_diagnostics = ans.h_diagnostics },
       ans.h_placement)

and run_many (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
             (ax: axioms v b) (ar: arm v p)
             (node_id: string) (ops: list (action v b k)) (s: store v) (pl: p)
  : Tot (outcome v & p) (decreases %[ops; 1]) =
  match ops with
  | [] -> store_only s, pl
  | x :: rest ->
    let (o1, p1) = run ax ar node_id x s pl in
    let (o2, p2) = run_many ax ar node_id rest o1.o_store p1 in
    { o_store = o2.o_store;
      o_effects = app o1.o_effects o2.o_effects;
      o_diagnostics = app o1.o_diagnostics o2.o_diagnostics },
    p2

(* ───────────────────────────────────────────────────────────────────
   THE THEOREMS

   Everything from here on is ghost: the predicates carry
   `noextract_to "FSharp"` and the lemmas are erased by the extractor,
   so the oracle the differential host runs is exactly the definitions
   above.
   ─────────────────────────────────────────────────────────────────── *)

// ─── 1. Totality over the closed union ───────────────────────────────

/// Every constructor, named once more with NO wildcard. Its value is
/// uninteresting; its shape is the point — a fifteenth arm added to
/// `action` fails to compile HERE exactly as it fails to compile in
/// `run`, which is what "no wildcard, no throw" buys, stated as a thing
/// the prover checks rather than a thing a reader must notice.
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

[@@ noextract_to "FSharp"]
let at_most_one (#a: Type0) (l: list a) : bool =
  match l with
  | [] -> true
  | [_] -> true
  | _ -> false

/// The placement ANSWERED this call — the one arm whose outcome is the
/// placement's rather than the fold's, and therefore the one the
/// structural characterisation below cannot speak for. Naming it is how
/// the seam stays visible instead of being quietly assumed away.
[@@ noextract_to "FSharp"]
let answered (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
             (ar: arm v p) (node_id: string) (a: action v b k) (s: store v) (pl: p) : bool =
  match a with
  | ACall endpoint _ ONone -> OSome? (ar.answer node_id endpoint s pl)
  | _ -> false

/// **`run_total`.** The fold is defined on every arm of the closed
/// union — delivered by the `Tot` effect and the `decreases` clause on
/// `run`, and by `handled` naming every constructor without a wildcard
/// — and one step that is neither the composition arm nor a call the
/// placement answered is characterised structurally: the placement is
/// untouched, the store is either unchanged or written at exactly one
/// key the host-reserved predicate rejects, and at most one effect and
/// at most one diagnostic are emitted.
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
  | ASetState state_key value value_from ->
    if ax.is_reserved state_key then ()
    else
      (match value_from with
       | OSome binding ->
         (match ax.resolve_jval s binding with
          | JResolved _ -> ()
          | _ -> ())
       | ONone -> (match value with | OSome _ -> () | ONone -> ()))
  | _ -> ()

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

/// **`run_no_closure`.** Two actions differing only in the closures they
/// carry produce IDENTICAL outcomes and identical placements. The fold
/// cannot depend on what a closure IS, so it cannot have applied one —
/// which is the safety property `BoundedActions.fs` states at the top of
/// the file, as a theorem rather than a comment. In this model the
/// closure slots have an abstract type with no elimination form, so a
/// fold that DID invoke one could not be written here at all; the
/// differential host is what carries the claim back to production, and
/// its go-red case commits exactly that fold to show the comparison
/// catches it.
let rec run_no_closure (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
                       (ax: axioms v b) (ar: arm v p)
                       (node_id: string) (x: action v b k) (y: action v b k) (s: store v) (pl: p)
  : Lemma (requires same_but_closures x y)
          (ensures run ax ar node_id x s pl == run ax ar node_id y s pl)
          (decreases %[x; 0]) =
  describe_ignores_closures ax x y;
  match x, y with
  | AChain xs, AChain ys -> run_no_closure_list ax ar node_id xs ys s pl
  | _, _ -> ()

and run_no_closure_list (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
                        (ax: axioms v b) (ar: arm v p)
                        (node_id: string) (xs: list (action v b k)) (ys: list (action v b k))
                        (s: store v) (pl: p)
  : Lemma (requires same_but_closures_list xs ys)
          (ensures run_many ax ar node_id xs s pl == run_many ax ar node_id ys s pl)
          (decreases %[xs; 1]) =
  match xs, ys with
  | [], [] -> ()
  | a1 :: r1, a2 :: r2 ->
    run_no_closure ax ar node_id a1 a2 s pl;
    let (o1, p1) = run ax ar node_id a1 s pl in
    run_no_closure_list ax ar node_id r1 r2 o1.o_store p1
  | _, _ -> ()

// ─── 3. `Chain` is the fold's homomorphism ───────────────────────────

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

/// **`chain_homomorphism`.** Running the concatenation of two operation
/// lists is running the first and then the second against the store the
/// first left, with the effects and the diagnostics concatenated in
/// order and the placement threaded through. This is DECISIONS.md D7's
/// splice property — a nested call sees the writes before it and is seen
/// by the writes after it — stated as an equation, and it is what makes
/// `Chain` a composition rather than a fifteenth special case.
let rec chain_homomorphism (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
                           (ax: axioms v b) (ar: arm v p)
                           (node_id: string) (xs: list (action v b k)) (ys: list (action v b k))
                           (s: store v) (pl: p)
  : Lemma
      (ensures
        (let (o1, p1) = run_many ax ar node_id xs s pl in
         let (o2, p2) = run_many ax ar node_id ys o1.o_store p1 in
         run_many ax ar node_id (app xs ys) s pl ==
           ({ o_store = o2.o_store;
              o_effects = app o1.o_effects o2.o_effects;
              o_diagnostics = app o1.o_diagnostics o2.o_diagnostics }, p2)))
      (decreases xs) =
  match xs with
  | [] ->
    let (o2, _) = run_many ax ar node_id ys s pl in
    app_nil o2.o_effects;
    app_nil o2.o_diagnostics
  | x :: rest ->
    let (ox, px) = run ax ar node_id x s pl in
    chain_homomorphism ax ar node_id rest ys ox.o_store px;
    let (o1r, p1r) = run_many ax ar node_id rest ox.o_store px in
    let (o2, _) = run_many ax ar node_id ys o1r.o_store p1r in
    app_assoc ox.o_effects o1r.o_effects o2.o_effects;
    app_assoc ox.o_diagnostics o1r.o_diagnostics o2.o_diagnostics

/// The same statement at the action level, which is the form
/// `BoundedActions.fs`'s `Chain` arm is read in.
let chain_action_homomorphism (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
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
  chain_homomorphism ax ar node_id xs ys s pl

// ─── 4. Host-reserved keys are untouched ─────────────────────────────

/// The placement's arm preserves host-reserved keys. This is an
/// ASSUMPTION about the seam, not a claim about it: the arm returns a
/// store of its own, so the fold cannot bound what the placement wrote.
/// `inert_preserves_reserved` below discharges it for the two placements
/// that run no handlers, which is why the theorem is not vacuous.
[@@ noextract_to "FSharp"]
let arm_preserves_reserved (#v: Type0) (#b: Type0) (#p: Type0)
                           (ax: axioms v b) (ar: arm v p) : prop =
  forall (node_id: string) (endpoint: string) (s: store v) (pl: p).
    (match ar.answer node_id endpoint s pl with
     | ONone -> True
     | OSome ans ->
       (forall (kk: key). ax.is_reserved kk ==> lookup ans.h_store kk == lookup s kk))

let rec write_preserves_other (#v: Type0) (s: store v) (k1: key) (x: v) (k2: key)
  : Lemma (requires ~(k1 == k2))
          (ensures lookup (write s k1 x) k2 == lookup s k2)
          (decreases s) =
  match s with
  | [] -> ()
  | (k', _) :: rest -> if k' = k1 then () else write_preserves_other rest k1 x k2

/// **`reserved_untouched`.** The store the fold returns agrees with the
/// store it was given at every host-reserved key. The only write the
/// fold performs is the `SetState` arm's, and that arm refuses a
/// reserved key before reaching it — so the output differs from the
/// input only at non-host-reserved `State` keys, which is the property
/// multi-tenant hosting of an untrusted tree rests on.
let rec reserved_untouched (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
                           (ax: axioms v b) (ar: arm v p)
                           (node_id: string) (a: action v b k) (s: store v) (pl: p) (kk: key)
  : Lemma (requires arm_preserves_reserved ax ar /\ ax.is_reserved kk)
          (ensures lookup (fst (run ax ar node_id a s pl)).o_store kk == lookup s kk)
          (decreases %[a; 0]) =
  match a with
  | ASetState state_key value value_from ->
    if ax.is_reserved state_key then ()
    else
      (match value_from with
       | OSome binding ->
         (match ax.resolve_jval s binding with
          | JResolved jv -> write_preserves_other s state_key jv kk
          | _ -> ())
       | ONone ->
         (match value with
          | OSome jv -> write_preserves_other s state_key jv kk
          | ONone -> ()))
  | AChain ops -> reserved_untouched_list ax ar node_id ops s pl kk
  | _ -> ()

and reserved_untouched_list (#v: Type0) (#b: Type0) (#k: Type0) (#p: Type0)
                            (ax: axioms v b) (ar: arm v p)
                            (node_id: string) (ops: list (action v b k)) (s: store v) (pl: p) (kk: key)
  : Lemma (requires arm_preserves_reserved ax ar /\ ax.is_reserved kk)
          (ensures lookup (fst (run_many ax ar node_id ops s pl)).o_store kk == lookup s kk)
          (decreases %[ops; 1]) =
  match ops with
  | [] -> ()
  | x :: rest ->
    reserved_untouched ax ar node_id x s pl kk;
    let (o1, p1) = run ax ar node_id x s pl in
    reserved_untouched_list ax ar node_id rest o1.o_store p1 kk

/// The inert arm preserves host-reserved keys — it declines every call,
/// so there is no store for it to have written. This is what makes
/// `reserved_untouched` a statement about the two placements that run no
/// handlers rather than a conditional nobody has discharged.
let inert_preserves_reserved (#v: Type0) (#b: Type0) (#p: Type0) (ax: axioms v b)
  : Lemma (arm_preserves_reserved ax (inert_arm #v #p)) = ()
