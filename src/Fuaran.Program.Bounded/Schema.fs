namespace Fuaran.Program.Bounded

open Fuaran.Core
open Fuaran.UI.Types
open Fuaran.UI.Ops.Introspect

// ============================================================================
//  The static query-schema walk, and the validator family over it.
//
//  A handler declares a query as a `DataSource` plus an ordered `Transform`
//  list, and lands the result in a named slot; some node in the domain tree
//  reads that slot. Nothing connected the two. A pipeline whose output cannot
//  satisfy its reader was a RUNTIME failure — and a deliberately thin one,
//  because the engine's own error quotes column names taken from the pipeline,
//  and a pipeline is host-declared today but wire-carried after the wire cut, so
//  surfacing that message verbatim would become a payload leak the day the cut
//  lands. The refusal therefore carries the error's DISCRIMINATOR only, and the
//  cost — `UnknownColumn` without the column name — was recorded rather than
//  argued away.
//
//  This file is the other half of that trade. `Transform` is data, so a
//  pipeline's output schema is derivable from its input schema WITHOUT
//  evaluating anything, and a mismatch can therefore be found before the
//  untrusted tree is involved at all. An error raised there can afford to be
//  detailed, so these findings name the column, the reader and what the query
//  actually provides. The runtime posture is untouched: it still says
//  `UnknownColumn` and nothing more, because it is still the thing that runs
//  while a wire-carried pipeline is in scope.
//
//  ── Why a static walk can answer this, and where it stops ───────────────────
//  The verb set is a closed DU, the expression algebra is a closed DU, and
//  neither carries code. So the walk ENUMERATES; it does not analyse, and there
//  is no fixpoint to reach. What it cannot do is see values, and three verbs
//  genuinely depend on them:
//
//    Derive    the output column's NAME is declared, but its TYPE is inferred
//              from the cells the expression produced. So the column is known to
//              exist with an unknown type — not guessed at from the expression,
//              because a guess that disagreed with the evaluator would be worse
//              than no answer.
//    Pivot     the output's value columns are NAMED BY THE DATA — one per
//              distinct value in the `on` column. The index columns are known;
//              the rest are not even countable.
//    Ref       a named source's rows are host-side by design (the wire carries
//              the name, never the data), so its schema is whatever the host
//              declares — and nothing at all when it declares none.
//
//  `SchemaKnowledge` carries that distinction in its shape rather than in a
//  comment: `Closed` means these columns and no others, and it is the ONLY case
//  from which "that column is absent" can be concluded. `AtLeast` means these
//  columns are present and the walk cannot name the rest, so it can confirm a
//  reader but never refute one. A check that cannot refute reports itself as
//  underivable and produces no finding — see `QuerySchemaReport`.
//
//  FORWARD-COUPLING: a new `Transform` verb, a new `ColExpr` case, or a new
//  reading node kind extends `ofTransform` / `readsOfExpr` / `readersOfTree`
//  here. The compiler catches the first two (both matches are over closed DUs
//  with no catch-all); the third is a node-kind addition whose catch-all is
//  deliberate, and is the one to remember.
// ============================================================================

/// What the walk knows about one output column. The name is always known — every
/// verb that adds a column declares its name — and the type is not always, which
/// is why only one of the two is an option.
type ColumnKnowledge =
    {
        Name: string
        /// `None` where the column exists but its type is decidable only from the
        /// data. A `Derive`'s type is inferred from the cells its expression
        /// produced, so it is `None` however simple the expression looks.
        Type: ColumnType option
    }

/// What a static walk knows about a query's output columns.
///
/// Two cases, not three: `AtLeast([], reason)` already says "nothing is known",
/// so a separate opaque case would be a second spelling of one state — and a
/// state with two spellings is one a check eventually gets wrong.
[<RequireQualifiedAccess>]
type SchemaKnowledge =
    /// The column set is CLOSED: these columns, in this order, and no others.
    /// **The only case that supports a negative verdict.**
    | Closed of columns: ColumnKnowledge list
    /// These columns are present; the walk cannot name what else might be. A
    /// reader can still be CONFIRMED against it and can never be REFUTED, and
    /// the reason says which verb or source cost the walk its certainty.
    | AtLeast of columns: ColumnKnowledge list * reason: string

/// The schemas of the named `Ref` sources a host serves — declared at
/// registration, beside the resolver that serves the rows.
///
/// The alternative was to READ the schema by resolving the source at validation
/// time. It was refused: this whole family exists to answer a question BEFORE
/// anything external runs, and a check that reaches a host's data resolver to
/// decide whether a handler may run has already run half the handler. An
/// undeclared name degrades to "unknown" and is reported as such — never to a
/// guess, and never to a refusal, because refusing a handler on the strength of
/// a schema nobody declared would punish the host for not answering a question
/// it was never asked.
type SourceSchemas = Map<string, Schema>

module SourceSchemas =

    /// No source schema declared. The default, and honest: a walk over a `Ref`
    /// under it derives nothing and says so.
    let none: SourceSchemas = Map.empty

    /// Declare the schema a named `Ref` source resolves to.
    let declare (name: string) (schema: Schema) (sources: SourceSchemas) : SourceSchemas = Map.add name schema sources

module Schema =

    // ─── knowledge helpers ───────────────────────────────────────────────────

    /// The columns the walk can name, whichever case it is in.
    let columns (knowledge: SchemaKnowledge) : ColumnKnowledge list =
        match knowledge with
        | SchemaKnowledge.Closed cols -> cols
        | SchemaKnowledge.AtLeast(cols, _) -> cols

    /// The named columns, in schema order.
    let names (knowledge: SchemaKnowledge) : string list = columns knowledge |> List.map _.Name

    /// True when the column set is closed, so an absence is a fact rather than
    /// an ignorance.
    let isClosed (knowledge: SchemaKnowledge) : bool =
        match knowledge with
        | SchemaKnowledge.Closed _ -> true
        | SchemaKnowledge.AtLeast _ -> false

    /// The declared type of a named column: `None` both when the column is
    /// absent and when it is present with an undecidable type. The two are
    /// different facts, and every caller here needs only the same answer for
    /// both — a type it cannot state.
    let typeOf (name: string) (knowledge: SchemaKnowledge) : ColumnType option =
        columns knowledge |> List.tryFind (fun c -> c.Name = name) |> Option.bind _.Type

    /// True when the walk can see this column. False on an `AtLeast` means "not
    /// visible", never "absent".
    let has (name: string) (knowledge: SchemaKnowledge) : bool =
        columns knowledge |> List.exists (fun c -> c.Name = name)

    /// Replace the column list, keeping the case (and its reason).
    let private withColumns (cols: ColumnKnowledge list) (knowledge: SchemaKnowledge) : SchemaKnowledge =
        match knowledge with
        | SchemaKnowledge.Closed _ -> SchemaKnowledge.Closed cols
        | SchemaKnowledge.AtLeast(_, reason) -> SchemaKnowledge.AtLeast(cols, reason)

    /// Add a column, or retype it in place where the name is already known —
    /// which is exactly what the evaluator's `Derive` does, position included.
    let private upsert (column: ColumnKnowledge) (knowledge: SchemaKnowledge) : SchemaKnowledge =
        let cols = columns knowledge

        if cols |> List.exists (fun c -> c.Name = column.Name) then
            knowledge
            |> withColumns (cols |> List.map (fun c -> if c.Name = column.Name then column else c))
        else
            knowledge |> withColumns (cols @ [ column ])

    /// Weaken to `AtLeast`, keeping what is known and recording why the rest is
    /// not. Already-weak knowledge keeps its ORIGINAL reason: the first thing
    /// that cost the walk its certainty is the one worth reporting, and a chain
    /// of "and then this too" reads as a list of causes where there is one.
    let private weaken (reason: string) (knowledge: SchemaKnowledge) : SchemaKnowledge =
        match knowledge with
        | SchemaKnowledge.Closed cols -> SchemaKnowledge.AtLeast(cols, reason)
        | SchemaKnowledge.AtLeast _ -> knowledge

    let private ofColumns (schema: Schema) : ColumnKnowledge list =
        schema |> List.map (fun (name, ty) -> { Name = name; Type = Some ty })

    // ─── the source ──────────────────────────────────────────────────────────

    /// What is known about a `DataSource` before any transform runs.
    let ofSource (sources: SourceSchemas) (source: DataSource) : SchemaKnowledge =
        match source with
        | DataSource.Embedded table -> SchemaKnowledge.Closed(ofColumns table.Schema)
        | DataSource.Ref name ->
            match Map.tryFind name sources with
            | Some schema -> SchemaKnowledge.Closed(ofColumns schema)
            | None ->
                // The name is safe to repeat: a handler's query is host-declared
                // data, so this string is the host's own vocabulary. The one
                // string in this subsystem that comes off the wire is the
                // endpoint an `Action.Call` names, and it appears nowhere here.
                SchemaKnowledge.AtLeast([], $"source '%s{name}' is a Ref with no host-declared schema")

    // ─── the walk ────────────────────────────────────────────────────────────

    /// The columns a `ColExpr` reads from its input row. Total over the closed
    /// expression algebra; duplicates preserved, because a caller that wants
    /// them distinct can say so and one that wants occurrence order cannot
    /// recover it.
    let rec readsOfExpr (expr: ColExpr) : string list =
        match expr with
        | ColExpr.Col name -> [ name ]
        | ColExpr.Lit _
        | ColExpr.Param _ -> []
        | ColExpr.Binary(_, a, b) -> readsOfExpr a @ readsOfExpr b
        | ColExpr.Not x -> readsOfExpr x
        | ColExpr.Coalesce xs -> xs |> List.collect readsOfExpr
        | ColExpr.Case(cases, els) ->
            (cases |> List.collect (fun (w, t) -> readsOfExpr w @ readsOfExpr t))
            @ readsOfExpr els
        | ColExpr.Cast(_, x) -> readsOfExpr x
        | ColExpr.ApplyFn(_, xs) -> xs |> List.collect readsOfExpr
        | ColExpr.InList(x, items) -> readsOfExpr x @ (items |> List.collect readsOfExpr)
        | ColExpr.IsNull x -> readsOfExpr x
        | ColExpr.InParam(x, _) -> readsOfExpr x

    /// The verb's discriminator — log-safe, and the name a finding uses so a
    /// reader can find the step in the declared pipeline.
    let verbOf (step: Transform) : string =
        match step with
        | Transform.Filter _ -> "Filter"
        | Transform.Project _ -> "Project"
        | Transform.Derive _ -> "Derive"
        | Transform.GroupBy _ -> "GroupBy"
        | Transform.Join _ -> "Join"
        | Transform.Window _ -> "Window"
        | Transform.Pivot _ -> "Pivot"
        | Transform.Unpivot _ -> "Unpivot"
        | Transform.Sort _ -> "Sort"
        | Transform.Distinct -> "Distinct"
        | Transform.Limit _ -> "Limit"
        | Transform.Union _ -> "Union"

    /// The columns a step requires its INPUT to carry — the ones whose absence
    /// the evaluator reports as `UnknownColumn`.
    ///
    /// Two verbs resolve columns and do NOT error on a missing one, so they are
    /// deliberately absent from this list rather than accidentally: a `Sort` key
    /// the input lacks is skipped by the comparator, and a `Window`'s
    /// `partitionBy` / `orderBy` keys are filtered to the resolvable ones. Both
    /// are silent behaviours in the evaluator, and reporting them here would
    /// refuse a handler that runs perfectly well. A `Window`'s `of` column is a
    /// genuine requirement — except for `RowNumber` and `Rank`, which never read
    /// it, and which the evaluator's own guard exempts.
    let readsOfTransform (step: Transform) : string list =
        match step with
        | Transform.Filter pred -> readsOfExpr pred
        | Transform.Project pairs -> pairs |> List.map fst
        | Transform.Derive(_, expr) -> readsOfExpr expr
        | Transform.GroupBy(keys, aggs) -> keys @ (aggs |> List.map _.Of)
        | Transform.Join(_, on, _) -> on |> List.map fst
        | Transform.Window spec ->
            match spec.Fn with
            | WindowFn.RowNumber
            | WindowFn.Rank -> []
            | _ -> [ spec.Of ]
        | Transform.Pivot spec -> spec.Index @ [ spec.On; spec.Values ]
        | Transform.Unpivot(idVars, valueVars) -> idVars @ valueVars
        | Transform.Sort _
        | Transform.Distinct
        | Transform.Limit _
        | Transform.Union _ -> []

    /// The type a window function's output column carries. Pinned to the
    /// evaluator's own rule rather than restated loosely: the counting functions
    /// are integers, the accumulating ones are floats, and the shifting pair
    /// keeps the source column's type — which is unknown exactly when the source
    /// column's type is.
    let private windowType (input: SchemaKnowledge) (spec: WindowSpec) : ColumnType option =
        match spec.Fn with
        | WindowFn.RowNumber
        | WindowFn.Rank -> Some ColumnType.IntType
        | WindowFn.CumulSum
        | WindowFn.RollingMean -> Some ColumnType.FloatType
        | WindowFn.Lag
        | WindowFn.Lead -> typeOf spec.Of input

    /// The type an aggregate produces over a source column of `sourceType`.
    /// Where the source type is unknown, only the aggregates that IGNORE it can
    /// still be typed — which is a fact about the aggregate, not a fallback.
    let private aggregateType (fn: AggFn) (sourceType: ColumnType option) : ColumnType option =
        match sourceType with
        | Some ty -> Some(Column.aggType fn ty)
        | None ->
            match fn with
            | AggFn.Count -> Some ColumnType.IntType
            | AggFn.Mean
            | AggFn.Median
            | AggFn.StdDev -> Some ColumnType.FloatType
            | AggFn.Sum
            | AggFn.Min
            | AggFn.Max
            | AggFn.First
            | AggFn.Last -> None

    /// The output schema of ONE transform step over an input schema. Total, and
    /// evaluates nothing: every case is a rearrangement of names and declared
    /// types.
    let ofTransform (sources: SourceSchemas) (input: SchemaKnowledge) (step: Transform) : SchemaKnowledge =
        match step with
        // Row-set verbs. They drop, reorder or dedup rows and touch no column.
        | Transform.Filter _
        | Transform.Sort _
        | Transform.Distinct
        | Transform.Limit _ -> input

        // A union's output is the LEFT schema — the evaluator requires the two
        // to agree before it gets here, and disagreement is a finding rather
        // than a schema.
        | Transform.Union _ -> input

        // Project CLOSES the set however open the input was: the output is
        // exactly the listed columns, in the listed order, whatever else the
        // input happened to carry.
        | Transform.Project pairs ->
            SchemaKnowledge.Closed(
                pairs
                |> List.map (fun (source, out) ->
                    { Name = out
                      Type = typeOf source input })
            )

        // The name is declared; the type is `inferType` over the cells the
        // expression produced, so it is data-dependent and stays unknown.
        | Transform.Derive(name, _) -> upsert { Name = name; Type = None } input

        // GroupBy closes the set too: the key columns then one column per
        // aggregate, and nothing survives that was not named.
        | Transform.GroupBy(keys, aggs) ->
            SchemaKnowledge.Closed(
                (keys |> List.map (fun key -> { Name = key; Type = typeOf key input }))
                @ (aggs
                   |> List.map (fun agg ->
                       { Name = agg.Name
                         Type = aggregateType agg.Fn (typeOf agg.Of input) }))
            )

        | Transform.Window spec ->
            input
            |> upsert
                { Name = spec.As
                  Type = windowType input spec }

        // The index columns are known; the value columns are one per DISTINCT
        // VALUE in the `on` column, which is data. Not even their number is
        // derivable, so the set opens here and every later step inherits that.
        | Transform.Pivot spec ->
            SchemaKnowledge.AtLeast(
                spec.Index
                |> List.map (fun name ->
                    { Name = name
                      Type = typeOf name input }),
                "a pivot's value columns are named by the data — one per distinct value in its `on` column"
            )

        | Transform.Unpivot(idVars, valueVars) ->
            SchemaKnowledge.Closed(
                (idVars
                 |> List.map (fun name ->
                     { Name = name
                       Type = typeOf name input }))
                @ [ { Name = "variable"
                      Type = Some ColumnType.StringType }
                    { Name = "value"
                      // The evaluator types the melted column from the first
                      // value column it can resolve.
                      Type = valueVars |> List.tryPick (fun name -> typeOf name input) } ]
            )

        | Transform.Join(source, _, _) ->
            let right = ofSource sources source
            let leftNames = names input |> Set.ofList

            // The evaluator suffixes a right column whose name collides with a
            // left one. So a right column's OUTPUT name is a function of the
            // left's names — and while any left name is invisible, every right
            // column's name is undecidable between `x` and `x_right`. That is
            // why an open left contributes no right columns at all rather than
            // guessing that no collision occurred.
            match input with
            | SchemaKnowledge.AtLeast(_, reason) ->
                SchemaKnowledge.AtLeast(
                    columns input,
                    reason
                    + " — and a join's right-hand output names depend on the left's, so they cannot be named either"
                )
            | SchemaKnowledge.Closed left ->
                let renamed =
                    columns right
                    |> List.map (fun c ->
                        if Set.contains c.Name leftNames then
                            { c with Name = c.Name + "_right" }
                        else
                            c)

                match right with
                | SchemaKnowledge.Closed _ -> SchemaKnowledge.Closed(left @ renamed)
                | SchemaKnowledge.AtLeast(_, reason) -> SchemaKnowledge.AtLeast(left @ renamed, reason)

    /// The output schema of a whole pipeline over a source. Total.
    let ofPipeline (sources: SourceSchemas) (source: DataSource) (pipeline: Transform list) : SchemaKnowledge =
        pipeline |> List.fold (ofTransform sources) (ofSource sources source)

// ============================================================================
//  The validator family.
// ============================================================================

/// Which query a finding is about. Both fields are HOST-declared strings: a
/// handler is registered by the host, so its name and the slot its query lands
/// in are the host's own vocabulary, not anything a generated tree supplied.
type QueryOrigin = { Handler: string; Slot: string }

/// A node that reads a query slot, and the columns it needs.
///
/// **`NodeId` and `Fields` come OFF THE WIRE**, unlike everything on
/// `QueryOrigin`. They are carried in full because this family's whole purpose
/// is to be detailed where the runtime cannot be — but a host that pipes these
/// findings verbatim into a shared log is echoing a generated tree's strings,
/// and should know it is choosing to.
type QueryReader =
    {
        /// The reading node.
        NodeId: string
        /// The query slot it is bound to.
        Slot: string
        /// The columns it names.
        Fields: string list
        /// True when the node ALSO projects rows through a closure, so `Fields`
        /// is a lower bound on what it reads.
        ClosureHeld: bool
    }

/// A mismatch the walk could PROVE. Every case names the query, and every case
/// carries the detail the runtime diagnostic deliberately withholds — this side
/// runs before the untrusted tree does, so it can.
[<RequireQualifiedAccess>]
type QuerySchemaFinding =
    /// A pipeline step reads a column its input does not carry. This is the
    /// runtime's `UnknownColumn`, moved forward and given back its name.
    /// `step` is the 0-based position in the declared pipeline.
    | UnknownColumn of query: QueryOrigin * step: int * verb: string * column: string * available: string list
    /// A `Union`'s two sides do not present the same columns in the same order,
    /// which the evaluator refuses. Order matters: it compares the two name
    /// lists, not the two name sets.
    | UnionColumnMismatch of query: QueryOrigin * step: int * left: string list * right: string list
    /// A reader needs a column the query's output does not provide. The finding
    /// this family exists for.
    | ReaderColumnMissing of query: QueryOrigin * reader: string * column: string * available: string list

/// What the static walk decided, and what it declined to decide.
///
/// The split is load-bearing. `Findings` are proofs, and a strict construction
/// refuses on them. `Underivable` and `OpaqueReaders` are DATA — a query whose
/// output is not statically closed and a reader whose projection is closure-held
/// are both perfectly legitimate, and raising them as failures would fire on
/// ordinary trees until the whole check learned to be ignored. It is the same
/// choice `DemandedProjection.OpaqueHandlers` makes, for the same reason.
type QuerySchemaReport =
    { Findings: QuerySchemaFinding list
      Underivable: (QueryOrigin * string) list
      OpaqueReaders: (QueryOrigin * string) list }

module QuerySchemaReport =

    let empty: QuerySchemaReport =
        { Findings = []
          Underivable = []
          OpaqueReaders = [] }

    let append (a: QuerySchemaReport) (b: QuerySchemaReport) : QuerySchemaReport =
        { Findings = a.Findings @ b.Findings
          Underivable = a.Underivable @ b.Underivable
          OpaqueReaders = a.OpaqueReaders @ b.OpaqueReaders }

    let concat (reports: QuerySchemaReport seq) : QuerySchemaReport = reports |> Seq.fold append empty

module QuerySchema =

    // ─── the reader side ─────────────────────────────────────────────────────

    /// The query slot a row source is bound to, when it is bound to one at all.
    ///
    /// Only a direct `Binding.Query` is a query read. A `Binding.Transform` over
    /// a query is a reader that transforms what it reads, and its expectation is
    /// the composition of this walk with its own pipeline — real, and deliberately
    /// not attempted here: it is a second question (what does a CLIENT-side
    /// pipeline need of a server-side one) and answering half of it would be
    /// worse than saying so.
    let private slotOf (source: Binding<Fuaran.Core.Row seq>) : string option =
        match source with
        | Binding.Query(name, _, _) -> Some name
        | _ -> None

    /// The columns a node needs of the query slot it reads, where it reads one.
    ///
    /// The two reading kinds are the two the wire has: a grid's columns name
    /// their fields, and a chart names its axes. Both are ordinary wire-carried
    /// strings, which is the whole reason this check is possible — see D10.
    ///
    /// `ClosureHeld` covers only the projections that decide WHICH COLUMNS ARE
    /// READ: a grid column with no `field` (its content IS a closure) and a
    /// closure row key. An `onRowClick` / `onPointClick` handler is deliberately
    /// not counted — it reads a row to build an action rather than to display a
    /// column, so no addition to the query could satisfy it, and it is already
    /// reported on the demanded projection's opaque-handler list.
    let private readerOf (node: Node<obj>) : QueryReader option =
        let reader slot fields closureHeld =
            Some
                { NodeId = node.Id
                  Slot = slot
                  Fields = fields |> List.distinct
                  ClosureHeld = closureHeld }

        match node.Kind with
        | NodeKind.DataGrid spec ->
            match slotOf spec.Source with
            | None -> None
            | Some slot ->
                let declared = spec.Columns |> List.choose _.Field
                let closureHeld = spec.Columns |> List.exists (fun c -> Option.isNone c.Field)

                reader slot (declared @ Option.toList spec.RowKeyField) (closureHeld || Option.isSome spec.RowKey)

        | NodeKind.Chart spec ->
            match slotOf spec.Source with
            | None -> None
            | Some slot -> reader slot (spec.XField :: spec.YFields) false

        // Every other kind reads no table. The catch-all is deliberate and is
        // the one place the compiler cannot help: a NEW row-reading node kind
        // lands here silently, and belongs above.
        | _ -> None

    /// Every query-slot reader in a tree, in traversal order.
    ///
    /// Walks the whole traversal surface (`descendantNodes` — the structural
    /// children AND the non-list slots such as a state-behaviour branch), so a
    /// grid parked in a loading state is not missed.
    let readersOfTree (root: Node<obj>) : QueryReader list =
        let rec walk (node: Node<obj>) =
            (readerOf node |> Option.toList) @ (descendantNodes node |> List.collect walk)

        walk root

    // ─── the check ───────────────────────────────────────────────────────────

    /// Human-readable, detailed description of a finding. Unlike the runtime
    /// diagnostics beside it, this quotes names — that is the point.
    let describe (finding: QuerySchemaFinding) : string =
        let list (names: string list) =
            if List.isEmpty names then
                "<none>"
            else
                String.concat ", " names

        match finding with
        | QuerySchemaFinding.UnknownColumn(query, step, verb, column, available) ->
            $"query '%s{query.Slot}' (handler '%s{query.Handler}'): step %d{step} (%s{verb}) reads column '%s{column}', "
            + $"which its input does not carry — available: %s{list available}"
        | QuerySchemaFinding.UnionColumnMismatch(query, step, left, right) ->
            $"query '%s{query.Slot}' (handler '%s{query.Handler}'): step %d{step} (Union) joins schemas that do not match — "
            + $"left: %s{list left}; right: %s{list right}"
        | QuerySchemaFinding.ReaderColumnMissing(query, reader, column, available) ->
            $"node '%s{reader}' reads column '%s{column}' from query '%s{query.Slot}' (handler '%s{query.Handler}'), "
            + $"which the query does not produce — available: %s{list available}"

    /// The findings ONE step contributes, given what is known of its input.
    ///
    /// A required column that the input cannot be seen to carry is a finding
    /// only when the input is CLOSED. On an open input the column may well be
    /// there, and a check that guessed would refuse working handlers.
    let private stepFindings
        (sources: SourceSchemas)
        (origin: QueryOrigin)
        (index: int)
        (input: SchemaKnowledge)
        (step: Transform)
        : QuerySchemaFinding list =
        let missing =
            if Schema.isClosed input then
                Schema.readsOfTransform step
                |> List.distinct
                |> List.filter (fun column -> not (Schema.has column input))
                |> List.map (fun column ->
                    QuerySchemaFinding.UnknownColumn(origin, index, Schema.verbOf step, column, Schema.names input))
            else
                []

        // The verbs that reach a SECOND schema check their own half against it:
        // a join's right-hand keys, and a union's whole column list.
        let againstRight =
            match step with
            | Transform.Join(source, on, _) ->
                let right = Schema.ofSource sources source

                if Schema.isClosed right then
                    on
                    |> List.map snd
                    |> List.distinct
                    |> List.filter (fun column -> not (Schema.has column right))
                    |> List.map (fun column ->
                        QuerySchemaFinding.UnknownColumn(origin, index, "Join", column, Schema.names right))
                else
                    []

            | Transform.Union source ->
                let right = Schema.ofSource sources source

                // Both sides closed, or there is nothing to compare: the
                // evaluator compares the ORDERED name lists, so two schemas
                // carrying the same names in a different order are a refusal.
                if
                    Schema.isClosed input
                    && Schema.isClosed right
                    && Schema.names input <> Schema.names right
                then
                    [ QuerySchemaFinding.UnionColumnMismatch(origin, index, Schema.names input, Schema.names right) ]
                else
                    []

            | _ -> []

        missing @ againstRight

    /// Check one query — a source, a pipeline, and the slot it lands in —
    /// against the readers of that slot.
    ///
    /// Readers of OTHER slots are filtered out here rather than by the caller,
    /// so a caller cannot accidentally check a reader against the wrong query.
    let checkQuery
        (sources: SourceSchemas)
        (origin: QueryOrigin)
        (source: DataSource)
        (pipeline: Transform list)
        (readers: QueryReader list)
        : QuerySchemaReport =
        let mine = readers |> List.filter (fun r -> r.Slot = origin.Slot)

        let output, _, pipelineFindings =
            pipeline
            |> List.fold
                (fun (input, index, acc) step ->
                    let findings = stepFindings sources origin index input step
                    Schema.ofTransform sources input step, index + 1, acc @ findings)
                (Schema.ofSource sources source, 0, [])

        let readerFindings =
            if Schema.isClosed output then
                mine
                |> List.collect (fun reader ->
                    reader.Fields
                    |> List.filter (fun column -> not (Schema.has column output))
                    |> List.map (fun column ->
                        QuerySchemaFinding.ReaderColumnMissing(origin, reader.NodeId, column, Schema.names output)))
            else
                []

        { Findings = pipelineFindings @ readerFindings
          Underivable =
            match output with
            | SchemaKnowledge.Closed _ -> []
            | SchemaKnowledge.AtLeast(_, reason) -> [ origin, reason ]
          OpaqueReaders = mine |> List.filter _.ClosureHeld |> List.map (fun r -> origin, r.NodeId) }
