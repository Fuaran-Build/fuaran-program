/// Phase 1716 — the proved budget as oracle, at the TOY witness.
///
/// `proofs/Budget.fst` is a model of the core's generic pricing functions,
/// `Budget.treeCost` (over the walk axis's `Cost` and child surface) and
/// `Budget.actionCascadeCost` (over the dispatch axis's action view). Until
/// fuaran#2017 those were compared with their extraction only at the UI
/// witness, which is leaving the repository; this host compares them at the
/// toy witness (`ToyDomain.fs`), in the project that reaches no UI type.
///
/// What is compared is an INTEGER, not a verdict. Above the ceiling `treeCost`
/// stops walking, so what it returns depends on which nodes it visited first —
/// the model has to keep production's explicit stack, in production's push
/// order, or the two agree only on the verdict and not on the number. So the
/// trees here are priced at every ceiling AROUND their own exact cost, where
/// the early stop lives, and include fans of UNEQUAL nodes, whose
/// above-the-ceiling answer differs with the visiting order.
///
/// Out of scope here: the G2 gate. The UI host also compares the model's
/// `step` with the UI transport loop's budget stage; there is no toy driver,
/// so there is no toy G2 stage to compare, and nothing here claims one.
///
/// And the go-red case is what says the comparison can lose: a walk that
/// accumulates with .NET's ordinary wrapping `+`, which must come out on the
/// wrong side of the ceiling.
module Fuaran.Program.Tests.ToyBudgetOracleTests

// The extraction's top-level module, aliased BEFORE the core is opened: the
// core has a `Budget` module of its own, and the two are the two sides of the
// comparison.
module ProvedBudget = Budget

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Tests.ToyDomain

module ShippedBudget = Fuaran.Program.Bounded.Budget

let private toyWitness = Fuaran.Program.Tests.ToyDomain.witness

// ─── Translation: production ⇄ the model ────────────────────────────────────
//
// The model takes the saturation bound as a parameter and owns the counting
// cap; production hardwires the first. So the bound is supplied here from
// `Int32.MaxValue` — the value production's `satAdd` / `satMul` compare
// against — and the cap is read back OUT of the extraction.

let private satBound: bigint = bigint System.Int32.MaxValue

let private countedCap: bigint = ProvedBudget.max_counted_rows

/// The toy's per-node data cost as the model's `cost_shape`. The toy's
/// `Walk.Cost` is the number of handlers a node carries, so production prices
/// a node at `satAdd 1 handlers`.
///
/// Which shape is the ASSUMED RUNG, and why this one. The model has three, and
/// only one reproduces `1 + walk.Cost` exactly at every handler count:
///
///  - `SPlain` is 1 — exact only for a node with no handlers, which is where
///    it is used.
///  - `SRows` counts its payload up to `max_counted_rows` — a cap the UI's
///    data-bearing kinds apply to their payloads and the toy's `Cost` does
///    NOT. Mapped through it, a node past the cap would diverge, and the
///    divergence would be the mapping's, not the walk's (the corpus carries
///    such a node to say so).
///  - `SWeighted` with ONE row and the weight `h` is `1 + satMul 1 h` — the
///    weight is an unbounded integer, so it is `1 + h` exactly, saturated as
///    production saturates. Used for every node with a handler.
///
/// The count is computed here from the node, not by calling the witness's
/// `Cost`: the translation states what the toy's cost IS, so a change to the
/// toy's `Cost` shows as a divergence rather than flowing through both sides.
let private handlerShape (h: bigint) : ProvedBudget.cost_shape =
    if h.IsZero then
        ProvedBudget.SPlain
    else
        ProvedBudget.SWeighted(ProvedBudget.PRows [ () ], h)

/// The children are production's own — the walk axis's `Nodes.Children`, the
/// function `treeCost` walks with — so the model and production never disagree
/// about the SHAPE of the tree, only ever about what the walk does with it.
let rec private modelNode (weigh: ToyNode -> bigint) (node: ToyNode) : ProvedBudget.nd =
    ProvedBudget.Nd(handlerShape (weigh node), toyWitness.Walk.Nodes.Children node |> List.map (modelNode weigh))

/// The toy's own weighing: one per handler.
let private handlerCount (node: ToyNode) : bigint = bigint (List.length node.Handlers)

// ─── A HEAVY variant of the toy witness ─────────────────────────────────────
//
// The toy's handler count cannot reach the saturation bound — no test builds a
// list of two billion handlers — so the saturating add inside the walk would
// never be priced. This variant changes ONE member, the walk axis's `Cost`, to
// the sum of a node's `Beep` volumes in the core's own saturating arithmetic
// (as a witness composing weights is told to), so a node can carry a cost near
// the bound. `treeCost` is generic over the witness; this is another
// instantiation of the same function, not a second implementation of it.

let private volumes (node: ToyNode) : int list =
    node.Handlers
    |> List.map (fun (_, action) ->
        match action with
        | Beep volume -> volume
        | _ -> 0)

let private heavyWitness =
    { toyWitness with
        Walk =
            { toyWitness.Walk with
                Cost = fun node -> volumes node |> List.fold ShippedBudget.satAdd 0 } }

/// The heavy weighing, EXACT — the model's `sat_mul` saturates it, which is
/// the claim being compared with production's per-handler `satAdd`.
let private volumeSum (node: ToyNode) : bigint = volumes node |> List.sumBy bigint

// ─── The comparison ─────────────────────────────────────────────────────────

/// The cost function under comparison. Production is one; the go-red case
/// below is another, and it is deliberately wrong.
type private TreeCost = int -> ToyNode -> int

let private productionCost: TreeCost = ShippedBudget.treeCost toyWitness

let private heavyProductionCost: TreeCost = ShippedBudget.treeCost heavyWitness

let private oracleTreeCost (weigh: ToyNode -> bigint) (ceiling: int) (node: ToyNode) : int =
    int (ProvedBudget.tree_cost satBound countedCap (bigint ceiling) (modelNode weigh node))

let private costDivergence
    (cost: TreeCost)
    (weigh: ToyNode -> bigint)
    (where: string)
    (ceiling: int)
    (node: ToyNode)
    : string option =
    let prod = cost ceiling node
    let oracle = oracleTreeCost weigh ceiling node

    if prod <> oracle then
        Some(sprintf "%s at ceiling %d\n  production: %d\n  oracle:     %d" where ceiling prod oracle)
    else
        None

/// Every ceiling worth asking about for one tree: the degenerate ones, the
/// exact cost, and one either side of it. The boundary is where the early stop
/// lives, so a corpus that never lands on it is a corpus that never tests it.
let private ceilingsAround (exact: int) : int list =
    [ 0
      1
      2
      exact - 2
      exact - 1
      exact
      exact + 1
      exact + 2
      System.Int32.MaxValue ]
    |> List.filter (fun c -> c >= 0)
    |> List.distinct

// ─── The trees ──────────────────────────────────────────────────────────────

let private node (id: string) (handlers: int) (children: ToyNode list) : ToyNode =
    { Id = id
      Label = Const(JStr id)
      Handlers = List.replicate handlers ("click", Hush)
      Children = children }

let private leaf (id: string) = node id 0 []

/// A chain of single-child nodes, `depth` deep, each carrying `handlers`. The
/// stack walk visits it one node at a time.
let rec private chain (depth: int) (handlers: int) : ToyNode =
    if depth <= 1 then
        node "leaf" handlers []
    else
        node ("d" + string depth) handlers [ chain (depth - 1) handlers ]

let private toyTrees: (string * ToyNode) list =
    [ "a bare leaf", leaf "m"
      "a node with three handlers", node "h" 3 []
      "a fan of seven leaves", node "root" 0 (List.init 7 (fun i -> leaf ("m" + string i)))
      "a chain twelve deep", chain 12 0
      "a chain eight deep, two handlers a node", chain 8 2
      "a fan of fans",
      node
          "root"
          1
          (List.init 3 (fun i -> node ("c" + string i) i (List.init 4 (fun j -> node (sprintf "m%d-%d" i j) j []))))
      // Unequal siblings: above the ceiling, WHICH of them the walk has
      // visited decides the number it returns, so these are the trees that
      // tell production's push order from any other.
      "a fan of unequal nodes",
      node "root" 0 [ node "a" 1 []; node "b" 50 []; node "c" 2 []; node "d" 300 []; node "e" 0 [] ]
      "a chain of unequal fans",
      node
          "root"
          4
          [ node "x" 9 [ node "x1" 0 []; node "x2" 70 [] ]
            node "y" 0 [ node "y1" 33 [] ] ]
      // Nodes whose `Cost` is large: what makes a node data-bearing at the toy.
      "a node with five thousand handlers", node "root" 0 [ node "big" 5_000 []; leaf "after" ]
      "a node with as many handlers as the counting cap", node "cap" (int ProvedBudget.max_counted_rows) []
      "a node past the counting cap", node "root" 1 [ node "past" (int ProvedBudget.max_counted_rows + 50_000) [] ] ]

let private beeping (id: string) (vols: int list) (children: ToyNode list) : ToyNode =
    { Id = id
      Label = Const(JStr id)
      Handlers = vols |> List.map (fun v -> "click", Beep v)
      Children = children }

/// Trees for the heavy variant, where a node's own cost — and a sum of them —
/// reaches the saturation bound.
let private heavyTrees: (string * ToyNode) list =
    [ "a quiet tree under the heavy weighing", beeping "root" [ 1; 2 ] [ beeping "a" [ 3 ] []; beeping "b" [] [] ]
      "a node whose own cost saturates", beeping "root" [ System.Int32.MaxValue; 5 ] []
      "a node one below the bound", beeping "root" [ System.Int32.MaxValue - 1 ] []
      "two nodes whose costs sum past the bound",
      beeping "root" [] [ beeping "a" [ 1_500_000_000 ] []; beeping "b" [ 1_500_000_000 ] [] ]
      "a heavy node among light ones",
      beeping "root" [ 1 ] [ beeping "a" [ 2 ] []; beeping "b" [ 2_000_000_000 ] []; beeping "c" [ 3 ] [] ] ]

/// The exact cost, which is what a ceiling is chosen AROUND. Priced at the
/// saturation bound, where the walk cannot stop early.
let private exactCost (cost: TreeCost) (tree: ToyNode) : int = cost System.Int32.MaxValue tree

let private priced (cost: TreeCost) (trees: (string * ToyNode) list) : (string * int * ToyNode) list =
    trees
    |> List.collect (fun (name, tree) ->
        ceilingsAround (exactCost cost tree)
        |> List.map (fun ceiling -> name, ceiling, tree))

// ─── The cascade cost ───────────────────────────────────────────────────────

/// A toy action, projected onto the model's `act` — or `None` where the model
/// has no shape for it. The model's `act` is a `Chain` of leaves: a sequence
/// sums its members and every other action costs one. Production prices the
/// assignment, the call, the guard and the leaves at one, as the model does;
/// it prices a `Choose` at one plus its dearer arm, a `Repeat` at one plus its
/// body times its bound, and an `Each` at its lowered elements summed — and the
/// model has NO shape for any of those three. Mapping them to `ALeaf` would
/// make the model agree by discarding the disagreement, so they are not
/// mapped; the test below records the gap instead.
let rec private modelCascade (a: ToyAction) : ProvedBudget.act option =
    match toyWitness.Dispatch.Action.View a with
    | ActionView.Sequence members ->
        let mapped = members |> List.map modelCascade

        if List.forall Option.isSome mapped then
            Some(ProvedBudget.AChain(mapped |> List.choose id))
        else
            None
    | ActionView.Assign _
    | ActionView.Call _
    | ActionView.Require _
    | ActionView.Leaf _ -> Some ProvedBudget.ALeaf
    | ActionView.Choose _
    | ActionView.Repeat _
    | ActionView.Each _ -> None

let private ring =
    Ring("/x", false, fun () -> failwith "a carried closure was invoked")

let rec private nest (depth: int) : ToyAction =
    if depth <= 0 then
        Beep 1
    else
        Seq [ nest (depth - 1); Hush ]

/// Every shape the model can express, nested.
let private cascadeCases: (string * ToyAction) list =
    [ "a leaf", Beep 3
      "a declined leaf", Hush
      "a write", Put("k", Some(JStr "v"), None)
      "a call", ring
      "a guard", Need(Const(JBool true))
      "an empty sequence", Seq []
      "a flat sequence of every non-flow shape",
      Seq [ Beep 1; Hush; Put("k", None, Some(Read "a")); ring; Need Missing ]
      "a sequence of empty sequences", Seq [ Seq []; Seq [] ]
      "a sequence nesting a sequence", Seq [ Seq [ Beep 1; Beep 2 ]; Hush ]
      "a sequence nested twenty deep", nest 20
      "a wide flat sequence", Seq(List.replicate 70 Hush) ]

/// The shapes production prices and the model cannot express — each priced by
/// production at something OTHER than what the model would answer if it were
/// mapped to a leaf (or a sequence of them), which is what makes this a gap
/// rather than a difference of notation.
let private unexpressible: (string * ToyAction * int) list =
    [ "a branch", Pick(Const(JBool true), Beep 1, Seq [ Beep 1; Beep 2 ], None), 3
      "a literal repeat", Times(Bound.Literal 3, Beep 1), 4
      "a parameter repeat, priced at the top of its range", Times(Bound.Parameter(Read "k", 0, 5), Beep 1), 6
      "an iteration over two elements", ForEach([ JInt 1; JInt 2 ], "x", Seq [ Beep 1; Hush ]), 4
      "an iteration over nothing", ForEach([], "x", Beep 1), 0
      "a sequence carrying a branch", Seq [ Hush; Pick(Missing, Beep 1, Times(Bound.Literal 2, Beep 1), None) ], 5 ]

// ─── The go-red case ────────────────────────────────────────────────────────

/// The proved walk with ONE line changed: the accumulator adds with .NET's
/// ordinary `int`, which wraps, instead of with the saturating add. Everything
/// else — the node cost, the push order, the guards — is the oracle's own,
/// called by name, so the only difference between this walk and the proved one
/// is the defect. It is committed on purpose: the core's `Budget.fs` says an
/// overflow that wrapped negative "would read as cheap and admit the very tree
/// the budget exists to refuse", and a differential that cannot be made to say
/// so is not evidence of anything.
let rec private wrappingWalk (ceiling: int) (pending: ProvedBudget.nd list) (running: int) : int =
    if running > ceiling then
        running
    else
        match pending with
        | [] -> running
        | cur :: rest ->
            wrappingWalk
                ceiling
                (ProvedBudget.push_all (ProvedBudget.kids cur) rest)
                (running + int (ProvedBudget.node_cost satBound countedCap cur))

let private wrappingTreeCost: TreeCost =
    fun ceiling tree -> wrappingWalk ceiling [ modelNode volumeSum tree ] 0

/// Two nodes whose costs sum past `Int32.MaxValue` while each sits inside it,
/// under a ceiling each of them individually clears. The saturating add
/// answers the bound and the tree is refused; a wrapping add answers a
/// negative number and the tree is admitted.
let private overflowingTree: ToyNode =
    heavyTrees
    |> List.find (fst >> (=) "two nodes whose costs sum past the bound")
    |> snd

let private overflowCeiling = 2_000_000_000

// ─── The tests ──────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList
        "Phase 1716 - the proved budget as oracle at the toy witness"
        [ test "the toy corpus straddles the ceiling in both directions" {
              // A corpus every ceiling admitted — or every ceiling refused —
              // would report the same green while testing none of the
              // behaviour that lives at the boundary.
              let verdicts =
                  (priced productionCost toyTrees
                   |> List.map (fun (_, ceiling, tree) -> productionCost ceiling tree > ceiling))
                  @ (priced heavyProductionCost heavyTrees
                     |> List.map (fun (_, ceiling, tree) -> heavyProductionCost ceiling tree > ceiling))

              Expect.isGreaterThanOrEqual (List.length toyTrees) 11 "the toy trees are the ones declared above"
              Expect.isGreaterThanOrEqual (List.length heavyTrees) 5 "the heavy trees are the ones declared above"

              Expect.isTrue
                  (List.contains true verdicts)
                  "no tree is over its ceiling at any of the ceilings tried, so the refusal path is never priced"

              Expect.isTrue
                  (List.contains false verdicts)
                  "no tree is within its ceiling at any of the ceilings tried, so the exact path is never priced"

              // The number, not just the verdict: a tree must cost more than
              // one, a node must cost more than the counting cap (which is
              // what says the rung chosen for the toy's `Cost` is exact past
              // it), and the heavy corpus must reach the saturation bound.
              Expect.isTrue
                  (toyTrees |> List.exists (fun (_, tree) -> exactCost productionCost tree > 1))
                  "every toy tree costs one, so nothing here prices a handler"

              Expect.isTrue
                  (toyTrees
                   |> List.exists (fun (_, tree) -> exactCost productionCost tree > int ProvedBudget.max_counted_rows))
                  "no toy tree costs more than the counting cap, so the rung's exactness past it is untested"

              Expect.isTrue
                  (heavyTrees
                   |> List.exists (fun (_, tree) -> exactCost heavyProductionCost tree = System.Int32.MaxValue))
                  "no heavy tree reaches the saturation bound, so the walk's saturating add is never priced"

              // And unequal siblings above the ceiling: a ceiling strictly
              // between two orders of visiting must exist, or the push order
              // is not being tested. Visiting the fan's children in the
              // OTHER order must give a different over-ceiling answer.
              let fan = toyTrees |> List.find (fst >> (=) "a fan of unequal nodes") |> snd

              let reversed =
                  { fan with
                      Children = List.rev fan.Children }

              Expect.isTrue
                  ([ 0..400 ]
                   |> List.exists (fun ceiling -> productionCost ceiling fan <> productionCost ceiling reversed))
                  "the unequal fan's over-ceiling answer does not depend on the visiting order, so it tests nothing about it"
          }

          test "the oracle agrees with production over toy trees at every ceiling around their exact cost" {
              let cases = priced productionCost toyTrees

              let divergences =
                  cases
                  |> List.choose (fun (name, ceiling, tree) ->
                      costDivergence productionCost handlerCount name ceiling tree)

              match divergences with
              | [] -> ()
              | first :: rest ->
                  failtestf
                      "the extracted model and production disagree on %d of %d (tree, ceiling) pair(s). First divergence:\n%s"
                      (List.length rest + 1)
                      cases.Length
                      first
          }

          test "the oracle agrees with production at the saturation bound, under a heavier toy weighing" {
              let cases = priced heavyProductionCost heavyTrees

              let divergences =
                  cases
                  |> List.choose (fun (name, ceiling, tree) ->
                      costDivergence heavyProductionCost volumeSum name ceiling tree)

              match divergences with
              | [] -> ()
              | first :: rest ->
                  failtestf
                      "the extracted model and production disagree on %d of %d heavy (tree, ceiling) pair(s). First divergence:\n%s"
                      (List.length rest + 1)
                      cases.Length
                      first
          }

          test "the oracle agrees with production on the action cascade cost of every shape it can express" {
              Expect.isGreaterThanOrEqual (List.length cascadeCases) 11 "the cascade corpus is the one declared above"

              let divergences =
                  cascadeCases
                  |> List.choose (fun (name, action) ->
                      match modelCascade action with
                      | None -> Some(sprintf "%s: the model was expected to express this case and does not" name)
                      | Some modelled ->
                          let prod = ShippedBudget.actionCascadeCost toyWitness action
                          let oracle = int (ProvedBudget.action_cascade_cost modelled)

                          if prod <> oracle then
                              Some(sprintf "%s\n  production: %d\n  oracle:     %d" name prod oracle)
                          else
                              None)

              match divergences with
              | [] -> ()
              | first :: _ -> failtestf "the extracted model and production disagree on a cascade cost:\n%s" first
          }

          test "FINDING: the budget model has no Choose, Repeat or Each, which production prices - recorded, not mapped" {
              // Production's `actionCascadeCost` gained the flow shapes in
              // Phase 1976 and the iteration in Phase 1990; the budget model's
              // `act` is still a chain of leaves. At the UI witness that is no
              // gap, because no UI action views as either; at the generic core
              // it is one, and this case keeps it visible. When the model
              // gains the shapes, this case goes red and is replaced by a
              // comparison.
              for name, action, expected in unexpressible do
                  Expect.isNone (modelCascade action) (sprintf "%s has no shape in the budget model" name)

                  let prod = ShippedBudget.actionCascadeCost toyWitness action

                  Expect.equal prod expected (sprintf "%s is priced by production at %d" name expected)

                  // What the model would answer if the shape were flattened
                  // into leaves: one per non-sequence node met at the top
                  // level. Production answers otherwise, so flattening would
                  // be a false agreement, not a translation.
                  let rec flattened (a: ToyAction) : int =
                      match toyWitness.Dispatch.Action.View a with
                      | ActionView.Sequence members -> members |> List.sumBy flattened
                      | _ -> 1

                  Expect.notEqual
                      prod
                      (flattened action)
                      (sprintf "%s: production's price differs from the leaf the model would read it as" name)
          }

          test "GO RED: an accumulator that wraps loses the ceiling comparison at the toy witness" {
              // The honest run first: the defect is committed against a tree
              // production and the oracle agree on.
              Expect.isNone
                  (costDivergence heavyProductionCost volumeSum "the overflowing tree" overflowCeiling overflowingTree)
                  "production disagrees with the oracle on the very tree the defect is committed against"

              // Then the defect, through the SAME comparison the cases above
              // run on, so what is shown is that the harness REPORTS it.
              Expect.isSome
                  (costDivergence wrappingTreeCost volumeSum "the overflowing tree" overflowCeiling overflowingTree)
                  "the comparison harness did not report a walk whose accumulator wraps — a harness that cannot lose is not evidence"

              let proved = oracleTreeCost volumeSum overflowCeiling overflowingTree
              let wrapped = wrappingTreeCost overflowCeiling overflowingTree

              // And the divergence is the one that matters: the ceiling
              // comparison coming out the other way — the tree ADMITTED.
              Expect.isTrue
                  (proved > overflowCeiling)
                  (sprintf "the proved cost %d should be over the ceiling %d" proved overflowCeiling)

              Expect.isTrue
                  (wrapped <= overflowCeiling)
                  (sprintf
                      "the wrapping cost %d should read as WITHIN the ceiling %d — that is the whole defect"
                      wrapped
                      overflowCeiling)
          } ]
