/// A FOURTH witness of the generic core, written for Phase 1992: a GRID — cells
/// keyed by permanent identities, ops that write a cell, and named regions that
/// grow by a row — instantiating the state axis only, the way the verb
/// (`VerbDomain.fs`) does. It exists to exercise per-element iteration (Phase
/// 1990, D29) against the loops a spreadsheet's automation actually writes
/// before a real spreadsheet domain depends on it: a column of fixed
/// identities, a two-variable grid as a nested `Each`, a copy-values loop that
/// reads one identity and writes another, `For i = a To b` over a range, and an
/// `If` inside the loop body — and, since Phase 2186, a computed write through
/// the value channel (`LetValue`, D42). No UI type anywhere, like the toy and
/// the verb.
///
/// Its composition is `ProgramWitness<Grid, CellOp, Unfilled, Unfilled>`: a
/// grid's automation is a handler over its cells, reached by no event and
/// walking no tree, so the walk and dispatch axes are unfilled BY COMPOSITION
/// (D20) rather than filled vacuously. Every one of the state axis's nine
/// members is meaningful here — `Diff` included, which the verb leaves empty —
/// and `docs/generic-tier.md` §3.12 records the census and the findings.
module Fuaran.Program.Tests.GridDomain

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Runtime
open Fuaran.Program.Server

// ─── the vocabulary ─────────────────────────────────────────────────────────

/// The grid's state. A cell is named by a PERMANENT identity (`r2c3`), never
/// by a position an insert would move; a region is a named, ordered list of
/// row identities that an op can grow. A region is never held empty: dropping
/// its last row removes it, so appending a row and dropping it are exact
/// inverses under `canonical`.
type Grid =
    { Cells: Map<string, string>
      Regions: Map<string, string list> }

/// The grid's EXPRESSIONS (Phase 2186, D42): what a computed write reads of
/// the plan. A cell's value, or an integer multiple of one — the smallest
/// language that writes `Cells(i, 4) = Cells(i, 3) * 2`. The domain's own
/// spelling: the core resolves it through the grid's `ExprWitness` over the
/// GRID as the store (`exprWitness`), and never sees inside it.
type CellExpr =
    /// The cell's value: a string, as the grid holds it; an EMPTY cell does
    /// not resolve (`ExprResolution.NotResolved`).
    | CellValue of cell: string
    /// `factor` times the value, read as an integer; a value that is not one
    /// is the domain's typed refusal (`ExprResolution.Errored`).
    | Times of expr: CellExpr * factor: int

/// The grid's ops: the three cell writes a spreadsheet's automation performs
/// (set, clear, copy one identity's value onto another), a region's growth and
/// its inverse, a guard on a cell being filled, and four flow ops the state
/// witness VIEWS as the core's: an `If <cell> <> ""` as `Choose`, a
/// `For Each` over a literal collection as `Each`, a `For i = a To b` as an
/// `Each` over the range's integers, and a computed value bound over a body
/// as `Let` — sugar the domain resolves in its view, with no construct of
/// the core's for it.
type CellOp =
    | Set of cell: string * value: string
    | Clear of cell: string
    /// Copy the SOURCE cell's value onto the TARGET cell — the read the
    /// copy-values loop needs, fused into one op. It STAYS beside the value
    /// channel (Phase 2186) because its empty-source case is a DEFAULT — an
    /// empty source clears the target, as a spreadsheet's copy does — and
    /// the channel refuses an unresolved value by design; a copy through the
    /// channel is a `LetValue` under a `WhenFilled` (the `copy through the
    /// channel` test), which is the same act spelled without the default.
    | Copy of source: string * target: string
    /// `v = <expr>` over a body that reads `{v}` (Phase 2186, D42): the value
    /// channel. Viewed as `OpView.Let`, so the core resolves the expression
    /// against the plan as of this position, substitutes the value for the
    /// placeholder in the body, and plans the body — the domain's `Set`
    /// never carries an expression, and `apply` never evaluates one.
    | LetValue of placeholder: string * expr: CellExpr * body: CellOp list
    | AppendRow of region: string * row: string
    /// Drop the region's LAST row — the exact inverse of an append.
    | DropRow of region: string
    /// The guard: the plan holds a value at this cell.
    | Filled of cell: string
    /// `If <cell> <> "" Then <body>`: viewed as `Choose(Filled cell, body, [], None)`.
    | WhenFilled of cell: string * body: CellOp list
    | ForEach of collection: JVal list * placeholder: string * body: CellOp list
    /// `For <placeholder> = first To last` — VBA's semantics, so a range whose
    /// first exceeds its last runs no iteration. Viewed as an `Each` over the
    /// integers `first .. last`.
    | ForRange of first: int * last: int * placeholder: string * body: CellOp list
    /// `For Each r In Range("orders")` resolved at RUN time (Phase 1991, class
    /// B): the region the grid holds when the loop is entered, read once,
    /// under a ceiling the automation declares.
    | ForRegion of region: string * ceiling: int * placeholder: string * body: CellOp list

/// The collection a range stands for: its integers, in order, as the elements
/// an `Each` substitutes.
let rangeElements (first: int) (last: int) : JVal list = [ first..last ] |> List.map JInt

// ─── placeholders in operands (Phase 1990) ──────────────────────────────────

/// A placeholder in an operand: a cell identity, a value, a region or a row
/// carrying `{name}` reads the placeholder `name` — the verb's spelling, the
/// domain's own; the core never sees it.
module Placeholder =
    let private pattern =
        System.Text.RegularExpressions.Regex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")

    let names (text: string) : string list =
        pattern.Matches text |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq

    /// A string element verbatim; any other element in its canonical rendering,
    /// so the integer 2 fills `r{i}c3` as `r2c3`.
    let fill (placeholder: string) (element: JVal) (text: string) : string =
        let value =
            match element with
            | JStr s -> s
            | other -> Canon.render other

        text.Replace("{" + placeholder + "}", value)

/// The cells an expression reads, in reading order.
let rec exprCells (expr: CellExpr) : string list =
    match expr with
    | CellValue cell -> [ cell ]
    | Times(inner, _) -> exprCells inner

/// The placeholder filled in every cell an expression names.
let rec substituteExpr (placeholder: string) (element: JVal) (expr: CellExpr) : CellExpr =
    match expr with
    | CellValue cell -> CellValue(Placeholder.fill placeholder element cell)
    | Times(inner, factor) -> Times(substituteExpr placeholder element inner, factor)

/// The grid's `Substitute`: the element written over the placeholder in every
/// operand of the op and of the ops beneath it, the op's shape untouched. A
/// nested `ForEach`, `ForRange` or `LetValue` keeps its own collection or
/// expression and placeholder; the expression's cells are operands too.
let rec substituteOp (placeholder: string) (element: JVal) (op: CellOp) : CellOp =
    let fill = Placeholder.fill placeholder element
    let sub = substituteOp placeholder element

    match op with
    | Set(cell, value) -> Set(fill cell, fill value)
    | Clear cell -> Clear(fill cell)
    | Copy(source, target) -> Copy(fill source, fill target)
    | AppendRow(region, row) -> AppendRow(fill region, fill row)
    | DropRow region -> DropRow(fill region)
    | Filled cell -> Filled(fill cell)
    | WhenFilled(cell, body) -> WhenFilled(fill cell, body |> List.map sub)
    | ForEach(collection, name, body) -> ForEach(collection, name, body |> List.map sub)
    | ForRange(first, last, name, body) -> ForRange(first, last, name, body |> List.map sub)
    | ForRegion(region, ceiling, name, body) -> ForRegion(region, ceiling, name, body |> List.map sub)
    | LetValue(name, expr, body) -> LetValue(name, substituteExpr placeholder element expr, body |> List.map sub)

/// The grid's `Placeholders`: the names an op's OWN operands read. A
/// `WhenFilled`'s condition is an op the core asks on its own (it is the
/// `Choose`'s entry), so the flow op itself reads nothing, as the verb's
/// branch reads nothing. A `LetValue`'s EXPRESSION is its own operand — the
/// cells it reads may carry an enclosing loop's placeholder — while the name
/// it binds is the body's, and the core scopes that.
let placeholdersOf (op: CellOp) : string list =
    match op with
    | Set(cell, value) -> Placeholder.names cell @ Placeholder.names value
    | Clear cell
    | Filled cell -> Placeholder.names cell
    | Copy(source, target) -> Placeholder.names source @ Placeholder.names target
    | AppendRow(region, row) -> Placeholder.names region @ Placeholder.names row
    | DropRow region -> Placeholder.names region
    | LetValue(_, expr, _) -> exprCells expr |> List.collect Placeholder.names
    | WhenFilled _
    | ForEach _
    | ForRange _
    | ForRegion _ -> []

// ─── the codec ──────────────────────────────────────────────────────────────

let rec encodeExpr (expr: CellExpr) : JVal =
    match expr with
    | CellValue cell -> Canon.typed "CellValue" [ "cell", JStr cell ]
    | Times(inner, factor) -> Canon.typed "Times" [ "expr", encodeExpr inner; "factor", JInt factor ]

let rec decodeExpr (value: JVal) : Result<CellExpr, string> =
    match value with
    | JObj members ->
        let field (name: string) =
            members |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

        match field "$type", field "cell", field "expr", field "factor" with
        | Some(JStr "CellValue"), Some(JStr cell), _, _ -> Ok(CellValue cell)
        | Some(JStr "Times"), _, Some inner, Some(JInt factor) ->
            decodeExpr inner |> Result.map (fun inner -> Times(inner, int factor))
        | Some(JStr other), _, _, _ -> Error(sprintf "'%s' is not a grid expression" other)
        | _ -> Error "the expression carries no '$type'"
    | _ -> Error "the expression is not a JSON object"

let rec encodeOp (op: CellOp) : string =
    let ops (body: CellOp list) =
        JArr(body |> List.map (encodeOp >> JStr))

    match op with
    | LetValue(placeholder, expr, body) ->
        Canon.render (
            Canon.typed "LetValue" [ "body", ops body; "expr", encodeExpr expr; "placeholder", JStr placeholder ]
        )
    | Set(cell, value) -> Canon.render (Canon.typed "Set" [ "cell", JStr cell; "value", JStr value ])
    | Clear cell -> Canon.render (Canon.typed "Clear" [ "cell", JStr cell ])
    | Copy(source, target) -> Canon.render (Canon.typed "Copy" [ "source", JStr source; "target", JStr target ])
    | AppendRow(region, row) -> Canon.render (Canon.typed "AppendRow" [ "region", JStr region; "row", JStr row ])
    | DropRow region -> Canon.render (Canon.typed "DropRow" [ "region", JStr region ])
    | Filled cell -> Canon.render (Canon.typed "Filled" [ "cell", JStr cell ])
    | WhenFilled(cell, body) -> Canon.render (Canon.typed "WhenFilled" [ "body", ops body; "cell", JStr cell ])
    | ForEach(collection, placeholder, body) ->
        Canon.render (
            Canon.typed
                "ForEach"
                [ "body", ops body
                  "collection", JArr collection
                  "placeholder", JStr placeholder ]
        )
    | ForRange(first, last, placeholder, body) ->
        Canon.render (
            Canon.typed
                "ForRange"
                [ "body", ops body
                  "first", JInt first
                  "last", JInt last
                  "placeholder", JStr placeholder ]
        )
    | ForRegion(region, ceiling, placeholder, body) ->
        Canon.render (
            Canon.typed
                "ForRegion"
                [ "body", ops body
                  "ceiling", JInt ceiling
                  "placeholder", JStr placeholder
                  "region", JStr region ]
        )

/// `encodeOp`'s inverse. Total: anything `encodeOp` did not produce is a
/// refusal naming what it is not.
let rec decodeOp (text: string) : Result<CellOp, string> =
    let field (name: string) (members: (string * JVal) list) =
        members |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

    let str name members =
        match field name members with
        | Some(JStr s) -> Ok s
        | _ -> Error(sprintf "member '%s' is not a string" name)

    let integer name members =
        match field name members with
        | Some(JInt n) -> Ok(int n)
        | _ -> Error(sprintf "member '%s' is not an integer" name)

    let ops (name: string) members =
        match field name members with
        | Some(JArr items) ->
            items
            |> List.fold
                (fun acc item ->
                    match acc, item with
                    | Ok decoded, JStr t -> decodeOp t |> Result.map (fun op -> decoded @ [ op ])
                    | Ok _, _ -> Error(sprintf "member '%s' holds a non-string op" name)
                    | Error e, _ -> Error e)
                (Ok [])
        | _ -> Error(sprintf "member '%s' is not an array" name)

    let two first second make members =
        str first members
        |> Result.bind (fun a -> str second members |> Result.map (fun b -> make (a, b)))

    match Json.parse text with
    | Ok(JObj members) ->
        match field "$type" members with
        | Some(JStr "Set") -> two "cell" "value" Set members
        | Some(JStr "Clear") -> str "cell" members |> Result.map Clear
        | Some(JStr "Copy") -> two "source" "target" Copy members
        | Some(JStr "AppendRow") -> two "region" "row" AppendRow members
        | Some(JStr "DropRow") -> str "region" members |> Result.map DropRow
        | Some(JStr "Filled") -> str "cell" members |> Result.map Filled
        | Some(JStr "WhenFilled") ->
            str "cell" members
            |> Result.bind (fun cell -> ops "body" members |> Result.map (fun body -> WhenFilled(cell, body)))
        | Some(JStr "ForEach") ->
            match field "collection" members with
            | Some(JArr collection) ->
                str "placeholder" members
                |> Result.bind (fun placeholder ->
                    ops "body" members
                    |> Result.map (fun body -> ForEach(collection, placeholder, body)))
            | _ -> Error "member 'collection' is not an array"
        | Some(JStr "ForRange") ->
            integer "first" members
            |> Result.bind (fun first ->
                integer "last" members
                |> Result.bind (fun last ->
                    str "placeholder" members
                    |> Result.bind (fun placeholder ->
                        ops "body" members
                        |> Result.map (fun body -> ForRange(first, last, placeholder, body)))))
        | Some(JStr "ForRegion") ->
            str "region" members
            |> Result.bind (fun region ->
                integer "ceiling" members
                |> Result.bind (fun ceiling ->
                    str "placeholder" members
                    |> Result.bind (fun placeholder ->
                        ops "body" members
                        |> Result.map (fun body -> ForRegion(region, ceiling, placeholder, body)))))
        | Some(JStr "LetValue") ->
            match field "expr" members with
            | Some expr ->
                decodeExpr expr
                |> Result.bind (fun expr ->
                    str "placeholder" members
                    |> Result.bind (fun placeholder ->
                        ops "body" members |> Result.map (fun body -> LetValue(placeholder, expr, body))))
            | None -> Error "member 'expr' is absent"
        | Some(JStr other) -> Error(sprintf "'%s' is not a grid op" other)
        | _ -> Error "the op carries no '$type'"
    | _ -> Error "the op is not a JSON object"

// ─── the state witness ──────────────────────────────────────────────────────

let private writeCell (cell: string) (value: string option) (grid: Grid) : Grid =
    match value with
    | Some v ->
        { grid with
            Cells = Map.add cell v grid.Cells }
    | None ->
        { grid with
            Cells = Map.remove cell grid.Cells }

/// Apply one op to the PLAN. Every write is total — a spreadsheet's set,
/// clear and copy never refuse — so the only apply refusals are a drop from a
/// region that holds no row and the guard's.
let apply (op: CellOp) (grid: Grid) : Result<Grid, string> =
    match op with
    | Set(cell, value) -> Ok(writeCell cell (Some value) grid)
    | Clear cell -> Ok(writeCell cell None grid)
    | Copy(source, target) -> Ok(writeCell target (Map.tryFind source grid.Cells) grid)
    | AppendRow(region, row) ->
        let rows = Map.tryFind region grid.Regions |> Option.defaultValue []

        Ok
            { grid with
                Regions = Map.add region (rows @ [ row ]) grid.Regions }
    | DropRow region ->
        match Map.tryFind region grid.Regions with
        | Some rows when not (List.isEmpty rows) ->
            let kept = List.take (List.length rows - 1) rows

            Ok
                { grid with
                    Regions =
                        if List.isEmpty kept then
                            Map.remove region grid.Regions
                        else
                            Map.add region kept grid.Regions }
        | _ -> Error(sprintf "region %s holds no row" region)
    | Filled cell ->
        if Map.containsKey cell grid.Cells then
            Ok grid
        else
            Error(sprintf "cell %s is empty" cell)
    | WhenFilled _
    | ForEach _
    | ForRange _
    | ForRegion _
    | LetValue _ -> Error "a flow op is planned through its view and never applied"

/// What an op REACHES: the cells it reads or writes under `cell` — BOTH of a
/// copy's, so an allow-list over cells bounds what a copy reads as well as
/// what it writes — and a region under `region`; all local. A guard reaches
/// the cell it reads, and no destination; so does a computed value (Phase
/// 2186): the cells its expression reads, so an allow-list over cells bounds
/// what a computed write READS as surely as what its body writes.
let reach (op: CellOp) : OpReach =
    let local arguments =
        { Arguments = arguments
          Destination = EffectDestination.Local }

    let reads cells =
        { Arguments = cells |> List.map (fun cell -> "cell", cell)
          Destination = EffectDestination.Absent }

    match op with
    | Set(cell, _)
    | Clear cell -> local [ "cell", cell ]
    | Copy(source, target) -> local [ "cell", source; "cell", target ]
    | AppendRow(region, _)
    | DropRow region -> local [ "region", region ]
    | Filled cell -> reads [ cell ]
    | LetValue(_, expr, _) -> reads (exprCells expr)
    // A flow op reaches nothing of its own: the policy reads its arms and its
    // LOWERED body beneath it, so a placeholder cannot hide a cell.
    | WhenFilled _
    | ForEach _
    | ForRange _
    | ForRegion _ -> OpReach.nothing

let canonical (grid: Grid) : string =
    Canon.render (
        JObj
            [ "cells", JObj(grid.Cells |> Map.toList |> List.map (fun (cell, value) -> cell, JStr value))
              "regions",
              JObj(
                  grid.Regions
                  |> Map.toList
                  |> List.map (fun (r, rows) -> r, JArr(rows |> List.map JStr))
              ) ]
    )

/// The ops that turn one grid into the other: a set for every cell the target
/// holds differently, a clear for every cell it no longer holds, and per
/// region the drops back to the common prefix of rows and the appends after
/// it. Meaningful where the verb's is empty, because a grid's state IS its
/// cells — and a test holds `apply (diff a b) a = b`.
let diff (before: Grid) (after: Grid) : CellOp list =
    let cells =
        (after.Cells
         |> Map.toList
         |> List.filter (fun (cell, value) -> Map.tryFind cell before.Cells <> Some value)
         |> List.map Set)
        @ (before.Cells
           |> Map.toList
           |> List.filter (fun (cell, _) -> not (Map.containsKey cell after.Cells))
           |> List.map (fst >> Clear))

    let regions =
        Set.union (before.Regions |> Map.keys |> Set.ofSeq) (after.Regions |> Map.keys |> Set.ofSeq)
        |> Set.toList
        |> List.collect (fun region ->
            let rowsOf (grid: Grid) =
                Map.tryFind region grid.Regions |> Option.defaultValue []

            let was, now = rowsOf before, rowsOf after

            let common = Seq.zip was now |> Seq.takeWhile (fun (a, b) -> a = b) |> Seq.length

            List.replicate (List.length was - common) (DropRow region)
            @ (now |> List.skip common |> List.map (fun row -> AppendRow(region, row))))

    cells @ regions

/// What undoes each op: every cell write the write of the bytes the pre-state
/// held at its target, or a clear where it held none; an append its drop; a
/// drop the append of the row it removed. Every edit is EXACTLY inverted, so a
/// grid handler's undo posture is reversible.
let undo (op: CellOp) : UndoClass<Grid, CellOp> =
    let restoring (cell: string) =
        UndoClass.Inverse(fun pre ->
            match Map.tryFind cell pre.Cells with
            | Some old -> [ Set(cell, old) ]
            | None -> [ Clear cell ])

    match op with
    | Set(cell, _)
    | Copy(_, cell) -> restoring cell
    | Clear cell ->
        UndoClass.Inverse(fun pre ->
            match Map.tryFind cell pre.Cells with
            | Some old -> [ Set(cell, old) ]
            | None -> [])
    | AppendRow(region, _) -> UndoClass.Inverse(fun _ -> [ DropRow region ])
    | DropRow region ->
        UndoClass.Inverse(fun pre ->
            match Map.tryFind region pre.Regions with
            | Some rows when not (List.isEmpty rows) -> [ AppendRow(region, List.last rows) ]
            | _ -> [])
    // Never an edit, so never asked.
    | Filled _
    | WhenFilled _
    | ForEach _
    | ForRange _
    | ForRegion _
    | LetValue _ -> UndoClass.OneWay "a guard or a flow op is not an edit"

/// The region a loop iterates, read as a collection: what an importer resolving
/// `For Each r In Range("orders")` at IMPORT time writes into a literal `Each`
/// (class A), and what `ForRegion` reads at RUN time through the state axis
/// (Phase 1991, class B).
let regionCollection (region: string) (grid: Grid) : JVal list =
    Map.tryFind region grid.Regions |> Option.defaultValue [] |> List.map JStr

/// The grid's EXPRESSION witness over the GRID as the store (Phase 2186,
/// D42): the dispatch axis's arrow, instantiated at the state — a cell's
/// value resolves to its string, an empty cell does not resolve, and a
/// multiple of a value that is not an integer is the grid's typed refusal,
/// which reaches the handler's diagnostic verbatim. `Uses` names the cells
/// read, so the core names the value by what it reads.
let exprWitness: ExprWitness<CellExpr, Grid> =
    let rec resolve (grid: Grid) (expr: CellExpr) : ExprResolution =
        match expr with
        | CellValue cell ->
            match Map.tryFind cell grid.Cells with
            | Some value -> ExprResolution.Resolved(JStr value)
            | None -> ExprResolution.NotResolved
        | Times(inner, factor) ->
            match resolve grid inner with
            | ExprResolution.Resolved(JInt n) -> ExprResolution.Resolved(JInt(n * factor))
            | ExprResolution.Resolved(JStr s) ->
                match System.Int32.TryParse s with
                | true, n -> ExprResolution.Resolved(JInt(n * factor))
                | _ -> ExprResolution.Errored(sprintf "'%s' is not a number" s)
            | ExprResolution.Resolved other -> ExprResolution.Errored(sprintf "%s is not a number" (Canon.render other))
            | other -> other

    { Resolve = resolve
      Uses = fun expr -> exprCells expr |> List.map BindingUse.State }

let view (op: CellOp) : OpView<Grid, CellOp> =
    match op with
    | Filled _ -> OpView.Require
    // The value channel (Phase 2186): the expression is the domain's, the
    // resolution against the plan is the core's, through `ExprWitness.value`.
    | LetValue(placeholder, expr, body) -> OpView.Let(placeholder, ExprWitness.value exprWitness expr, body)
    | WhenFilled(cell, body) -> OpView.Choose(Filled cell, body, [], None)
    | ForEach(collection, placeholder, body) -> OpView.Each(Collection.Literal collection, placeholder, body)
    | ForRange(first, last, placeholder, body) ->
        OpView.Each(Collection.Literal(rangeElements first last), placeholder, body)
    // The region as the grid holds it WHEN THE LOOP IS ENTERED (Phase 1991):
    // the read is the domain's own, through the state axis.
    | ForRegion(region, ceiling, placeholder, body) ->
        OpView.Each(
            Collection.Stored(
                { Name = region
                  Read = regionCollection region },
                ceiling
            ),
            placeholder,
            body
        )
    | Set _
    | Clear _
    | Copy _
    | AppendRow _
    | DropRow _ -> OpView.Edit

/// The grid witness: the state axis, and nothing else.
let witness: ProgramWitness<Grid, CellOp, Unfilled, Unfilled> =
    { State =
        { Stream =
            { Apply = apply
              Encode = encodeOp
              Decode = decodeOp }
          Reach = reach
          AbsoluteTarget =
            fun op ->
                match op with
                | Set(cell, _)
                | Clear cell
                | Copy(_, cell)
                | Filled cell -> Some cell
                | AppendRow(region, _)
                | DropRow region -> Some region
                | WhenFilled _
                | ForEach _
                | ForRange _
                | ForRegion _
                | LetValue _ -> None
          Canonical = canonical
          Diff = diff
          View = view
          Undo = undo
          Substitute = substituteOp
          Placeholders = placeholdersOf }
      Walk = Unfilled
      Dispatch = Unfilled }

// ─── the workbook, and the performer over it ────────────────────────────────

/// The WORKBOOK the grid's performer acts on — what a committed plan is
/// performed against. Mutable on purpose: the tests read it to see what was
/// actually done, and count the performer's invocations to see what a replay
/// served from its journal instead.
type Workbook(seed: Grid) =
    let mutable grid = seed
    let invocations = System.Collections.Generic.List<string>()

    member _.Grid = grid
    member _.Invocations = List.ofSeq invocations

    /// Performs each op against the workbook by applying it, and answers a
    /// receipt naming the op it performed. A guard or a flow op never reaches
    /// it; one that did is refused, so a test would see it.
    member _.Performer: Grid -> CellOp -> Result<JVal, string> =
        fun _ op ->
            invocations.Add(encodeOp op)

            match view op with
            | OpView.Edit ->
                match apply op grid with
                | Ok next ->
                    grid <- next
                    Ok(JObj [ "performed", JStr(encodeOp op) ])
                | Error reason -> Error reason
            | _ -> Error "a guard or a flow op reached the performer"
