namespace Fuaran.Program.Server

open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open Fuaran.UI.OpStream.Replay
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded

// ============================================================================
//  The SERVER-LOGIC placement of the bounded program loop — the vertical's
//  missing middle, as a spike.
//
//  Two placements already exist and share one interpreter: a server session
//  that diffs and lowers to patches, and a browser client that renders in
//  process. Both run the tree's own interactions. Neither can run SERVER LOGIC:
//  a generated tree that wants to read data, mutate durable state and respond
//  has, at both, exactly one option — an `Action.Call` that is a documented
//  no-op.
//
//  This placement is that arm implemented. The loop is the same one:
//
//    inbound event
//      → G1 validate            (the SAME `Validation.validate`)
//      → G2 budget              (the SAME `Budget` functions)
//      → interpret              (the SAME `BoundedActions.runBoundedAction`)
//      → effects                (this placement's closed vocabulary, gated)
//      → re-resolve             (the SAME `Resolve.resolveTree`)
//      → respond
//
//  What is new is the fourth line, and only the fourth line. Everything else is
//  imported. That is the point of the spike rather than an economy: the claim
//  the whole vertical rests on is "one algebra, two placements", and a third
//  placement that had to reimplement the algebra to reach the server would have
//  disproved it.
//
//  ── Where the handler joins ─────────────────────────────────────────────────
//  At the `Action.Call` arm, which the shared fold documents as inert. When the
//  endpoint names a registered handler, the handler runs; otherwise the
//  behaviour is the shared fold's, unchanged. So a tree that names no handler
//  behaves at this placement EXACTLY as it does at the other two — a property
//  the tier-parity family asserts over the whole fixture corpus rather than
//  taking on trust.
//
//  The recognition is TOP-LEVEL only: an `Action.Call` nested inside a `Chain`
//  stays the shared fold's no-op. Reaching into the action tree to find nested
//  calls would mean matching on `Action` a second time, which is the thing D1
//  forbids; doing it properly means the fold itself gaining a handler-effect
//  arm, which is a change to the shared algebra and therefore not a spike's
//  decision to make. Recorded in `docs/server-handler-atomicity.md`.
//
//  ── No wire commitment ──────────────────────────────────────────────────────
//  A handler is host-registered data. The wire carries the endpoint NAME and
//  nothing else, so nothing here fixes a serialised form for a handler, a stage
//  or a server effect. That cut comes later, informed by this spike and by the
//  demand census — not by whatever shapes happened to be convenient here.
// ============================================================================

/// The host-coupled seams the server-logic loop delegates to. The same posture
/// as the other placements: this core takes no transport, renderer, store or
/// data-access dependency; each arrives as an injected value.
type ServerServices =
    {
        /// G1 check (d): the dispatch policy gate.
        CanDispatch: Action<obj> -> bool
        /// The closed handler registry, by the endpoint an `Action.Call` names.
        /// A tree can reach only what is in here.
        Handlers: Map<string, Handler>
        /// The effect gate + host-function performers.
        Effects: ServerEffectRegistry
        /// Resolves a named data source for `ServerEffect.RunQuery`. Defaults to
        /// refusing every name, so a host that wires no data serves no query.
        Sources: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>
        /// The ops applied this step, for the journal / telemetry sink — the
        /// same seam, by the same name, as the other placements.
        OnApply: TreeOp<obj> list -> unit
        /// G2 — per-interaction resource caps, shared with both other
        /// placements so a breach means the same thing everywhere.
        Budget: InteractionBudget
    }

module ServerServices =

    /// Default services — **DENY all dispatch**, no handlers, no effects, no
    /// data sources, no sink, default budget.
    ///
    /// Every one of those defaults is closed, and for one reason: this loop
    /// exists to run emitted trees against durable state, which is the most
    /// consequential thing anything in this domain does. `createPermissive` is
    /// the named opt-in, and it opens the gates — it does not conjure handlers,
    /// performers or sources, because those are host acts.
    let create: ServerServices =
        { CanDispatch = fun _ -> false
          Handlers = Map.empty
          Effects = ServerEffectRegistry.denyAll
          Sources = Fuaran.Core.DataFrame.noResolve
          OnApply = ignore
          Budget = InteractionBudget.defaults }

    /// **The named opt-in back to allow-everything gates** — both of them, the
    /// dispatch gate and the effect gate. Still no handlers and no performers.
    let createPermissive: ServerServices =
        { create with
            CanDispatch = fun _ -> true
            Effects = ServerEffectRegistry.permissive ServerEffectRegistry.denyAll }

    /// Register a handler under the endpoint a tree's `Action.Call` will name.
    let withHandler (endpoint: string) (handler: Handler) (services: ServerServices) : ServerServices =
        { services with
            Handlers = Map.add endpoint handler services.Handlers }

/// Why the server placement produced no change: a G1 gate rejection or a G2
/// budget breach — the same two refusal classes the other placements report, by
/// the same names.
type ServerReject =
    | Gate of Validation.RejectReason
    | BudgetExceeded of detail: string

/// One connection's server-logic state: the domain tree (which a handler's
/// `ApplyOps` may edit — unlike the other placements, where the base tree is
/// fixed), the binding store, the current resolved tree, the cached render cost
/// and the injected services.
type ServerSession =
    { BaseTree: Node<obj>
      Store: BoundedStore
      Resolved: Node<obj>
      NodeCount: int
      Services: ServerServices }

/// The observable result of stepping a server session with one inbound event.
type ServerStepOutput =
    {
        /// The resolved tree after this step (unchanged on a refusal).
        Resolved: Node<obj>
        /// The re-resolve diff — what the host lowers, ships or journals. The
        /// response.
        Ops: TreeOp<obj> list
        /// Ops a handler explicitly asked to be pushed to the client, distinct
        /// from anything durable.
        Patches: TreeOp<obj> list
        /// Host-channel messages a handler asked for.
        Notifications: (string * Fuaran.Core.JVal) list
        /// The capabilities a handler performed, in order.
        Performed: string list
        /// Closure-free client effects, from the shared interpreter.
        ClientEffects: ClientEffect list
        /// `false` when a handler halted and rolled back. Always `true` on the
        /// shared-fold path, which has nothing to roll back.
        Committed: bool
        Rejected: ServerReject option
        Diagnostics: ServerDiagnostic list
    }

module ServerSession =

    /// Build a session from a decoded `WireTree` + its initial store. Budget
    /// ordering matches the other placements: price first, resolve only within
    /// budget, and return an over-budget session unresolved rather than refusing
    /// to construct one.
    let init (services: ServerServices) (store: BoundedStore) (wire: WireTree) : ServerSession =
        let tree = WireTree.reify wire
        let budget = services.Budget.MaxNodes
        let cost = Budget.treeCost budget tree

        { BaseTree = tree
          Store = store
          Resolved =
            (if cost > budget then
                 tree
             else
                 Resolve.resolveTree store tree)
          NodeCount = cost
          Services = services }

    let private inert (session: ServerSession) : ServerStepOutput =
        { Resolved = session.Resolved
          Ops = []
          Patches = []
          Notifications = []
          Performed = []
          ClientEffects = []
          Committed = true
          Rejected = None
          Diagnostics = [] }

    let private rejected (session: ServerSession) (reject: ServerReject) : ServerStepOutput =
        { inert session with
            Rejected = Some reject }

    /// Commit a completed interpretation: re-resolve the (possibly edited) base
    /// tree, diff against the previous resolved tree, hand the ops to the sink,
    /// and report.
    ///
    /// The cost is re-priced because `ApplyOps` can change the tree, and the
    /// over-budget case is handled exactly as `init` handles it — the session is
    /// carried unresolved and the NEXT event is refused, rather than a mutation
    /// that already succeeded being un-done by a budget check that ran too late.
    let private commit
        (session: ServerSession)
        (tree: Node<obj>)
        (store: BoundedStore)
        (outcome: HandlerOutcome)
        : ServerSession * ServerStepOutput =
        let budget = session.Services.Budget.MaxNodes

        let cost =
            if LanguagePrimitives.PhysicalEquality tree session.BaseTree then
                session.NodeCount
            else
                Budget.treeCost budget tree

        let resolved =
            if cost > budget then
                tree
            else
                Resolve.resolveTree store tree

        let ops = TreeOpDiff.diff session.Resolved resolved
        session.Services.OnApply ops

        { session with
            BaseTree = tree
            Store = store
            Resolved = resolved
            NodeCount = cost },
        { Resolved = resolved
          Ops = ops
          Patches = outcome.Patches
          Notifications = outcome.Notifications
          Performed = outcome.Performed
          ClientEffects = outcome.ClientEffects
          Committed = outcome.Committed
          Rejected = None
          Diagnostics = outcome.Diagnostics }

    /// Step the session with one untrusted inbound event.
    ///
    /// On a G1 rejection or a G2 budget breach the session is returned UNCHANGED
    /// with `Rejected = Some _` — default-deny by shape, no hang, no partial
    /// state, exactly as at the other placements.
    let step (session: ServerSession) (ev: LiveEvent) : ServerSession * ServerStepOutput =
        match Validation.validate session.Services.CanDispatch session.Resolved ev with
        | Error reason -> session, rejected session (Gate reason)
        | Ok { Action = None } -> session, inert session
        | Ok { Action = Some action } ->
            let budget = session.Services.Budget
            let cost = Budget.actionCascadeCost action

            if cost > budget.MaxActions then
                session,
                rejected
                    session
                    (BudgetExceeded(sprintf "action cascade cost %d exceeds MaxActions %d" cost budget.MaxActions))
            elif session.NodeCount > budget.MaxNodes then
                session,
                rejected
                    session
                    (BudgetExceeded(sprintf "tree cost %d exceeds MaxNodes %d" session.NodeCount budget.MaxNodes))
            else
                match action with
                // The handler arm. Note what is NOT here: no second match on the
                // action's structure, no recursion into a `Chain`. One endpoint
                // lookup, then either a handler or the shared fold.
                | Action.Call(endpoint, _, _) ->
                    match Map.tryFind endpoint session.Services.Handlers with
                    | None ->
                        // Inert, and diagnosed — the same treatment the shared
                        // fold gives an action with no form, so a tree naming an
                        // absent handler is observable rather than a silent dead
                        // end. The endpoint itself is not repeated back.
                        session,
                        { inert session with
                            Diagnostics = [ ServerDiagnostic.HandlerUnregistered ] }
                    | Some handler ->
                        let outcome =
                            Handler.run
                                session.Services.Effects
                                session.Services.Sources
                                ev.NodeId
                                handler
                                { Tree = session.BaseTree
                                  Bindings = session.Store }

                        commit session outcome.Store.Tree outcome.Store.Bindings outcome

                // Everything else is the shared fold, byte for byte what the
                // other placements do with the same action and the same store.
                | _ ->
                    let bounded = BoundedActions.runBoundedAction ev.NodeId action session.Store

                    let outcome =
                        { Store =
                            { Tree = session.BaseTree
                              Bindings = bounded.Store }
                          Committed = true
                          Performed = []
                          Patches = []
                          Notifications = []
                          ClientEffects = bounded.Effects
                          Diagnostics = bounded.Diagnostics |> List.map ServerDiagnostic.Bounded }

                    commit session session.BaseTree bounded.Store outcome
