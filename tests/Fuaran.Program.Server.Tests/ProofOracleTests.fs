module Fuaran.Program.Server.Tests.ProofOracleTests

// ============================================================================
//  Phase 1717 — the differential host for the proved two-phase staging.
//
//  `proofs/Staging.fst` is a model of `Handler.run` — the plan phase, the
//  phase boundary and the perform phase — with the performer abstract, and
//  four theorems about it: `plan_pure`, `residual_is_prefix`,
//  `performed_in_order` and `commit_is_total_prefix`. A theorem about a
//  model is a theorem about the code only if the model IS the code, and
//  nothing in the prover can say so. This host says so, the only way it can
//  be said: it runs the EXTRACTION of the model (`proofs/oracle/Staging.fs`,
//  byte-compared against a fresh extraction by `proofs/check.ps1`) beside
//  production, over the same inputs, and requires the two to produce the
//  same outcome.
//
//  What the corpus is. The staging cases the handler's own suites drive —
//  `HandlerLoopTests`' every-arm handler, its ordered handler, its landing
//  slot, its three-call half-performer, its plan-then-halt, its reads-too-
//  early, its refused gate; `DurableInterpreterTests`' refresh handler and
//  its two-call handler — re-declared here (they are private to those
//  modules), plus the plan-halt arms those cases do not reach: an
//  unregistered performer, a refused argument policy, a reserved landing
//  slot, an apply refusal, an unresolvable query.
//
//  What the performer is. A SCRIPTED one: it counts its own invocations and
//  refuses at a chosen position, so every staged list is run at EVERY failure
//  position — before the first call, after it, ... , after the last — and
//  once with no failure at all. The pure model quantifies over performers
//  that are functions of the call; the scripted performer is a function of
//  its history, which the extraction exercises identically to production
//  because the perform phase asks each call once, in order. That is the one
//  case the theorem cannot state and the differential can.
//
//  What is compared. The outcome, projected the way the durable parity leg
//  projects it (the canonical tree, the state, the query slots, the verdict,
//  the audit trail, the patches, the notifications, the effects and the
//  diagnostics) — AND the performer's own log of what it ran, on both sides.
//  The log is the ground truth the law is about: `Performed` is a CLAIM the
//  handler makes about what happened outside, and the log is what happened.
//  Every green case checks the claim against the log, which is why the go-red
//  case is a performer that LIES — reports success for a call it did not run
//  — and requires that check to report it. A comparison that could not lose
//  would not be evidence.
// ============================================================================

open System
open Expecto
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Ops
open Fuaran.UI.Ops.Types
open Fuaran.UI.Ops.Introspect
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.OpStream.Abstractions
open Fuaran.UI.OpStream.Replay
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded
open Fuaran.Program.UI
open Fuaran.Program.Server
open Fuaran.Program.Server.UI
open Fuaran.Program.Parity

let private jstr (s: string) = Fuaran.Core.JStr s

// ─── Translation: production ⇄ the model ────────────────────────────────────
//
// The model is generic in the tree, the bindings, the values, the ops, the
// queries, the actions, the client effects and the shared fold's diagnostics;
// here they are the UI witness's concrete types, and the performer token `p`
// is production's own closure. What the model reads of the witness and of the
// registry is supplied from production's own members — these are the ASSUMED
// rung, and the point of the differential is that everything ELSE is the
// extraction.

type private Query = Fuaran.Core.DataSource * Fuaran.Core.Transform list
type private Performer = Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>

type private ModelWitness =
    Staging.witness<
        Node<obj>,
        BindingSources,
        Fuaran.Core.JVal,
        TreeOp<obj>,
        Query,
        Action<obj>,
        ClientEffect,
        BoundedDiagnostic
     >

type private ModelRegistry = Staging.registry<Node<obj>, Fuaran.Core.JVal, TreeOp<obj>, Query, Performer>
type private ModelStage = Staging.stage<Action<obj>, Fuaran.Core.JVal, TreeOp<obj>, Query>

type private ModelOutcome =
    Staging.outcome<Node<obj>, BindingSources, Fuaran.Core.JVal, TreeOp<obj>, ClientEffect, BoundedDiagnostic>

let private modelOpt (value: 'T option) : Staging.opt<'T> =
    match value with
    | Some v -> Staging.OSome v
    | None -> Staging.ONone

let private modelRes (value: Result<'T, string>) : Staging.res<'T> =
    match value with
    | Ok v -> Staging.ROk v
    | Error e -> Staging.RErr e

/// Production reduces an evaluation error to its DISCRIMINATOR before it
/// reaches a diagnostic (`Handler.evalErrorKind`, private, one arm per case
/// and every arm its case name). The case name IS that mapping, read off the
/// union rather than restated arm by arm — so a production arm whose text
/// stopped being its name would surface here as a diagnostic divergence
/// rather than being copied into agreement.
let private evalErrorKind (error: Fuaran.Core.EvalError) : string =
    let case, _ =
        Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(error, typeof<Fuaran.Core.EvalError>)

    case.Name

let private witness = UiWitness.witness

/// The UI witness with an op-channel GUARD (Phase 1974): `UpdateStyle` is
/// viewed as one — it resolves to a refusal exactly when the node it names is
/// not in the tree as planned, and whatever style it would have set is
/// discarded. The UI tier itself has no guard; this composition exists so the
/// differential reaches the guard clause of the `ApplyOps` arm.
let private guardedWitness: UiWitness.UiProgramWitness =
    { witness with
        State =
            { witness.State with
                View =
                    fun op ->
                        match op with
                        | TreeOp.UpdateStyle _ -> OpView.Require
                        | _ -> OpView.Edit } }

let private modelWitness
    (witness: UiWitness.UiProgramWitness)
    (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
    : ModelWitness =
    { w_compute =
        fun nodeId action bindings ->
            let outcome = BoundedActions.runInert witness nodeId action bindings

            { bo_store = outcome.Store
              bo_effects = outcome.Effects
              bo_diagnostics = outcome.Diagnostics }
      w_query =
        fun name (source, pipeline) bindings ->
            Fuaran.Core.DataFrame.evalSource resolve source
            |> Result.bind (Fuaran.Core.DataFrame.evalPipelineWith resolve pipeline)
            |> Result.map (fun table -> witness.Dispatch.Store.LandQuery name table bindings)
            |> Result.mapError evalErrorKind
            |> modelRes
      w_apply = fun op tree -> witness.State.Stream.Apply op tree |> modelRes
      // The production `View`, taken to exhaustion — the model's `w_op_view`
      // (Phase 1976): an edit and a guard carry the op the handler holds, a
      // branch's arms and a repeat's body are viewed in turn.
      w_op_view =
        let rec view (op: TreeOp<obj>) : Staging.op_view<TreeOp<obj>> =
            match witness.State.View op with
            | OpView.Edit -> Staging.OEdit op
            | OpView.Require -> Staging.ORequire op
            | OpView.Choose(entry, whenTrue, whenFalse, exit) ->
                Staging.OChoose(entry, whenTrue |> List.map view, whenFalse |> List.map view, modelOpt exit)
            | OpView.Repeat(count, body) -> Staging.ORepeat(bigint count, body |> List.map view)

        view
      w_assign = witness.Dispatch.Store.Assign
      // The landing-slot refusal as production renders it (Phase 1974): the
      // reserved-namespace text over this witness's predicate and prefix.
      w_slot_refused =
        fun key ->
            if witness.Dispatch.Store.IsReserved key then
                Staging.OSome(
                    sprintf
                        "landing slot is under the host-reserved '%s' namespace"
                        witness.Dispatch.Store.ReservedPrefix
                )
            else
                Staging.ONone }

let private modelEffect
    (effect: ServerEffect<TreeOp<obj>>)
    : Staging.server_effect<Fuaran.Core.JVal, TreeOp<obj>, Query> =
    match effect with
    | ServerEffect.RunQuery(name, source, pipeline) -> Staging.RunQuery(name, (source, pipeline))
    | ServerEffect.ApplyOps ops -> Staging.ApplyOps ops
    | ServerEffect.HostCall(fn, args, into) -> Staging.HostCall(fn, args, modelOpt into)
    | ServerEffect.EmitPatch ops -> Staging.EmitPatch ops
    | ServerEffect.Notify(channel, payload) -> Staging.Notify(channel, payload)

let private modelStage (stage: HandlerStage) : ModelStage =
    match stage with
    | Compute action -> Staging.SCompute action
    | Effect effect -> Staging.SEffect(modelEffect effect)

/// The registry, split as the model splits it: the LOOKUP answers the
/// closure as an opaque token, and the BEHAVIOUR applies it. The argument
/// policy is consulted on the production effect, because the policy's
/// vocabulary is production's.
let private modelRegistry
    (performance: OpPerformance<Node<obj>, TreeOp<obj>>)
    (registry: ServerEffectRegistry)
    : ModelRegistry =
    { r_gate = registry.Gate
      r_policy =
        fun effect ->
            let production =
                match effect with
                | Staging.RunQuery(name, (source, pipeline)) -> ServerEffect.RunQuery(name, source, pipeline)
                | Staging.ApplyOps ops -> ServerEffect.ApplyOps ops
                | Staging.HostCall(fn, args, into) ->
                    ServerEffect.HostCall(
                        fn,
                        args,
                        (match into with
                         | Staging.OSome key -> Some key
                         | Staging.ONone -> None)
                    )
                | Staging.EmitPatch ops -> ServerEffect.EmitPatch ops
                | Staging.Notify(channel, payload) -> ServerEffect.Notify(channel, payload)

            match ServerArgumentPolicy.check witness.State registry production with
            | Ok() -> Staging.ONone
            | Error defect -> Staging.OSome(ServerArgumentPolicy.describe defect)
      r_lookup = fun fn -> Map.tryFind fn registry.HostFunctions |> modelOpt
      r_perf = fun performer args -> performer args |> modelRes
      // The op performer, split as the model splits it (Phase 1967): the
      // TOKEN is production's own closure over the state as of the op and the
      // op (Phase 1974), the argument inert — the same shape production
      // stages, so the perform phase runs the two through one loop on both
      // sides.
      r_op_perform =
        match performance with
        | OpPerformance.InMemory -> Staging.ONone
        | OpPerformance.Performed perform ->
            Staging.OSome(fun state op ->
                (fun (_: Fuaran.Core.JVal) -> perform state op |> Result.map (fun () -> Fuaran.Core.JObj [])),
                Fuaran.Core.JObj []) }

let private productionDiagnostic (diagnostic: Staging.diagnostic<BoundedDiagnostic>) : ServerDiagnostic =
    match diagnostic with
    | Staging.Bounded inner -> ServerDiagnostic.Bounded inner
    | Staging.Denied(Staging.Unregistered capability) ->
        ServerDiagnostic.Denied(ServerEffectDenial.Unregistered capability)
    | Staging.Denied(Staging.GateRefused capability) ->
        ServerDiagnostic.Denied(ServerEffectDenial.GateRefused capability)
    | Staging.Failed(capability, reason) -> ServerDiagnostic.Failed(capability, reason)
    | Staging.PerformFailed(capability, reason) -> ServerDiagnostic.PerformFailed(capability, reason)

/// The model's outcome in production's shape, so ONE projection serves both.
let private productionShaped (outcome: ModelOutcome) : HandlerOutcome =
    { Store =
        { Tree = outcome.oc_store.st_tree
          Bindings = outcome.oc_store.st_bindings }
      Committed = outcome.oc_committed
      Performed = outcome.oc_performed
      Patches = outcome.oc_patches
      Notifications = outcome.oc_notifications
      ClientEffects = outcome.oc_client_effects
      Diagnostics = outcome.oc_diagnostics |> List.map productionDiagnostic }

/// The comparable projection — the same one the durable parity leg uses,
/// for the same reason: a resolved tree's nodes carry handler slots, so the
/// record itself has no structural equality, and the canonical encoding is
/// what every other parity leg in this repository compares.
let private projectionOf (outcome: HandlerOutcome) =
    {| Tree = CanonicalJson.encodeNode outcome.Store.Tree
       State = outcome.Store.Bindings.State |> Map.map (fun _ v -> sprintf "%A" v)
       Queries = outcome.Store.Bindings.QueryResults |> Map.toList |> List.map fst
       Committed = outcome.Committed
       Performed = outcome.Performed
       Patches = outcome.Patches |> List.map (sprintf "%A")
       Notifications = outcome.Notifications
       Effects = outcome.ClientEffects |> List.map ClientEffect.encode
       Diagnostics = outcome.Diagnostics |> List.map (sprintf "%A") |}

let private runModel
    (witness: UiWitness.UiProgramWitness)
    (performance: OpPerformance<Node<obj>, TreeOp<obj>>)
    (registry: ServerEffectRegistry)
    (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
    (nodeId: string)
    (handler: Handler)
    (store: ServerStore)
    : HandlerOutcome =
    Staging.run
        (modelWitness witness resolve)
        (modelRegistry performance registry)
        nodeId
        (handler.Stages |> List.map modelStage)
        { st_tree = store.Tree
          st_bindings = store.Bindings }
    |> productionShaped

// ─── The scripted performer ─────────────────────────────────────────────────

/// What a performer actually did: the CAPABILITIES it was invoked under, in
/// invocation order — `host:<fn>` for a host function, `ApplyOps` for an op
/// under a registered op performer (Phase 1967). The ground truth every claim
/// below is checked against.
type private Log = System.Collections.Generic.List<string>

/// A scripted performer's two faces over ONE counter: the host functions, by
/// name, and the op performer. One counter, because the perform phase runs
/// ops and host calls through one loop in plan order, and a failure position
/// counts across both.
type private Scripted =
    {
        Host: string -> Performer
        Op: Node<obj> -> TreeOp<obj> -> Result<unit, string>
        /// The state the op performer was handed with each op, canonically
        /// encoded, in invocation order (Phase 1974) — compared across the two
        /// sides, so the model's `staged_from` and production's staged op agree
        /// on WHICH state travels with each op, not only on which op.
        Handed: System.Collections.Generic.List<string>
    }

/// A performer that counts its invocations across every name it is
/// registered under — and every op it performs — and refuses at ONE position,
/// or never. Each run builds a fresh one, so production and the model each
/// start from zero.
let private scripted (log: Log) (failAt: int option) : Scripted =
    let calls = ref 0
    let handed = System.Collections.Generic.List<string>()

    let ask (capability: string) : Result<unit, string> =
        let position = calls.Value
        calls.Value <- position + 1
        log.Add capability

        match failAt with
        | Some k when k = position -> Error(sprintf "scripted refusal at position %d" k)
        | _ -> Ok()

    { Host = fun name -> fun _ -> ask ("host:" + name) |> Result.map (fun () -> jstr (sprintf "ran:%s" name))
      Op =
        fun state _ ->
            handed.Add(Fuaran.UI.OpStream.Abstractions.CanonicalJson.encodeNode state)
            ask "ApplyOps"
      Handed = handed }

/// The functions a case registers, and the registry built around a
/// performer factory so production and the model each get their own.
type private StagingCase =
    {
        Name: string
        Origin: string
        Functions: string list
        Handler: Handler
        Store: ServerStore
        /// Wraps the permissive, fully-registered registry — the gate and the
        /// constraints a case narrows with.
        Shape: ServerEffectRegistry -> ServerEffectRegistry
        Resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>
        /// Whether this case registers the scripted OP performer (Phase 1967),
        /// making `ApplyOps` a staged arm; `false` is in-memory, every
        /// placement's default and the UI tier's.
        PerformOps: bool
        /// Whether this case runs under the witness with an op-channel guard
        /// (Phase 1974); `false` is the UI witness as it ships.
        Guarded: bool
    }

/// The witness a case runs under, on both sides.
let private witnessOf (case: StagingCase) : UiWitness.UiProgramWitness =
    if case.Guarded then guardedWitness else witness

/// The op performance a case runs under, over this run's scripted performer.
let private performanceOf (case: StagingCase) (s: Scripted) : OpPerformance<Node<obj>, TreeOp<obj>> =
    if case.PerformOps then
        OpPerformance.Performed s.Op
    else
        OpPerformance.InMemory

let private registryFor (case: StagingCase) (performer: string -> Performer) : ServerEffectRegistry =
    case.Functions
    |> List.fold (fun r fn -> ServerEffectRegistry.register fn (performer fn) r) ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.permissive
    |> case.Shape

/// One run of one case, on both sides, at one failure position.
type private Run =
    { Production: HandlerOutcome
      ProductionLog: string list
      ProductionHanded: string list
      Model: HandlerOutcome
      ModelLog: string list
      ModelHanded: string list }

let private runCase (case: StagingCase) (failAt: int option) : Run =
    let productionLog = Log()
    let modelLog = Log()

    let productionScript = scripted productionLog failAt
    let modelScript = scripted modelLog failAt

    let production =
        Fuaran.Program.Server.Handler.runWith
            (witnessOf case)
            (registryFor case productionScript.Host)
            (performanceOf case productionScript)
            case.Resolve
            "call"
            case.Handler
            case.Store

    let model =
        runModel
            (witnessOf case)
            (performanceOf case modelScript)
            (registryFor case modelScript.Host)
            case.Resolve
            "call"
            case.Handler
            case.Store

    { Production = production
      ProductionLog = List.ofSeq productionLog
      ProductionHanded = List.ofSeq productionScript.Handed
      Model = model
      ModelLog = List.ofSeq modelLog
      ModelHanded = List.ofSeq modelScript.Handed }

/// The calls the performer's log says ran — the log already records
/// capabilities.
let private ranCapabilities (log: string list) = log

/// `Performed`'s external suffix — what the handler CLAIMS ran outside: its
/// host calls, and, under a registered op performer, its ops. In memory an
/// `ApplyOps` entry is the plan phase's and is not a claim about the outside.
let private claimedExternal (case: StagingCase) (outcome: HandlerOutcome) =
    outcome.Performed
    |> List.filter (fun c ->
        c.StartsWith("host:", StringComparison.Ordinal)
        || (case.PerformOps && c = "ApplyOps"))

/// The whole comparison, as a reported divergence or nothing. Three checks:
/// the two outcomes project equal; the two performers were asked the same
/// things in the same order; and production's claim about what ran outside
/// is the performer's own log — bar the one call the log names and the
/// claim may not, the refused one. The third is the one the go-red case
/// defeats.
let private divergence (case: StagingCase) (failAt: int option) (run: Run) : string option =
    let where =
        sprintf
            "%s (%s), failure at %s"
            case.Name
            case.Origin
            (failAt |> Option.map string |> Option.defaultValue "none")

    let production = projectionOf run.Production
    let model = projectionOf run.Model

    if production <> model then
        Some(sprintf "%s: the outcomes differ\n  production: %A\n  model:      %A" where production model)
    elif run.ProductionLog <> run.ModelLog then
        Some(
            sprintf
                "%s: the performers were asked different things\n  production: %A\n  model:      %A"
                where
                run.ProductionLog
                run.ModelLog
        )
    elif run.ProductionHanded <> run.ModelHanded then
        Some(
            sprintf
                "%s: the op performers were handed different states\n  production: %A\n  model:      %A"
                where
                run.ProductionHanded
                run.ModelHanded
        )
    else
        // What ran, per the log, minus the refused call — the log records an
        // invocation the moment it is asked, and a refused one is asked and
        // not performed.
        let ran =
            match failAt with
            | Some k when k < List.length run.ProductionLog -> List.take k run.ProductionLog
            | _ -> run.ProductionLog

        if claimedExternal case run.Production <> ranCapabilities ran then
            Some(
                sprintf
                    "%s: Performed claims %A outside, but the performer's log says %A ran"
                    where
                    (claimedExternal case run.Production)
                    (ranCapabilities ran)
            )
        else
            None

// ─── The corpus ─────────────────────────────────────────────────────────────

let private rows: Fuaran.Core.Table =
    { Fuaran.Core.Table.empty with
        Columns =
            [ { Name = "n"
                Type = Fuaran.Core.IntType
                Cells = [ Fuaran.Core.Int 1; Fuaran.Core.Int 2; Fuaran.Core.Int 3 ] } ] }

let private limitTwo =
    [ Fuaran.Core.Limit(Fuaran.Core.Slot.Lit 2, Fuaran.Core.Slot.Lit 0) ]

/// `HandlerLoopTests`' tree: one node the handlers address.
let private loopTree: Node<obj> =
    Fuaran.dashboard
        "root"
        { Defaults.dashboard<obj> with
            Children =
                [ Fuaran.button
                      "call"
                      { Defaults.button<obj> with
                          Label = TextSource.Literal "call"
                          OnClick = Action.Call("/handlers/x", None, None) } ] }

/// `DurableInterpreterTests`' tree: the two nodes its refresh handler
/// addresses.
let private durableTree: Node<obj> =
    Fuaran.dashboard
        "root"
        { Defaults.dashboard<obj> with
            Children =
                [ Fuaran.button
                      "refresh"
                      { Defaults.button<obj> with
                          Label = TextSource.Literal "refresh" }
                  Fuaran.markdown "readout" "idle" ] }

let private loopStore: ServerStore = { Tree = loopTree; Bindings = empty }
let private durableStore: ServerStore = { Tree = durableTree; Bindings = empty }

let private case
    (origin: string)
    (name: string)
    (functions: string list)
    (store: ServerStore)
    (stages: HandlerStage list)
    =
    { Name = name
      Origin = origin
      Functions = functions
      Handler = { Name = name; Stages = stages }
      Store = store
      Shape = id
      Resolve = Fuaran.Core.DataFrame.noResolve
      PerformOps = false
      Guarded = false }

let private shaped (shape: ServerEffectRegistry -> ServerEffectRegistry) (c: StagingCase) = { c with Shape = shape }

/// The case registers the scripted op performer (Phase 1967).
let private performingOps (c: StagingCase) = { c with PerformOps = true }

/// The case runs under the witness with an op-channel guard (Phase 1974).
let private guarded (c: StagingCase) = { c with Guarded = true }

let private loopOrigin = "HandlerLoopTests"
let private durableOrigin = "DurableInterpreterTests"

/// The staging cases `HandlerLoopTests` pins instances of, re-declared.
let private loopCases: StagingCase list =
    [ case
          loopOrigin
          "a query, a state write, a host call, a patch and a notification"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, limitTwo))
            Compute(Action.SetState("status", Some(jstr "written"), None))
            Effect(ServerEffect.HostCall("audit", jstr "note", Some "audited"))
            Effect(ServerEffect.EmitPatch [ TreeOp.RemoveNode(NodeId "call") ])
            Effect(ServerEffect.Notify("channel", jstr "note")) ]
      case
          loopOrigin
          "execution order, not stage order"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, []))
            Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.ApplyOps [])
            Effect(ServerEffect.EmitPatch [])
            Effect(ServerEffect.Notify("channel", jstr "note")) ]
      case
          loopOrigin
          "a host call landing in its declared slot"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", Some "audited")) ]
      case
          loopOrigin
          "three host calls"
          [ "first"; "second"; "third" ]
          loopStore
          [ Effect(ServerEffect.HostCall("first", jstr "a", Some "landed"))
            Effect(ServerEffect.HostCall("second", jstr "b", None))
            Effect(ServerEffect.HostCall("third", jstr "c", None)) ]
      case
          loopOrigin
          "a host call staged, then the plan fails"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", Some "audited"))
            Compute(Action.SetState("status", Some(jstr "written"), None))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "absent") ]) ]
      case
          loopOrigin
          "a later stage reading an earlier host call's result"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", Some "audited"))
            Compute(Action.SetState("echo", None, Some(Binding.State("audited", Some(jstr "unresolved-at-plan-time"))))) ]
      case
          loopOrigin
          "the gate refuses the host call"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", None)) ]
      |> shaped (ServerEffectRegistry.withGate (fun _ -> false))
      case
          loopOrigin
          "an unresolvable query source"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Ref "elsewhere", []))
            Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.Notify("channel", jstr "never")) ] ]

/// The staging cases `DurableInterpreterTests` drives, re-declared.
let private durableCases: StagingCase list =
    [ case
          durableOrigin
          "the refresh handler — one arm of every capability"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, limitTwo))
            Compute(Action.SetState("rows", Some(jstr "2 rows"), None))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "refresh") ])
            Effect(ServerEffect.HostCall("audit", jstr "refreshed", None))
            Effect(ServerEffect.EmitPatch [ TreeOp.RemoveNode(NodeId "readout") ])
            Effect(ServerEffect.Notify("audit", jstr "refreshed")) ]
      case
          durableOrigin
          "two host calls, both landing"
          [ "alpha"; "beta" ]
          durableStore
          [ Effect(ServerEffect.HostCall("alpha", jstr "one", Some "first"))
            Effect(ServerEffect.HostCall("beta", jstr "two", Some "second")) ]
      case
          durableOrigin
          "the same function staged twice"
          [ "boom" ]
          durableStore
          [ Effect(ServerEffect.HostCall("boom", jstr "one", Some "first"))
            Effect(ServerEffect.HostCall("boom", jstr "two", Some "second")) ]
      case
          durableOrigin
          "every effect arm once, with the host call in the middle"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, []))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "readout") ])
            Effect(ServerEffect.HostCall("audit", jstr "a", None))
            Effect(ServerEffect.EmitPatch [])
            Effect(ServerEffect.Notify("audit", jstr "a")) ] ]

/// The plan-halt arms the two suites' staging cases do not reach, so every
/// `halt` and `deny` in the model is exercised against production's.
let private planHaltCases: StagingCase list =
    [ case
          "plan-halt"
          "a host call naming no registered performer"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.HostCall("missing", jstr "note", None)) ]
      case
          "plan-halt"
          "a host call whose arguments the declared policy refuses"
          [ "fetch" ]
          loopStore
          [ Effect(ServerEffect.HostCall("fetch", jstr (String.replicate 64 "x"), None)) ]
      |> shaped (ServerEffectRegistry.constrain "host:fetch" [ ServerConstraintClause.Ceiling 16 ])
      case
          "plan-halt"
          "a host call landing under the host-reserved namespace"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", Some "first"))
            Effect(ServerEffect.HostCall("audit", jstr "note", Some(witness.Dispatch.Store.ReservedPrefix + "x"))) ]
      case
          "plan-halt"
          "an op the apply engine refuses, after a host call was staged"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "refresh"); TreeOp.RemoveNode(NodeId "absent") ]) ]
      case
          "plan-halt"
          "a query the evaluator refuses, after a host call was staged"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Ref "nowhere", [])) ] ]

/// The cases that register an OP PERFORMER (Phase 1967): `ApplyOps` is then a
/// staged arm, performed after the plan commits, one call per op in plan
/// order beside the host calls — so the same scripted performer, the same
/// positions and the same three checks cover it. The first case is the same
/// handler run in memory beside it, so the two placements of one handler are
/// compared under one roof.
let private opCases: StagingCase list =
    [ case
          "op-performer"
          "ops and a host call, in memory: nothing but the host call runs outside"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "refresh") ])
            Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "readout") ]) ]
      case
          "op-performer"
          "ops and a host call, performed: each op is its own staged call, in plan order"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "refresh") ])
            Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "readout") ]) ]
      |> performingOps
      case
          "op-performer"
          "two ops in one stage, then a host call landing"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "refresh"); TreeOp.RemoveNode(NodeId "readout") ])
            Compute(Action.SetState("status", Some(jstr "planned"), None))
            Effect(ServerEffect.HostCall("audit", jstr "note", Some "audited")) ]
      |> performingOps
      case
          "op-performer"
          "a plan that halts after ops were staged performs none of them"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "refresh") ])
            Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "absent") ]) ]
      |> performingOps
      case
          "op-performer"
          "an op sequence the policy refuses is refused before anything performs"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "refresh") ]) ]
      |> performingOps
      |> shaped (
          ServerEffectRegistry.constrain "ApplyOps" [ ServerConstraintClause.AllowList("target", [ "readout" ]) ]
      ) ]

/// A style no node in the corpus carries, so a guard that wrote it would leave
/// a visible trace — the guard clause's "moves nothing" is then observable.
let private guardStyle =
    { Defaults.style with
        Tone = ToneVariant.Critical }

/// The cases with an OP-CHANNEL GUARD (Phase 1974): `UpdateStyle` names a
/// node the plan must hold at that point. A guard that holds stages nothing
/// and moves nothing; a guard that refuses after an edit removed what it
/// checks refuses on the PLAN; and under a performer every edit is handed the
/// state it produced — compared across the two sides by `Handed`.
let private guardCases: StagingCase list =
    [ case
          "op-guard"
          "a guard that holds, performed: the edits are staged, the guard is not"
          [ "audit" ]
          durableStore
          [ Effect(
                ServerEffect.ApplyOps
                    [ TreeOp.RemoveNode(NodeId "refresh")
                      TreeOp.UpdateStyle(NodeId "readout", guardStyle) ]
            )
            Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "readout") ]) ]
      |> performingOps
      |> guarded
      case
          "op-guard"
          "a guard that holds, in memory: the state does not move for it"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ TreeOp.UpdateStyle(NodeId "readout", guardStyle) ])
            Effect(ServerEffect.HostCall("audit", jstr "note", None)) ]
      |> guarded
      case
          "op-guard"
          "a guard after an edit that removed what it checks refuses on the plan"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.HostCall("audit", jstr "note", None))
            Effect(
                ServerEffect.ApplyOps
                    [ TreeOp.RemoveNode(NodeId "readout")
                      TreeOp.UpdateStyle(NodeId "readout", guardStyle) ]
            ) ]
      |> performingOps
      |> guarded ]

/// The failure positions a case is run at: every position of its staged
/// list, and none. Counted over the `HostCall` stages and, under a registered
/// op performer, every op of every `ApplyOps` stage — a position past what the
/// plan phase actually staged is simply never reached, and the run is then
/// the all-succeed run again, compared all the same.
let private positions (case: StagingCase) : int option list =
    let staged =
        case.Handler.Stages
        |> List.sumBy (fun stage ->
            match stage with
            | Effect(ServerEffect.HostCall _) -> 1
            | Effect(ServerEffect.ApplyOps ops) when case.PerformOps -> List.length ops
            | _ -> 0)

    None :: [ for k in 0 .. staged - 1 -> Some k ]

let private divergences (cases: StagingCase list) : string list =
    [ for c in cases do
          for failAt in positions c do
              match divergence c failAt (runCase c failAt) with
              | Some report -> report
              | None -> () ]

// ─── The tests ──────────────────────────────────────────────────────────────

[<Tests>]
let stagingTests =
    testList
        "Phase 1717 - the proved staging as oracle"
        [ test "the corpus stages something, and fails at every position of a staged list" {
              // A corpus whose every run committed — or whose every run halted
              // while planning — would report the same green while exercising
              // none of the perform phase. The floor is that the corpus reaches
              // the perform phase, reaches a failure INSIDE it at more than one
              // position, and reaches the all-succeed run.
              let all = loopCases @ durableCases @ planHaltCases @ opCases @ guardCases

              let verdicts =
                  [ for c in all do
                        for failAt in positions c do
                            let run = runCase c failAt

                            yield
                                {| Committed = run.Production.Committed
                                   PerformedOutside = List.length (claimedExternal c run.Production)
                                   Asked = List.length run.ProductionLog |} ]

              Expect.isNonEmpty all "the corpus is empty"
              Expect.isTrue (verdicts |> List.exists (fun v -> v.Committed)) "no run committed"

              Expect.isTrue
                  (verdicts |> List.exists (fun v -> not v.Committed && v.Asked > 0))
                  "no run reached the perform phase and failed there"

              Expect.isTrue
                  (verdicts |> List.exists (fun v -> not v.Committed && v.PerformedOutside >= 2))
                  "no run left a residual of two or more host calls — the prefix is never longer than one"

              Expect.isTrue
                  (verdicts |> List.exists (fun v -> not v.Committed && v.Asked = 0))
                  "no run halted while planning"
          }

          test "the oracle agrees with production on the HandlerLoopTests staging cases at every failure position" {
              Expect.isEmpty (divergences loopCases) "the extracted model and production diverged"
          }

          test
              "the oracle agrees with production on the DurableInterpreterTests staging cases at every failure position" {
              Expect.isEmpty (divergences durableCases) "the extracted model and production diverged"
          }

          test "the oracle agrees with production when ops are PERFORMED after the plan, at every failure position" {
              // Phase 1967: the staged list holds ops as well as host calls,
              // and the same three checks cover them — the outcomes agree, the
              // performers were asked the same things in the same order, and
              // `Performed`'s claim is the log.
              Expect.isEmpty (divergences opCases) "the extracted model and production diverged"

              let performed = opCases |> List.filter _.PerformOps
              Expect.isGreaterThanOrEqual (List.length performed) 3 "the corpus registers an op performer"

              // And the two placements of ONE handler differ exactly as D19
              // says: in memory `ApplyOps` is performed at plan time and the
              // log holds the host call alone; performed, each op is a staged
              // call the log names, and `Performed` is execution order.
              let inMemory = runCase opCases.Head None
              let performedRun = runCase opCases.[1] None
              Expect.isTrue inMemory.Production.Committed "in memory: committed"
              Expect.equal inMemory.ProductionLog [ "host:audit" ] "in memory: only the host call ran outside"

              Expect.equal
                  inMemory.Production.Performed
                  [ "ApplyOps"; "ApplyOps"; "host:audit" ]
                  "in memory: plan-phase applies, then the staged host call"

              Expect.isTrue performedRun.Production.Committed "performed: committed"

              Expect.equal
                  performedRun.ProductionLog
                  [ "ApplyOps"; "host:audit"; "ApplyOps" ]
                  "performed: each op is a staged call, in plan order beside the host call"

              Expect.equal
                  performedRun.Production.Performed
                  performedRun.ProductionLog
                  "performed: `Performed` is exactly the log — execution order, nothing at plan time"

              // A part-way failure: the op after the host call refuses, and the
              // residual is the prefix that ran — a positioned `PerformFailed`
              // under the op's capability, the entry tree, nothing else.
              let partWay = runCase opCases.[1] (Some 2)
              Expect.isFalse partWay.Production.Committed "part-way: rolled back"
              Expect.equal partWay.Production.Performed [ "ApplyOps"; "host:audit" ] "part-way: the prefix that ran"

              Expect.equal
                  (partWay.Production.Diagnostics |> List.last)
                  (ServerDiagnostic.PerformFailed("ApplyOps", "scripted refusal at position 2"))
                  "part-way: the failure names the op's capability and the performer's reason"

              Expect.isTrue
                  (LanguagePrimitives.PhysicalEquality partWay.Production.Store.Tree durableStore.Tree)
                  "part-way: the planned tree is discarded"

              // The policy refusal and the apply halt reach no performer at all.
              for c in
                  opCases
                  |> List.filter (fun c -> c.PerformOps && c.Name.Contains "refused" || c.Name.Contains "halts") do
                  let run = runCase c None
                  Expect.isFalse run.Production.Committed (sprintf "%s: committed" c.Name)
                  Expect.isEmpty run.ProductionLog (sprintf "%s: a performer ran" c.Name)
                  Expect.isEmpty run.Production.Performed (sprintf "%s: something is reported performed" c.Name)
          }

          test "the oracle agrees with production on the OP-CHANNEL GUARD and the performer's state (Phase 1974)" {
              Expect.isEmpty (divergences guardCases) "the extracted model and production diverged"

              // A guard that holds is never staged: the log holds the two
              // edits and the host call, never the guard.
              let holds = runCase guardCases.[0] None
              Expect.isTrue holds.Production.Committed "holds: committed"

              Expect.equal
                  holds.ProductionLog
                  [ "ApplyOps"; "host:audit"; "ApplyOps" ]
                  "holds: two edits and the host call — the guard was not staged"

              // The performer is handed the state each edit PRODUCED: the
              // first with `refresh` gone, the last with both gone — which is
              // the committed tree.
              Expect.equal (List.length holds.ProductionHanded) 2 "holds: one handed state per edit"

              Expect.equal
                  (List.last holds.ProductionHanded)
                  (Fuaran.UI.OpStream.Abstractions.CanonicalJson.encodeNode holds.Production.Store.Tree)
                  "holds: the last state handed is the committed plan"

              // In memory the guard moves nothing: the tree is the entry tree.
              let inMemory = runCase guardCases.[1] None
              Expect.isTrue inMemory.Production.Committed "in memory: committed"

              Expect.equal
                  (Fuaran.UI.OpStream.Abstractions.CanonicalJson.encodeNode inMemory.Production.Store.Tree)
                  (Fuaran.UI.OpStream.Abstractions.CanonicalJson.encodeNode durableStore.Tree)
                  "in memory: the style the guard would have set is discarded"

              // A guard over the PLAN: the edit before it removed the node it
              // names, so it refuses — and nothing reaches any performer.
              let refuses = runCase guardCases.[2] None
              Expect.isFalse refuses.Production.Committed "refuses: halted"
              Expect.isEmpty refuses.ProductionLog "refuses: no performer ran"

              match refuses.Production.Diagnostics |> List.last with
              | ServerDiagnostic.Failed("ApplyOps", _) -> ()
              | other -> failtestf "refuses: expected the op channel's halt, got %A" other
          }

          test "the oracle agrees with production on every plan-phase halt — gate, policy, lookup, slot, apply, query" {
              Expect.isEmpty (divergences planHaltCases) "the extracted model and production diverged"

              // And they are plan-phase halts: nothing was asked of any performer.
              for c in planHaltCases do
                  let run = runCase c None
                  Expect.isFalse run.Production.Committed (sprintf "%s: the handler committed" c.Name)
                  Expect.isEmpty run.ProductionLog (sprintf "%s: a performer ran during a plan-phase halt" c.Name)
                  Expect.isEmpty run.Production.Performed (sprintf "%s: something is reported performed" c.Name)
          }

          test "the residual is the prefix that ran, in order, and the store is the entry store" {
              // The law, against production and the performer's log directly —
              // the differential above says the model agrees with production;
              // this says what they agree ON is D8. At every failure position
              // INSIDE the perform phase: uncommitted, the entry tree by
              // reference, the entry state, no patches / notifications /
              // effects, and `Performed` exactly the first k staged calls in
              // declaration order — which is the log, minus the refused call.
              let reached =
                  [ for c in loopCases @ durableCases @ opCases do
                        for failAt in positions c do
                            match failAt with
                            | Some k ->
                                let run = runCase c failAt

                                if k < List.length run.ProductionLog then
                                    yield c, k, run
                            | None -> () ]

              Expect.isTrue (List.length reached >= 3) "fewer than three runs failed inside the perform phase"

              for c, k, run in reached do
                  let where = sprintf "%s (%s), failure at %d" c.Name c.Origin k
                  let outcome = run.Production

                  Expect.isFalse outcome.Committed (sprintf "%s: committed past a perform-phase failure" where)

                  Expect.isTrue
                      (LanguagePrimitives.PhysicalEquality outcome.Store.Tree c.Store.Tree)
                      (sprintf "%s: the tree is not the entry tree" where)

                  Expect.equal outcome.Store.Bindings.State c.Store.Bindings.State (sprintf "%s: the state moved" where)
                  Expect.isEmpty outcome.Patches (sprintf "%s: patches survived a rollback" where)
                  Expect.isEmpty outcome.Notifications (sprintf "%s: notifications survived a rollback" where)
                  Expect.isEmpty outcome.ClientEffects (sprintf "%s: client effects survived a rollback" where)

                  Expect.equal
                      outcome.Performed
                      (ranCapabilities (List.take k run.ProductionLog))
                      (sprintf "%s: Performed is not exactly the first %d staged calls, in order" where k)

                  Expect.equal
                      (List.length run.ProductionLog)
                      (k + 1)
                      (sprintf "%s: the perform phase did not stop at the refused call" where)
          }

          test "GO RED: a performer that reports success for a call it did not run loses the comparison" {
              // The honest run first: the case is one production and the oracle
              // agree on, and one whose claim the log corroborates, so what the
              // lying performer loses is the lie and not the fixture.
              let c =
                  loopCases |> List.find (fun c -> c.Functions = [ "first"; "second"; "third" ])

              Expect.isNone
                  (divergence c None (runCase c None))
                  "production, the oracle and the log disagree on the very case the defect is committed against"

              // Then the defect. `second` answers Ok WITHOUT running — nothing
              // logged, a result returned — on both sides, so the two outcomes
              // still agree with each other and both claim `host:second`
              // performed. The check that has to catch it is the third: the
              // claim against the log. If it did not, every green above would
              // be a comparison of two claims with nothing behind them.
              let lying (log: Log) : string -> Performer =
                  let honest = (scripted log None).Host

                  fun name ->
                      if name = "second" then
                          (fun _ -> Ok(jstr "ran:second"))
                      else
                          honest name

              let productionLog = Log()
              let modelLog = Log()

              let run =
                  { Production = Handler.run (registryFor c (lying productionLog)) c.Resolve "call" c.Handler c.Store
                    ProductionLog = List.ofSeq productionLog
                    ProductionHanded = []
                    Model =
                      runModel
                          witness
                          OpPerformance.InMemory
                          (registryFor c (lying modelLog))
                          c.Resolve
                          "call"
                          c.Handler
                          c.Store
                    ModelLog = List.ofSeq modelLog
                    ModelHanded = [] }

              Expect.equal
                  (projectionOf run.Production)
                  (projectionOf run.Model)
                  "the lie is not a divergence between production and the model — both believed it"

              Expect.equal
                  (claimedExternal c run.Production)
                  [ "host:first"; "host:second"; "host:third" ]
                  "the handler claims all three ran — the lie was believed"

              Expect.equal run.ProductionLog [ "host:first"; "host:third" ] "the log says two ran — the lie was not"

              match divergence c None run with
              | Some report ->
                  Expect.stringContains
                      report
                      "Performed claims"
                      "the harness reported a divergence, but not the claim-versus-log one"
              | None ->
                  failtest
                      "the comparison harness did not report a performer that lied — a harness that cannot lose is not evidence"
          } ]

// ═══════════════════════════════════════════════════════════════════════════
//  Phase 1759 — the proved effect gate as oracle.
//
//  `proofs/EffectGate.fst` proves, OVER the staging model above, that the gate
//  is consulted before any performer (`gate_before_perform`), that a stateless
//  gate is sufficient for every policy inductive under it (`policy_sufficient`)
//  and that a return contract checks a performer's result before it reaches
//  the store (`return_contract`). The runtime piece it adds is one wrapper,
//  `check_return`, which is `ReturnContract.check` in production; the host
//  below builds the model's registry so that the model wraps the RAW
//  performer with the EXTRACTED wrapper while production wraps it with its
//  own, and compares the two over the `ServerEffectTests` registry shapes and
//  generated (capability, gate, performer) triples: the outcomes, the
//  performers' logs, the DENIAL STREAM (production's `OnDenied` sink against
//  both sides' `Denied` diagnostics) and the ORDER of events — every performer
//  invocation preceded by the gate's decision on its capability.
// ═══════════════════════════════════════════════════════════════════════════

/// The model's performer token for this host: the function NAME and the raw
/// closure, so `checked_by` can key a contract by the name the host declared
/// it under — which is how `registerChecked` keys it.
type private GateToken = string * Performer

type private GateModelRegistry = Staging.registry<Node<obj>, Fuaran.Core.JVal, TreeOp<obj>, Query, GateToken>

type private GateAccumulator =
    Staging.accumulator<
        Node<obj>,
        BindingSources,
        Fuaran.Core.JVal,
        TreeOp<obj>,
        ClientEffect,
        BoundedDiagnostic,
        GateToken
     >

/// What happened, in order: `gate:<capability>` when the gate was asked,
/// `perform:<fn>` when a performer was invoked, `deny:<capability>` when the
/// sink fired. The ground truth `gate_before_perform` is checked against.
type private Events = System.Collections.Generic.List<string>

/// The three performer behaviours a triple ranges over.
type private Behaviour =
    /// Answers a string — the value the `text` contract admits.
    | Answers
    /// Answers a number — admitted by no contract, so a checked function
    /// refuses it and an unchecked one lands it.
    | AnswersWrongly
    /// Refuses outright, with the host's own reason.
    | Refuses

let private behave (events: Events) (behaviour: Behaviour) : string -> Performer =
    fun name ->
        fun _ ->
            events.Add("perform:" + name)

            match behaviour with
            | Answers -> Ok(jstr ("ran:" + name))
            | AnswersWrongly -> Ok(Fuaran.Core.JInt 1)
            | Refuses -> Error("host refused " + name)

/// The one contract the corpus declares, on `audit` only: the result is text.
let private textContract: ReturnContract =
    { Name = "text"
      Holds =
        fun value ->
            match value with
            | Fuaran.Core.JStr _ -> true
            | _ -> false }

/// The functions a triple registers: `audit` behind the contract, `raw`
/// without one. `missing` is never registered and is what the lookup misses.
let private checkedFunctions = [ "audit", Some textContract; "raw", None ]

/// A gate SHAPE — the gate itself plus the argument policy a case narrows
/// with, applied to both sides alike.
type private GateShape =
    { Label: string
      Gate: string -> bool
      Narrow: ServerEffectRegistry -> ServerEffectRegistry }

let private gateShapes: GateShape list =
    [ { Label = "deny-all"
        Gate = (fun _ -> false)
        Narrow = id }
      { Label = "permissive"
        Gate = (fun _ -> true)
        Narrow = id }
      { Label = "reads-only"
        Gate = (fun cap -> cap = "RunQuery")
        Narrow = id }
      { Label = "host-only"
        Gate = (fun cap -> cap.StartsWith("host:", StringComparison.Ordinal))
        Narrow = id }
      { Label = "an-exact-set"
        Gate = (fun cap -> List.contains cap [ "ApplyOps"; "host:audit"; "Notify" ])
        Narrow = id }
      { Label = "permissive, with the notification channel allow-listed elsewhere"
        Gate = (fun _ -> true)
        Narrow = ServerEffectRegistry.constrain "Notify" [ ServerConstraintClause.AllowList("channel", [ "other" ]) ] }
      { Label = "permissive, with a four-byte ceiling on audit"
        Gate = (fun _ -> true)
        Narrow = ServerEffectRegistry.constrain "host:audit" [ ServerConstraintClause.Ceiling 4 ] } ]

/// The effects a triple ranges over — one per capability the corpus can name,
/// including the unregistered host function.
let private effects: (string * ServerEffect<TreeOp<obj>>) list =
    [ "RunQuery", ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, limitTwo)
      "ApplyOps", ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "call") ]
      "host:audit", ServerEffect.HostCall("audit", jstr "note", Some "audited")
      "host:raw", ServerEffect.HostCall("raw", jstr "note", Some "rawed")
      "host:missing", ServerEffect.HostCall("missing", jstr "note", None)
      "EmitPatch", ServerEffect.EmitPatch [ TreeOp.RemoveNode(NodeId "call") ]
      "Notify", ServerEffect.Notify("channel", jstr "note") ]

/// The programs a triple's effect is run in: alone, after a checked host
/// call, and after an unchecked and a checked host call.
let private programs (effect: ServerEffect<TreeOp<obj>>) : (string * HandlerStage list) list =
    [ "alone", [ Effect effect ]
      "after audit",
      [ Effect(ServerEffect.HostCall("audit", jstr "first", Some "first"))
        Effect effect ]
      "after raw and audit",
      [ Effect(ServerEffect.HostCall("raw", jstr "one", None))
        Effect(ServerEffect.HostCall("audit", jstr "two", Some "second"))
        Effect effect ] ]

/// A (capability, gate, performer) triple, as a runnable case.
type private Triple =
    { Capability: string
      Program: string
      Stages: HandlerStage list
      Shape: GateShape
      Behaviour: Behaviour }

let private triples: Triple list =
    [ for capability, effect in effects do
          for program, stages in programs effect do
              for shape in gateShapes do
                  for behaviour in [ Answers; AnswersWrongly; Refuses ] do
                      { Capability = capability
                        Program = program
                        Stages = stages
                        Shape = shape
                        Behaviour = behaviour } ]

/// Production's registry for a triple: the contract-bearing functions through
/// `registerChecked`, the rest through `register`; the gate and the sink both
/// logging to the event list.
let private productionRegistry (events: Events) (triple: Triple) : ServerEffectRegistry =
    let performer = behave events triple.Behaviour

    checkedFunctions
    |> List.fold
        (fun r (fn, contract) ->
            match contract with
            | Some c -> ServerEffectRegistry.registerChecked fn c (performer fn) r
            | None -> ServerEffectRegistry.register fn (performer fn) r)
        ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.withGate (fun cap ->
        events.Add("gate:" + cap)
        triple.Shape.Gate cap)
    |> triple.Shape.Narrow
    |> ServerEffectRegistry.onDenied (fun denial ->
        match denial with
        | ServerEffectDenial.Unregistered cap -> events.Add("deny:unregistered:" + cap)
        | ServerEffectDenial.GateRefused cap -> events.Add("deny:gate:" + cap))

/// The model's registry for the same triple: the gate and the argument policy
/// are production's own members (the assumed rung, as above); the lookup
/// answers the RAW closure as a token, and the behaviour is the EXTRACTED
/// `checked_by` over it — so what the model wraps with is the model's.
let private gateModelRegistry (events: Events) (triple: Triple) : GateModelRegistry =
    let performer = behave events triple.Behaviour

    let raw =
        checkedFunctions |> List.map (fun (fn, _) -> fn, performer fn) |> Map.ofList

    let contracts =
        checkedFunctions
        |> List.choose (fun (fn, contract) -> contract |> Option.map (fun c -> fn, c))
        |> Map.ofList

    // Production's gate and policy, read through the staging bridge above —
    // the registry it is given has no performers, which is fine, because the
    // bridge is only consulted for `r_gate` and `r_policy` here.
    let policyBearer =
        ServerEffectRegistry.denyAll
        |> ServerEffectRegistry.withGate (fun cap ->
            events.Add("gate:" + cap)
            triple.Shape.Gate cap)
        |> triple.Shape.Narrow
        |> modelRegistry OpPerformance.InMemory

    let contractOf ((fn, _): GateToken) : Staging.opt<EffectGate.contract<Fuaran.Core.JVal>> =
        contracts
        |> Map.tryFind fn
        |> Option.map (fun c ->
            { EffectGate.ct_name = c.Name
              EffectGate.ct_holds = c.Holds })
        |> modelOpt

    let rawBehaviour ((_, perf): GateToken) (args: Fuaran.Core.JVal) : Staging.res<Fuaran.Core.JVal> =
        perf args |> modelRes

    { r_gate = policyBearer.r_gate
      r_policy = policyBearer.r_policy
      r_lookup = fun fn -> raw |> Map.tryFind fn |> Option.map (fun perf -> fn, perf) |> modelOpt
      r_perf = EffectGate.checked_by contractOf rawBehaviour
      r_op_perform = Staging.ONone }

let private handlerOf (triple: Triple) : Handler =
    { Name = triple.Capability
      Stages = triple.Stages }

let private modelStore: Staging.store<Node<obj>, BindingSources> =
    { st_tree = loopStore.Tree
      st_bindings = loopStore.Bindings }

type private GateRun =
    { Production: HandlerOutcome
      ProductionEvents: string list
      Model: HandlerOutcome
      ModelEvents: string list }

/// One triple on both sides — production, and the model through `run`, which
/// is the extraction's `Staging.run` unless a mutant is handed in.
let private runTripleWith
    (run:
        ModelWitness
            -> GateModelRegistry
            -> string
            -> ModelStage list
            -> Staging.store<Node<obj>, BindingSources>
            -> HandlerOutcome)
    (triple: Triple)
    : GateRun =
    let productionEvents = Events()
    let modelEvents = Events()

    let production =
        Handler.run
            (productionRegistry productionEvents triple)
            Fuaran.Core.DataFrame.noResolve
            "call"
            (handlerOf triple)
            loopStore

    let model =
        run
            (modelWitness witness Fuaran.Core.DataFrame.noResolve)
            (gateModelRegistry modelEvents triple)
            "call"
            (triple.Stages |> List.map modelStage)
            modelStore

    { Production = production
      ProductionEvents = List.ofSeq productionEvents
      Model = model
      ModelEvents = List.ofSeq modelEvents }

let private runTriple (triple: Triple) : GateRun =
    runTripleWith (fun w reg nodeId stages store -> Staging.run w reg nodeId stages store |> productionShaped) triple

/// The denials an outcome's diagnostics carry, in order, as the sink spells
/// them — so the sink's log and the diagnostics compare as one list.
let private deniedIn (outcome: HandlerOutcome) : string list =
    outcome.Diagnostics
    |> List.choose (fun d ->
        match d with
        | ServerDiagnostic.Denied(ServerEffectDenial.Unregistered cap) -> Some("deny:unregistered:" + cap)
        | ServerDiagnostic.Denied(ServerEffectDenial.GateRefused cap) -> Some("deny:gate:" + cap)
        | _ -> None)

let private sinkLog (events: string list) =
    events |> List.filter (fun e -> e.StartsWith("deny:", StringComparison.Ordinal))

let private performerLog (events: string list) =
    events
    |> List.filter (fun e -> e.StartsWith("perform:", StringComparison.Ordinal))

let private describeTriple (triple: Triple) =
    sprintf "%s, %s, gate %s, performer %A" triple.Capability triple.Program triple.Shape.Label triple.Behaviour

/// The whole comparison for one triple, as a reported divergence or nothing:
/// the outcomes project equal; the performers were asked the same things in
/// the same order; and the denial stream is one list three ways — production's
/// sink, production's diagnostics, the model's diagnostics.
let private gateDivergence (triple: Triple) (run: GateRun) : string option =
    let where = describeTriple triple
    let production = projectionOf run.Production
    let model = projectionOf run.Model

    if production <> model then
        Some(sprintf "%s: the outcomes differ\n  production: %A\n  model:      %A" where production model)
    elif performerLog run.ProductionEvents <> performerLog run.ModelEvents then
        Some(
            sprintf
                "%s: the performers were asked different things\n  production: %A\n  model:      %A"
                where
                (performerLog run.ProductionEvents)
                (performerLog run.ModelEvents)
        )
    elif sinkLog run.ProductionEvents <> deniedIn run.Production then
        Some(
            sprintf
                "%s: production's sink saw %A but its diagnostics carry %A"
                where
                (sinkLog run.ProductionEvents)
                (deniedIn run.Production)
        )
    elif deniedIn run.Production <> deniedIn run.Model then
        Some(
            sprintf
                "%s: the denial streams differ\n  production: %A\n  model:      %A"
                where
                (deniedIn run.Production)
                (deniedIn run.Model)
        )
    else
        None

/// `gate_before_perform`, read off production's event log: every performer
/// invocation is preceded by the gate's decision on that function's
/// capability.
let private performerPrecededByGate (events: string list) : string option =
    let indexed = events |> List.indexed

    indexed
    |> List.tryPick (fun (i, e) ->
        if e.StartsWith("perform:", StringComparison.Ordinal) then
            let capability = "host:" + e.Substring("perform:".Length)

            let gated = indexed |> List.exists (fun (j, g) -> j < i && g = "gate:" + capability)

            if gated then
                None
            else
                Some(sprintf "%s ran with no prior gate decision on %s: %A" e capability events)
        else
            None)

// ─── The ServerEffectTests registry shapes, re-declared ────────────────────

/// What `ServerEffectTests` pins about the registry, as triples: the default
/// refuses every kind; registration does not permit; permission does not
/// register; a host call is namespaced away from the built-in arms. Each is a
/// shape the generated corpus also reaches; naming them here is what ties the
/// differential to the suite the shard names.
let private registryShapeTriples: Triple list =
    let shape label gate =
        { Label = label
          Gate = gate
          Narrow = id }

    [ // the default registry refuses everything — every kind, under deny-all
      for capability, effect in effects do
          { Capability = capability
            Program = "alone"
            Stages = [ Effect effect ]
            Shape = shape "deny-all (the default)" (fun _ -> false)
            Behaviour = Answers }
      // registration does not permit: audit is registered, the gate refuses it
      { Capability = "host:audit"
        Program = "alone"
        Stages = [ Effect(ServerEffect.HostCall("audit", jstr "note", None)) ]
        Shape = shape "everything but host:audit" (fun cap -> cap <> "host:audit")
        Behaviour = Answers }
      // permission does not register: missing is permitted, nobody performs it
      { Capability = "host:missing"
        Program = "alone"
        Stages = [ Effect(ServerEffect.HostCall("missing", jstr "note", None)) ]
        Shape = shape "permissive" (fun _ -> true)
        Behaviour = Answers }
      // a host function named like a built-in arm is still host:<fn>
      { Capability = "host:ApplyOps"
        Program = "alone"
        Stages = [ Effect(ServerEffect.HostCall("ApplyOps", jstr "note", None)) ]
        Shape = shape "ApplyOps only" (fun cap -> cap = "ApplyOps")
        Behaviour = Answers } ]

// ─── The go-red mutant: lookup before the gate ──────────────────────────────

/// A model whose `HostCall` arm consults the performer LOOKUP before the gate
/// — the one reordering the shard names — with every other clause the
/// extraction's. Observable exactly when a host call is both unregistered and
/// refused: production and the honest model land `GateRefused`, this lands
/// `Unregistered`, because it asked the registry about the performer before
/// asking the gate whether it may.
let private lookupFirstPlanStage
    (w: ModelWitness)
    (reg: GateModelRegistry)
    (nodeId: string)
    (stage: ModelStage)
    (acc: GateAccumulator)
    : GateAccumulator =
    match stage with
    | Staging.SEffect(Staging.HostCall(fn, _, _) as effect) ->
        match reg.r_lookup fn with
        | Staging.ONone -> Staging.deny (Staging.Unregistered(Staging.capability effect)) acc
        | Staging.OSome _ -> Staging.plan_stage w reg nodeId stage acc
    | _ -> Staging.plan_stage w reg nodeId stage acc

let rec private lookupFirstPlan
    (w: ModelWitness)
    (reg: GateModelRegistry)
    (nodeId: string)
    (stages: ModelStage list)
    (acc: GateAccumulator)
    : GateAccumulator =
    match stages with
    | [] -> acc
    | stage :: rest ->
        lookupFirstPlan
            w
            reg
            nodeId
            rest
            (if acc.ac_halted then
                 acc
             else
                 lookupFirstPlanStage w reg nodeId stage acc)

/// `Staging.run` with the mutant plan phase; the phase boundary, the perform
/// phase and the two outcome constructors are the extraction's.
let private lookupFirstRun
    (w: ModelWitness)
    (reg: GateModelRegistry)
    (nodeId: string)
    (stages: ModelStage list)
    (store: Staging.store<Node<obj>, BindingSources>)
    : HandlerOutcome =
    let planned = lookupFirstPlan w reg nodeId stages (Staging.start store)

    let final =
        if planned.ac_halted then
            planned
        else
            Staging.perform w reg (Staging.rev planned.ac_staged) planned

    let outcome: ModelOutcome =
        if final.ac_halted then
            { oc_store = store
              oc_committed = false
              oc_performed = Staging.rev final.ac_externally
              oc_patches = []
              oc_notifications = []
              oc_client_effects = []
              oc_diagnostics = Staging.rev final.ac_diagnostics }
        else
            { oc_store = final.ac_store
              oc_committed = true
              oc_performed = Staging.app (Staging.rev final.ac_performed) (Staging.rev final.ac_externally)
              oc_patches = Staging.rev final.ac_patches
              oc_notifications = Staging.rev final.ac_notifications
              oc_client_effects = Staging.rev final.ac_client_effects
              oc_diagnostics = Staging.rev final.ac_diagnostics }

    productionShaped outcome

// ─── The tests ──────────────────────────────────────────────────────────────

[<Tests>]
let effectGateTests =
    testList
        "Phase 1759 - the proved effect gate as oracle"
        [ test
              "the corpus reaches every verdict - a gate refusal, a missing performer, a policy halt, a contract refusal, a raw refusal and a commit" {
              // A corpus whose every run committed, or whose every run was
              // refused at the gate, would compare two sides over none of the
              // clauses the theorems are about. The floor is that every arm
              // of the plan phase's decision and every verdict of the perform
              // phase is reached at least once.
              let runs = triples |> List.map (fun t -> t, runTriple t)

              let has (predicate: Triple -> GateRun -> bool) =
                  runs |> List.exists (fun (t, r) -> predicate t r)

              let hasDiagnostic (predicate: ServerDiagnostic -> bool) =
                  has (fun _ r -> r.Production.Diagnostics |> List.exists predicate)

              Expect.isTrue (List.length triples >= 100) "the generated corpus is smaller than a hundred triples"
              Expect.isTrue (has (fun _ r -> r.Production.Committed)) "no run committed"

              Expect.isTrue
                  (has (fun _ r -> deniedIn r.Production |> List.exists (fun d -> d.StartsWith "deny:gate:")))
                  "no run was refused at the gate"

              Expect.isTrue
                  (has (fun _ r ->
                      deniedIn r.Production
                      |> List.exists (fun d -> d.StartsWith "deny:unregistered:")))
                  "no run missed a performer"

              Expect.isTrue
                  (hasDiagnostic (fun d ->
                      match d with
                      | ServerDiagnostic.Failed(_, reason) ->
                          reason.StartsWith "argument-not-allowed:"
                          || reason.StartsWith "payload-over-ceiling:"
                      | _ -> false))
                  "no run halted on the argument policy"

              Expect.isTrue
                  (hasDiagnostic (fun d ->
                      match d with
                      | ServerDiagnostic.PerformFailed(_, reason) -> reason = ReturnContract.describe textContract
                      | _ -> false))
                  "no run had a result refused by the return contract"

              Expect.isTrue
                  (hasDiagnostic (fun d ->
                      match d with
                      | ServerDiagnostic.PerformFailed(_, reason) -> reason.StartsWith "host refused"
                      | _ -> false))
                  "no run had a performer refuse outright"

              Expect.isTrue
                  (has (fun t r ->
                      t.Capability = "host:raw"
                      && t.Behaviour = AnswersWrongly
                      && r.Production.Committed))
                  "no unchecked function landed the value the contract would have refused"
          }

          test "the oracle agrees with production on the ServerEffectTests registry shapes" {
              let divergences =
                  registryShapeTriples |> List.choose (fun t -> gateDivergence t (runTriple t))

              Expect.isEmpty divergences "the extracted model and production diverged"
          }

          test
              "the oracle agrees with production on every generated (capability, gate, performer) triple - outcome, denial stream, performed set" {
              let divergences = triples |> List.choose (fun t -> gateDivergence t (runTriple t))

              Expect.isEmpty divergences "the extracted model and production diverged"
          }

          test "the gate is consulted before any performer, and every performed capability is one the gate admitted" {
              // `gate_before_perform` and `policy_sufficient`, as instances
              // against production: the event log orders every invocation
              // after its gate decision, and `Performed` passes the extracted
              // `admitted` under the triple's own gate.
              for t in triples @ registryShapeTriples do
                  let run = runTriple t
                  let where = describeTriple t

                  match performerPrecededByGate run.ProductionEvents with
                  | Some report -> failtest (sprintf "%s: %s" where report)
                  | None -> ()

                  Expect.isTrue
                      (EffectGate.admitted t.Shape.Gate run.Production.Performed)
                      (sprintf
                          "%s: Performed carries a capability the gate did not admit: %A"
                          where
                          run.Production.Performed)

                  Expect.isTrue
                      (EffectGate.admitted t.Shape.Gate run.Model.Performed)
                      (sprintf "%s: the model's Performed carries a capability the gate did not admit" where)
          }

          test
              "a refused capability lands exactly one denial carrying the capability, and nothing after it is planned or performed" {
              // `gate_refusal_halts_run`: for every triple whose program's own
              // effect the gate refuses after the stages before it were
              // admitted — the outcome is uncommitted, the store is the entry
              // store, nothing is performed, and the diagnostics end with the
              // one GateRefused denial, which by its type carries the
              // capability and nothing else.
              let refused =
                  [ for t in triples do
                        let before =
                            t.Stages
                            |> List.take (List.length t.Stages - 1)
                            |> List.forall (fun s ->
                                match s with
                                | Effect e -> t.Shape.Gate(ServerEffect.capability e)
                                | Compute _ -> true)

                        if before && not (t.Shape.Gate t.Capability) then
                            yield t, runTriple t ]

              Expect.isTrue (List.length refused >= 20) "fewer than twenty triples end in a gate refusal"

              for t, run in refused do
                  let where = describeTriple t
                  let outcome = run.Production
                  Expect.isFalse outcome.Committed (sprintf "%s: committed past a refusal" where)

                  Expect.isTrue
                      (LanguagePrimitives.PhysicalEquality outcome.Store.Tree loopStore.Tree)
                      (sprintf "%s: the tree is not the entry tree" where)

                  Expect.isEmpty outcome.Performed (sprintf "%s: something is reported performed" where)
                  Expect.isEmpty (performerLog run.ProductionEvents) (sprintf "%s: a performer ran" where)

                  Expect.equal
                      (deniedIn outcome |> List.filter (fun d -> d.StartsWith "deny:gate:"))
                      [ "deny:gate:" + t.Capability ]
                      (sprintf "%s: not exactly one gate denial naming the capability" where)

                  match List.tryLast outcome.Diagnostics with
                  | Some(ServerDiagnostic.Denied(ServerEffectDenial.GateRefused cap)) ->
                      Expect.equal cap t.Capability (sprintf "%s: the denial names another capability" where)
                  | other -> failtest (sprintf "%s: the last diagnostic is %A, not the denial" where other)
          }

          test
              "a result the return contract rejects is a typed refusal naming the contract, the handler rolls back, and the store never sees it" {
              // `return_contract`: the checked function answering a value the
              // contract rejects, under a gate that admits it. Position k is
              // where `audit` sits among the staged calls; everything before
              // it ran, nothing after it did, the store is the entry store,
              // and the last diagnostic names the capability and the
              // contract's NAME — not the value.
              let rejected =
                  [ for t in triples do
                        if t.Behaviour = AnswersWrongly && t.Shape.Label = "permissive" then
                            let staged =
                                t.Stages
                                |> List.choose (fun s ->
                                    match s with
                                    | Effect(ServerEffect.HostCall(fn, _, _)) when fn <> "missing" -> Some fn
                                    | _ -> None)

                            // The theorem's hypothesis is that the plan completed: a
                            // program the plan phase halts — a missing performer
                            // after the checked call — never reaches the perform
                            // phase, and is the staging theorem's case, not this one.
                            let plans =
                                t.Stages
                                |> List.forall (fun s ->
                                    match s with
                                    | Effect(ServerEffect.HostCall("missing", _, _)) -> false
                                    | _ -> true)

                            match List.tryFindIndex ((=) "audit") staged with
                            | Some k when plans -> yield t, k, staged, runTriple t
                            | _ -> () ]

              Expect.isTrue (List.length rejected >= 3) "fewer than three triples reach a contract refusal"

              for t, k, staged, run in rejected do
                  let where = describeTriple t
                  let outcome = run.Production
                  Expect.isFalse outcome.Committed (sprintf "%s: committed past a contract refusal" where)

                  Expect.isTrue
                      (LanguagePrimitives.PhysicalEquality outcome.Store.Tree loopStore.Tree)
                      (sprintf "%s: the tree is not the entry tree" where)

                  Expect.equal
                      outcome.Store.Bindings.State
                      loopStore.Bindings.State
                      (sprintf "%s: the state moved" where)

                  Expect.equal
                      outcome.Performed
                      (staged |> List.take k |> List.map (fun fn -> "host:" + fn))
                      (sprintf "%s: Performed is not exactly the %d calls before the rejected one" where k)

                  Expect.equal
                      (performerLog run.ProductionEvents)
                      (staged |> List.take (k + 1) |> List.map (fun fn -> "perform:" + fn))
                      (sprintf "%s: the perform phase did not stop at the rejected result" where)

                  match List.tryLast outcome.Diagnostics with
                  | Some(ServerDiagnostic.PerformFailed(cap, reason)) ->
                      Expect.equal cap "host:audit" (sprintf "%s: the refusal names another capability" where)

                      Expect.equal
                          reason
                          "return-contract:text"
                          (sprintf "%s: the refusal is not the contract's name" where)
                  | other -> failtest (sprintf "%s: the last diagnostic is %A, not the typed refusal" where other)

                  Expect.isNone (gateDivergence t run) (sprintf "%s: the model disagrees" where)
          }

          test "GO RED: a model that looks up the performer before consulting the gate loses the differential" {
              // The honest run first: an unregistered function under a gate
              // that refuses it is a triple production and the extraction
              // agree on, so what the mutant loses is the ordering and not the
              // fixture. Then the mutant, which asks the lookup first and so
              // lands `Unregistered` where the gate's `GateRefused` should be.
              let t =
                  { Capability = "host:missing"
                    Program = "alone"
                    Stages = [ Effect(ServerEffect.HostCall("missing", jstr "note", None)) ]
                    Shape = gateShapes |> List.find (fun s -> s.Label = "deny-all")
                    Behaviour = Answers }

              Expect.isNone
                  (gateDivergence t (runTriple t))
                  "production and the oracle disagree on the very triple the mutant is run against"

              // And the mutant agrees whenever the gate admits — so it is the
              // ORDER it gets wrong, not the arm.
              let admitted =
                  { t with
                      Shape = gateShapes |> List.find (fun s -> s.Label = "permissive") }

              Expect.isNone
                  (gateDivergence admitted (runTripleWith lookupFirstRun admitted))
                  "the mutant diverges even where the ordering is unobservable"

              let run = runTripleWith lookupFirstRun t

              Expect.equal
                  (deniedIn run.Production)
                  [ "deny:gate:host:missing" ]
                  "production did not refuse at the gate"

              Expect.equal
                  (deniedIn run.Model)
                  [ "deny:unregistered:host:missing" ]
                  "the mutant did not report the lookup's denial"

              match gateDivergence t run with
              | Some report ->
                  Expect.stringContains report "differ" "the harness reported a divergence, but not the outcome one"
              | None ->
                  failtest
                      "the comparison harness did not report a model that consulted the lookup before the gate - a harness that cannot lose is not evidence"
          } ]
