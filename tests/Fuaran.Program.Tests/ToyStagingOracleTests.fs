module Fuaran.Program.Tests.ToyStagingOracleTests

// ============================================================================
//  fuaran#2017 — the differential hosts for the proved staging (Phase 1717),
//  the proved undo (Phase 1977) and the proved effect gate (Phase 1759, with
//  the op contract of Phases 1981 and 1984), at the TOY witness.
//
//  The three models (`proofs/oracle/Staging.fs`, `Undo.fs`, `EffectGate.fs`)
//  are generic in the tree, the bindings, the values, the ops, the queries,
//  the actions, the client effects and the fold's diagnostics. Until this
//  file, the only host that ran them beside production instantiated them at
//  the UI tier's types — so the evidence that the extractions ARE the code
//  was evidence about one witness, and it lived in a test project that
//  reaches the UI tier. Here the same comparisons run at the toy witness
//  (`ToyDomain`: node `ToyNode`, action `ToyAction`, store `ToyStore`, op
//  `ToyOp`, effect `ToyEffect`), in a project that reaches no UI type at all.
//
//  The structure is the UI host's, case for case: a translation layer that
//  builds the model's witness and registry from PRODUCTION's members (the
//  assumed rung), so that the only thing that can disagree with production is
//  the extraction; a scripted performer that fails at every position of a
//  staged list; and the three checks — outcomes, the performers' logs, and
//  the handler's claim about what ran outside against the log — every one of
//  which a GO RED case below shows losing.
//
//  Where the UI corpus leans on something the shipped toy witness does not
//  have, it is COMPOSED here over the toy's own types, never added to the
//  toy: an apply that can refuse (the toy's relabel is total), an op-channel
//  guard, and an undo classing some ops compensable and one-way. Each
//  composition is named and explained where it is declared.
// ============================================================================

open System
open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain

// ─── The toy witness, composed ──────────────────────────────────────────────

type private ToyWitness = FullWitness<ToyNode, ToyAction, ToyExpr, ToyStore, ToyOp, ToyEffect>
type private ToyHandler = Handler<ToyAction, ToyOp>
type private ToyStage = HandlerStage<ToyAction, ToyOp>
type private ToyServerStore = ServerStore<ToyNode, ToyStore>
type private ToyOutcome = HandlerOutcome<ToyNode, ToyStore, ToyOp, ToyEffect>

/// A label that CHECKS rather than writes: `Relabel(id, "=x")` asks that node
/// `id` holds the literal label `x`. Under the strict apply below it refuses
/// when the node holds anything else; applied as an edit it writes the label
/// `"=x"` itself, so an edit of this shape is visible in the tree and a guard
/// of this shape, whose answer is discarded, is not.
let private checks (label: string) =
    label.StartsWith("=", StringComparison.Ordinal)

let rec private find (id: string) (node: ToyNode) : ToyNode option =
    if node.Id = id then
        Some node
    else
        node.Children |> List.tryPick (find id)

/// The toy's relabel is TOTAL — it answers `Ok` for a node that is not in the
/// tree — so the shipped toy has no apply refusal, and the `ApplyOps` arm's
/// halt clause would be unreachable at it. This apply refuses a relabel of an
/// absent node and a check whose node does not hold the label it names, and
/// otherwise IS the toy's own apply. Every case below runs over it: the
/// halt clause is what several of the UI corpus's cases exist to reach.
let private strictApply (op: ToyOp) (tree: ToyNode) : Result<ToyNode, string> =
    match op with
    | Relabel(id, label) ->
        match find id tree with
        | None -> Error(sprintf "no node '%s' in the tree" id)
        | Some node when checks label && node.Label <> Const(JStr(label.Substring 1)) ->
            Error(sprintf "node '%s' does not hold '%s'" id (label.Substring 1))
        | Some _ -> ToyDomain.witness.State.Stream.Apply op tree

let private witness: ToyWitness =
    { ToyDomain.witness with
        State =
            { ToyDomain.witness.State with
                Stream =
                    { ToyDomain.witness.State.Stream with
                        Apply = strictApply } } }

/// The toy with an op-channel GUARD (Phase 1974): a CHECK relabel is viewed as
/// one. It resolves to a refusal exactly when its node does not hold the label
/// as planned, and the label it would have written is discarded. The toy
/// itself views every op as an edit; this composition exists so the
/// differential reaches the guard clause of the `ApplyOps` arm.
let private guardedWitness: ToyWitness =
    { witness with
        State =
            { witness.State with
                View =
                    fun op ->
                        match op with
                        | Relabel(_, label) when checks label -> OpView.Require
                        | _ -> OpView.Edit } }

/// The canonical bytes every comparison below reads a tree by. NOT the
/// witness's `Canonical`, which renders ids and children only — a relabel is
/// invisible to it — but the toy codec's full rendering, labels included, so
/// two trees that differ in any label compare unequal.
let private render (node: ToyNode) : string = ToyWire.renderNode node

// ─── Translation: production ⇄ the model ────────────────────────────────────

type private Query = DataSource * Fuaran.Compute.Transform list
type private Performer = JVal -> Result<JVal, string>

type private ModelWitness =
    Staging.witness<ToyNode, ToyStore, JVal, ToyOp, Query, ToyAction, ToyEffect, BoundedDiagnostic>

type private ModelRegistry = Staging.registry<ToyNode, JVal, ToyOp, Query, Performer>
type private ModelStage = Staging.stage<ToyAction, JVal, ToyOp, Query>
type private ModelStore = Staging.store<ToyNode, ToyStore>

type private ModelOutcome = Staging.outcome<ToyNode, ToyStore, JVal, ToyOp, ToyEffect, BoundedDiagnostic>

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
/// stopped being its name would surface here as a divergence rather than
/// being copied into agreement.
let private evalErrorKind (error: Fuaran.Compute.EvalError) : string =
    let case, _ =
        Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(error, typeof<Fuaran.Compute.EvalError>)

    case.Name

let private modelWitness
    (witness: ToyWitness)
    (resolve: string -> Result<Table, Fuaran.Compute.EvalError>)
    : ModelWitness =
    { w_compute =
        fun nodeId action bindings ->
            let outcome = BoundedActions.runInert witness nodeId action bindings

            { bo_store = outcome.Store
              bo_effects = outcome.Effects
              bo_diagnostics = outcome.Diagnostics }
      // What undoes a compute stage's binding writes (Phase 1977): the fold's
      // own reversal over the trace the traced run records — production's
      // `Undo.restoreBindings` step, built from the same `reverse` and
      // `runReversed` — or nothing when the action is outside the reversible
      // fragment or the trace is not restorable.
      w_undo_compute =
        fun nodeId action bindings ->
            let _, (), trace =
                BoundedActions.runTraced witness HandlerArm.inert nodeId action bindings ()

            if BoundedActions.reversible witness action && Trace.restorable trace then
                Staging.OSome(fun (b: ToyStore) ->
                    (BoundedActions.runReversed witness nodeId (BoundedActions.reverse witness action trace) b).Store)
            else
                Staging.ONone
      w_query =
        fun name (source, pipeline) bindings ->
            Fuaran.Compute.DataFrame.evalSource resolve source
            |> Result.bind (Fuaran.Compute.DataFrame.evalPipelineWith resolve pipeline)
            |> Result.map (fun table -> witness.Dispatch.Store.LandQuery name table bindings)
            |> Result.mapError evalErrorKind
            |> modelRes
      w_apply = fun op tree -> witness.State.Stream.Apply op tree |> modelRes
      // The production `View`, taken to exhaustion — the model's `w_op_view`.
      // The toy's op is only ever an edit or (under the guard composition) a
      // guard, but the translation is total, as the UI host's is, so a
      // composition that viewed a relabel as a branch, a repeat or an `Each`
      // would reach the model translated rather than refused.
      w_op_view =
        let rec view (op: ToyOp) : Staging.op_view<JVal, ToyOp> =
            match witness.State.View op with
            | OpView.Edit -> Staging.OEdit op
            | OpView.Require -> Staging.ORequire op
            | OpView.Choose(entry, whenTrue, whenFalse, exit) ->
                Staging.OChoose(entry, whenTrue |> List.map view, whenFalse |> List.map view, modelOpt exit)
            | OpView.Repeat(count, body) -> Staging.ORepeat(bigint count, body |> List.map view)
            | OpView.Each(Collection.Literal elements, placeholder, body) ->
                Staging.OEach(
                    elements
                    |> List.map (fun element -> body |> List.map (witness.State.Substitute placeholder element >> view))
                )
            // A collection the state holds (Phase 1991): the toy's state holds
            // none and no toy op views as one, so the translation is total
            // over an extent the toy never answers — `w_read_extent` below
            // answers none, and the model refuses the shape exactly as the
            // plan would refuse a collection the state does not hold. The
            // shape is exercised at the grid witness (`GridWitnessTests`).
            | OpView.Each(Collection.Stored(collection, ceiling), _, _) ->
                Staging.OEachOf(collection.Name, bigint (max ceiling 0), [], [])

        view
      // The toy's state holds no collection.
      w_read_extent = fun _ _ -> Staging.ONone
      w_assign = witness.Dispatch.Store.Assign
      // The landing-slot refusal as production renders it: the
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

let private modelEffect (effect: ServerEffect<ToyOp>) : Staging.server_effect<JVal, ToyOp, Query> =
    match effect with
    | ServerEffect.RunQuery(name, source, pipeline) -> Staging.RunQuery(name, (source, pipeline))
    | ServerEffect.ApplyOps ops -> Staging.ApplyOps ops
    | ServerEffect.HostCall(fn, args, into) -> Staging.HostCall(fn, args, modelOpt into)
    | ServerEffect.EmitPatch ops -> Staging.EmitPatch ops
    | ServerEffect.Notify(channel, payload) -> Staging.Notify(channel, payload)

let private modelStage (stage: ToyStage) : ModelStage =
    match stage with
    | Compute action -> Staging.SCompute action
    | Effect effect -> Staging.SEffect(modelEffect effect)

/// The registry, split as the model splits it: the LOOKUP answers the closure
/// as an opaque token, and the BEHAVIOUR applies it. The argument policy is
/// consulted on the production effect, through production's own check over
/// the toy's state axis, because the policy's vocabulary is production's.
let private modelRegistry
    (performance: OpPerformance<ToyNode, ToyOp>)
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
      // The op performer, split as the model splits it: the TOKEN is
      // production's own closure over the state as of the op and the op, the
      // argument inert — the shape production stages.
      r_op_perform =
        match performance with
        | OpPerformance.InMemory -> Staging.ONone
        // The prefix (Phase 2165) is outside the Staging model's token: the
        // performers this oracle stages read none of it, so the model's side
        // hands the entry prefix — `proofs.json`, `op-prefix-out-of-model`.
        | OpPerformance.Performed perform ->
            // The model's receipt is the DETAIL (Phase 2197, D43): the
            // performers this oracle stages name no typed write.
            Staging.OSome(fun state op ->
                (fun (_: JVal) -> perform (OpPrefix.atEntry state) state op |> Result.map _.Detail), JObj []) }

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
let private productionShaped (outcome: ModelOutcome) : ToyOutcome =
    { Store =
        { Tree = outcome.oc_store.st_tree
          Bindings = outcome.oc_store.st_bindings }
      Committed = outcome.oc_committed
      Performed = outcome.oc_performed
      Patches = outcome.oc_patches
      Notifications = outcome.oc_notifications
      ClientEffects = outcome.oc_client_effects
      Diagnostics = outcome.oc_diagnostics |> List.map productionDiagnostic
      // The model carries no flow decisions (DECISIONS.md D25), and the
      // projection below does not compare them.
      Flow = []
      // Nor any typed receipts (Phase 2197, D43): its performers return a
      // detail, and the projection below does not compare them.
      Receipts = [] }

/// The comparable projection: the tree by the toy codec's canonical bytes
/// (labels included), the store — whose one channel is where the toy lands a
/// query too, so the query slots are compared here rather than apart — the
/// verdict, the audit trail, the patches, the notifications, the effects by
/// the toy's shipped emitter, and the diagnostics.
let private projectionOf (outcome: ToyOutcome) =
    {| Tree = render outcome.Store.Tree
       State = outcome.Store.Bindings |> Map.map (fun _ v -> sprintf "%A" v)
       Committed = outcome.Committed
       Performed = outcome.Performed
       Patches = outcome.Patches |> List.map (sprintf "%A")
       Notifications = outcome.Notifications
       Effects = outcome.ClientEffects |> List.map ToyWire.encodeEffect
       Diagnostics = outcome.Diagnostics |> List.map (sprintf "%A") |}

let private runModel
    (witness: ToyWitness)
    (performance: OpPerformance<ToyNode, ToyOp>)
    (registry: ServerEffectRegistry)
    (resolve: string -> Result<Table, Fuaran.Compute.EvalError>)
    (nodeId: string)
    (handler: ToyHandler)
    (store: ToyServerStore)
    : ToyOutcome =
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
/// under a registered op performer. The ground truth every claim below is
/// checked against.
type private Log = System.Collections.Generic.List<string>

/// A scripted performer's two faces over ONE counter: the host functions, by
/// name, and the op performer. One counter, because the perform phase runs
/// ops and host calls through one loop in plan order, and a failure position
/// counts across both.
type private Scripted =
    {
        Host: string -> Performer
        Op: ToyNode -> ToyOp -> Result<JVal, string>
        /// The state the op performer was handed with each op, canonically
        /// rendered, in invocation order — compared across the two sides, so
        /// the model's `staged_from` and production's staged op agree on
        /// WHICH state travels with each op, not only on which op.
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

    { Host = fun name -> fun _ -> ask ("host:" + name) |> Result.map (fun () -> JStr(sprintf "ran:%s" name))
      Op =
        fun state _ ->
            handed.Add(render state)
            ask "ApplyOps" |> Result.map (fun () -> JObj [])
      Handed = handed }

type private StagingCase =
    {
        Name: string
        Origin: string
        Functions: string list
        Handler: ToyHandler
        Store: ToyServerStore
        /// Wraps the permissive, fully-registered registry — the gate and the
        /// constraints a case narrows with.
        Shape: ServerEffectRegistry -> ServerEffectRegistry
        Resolve: string -> Result<Table, Fuaran.Compute.EvalError>
        /// Whether this case registers the scripted OP performer, making
        /// `ApplyOps` a staged arm; `false` is in memory.
        PerformOps: bool
        /// Whether this case runs under the guard composition.
        Guarded: bool
    }

let private witnessOf (case: StagingCase) : ToyWitness =
    if case.Guarded then guardedWitness else witness

let private performanceOf (case: StagingCase) (s: Scripted) : OpPerformance<ToyNode, ToyOp> =
    if case.PerformOps then
        OpPerformance.performedWithDetail s.Op
    else
        OpPerformance.InMemory

let private registryFor (case: StagingCase) (performer: string -> Performer) : ServerEffectRegistry =
    case.Functions
    |> List.fold (fun r fn -> ServerEffectRegistry.register fn (performer fn) r) ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.permissive
    |> case.Shape

/// One run of one case, on both sides, at one failure position.
type private Run =
    { Production: ToyOutcome
      ProductionLog: string list
      ProductionHanded: string list
      Model: ToyOutcome
      ModelLog: string list
      ModelHanded: string list }

let private runCase (case: StagingCase) (failAt: int option) : Run =
    let productionLog = Log()
    let modelLog = Log()

    let productionScript = scripted productionLog failAt
    let modelScript = scripted modelLog failAt

    let production =
        Handler.runWith
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

/// `Performed`'s external suffix — what the handler CLAIMS ran outside: its
/// host calls, and, under a registered op performer, its ops. In memory an
/// `ApplyOps` entry is the plan phase's and is not a claim about the outside.
let private claimedExternal (case: StagingCase) (outcome: ToyOutcome) =
    outcome.Performed
    |> List.filter (fun c ->
        c.StartsWith("host:", StringComparison.Ordinal)
        || (case.PerformOps && c = "ApplyOps"))

/// The whole comparison, as a reported divergence or nothing. Three checks:
/// the two outcomes project equal; the two performers were asked the same
/// things in the same order (and the op performers handed the same states);
/// and production's claim about what ran outside is the performer's own log,
/// bar the refused call. The third is the one the go-red case defeats.
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

        if claimedExternal case run.Production <> ran then
            Some(
                sprintf
                    "%s: Performed claims %A outside, but the performer's log says %A ran"
                    where
                    (claimedExternal case run.Production)
                    ran
            )
        else
            None

// ─── The corpus ─────────────────────────────────────────────────────────────

let private rows: Table =
    { Table.empty with
        Columns =
            [ { Name = "n"
                Type = IntType
                Cells = [ Int 1; Int 2; Int 3 ] } ] }

let private limitTwo =
    [ Fuaran.Compute.Limit(Fuaran.Compute.Slot.Lit 2, Fuaran.Compute.Slot.Lit 0) ]

let private leaf (id: string) (label: string) : ToyNode =
    { Id = id
      Label = Const(JStr label)
      Handlers = []
      Children = [] }

/// The handler-loop tree: one node the handlers address — `call`, the toy
/// counterpart of the UI host's one button.
let private loopTree: ToyNode =
    { leaf "root" "root" with
        Children = [ leaf "call" "call" ] }

/// The durable tree: the two nodes the refresh handler addresses — `refresh`,
/// and `readout`, which holds the label `idle`.
let private durableTree: ToyNode =
    { leaf "root" "root" with
        Children = [ leaf "refresh" "refresh"; leaf "readout" "idle" ] }

let private loopStore: ToyServerStore =
    { Tree = loopTree
      Bindings = Map.empty }

let private durableStore: ToyServerStore =
    { Tree = durableTree
      Bindings = Map.empty }

/// The edits the corpus makes, in place of the UI host's node removals: each
/// moves the tree visibly, has an exact inverse at the toy, and names a node
/// that is there — except `absent`, which the strict apply refuses.
let private touch (id: string) = Relabel(id, id + "-touched")
let private absent = Relabel("absent", "never")

/// A CHECK of `readout`'s entry label: a guard under the guard composition,
/// and under the plain one an edit writing `=idle`.
let private readoutCheck = Relabel("readout", "=idle")

let private case
    (origin: string)
    (name: string)
    (functions: string list)
    (store: ToyServerStore)
    (stages: ToyStage list)
    =
    { Name = name
      Origin = origin
      Functions = functions
      Handler = { Name = name; Stages = stages }
      Store = store
      Shape = id
      Resolve = Fuaran.Compute.DataFrame.noResolve
      PerformOps = false
      Guarded = false }

let private shaped (shape: ServerEffectRegistry -> ServerEffectRegistry) (c: StagingCase) = { c with Shape = shape }
let private performingOps (c: StagingCase) = { c with PerformOps = true }
let private guarded (c: StagingCase) = { c with Guarded = true }

let private loopOrigin = "handler loop"
let private durableOrigin = "durable interpreter"

/// The handler loop's staging cases, at the toy: the shapes the UI host
/// re-declares from the handler loop's suite, with toy writes for its state
/// writes and relabels for its tree edits.
let private loopCases: StagingCase list =
    [ case
          loopOrigin
          "a query, a state write, a host call, a patch and a notification"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.RunQuery("rows", Embedded rows, limitTwo))
            Compute(Put("status", Some(JStr "written"), None))
            Effect(ServerEffect.HostCall("audit", JStr "note", Some "audited"))
            Effect(ServerEffect.EmitPatch [ touch "call" ])
            Effect(ServerEffect.Notify("channel", JStr "note")) ]
      case
          loopOrigin
          "execution order, not stage order"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.RunQuery("rows", Embedded rows, []))
            Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [])
            Effect(ServerEffect.EmitPatch [])
            Effect(ServerEffect.Notify("channel", JStr "note")) ]
      case
          loopOrigin
          "a host call landing in its declared slot"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", Some "audited")) ]
      case
          loopOrigin
          "three host calls"
          [ "first"; "second"; "third" ]
          loopStore
          [ Effect(ServerEffect.HostCall("first", JStr "a", Some "landed"))
            Effect(ServerEffect.HostCall("second", JStr "b", None))
            Effect(ServerEffect.HostCall("third", JStr "c", None)) ]
      case
          loopOrigin
          "a host call staged, then the plan fails"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", Some "audited"))
            Compute(Put("status", Some(JStr "written"), None))
            Effect(ServerEffect.ApplyOps [ absent ]) ]
      case
          loopOrigin
          "a later stage reading an earlier host call's result"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", Some "audited"))
            Compute(Put("echo", None, Some(Read "audited"))) ]
      case
          loopOrigin
          "the gate refuses the host call"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", None)) ]
      |> shaped (ServerEffectRegistry.withGate (fun _ -> false))
      case
          loopOrigin
          "an unresolvable query source"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.RunQuery("rows", Ref "elsewhere", []))
            Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.Notify("channel", JStr "never")) ] ]

/// The durable interpreter's staging cases, at the toy.
let private durableCases: StagingCase list =
    [ case
          durableOrigin
          "the refresh handler — one arm of every capability"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.RunQuery("rows", Embedded rows, limitTwo))
            Compute(Put("rows", Some(JStr "2 rows"), None))
            Effect(ServerEffect.ApplyOps [ touch "refresh" ])
            Effect(ServerEffect.HostCall("audit", JStr "refreshed", None))
            Effect(ServerEffect.EmitPatch [ touch "readout" ])
            Effect(ServerEffect.Notify("audit", JStr "refreshed")) ]
      case
          durableOrigin
          "two host calls, both landing"
          [ "alpha"; "beta" ]
          durableStore
          [ Effect(ServerEffect.HostCall("alpha", JStr "one", Some "first"))
            Effect(ServerEffect.HostCall("beta", JStr "two", Some "second")) ]
      case
          durableOrigin
          "the same function staged twice"
          [ "boom" ]
          durableStore
          [ Effect(ServerEffect.HostCall("boom", JStr "one", Some "first"))
            Effect(ServerEffect.HostCall("boom", JStr "two", Some "second")) ]
      case
          durableOrigin
          "every effect arm once, with the host call in the middle"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.RunQuery("rows", Embedded rows, []))
            Effect(ServerEffect.ApplyOps [ touch "readout" ])
            Effect(ServerEffect.HostCall("audit", JStr "a", None))
            Effect(ServerEffect.EmitPatch [])
            Effect(ServerEffect.Notify("audit", JStr "a")) ] ]

/// The plan-halt arms the two suites' staging cases do not reach, so every
/// `halt` and `deny` in the model is exercised against production's.
let private planHaltCases: StagingCase list =
    [ case
          "plan-halt"
          "a host call naming no registered performer"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.HostCall("missing", JStr "note", None)) ]
      case
          "plan-halt"
          "a host call whose arguments the declared policy refuses"
          [ "fetch" ]
          loopStore
          [ Effect(ServerEffect.HostCall("fetch", JStr(String.replicate 64 "x"), None)) ]
      |> shaped (ServerEffectRegistry.constrain "host:fetch" [ ServerConstraintClause.Ceiling 16 ])
      case
          "plan-halt"
          "a host call landing under the host-reserved namespace"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", Some "first"))
            Effect(ServerEffect.HostCall("audit", JStr "note", Some(witness.Dispatch.Store.ReservedPrefix + "x"))) ]
      case
          "plan-halt"
          "an op the apply engine refuses, after a host call was staged"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [ touch "refresh"; absent ]) ]
      case
          "plan-halt"
          "a query the evaluator refuses, after a host call was staged"
          [ "audit" ]
          loopStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.RunQuery("rows", Ref "nowhere", [])) ] ]

/// The cases that register an OP PERFORMER (Phase 1967). The first is the same
/// handler as the second run in memory, so the two placements of one handler
/// are compared under one roof.
let private opCases: StagingCase list =
    [ case
          "op-performer"
          "ops and a host call, in memory: nothing but the host call runs outside"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh" ])
            Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [ touch "readout" ]) ]
      case
          "op-performer"
          "ops and a host call, performed: each op is its own staged call, in plan order"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh" ])
            Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [ touch "readout" ]) ]
      |> performingOps
      case
          "op-performer"
          "two ops in one stage, then a host call landing"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh"; touch "readout" ])
            Compute(Put("status", Some(JStr "planned"), None))
            Effect(ServerEffect.HostCall("audit", JStr "note", Some "audited")) ]
      |> performingOps
      case
          "op-performer"
          "a plan that halts after ops were staged performs none of them"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh" ])
            Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [ absent ]) ]
      |> performingOps
      case
          "op-performer"
          "an op sequence the policy refuses is refused before anything performs"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [ touch "refresh" ]) ]
      |> performingOps
      |> shaped (
          ServerEffectRegistry.constrain "ApplyOps" [ ServerConstraintClause.AllowList("target", [ "readout" ]) ]
      ) ]

/// The cases with an OP-CHANNEL GUARD (Phase 1974): a check of `readout`'s
/// entry label. A guard that holds stages nothing and moves nothing; a guard
/// after an edit that changed what it checks refuses on the PLAN; and under a
/// performer every edit is handed the state it produced.
let private guardCases: StagingCase list =
    [ case
          "op-guard"
          "a guard that holds, performed: the edits are staged, the guard is not"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh"; readoutCheck ])
            Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [ touch "readout" ]) ]
      |> performingOps
      |> guarded
      case
          "op-guard"
          "a guard that holds, in memory: the state does not move for it"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ readoutCheck ])
            Effect(ServerEffect.HostCall("audit", JStr "note", None)) ]
      |> guarded
      case
          "op-guard"
          "a guard after an edit that changed what it checks refuses on the plan"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [ touch "readout"; readoutCheck ]) ]
      |> performingOps
      |> guarded ]

/// The failure positions a case is run at: every position of its staged list,
/// and none. A position past what the plan phase actually staged is never
/// reached, and the run is then the all-succeed run again, compared all the
/// same.
let private positions (case: StagingCase) : int option list =
    let staged =
        case.Handler.Stages
        |> List.sumBy (fun stage ->
            match stage with
            | Effect(ServerEffect.HostCall _) -> 1
            | Effect(ServerEffect.ApplyOps ops) when case.PerformOps -> List.length ops
            | _ -> 0)

    None :: [ for k in 0 .. staged - 1 -> Some k ]

/// A differential host's verdict: no divergences, and if there are some the
/// message names how many and shows the first.
let private expectNoDivergence (subject: string) (divergences: string list) =
    Expect.isEmpty
        divergences
        (sprintf
            "%s diverged on %d case(s); the first: %s"
            subject
            (List.length divergences)
            (divergences |> List.truncate 1 |> String.concat " | "))

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
        "Phase 1717 - the proved staging as oracle at the toy witness"
        [ test "the corpus stages something, and fails at every position of a staged list" {
              // A corpus whose every run committed — or whose every run halted
              // while planning — would report the same green while exercising
              // none of the perform phase.
              let all = loopCases @ durableCases @ planHaltCases @ opCases @ guardCases

              let verdicts =
                  [ for c in all do
                        for failAt in positions c do
                            let run = runCase c failAt

                            yield
                                {| Committed = run.Production.Committed
                                   PerformedOutside = List.length (claimedExternal c run.Production)
                                   Asked = List.length run.ProductionLog |} ]

              Expect.isGreaterThanOrEqual (List.length all) 25 "the corpus is smaller than the UI host's 25 cases"
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

          test "the oracle agrees with production on the handler loop's staging cases at every failure position" {
              expectNoDivergence "the extracted model and production" (divergences loopCases)
          }

          test "the oracle agrees with production on the durable interpreter's staging cases at every failure position" {
              expectNoDivergence "the extracted model and production" (divergences durableCases)
          }

          test "the oracle agrees with production when ops are PERFORMED after the plan, at every failure position" {
              expectNoDivergence "the extracted model and production" (divergences opCases)

              let performed = opCases |> List.filter _.PerformOps
              Expect.isGreaterThanOrEqual (List.length performed) 3 "the corpus registers an op performer"

              // The two placements of ONE handler differ exactly as D19 says.
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
              expectNoDivergence "the extracted model and production" (divergences guardCases)

              let holds = runCase guardCases.[0] None
              Expect.isTrue holds.Production.Committed "holds: committed"

              Expect.equal
                  holds.ProductionLog
                  [ "ApplyOps"; "host:audit"; "ApplyOps" ]
                  "holds: two edits and the host call — the guard was not staged"

              // The performer is handed the state each edit PRODUCED; the last
              // is the committed tree.
              Expect.equal (List.length holds.ProductionHanded) 2 "holds: one handed state per edit"

              Expect.equal
                  (List.last holds.ProductionHanded)
                  (render holds.Production.Store.Tree)
                  "holds: the last state handed is the committed plan"

              // In memory the guard moves nothing: had it been applied as an
              // edit it would have written the label `=idle`.
              let inMemory = runCase guardCases.[1] None
              Expect.isTrue inMemory.Production.Committed "in memory: committed"

              Expect.equal
                  (render inMemory.Production.Store.Tree)
                  (render durableStore.Tree)
                  "in memory: the label the guard would have written is discarded"

              // A guard over the PLAN: the edit before it relabelled the node
              // it checks, so it refuses — and nothing reaches any performer.
              let refuses = runCase guardCases.[2] None
              Expect.isFalse refuses.Production.Committed "refuses: halted"
              Expect.isEmpty refuses.ProductionLog "refuses: no performer ran"

              match refuses.Production.Diagnostics |> List.last with
              | ServerDiagnostic.Failed("ApplyOps", _) -> ()
              | other -> failtestf "refuses: expected the op channel's halt, got %A" other
          }

          test "the oracle agrees with production on every plan-phase halt — gate, policy, lookup, slot, apply, query" {
              expectNoDivergence "the extracted model and production" (divergences planHaltCases)

              for c in planHaltCases do
                  let run = runCase c None
                  Expect.isFalse run.Production.Committed (sprintf "%s: the handler committed" c.Name)
                  Expect.isEmpty run.ProductionLog (sprintf "%s: a performer ran during a plan-phase halt" c.Name)
                  Expect.isEmpty run.Production.Performed (sprintf "%s: something is reported performed" c.Name)
          }

          test "the residual is the prefix that ran, in order, and the store is the entry store" {
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

                  Expect.equal outcome.Store.Bindings c.Store.Bindings (sprintf "%s: the state moved" where)
                  Expect.isEmpty outcome.Patches (sprintf "%s: patches survived a rollback" where)
                  Expect.isEmpty outcome.Notifications (sprintf "%s: notifications survived a rollback" where)
                  Expect.isEmpty outcome.ClientEffects (sprintf "%s: client effects survived a rollback" where)

                  Expect.equal
                      outcome.Performed
                      (List.take k run.ProductionLog)
                      (sprintf "%s: Performed is not exactly the first %d staged calls, in order" where k)

                  Expect.equal
                      (List.length run.ProductionLog)
                      (k + 1)
                      (sprintf "%s: the perform phase did not stop at the refused call" where)
          }

          test "GO RED: a performer that reports success for a call it did not run loses the comparison" {
              let c =
                  loopCases |> List.find (fun c -> c.Functions = [ "first"; "second"; "third" ])

              Expect.isNone
                  (divergence c None (runCase c None))
                  "production, the oracle and the log disagree on the very case the defect is committed against"

              // `second` answers Ok WITHOUT running — nothing logged, a result
              // returned — on both sides, so the two outcomes still agree with
              // each other. The check that has to catch it is the claim
              // against the log.
              let lying (log: Log) : string -> Performer =
                  let honest = (scripted log None).Host

                  fun name ->
                      if name = "second" then
                          (fun _ -> Ok(JStr "ran:second"))
                      else
                          honest name

              let productionLog = Log()
              let modelLog = Log()

              let run =
                  { Production =
                      Handler.run witness (registryFor c (lying productionLog)) c.Resolve "call" c.Handler c.Store
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
//  Phase 1977 — the proved undo as oracle, at the toy witness.
//
//  `Undo.posture` and `Undo.run` over `Handler.runPlanned`'s plan, beside the
//  extraction's `posture`, `reasons`, `run_planned` and `undo_run`, with the
//  scripted performer refusing at every position of the undo's own staged
//  list. Four things must agree: the posture and its reasons, the recorded
//  plan step by step, the undo's answer, and the undo performer's log.
// ═══════════════════════════════════════════════════════════════════════════

type private ModelStep = Staging.step<ToyNode, ToyStore, ToyOp>

/// Production's `Undo` member in the model's shape.
let private modelUndoClass (witness: ToyWitness) (op: ToyOp) : global.Undo.undo_class<ToyNode, ToyOp> =
    match witness.State.Undo op with
    | UndoClass.Inverse inverse -> global.Undo.Inverse inverse
    | UndoClass.Compensate compensation -> global.Undo.Compensate compensation
    | UndoClass.OneWay reason -> global.Undo.OneWay reason

/// The toy with two kinds of op CLASSED for the undo: a CHECK relabel is
/// one-way, and a relabel of `readout` carries its exact inverse but is
/// DECLARED a compensation. The toy classes every relabel an exact inverse;
/// this composition exists so the differential reaches the compensable and
/// one-way clauses of the posture and the run. The check clause comes first,
/// because a check of `readout` is a relabel of `readout` too.
let private classedWitness: ToyWitness =
    { witness with
        State =
            { witness.State with
                Undo =
                    fun op ->
                        match op with
                        | Relabel(_, label) when checks label -> UndoClass.OneWay "a check is one-way here"
                        | Relabel("readout", _) ->
                            match witness.State.Undo op with
                            | UndoClass.Inverse inverse -> UndoClass.Compensate inverse
                            | other -> other
                        | _ -> witness.State.Undo op } }

let private projectStep (step: UndoStep<ToyNode, ToyOp, ToyAction>) : string =
    match step with
    | UndoStep.Edit(pre, op) -> "edit " + witness.State.Stream.Encode op + " before " + render pre
    | UndoStep.Compute(action, trace) ->
        "compute "
        + sprintf "%A" (BoundedActions.reverse witness action trace |> BoundedActions.Reversed.encode)
    | UndoStep.Reached capability -> "reached " + capability
    | UndoStep.Emitted capability -> "emitted " + capability

let private projectModelStep (step: ModelStep) : string =
    match step with
    | Staging.TEdit(pre, op) -> "edit " + witness.State.Stream.Encode op + " before " + render pre
    // The model's compute step carries its restorer, opaque; the kind is what
    // is compared, and the restored bindings are compared through the outcome.
    | Staging.TCompute _ -> "compute"
    | Staging.TReached capability -> "reached " + capability
    | Staging.TEmitted capability -> "emitted " + capability

let private sameKind (production: string) (model: string) =
    production = model
    || (production.StartsWith("compute ", StringComparison.Ordinal) && model = "compute")

type private UndoCase = { Case: StagingCase; Classed: bool }

let private undoWitnessOf (u: UndoCase) : ToyWitness =
    if u.Classed then classedWitness
    elif u.Case.Guarded then guardedWitness
    else witness

/// A store that already holds `status`, so a compute stage that writes it
/// leaves a RESTORABLE trace.
let private statusStore: ToyServerStore =
    { Tree = durableTree
      Bindings = witness.Dispatch.Store.Assign "status" (JStr "idle") Map.empty }

let private undoCases: UndoCase list =
    let plain (c: StagingCase) = { Case = c; Classed = false }
    let classed (c: StagingCase) = { Case = c; Classed = true }

    [ case
          "undo"
          "two edits performed, reversible"
          []
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh" ])
            Effect(ServerEffect.ApplyOps [ touch "readout" ]) ]
      |> performingOps
      |> plain
      case
          "undo"
          "two edits in memory, reversible"
          []
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh"; touch "readout" ]) ]
      |> plain
      case
          "undo"
          "a compute stage in the fragment with a restorable trace, beside an edit"
          []
          statusStore
          [ Compute(Put("status", Some(JStr "written"), None))
            Effect(ServerEffect.ApplyOps [ touch "refresh" ]) ]
      |> performingOps
      |> plain
      case
          "undo"
          "a compute stage whose trace is not restorable: the key was absent"
          []
          durableStore
          [ Compute(Put("status", Some(JStr "written"), None))
            Effect(ServerEffect.ApplyOps [ touch "refresh" ]) ]
      |> performingOps
      |> plain
      case
          "undo"
          "a host call among the edits is one-way at its step"
          [ "audit" ]
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh" ])
            Effect(ServerEffect.HostCall("audit", JStr "note", None))
            Effect(ServerEffect.ApplyOps [ touch "readout" ]) ]
      |> performingOps
      |> plain
      case
          "undo"
          "a notification is one-way"
          []
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh" ])
            Effect(ServerEffect.Notify("channel", JStr "note")) ]
      |> performingOps
      |> plain
      case
          "undo"
          "an emitted patch is undecidable"
          []
          durableStore
          [ Effect(ServerEffect.EmitPatch [ touch "readout" ])
            Effect(ServerEffect.ApplyOps [ touch "refresh" ]) ]
      |> performingOps
      |> plain
      case
          "undo"
          "a compensable edit: declared, undone in effect"
          []
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh"; touch "readout" ]) ]
      |> performingOps
      |> classed
      case
          "undo"
          "a one-way edit, after a reversible one"
          []
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh" ])
            Effect(ServerEffect.ApplyOps [ readoutCheck ]) ]
      |> performingOps
      |> classed
      case
          "undo"
          "a guard among the edits is not a step"
          []
          durableStore
          [ Effect(ServerEffect.ApplyOps [ touch "refresh"; readoutCheck ]) ]
      |> performingOps
      |> guarded
      |> plain ]

type private UndoRun =
    { Posture: UndoVerdict * UndoReason list
      ModelPosture: global.Undo.verdict * (bigint * global.Undo.defect) list
      Plan: string list
      ModelPlan: string list
      Production: Result<ToyOutcome, UndoRefusal>
      ProductionLog: string list
      Model: Result<ToyOutcome, string>
      ModelLog: string list }

let private runUndoCase (u: UndoCase) (failAt: int option) : UndoRun =
    let w = undoWitnessOf u
    let c = u.Case
    let mw = modelWitness w c.Resolve
    let cls = modelUndoClass w

    // The forward run, on both sides, never failing.
    let forwardLog = Log()
    let forwardScript = scripted forwardLog None

    let outcome, plan =
        Handler.runPlanned
            w
            (registryFor c forwardScript.Host)
            (performanceOf c forwardScript)
            c.Resolve
            "call"
            c.Handler
            c.Store

    let modelForwardLog = Log()
    let modelForwardScript = scripted modelForwardLog None

    let modelOutcome, modelSteps =
        Staging.run_planned
            mw
            (modelRegistry (performanceOf c modelForwardScript) (registryFor c modelForwardScript.Host))
            "call"
            (c.Handler.Stages |> List.map modelStage)
            { st_tree = c.Store.Tree
              st_bindings = c.Store.Bindings }

    // The undo, on both sides, under a fresh performer that fails at the
    // position.
    let undoLog = Log()
    let undoScript = scripted undoLog failAt

    let production =
        Fuaran.Program.Server.Undo.run
            w
            (registryFor c undoScript.Host)
            (performanceOf c undoScript)
            c.Resolve
            "call"
            plan
            outcome.Store

    let modelUndoLog = Log()
    let modelUndoScript = scripted modelUndoLog failAt

    // The drift check's canonical form is the witness's own `Canonical` on
    // both sides — production's member, handed to the model — and not the
    // fuller rendering the comparisons use: the model must reach the same
    // verdict production reaches, by the same test.
    let model =
        match
            global.Undo.undo_run
                mw
                (modelRegistry (performanceOf c modelUndoScript) (registryFor c modelUndoScript.Host))
                "call"
                cls
                w.State.Canonical
                c.Store.Tree
                modelOutcome.oc_committed
                modelSteps
                modelOutcome.oc_store
        with
        | Staging.ROk out -> Ok(productionShaped out)
        | Staging.RErr reason -> Error reason

    { Posture = Fuaran.Program.Server.Undo.posture w c.Handler, Fuaran.Program.Server.Undo.reasons w c.Handler
      ModelPosture =
        global.Undo.posture mw cls (BoundedActions.reversible w) (c.Handler.Stages |> List.map modelStage),
        global.Undo.reasons mw cls (BoundedActions.reversible w) (c.Handler.Stages |> List.map modelStage)
      Plan = plan.Steps |> List.map projectStep
      ModelPlan = modelSteps |> List.map projectModelStep
      Production = production
      ProductionLog = List.ofSeq undoLog
      Model = model
      ModelLog = List.ofSeq modelUndoLog }

let private verdictText (v: UndoVerdict) = Fuaran.Program.Server.Undo.verdictTag v

let private modelVerdictText (v: global.Undo.verdict) =
    match v with
    | global.Undo.Reversible -> "reversible"
    | global.Undo.Compensable -> "compensable"
    | global.Undo.OneWayVerdict -> "one-way"
    | global.Undo.Unknown -> "unknown"

let private defectText (d: UndoDefect) = Fuaran.Program.Server.Undo.defectTag d

let private modelDefectText (d: global.Undo.defect) =
    match d with
    | global.Undo.CompensatedOp -> "compensated-op"
    | global.Undo.OneWayOp -> "one-way-op"
    | global.Undo.OpaqueHostCall -> "opaque-host-call"
    | global.Undo.OutboundNotification -> "outbound-notification"
    | global.Undo.EmittedPatch -> "emitted-patch"
    | global.Undo.ComputeOutsideFragment -> "compute-outside-fragment"

let private undoDivergence (u: UndoCase) (failAt: int option) (run: UndoRun) : string option =
    let where =
        sprintf
            "%s (%s), undo failure at %s"
            u.Case.Name
            u.Case.Origin
            (failAt |> Option.map string |> Option.defaultValue "none")

    let posture =
        let v, rs = run.Posture
        verdictText v, rs |> List.map (fun r -> r.Stage, defectText r.Defect)

    let modelPosture =
        let v, rs = run.ModelPosture
        modelVerdictText v, rs |> List.map (fun (k, d) -> int k, modelDefectText d)

    let answer =
        match run.Production with
        | Ok outcome -> Choice1Of2(projectionOf outcome)
        | Error refusal -> Choice2Of2(Fuaran.Program.Server.Undo.describe refusal)

    let modelAnswer =
        match run.Model with
        | Ok outcome -> Choice1Of2(projectionOf outcome)
        | Error reason -> Choice2Of2 reason

    if posture <> modelPosture then
        Some(sprintf "%s: the postures differ\n  production: %A\n  model:      %A" where posture modelPosture)
    elif
        List.length run.Plan <> List.length run.ModelPlan
        || not (List.forall2 sameKind run.Plan run.ModelPlan)
    then
        Some(sprintf "%s: the plans differ\n  production: %A\n  model:      %A" where run.Plan run.ModelPlan)
    elif answer <> modelAnswer then
        Some(sprintf "%s: the undo answers differ\n  production: %A\n  model:      %A" where answer modelAnswer)
    elif run.ProductionLog <> run.ModelLog then
        Some(
            sprintf
                "%s: the undo performers were asked different things\n  production: %A\n  model:      %A"
                where
                run.ProductionLog
                run.ModelLog
        )
    else
        None

/// The failure positions an undo is run at: every position of its own staged
/// list — read off the all-succeed undo's `Performed` — and none.
let private undoPositions (u: UndoCase) : int option list =
    let staged =
        match (runUndoCase u None).Production with
        | Ok outcome when u.Case.PerformOps -> outcome.Performed |> List.filter (fun c -> c = "ApplyOps") |> List.length
        | _ -> 0

    None :: [ for k in 0 .. staged - 1 -> Some k ]

let private undoDivergences (cases: UndoCase list) : string list =
    [ for u in cases do
          for failAt in undoPositions u do
              match undoDivergence u failAt (runUndoCase u failAt) with
              | Some report -> report
              | None -> () ]

let private corpusAsUndo: UndoCase list =
    loopCases @ durableCases @ opCases @ guardCases
    |> List.map (fun c -> { Case = c; Classed = false })

[<Tests>]
let undoOracleTests =
    testList
        "Phase 1977 - the proved undo as oracle at the toy witness"
        [ test
              "the corpus reaches every answer: a restored run, a compensated one, a refusal of each kind, a failed undo step" {
              let answers =
                  [ for u in undoCases do
                        for failAt in undoPositions u do
                            let run = runUndoCase u failAt

                            yield
                                match run.Production with
                                | Ok outcome when outcome.Committed -> "committed:" + verdictText (fst run.Posture)
                                | Ok _ -> "rolled-back"
                                | Error refusal -> refusal.Code ]

              Expect.isGreaterThanOrEqual (List.length undoCases) 10 "the undo corpus is smaller than the UI host's"
              Expect.contains answers ("committed:" + "reversible") "no reversible run was undone"
              Expect.contains answers ("committed:" + "compensable") "no compensable run was undone"
              Expect.contains answers UndoCode.OneWayStep "no one-way step was refused"
              Expect.contains answers UndoCode.UndecidableStep "no undecidable step was refused"
              Expect.contains answers "rolled-back" "no undo failed inside its perform phase"
          }

          test "the oracle agrees with production on the undo cases at every failure position of the undo" {
              expectNoDivergence "the extracted model and production" (undoDivergences undoCases)
          }

          test "the oracle agrees with production on the staging corpus, undone" {
              expectNoDivergence "the extracted model and production" (undoDivergences corpusAsUndo)
          }

          test "a REVERSIBLE run is undone to the entry state, bindings included, through the performers" {
              let u =
                  undoCases
                  |> List.find (fun u -> u.Case.Name.StartsWith "a compute stage in the fragment")

              let run = runUndoCase u None
              Expect.equal (fst run.Posture) UndoVerdict.Reversible "read reversible"

              match run.Production with
              | Error refusal -> failtestf "refused: %s" (Fuaran.Program.Server.Undo.describe refusal)
              | Ok undone ->
                  Expect.isTrue undone.Committed "the undo commits"
                  Expect.equal (render undone.Store.Tree) (render u.Case.Store.Tree) "the tree is the entry tree"

                  Expect.equal
                      undone.Store.Bindings
                      u.Case.Store.Bindings
                      "the binding the compute stage wrote is restored to what it held"

                  Expect.isNonEmpty run.ProductionLog "the inverses were performed"
                  Expect.all run.ProductionLog ((=) "ApplyOps") "through the op performer, and nothing else"

              // And without the key present before the run, the compute stage's
              // trace is not restorable and the undo refuses, naming the step.
              let absentKey =
                  undoCases
                  |> List.find (fun u -> u.Case.Name.StartsWith "a compute stage whose trace")

              match (runUndoCase absentKey None).Production with
              | Error refusal ->
                  Expect.equal refusal.Code UndoCode.UndecidableStep "undecidable"
                  Expect.equal refusal.Step 0 "the compute stage is step 0"
              | Ok _ -> failtest "an unrestorable compute stage was undone"
          }

          test "a one-way step is refused before anything is undone, at the FIRST such step" {
              let hostCall =
                  undoCases |> List.find (fun u -> u.Case.Name.StartsWith "a host call among")

              let run = runUndoCase hostCall None
              Expect.equal (fst run.Posture) UndoVerdict.OneWay "read one-way"

              Expect.equal
                  (snd run.Posture)
                  [ { UndoReason.Stage = 1
                      Defect = UndoDefect.OpaqueHostCall } ]
                  "the host call's stage is named"

              match run.Production with
              | Error refusal ->
                  Expect.equal refusal.Code UndoCode.OneWayStep "one-way"
                  Expect.equal refusal.Step 1 "the edit is step 0, the host call step 1"
                  Expect.equal refusal.Reason "host:audit" "named by its capability"
              | Ok _ -> failtest "a plan with a host call was undone"

              Expect.isEmpty run.ProductionLog "no performer was asked"
          }

          test "GO RED: an undo whose model is handed the wrong class for an op loses the differential" {
              // The UI host has no go-red case of its own for the undo; the
              // comparison above could be one that never loses. So: the
              // compensable case, with the model handed the toy's SHIPPED
              // class (every relabel an exact inverse) while production runs
              // the classed composition. The posture must then disagree —
              // production reads `compensable`, the mis-classed model
              // `reversible` — and the harness must say so.
              let u =
                  undoCases |> List.find (fun u -> u.Case.Name.StartsWith "a compensable edit")

              Expect.isNone
                  (undoDivergence u None (runUndoCase u None))
                  "production and the oracle disagree on the very case the mis-classing is run against"

              let honest = runUndoCase u None
              let stages = u.Case.Handler.Stages |> List.map modelStage
              let mw = modelWitness classedWitness u.Case.Resolve
              let wrongClass = modelUndoClass witness

              let misClassed =
                  { honest with
                      ModelPosture =
                          global.Undo.posture mw wrongClass (BoundedActions.reversible classedWitness) stages,
                          global.Undo.reasons mw wrongClass (BoundedActions.reversible classedWitness) stages }

              Expect.equal (fst honest.Posture) UndoVerdict.Compensable "production reads the declared compensation"

              match undoDivergence u None misClassed with
              | Some report ->
                  Expect.stringContains
                      report
                      "postures differ"
                      "the harness reported a divergence, but not the posture one"
              | None ->
                  failtest
                      "the comparison harness did not report a model handed the wrong undo class - a harness that cannot lose is not evidence"
          } ]

// ═══════════════════════════════════════════════════════════════════════════
//  Phase 1759 — the proved effect gate as oracle, at the toy witness.
//
//  The model wraps the RAW performer with the EXTRACTED `checked_by` while
//  production wraps it with its own `registerChecked`, and the two are
//  compared over the registry shapes and generated (capability, gate,
//  performer) triples: the outcomes, the performers' logs, the DENIAL STREAM
//  (production's `OnDenied` sink against both sides' `Denied` diagnostics)
//  and the ORDER of events — every performer invocation preceded by the
//  gate's decision on its capability.
// ═══════════════════════════════════════════════════════════════════════════

type private GateToken = string * Performer

type private GateModelRegistry = Staging.registry<ToyNode, JVal, ToyOp, Query, GateToken>

type private GateAccumulator =
    Staging.accumulator<ToyNode, ToyStore, JVal, ToyOp, ToyEffect, BoundedDiagnostic, GateToken>

/// What happened, in order: `gate:<capability>`, `perform:<fn>`,
/// `deny:<kind>:<capability>`.
type private Events = System.Collections.Generic.List<string>

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
            | Answers -> Ok(JStr("ran:" + name))
            | AnswersWrongly -> Ok(JInt 1)
            | Refuses -> Error("host refused " + name)

let private textContract: ReturnContract =
    { Name = "text"
      Holds =
        fun value ->
            match value with
            | JStr _ -> true
            | _ -> false }

/// `audit` behind the contract, `raw` without one; `missing` is never
/// registered and is what the lookup misses.
let private checkedFunctions = [ "audit", Some textContract; "raw", None ]

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

/// One effect per capability the corpus can name, including the unregistered
/// host function.
let private effects: (string * ServerEffect<ToyOp>) list =
    [ "RunQuery", ServerEffect.RunQuery("rows", Embedded rows, limitTwo)
      "ApplyOps", ServerEffect.ApplyOps [ touch "call" ]
      "host:audit", ServerEffect.HostCall("audit", JStr "note", Some "audited")
      "host:raw", ServerEffect.HostCall("raw", JStr "note", Some "rawed")
      "host:missing", ServerEffect.HostCall("missing", JStr "note", None)
      "EmitPatch", ServerEffect.EmitPatch [ touch "call" ]
      "Notify", ServerEffect.Notify("channel", JStr "note") ]

let private programs (effect: ServerEffect<ToyOp>) : (string * ToyStage list) list =
    [ "alone", [ Effect effect ]
      "after audit",
      [ Effect(ServerEffect.HostCall("audit", JStr "first", Some "first"))
        Effect effect ]
      "after raw and audit",
      [ Effect(ServerEffect.HostCall("raw", JStr "one", None))
        Effect(ServerEffect.HostCall("audit", JStr "two", Some "second"))
        Effect effect ] ]

type private Triple =
    { Capability: string
      Program: string
      Stages: ToyStage list
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
/// are production's own members; the lookup answers the RAW closure as a
/// token, and the behaviour is the EXTRACTED `checked_by` over it.
let private gateModelRegistry (events: Events) (triple: Triple) : GateModelRegistry =
    let performer = behave events triple.Behaviour

    let raw =
        checkedFunctions |> List.map (fun (fn, _) -> fn, performer fn) |> Map.ofList

    let contracts =
        checkedFunctions
        |> List.choose (fun (fn, contract) -> contract |> Option.map (fun c -> fn, c))
        |> Map.ofList

    let policyBearer =
        ServerEffectRegistry.denyAll
        |> ServerEffectRegistry.withGate (fun cap ->
            events.Add("gate:" + cap)
            triple.Shape.Gate cap)
        |> triple.Shape.Narrow
        |> modelRegistry OpPerformance.InMemory

    let contractOf ((fn, _): GateToken) : Staging.opt<EffectGate.contract<JVal>> =
        contracts
        |> Map.tryFind fn
        |> Option.map (fun c ->
            { EffectGate.ct_name = c.Name
              EffectGate.ct_holds = c.Holds })
        |> modelOpt

    let rawBehaviour ((_, perf): GateToken) (args: JVal) : Staging.res<JVal> = perf args |> modelRes

    { r_gate = policyBearer.r_gate
      r_policy = policyBearer.r_policy
      r_lookup = fun fn -> raw |> Map.tryFind fn |> Option.map (fun perf -> fn, perf) |> modelOpt
      r_perf = EffectGate.checked_by contractOf rawBehaviour
      r_op_perform = Staging.ONone }

let private handlerOf (triple: Triple) : ToyHandler =
    { Name = triple.Capability
      Stages = triple.Stages }

let private modelStore: ModelStore =
    { st_tree = loopStore.Tree
      st_bindings = loopStore.Bindings }

type private GateRun =
    { Production: ToyOutcome
      ProductionEvents: string list
      Model: ToyOutcome
      ModelEvents: string list }

let private runTripleWith
    (run: ModelWitness -> GateModelRegistry -> string -> ModelStage list -> ModelStore -> ToyOutcome)
    (triple: Triple)
    : GateRun =
    let productionEvents = Events()
    let modelEvents = Events()

    let production =
        Handler.run
            witness
            (productionRegistry productionEvents triple)
            Fuaran.Compute.DataFrame.noResolve
            "call"
            (handlerOf triple)
            loopStore

    let model =
        run
            (modelWitness witness Fuaran.Compute.DataFrame.noResolve)
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

let private deniedIn (outcome: ToyOutcome) : string list =
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

/// `gate_before_perform`, read off production's event log.
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

/// The registry shapes the effect registry's own suite pins, as triples: the
/// default refuses every kind; registration does not permit; permission does
/// not register; a host call is namespaced away from the built-in arms.
let private registryShapeTriples: Triple list =
    let shape label gate =
        { Label = label
          Gate = gate
          Narrow = id }

    [ for capability, effect in effects do
          { Capability = capability
            Program = "alone"
            Stages = [ Effect effect ]
            Shape = shape "deny-all (the default)" (fun _ -> false)
            Behaviour = Answers }
      { Capability = "host:audit"
        Program = "alone"
        Stages = [ Effect(ServerEffect.HostCall("audit", JStr "note", None)) ]
        Shape = shape "everything but host:audit" (fun cap -> cap <> "host:audit")
        Behaviour = Answers }
      { Capability = "host:missing"
        Program = "alone"
        Stages = [ Effect(ServerEffect.HostCall("missing", JStr "note", None)) ]
        Shape = shape "permissive" (fun _ -> true)
        Behaviour = Answers }
      { Capability = "host:ApplyOps"
        Program = "alone"
        Stages = [ Effect(ServerEffect.HostCall("ApplyOps", JStr "note", None)) ]
        Shape = shape "ApplyOps only" (fun cap -> cap = "ApplyOps")
        Behaviour = Answers } ]

// ─── The go-red mutant: lookup before the gate ──────────────────────────────

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
    (store: ModelStore)
    : ToyOutcome =
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

// ─── Phase 1984 — the op contract at the handler, keyed on the op token ─────
//
//  This host stages a token that CARRIES what the model keys on — the planned
//  state and the op — and keys the model's contract on it, beside
//  production's handler under `OpPerformance.performedChecked`, where the
//  contract is composed into the closure instead (DECISIONS.md D27).

type private KeyedToken =
    | HostToken of fn: string * performer: Performer
    | OpToken of state: ToyNode * op: ToyOp

type private KeyedModelRegistry = Staging.registry<ToyNode, JVal, ToyOp, Query, KeyedToken>

/// The receipt an honest op performer answers: the planned state's hash, over
/// the canonical rendering WITH labels (so a receipt for the entry tree and
/// one for a relabelled tree differ), beside the op's content address.
let private receiptText (state: ToyNode) (op: ToyOp) : string =
    Hash.sha256Hex (render state) + "/" + Durable.opSubject witness.State op

let private receiptFor (state: ToyNode) (op: ToyOp) : JVal = JStr(receiptText state op)

let private opContractName = "names-the-planned-op"

let private plannedOpContract: OpContract<ToyNode, ToyOp> =
    OpContract.at opContractName (fun state op receipt -> receipt.Detail = receiptFor state op)

let private modelOpContract: EffectGate.op_contract<ToyNode, ToyOp, JVal> =
    { EffectGate.oc_name = opContractName
      EffectGate.oc_holds = fun state op receipt -> receipt = receiptFor state op }

type private OpBehaviour =
    | Honest
    | Elsewhere
    | HonestThenElsewhere
    | RefusesOps

type private KeyedLog = System.Collections.Generic.List<string>

let private keyedHost (log: KeyedLog) (fn: string) : Performer =
    fun _ ->
        log.Add("host:" + fn)
        Ok(JStr("ran:" + fn))

let private keyedOp
    (log: KeyedLog)
    (receipts: System.Collections.Generic.List<ToyNode * ToyOp * JVal>)
    (behaviour: OpBehaviour)
    : ToyNode -> ToyOp -> Result<JVal, string> =
    let calls = ref 0

    fun state op ->
        let n = calls.Value
        calls.Value <- n + 1
        log.Add("ApplyOps:" + receiptText state op)

        let answer =
            match behaviour with
            | Honest -> Ok(receiptFor state op)
            | Elsewhere -> Ok(JStr "elsewhere")
            | HonestThenElsewhere ->
                if n = 0 then
                    Ok(receiptFor state op)
                else
                    Ok(JStr "elsewhere")
            | RefusesOps -> Error "op performer refused"

        match answer with
        | Ok receipt -> receipts.Add((state, op, receipt))
        | Error _ -> ()

        answer

type private OpKeying =
    | KeyedOnToken
    | Uncontracted
    | KeyedOnEntryState of ToyNode

let private keyedContract (keying: OpKeying) (token: KeyedToken) : Staging.opt<EffectGate.contract<JVal>> =
    match token, keying with
    | HostToken("audit", _), _ ->
        Staging.OSome
            { EffectGate.ct_name = textContract.Name
              EffectGate.ct_holds = textContract.Holds }
    | HostToken _, _ -> Staging.ONone
    | OpToken(state, op), KeyedOnToken -> Staging.OSome(EffectGate.op_at modelOpContract state op)
    | OpToken _, Uncontracted -> Staging.ONone
    | OpToken(_, op), KeyedOnEntryState entry -> Staging.OSome(EffectGate.op_at modelOpContract entry op)

let private keyedPlans: (string * ToyStage list) list =
    [ "an op alone", [ Effect(ServerEffect.ApplyOps [ touch "refresh" ]) ]
      "two ops in one stage", [ Effect(ServerEffect.ApplyOps [ touch "refresh"; touch "readout" ]) ]
      "an op, then a checked host call landing",
      [ Effect(ServerEffect.ApplyOps [ touch "refresh" ])
        Effect(ServerEffect.HostCall("audit", JStr "note", Some "audited")) ]
      "a checked host call, then an op",
      [ Effect(ServerEffect.HostCall("audit", JStr "note", Some "audited"))
        Effect(ServerEffect.ApplyOps [ touch "refresh" ]) ]
      "host call, op, host call, op",
      [ Effect(ServerEffect.HostCall("audit", JStr "one", None))
        Effect(ServerEffect.ApplyOps [ touch "refresh" ])
        Compute(Put("status", Some(JStr "planned"), None))
        Effect(ServerEffect.HostCall("raw", JStr "two", None))
        Effect(ServerEffect.ApplyOps [ touch "readout" ]) ] ]

type private KeyedCase =
    { Plan: string
      Stages: ToyStage list
      Behaviour: OpBehaviour
      Contracted: bool }

let private keyedCases: KeyedCase list =
    [ for plan, stages in keyedPlans do
          for behaviour in [ Honest; Elsewhere; HonestThenElsewhere; RefusesOps ] do
              for contracted in [ true; false ] do
                  { Plan = plan
                    Stages = stages
                    Behaviour = behaviour
                    Contracted = contracted } ]

let private describeKeyed (c: KeyedCase) =
    sprintf "%s / %A / %s" c.Plan c.Behaviour (if c.Contracted then "contracted" else "uncontracted")

type private KeyedSide =
    { Log: KeyedLog
      Receipts: System.Collections.Generic.List<ToyNode * ToyOp * JVal>
      Op: ToyNode -> ToyOp -> Result<JVal, string> }

let private keyedSide (behaviour: OpBehaviour) : KeyedSide =
    let log = KeyedLog()
    let receipts = System.Collections.Generic.List<_>()

    { Log = log
      Receipts = receipts
      Op = keyedOp log receipts behaviour }

let private keyedProduction (c: KeyedCase) (side: KeyedSide) =
    let registry =
        ServerEffectRegistry.denyAll
        |> ServerEffectRegistry.registerChecked "audit" textContract (keyedHost side.Log "audit")
        |> ServerEffectRegistry.register "raw" (keyedHost side.Log "raw")
        |> ServerEffectRegistry.permissive

    let performance =
        if c.Contracted then
            OpPerformance.performedChecked [ plannedOpContract ] (fun _ state op ->
                side.Op state op |> Result.map OpReceipt.ofDetail)
        else
            OpPerformance.performedWithDetail side.Op

    registry, performance

let private keyedModel
    (keying: OpKeying)
    (side: KeyedSide)
    (answers: System.Collections.Generic.List<Staging.res<JVal>>)
    : KeyedModelRegistry =
    let bridge =
        ServerEffectRegistry.denyAll
        |> ServerEffectRegistry.permissive
        |> modelRegistry OpPerformance.InMemory

    let raw (token: KeyedToken) (args: JVal) : Staging.res<JVal> =
        match token with
        | HostToken(_, performer) -> performer args |> modelRes
        | OpToken(state, op) -> side.Op state op |> modelRes

    let checkedBehaviour = EffectGate.checked_by (keyedContract keying) raw

    { r_gate = bridge.r_gate
      r_policy = bridge.r_policy
      r_lookup =
        fun fn ->
            if fn = "audit" || fn = "raw" then
                Staging.OSome(HostToken(fn, keyedHost side.Log fn))
            else
                Staging.ONone
      r_perf =
        fun token args ->
            let answer = checkedBehaviour token args
            answers.Add answer
            answer
      r_op_perform = Staging.OSome(fun state op -> OpToken(state, op), JObj []) }

let private keyedModelStore: ModelStore =
    { st_tree = durableStore.Tree
      st_bindings = durableStore.Bindings }

let private keyedHandler (c: KeyedCase) : ToyHandler = { Name = c.Plan; Stages = c.Stages }

type private KeyedRun =
    { Production: ToyOutcome
      ProductionLog: string list
      ProductionReceipts: (ToyNode * ToyOp * JVal) list
      Model: ToyOutcome
      ModelLog: string list }

let private runKeyed (keying: OpKeying) (c: KeyedCase) : KeyedRun =
    let productionSide = keyedSide c.Behaviour
    let modelSide = keyedSide c.Behaviour
    let registry, performance = keyedProduction c productionSide

    let production =
        Handler.runWith
            witness
            registry
            performance
            Fuaran.Compute.DataFrame.noResolve
            "call"
            (keyedHandler c)
            durableStore

    let keyingFor = if c.Contracted then keying else Uncontracted

    let model =
        Staging.run
            (modelWitness witness Fuaran.Compute.DataFrame.noResolve)
            (keyedModel keyingFor modelSide (System.Collections.Generic.List<_>()))
            "call"
            (c.Stages |> List.map modelStage)
            keyedModelStore
        |> productionShaped

    { Production = production
      ProductionLog = List.ofSeq productionSide.Log
      ProductionReceipts = List.ofSeq productionSide.Receipts
      Model = model
      ModelLog = List.ofSeq modelSide.Log }

let private keyedDivergence (c: KeyedCase) (run: KeyedRun) : string option =
    let where = describeKeyed c
    let production = projectionOf run.Production
    let model = projectionOf run.Model

    if production <> model then
        Some(sprintf "%s: the outcomes differ\n production: %A\n model:      %A" where production model)
    elif run.ProductionLog <> run.ModelLog then
        Some(
            sprintf
                "%s: the performers were asked different things\n production: %A\n model:      %A"
                where
                run.ProductionLog
                run.ModelLog
        )
    else
        None

/// A journal record reduced to what both sides can say about a DECIDED step.
let private decidedOf (entries: JournalEntry list) =
    entries
    |> List.filter (fun e -> e.Step <> Journal.InvocationStep)
    |> List.choose (fun e ->
        match e.Phase with
        | JournalPhase.Completed value -> Some(e.Step, e.Capability, e.Subject, Ok value)
        | JournalPhase.Refused reason -> Some(e.Step, e.Capability, e.Subject, Error reason)
        | _ -> None)

let private keyedJournal (entries: JournalEntry list) : Staging.journal<JVal> =
    { j_step =
        fun k ->
            match Journal.stepOf entries (int k) with
            | JournaledStep.Unrun -> Staging.JUnrun
            | JournaledStep.Value v -> Staging.JValue v
            | JournaledStep.Refusal r -> Staging.JRefusal r
            | JournaledStep.Indeterminate _ -> Staging.JIndeterminate
      j_recorded =
        fun k ->
            match Journal.capabilityOf entries (int k), Journal.subjectOf entries (int k) with
            | Some capability, Some subject -> Staging.OSome(capability, modelOpt subject)
            | _ -> Staging.ONone }

let private keyedSubject (call: Staging.staged_call<JVal, KeyedToken>) : Staging.opt<string> =
    match call.sc_performer with
    | OpToken(_, op) -> Staging.OSome(Durable.opSubject witness.State op)
    | HostToken _ -> Staging.ONone

type private ModelDurableOutcome = Staging.durable_outcome<ToyNode, ToyStore, JVal, ToyOp, ToyEffect, BoundedDiagnostic>

type private DurableKeyedRun =
    { Production: DurableOutcome<ToyNode, ToyStore, ToyOp, ToyEffect>
      ProductionDecided: (int * string * string option * Result<JVal, string>) list
      Model: ModelDurableOutcome
      ModelDecided: (int * string * string option * Result<JVal, string>) list
      Resumed: DurableOutcome<ToyNode, ToyStore, ToyOp, ToyEffect>
      ModelResumed: ModelDurableOutcome }

let private runKeyedDurably (c: KeyedCase) : DurableKeyedRun =
    let journal = Journal.inMemory ()
    let services = DurableServices.create |> DurableServices.withJournal journal

    let durableFor (entries: JournalEntry list) : Staging.durable<JVal, KeyedToken> =
        { d_journal = keyedJournal entries
          d_subject = keyedSubject
          d_idempotent =
            fun call ->
                match call.sc_performer with
                | OpToken _ -> PerformerFacets.opPerformerFacet services.Performers = IdempotencyFacet.Idempotent
                | HostToken(fn, _) -> PerformerFacets.facetOf fn services.Performers = IdempotencyFacet.Idempotent
          d_reinvoke = services.ReinvokeIndeterminate }

    let keying = if c.Contracted then KeyedOnToken else Uncontracted
    let mw = modelWitness witness Fuaran.Compute.DataFrame.noResolve
    let stages = c.Stages |> List.map modelStage

    let runProduction () =
        let side = keyedSide c.Behaviour
        let registry, performance = keyedProduction c side

        Durable.runWith
            witness
            services
            "inv"
            registry
            performance
            Fuaran.Compute.DataFrame.noResolve
            "call"
            (keyedHandler c)
            durableStore

    let runModel (entries: JournalEntry list) =
        let answers = System.Collections.Generic.List<Staging.res<JVal>>()
        let registry = keyedModel keying (keyedSide c.Behaviour) answers

        let outcome =
            Staging.durable_run mw registry (durableFor entries) "call" stages keyedModelStore

        // The staged list the perform phase walked, read off the plan — the
        // plan phase never applies a behaviour, so this asks no performer.
        let staged =
            (Staging.plan mw registry "call" stages (Staging.start keyedModelStore)).ac_staged
            |> Staging.rev
            |> Array.ofList

        let decided =
            List.zip (outcome.do_invoked |> List.map int) (List.ofSeq answers)
            |> List.map (fun (k, answer) ->
                let call = staged.[k]

                k,
                call.sc_capability,
                (match keyedSubject call with
                 | Staging.OSome s -> Some s
                 | Staging.ONone -> None),
                (match answer with
                 // Production journals an op stage's TYPED receipt as its
                 // encoding (Phase 2197, D43); the model's receipt is the
                 // detail of a receipt naming no write, so the bridge is
                 // `encode (ofDetail v)` at an op stage and the identity at a
                 // host call.
                 | Staging.ROk v when call.sc_capability = OpPerformance.RegistrationKey ->
                     Ok(OpReceipt.encode (OpReceipt.ofDetail v))
                 | Staging.ROk v -> Ok v
                 | Staging.RErr r -> Error r))

        outcome, decided

    let production = runProduction ()
    let first = journal.Read "inv"
    let model, modelDecided = runModel []
    let resumed = runProduction ()
    let modelResumed, _ = runModel first

    { Production = production
      ProductionDecided = decidedOf first
      Model = model
      ModelDecided = modelDecided
      Resumed = resumed
      ModelResumed = modelResumed }

let private durableKeyedDivergence (c: KeyedCase) (run: DurableKeyedRun) : string option =
    let where = describeKeyed c
    let ordinals (xs: bigint list) = xs |> List.map int

    let compareOne
        (leg: string)
        (production: DurableOutcome<ToyNode, ToyStore, ToyOp, ToyEffect>)
        (model: ModelDurableOutcome)
        =
        let p = projectionOf production.Outcome
        let m = projectionOf (productionShaped model.do_outcome)

        if p <> m then
            Some(sprintf "%s (%s): the outcomes differ\n production: %A\n model:      %A" where leg p m)
        elif
            (production.Replayed, production.Invoked, production.Indeterminate)
            <> (ordinals model.do_replayed, ordinals model.do_invoked, ordinals model.do_indeterminate)
        then
            Some(
                sprintf
                    "%s (%s): the ordinal lists differ\n production: %A\n model:      %A"
                    where
                    leg
                    (production.Replayed, production.Invoked, production.Indeterminate)
                    (model.do_replayed, model.do_invoked, model.do_indeterminate)
            )
        else
            None

    match compareOne "first run" run.Production run.Model with
    | Some report -> Some report
    | None ->
        if run.ProductionDecided <> run.ModelDecided then
            Some(
                sprintf
                    "%s: the decided steps differ\n production's journal: %A\n model:                %A"
                    where
                    run.ProductionDecided
                    run.ModelDecided
            )
        else
            compareOne "resumed" run.Resumed run.ModelResumed

// ─── The tests ──────────────────────────────────────────────────────────────

[<Tests>]
let effectGateTests =
    testList
        "Phase 1759 - the proved effect gate as oracle at the toy witness"
        [ test
              "the corpus reaches every verdict - a gate refusal, a missing performer, a policy halt, a contract refusal, a raw refusal and a commit" {
              let runs = triples |> List.map (fun t -> t, runTriple t)

              let has (predicate: Triple -> GateRun -> bool) =
                  runs |> List.exists (fun (t, r) -> predicate t r)

              let hasDiagnostic (predicate: ServerDiagnostic -> bool) =
                  has (fun _ r -> r.Production.Diagnostics |> List.exists predicate)

              Expect.equal
                  (List.length triples)
                  441
                  "the generated corpus is seven effects, three programs, seven gate shapes and three behaviours"

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

          test "the oracle agrees with production on the effect registry's own shapes" {
              let divergences =
                  registryShapeTriples |> List.choose (fun t -> gateDivergence t (runTriple t))

              expectNoDivergence "the extracted model and production" divergences
          }

          test
              "the oracle agrees with production on every generated (capability, gate, performer) triple - outcome, denial stream, performed set" {
              let divergences = triples |> List.choose (fun t -> gateDivergence t (runTriple t))

              expectNoDivergence "the extracted model and production" divergences
          }

          test "the gate is consulted before any performer, and every performed capability is one the gate admitted" {
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
              let rejected =
                  [ for t in triples do
                        if t.Behaviour = AnswersWrongly && t.Shape.Label = "permissive" then
                            let staged =
                                t.Stages
                                |> List.choose (fun s ->
                                    match s with
                                    | Effect(ServerEffect.HostCall(fn, _, _)) when fn <> "missing" -> Some fn
                                    | _ -> None)

                            // The theorem's hypothesis is that the plan completed.
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

                  Expect.equal outcome.Store.Bindings loopStore.Bindings (sprintf "%s: the state moved" where)

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
              let t =
                  { Capability = "host:missing"
                    Program = "alone"
                    Stages = [ Effect(ServerEffect.HostCall("missing", JStr "note", None)) ]
                    Shape = gateShapes |> List.find (fun s -> s.Label = "deny-all")
                    Behaviour = Answers }

              Expect.isNone
                  (gateDivergence t (runTriple t))
                  "production and the oracle disagree on the very triple the mutant is run against"

              // The mutant agrees whenever the gate admits — so it is the ORDER
              // it gets wrong, not the arm.
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
          }

          test
              "the op contract wrapper agrees with the extracted check_op over every receipt, verdict and refusal (Phase 1981)" {
              // Instantiated at the toy's OWN state and op types — the UI host
              // instantiates the wrapper at strings — so the generic wrapper is
              // run here at a node and an op the witness defines.
              let states = [ loopTree; durableTree ]
              let ops = [ touch "call"; Relabel("readout", "done") ]

              let named (s: ToyNode) (o: ToyOp) =
                  JStr(
                      s.Id
                      + "/"
                      + string (List.length s.Children)
                      + "/"
                      + witness.State.Stream.Encode o
                  )

              let behaviours: (string * (ToyNode -> ToyOp -> Result<JVal, string>)) list =
                  [ "answers-the-op", (fun s o -> Ok(named s o))
                    "answers-elsewhere", (fun _ _ -> Ok(JStr "elsewhere"))
                    "answers-an-object", (fun _ _ -> Ok(JObj [ "n", JStr "7" ]))
                    "refuses", (fun _ _ -> Error "host refused") ]

              let verdicts: (string * (ToyNode -> ToyOp -> JVal -> bool)) list =
                  [ "names-the-op", (fun s o r -> r = named s o)
                    "any-text",
                    (fun _ _ r ->
                        match r with
                        | JStr _ -> true
                        | _ -> false)
                    "nothing", (fun _ _ _ -> false) ]

              let mutable cases = 0

              for state in states do
                  for op in ops do
                      for (bLabel, behaviour) in behaviours do
                          for (vLabel, verdict) in verdicts do
                              let productionSeen = ResizeArray<string * string>()
                              let modelSeen = ResizeArray<string * string>()

                              let production: OpContract<ToyNode, ToyOp> =
                                  OpContract.at vLabel (fun s o r ->
                                      productionSeen.Add(render s, witness.State.Stream.Encode o)
                                      verdict s o r.Detail)

                              let model: EffectGate.op_contract<ToyNode, ToyOp, JVal> =
                                  { EffectGate.oc_name = vLabel
                                    EffectGate.oc_holds =
                                      fun s o r ->
                                          modelSeen.Add(render s, witness.State.Stream.Encode o)
                                          verdict s o r }

                              let modelPerform (s: ToyNode) (o: ToyOp) = behaviour s o |> modelRes

                              let expected =
                                  OpContract.check
                                      production
                                      (fun _ s o -> behaviour s o |> Result.map OpReceipt.ofDetail)
                                      (OpPrefix.atEntry state)
                                      state
                                      op
                                  |> Result.map _.Detail
                                  |> modelRes

                              let actual = EffectGate.check_op model modelPerform state op

                              let label =
                                  sprintf "%s / %s / %s / %s" state.Id (witness.State.Stream.Encode op) bLabel vLabel

                              Expect.equal actual expected (sprintf "check_op and OpContract.check differ on %s" label)

                              Expect.equal
                                  (List.ofSeq modelSeen)
                                  (List.ofSeq productionSeen)
                                  (sprintf "the contract was handed different (state, op) on the two sides for %s" label)

                              Expect.equal
                                  (EffectGate.check_return
                                      (EffectGate.op_at model state op)
                                      (fun (_: unit) (_: JVal) -> modelPerform state op)
                                      ()
                                      (JObj []))
                                  actual
                                  (sprintf "check_op is not check_return at the state and the op for %s" label)

                              cases <- cases + 1

              Expect.equal cases 48 "the corpus is two states, two ops, four behaviours and three verdicts"
          }

          test
              "the op-contract host reaches every verdict at the handler - a receipt landed, refused by its contract first, refused after a host call ran, and a raw refusal (Phase 1984)" {
              let runs =
                  keyedCases
                  |> List.filter _.Contracted
                  |> List.map (fun c -> c, runKeyed KeyedOnToken c)

              let refusedByContract (r: KeyedRun) =
                  match List.tryLast r.Production.Diagnostics with
                  | Some(ServerDiagnostic.PerformFailed("ApplyOps", reason)) ->
                      reason = OpContract.describe plannedOpContract
                  | _ -> false

              Expect.equal (List.length keyedCases) 40 "five plans, four behaviours, contracted and not"

              Expect.isTrue
                  (runs
                   |> List.exists (fun (_, r) -> r.Production.Committed && not (List.isEmpty r.ProductionReceipts)))
                  "no contracted run landed an op receipt"

              Expect.isTrue
                  (runs
                   |> List.exists (fun (_, r) -> refusedByContract r && List.isEmpty r.Production.Performed))
                  "no run had its FIRST stage refused by the op contract"

              Expect.isTrue
                  (runs
                   |> List.exists (fun (_, r) ->
                       refusedByContract r
                       && r.Production.Performed
                          |> List.exists (fun c -> c.StartsWith("host:", StringComparison.Ordinal))))
                  "no run had an op refused by its contract AFTER a host call ran"

              Expect.isTrue
                  (runs
                   |> List.exists (fun (_, r) -> refusedByContract r && List.contains "ApplyOps" r.Production.Performed))
                  "no run had a LATER op refused after an earlier op landed"

              Expect.isTrue
                  (runs
                   |> List.exists (fun (_, r) ->
                       match List.tryLast r.Production.Diagnostics with
                       | Some(ServerDiagnostic.PerformFailed("ApplyOps", "op performer refused")) -> true
                       | _ -> false))
                  "no run had the op performer refuse outright"
          }

          test
              "the extracted handler under an op contract keyed on the op token agrees with production's handler under performedChecked - outcome, performer log, the refusal and the rollback (Phase 1984)" {
              let divergences =
                  keyedCases |> List.choose (fun c -> keyedDivergence c (runKeyed KeyedOnToken c))

              expectNoDivergence "the extracted handler and production" divergences

              for c in keyedCases |> List.filter _.Contracted do
                  let run = runKeyed KeyedOnToken c
                  let where = describeKeyed c
                  let outcome = run.Production

                  if outcome.Committed then
                      for state, op, receipt in run.ProductionReceipts do
                          Expect.isTrue
                              (plannedOpContract.Holds (OpPrefix.atEntry state) state op (OpReceipt.ofDetail receipt))
                              (sprintf "%s: a committed run landed a receipt its contract rejects" where)

                  match List.tryLast outcome.Diagnostics with
                  | Some(ServerDiagnostic.PerformFailed("ApplyOps", reason)) when
                      reason = OpContract.describe plannedOpContract
                      ->
                      Expect.isFalse outcome.Committed (sprintf "%s: committed past a contract refusal" where)

                      Expect.isTrue
                          (LanguagePrimitives.PhysicalEquality outcome.Store.Tree durableStore.Tree)
                          (sprintf "%s: the tree is not the entry tree" where)

                      Expect.equal
                          outcome.Performed
                          (run.ProductionLog
                           |> List.take (List.length run.ProductionLog - 1)
                           |> List.map (fun e -> if e.StartsWith "host:" then e else "ApplyOps"))
                          (sprintf "%s: Performed is not exactly the stages before the rejected op" where)

                      Expect.isTrue
                          ((List.last run.ProductionLog).StartsWith "ApplyOps:")
                          (sprintf "%s: the perform phase did not stop at the rejected op" where)

                      Expect.isFalse
                          (outcome.Diagnostics
                           |> List.exists (fun d -> (sprintf "%A" d).Contains "elsewhere"))
                          (sprintf "%s: the rejected receipt is echoed in a diagnostic" where)
                  | _ ->
                      Expect.isFalse
                          (c.Behaviour = Elsewhere && outcome.Committed)
                          (sprintf "%s: a receipt naming nothing was accepted" where)

              // op_contract_keyed, as an instance against the model.
              for c in keyedCases do
                  let staged =
                      let planned =
                          Staging.plan
                              (modelWitness witness Fuaran.Compute.DataFrame.noResolve)
                              (keyedModel KeyedOnToken (keyedSide c.Behaviour) (System.Collections.Generic.List<_>()))
                              "call"
                              (c.Stages |> List.map modelStage)
                              (Staging.start keyedModelStore)

                      Staging.rev planned.ac_staged

                  for call in staged do
                      match call.sc_performer with
                      | OpToken(state, op) ->
                          match keyedContract KeyedOnToken call.sc_performer with
                          | Staging.OSome contract ->
                              Expect.equal contract.ct_name opContractName "the op token is keyed to another contract"

                              Expect.isTrue
                                  (contract.ct_holds (receiptFor state op))
                                  (sprintf "%s: the keyed contract rejects the honest receipt" (describeKeyed c))

                              Expect.isFalse
                                  (contract.ct_holds (JStr "elsewhere"))
                                  (sprintf "%s: the keyed contract admits a foreign receipt" (describeKeyed c))
                          | Staging.ONone -> failtest "an op token carries no contract"
                      | HostToken _ -> ()
          }

          test
              "under Durable.runWith a contract-rejected receipt journals as Refused at its ordinal in the model's durable_run and in production alike, and a resume serves that refusal (Phase 1984)" {
              let runs = keyedCases |> List.map (fun c -> c, runKeyedDurably c)

              let divergences = runs |> List.choose (fun (c, run) -> durableKeyedDivergence c run)

              expectNoDivergence "the extracted durable run and production" divergences

              let refused =
                  [ for c, run in runs do
                        match List.tryLast run.ProductionDecided with
                        | Some(k, "ApplyOps", Some _, Error reason) when reason = OpContract.describe plannedOpContract ->
                            yield c, k, run
                        | _ -> () ]

              Expect.isTrue (List.length refused >= 3) "fewer than three durable runs journal a contract refusal"

              Expect.isTrue
                  (refused |> List.exists (fun (_, k, _) -> k > 0))
                  "no contract refusal was journaled past ordinal 0"

              for c, k, run in refused do
                  let where = describeKeyed c

                  Expect.isFalse
                      run.Production.Outcome.Committed
                      (sprintf "%s: committed past a journaled refusal" where)

                  Expect.equal
                      (List.last run.Production.Invoked)
                      k
                      (sprintf "%s: the refusal is not at the last invoked ordinal" where)

                  Expect.isFalse
                      (run.ProductionDecided
                       |> List.exists (fun (step, _, _, answer) ->
                           step = k
                           && (match answer with
                               | Ok _ -> true
                               | Error _ -> false)))
                      (sprintf "%s: the refused ordinal is journaled as completed" where)

                  Expect.isEmpty run.Resumed.Invoked (sprintf "%s: the resume invoked a performer" where)

                  Expect.equal
                      run.Resumed.Replayed
                      [ 0..k ]
                      (sprintf "%s: the resume did not serve every decided step" where)

                  Expect.equal
                      run.Resumed.Outcome.Diagnostics
                      run.Production.Outcome.Diagnostics
                      (sprintf "%s: the resume did not serve the same refusal" where)
          }

          test
              "GO RED: a model whose op contract is keyed on the entry state rather than the op token's own loses the handler-level differential (Phase 1984)" {
              let misKeyed = KeyedOnEntryState durableStore.Tree

              let single =
                  keyedCases
                  |> List.find (fun c -> c.Plan = "an op alone" && c.Behaviour = RefusesOps && c.Contracted)

              Expect.isNone
                  (keyedDivergence single (runKeyed misKeyed single))
                  "the mis-keyed model diverges even where no receipt reaches the contract"

              let honestTwo =
                  keyedCases
                  |> List.find (fun c -> c.Plan = "two ops in one stage" && c.Behaviour = Honest && c.Contracted)

              Expect.isNone
                  (keyedDivergence honestTwo (runKeyed KeyedOnToken honestTwo))
                  "production and the honestly keyed model disagree on the very case the mis-keying is run against"

              let run = runKeyed misKeyed honestTwo
              Expect.isTrue run.Production.Committed "production refused an honest receipt"
              Expect.isFalse run.Model.Committed "the mis-keyed model accepted every receipt"

              match keyedDivergence honestTwo run with
              | Some report ->
                  Expect.stringContains report "differ" "the harness reported a divergence, but not the outcome one"
              | None ->
                  failtest
                      "the comparison harness did not report a contract keyed on the wrong state - a harness that cannot lose is not evidence"

              let misKeyedDivergences =
                  keyedCases |> List.choose (fun c -> keyedDivergence c (runKeyed misKeyed c))

              Expect.isNonEmpty misKeyedDivergences "the mis-keyed model lost no case of the corpus"
          } ]
