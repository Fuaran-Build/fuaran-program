namespace Fuaran.Program.Server

open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open Fuaran.UI.ServerDriven
open Fuaran.Program.Bounded

// ============================================================================
//  A handler, as data — and the fold that runs one.
//
//  A handler is the decomposition every host already performs by hand
//  (validate → read → compute → mutate → respond), written down as a STAGE LIST
//  rather than as a function. Two stage kinds, and the split is the whole
//  design:
//
//    Compute a   the bounded algebra — interpreted by the SHARED fold, the same
//                one the browser placement and the server driver run. Not a
//                copy, not a variant: `BoundedActions.runBoundedAction`, called
//                from exactly one place in this file. That single call site is
//                the guard on D1's "no second evaluator" — there is nowhere
//                else in this package that matches on an `Action`.
//
//    Effect e    one arm of this placement's closed effect vocabulary, run
//                through the default-deny gate beside it.
//
//  Sequencing is the core algebra's, not a new construct: a stage list is read
//  in order exactly as `Action.Chain` folds in order. What the list adds is the
//  ability to interleave the two vocabularies, which is precisely what a
//  handler is for and precisely what the client placement has no need of.
//
//  ── Where a handler comes from ──────────────────────────────────────────────
//  The HOST registers it. A generated tree can only NAME a handler, through an
//  `Action.Call` endpoint; it cannot carry one. That is deliberate at this
//  stage: it means every host function, pipeline and op sequence in this file
//  is host-authored data, so the capability envelope of a session is fixed
//  before any untrusted tree arrives. Giving a handler a wire form is a later,
//  separate act — see `docs/server-handler-atomicity.md`.
//
//  ── Atomicity ───────────────────────────────────────────────────────────────
//  The HANDLER is the unit, not the stage. Stages thread a value; nothing is
//  committed until the last one succeeds; a denial or a failure discards the
//  accumulated store, the ops, the effects and the notifications, keeping only
//  the diagnostics that say why. A half-applied handler is therefore
//  unrepresentable in the returned value.
//
//  That is total for state this placement owns and NOT total for a host
//  performer that already ran — a `HostCall` in stage 2 has done whatever it
//  does by the time stage 3 fails, and no rollback here reaches it. The outcome
//  says which happened (`Committed`) rather than implying the stronger claim.
//  The design note works through what closing that gap would cost.
// ============================================================================

/// The state a handler runs against. Two channels, deliberately named apart:
/// `Tree` is DOMAIN state, mutated only by `ServerEffect.ApplyOps`; `Bindings`
/// is the session's binding store, written by the shared fold's `SetState` and
/// by the query/host-call landing slots. A reader who conflates them will
/// eventually claim the wrong thing about durability.
type ServerStore =
    { Tree: Node<obj>
      Bindings: BoundedStore }

/// One stage of a handler.
type HandlerStage =
    /// Bounded algebra, run by the SHARED interpreter against `Bindings`.
    | Compute of Action<obj>
    /// One server effect, run through the gate.
    | Effect of ServerEffect

/// A named, host-registered handler: an ordered stage list and nothing else.
/// No closure, no host reference, no captured state — a handler is data, which
/// is what lets it be inspected, diffed and (later) given a wire form.
type Handler =
    { Name: string
      Stages: HandlerStage list }

/// What a handler run produced, at the level a host cares about. Payload-free
/// where it is a record of a refusal, payload-bearing where it is work the host
/// must actually perform.
[<RequireQualifiedAccess>]
type ServerDiagnostic =
    /// A diagnostic from the shared interpreter, passed through unchanged so a
    /// `Compute` stage reports exactly what the same action reports at the other
    /// placements.
    | Bounded of BoundedDiagnostic
    /// An effect was refused at the gate or had no performer.
    | Denied of ServerEffectDenial
    /// An effect ran and failed. `reason` is log-safe: for a `HostCall` it is
    /// the host performer's own text, and for an engine failure it is the error
    /// DISCRIMINATOR only. The engine's full message quotes names taken from the
    /// pipeline or the op — handler-declared today, wire-carried tomorrow — and
    /// the moment that changes, a verbatim message becomes a payload leak. The
    /// cost is a thinner error, and it is recorded as an open question rather
    /// than pretended away.
    | Failed of capability: string * reason: string
    /// The tree's `Action.Call` named an endpoint no handler is registered
    /// under.
    ///
    /// **It deliberately does not say which.** That string is the one value in
    /// this subsystem that comes off the wire, so echoing it into a host's logs
    /// would be exactly the leak the effect denials avoid — the same rule that
    /// keeps a refused `Navigate` from logging its route. A host debugging an
    /// emission reads its own registry instead, which is a closed list it
    /// already has.
    | HandlerUnregistered

/// The result of running one handler.
type HandlerOutcome =
    {
        /// The store after the handler, or the store it started from when the
        /// handler did not commit.
        Store: ServerStore
        /// Whether the handler ran to completion. `false` means every value
        /// below is the entry state and the handler's declared work did not
        /// happen — except for any host performer that had already run.
        Committed: bool
        /// The capabilities performed, in order — the audit trail of what the
        /// handler actually did.
        Performed: string list
        /// Ops the handler asked to be shipped to the client, in order.
        /// Distinct from anything applied to the domain tree.
        Patches: TreeOp<obj> list
        /// Host-channel messages the handler asked for, in order.
        Notifications: (string * Fuaran.Core.JVal) list
        /// Closure-free client effects the shared interpreter emitted from a
        /// `Compute` stage — the same values, from the same fold, as at the
        /// other placements.
        ClientEffects: ClientEffect list
        Diagnostics: ServerDiagnostic list
    }

module Handler =

    /// The state threaded through the stage fold. Lists accumulate reversed and
    /// are flipped once at the end, so a long handler does not quadratically
    /// re-append.
    type private Accumulator =
        { Store: ServerStore
          Halted: bool
          Performed: string list
          Patches: TreeOp<obj> list
          Notifications: (string * Fuaran.Core.JVal) list
          ClientEffects: ClientEffect list
          Diagnostics: ServerDiagnostic list }

    /// The discriminator of a pipeline-evaluation failure. Deliberately not the
    /// full error: see `ServerDiagnostic.Failed`.
    let private evalErrorKind (error: Fuaran.Core.EvalError) : string =
        match error with
        | Fuaran.Core.UnknownColumn _ -> "UnknownColumn"
        | Fuaran.Core.TypeError _ -> "TypeError"
        | Fuaran.Core.AggError _ -> "AggError"
        | Fuaran.Core.JoinError _ -> "JoinError"
        | Fuaran.Core.ArityError _ -> "ArityError"
        | Fuaran.Core.UnresolvedSource _ -> "UnresolvedSource"
        | Fuaran.Core.OverflowError _ -> "OverflowError"
        | Fuaran.Core.UnboundParam _ -> "UnboundParam"

    let private halt (capability: string) (reason: string) (acc: Accumulator) : Accumulator =
        { acc with
            Halted = true
            Diagnostics = ServerDiagnostic.Failed(capability, reason) :: acc.Diagnostics }

    let private deny (denial: ServerEffectDenial) (acc: Accumulator) : Accumulator =
        { acc with
            Halted = true
            Diagnostics = ServerDiagnostic.Denied denial :: acc.Diagnostics }

    /// Run one effect against the store. The gate is consulted FIRST — before a
    /// pipeline is evaluated, before an op reaches the apply engine, and before
    /// a performer is even looked up as a callable — so no side effect of any
    /// kind can precede the policy decision.
    let private runEffect
        (registry: ServerEffectRegistry)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (effect: ServerEffect)
        (acc: Accumulator)
        : Accumulator =
        let capability = ServerEffect.capability effect

        if not (registry.Gate capability) then
            let denial = ServerEffectDenial.GateRefused capability
            registry.OnDenied denial
            deny denial acc
        else
            let performed =
                { acc with
                    Performed = capability :: acc.Performed }

            match effect with
            | ServerEffect.RunQuery(name, source, pipeline) ->
                let evaluated =
                    Fuaran.Core.DataFrame.evalSource resolve source
                    |> Result.bind (Fuaran.Core.DataFrame.evalPipelineWith resolve pipeline)

                match evaluated with
                | Error err -> halt capability (evalErrorKind err) acc
                | Ok table ->
                    { performed with
                        Store =
                            { performed.Store with
                                Bindings =
                                    { performed.Store.Bindings with
                                        QueryResults =
                                            Map.add
                                                name
                                                (Unchecked.nonNull (box table))
                                                performed.Store.Bindings.QueryResults } } }

            | ServerEffect.ApplyOps ops ->
                // The only domain-state mutation. Folded with short-circuit: an
                // op that fails leaves the whole handler uncommitted rather than
                // applying its predecessors, which is what makes the atomicity
                // claim above true of the tree and not merely of the store.
                let applied =
                    ops
                    |> List.fold
                        (fun state op -> state |> Result.bind (Fuaran.UI.Ops.Apply.apply op))
                        (Ok performed.Store.Tree)

                match applied with
                | Error err -> halt capability (sprintf "%A" err.Code) acc
                | Ok tree ->
                    { performed with
                        Store = { performed.Store with Tree = tree } }

            | ServerEffect.HostCall(fn, args, into) ->
                match Map.tryFind fn registry.HostFunctions with
                | None ->
                    let denial = ServerEffectDenial.Unregistered capability
                    registry.OnDenied denial
                    deny denial acc
                | Some performer ->
                    match performer args with
                    | Error reason -> halt capability reason acc
                    | Ok result ->
                        match into with
                        | None -> performed
                        | Some key ->
                            // The host-reserved namespace is closed here for the
                            // same reason the shared interpreter closes it: a
                            // landing slot is a write, and a write into the
                            // host's own namespace is the case the namespace
                            // exists for.
                            if Fuaran.UI.Renderer.StateKeys.isHostReserved key then
                                halt
                                    capability
                                    (sprintf
                                        "landing slot is under the host-reserved '%s' namespace"
                                        Fuaran.UI.Renderer.StateKeys.HostReservedPrefix)
                                    acc
                            else
                                { performed with
                                    Store =
                                        { performed.Store with
                                            Bindings =
                                                { performed.Store.Bindings with
                                                    State =
                                                        Map.add
                                                            key
                                                            (JValObj.toObj result)
                                                            performed.Store.Bindings.State } } }

            | ServerEffect.EmitPatch ops ->
                { performed with
                    Patches = List.rev ops @ performed.Patches }

            | ServerEffect.Notify(channel, payload) ->
                { performed with
                    Notifications = (channel, payload) :: performed.Notifications }

    /// Run one stage.
    let private runStage
        (registry: ServerEffectRegistry)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (nodeId: string)
        (stage: HandlerStage)
        (acc: Accumulator)
        : Accumulator =
        match stage with
        | Compute action ->
            // THE shared fold. One call site, and the only place this package
            // looks at an `Action` at all — so "the interpreter is imported, not
            // forked" is a property a reader can check by grep rather than a
            // claim they have to trust.
            let outcome = BoundedActions.runBoundedAction nodeId action acc.Store.Bindings

            { acc with
                Store =
                    { acc.Store with
                        Bindings = outcome.Store }
                ClientEffects = List.rev outcome.Effects @ acc.ClientEffects
                Diagnostics =
                    (outcome.Diagnostics |> List.rev |> List.map ServerDiagnostic.Bounded)
                    @ acc.Diagnostics }
        | Effect effect -> runEffect registry resolve effect acc

    /// Run a handler's stages in order against `store`, committing only if every
    /// stage succeeded. `nodeId` is the originating event's node, threaded into
    /// the shared interpreter exactly as the other placements thread it.
    let run
        (registry: ServerEffectRegistry)
        (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
        (nodeId: string)
        (handler: Handler)
        (store: ServerStore)
        : HandlerOutcome =
        let start =
            { Store = store
              Halted = false
              Performed = []
              Patches = []
              Notifications = []
              ClientEffects = []
              Diagnostics = [] }

        let final =
            handler.Stages
            |> List.fold
                (fun acc stage ->
                    if acc.Halted then
                        acc
                    else
                        runStage registry resolve nodeId stage acc)
                start

        if final.Halted then
            // Roll back to the entry state. The diagnostics survive: they are
            // the entire record of why nothing happened, and discarding them
            // would turn a refusal into a silence.
            { Store = store
              Committed = false
              Performed = []
              Patches = []
              Notifications = []
              ClientEffects = []
              Diagnostics = List.rev final.Diagnostics }
        else
            { Store = final.Store
              Committed = true
              Performed = List.rev final.Performed
              Patches = List.rev final.Patches
              Notifications = List.rev final.Notifications
              ClientEffects = List.rev final.ClientEffects
              Diagnostics = List.rev final.Diagnostics }
