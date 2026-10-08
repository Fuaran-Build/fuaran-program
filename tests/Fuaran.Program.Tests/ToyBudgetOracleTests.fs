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
/// The cascade cost is compared over EVERY action shape the core accepts
/// (fuaran#2018): the sequence, the assignment, the call, the guard and the
/// leaves, and the three flow shapes — `Choose`, `Repeat` and `Each` — each
/// with several trees, nested in one another, and at the saturation bound.
/// Until 2018 the model had no shape for the three and a FINDING case held
/// the gap open; the model now carries them and that case is a comparison.
///
/// Out of scope here: the G2 gate. The UI host also compares the model's
/// `step` with the UI transport loop's budget stage; there is no toy driver,
/// so there is no toy G2 stage to compare, and nothing here claims one.
///
/// And the go-red cases are what say the comparison can lose: a walk that
/// accumulates with .NET's ordinary wrapping `+`, which must come out on the
/// wrong side of the ceiling, and a cascade that prices a selection by its
/// CHEAPER arm, which the harness must report.
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

/// A toy action, translated onto the model's `act` — TOTAL since fuaran#2018,
/// because the model carries every shape the view has. The translation states
/// what the pricing reads of each shape, and nothing it does not:
///
///  - a `Sequence` is the chain of its members;
///  - the assignment, the call, the guard and the leaves are each a leaf;
///  - a `Choose` is its two arms — the condition and the exit are not carried,
///    because the price never reads them;
///  - a `Repeat` is its bound and its body — a literal's count, or a
///    parameter's range with the count EXPRESSION dropped, because the price
///    reads the top of the range and never resolves the count;
///  - an `Each` is the sequence of its lowered elements, and the elements are
///    stated here INDEPENDENTLY of production's lowering: the body once per
///    element of the collection. At the toy, substitution writes the element
///    over the placeholder in expressions only and leaves the action's shape
///    untouched (`ToyDomain.substitute`), so every lowered element prices as
///    the body does. Building the elements with the witness's own `lowered`
///    would be handing the oracle production's answer to the one question the
///    `Each` arm asks; stating the toy's lowering here means a toy whose
///    substitution ever changed an action's shape shows as a divergence.
let rec private modelCascade (a: ToyAction) : ProvedBudget.act =
    match toyWitness.Dispatch.Action.View a with
    | ActionView.Sequence members -> ProvedBudget.AChain(members |> List.map modelCascade)
    | ActionView.Choose(_, whenTrue, whenFalse, _) ->
        ProvedBudget.AChoose(modelCascade whenTrue, modelCascade whenFalse)
    | ActionView.Repeat(Bound.Literal count, body) ->
        ProvedBudget.ARepeat(ProvedBudget.BLiteral(bigint count), modelCascade body)
    | ActionView.Repeat(Bound.Parameter(_, lo, hi), body) ->
        ProvedBudget.ARepeat(ProvedBudget.BParameter(bigint lo, bigint hi), modelCascade body)
    | ActionView.Each(collection, _, body) ->
        ProvedBudget.AEach(List.replicate (List.length collection) (modelCascade body))
    | ActionView.Assign _
    | ActionView.Call _
    | ActionView.Require _
    | ActionView.Leaf _ -> ProvedBudget.ALeaf

/// The cost function under comparison for the cascade. Production is one; the
/// go-red case below is another, and it is deliberately wrong.
type private CascadeCost = ToyAction -> int

let private productionCascade: CascadeCost =
    ShippedBudget.actionCascadeCost toyWitness

let private oracleCascade (a: ToyAction) : int =
    int (ProvedBudget.action_cascade_cost satBound (modelCascade a))

let private cascadeDivergence (cost: CascadeCost) (name: string) (action: ToyAction) : string option =
    let prod = cost action
    let oracle = oracleCascade action

    if prod <> oracle then
        Some(sprintf "%s\n  production: %d\n  oracle:     %d" name prod oracle)
    else
        None

let private ring =
    Ring("/x", false, fun () -> failwith "a carried closure was invoked")

let rec private nest (depth: int) : ToyAction =
    if depth <= 0 then
        Beep 1
    else
        Seq [ nest (depth - 1); Hush ]

/// Every non-flow shape, nested — the corpus the model could express before
/// fuaran#2018, kept as it was.
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

/// The three flow shapes, each with several trees and each nested inside the
/// others (fuaran#2018). The price the core computes is stated beside each —
/// a selection is one plus its dearer arm, a repeat is one plus its body times
/// its bound (a parameter bound at the top of its range), an iteration is its
/// body once per element — so the case says what the number IS, and the
/// oracle's agreement is with a figure a reader can check by hand rather than
/// with whatever production answered.
let private flowCases: (string * ToyAction * int) list =
    [ // Choose: one plus the dearer arm; the condition and the exit cost nothing.
      "a branch between a leaf and a pair", Pick(Const(JBool true), Beep 1, Seq [ Beep 1; Beep 2 ], None), 3
      "a branch with an exit, arms of equal price", Pick(Missing, Beep 1, Hush, Some(Read "k")), 2
      "a branch whose arms are empty", Pick(Const(JBool false), Seq [], Seq [], None), 1
      "a branch nesting a branch in each arm",
      Pick(Missing, Pick(Missing, Beep 1, Seq [ Beep 1; Beep 2 ], None), Pick(Missing, Hush, Hush, None), None),
      4
      // Repeat: one plus the body times the bound.
      "a literal repeat", Times(Bound.Literal 3, Beep 1), 4
      "a literal repeat of a pair", Times(Bound.Literal 5, Seq [ Beep 1; Hush ]), 11
      "a repeat of nothing", Times(Bound.Literal 0, Beep 1), 1
      "a negative literal repeat, priced as nothing but its step", Times(Bound.Literal -4, Seq [ Beep 1; Beep 2 ]), 1
      "a parameter repeat, priced at the top of its range", Times(Bound.Parameter(Read "k", 0, 5), Beep 1), 6
      "a parameter repeat whose range is a point", Times(Bound.Parameter(Missing, 2, 2), Seq [ Beep 1; Hush ]), 5
      "a repeat nesting a repeat", Times(Bound.Literal 2, Times(Bound.Literal 3, Beep 1)), 9
      // Each: the body once per element, no step for a bound.
      "an iteration over two elements", ForEach([ JInt 1; JInt 2 ], "x", Seq [ Beep 1; Hush ]), 4
      "an iteration over nothing", ForEach([], "x", Beep 1), 0
      "an iteration whose body reads the element",
      ForEach([ JStr "a"; JStr "b"; JStr "c" ], "x", Put("last", None, Some(Hole "x"))),
      3
      "an iteration nesting an iteration",
      ForEach([ JInt 1; JInt 2 ], "x", ForEach([ JInt 3; JInt 4; JInt 5 ], "y", Beep 1)),
      6
      // Every shape inside every other.
      "a sequence carrying a branch", Seq [ Hush; Pick(Missing, Beep 1, Times(Bound.Literal 2, Beep 1), None) ], 5
      "a branch whose arms are a repeat and an iteration",
      Pick(Missing, Times(Bound.Literal 4, Beep 1), ForEach([ JInt 1; JInt 2; JInt 3 ], "x", Beep 1), None),
      6
      "a repeat of a branch over an iteration",
      Times(Bound.Parameter(Read "n", 1, 3), Pick(Missing, ForEach([ JInt 1; JInt 2 ], "x", Beep 1), Hush, None)),
      10
      "an iteration whose body is a repeat of a branch",
      ForEach([ JInt 1; JInt 2 ], "x", Times(Bound.Literal 2, Pick(Hole "x", Seq [ Beep 1; Beep 2 ], Beep 3, None))),
      14 ]

/// Trees whose cascade price reaches the saturation bound — where the model's
/// saturating adds and multiplies are priced, which the pre-2018 cascade (a
/// chain of leaves) could never reach.
let private saturatingCascades: (string * ToyAction) list =
    [ "a repeat whose multiply saturates", Times(Bound.Literal 2_000_000_000, Seq [ Beep 1; Beep 2 ])
      "a repeat whose bound is the whole range", Times(Bound.Literal System.Int32.MaxValue, Beep 1)
      "a parameter repeat whose top saturates",
      Times(Bound.Parameter(Missing, 0, System.Int32.MaxValue), Seq [ Hush; Hush ])
      "nested repeats whose product saturates", Times(Bound.Literal 100_000, Times(Bound.Literal 100_000, Beep 1))
      "a branch between a saturated arm and a cheap one",
      Pick(Missing, Times(Bound.Literal System.Int32.MaxValue, Beep 1), Hush, None)
      "a sequence of two repeats whose sum saturates",
      Seq
          [ Times(Bound.Literal 1_500_000_000, Beep 1)
            Times(Bound.Literal 1_500_000_000, Beep 1) ]
      "an iteration over saturated bodies",
      ForEach([ JInt 1; JInt 2 ], "x", Times(Bound.Literal System.Int32.MaxValue, Beep 1)) ]

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

/// The proved cascade with ONE arm changed: a selection priced by its CHEAPER
/// arm. The sequence, the repeat, the iteration and the leaves are the
/// oracle's own, called by name. A gate pricing this way would admit a branch
/// whose dearer arm the run then takes — the defect `choose_covers_either_arm`
/// excludes.
let rec private cheaperArmCascade (a: ProvedBudget.act) : int =
    match a with
    | ProvedBudget.AChoose(whenTrue, whenFalse) ->
        int (ProvedBudget.sat_add satBound 1I (bigint (min (cheaperArmCascade whenTrue) (cheaperArmCascade whenFalse))))
    | other -> int (ProvedBudget.action_cascade_cost satBound other)

let private cheaperArmCost: CascadeCost =
    fun action -> cheaperArmCascade (modelCascade action)

/// A branch whose arms differ in price, so the cheaper-arm pricing answers a
/// different number.
let private unequalBranch: ToyAction =
    Pick(Missing, Beep 1, Seq [ Beep 1; Beep 2; Beep 3 ], None)

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

          test "the oracle agrees with production on the action cascade cost of every non-flow shape, nested" {
              Expect.isGreaterThanOrEqual (List.length cascadeCases) 11 "the cascade corpus is the one declared above"

              let divergences =
                  cascadeCases
                  |> List.choose (fun (name, action) -> cascadeDivergence productionCascade name action)

              match divergences with
              | [] -> ()
              | first :: _ -> failtestf "the extracted model and production disagree on a cascade cost:\n%s" first
          }

          test
              "the oracle agrees with production on every flow shape - Choose, Repeat and Each - nested, at the price stated" {
              // Each shape with several trees, and each nested inside the
              // others; the corpus is asserted to hold all three.
              Expect.isGreaterThanOrEqual (List.length flowCases) 19 "the flow corpus is the one declared above"

              let shapesOf (action: ToyAction) : Set<string> =
                  let rec go (a: ToyAction) : string list =
                      match toyWitness.Dispatch.Action.View a with
                      | ActionView.Sequence members -> members |> List.collect go
                      | ActionView.Choose(_, t, f, _) -> "Choose" :: go t @ go f
                      | ActionView.Repeat(_, body) -> "Repeat" :: go body
                      | ActionView.Each(_, _, body) -> "Each" :: go body
                      | _ -> []

                  go action |> Set.ofList

              for shape in [ "Choose"; "Repeat"; "Each" ] do
                  let carrying =
                      flowCases
                      |> List.filter (fun (_, action, _) -> Set.contains shape (shapesOf action))

                  Expect.isGreaterThanOrEqual
                      (List.length carrying)
                      2
                      (sprintf "the flow corpus carries at least two trees with a %s" shape)

              Expect.isTrue
                  (flowCases
                   |> List.exists (fun (_, action, _) -> shapesOf action = Set.ofList [ "Choose"; "Repeat"; "Each" ]))
                  "the flow corpus nests all three shapes in one tree"

              // The number production answers is the one stated beside the
              // case, so the agreement below is with a figure checked by
              // hand rather than with whatever production said.
              for name, action, stated in flowCases do
                  Expect.equal (productionCascade action) stated (sprintf "%s is priced by the core at %d" name stated)

              let divergences =
                  flowCases
                  |> List.choose (fun (name, action, _) -> cascadeDivergence productionCascade name action)

              match divergences with
              | [] -> ()
              | first :: rest ->
                  failtestf
                      "the extracted model and production disagree on %d of %d flow-shape cascade(s). First divergence:\n%s"
                      (List.length rest + 1)
                      flowCases.Length
                      first
          }

          test "the oracle agrees with production on cascades whose price saturates" {
              Expect.isGreaterThanOrEqual
                  (List.length saturatingCascades)
                  7
                  "the saturating corpus is the one declared above"

              // The corpus must actually reach the bound, or the saturating
              // arithmetic inside the cascade is never priced.
              for name, action in saturatingCascades do
                  Expect.equal
                      (productionCascade action)
                      System.Int32.MaxValue
                      (sprintf "%s reaches the saturation bound" name)

              let divergences =
                  saturatingCascades
                  |> List.choose (fun (name, action) -> cascadeDivergence productionCascade name action)

              match divergences with
              | [] -> ()
              | first :: _ -> failtestf "the extracted model and production disagree on a saturating cascade:\n%s" first
          }

          test "GO RED: a cascade pricing a selection by its cheaper arm is reported at the toy witness" {
              // The honest run first: production and the oracle agree on the
              // very branch the defect is committed against.
              Expect.isNone
                  (cascadeDivergence productionCascade "the unequal branch" unequalBranch)
                  "production disagrees with the oracle on the very branch the defect is committed against"

              // Then the defect, through the SAME comparison, so what is
              // shown is that the harness REPORTS it.
              Expect.isSome
                  (cascadeDivergence cheaperArmCost "the unequal branch" unequalBranch)
                  "the comparison harness did not report a cascade that prices a selection by its cheaper arm - a harness that cannot lose is not evidence"

              // And the divergence is the one that matters: the cheaper-arm
              // price is BELOW the dearer arm's own price, so a gate capped
              // between the two admits a branch whose run it cannot afford.
              let dearerArm =
                  match unequalBranch with
                  | Pick(_, _, whenFalse, _) -> productionCascade whenFalse
                  | _ -> failwith "the unequal branch is a Pick"

              Expect.isLessThan
                  (cheaperArmCost unequalBranch)
                  dearerArm
                  "the cheaper-arm price should be below the dearer arm's own price - that is the whole defect"
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
