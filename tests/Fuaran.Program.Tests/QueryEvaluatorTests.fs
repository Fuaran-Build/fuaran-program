module Fuaran.Program.Tests.QueryEvaluatorTests

// ============================================================================
//  The query evaluator seam (Phase 1905, DECISIONS.md D34), at the toy witness.
//
//  What is pinned here:
//    * the law family: the in-memory fold certifies trivially, a host
//      evaluator that pushes a filter down to its source certifies and answers
//      the fold's table, and one that answers differently is reported with the
//      fixture it disagreed on;
//    * the handler: an absent evaluator is the fold (the existing suites pin
//      that byte for byte); a declared pure read runs in the plan phase,
//      unstaged; an evaluator its host could not declare a pure read is STAGED
//      like a host call — never asked when the plan halts, asked once after it
//      completes, one-way for undo;
//    * the gate and the argument policy decide before any evaluator is asked;
//    * the schema check: a host answer the statically derived schema refutes
//      halts, naming both, whether the query was staged or not;
//    * the durable interpreter journals a staged query at its ordinal and a
//      replay serves it without asking the evaluator again;
//    * the facets read the declaration, and the check finds a staged
//      evaluator they were not told about.
// ============================================================================

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain

type private ToyHandler = Handler<ToyAction, ToyOp>
type private ToyServerStore = ServerStore<ToyNode, ToyStore>

let private column (name: string) (ty: ColumnType) (cells: Cell list) : Column =
    { Name = name
      Type = ty
      Cells = cells }

let private tableOf (columns: Column list) : Table =
    { Schema = columns |> List.map (fun c -> c.Name, c.Type)
      Columns = columns }

/// The host's named source: three orders.
let private orders: Table =
    tableOf
        [ column "id" IntType [ Int 1; Int 2; Int 3 ]
          column "amount" IntType [ Int 5; Int 50; Int 500 ] ]

let private environment: Map<string, Table> = Map.ofList [ "orders", orders ]

let private resolve = QueryEvaluatorLaws.resolverOf environment

let private overTen: Transform = Filter(Binary(Gt, Col "amount", Lit(Int 10)))

let private bigOrders: Transform list = [ overTen; Project [ "id", "id" ] ]

/// A host evaluator that pushes a leading filter down to its source: the
/// source answers only the matching rows, and the rest of the pipeline is
/// folded over what came back. What a database adapter does, in miniature.
let private pushdown
    (calls: int ref)
    : DataSource -> Transform list -> (string -> Result<Table, EvalError>) -> Result<Table, QueryFault> =
    fun source pipeline resolve ->
        calls.Value <- calls.Value + 1

        let atSource, rest =
            match pipeline with
            | (Filter _ as filter) :: rest -> [ filter ], rest
            | _ -> [], pipeline

        DataFrame.evalSource resolve source
        |> Result.bind (DataFrame.evalPipelineWith resolve atSource)
        |> Result.bind (DataFrame.evalPipelineWith resolve rest)
        |> Result.mapError QueryFault.Eval

/// A host evaluator that ignores the pipeline's filter.
let private forgetful: DataSource -> Transform list -> (string -> Result<Table, EvalError>) -> Result<Table, QueryFault> =
    fun source pipeline resolve ->
        let kept =
            pipeline
            |> List.filter (fun step ->
                match step with
                | Filter _ -> false
                | _ -> true)

        QueryEvaluator.fold source kept resolve

/// A host evaluator answering a table of a fixed shape whatever it is asked.
let private answering (table: Table) (calls: int ref) =
    fun (_: DataSource) (_: Transform list) (_: string -> Result<Table, EvalError>) ->
        calls.Value <- calls.Value + 1
        Ok table

let private fixtures: QueryFixture list =
    [ { Name = "embedded, no pipeline"
        Source = Embedded orders
        Pipeline = []
        Sources = Map.empty }
      { Name = "a named source, filtered and projected"
        Source = Ref "orders"
        Pipeline = bigOrders
        Sources = environment }
      { Name = "an unresolved source"
        Source = Ref "elsewhere"
        Pipeline = [ overTen ]
        Sources = environment } ]

let private leaf (id: string) : ToyNode =
    { Id = id
      Label = Const(JStr id)
      Handlers = []
      Children = [] }

let private store: ToyServerStore =
    { Tree =
        { leaf "root" with
            Children = [ leaf "call" ] }
      Bindings = Map.empty }

let private handler (stages: HandlerStage<ToyAction, ToyOp> list) : ToyHandler = { Name = "query"; Stages = stages }

let private permissive: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll

let private withAudit (registry: ServerEffectRegistry) =
    registry |> ServerEffectRegistry.register "audit" (fun args -> Ok args)

let private run (registry: ServerEffectRegistry) (stages: HandlerStage<ToyAction, ToyOp> list) =
    Handler.run witness registry resolve "call" (handler stages) store

let private query = Effect(ServerEffect.RunQuery("big", Ref "orders", bigOrders))

/// The toy store lands a query as its column count.
let private landed (outcome: HandlerOutcome<ToyNode, ToyStore, ToyOp, ToyEffect>) =
    Map.tryFind "big" outcome.Store.Bindings

let private ordersSchema: SourceSchemas =
    SourceSchemas.none |> SourceSchemas.declare "orders" orders.Schema

[<Tests>]
let tests =
    testList
        "query evaluator seam (Phase 1905)"
        [ testList
              "the law family"
              [ testCase "the in-memory fold certifies trivially"
                <| fun () ->
                    Expect.isEmpty
                        (QueryEvaluatorLaws.certify QueryEvaluator.inMemory fixtures)
                        "the reference agrees with itself"

                testCase "a host evaluator that pushes a filter down certifies, and answers the fold's table"
                <| fun () ->
                    let calls = ref 0
                    let evaluator = QueryEvaluator.reaching (pushdown calls)
                    Expect.isEmpty (QueryEvaluatorLaws.certify evaluator fixtures) "pushdown agrees with the fold"

                    let pushed = evaluator.Evaluate (Ref "orders") bigOrders resolve
                    let folded = QueryEvaluator.fold (Ref "orders") bigOrders resolve

                    match pushed, folded with
                    | Ok a, Ok b ->
                        Expect.equal
                            (ColumnCodec.encode (Embedded a))
                            (ColumnCodec.encode (Embedded b))
                            "the same table, byte for byte in canonical form"

                        Expect.equal (List.length (List.head a.Columns).Cells) 2 "the filter ran: two orders over ten"
                    | _ -> failtest "both evaluators answer a table"

                testCase "an evaluator that differs is reported with the fixture"
                <| fun () ->
                    let findings =
                        QueryEvaluatorLaws.certify (QueryEvaluator.reaching forgetful) fixtures

                    Expect.equal
                        (findings |> List.map _.Fixture)
                        [ "a named source, filtered and projected" ]
                        "only the filtered fixture differs"

                    let finding = List.head findings
                    Expect.notEqual finding.Reference finding.Answered "the finding carries both answers"
                    Expect.stringStarts finding.Reference "table " "the reference answered a table" ]

          testList
              "the handler"
              [ testCase "no evaluator: the registry default is the fold"
                <| fun () ->
                    Expect.isNone ServerEffectRegistry.denyAll.QueryEvaluator "absent is the default"
                    let outcome = run permissive [ query ]
                    Expect.isTrue outcome.Committed "the fold answered"
                    Expect.equal outcome.Performed [ "RunQuery" ] "performed in the plan phase"
                    Expect.equal (landed outcome) (Some(JInt 1)) "one projected column landed"

                testCase "a declared pure read runs in the plan phase, unstaged"
                <| fun () ->
                    let calls = ref 0

                    let registry =
                        permissive
                        |> withAudit
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.pureRead (pushdown calls))

                    let outcome =
                        run registry [ Effect(ServerEffect.HostCall("audit", JStr "a", None)); query ]

                    Expect.isTrue outcome.Committed "committed"
                    Expect.equal calls.Value 1 "asked once"
                    // Plan-phase capabilities first, then what the perform phase ran:
                    // the query was not staged.
                    Expect.equal outcome.Performed [ "RunQuery"; "host:audit" ] "the query ran in the plan phase"
                    Expect.equal (landed outcome) (Some(JInt 1)) "its table landed"

                testCase "an evaluator its host could not declare a pure read is staged like a host call"
                <| fun () ->
                    let calls = ref 0

                    let registry =
                        permissive
                        |> withAudit
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.reaching (pushdown calls))

                    let outcome =
                        run registry [ Effect(ServerEffect.HostCall("audit", JStr "a", None)); query ]

                    Expect.isTrue outcome.Committed "committed"
                    Expect.equal calls.Value 1 "asked once, in the perform phase"

                    Expect.equal
                        outcome.Performed
                        [ "host:audit"; "RunQuery" ]
                        "performed in plan order, after the plan"

                    Expect.equal (landed outcome) (Some(JInt 1)) "its table landed in the slot"

                testCase "a staged evaluator is never asked when the plan halts"
                <| fun () ->
                    let calls = ref 0

                    let registry =
                        permissive
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.reaching (pushdown calls))

                    let outcome =
                        run registry [ query; Effect(ServerEffect.HostCall("unregistered", JStr "a", None)) ]

                    Expect.isFalse outcome.Committed "the plan halted"
                    Expect.equal calls.Value 0 "nothing reached outside"
                    Expect.isEmpty outcome.Performed "nothing performed"
                    Expect.isNone (landed outcome) "nothing landed"

                testCase "a staged query is one-way for undo"
                <| fun () ->
                    let registry =
                        permissive
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.reaching (pushdown (ref 0)))

                    let _, plan =
                        Handler.runPlanned
                            witness
                            registry
                            OpPerformance.InMemory
                            resolve
                            "call"
                            (handler [ query ])
                            store

                    let reached =
                        plan.Steps
                        |> List.exists (fun step ->
                            match step with
                            | UndoStep.Reached "RunQuery" -> true
                            | _ -> false)

                    Expect.isTrue reached "the trail records that the run reached outside"

                testCase "the gate decides before any evaluator is asked"
                <| fun () ->
                    let calls = ref 0

                    let registry =
                        ServerEffectRegistry.denyAll
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.pureRead (pushdown calls))

                    let outcome = run registry [ query ]
                    Expect.isFalse outcome.Committed "refused"
                    Expect.equal calls.Value 0 "the evaluator was never asked"

                    Expect.equal
                        outcome.Diagnostics
                        [ ServerDiagnostic.Denied(ServerEffectDenial.GateRefused "RunQuery") ]
                        "the gate's denial, by capability" ]

          testList
              "the schema check"
              [ let wrongShape =
                    tableOf [ column "id" IntType [ Int 2 ]; column "secret" StringType [ Str "x" ] ]

                for posture, construct, diagnose in
                    [ "pure read", QueryEvaluator.pureRead, ServerDiagnostic.Failed
                      "staged", QueryEvaluator.reaching, ServerDiagnostic.PerformFailed ] do
                    testCase $"a {posture} answer the derived schema refutes halts, naming both"
                    <| fun () ->
                        let registry =
                            permissive
                            |> ServerEffectRegistry.withQueryEvaluator (construct (answering wrongShape (ref 0)))
                            |> ServerEffectRegistry.checkingQueries ordersSchema

                        let outcome = run registry [ query ]
                        Expect.isFalse outcome.Committed "halted"

                        Expect.equal
                            outcome.Diagnostics
                            [ diagnose (
                                  "RunQuery",
                                  "query-schema-mismatch: expected [id:int], answered [id:int, secret:string]"
                              ) ]
                            "the halt names the derived schema and the answer"

                testCase "an answer that agrees passes, in any column order"
                <| fun () ->
                    let reordered =
                        tableOf [ column "amount" IntType [ Int 50 ]; column "id" IntType [ Int 2 ] ]

                    let registry =
                        permissive
                        |> ServerEffectRegistry.withQueryEvaluator (
                            QueryEvaluator.pureRead (answering reordered (ref 0))
                        )
                        |> ServerEffectRegistry.checkingQueries ordersSchema

                    let outcome =
                        run registry [ Effect(ServerEffect.RunQuery("big", Ref "orders", [ overTen ])) ]

                    Expect.isTrue outcome.Committed "the answer conforms"

                testCase "no evaluator: checking changes nothing"
                <| fun () ->
                    let registry = permissive |> ServerEffectRegistry.checkingQueries ordersSchema
                    Expect.isNone registry.QueryEvaluator "the fold is not checked against itself"

                testCase "the server placement composes the check from its declared schemas"
                <| fun () ->
                    let services =
                        ServerServices.createPermissive witness
                        |> ServerServices.withQueryEvaluator (QueryEvaluator.pureRead (answering wrongShape (ref 0)))
                        |> ServerServices.withSourceSchema "orders" orders.Schema

                    let registry = ServerServices.effects services
                    let outcome = Handler.run witness registry resolve "call" (handler [ query ]) store

                    Expect.isFalse outcome.Committed "the declared schema refutes the answer" ]

          testList
              "the durable interpreter"
              [ testCase "a staged query is journaled at its ordinal, and a replay serves it"
                <| fun () ->
                    let calls = ref 0
                    let journal = Journal.inMemory ()

                    let services =
                        DurableServices.create
                        |> DurableServices.withJournal journal
                        |> DurableServices.declaringQueryEvaluator IdempotencyFacet.Idempotent

                    let registry =
                        permissive
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.reaching (pushdown calls))

                    let first =
                        Durable.run witness services "inv" registry resolve "call" (handler [ query ]) store

                    Expect.isTrue first.Outcome.Committed "the first run committed"
                    Expect.equal first.Invoked [ 0 ] "the query was the step at ordinal 0"
                    Expect.equal calls.Value 1 "asked once"

                    let again =
                        Durable.run witness services "inv" registry resolve "call" (handler [ query ]) store

                    Expect.equal again.Replayed [ 0 ] "the replay served ordinal 0 from the journal"
                    Expect.equal calls.Value 1 "and did not ask the evaluator again"
                    Expect.equal (landed again.Outcome) (landed first.Outcome) "the same table landed"

                testCase "a pure read is not journaled"
                <| fun () ->
                    let registry =
                        permissive
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.pureRead (pushdown (ref 0)))

                    let outcome =
                        Durable.run
                            witness
                            (DurableServices.create |> DurableServices.withJournal (Journal.inMemory ()))
                            "inv"
                            registry
                            resolve
                            "call"
                            (handler [ query ])
                            store

                    Expect.isTrue outcome.Outcome.Committed "committed"
                    Expect.isEmpty outcome.Invoked "no performer step" ]

          testList
              "the facets"
              [ let discipline =
                    PlacementDiscipline.DeterministicReplay
                        { JournalSurvivesRestart = true
                          ReinvokeIndeterminate = false }

                testCase "undeclared, RunQuery derives as the recomputed read it always was"
                <| fun () ->
                    let derived =
                        Facets.ofHandler discipline PerformerFacets.none OpPerformance.InMemory (handler [ query ])

                    Expect.equal derived.Idempotency IdempotencyFacet.Idempotent "a read repeats freely"

                testCase "a declared staged evaluator derives on a host call's terms"
                <| fun () ->
                    let performers =
                        PerformerFacets.none
                        |> PerformerFacets.declareQueryEvaluator IdempotencyFacet.NonIdempotent

                    let derived =
                        Facets.ofHandler discipline performers OpPerformance.InMemory (handler [ query ])

                    Expect.notEqual derived.Idempotency IdempotencyFacet.Idempotent "no longer the free read"

                testCase "the check finds a staged evaluator the facets were not told about"
                <| fun () ->
                    let staged =
                        permissive
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.reaching (pushdown (ref 0)))

                    let pureRead =
                        permissive
                        |> ServerEffectRegistry.withQueryEvaluator (QueryEvaluator.pureRead (pushdown (ref 0)))

                    let declared =
                        PerformerFacets.none
                        |> PerformerFacets.declareQueryEvaluator IdempotencyFacet.Idempotent

                    Expect.isTrue
                        (Facets.undeclaredQueryEvaluator PerformerFacets.none staged [ handler [ query ] ])
                        "staged, undeclared"

                    Expect.isFalse
                        (Facets.undeclaredQueryEvaluator declared staged [ handler [ query ] ])
                        "staged, declared"

                    Expect.isFalse
                        (Facets.undeclaredQueryEvaluator PerformerFacets.none pureRead [ handler [ query ] ])
                        "a pure read"

                    Expect.isFalse
                        (Facets.undeclaredQueryEvaluator PerformerFacets.none permissive [ handler [ query ] ])
                        "the fold" ] ]
