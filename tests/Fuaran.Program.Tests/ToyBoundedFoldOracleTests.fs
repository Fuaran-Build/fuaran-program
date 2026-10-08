/// Phase 1715 — the proved bounded fold as oracle, at the TOY witness.
///
/// `proofs/BoundedFold.fst` is a model of the generic fold, and a model is a
/// claim about the code only if something runs the two side by side. This host
/// runs the EXTRACTION's generic `run_action` (`proofs/oracle/BoundedFold.fs`,
/// byte-identical to what the prover emitted) beside the core's own
/// `BoundedActions.run`, both at the toy witness (`ToyDomain.fs`, a domain with
/// its own node, action, expression, store, op and effect types), and compares
/// the store, the effects, the diagnostics, the halt flag and the placement at
/// once.
///
/// It was first written (Phase 1896) inside the UI-typed parity suite, beside
/// the comparison at the UI witness. It is re-hosted here (fuaran#2017) because
/// nothing in it is UI-typed: the generic core is compared with the generic
/// model through a witness that is not the UI tier, in the project that reaches
/// no UI type — so the evidence does not leave the repository when the UI
/// adapters do.
///
/// The model's witness is DERIVED from the production one rather than written
/// beside it: its view is the production `View` applied to exhaustion, and its
/// `Lower` / `Describe` / `Resolve` / reserved predicate are the production
/// members over the model's association-list store. So the only thing that can
/// disagree is the fold.
///
/// Three corpora answer different questions. The hand-written corpus names
/// every view shape, every refusal the fold owns, every way a guard halts
/// (Phase 1967), every arm and halt of the two flow shapes (Phase 1976) and the
/// per-element iteration (Phase 1990). The answering arm crosses the placement
/// seam. The specification's toy driver-semantics family (fuaran#2011) is the
/// documents this repository is certified against. And the go-red case is what
/// says the comparison can lose at all.
module Fuaran.Program.Tests.ToyBoundedFoldOracleTests

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Tests.ToyDomain

module ToyFamily = Fuaran.Program.Tests.ToyScenarios

let private toyWitness = Fuaran.Program.Tests.ToyDomain.witness

// ─── Translation: production ⇄ the model ────────────────────────────────────
//
// Deliberately DUMB — a constructor for a constructor, with no decision in it —
// because a translation that decided anything would be a third implementation
// of the fold, sitting between the two this file exists to compare.

let private modelOpt (x: 'a option) : BoundedFold.opt<'a> =
    match x with
    | Some v -> BoundedFold.OSome v
    | None -> BoundedFold.ONone

/// `BoundedDiagnostic` is the core's type, not the UI tier's: the fold reports
/// in it at every witness.
let private modelDiagnostic (d: BoundedDiagnostic) : BoundedFold.diagnostic =
    match d with
    | BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, action) -> BoundedFold.DUnsupported(nodeId, action)
    | BoundedDiagnostic.Refused(nodeId, action, reason) -> BoundedFold.DRefused(nodeId, action, reason)

let private toyLookup (s: BoundedFold.store<JVal>) (key: string) : JVal option =
    match BoundedFold.lookup s key with
    | BoundedFold.OSome value -> Some value
    | BoundedFold.ONone -> None

/// A bound, in the model's shape: the model's count is a `nat`, which the
/// extraction renders as `BigInteger`.
let private modelBound (bound: Bound<ToyExpr>) : BoundedFold.bound<ToyExpr> =
    match bound with
    | Bound.Literal count -> BoundedFold.BLiteral(bigint count)
    | Bound.Parameter(count, lo, hi) -> BoundedFold.BParameter(count, bigint lo, bigint hi)

/// The production `View`, taken to exhaustion — the model's `w_view`. Nine
/// shapes since Phase 1991, named without a wildcard, as the model names them.
/// An `Each` reaches the model LOWERED (`VEach` carries its elements): the
/// production `Substitute` is applied here, once per element, and the
/// elements viewed — so the differential below compares production, which
/// substitutes as it folds, against a model handed the substituted bodies,
/// and a `Substitute` that did not preserve the body's shape would show as a
/// divergence. A STORE-BOUND `Each` (Phase 1991) reaches the model as
/// `VEachOf`, lowered over the extent `entry` — the store the case STARTS
/// from — answers for its source; the model reads the source itself and
/// checks the ceiling, so what the host hands it is the obligation D36 item
/// 7 states: the elements of the extent the same store holds at entry. No
/// corpus case writes a source before entering the loop that reads it.
let rec private toyModelViewIn (entry: ToyStore) (a: ToyAction) : BoundedFold.action_view<ToyAction, ToyExpr, JVal> =
    let view = toyModelViewIn entry

    match toyWitness.Dispatch.Action.View a with
    | ActionView.Sequence items -> BoundedFold.VSequence(a, items |> List.map view)
    | ActionView.Assign(key, value, from) -> BoundedFold.VAssign(a, key, modelOpt value, modelOpt from)
    | ActionView.Call(endpoint, declaresTarget) -> BoundedFold.VCall(a, endpoint, declaresTarget)
    | ActionView.Require condition -> BoundedFold.VRequire(a, condition)
    | ActionView.Choose(entry', whenTrue, whenFalse, exit) ->
        BoundedFold.VChoose(a, entry', view whenTrue, view whenFalse, modelOpt exit)
    | ActionView.Repeat(bound, body) -> BoundedFold.VRepeat(a, modelBound bound, view body)
    | ActionView.Each(Collection.Literal elements, placeholder, body) ->
        BoundedFold.VEach(
            a,
            ActionWitness.lowered toyWitness.Dispatch.Action elements placeholder body
            |> List.map view
        )
    | ActionView.Each(Collection.Stored(source, ceiling), placeholder, body) ->
        let extent =
            match resolveWith (fun k -> Map.tryFind k entry) source with
            | ExprResolution.Resolved(JArr xs) -> xs
            | _ -> []

        BoundedFold.VEachOf(
            a,
            source,
            bigint (max ceiling 0),
            extent,
            ActionWitness.lowered toyWitness.Dispatch.Action extent placeholder body
            |> List.map view
        )
    | ActionView.Leaf _ -> BoundedFold.VLeaf a

/// The model witness, lowering a store-bound `Each` over the extent `entry`
/// holds (the production store the case starts from).
let private toyModelWitnessIn (entry: ToyStore) : BoundedFold.witness<ToyAction, ToyExpr, JVal, ToyEffect> =
    { w_view = toyModelViewIn entry
      w_lower =
        fun nodeId action s ->
            match lowerWith (toyLookup s) nodeId action with
            | LeafOutcome.Emit effect -> BoundedFold.Emit effect
            | LeafOutcome.Refuse reason -> BoundedFold.Refuse reason
            | LeafOutcome.Decline -> BoundedFold.Decline
      w_describe = toyWitness.Dispatch.Action.Describe
      w_resolve =
        fun s expr ->
            match resolveWith (toyLookup s) expr with
            | ExprResolution.Resolved value -> BoundedFold.Resolved value
            | ExprResolution.NotResolved -> BoundedFold.NotResolved
            | ExprResolution.Errored message -> BoundedFold.Errored message
      w_is_reserved = toyWitness.Dispatch.Store.IsReserved
      w_reserved_prefix = toyWitness.Dispatch.Store.ReservedPrefix
      // The core's own `jv = JBool true`, an arrow here because the model's
      // value type is abstract — see the model's `w_is_true`.
      w_is_true = fun jv -> jv = JBool true
      // The core reads `JInt n` itself; the model's arrow, wired to that.
      w_as_count =
        fun jv ->
            match jv with
            | JInt n when n >= 0 -> BoundedFold.OSome(bigint n)
            | _ -> BoundedFold.ONone
      // The core reads `JArr xs` itself (Phase 1991); the model's arrow, wired
      // to that.
      w_as_elements =
        fun jv ->
            match jv with
            | JArr xs -> BoundedFold.OSome xs
            | _ -> BoundedFold.ONone }

/// The model witness at the empty store — for the walks that read no store.
let private toyModelWitness: BoundedFold.witness<ToyAction, ToyExpr, JVal, ToyEffect> =
    toyModelWitnessIn Map.empty

let private toyNoCall = fun () -> failwith "a carried closure was invoked"

// ─── The hand-written corpus ────────────────────────────────────────────────

/// The flow-shape cases (Phase 1976), by the labels the corpus below carries —
/// named here so the corpus-shape test can assert each is still present, and a
/// later edit cannot delete one while leaving the claim that cites it standing.
let private flowShapeCases: string list =
    [ "branch true arm, no exit"
      "branch false arm, exit fails as it must"
      "branch true arm, exit holds as it must"
      "branch exit violated after the true arm"
      "branch exit held after the false arm"
      "branch condition unresolved halts"
      "branch condition errored halts with its text"
      "branch exit unresolved"
      "branch exit errored"
      "repeat literal"
      "repeat zero times"
      "repeat parameter in range"
      "repeat over its bound halts before the body"
      "repeat bound not a count"
      "repeat bound unresolved"
      "repeat bound errored"
      "repeat halts inside its body"
      "a branch inside a repeat inside a branch" ]

/// The per-element iteration cases (Phase 1990), likewise.
let private eachCases: string list =
    [ "each writes the element, element by element"
      "each over nothing runs nothing"
      "each with a leaf and a write per element"
      "each halts at the element whose guard fails"
      "each nested in each, distinct placeholders"
      "each inside a branch inside a repeat" ]

/// The store-bound cases (Phase 1991), by label: each loop case reads a
/// collection the case BEFORE it wrote, so the host lowers over the extent the
/// case's entry store holds, as the model's hypothesis requires.
let private storedEachCases: string list =
    [ "stored each writes the element, element by element"
      "stored each over the ceiling halts before the first element"
      "stored each over a source that is not a list halts"
      "stored each over a source that does not resolve halts"
      "stored each whose body grows its own collection runs the entry extent"
      "stored each over nothing runs nothing" ]

/// Every view shape, every refusal the fold owns and every leaf outcome: a
/// literal and a derived write, a write reading a key an earlier step wrote,
/// the reserved namespace, an unresolved and an errored expression, a call
/// declined, a call declaring a target, a leaf emitted / refused / declined,
/// a leaf reading the store, and all of it nested. Run IN ORDER, threading the
/// store: "derived write" reads what "literal write" wrote, and "muted leaf"
/// is muted by "muting".
let private toyCorpus: (string * ToyAction) list =
    [ "literal write", Put("a", Some(JStr "one"), None)
      "derived write", Put("b", None, Some(Read "a"))
      "reserved key", Put("sys.x", Some(JStr "no"), None)
      "unresolved", Put("c", None, Some Missing)
      "errored", Put("d", None, Some(Fail "boom"))
      "neither value nor expression", Put("e", None, None)
      "declined call", Ring("/nowhere", false, toyNoCall)
      "targeted call", Ring("/answer", true, toyNoCall)
      "emitted leaf", Beep 4
      "refused leaf", Beep 12
      "declined leaf", Hush
      "muting", Put("muted", Some(JBool true), None)
      "muted leaf", Beep 4
      "nested",
      Seq
          [ Put("muted", Some(JBool false), None)
            Seq [ Beep 2; Put("f", None, Some(Read "b")) ]
            Ring("/answer", false, toyNoCall)
            Put("g", None, Some(Read "answered"))
            Seq [] ]
      // The halting guard (Phase 1967): one that holds, one that is false,
      // one that is not a boolean, one unresolved, one errored — and each
      // halting one inside a sequence, so the short-circuit is compared too.
      "guard holds", Need(Const(JBool true))
      "guard false", Seq [ Need(Const(JBool false)); Put("never", Some(JStr "no"), None) ]
      "guard non-boolean", Seq [ Need(Const(JStr "yes")); Beep 1 ]
      "guard unresolved",
      Seq
          [ Put("h", Some(JStr "before"), None)
            Need Missing
            Put("i", Some(JStr "after"), None) ]
      "guard errored", Seq [ Need(Fail "typed refusal"); Ring("/answer", false, toyNoCall) ]
      "guard reads the store", Seq [ Put("ok", Some(JBool true), None); Need(Read "ok"); Beep 3 ]
      "nested halt stops the outer sequence", Seq [ Seq [ Beep 1; Need(Const(JBool false)) ]; Beep 2 ]
      // The two flow shapes (Phase 1976): every arm of the branch and the
      // repeat, every way each halts, and the two nested.
      "branch true arm, no exit",
      Pick(Const(JBool true), Put("p", Some(JStr "t"), None), Put("p", Some(JStr "f"), None), None)
      "branch false arm, exit fails as it must",
      Pick(Const(JBool false), Beep 1, Put("q", Some(JStr "f"), None), Some(Const(JBool false)))
      "branch true arm, exit holds as it must", Pick(Read "ok", Put("r", Some(JStr "t"), None), Hush, Some(Read "ok"))
      "branch exit violated after the true arm",
      Seq
          [ Pick(Const(JBool true), Put("s", Some(JStr "t"), None), Hush, Some(Const(JBool false)))
            Beep 2 ]
      "branch exit held after the false arm",
      Seq [ Pick(Const(JStr "no"), Beep 1, Beep 2, Some(Const(JBool true))); Beep 3 ]
      "branch condition unresolved halts", Seq [ Pick(Missing, Beep 1, Beep 2, None); Beep 3 ]
      "branch condition errored halts with its text", Pick(Fail "typed refusal", Beep 1, Beep 2, None)
      "branch exit unresolved", Pick(Const(JBool true), Beep 1, Beep 2, Some Missing)
      "branch exit errored", Pick(Const(JBool false), Beep 1, Beep 2, Some(Fail "exit typed"))
      "repeat literal", Times(Bound.Literal 3, Seq [ Beep 1; Put("n", Some(JStr "x"), None) ])
      "repeat zero times", Times(Bound.Literal 0, Beep 9)
      "repeat parameter in range", Seq [ Put("k", Some(JInt 2), None); Times(Bound.Parameter(Read "k", 0, 5), Beep 1) ]
      "repeat over its bound halts before the body", Seq [ Times(Bound.Parameter(Const(JInt 9), 0, 5), Beep 1); Beep 4 ]
      "repeat bound not a count", Times(Bound.Parameter(Const(JStr "x"), 0, 5), Beep 1)
      "repeat bound unresolved", Times(Bound.Parameter(Missing, 0, 5), Beep 1)
      "repeat bound errored", Times(Bound.Parameter(Fail "bound typed", 0, 5), Beep 1)
      "repeat halts inside its body", Seq [ Times(Bound.Literal 3, Seq [ Beep 1; Need(Const(JBool false)) ]); Beep 2 ]
      "a branch inside a repeat inside a branch",
      Pick(
          Const(JBool true),
          Times(
              Bound.Literal 2,
              Pick(Const(JBool false), Beep 1, Put("m", Some(JStr "f"), None), Some(Const(JBool false)))
          ),
          Hush,
          Some(Const(JBool true))
      )
      // Per-element iteration over a literal collection (Phase 1990): the
      // lowered form element by element — a write per element, a leaf per
      // element, nothing for an empty collection, a halt at the element whose
      // guard fails (and the elements after it never run), nested iterations
      // with distinct placeholders, and an iteration inside the other two
      // flow shapes.
      "each writes the element, element by element",
      ForEach([ JStr "a"; JStr "b"; JStr "c" ], "x", Put("last", None, Some(Hole "x")))
      "each over nothing runs nothing", ForEach([], "x", Beep 9)
      "each with a leaf and a write per element",
      ForEach([ JInt 1; JInt 2 ], "v", Seq [ Beep 2; Put("seen", None, Some(Hole "v")) ])
      "each halts at the element whose guard fails",
      Seq
          [ ForEach([ JBool true; JBool false; JBool true ], "ok", Seq [ Need(Hole "ok"); Beep 1 ])
            Beep 5 ]
      "each nested in each, distinct placeholders",
      ForEach(
          [ JStr "r1"; JStr "r2" ],
          "row",
          ForEach(
              [ JInt 1; JInt 2 ],
              "col",
              Seq [ Put("row", None, Some(Hole "row")); Put("col", None, Some(Hole "col")) ]
          )
      )
      "each inside a branch inside a repeat",
      Times(
          Bound.Literal 2,
          Pick(
              Const(JBool true),
              ForEach([ JInt 7 ], "n", Put("n", None, Some(Hole "n"))),
              Hush,
              Some(Const(JBool true))
          )
      )
      // Phase 1991: the collection is written by one case and read at entry
      // by the next.
      "rows for the stored each", Put("rows", Some(JArr [ JStr "r1"; JInt 2; JBool false ]), None)
      "stored each writes the element, element by element",
      ForEachOf(Read "rows", 3, "x", Seq [ Put("last", None, Some(Hole "x")); Beep 1 ])
      "stored each over the ceiling halts before the first element",
      ForEachOf(Read "rows", 2, "x", Seq [ Put("never", Some(JStr "ran"), None); Beep 9 ])
      "stored each over a source that is not a list halts", ForEachOf(Read "a", 3, "x", Beep 9)
      "stored each over a source that does not resolve halts", ForEachOf(Read "absent", 3, "x", Beep 9)
      "stored each whose body grows its own collection runs the entry extent",
      ForEachOf(
          Read "rows",
          9,
          "x",
          Seq
              [ Put("last", None, Some(Hole "x"))
                Put("rows", Some(JArr [ JStr "p"; JStr "q"; JStr "r"; JStr "s"; JStr "t" ]), None)
                Beep 2 ]
      )
      "empty rows for the stored each", Put("rows", Some(JArr []), None)
      "stored each over nothing runs nothing", ForEachOf(Read "rows", 0, "x", Beep 9) ]

// ─── The comparison ─────────────────────────────────────────────────────────

/// The fold under comparison. Production is one; the go-red case below is
/// another, and it is deliberately wrong.
type private ToyFold =
    HandlerArm<ToyStore, ToyEffect, int>
        -> string
        -> ToyAction
        -> ToyStore
        -> int
        -> BoundedOutcome<ToyStore, ToyEffect> * int

let private production: ToyFold =
    fun arm nodeId action s placement -> BoundedActions.run toyWitness arm nodeId action s placement

/// A placement that answers `/answer` — writing the store, emitting an effect,
/// reporting a diagnostic and counting — and declines everything else.
let private toyArm: HandlerArm<ToyStore, ToyEffect, int> =
    { ReadExtent = ExtentReader.live
      Answer =
        fun nodeId endpoint s count ->
            if endpoint = "/answer" then
                Some
                    { Store = Map.add "answered" (JStr endpoint) s
                      Effects = [ Sound(nodeId, 7) ]
                      Diagnostics = [ BoundedDiagnostic.Refused(nodeId, "arm", "noted") ]
                      Placement = count + 1 }
            else
                None }

let private toyModelArm: BoundedFold.handler_arm<JVal, ToyEffect, int> =
    { answer =
        fun nodeId endpoint s count ->
            if endpoint = "/answer" then
                BoundedFold.OSome
                    { h_store = BoundedFold.write s "answered" (JStr endpoint)
                      h_effects = [ Sound(nodeId, 7) ]
                      h_diagnostics = [ BoundedFold.DRefused(nodeId, "arm", "noted") ]
                      h_placement = count + 1 }
            else
                BoundedFold.ONone }

/// Run a corpus through both, threading each side's store and placement from
/// step to step, and report every step where the store, the effects, the
/// diagnostics, the halt flag or the placement disagree.
let private toyDivergences
    (fold: ToyFold)
    (corpus: (string * ToyAction) list)
    (prodArm: HandlerArm<ToyStore, ToyEffect, int>)
    (modelArm: BoundedFold.handler_arm<JVal, ToyEffect, int>)
    : string list =
    let seed = Map.ofList [ "title", JStr "t" ]

    corpus
    |> List.fold
        (fun (prodStore, modelStore, prodCount, modelCount, found) (label, action) ->
            let prod, prodCount' = fold prodArm "n1" action prodStore prodCount

            let model, modelCount' =
                BoundedFold.run_action (toyModelWitnessIn prodStore) modelArm "n1" action modelStore modelCount

            let prodState = prod.Store |> Map.toList
            let modelState = model.o_store |> List.sortBy fst
            let prodDiagnostics = prod.Diagnostics |> List.map modelDiagnostic

            let found' =
                if
                    prodState <> modelState
                    || prod.Effects <> model.o_effects
                    || prodDiagnostics <> model.o_diagnostics
                    || prod.Halted <> model.o_halted
                    || prodCount' <> modelCount'
                then
                    found
                    @ [ sprintf
                            "%s: store %A / %A, effects %A / %A, diagnostics %A / %A, halted %b / %b, placement %d / %d"
                            label
                            prodState
                            modelState
                            prod.Effects
                            model.o_effects
                            prodDiagnostics
                            model.o_diagnostics
                            prod.Halted
                            model.o_halted
                            prodCount'
                            modelCount' ]
                else
                    found

            prod.Store, model.o_store, prodCount', modelCount', found')
        (seed, Map.toList seed, 0, 0, [])
    |> fun (_, _, _, _, found) -> found

// ─── fuaran#2011 — the toy driver-semantics FAMILY through the generic fold ──
//
// The hand-written corpus above is this host's own; the scenario family is the
// SPECIFICATION's (`PROGRAM_WIRE.md` §10.6), read off the same corpus clone the
// conformance leg reads, through the same loader (`ToyCorpus`). Every scripted
// event of every toy scenario, resolved by the toy transport to the action its
// node carries, is folded by the extraction and by the ported core — each side
// threading its own store from event to event, exactly as the scenario drives
// it — at a placement that answers no call. A handler-loop scenario's call is
// answered by a handler at its own placement, which is the staging oracle's
// subject, not this fold's: here its call is compared unanswered, as every
// other.

/// The toy family as the corpus enumerates it — or `None` when the run's
/// family selection leaves it out. The corpus root is resolved as the
/// conformance leg resolves it (`FUARAN_PROGRAM_SPEC`, else the sibling clone),
/// and a corpus that is absent FAILS rather than skips.
let private toyFamily () : ToyFamily.ToyScenario list option =
    let root = ToyCorpus.fixturesRoot

    if List.contains ToyFamily.Family (ToyCorpus.selected root) then
        Some(ToyCorpus.load root)
    else
        None

/// Every scripted action of a scenario, in drive order, with its node: the
/// actions the transport admits AND the budget prices within the ceiling — an
/// event the loop refuses never reaches the fold, on either side.
let private toyFamilyActions (scenario: ToyFamily.ToyScenario) : (string * ToyAction) list =
    scenario.Events
    |> List.choose (fun ev ->
        match ToyFamily.dispatch scenario.Tree ev with
        | ToyFamily.Run action when
            Fuaran.Program.Bounded.Budget.actionCascadeCost toyWitness action
            <= ToyFamily.budget.MaxActions
            ->
            Some(ev.NodeId, action)
        | _ -> None)

/// Fold one scenario's script through both, threading each side's store from
/// the empty one, and report the first event where they disagree.
let private toyFamilyDivergence (scenario: ToyFamily.ToyScenario) : string option =
    toyFamilyActions scenario
    |> List.indexed
    |> List.fold
        (fun (prodStore: ToyStore, modelStore, found) (i, (nodeId, action)) ->
            match found with
            | Some _ -> prodStore, modelStore, found
            | None ->
                let prod, _ =
                    BoundedActions.run toyWitness HandlerArm.inert nodeId action prodStore ()

                let model, _ =
                    BoundedFold.run_action
                        (toyModelWitnessIn prodStore)
                        (BoundedFold.inert_arm ())
                        nodeId
                        action
                        modelStore
                        ()

                let agree =
                    (prod.Store |> Map.toList) = (model.o_store |> List.sortBy fst)
                    && prod.Effects = model.o_effects
                    && (prod.Diagnostics |> List.map modelDiagnostic) = model.o_diagnostics
                    && prod.Halted = model.o_halted

                prod.Store,
                model.o_store,
                (if agree then
                     None
                 else
                     Some(
                         sprintf
                             "%s, scripted action %d at %s: store %A / %A, effects %A / %A, halted %b / %b"
                             scenario.Name
                             i
                             nodeId
                             (prod.Store |> Map.toList)
                             (model.o_store |> List.sortBy fst)
                             prod.Effects
                             model.o_effects
                             prod.Halted
                             model.o_halted
                     )))
        (Map.empty, [], None)
    |> fun (_, _, found) -> found

// ─── The go-red case ────────────────────────────────────────────────────────

/// A fold that INVOKES the closure a `Ring` carries and lets that reach the
/// store. This is precisely what the model's `run_no_closure` rules out — the
/// model's closure slots have an abstract type with no elimination form — and
/// it is committed here on purpose: a differential that cannot be made to fail
/// is not evidence of anything. Everything else defers to production, so the
/// ONLY difference between this fold and the real one is the defect.
let private closureInvoking: ToyFold =
    fun arm nodeId action s placement ->
        match action with
        | Ring(_, _, onAnswer) ->
            onAnswer ()

            { Store = Map.add "invoked" (JBool true) s
              Effects = []
              Diagnostics = []
              Halted = false },
            placement
        | _ -> production arm nodeId action s placement

/// A `Ring` whose closure is harmless to run, so the defect shows as a
/// divergence rather than as an exception.
let private carrying: (string * ToyAction) list =
    [ "go-red", Ring("/nowhere", false, ignore) ]

// ─── The tests ──────────────────────────────────────────────────────────────

let private shapeOf (action: ToyAction) : string =
    match toyWitness.Dispatch.Action.View action with
    | ActionView.Sequence _ -> "Sequence"
    | ActionView.Assign _ -> "Assign"
    | ActionView.Call _ -> "Call"
    | ActionView.Require _ -> "Require"
    | ActionView.Choose _ -> "Choose"
    | ActionView.Repeat _ -> "Repeat"
    | ActionView.Each _ -> "Each"
    | ActionView.Leaf _ -> "Leaf"

[<Tests>]
let tests =
    testList
        "Phase 1715 - the proved bounded fold as oracle at the toy witness"
        [ test "the toy driver-semantics family yields actions to compare (fuaran#2011)" {
              match toyFamily () with
              | None -> skiptestf "%s is not selected by %s" ToyFamily.Family ToyFamily.SelectionVariable
              | Some scenarios ->
                  Expect.isNonEmpty scenarios "the corpus enumerates no toy scenario"

                  let actions = scenarios |> List.map (fun s -> s.Name, toyFamilyActions s)

                  for name, scripted in actions do
                      Expect.isNonEmpty scripted (name + " contributes at least one action to compare over")

                  let handlerLoop =
                      scenarios
                      |> List.filter (fun s -> s.Requires = ToyFamily.HandlerLoop)
                      |> List.length

                  printfn
                      "proof host: %s - %d scenario(s), %d scripted action(s) folded by the extraction and the core (%d handler-loop scenario(s) compared at an unanswering placement)"
                      ToyFamily.Family
                      (List.length scenarios)
                      (actions |> List.sumBy (snd >> List.length))
                      handlerLoop
          }

          test
              "the extracted generic fold agrees with the ported core over the toy driver-semantics family (fuaran#2011)" {
              match toyFamily () with
              | None -> skiptestf "%s is not selected by %s" ToyFamily.Family ToyFamily.SelectionVariable
              | Some scenarios ->
                  match scenarios |> List.tryPick toyFamilyDivergence with
                  | None -> ()
                  | Some first ->
                      failtestf "the generic model and the ported core disagree over the toy family:\n%s" first
          }

          test "the toy corpus names every view shape, and the fold's every refusal" {
              let shapes = toyCorpus |> List.map (snd >> shapeOf) |> Set.ofList

              Expect.equal
                  shapes
                  (Set.ofList [ "Sequence"; "Assign"; "Call"; "Require"; "Choose"; "Repeat"; "Each"; "Leaf" ])
                  "all eight shapes"

              Expect.isGreaterThanOrEqual (List.length toyCorpus) 21 "the corpus is the one declared above"

              let halting =
                  toyCorpus
                  |> List.filter (fun (_, a) ->
                      (BoundedActions.runInert toyWitness "n1" a (Map.ofList [ "ok", JBool true ])).Halted)

              Expect.isGreaterThanOrEqual (List.length halting) 5 "the corpus halts, at the top and inside a sequence"
          }

          test "the toy corpus carries the flow-shape cases (Phase 1976) and the each cases (Phase 1990) by name" {
              // The claims that cite these cases cite them BY LABEL, and the
              // labels live inside one threaded run rather than as tests of
              // their own — so a deleted case would leave no failing test
              // behind it. This is the test it leaves behind.
              let labels = toyCorpus |> List.map fst

              Expect.equal (List.distinct labels) labels "every corpus label is distinct"

              for label in flowShapeCases @ eachCases @ storedEachCases do
                  Expect.contains labels label (sprintf "the corpus carries %s" label)

              let shapeOfLabel label =
                  toyCorpus |> List.find (fst >> (=) label) |> snd |> shapeOf

              // A flow case is a branch or a repeat at its top, or a sequence
              // around one (the cases that compare the short-circuit after a
              // halt); an each case is an iteration, or a sequence or repeat
              // around one.
              let rec reaches (shapes: string list) (action: ToyAction) : bool =
                  List.contains (shapeOf action) shapes
                  || (match toyWitness.Dispatch.Action.View action with
                      | ActionView.Sequence members -> members |> List.exists (reaches shapes)
                      | ActionView.Choose(_, whenTrue, whenFalse, _) ->
                          reaches shapes whenTrue || reaches shapes whenFalse
                      | ActionView.Repeat(_, body) -> reaches shapes body
                      | _ -> false)

              let actionOf label =
                  toyCorpus |> List.find (fst >> (=) label) |> snd

              for label in flowShapeCases do
                  Expect.isTrue
                      (reaches [ "Choose"; "Repeat" ] (actionOf label))
                      (sprintf "%s reaches a branch or a repeat (it views as %s)" label (shapeOfLabel label))

              for label in eachCases do
                  Expect.isTrue
                      (reaches [ "Each" ] (actionOf label))
                      (sprintf "%s reaches an iteration (it views as %s)" label (shapeOfLabel label))

              Expect.equal (List.length flowShapeCases) 18 "eighteen flow-shape cases"
              Expect.equal (List.length eachCases) 6 "six each cases"

              for label in storedEachCases do
                  Expect.isTrue (toyCorpus |> List.exists (fun (l, _) -> l = label)) label

              Expect.equal (List.length storedEachCases) 6 "six stored-each cases (Phase 1991)"
          }

          test "the extracted generic fold agrees with the ported core at the toy witness" {
              match toyDivergences production toyCorpus HandlerArm.inert (BoundedFold.inert_arm ()) with
              | [] -> ()
              | first :: _ -> failtestf "the generic model and the ported core disagree:\n%s" first
          }

          test "and across the placement seam, when the toy placement ANSWERS a call" {
              match toyDivergences production toyCorpus toyArm toyModelArm with
              | [] -> ()
              | first :: _ -> failtestf "the generic model and the ported core disagree on the answered call:\n%s" first
          }

          test "GO RED: a fold that invokes a carried closure loses the comparison at the toy witness" {
              // The honest run first, on the very case the defect is
              // committed against, so what the defective fold loses is the
              // defect and not the fixture.
              Expect.isEmpty
                  (toyDivergences production carrying HandlerArm.inert (BoundedFold.inert_arm ()))
                  "production agrees with the oracle on the very case the defect is committed against"

              Expect.isNonEmpty
                  (toyDivergences closureInvoking carrying HandlerArm.inert (BoundedFold.inert_arm ()))
                  "a fold that invokes the closure a Ring carries MUST diverge from the proved model — a comparison that cannot lose is not evidence"
          } ]
