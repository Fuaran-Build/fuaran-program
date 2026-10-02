namespace Fuaran.Program.Bounded

// ============================================================================
//  Resource bounds — the other half of "safe to run untrusted".
//
//  Placement-neutral, like the interpreter and the re-resolution pass. The
//  no-closures invariant prevents arbitrary *code*; it does not prevent
//  arbitrary *cost*. A generated tree can still drive an enormous `Chain` or be
//  pathologically large, and a host that bounds one placement but not the other
//  has not bounded anything — so both placements price the same way, with the
//  same functions, and a budget breach means the same thing on a server session
//  and in a browser.
//
//  Deterministic on purpose: the budget is step/size based, not wall-clock (no
//  `Stopwatch` / `Date.now`), so the same tree + event sequence bounds
//  identically at every placement and is unit-testable headlessly.
// ============================================================================

/// Per-interaction resource caps for host-executed generated apps. Bounded
/// language (the interpreter) ⇒ no arbitrary code; bounded cost (this) ⇒ no
/// arbitrary cost. Both are required before running untrusted generated apps on
/// shared infrastructure.
type InteractionBudget =
    {
        /// Max leaf-action count of one bounded-action cascade (a `Chain`
        /// flattens; each non-Chain action costs 1). Caps a pathological deep /
        /// wide `Chain`.
        MaxActions: int
        /// Max render COST of the driven tree — the per-interaction
        /// re-resolve + render cost (and a proxy for the memory ceiling, since
        /// the resolved tree and any op list scale with it).
        ///
        /// Cost is the node count with data-bearing nodes weighted by the data
        /// they carry: a `Chart` costs one per (point × series), a `DataGrid`
        /// one per (row × column). For a tree with no data-bearing node this is
        /// exactly the node count.
        MaxNodes: int
    }

module InteractionBudget =
    /// No caps — the single-tenant / trusted-author case (the generated app is
    /// your own). `MaxActions` / `MaxNodes` at `Int32.MaxValue`.
    let unlimited: InteractionBudget =
        { MaxActions = System.Int32.MaxValue
          MaxNodes = System.Int32.MaxValue }

    /// Conservative defaults for the multi-tenant "platform runs your app" case.
    let defaults: InteractionBudget = { MaxActions = 64; MaxNodes = 10_000 }

module Budget =

    /// Saturating `int` arithmetic — a cost is a budget comparand, and an
    /// overflow that wrapped NEGATIVE would read as "cheap" and admit the very
    /// tree the budget exists to refuse. Public so a witness's `Walk.Cost`
    /// composes its weights with the same arithmetic.
    let satAdd (a: int) (b: int) : int =
        let sum = int64 a + int64 b

        if sum > int64 System.Int32.MaxValue then
            System.Int32.MaxValue
        else
            int sum

    let satMul (a: int) (b: int) : int =
        let product = int64 a * int64 b

        if product > int64 System.Int32.MaxValue then
            System.Int32.MaxValue
        else
            int product

    /// The step count of one bounded-action cascade — the price the budget
    /// compares against `MaxActions` BEFORE the run. A `Sequence` sums its
    /// members; a `Choose` is one step (its condition) plus the MORE EXPENSIVE
    /// arm, because the price must bound the run whichever arm it takes; a
    /// `Repeat` is one step (its bound) plus its body times the bound, a
    /// parameter bound priced at the TOP of its range so the price needs no
    /// store; an `Each` is its LOWERED form — the body's cost once per element
    /// of its literal collection, each element substituted, summed, with no
    /// step for a bound because a literal collection is not read (Phase
    /// 1990) — so an `Each` whose lowered size exceeds the ceiling is refused
    /// by the driver's gate before its first element, exactly as an
    /// over-bound repeat is; every other shape costs 1. Read through the
    /// witness's view, so what counts as composition is the fold's own notion
    /// of it, and in saturating arithmetic. `fold_steps_within_cost` in
    /// `proofs/BoundedFold.fst` is the statement that a run never takes more
    /// steps than this prices (Phase 1976; over the lowered elements, Phase
    /// 1990).
    ///
    /// Reads the DISPATCH axis (the action view) and nothing else.
    let actionCascadeCost
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (a: 'Action)
        : int =
        let fold = DispatchPosition.fold witness.Dispatch

        let rec cost (a: 'Action) : int =
            match fold.Action.View a with
            | ActionView.Sequence xs -> xs |> List.fold (fun acc x -> satAdd acc (cost x)) 0
            | ActionView.Choose(_, whenTrue, whenFalse, _) -> satAdd 1 (max (cost whenTrue) (cost whenFalse))
            | ActionView.Repeat(Bound.Literal count, body) -> satAdd 1 (satMul (max count 0) (cost body))
            | ActionView.Repeat(Bound.Parameter(_, _, hi), body) -> satAdd 1 (satMul (max hi 0) (cost body))
            | ActionView.Each(collection, placeholder, body) ->
                ActionWitness.lowered fold.Action collection placeholder body
                |> List.fold (fun acc x -> satAdd acc (cost x)) 0
            | ActionView.Assign _
            | ActionView.Call _
            | ActionView.Require _
            | ActionView.Leaf _ -> 1

        cost a

    // ─── Per-node render cost ────────────────────────────────────────────────
    //
    // Most nodes cost 1: one node's worth of re-resolve and render. A
    // DATA-BEARING node is different — its render cost scales with data it
    // carries INSIDE one node, which a node count cannot see. A chart is a
    // single node whose render emits geometry per (point × series); a grid is
    // a single node whose render emits a cell per (row × column). Counting
    // those as 1 is what lets a single node carry unbounded work behind a
    // bounded-looking tree, and weighting them is what stops the shape
    // reappearing on the next data-bearing kind.
    //
    // Which kinds are data-bearing, and by what they are weighted, is the
    // domain's: the walk axis's `Cost` answers a node's OWN data cost, and
    // the walk below adds the node itself. The arithmetic and the walk are
    // this module's, and they are what proofs/Budget.fst proves.

    /// Ceiling on rows counted for cost. Reading a possibly-lazy payload to
    /// exhaustion just to price it would itself be the unbounded work; a count
    /// this far past any sane budget is already "refuse", so the exact figure
    /// past it carries no decision. Public so a witness pricing its own
    /// payloads applies the same cap.
    [<Literal>]
    let maxCountedRows = 100_000

    /// The render cost of ONE node, excluding its children: the node itself,
    /// plus whatever data it carries.
    let private nodeCost (walk: WalkWitness<'Node>) (node: 'Node) : int = satAdd 1 (walk.Cost node)

    /// The tree's total render cost — the node count, with data-bearing nodes
    /// weighted by the data they carry. For every non-data-bearing tree this is
    /// exactly the node count. Walked over the STRUCTURAL child surface
    /// (the WALK axis's `Nodes`), in the order it enumerates. Reads the walk
    /// axis and nothing else.
    ///
    /// ITERATIVE, with an explicit stack, and it STOPS as soon as the cost
    /// passes `ceiling`. Both properties matter:
    ///
    ///  - Recursive, it would be bounded by the caller's stack rather than by
    ///    the budget, so the function whose whole job is to bound a tree's cost
    ///    would itself be the thing an over-deep tree could kill.
    ///  - Unconditional, it would walk the ENTIRE tree before anyone compared
    ///    the result against `MaxNodes` — charging the budget only after the
    ///    work it exists to refuse had been done. The ceiling makes the check
    ///    happen DURING the walk: a tree ten thousand times over budget costs
    ///    the same as one marginally over.
    ///
    /// The returned value is exact when it is at or below `ceiling`, and is
    /// "greater than `ceiling`" otherwise — all a budget comparand needs, since
    /// every use of it past that point is a refusal.
    let treeCost
        (witness: ProgramWitness<'Node, 'Op, WalkWitness<'Node>, 'Dispatch>)
        (ceiling: int)
        (node: 'Node)
        : int =
        let pending = System.Collections.Generic.Stack<'Node>()
        pending.Push node
        let mutable total = 0

        while pending.Count > 0 && total <= ceiling do
            let current = pending.Pop()
            total <- satAdd total (nodeCost witness.Walk current)

            for kid in witness.Walk.Nodes.Children current do
                pending.Push kid

        total
