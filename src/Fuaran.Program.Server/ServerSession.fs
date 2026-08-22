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
//  At the shared fold's HANDLER-EFFECT ARM (DECISIONS.md D7), which this module
//  supplies as a `HandlerArm` and the fold consults wherever a call action
//  appears. When the endpoint names a registered handler, the handler runs;
//  otherwise the behaviour is the shared fold's, unchanged. So a tree that names
//  no handler behaves at this placement EXACTLY as it does at the other two — a
//  property the tier-parity family asserts over the whole fixture corpus rather
//  than taking on trust.
//
//  Recognition is UNIFORM, not top-level: a call nested inside a `Chain` reaches
//  the same arm as one sitting alone, in its place in the chain, seeing the
//  writes before it and seen by the writes after it. It reads that way because
//  the fold does the recognising — this module never matches on an `Action`,
//  which is what keeps D1's "no second evaluator" true while the arm exists at
//  all. The spike's top-level-only special case is retired.
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
        /// The SCHEMAS of those named sources, declared here beside the resolver
        /// that serves their rows. Read only by the pre-execution query-schema
        /// walk, never by the loop: a `Ref`'s rows are host-side by design, so
        /// its columns are whatever the host says they are, and an undeclared
        /// name degrades the walk to "unknown" rather than to a guess.
        SourceSchemas: SourceSchemas
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
          SourceSchemas = SourceSchemas.none
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

    /// Declare the schema a named `Ref` source resolves to.
    ///
    /// Separate from wiring the resolver, and deliberately: a host may serve
    /// rows it cannot describe statically (a foreign table, a projection built
    /// at request time), and forcing a schema alongside every resolver would
    /// make it invent one. Declaring is what buys the check; not declaring costs
    /// only the check, and says so on the report.
    let withSourceSchema (name: string) (schema: Fuaran.Core.Schema) (services: ServerServices) : ServerServices =
        { services with
            SourceSchemas = SourceSchemas.declare name schema services.SourceSchemas }

/// Why the server placement produced no change: a G1 gate rejection or a G2
/// budget breach — the same two refusal classes the other placements report, by
/// the same names.
type ServerReject =
    | Gate of Validation.RejectReason
    | BudgetExceeded of detail: string

/// Why a strict construction refused to build a session. Two vocabularies,
/// deliberately kept apart rather than merged into one finding type: one asks
/// whether this host can PERFORM what the tree can ask for, the other whether
/// the host's own queries can SATISFY what the tree reads. A host correcting the
/// first edits its registry; a host correcting the second edits a pipeline or a
/// grid, and collapsing them would make the list harder to act on, not shorter.
[<RequireQualifiedAccess>]
type ServerStrictFinding =
    /// The demanded-effect coverage check: the tree can ask for something this
    /// host does not offer or its policy refuses.
    | Coverage of CoverageFinding
    /// The pre-execution query-schema check: a pipeline cannot satisfy a reader,
    /// or cannot run at all against its own source.
    | QuerySchema of QuerySchemaFinding

module ServerStrictFinding =
    /// Human-readable, and detailed on the query side — see `QuerySchema.describe`
    /// for why that side can afford names where the runtime cannot.
    let describe (finding: ServerStrictFinding) : string =
        match finding with
        | ServerStrictFinding.Coverage f -> Demanded.describe f
        | ServerStrictFinding.QuerySchema f -> QuerySchema.describe f

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
        /// `false` when ANY handler this event reached halted and rolled back.
        /// Always `true` when the event reached no handler, since the shared
        /// fold has nothing to roll back.
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

    /// Every query the registered handlers declare, with the handler and slot it
    /// belongs to.
    ///
    /// Every REGISTERED handler, not only the ones this tree names. A handler
    /// whose pipeline cannot run against its own source is a defect in the
    /// host's registration, and it is one whether or not today's tree happens to
    /// call it — the tree decides which handlers RUN, never which are correct.
    let private declaredQueries
        (services: ServerServices)
        : (QueryOrigin * Fuaran.Core.DataSource * Fuaran.Core.Transform list) list =
        services.Handlers
        |> Map.toList
        |> List.collect (fun (_, handler) ->
            handler.Stages
            |> List.choose (fun stage ->
                match stage with
                | Effect(ServerEffect.RunQuery(slot, source, pipeline)) ->
                    Some({ Handler = handler.Name; Slot = slot }, source, pipeline)
                | _ -> None))

    /// What the static query-schema walk decided about this host's queries and
    /// this tree's readers — findings, plus the two kinds of thing it declined
    /// to decide.
    ///
    /// Exposed as DATA and not only as a refusal, for the reason
    /// `Demanded.ofTree` is: a host may want to see what could not be checked
    /// (an undeclared `Ref`, a pivot, a closure-projecting grid) without being
    /// stopped by it, and a host that reads the report and starts anyway is
    /// making a different, legitimate choice from one that never looked.
    let querySchemaReport (services: ServerServices) (wire: WireTree) : QuerySchemaReport =
        let readers = QuerySchema.readersOfTree (WireTree.reify wire)

        declaredQueries services
        |> List.map (fun (origin, source, pipeline) ->
            QuerySchema.checkQuery services.SourceSchemas origin source pipeline readers)
        |> QuerySchemaReport.concat

    /// Build a session ONLY if this host can cover what the tree can ask for AND
    /// its own queries can satisfy what the tree reads.
    ///
    /// **Opt-in, and `init` remains the default** — the same posture the bounded
    /// driver's strict construction takes, and for the same reason: a session
    /// whose tree names one uncoverable effect still works for everything else,
    /// and refusing it wholesale is a stricter policy than the interpreter's
    /// own. What is new here is the second half. The effect check asks what the
    /// tree can DEMAND; the schema check asks whether what the host will PRODUCE
    /// fits what the tree will READ — and that question had no answer before
    /// execution at all, only a thin runtime error after it.
    ///
    /// `Error` carries EVERY finding from both checks, never the first: a host
    /// correcting its registration wants the whole list, and stopping early
    /// makes that an iterative guessing game.
    let initStrict
        (coverage: HostCoverage)
        (services: ServerServices)
        (store: BoundedStore)
        (wire: WireTree)
        : Result<ServerSession, ServerStrictFinding list> =
        let tree = WireTree.reify wire

        let findings =
            (Demanded.check coverage tree |> List.map ServerStrictFinding.Coverage)
            @ ((querySchemaReport services wire).Findings
               |> List.map ServerStrictFinding.QuerySchema)

        match findings with
        | [] -> Ok(init services store wire)
        | _ -> Error findings

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

    /// This placement's answer to a call action the shared fold recognised
    /// (DECISIONS.md D7) — the whole of what makes the server-logic placement
    /// different from the other two, and the only thing it adds to the algebra.
    ///
    /// It always answers, never declines. Declining is what a placement with no
    /// registry does, and this one HAS a registry: an endpoint it does not hold
    /// is a fact about this host, reported as `HandlerUnregistered` rather than
    /// as the shared fold's "no form on this path", because those say different
    /// things to whoever is debugging an emission.
    let private handlerArm (services: ServerServices) : HandlerArm<HandlerTally> =
        { Answer =
            fun nodeId endpoint bindings tally ->
                match Map.tryFind endpoint services.Handlers with
                | None ->
                    // Inert, and diagnosed. The endpoint itself is not repeated
                    // back: it is the one string in this subsystem that comes
                    // off the wire.
                    Some
                        { Store = bindings
                          Effects = []
                          Diagnostics = []
                          Placement =
                            { tally with
                                Diagnostics = tally.Diagnostics @ [ ServerDiagnostic.HandlerUnregistered ] } }
                | Some handler ->
                    let outcome =
                        Handler.run
                            services.Effects
                            services.Sources
                            nodeId
                            handler
                            { Tree = tally.Tree
                              Bindings = bindings }

                    Some
                        { Store = outcome.Store.Bindings
                          Effects = outcome.ClientEffects
                          // The handler's diagnostics ride the tally rather than
                          // the fold's channel: they are `ServerDiagnostic`s,
                          // and a `Compute` stage's bounded ones are already
                          // wrapped among them in stage order.
                          Diagnostics = []
                          Placement =
                            { Tree = outcome.Store.Tree
                              // A handler is the atomicity unit, so one that
                              // halted rolled itself back and the fold carries
                              // on. What the EVENT reports is whether every
                              // handler it reached committed.
                              Committed = tally.Committed && outcome.Committed
                              Performed = tally.Performed @ outcome.Performed
                              Patches = tally.Patches @ outcome.Patches
                              Notifications = tally.Notifications @ outcome.Notifications
                              Diagnostics = tally.Diagnostics @ outcome.Diagnostics } } }

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
                // ONE path. There is no longer a call case and an everything-else
                // case: the fold recognises the calls, wherever they are, and
                // asks the arm below what they mean here.
                let bounded, tally =
                    BoundedActions.runBoundedActionWith
                        (handlerArm session.Services)
                        ev.NodeId
                        action
                        session.Store
                        (HandlerTally.start session.BaseTree)

                let outcome =
                    { Store =
                        { Tree = tally.Tree
                          Bindings = bounded.Store }
                      Committed = tally.Committed
                      Performed = tally.Performed
                      Patches = tally.Patches
                      Notifications = tally.Notifications
                      ClientEffects = bounded.Effects
                      // Two vocabularies, one list: the shared fold's
                      // diagnostics, then the handlers' in invocation order. The
                      // fold has no channel to interleave a foreign diagnostic
                      // into its own, and inventing one for a debugging surface
                      // would put a placement's vocabulary in the shared
                      // package — the exact coupling the arm exists to avoid.
                      Diagnostics = (bounded.Diagnostics |> List.map ServerDiagnostic.Bounded) @ tally.Diagnostics }

                commit session tally.Tree bounded.Store outcome
