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

type private ModelRegistry = Staging.registry<Fuaran.Core.JVal, TreeOp<obj>, Query, Performer>
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

let private modelWitness (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>) : ModelWitness =
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
            |> Result.map (fun table -> witness.Store.LandQuery name table bindings)
            |> Result.mapError evalErrorKind
            |> modelRes
      w_apply = fun op tree -> witness.Op.Stream.Apply op tree |> modelRes
      w_assign = witness.Store.Assign
      w_is_reserved = witness.Store.IsReserved
      w_reserved_prefix = witness.Store.ReservedPrefix }

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
let private modelRegistry (registry: ServerEffectRegistry) : ModelRegistry =
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

            match ServerArgumentPolicy.check registry production with
            | Ok() -> Staging.ONone
            | Error defect -> Staging.OSome(ServerArgumentPolicy.describe defect)
      r_lookup = fun fn -> Map.tryFind fn registry.HostFunctions |> modelOpt
      r_perf = fun performer args -> performer args |> modelRes }

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
    (registry: ServerEffectRegistry)
    (resolve: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError>)
    (nodeId: string)
    (handler: Handler)
    (store: ServerStore)
    : HandlerOutcome =
    Staging.run
        (modelWitness resolve)
        (modelRegistry registry)
        nodeId
        (handler.Stages |> List.map modelStage)
        { st_tree = store.Tree
          st_bindings = store.Bindings }
    |> productionShaped

// ─── The scripted performer ─────────────────────────────────────────────────

/// What a performer actually did: the function names it was invoked under,
/// in invocation order. The ground truth every claim below is checked
/// against.
type private Log = System.Collections.Generic.List<string>

/// A performer that counts its invocations across every name it is
/// registered under and refuses at ONE position, or never. Each run builds a
/// fresh one, so production and the model each start from zero.
let private scripted (log: Log) (failAt: int option) : string -> Performer =
    let calls = ref 0

    fun name ->
        fun _ ->
            let position = calls.Value
            calls.Value <- position + 1
            log.Add name

            match failAt with
            | Some k when k = position -> Error(sprintf "scripted refusal at position %d" k)
            | _ -> Ok(jstr (sprintf "ran:%s" name))

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
    }

let private registryFor (case: StagingCase) (performer: string -> Performer) : ServerEffectRegistry =
    case.Functions
    |> List.fold (fun r fn -> ServerEffectRegistry.register fn (performer fn) r) ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.permissive
    |> case.Shape

/// One run of one case, on both sides, at one failure position.
type private Run =
    { Production: HandlerOutcome
      ProductionLog: string list
      Model: HandlerOutcome
      ModelLog: string list }

let private runCase (case: StagingCase) (failAt: int option) : Run =
    let productionLog = Log()
    let modelLog = Log()

    let production =
        Handler.run (registryFor case (scripted productionLog failAt)) case.Resolve "call" case.Handler case.Store

    let model =
        runModel (registryFor case (scripted modelLog failAt)) case.Resolve "call" case.Handler case.Store

    { Production = production
      ProductionLog = List.ofSeq productionLog
      Model = model
      ModelLog = List.ofSeq modelLog }

/// The host calls the performer's log says ran, as capabilities.
let private ranCapabilities (log: string list) =
    log |> List.map (fun fn -> "host:" + fn)

/// `Performed`'s host-call suffix — what the handler CLAIMS ran outside.
let private claimedHostCalls (outcome: HandlerOutcome) =
    outcome.Performed
    |> List.filter (fun c -> c.StartsWith("host:", StringComparison.Ordinal))

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
    else
        // What ran, per the log, minus the refused call — the log records an
        // invocation the moment it is asked, and a refused one is asked and
        // not performed.
        let ran =
            match failAt with
            | Some k when k < List.length run.ProductionLog -> List.take k run.ProductionLog
            | _ -> run.ProductionLog

        if claimedHostCalls run.Production <> ranCapabilities ran then
            Some(
                sprintf
                    "%s: Performed claims %A outside, but the performer's log says %A ran"
                    where
                    (claimedHostCalls run.Production)
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
      Resolve = Fuaran.Core.DataFrame.noResolve }

let private shaped (shape: ServerEffectRegistry -> ServerEffectRegistry) (c: StagingCase) = { c with Shape = shape }

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
            Effect(ServerEffect.HostCall("audit", jstr "note", Some(witness.Store.ReservedPrefix + "x"))) ]
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

/// The failure positions a case is run at: every position of its staged
/// list, and none. Counted over the `HostCall` stages — a position past what
/// the plan phase actually staged is simply never reached, and the run is
/// then the all-succeed run again, compared all the same.
let private positions (case: StagingCase) : int option list =
    let hostCalls =
        case.Handler.Stages
        |> List.filter (fun stage ->
            match stage with
            | Effect(ServerEffect.HostCall _) -> true
            | _ -> false)
        |> List.length

    None :: [ for k in 0 .. hostCalls - 1 -> Some k ]

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
              let all = loopCases @ durableCases @ planHaltCases

              let verdicts =
                  [ for c in all do
                        for failAt in positions c do
                            let run = runCase c failAt

                            yield
                                {| Committed = run.Production.Committed
                                   PerformedOutside = List.length (claimedHostCalls run.Production)
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
                  [ for c in loopCases @ durableCases do
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
                  let honest = scripted log None

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
                    Model = runModel (registryFor c (lying modelLog)) c.Resolve "call" c.Handler c.Store
                    ModelLog = List.ofSeq modelLog }

              Expect.equal
                  (projectionOf run.Production)
                  (projectionOf run.Model)
                  "the lie is not a divergence between production and the model — both believed it"

              Expect.equal
                  (claimedHostCalls run.Production)
                  [ "host:first"; "host:second"; "host:third" ]
                  "the handler claims all three ran — the lie was believed"

              Expect.equal run.ProductionLog [ "first"; "third" ] "the log says two ran — the lie was not"

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
