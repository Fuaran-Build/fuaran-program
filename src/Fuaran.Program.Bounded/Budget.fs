namespace Fuaran.Program.Bounded

open Fuaran.UI.Types
open Fuaran.UI.Ops.Introspect

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

    /// The leaf-action count of one bounded-action cascade. A `Chain` flattens;
    /// every other arm costs 1.
    let rec actionCascadeCost (a: Action<obj>) : int =
        match a with
        | Action.Chain xs -> xs |> List.sumBy actionCascadeCost
        | _ -> 1

    // ─── Per-kind render cost ────────────────────────────────────────────────
    //
    // Most nodes cost 1: one node's worth of re-resolve and render. A
    // DATA-BEARING node is different — its render cost scales with data it
    // carries INSIDE one node, which a node count cannot see. A `Chart` is a
    // single node whose render emits geometry per (point × series); a
    // `DataGrid` is a single node whose render emits a cell per (row × column).
    // Counting those as 1 is what lets a single node carry unbounded work
    // behind a bounded-looking tree, and weighting them is what stops the shape
    // reappearing on the next data-bearing kind: a new kind that carries its
    // own data joins this function, and the existing `MaxNodes` budget then
    // sees it with no host-side change.
    //
    // Only a `Binding.Static` payload is counted. A `Query` / `State` /
    // `Transform` binding resolves at render time from the host's own store, so
    // its size is not a property of the untrusted tree and is not this budget's
    // business.

    /// Ceiling on rows counted for cost. Reading a possibly-lazy payload to
    /// exhaustion just to price it would itself be the unbounded work; a count
    /// this far past any sane budget is already "refuse", so the exact figure
    /// past it carries no decision.
    [<Literal>]
    let private maxCountedRows = 100_000

    /// Saturating `int` arithmetic — a cost is a budget comparand, and an
    /// overflow that wrapped NEGATIVE would read as "cheap" and admit the very
    /// tree the budget exists to refuse.
    let private satAdd (a: int) (b: int) : int =
        let sum = int64 a + int64 b

        if sum > int64 System.Int32.MaxValue then
            System.Int32.MaxValue
        else
            int sum

    let private satMul (a: int) (b: int) : int =
        let product = int64 a * int64 b

        if product > int64 System.Int32.MaxValue then
            System.Int32.MaxValue
        else
            int product

    let private staticSeqCount (binding: Binding<'t seq>) : int =
        match binding with
        | Binding.Static(Some items) -> items |> Seq.truncate maxCountedRows |> Seq.length
        | _ -> 0

    let private staticListCount (binding: Binding<'t list>) : int =
        match binding with
        | Binding.Static(Some items) -> min maxCountedRows (List.length items)
        | _ -> 0

    /// The render cost of ONE node, excluding its children.
    let private nodeCost (node: Node<'a>) : int =
        match node.Kind with
        | NodeKind.Chart spec -> satAdd 1 (satMul (staticSeqCount spec.Source) (max 1 (List.length spec.YFields)))
        | NodeKind.DataGrid spec -> satAdd 1 (satMul (staticSeqCount spec.Source) (max 1 (List.length spec.Columns)))
        | NodeKind.Map spec -> satAdd 1 (staticListCount spec.Source)
        | NodeKind.Sparkline spec -> satAdd 1 (staticListCount spec.Source)
        | _ -> 1

    /// The tree's total render cost — the node count, with data-bearing nodes
    /// weighted by the data they carry. For every non-data-bearing tree this is
    /// exactly the node count.
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
    let treeCost (ceiling: int) (node: Node<'a>) : int =
        let pending = System.Collections.Generic.Stack<Node<'a>>()
        pending.Push node
        let mutable total = 0

        while pending.Count > 0 && total <= ceiling do
            let current = pending.Pop()
            total <- satAdd total (nodeCost current)

            match getChildren current.Kind with
            | Some kids ->
                for kid in kids do
                    pending.Push kid
            | None -> ()

        total
