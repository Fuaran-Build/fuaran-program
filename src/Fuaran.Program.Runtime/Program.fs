namespace Fuaran.Program.Runtime

open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open Fuaran.UI.OpStream.Replay
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded

// ============================================================================
//  The CLIENT placement of the bounded program loop.
//
//  Same algebra, different host. The server placement (`Fuaran.Program.Bounded`'s
//  driver) validates, interprets, re-resolves, then DIFFS and LOWERS to DOM
//  patches because the thing that will apply them is across a wire. This
//  placement runs in the browser, where the renderer is right there: validate,
//  interpret, re-resolve, hand the resolved tree to the host to render. No
//  server, no `'Msg` type, no hand-authored `update`.
//
//  The interpreter and the re-resolution pass are NOT reimplemented here — they
//  are the ones in `Fuaran.Program.Bounded`. That is the whole point: "one
//  algebra, two placements" is a property of the code, not a claim in a
//  document. The parity family asserts it executably.
//
//  ── What this core does NOT depend on ───────────────────────────────────────
//  Not the renderer, not a transport, not a sink. Each is an injected seam, the
//  same posture the server placement takes with its fragment renderer: a host
//  supplies a `Render` callback (a browser host wires the tier-1 renderer; a
//  headless test wires a recorder), an effect performer, an optional op sink and
//  an optional live channel. That is what lets the identical loop run in a
//  browser and under a headless test with nothing stubbed out.
// ============================================================================

/// A live channel the client can subscribe to for server-pushed `TreeOp`s — the
/// client mirror of the server-driven transport seam. Pure client-only
/// operation is the DEFAULT and needs no channel at all; a host supplies one
/// only for the hybrid mode, where a generated app runs its own interactions
/// locally *and* receives pushed updates.
type IClientLiveChannel =
    /// Subscribe to pushed ops. The returned disposable unsubscribes; the
    /// program's `dispose` calls it.
    abstract Subscribe: (TreeOp<obj> list -> unit) -> System.IDisposable

/// The host-coupled seams the client program loop delegates to.
type ProgramServices =
    {
        /// The dispatch policy gate (default-deny, as everywhere else in the
        /// bounded stack — the tree is untrusted).
        CanDispatch: Action<obj> -> bool
        /// Render the resolved tree. A browser host wires the tier-1 renderer
        /// here; a headless test wires a recorder. Called once per accepted
        /// event, and once at `init`.
        Render: Node<obj> -> unit
        /// Perform one closure-free client effect. The built-in arms behave
        /// identically to the server-driven shim's performer — one effect
        /// vocabulary, two transports.
        PerformEffect: ClientEffect -> unit
        /// The ops applied this step, for the journal / telemetry sink. A
        /// client-run generated app is replayable exactly like a server-driven
        /// one.
        OnApply: TreeOp<obj> list -> unit
        /// Per-interaction resource caps. Bounded code (the interpreter) +
        /// bounded cost (this) — the client placement inherits both.
        Budget: InteractionBudget
        /// Optional live channel for the hybrid mode. `None` is the default.
        Channel: IClientLiveChannel option
    }

module ProgramServices =
    /// Default services — **DENY all dispatch**, no effects performed, no sink,
    /// no channel, default budget. `render` MUST be supplied.
    ///
    /// The gate default is deny for the same reason it is on the server
    /// placement: this loop exists to run emitted trees, so the tree is
    /// untrusted by construction. `createPermissive` is the named opt-in.
    let create (render: Node<obj> -> unit) : ProgramServices =
        { CanDispatch = fun _ -> false
          Render = render
          PerformEffect = ignore
          OnApply = ignore
          Budget = InteractionBudget.defaults
          Channel = None }

    /// **The named opt-in back to an allow-everything gate.**
    let createPermissive (render: Node<obj> -> unit) : ProgramServices =
        { create render with
            CanDispatch = fun _ -> true }

/// Why a step produced no new tree: a gate rejection or a budget breach. Either
/// way the store is unchanged — the same two refusal classes the server
/// placement reports, by the same names.
type ProgramReject =
    | Gate of Validation.RejectReason
    | BudgetExceeded of detail: string

/// The observable result of one step.
type StepOutput =
    {
        /// The resolved tree after this step (unchanged on a refusal).
        Resolved: Node<obj>
        /// Closure-free effects performed this step, in order. Reported as well
        /// as performed so a test can assert on them without a performer.
        Effects: ClientEffect list
        Rejected: ProgramReject option
        Diagnostics: BoundedDiagnostic list
    }

/// A running client program: the FIXED decoded tree, the store, the current
/// resolved tree, the cached render cost, the services, and the live-channel
/// subscription if the host supplied a channel.
type Program =
    { BaseTree: Node<obj>
      Store: BoundedStore
      Resolved: Node<obj>
      NodeCount: int
      Services: ProgramServices
      Subscription: System.IDisposable option }

module Program =

    /// Build a program from a decoded `WireTree` + its initial store, render the
    /// initial resolved tree, and subscribe to the live channel if one was
    /// supplied.
    ///
    /// The wire tree is the correct input: its closures are inert sentinels and
    /// this loop never invokes one, so there is no `reify` step a host could get
    /// wrong. Budget ordering matches the server placement — the tree is priced
    /// first and only resolved if it is within budget, so an over-budget tree is
    /// not walked twice by the very construction meant to refuse it. An
    /// over-budget program is returned rather than refused (the signature is
    /// total), unresolved, and the first `handleEvent` rejects it.
    let mkBounded (services: ProgramServices) (store: BoundedStore) (wire: WireTree) : Program =
        let tree = WireTree.reify wire
        let budget = services.Budget.MaxNodes
        let cost = Budget.treeCost budget tree

        let resolved =
            if cost > budget then
                tree
            else
                Resolve.resolveTree store tree

        services.Render resolved

        { BaseTree = tree
          Store = store
          Resolved = resolved
          NodeCount = cost
          Services = services
          Subscription = None }

    let private rejected (program: Program) (r: ProgramReject) : Program * StepOutput =
        program,
        { Resolved = program.Resolved
          Effects = []
          Rejected = Some r
          Diagnostics = [] }

    /// Step the program with one untrusted inbound event: validate (the same G1
    /// gate the server placement uses), budget, interpret the bounded action
    /// against the store, re-resolve the FIXED base tree, render, perform the
    /// closure-free effects.
    ///
    /// No closure carried by the tree is ever invoked — the invariant is the
    /// shared interpreter's, so it holds here by construction rather than by a
    /// second implementation remembering to.
    let handleEvent (program: Program) (ev: LiveEvent) : Program * StepOutput =
        match Validation.validate program.Services.CanDispatch program.Resolved ev with
        | Error reason -> rejected program (Gate reason)
        | Ok { Action = None } ->
            // Legitimate event, no resolvable action — nothing to do, and
            // deliberately not a refusal.
            program,
            { Resolved = program.Resolved
              Effects = []
              Rejected = None
              Diagnostics = [] }
        | Ok { Action = Some action } ->
            let budget = program.Services.Budget
            let cost = Budget.actionCascadeCost action

            if cost > budget.MaxActions then
                rejected
                    program
                    (BudgetExceeded(sprintf "action cascade cost %d exceeds MaxActions %d" cost budget.MaxActions))
            elif program.NodeCount > budget.MaxNodes then
                rejected
                    program
                    (BudgetExceeded(sprintf "tree cost %d exceeds MaxNodes %d" program.NodeCount budget.MaxNodes))
            else
                let outcome = BoundedActions.runBoundedAction ev.NodeId action program.Store
                let newResolved = Resolve.resolveTree outcome.Store program.BaseTree

                // The op journal sees the same ops the server placement would
                // journal for this step, derived the same way — which is what
                // makes a client-run app replayable against a server-run one.
                let ops = TreeOpDiff.diff program.Resolved newResolved
                program.Services.OnApply ops

                program.Services.Render newResolved

                for effect in outcome.Effects do
                    program.Services.PerformEffect effect

                { program with
                    Store = outcome.Store
                    Resolved = newResolved },
                { Resolved = newResolved
                  Effects = outcome.Effects
                  Rejected = None
                  Diagnostics = outcome.Diagnostics }

    /// Apply server-pushed ops to the base tree (the hybrid mode). The pushed
    /// ops edit the BASE tree, not the resolved projection, so local state
    /// re-resolves on top of the new structure rather than being overwritten by
    /// it.
    let applyPushed (program: Program) (ops: TreeOp<obj> list) : Program =
        let newBase =
            ops
            |> List.fold (fun t op -> Fuaran.UI.Ops.Apply.apply op t |> Result.defaultValue t) program.BaseTree

        let newResolved = Resolve.resolveTree program.Store newBase
        program.Services.OnApply ops
        program.Services.Render newResolved

        { program with
            BaseTree = newBase
            Resolved = newResolved }

    /// Subscribe to the host's live channel, if one was supplied. Returns the
    /// program carrying its subscription; `dispose` releases it. Pure
    /// client-only operation never calls this.
    let subscribe (onProgram: Program -> unit) (program: Program) : Program =
        match program.Services.Channel with
        | None -> program
        | Some channel ->
            let mutable current = program

            let sub =
                channel.Subscribe(fun ops ->
                    current <- applyPushed current ops
                    onProgram current)

            current <- { current with Subscription = Some sub }
            current

    /// Release the live-channel subscription, if any. Idempotent.
    let dispose (program: Program) : Program =
        match program.Subscription with
        | None -> program
        | Some sub ->
            sub.Dispose()
            { program with Subscription = None }
