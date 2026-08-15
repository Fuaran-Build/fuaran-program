module Fuaran.Program.Bounded.BoundedDriver

open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open Fuaran.UI.Ops.Introspect
open Fuaran.UI.OpStream.Replay
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.Renderer
open Fuaran.UI.Renderer.BindingResolver

// ============================================================================
//  The no-`'Msg` server placement of the bounded program loop.
//
//  The UI tier's hand-authored driver runs a HAND-AUTHORED Elmish
//  `(Model, update, view)` loop on the server. This driver runs the *generated*-
//  app case: there is no hand-authored `update` / `'Msg` / `view` — only an
//  **emitted, wire-decoded `Node<obj>` tree** and its **state store**
//  (`BindingResolver.BindingSources`). The loop:
//
//    inbound LiveEvent
//      → G1 validate (`Validation.validate` — node exists / event legit /
//        payload in-bounds / action policy-gated)
//      → interpret the resolved bounded `Action` against the store
//        (`BoundedActions.runBoundedAction` — SetState mutates, the rest are
//        closure-free effects / no-ops; NO closure is ever invoked)
//      → re-resolve the FIXED base tree's bindings against the new store
//        (`Resolve.resolveTree`)
//      → diff old-resolved → new-resolved (`TreeOpDiff.diff`)
//      → lower to DomPatches (`Lowering.lower`) + ship the client effects.
//
//  The interpreter and the re-resolution pass are placement-neutral (they sit
//  beside this file); only the transport half — validate, diff, lower, budget —
//  is this placement's. The browser placement of the same algebra is
//  `Fuaran.Program.Runtime`.
//
//  ── G2 — resource bounds (the other half of "safe on shared infra") ─────────
//  The no-closures invariant (BoundedActions) prevents arbitrary *code*; it does
//  not prevent arbitrary *cost*. A generated tree can still drive an enormous
//  `Chain` or be pathologically large. `InteractionBudget` caps both per
//  interaction — the bounded-action cascade size (`MaxActions`) and the
//  re-resolve+diff tree size (`MaxNodes`, the memory/work proxy) — and a breach
//  surfaces a structured `BudgetExceeded` (NOT a hang, NOT a state mutation).
//  Bounded code (BoundedActions) + bounded cost (here) = safe to run untrusted
//  generated apps on shared / multi-tenant infrastructure.
//
//  Fable-clean + deterministic on purpose: the budget is step/size based, not
//  wall-clock (no `Stopwatch` / `Date.now`), so the same tree + event sequence
//  bounds identically at every placement and is unit-testable headlessly.
// ============================================================================

// ─── G2 — per-interaction resource budget ────────────────────────────────────

/// Per-interaction resource caps for host-executed generated apps. Bounded
/// language (BoundedActions) ⇒ no arbitrary code; bounded cost (this) ⇒ no
/// arbitrary cost. Both are required before running untrusted generated apps on
/// shared infrastructure.
type InteractionBudget =
    {
        /// Max leaf-action count of one bounded-action cascade (a `Chain`
        /// flattens; each non-Chain action costs 1). Caps a pathological deep /
        /// wide `Chain`.
        MaxActions: int
        /// Max render COST of the driven tree — the per-interaction
        /// re-resolve + diff cost (and a proxy for the memory ceiling, since the
        /// resolved tree + op list scale with it).
        ///
        /// Cost is the node count with data-bearing nodes weighted by the data
        /// they carry: a `Chart` costs one per (point × series), a `DataGrid`
        /// one per (row × column). For a tree with no data-bearing node this is
        /// exactly the node count, which is what it was before.
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

let rec private actionCost (a: Action<obj>) : int =
    match a with
    | Action.Chain xs -> xs |> List.sumBy actionCost
    | _ -> 1

// ─── Per-kind render cost ────────────────────────────────────────────────────
//
// Most nodes cost 1: one node's worth of re-resolve, diff and render. A
// DATA-BEARING node is different — its render cost scales with data it carries
// INSIDE one node, which a node count cannot see. A `Chart` is a single node
// whose lowering emits geometry per (point × series); a `DataGrid` is a single
// node whose render emits a cell per (row × column). Counting those as 1 is what
// let a single node carry unbounded work behind a bounded-looking tree, and
// weighting them is what stops the shape reappearing on the next data-bearing
// kind: a new kind that carries its own data joins this function, and the
// existing `MaxNodes` budget then sees it with no host-side change.
//
// Only a `Binding.Static` payload is counted. A `Query` / `State` / `Transform`
// binding resolves at render time from the host's own store, so its size is not
// a property of the untrusted tree and is not this budget's business.

/// Ceiling on rows counted for cost. Reading a possibly-lazy payload to
/// exhaustion just to price it would itself be the unbounded work; a count this
/// far past any sane budget is already "refuse", so the exact figure past it
/// carries no decision.
[<Literal>]
let private maxCountedRows = 100_000

/// Saturating `int` arithmetic — a cost is a budget comparand, and an overflow
/// that wrapped NEGATIVE would read as "cheap" and admit the very tree the
/// budget exists to refuse.
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
/// weighted by the data they carry. Named `countNodes` no longer: a cost is what
/// `MaxNodes` has always been comparing, and for every non-data-bearing tree
/// this is exactly the node count it was before.
///
/// ITERATIVE, with an explicit stack, and it STOPS as soon as the cost passes
/// `ceiling`. Both properties matter and neither was true originally:
///
///  - Recursive, it was bounded by the caller's stack rather than by the budget,
///    so the function whose whole job is to bound a tree's cost was itself the
///    thing an over-deep tree could kill.
///  - Unconditional, it walked the ENTIRE tree before anyone compared the result
///    against `MaxNodes` — so the budget was charged after the work it exists to
///    refuse had already been done. The ceiling makes the check happen DURING
///    the walk: a tree ten thousand times over budget costs the same as one
///    marginally over.
///
/// The returned value is exact when it is at or below `ceiling`, and is
/// "greater than `ceiling`" otherwise — which is all a budget comparand needs,
/// since every use of it past that point is a refusal.
let private treeCostTo (ceiling: int) (node: Node<'a>) : int =
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

// ─── the driver ──────────────────────────────────────────────────────────────

/// The host-coupled seams the bounded driver delegates to (the portability
/// posture: this core takes no renderer / transport / sink dependency).
type BoundedServices =
    {
        /// G1 check (d): the dispatch policy gate (host maps `Action` → renderer
        /// `ActionDescriptor` → `IFuaranRuntime.CanDispatch`).
        CanDispatch: Action<obj> -> bool
        /// Render a node's HTML fragment (host wires its server renderer).
        /// The nodes handed here are already resolved (`Binding.Static`), so the
        /// host renderer needs no live binding sources to produce correct HTML.
        RenderFragment: Node<obj> -> string
        /// The ops applied this step, for the journal / telemetry sink.
        OnApply: TreeOp<obj> list -> unit
        /// G2 — per-interaction resource caps.
        Budget: InteractionBudget
    }

module BoundedServices =
    /// Default services — **DENY all dispatch**, no op sink, default budget.
    /// `renderFragment` MUST be supplied (HTML production is host-owned).
    ///
    /// This driver exists specifically to run emitted, wire-decoded trees, so an
    /// allow-everything gate default is the least defensible option: the whole
    /// point of the bounded path is that the tree is untrusted.
    /// `createPermissive` is the named opt-in back to it.
    let create (renderFragment: Node<obj> -> string) : BoundedServices =
        { CanDispatch = fun _ -> false
          RenderFragment = renderFragment
          OnApply = ignore
          Budget = InteractionBudget.defaults }

    /// **The named opt-in back to an allow-everything gate.**
    let createPermissive (renderFragment: Node<obj> -> string) : BoundedServices =
        { create renderFragment with
            CanDispatch = fun _ -> true }

/// One connection's bounded live state: the FIXED decoded tree (`BaseTree`), the
/// mutable store, the current resolved tree (the diff baseline), the cached node
/// count (for G2), and the injected services.
type BoundedSession =
    {
        BaseTree: Node<obj>
        Store: BoundedStore
        Resolved: Node<obj>
        /// The tree's cached render COST — the node count with data-bearing
        /// nodes weighted by their payload. Field name kept for source
        /// compatibility; `MaxNodes` is what it is compared against.
        NodeCount: int
        Services: BoundedServices
    }

/// Why the bounded driver produced no patches: a G1 gate rejection or a G2
/// budget breach. Either way the store is unchanged.
type BoundedReject =
    | Gate of Validation.RejectReason
    | BudgetExceeded of detail: string

/// The outcome of stepping a bounded session with one inbound event.
/// `Diagnostics` carries the bounded interpreter's readable no-op signals
/// ("this action is inert on the generated-app path") — observability for
/// emission debugging, never behaviour.
type BoundedStepOutput =
    { Patches: DomPatch list
      Effects: ClientEffect list
      Rejected: BoundedReject option
      Diagnostics: BoundedDiagnostic list }

/// Build a bounded session from a decoded `WireTree` + its initial store. The
/// bounded driver is the *correct* consumer of a wire tree: it never invokes
/// the tree's (inert) closures — interactivity is re-derived from the store —
/// so `decodeNode json |> Result.map (BoundedDriver.init services store)` is
/// the safe end-to-end path with no `reify`. The initial resolved tree (the
/// first diff baseline) is `Resolve.resolveTree store tree`.
///
/// **Budget ORDERING.** The cost is priced FIRST, with the walk stopping the
/// moment it passes `MaxNodes`, and `resolveTree` runs only if the tree is
/// within budget. The naive ordering is the opposite: walk the whole tree to
/// price it, then resolve the whole tree, and compare against `MaxNodes` only at
/// the first `step` — so an over-budget tree is fully walked twice by the very
/// construction supposed to refuse it, and only then declared too expensive. An
/// over-budget session is still RETURNED rather than refused (the signature is
/// total and consumers depend on that), but it is returned unresolved, and
/// `step` rejects it with `BudgetExceeded` on the first event — so the
/// observable contract is unchanged and only the work is.
let init (services: BoundedServices) (store: BoundedStore) (wire: WireTree) : BoundedSession =
    let tree = WireTree.reify wire
    let budget = services.Budget.MaxNodes
    let cost = treeCostTo budget tree

    { BaseTree = tree
      Store = store
      Resolved =
        (if cost > budget then
             tree
         else
             Resolve.resolveTree store tree)
      NodeCount = cost
      Services = services }

let private rejected (r: BoundedReject) : BoundedStepOutput =
    { Patches = []
      Effects = []
      Rejected = Some r
      Diagnostics = [] }

/// Step the bounded session with one untrusted inbound event. Validates (G1),
/// budgets (G2), interprets the bounded action against the store, re-resolves +
/// diffs + lowers — returning the updated session + patch / effect output. On a
/// G1 rejection or a G2 budget breach the session is returned UNCHANGED with
/// `Rejected = Some _` and no patches (default-deny by shape; no hang).
///
/// G1 validates against the CURRENT RESOLVED tree so a `Select` whose options
/// resolved to `Binding.Static` gets a precise bounds check; the resolved tree's
/// `Action` handlers are untouched by resolution, so action resolution is
/// identical to validating against the base tree.
let step (session: BoundedSession) (ev: LiveEvent) : BoundedSession * BoundedStepOutput =
    match Validation.validate session.Services.CanDispatch session.Resolved ev with
    | Error reason -> session, rejected (Gate reason)
    | Ok { Action = None } ->
        // Legitimate but no resolvable action — no state change.
        session,
        { Patches = []
          Effects = []
          Rejected = None
          Diagnostics = [] }
    | Ok { Action = Some action } ->
        let budget = session.Services.Budget
        let cost = actionCost action

        if cost > budget.MaxActions then
            session,
            rejected (BudgetExceeded(sprintf "action cascade cost %d exceeds MaxActions %d" cost budget.MaxActions))
        elif session.NodeCount > budget.MaxNodes then
            session,
            rejected (BudgetExceeded(sprintf "tree cost %d exceeds MaxNodes %d" session.NodeCount budget.MaxNodes))
        else
            let outcome = BoundedActions.runBoundedAction ev.NodeId action session.Store
            let newResolved = Resolve.resolveTree outcome.Store session.BaseTree
            let ops = TreeOpDiff.diff session.Resolved newResolved
            session.Services.OnApply ops
            let patches = Lowering.lower session.Services.RenderFragment newResolved ops

            { session with
                Store = outcome.Store
                Resolved = newResolved },
            { Patches = patches
              Effects = outcome.Effects
              Rejected = None
              Diagnostics = outcome.Diagnostics }
