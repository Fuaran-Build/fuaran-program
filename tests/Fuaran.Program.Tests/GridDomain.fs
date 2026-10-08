/// A FOURTH witness of the generic core, written for Phase 1992: a GRID — cells
/// keyed by permanent identities, ops that write a cell, and named regions that
/// grow by a row — instantiating the state axis only, the way the verb
/// (`VerbDomain.fs`) does. It exists to exercise per-element iteration (Phase
/// 1990, D29) against the loops a spreadsheet's automation actually writes
/// before a real spreadsheet domain depends on it: a column of fixed
/// identities, a two-variable grid as a nested `Each`, a copy-values loop that
/// reads one identity and writes another, `For i = a To b` over a range, and an
/// `If` inside the loop body. No UI type anywhere, like the toy and the verb.
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

/// The grid's ops: the three cell writes a spreadsheet's automation performs
/// (set, clear, copy one identity's value onto another), a region's growth and
/// its inverse, a guard on a cell being filled, and three flow ops the state
/// witness VIEWS as the core's: an `If <cell> <> ""` as `Choose`, a
/// `For Each` over a literal collection as `Each`, and a `For i = a To b` as an
/// `Each` over the range's integers — sugar the domain resolves in its view,
/// with no construct of the core's for it.
type CellOp =
    | Set of cell: string * value: string
    | Clear of cell: string
    /// Copy the SOURCE cell's value onto the TARGET cell — the read the
    /// copy-values loop needs, fused into one op because the state axis has no
    /// channel that carries a value from one op to a later one. An empty
    /// source clears the target, as a spreadsheet's copy does.
    | Copy of source: string * target: string
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

/// The grid's `Substitute`: the element written over the placeholder in every
/// operand of the op and of the ops beneath it, the op's shape untouched. A
/// nested `ForEach` or `ForRange` keeps its own collection and placeholder.
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

/// The grid's `Placeholders`: the names an op's OWN operands read. A
/// `WhenFilled`'s condition is an op the core asks on its own (it is the
/// `Choose`'s entry), so the flow op itself reads nothing, as the verb's
/// branch reads nothing.
let placeholdersOf (op: CellOp) : string list =
    match op with
    | Set(cell, value) -> Placeholder.names cell @ Placeholder.names value
    | Clear cell
    | Filled cell -> Placeholder.names cell
    | Copy(source, target) -> Placeholder.names source @ Placeholder.names target
    | AppendRow(region, row) -> Placeholder.names region @ Placeholder.names row
    | DropRow region -> Placeholder.names region
    | WhenFilled _
    | ForEach _
    | ForRange _ -> []

// ─── the codec ──────────────────────────────────────────────────────────────

let rec encodeOp (op: CellOp) : string =
    let ops (body: CellOp list) =
        JArr(body |> List.map (encodeOp >> JStr))

    match op with
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
    | ForRange _ -> Error "a flow op is planned through its view and never applied"

/// What an op REACHES: the cells it reads or writes under `cell` — BOTH of a
/// copy's, so an allow-list over cells bounds what a copy reads as well as
/// what it writes — and a region under `region`; all local. A guard reaches
/// the cell it reads, and no destination.
let reach (op: CellOp) : OpReach =
    let local arguments =
        { Arguments = arguments
          Destination = EffectDestination.Local }

    match op with
    | Set(cell, _)
    | Clear cell -> local [ "cell", cell ]
    | Copy(source, target) -> local [ "cell", source; "cell", target ]
    | AppendRow(region, _)
    | DropRow region -> local [ "region", region ]
    | Filled cell ->
        { Arguments = [ "cell", cell ]
          Destination = EffectDestination.Absent }
    // A flow op reaches nothing of its own: the policy reads its arms and its
    // LOWERED body beneath it, so a placeholder cannot hide a cell.
    | WhenFilled _
    | ForEach _
    | ForRange _ -> OpReach.nothing

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
    | ForRange _ -> UndoClass.OneWay "a guard or a flow op is not an edit"

let view (op: CellOp) : OpView<CellOp> =
    match op with
    | Filled _ -> OpView.Require
    | WhenFilled(cell, body) -> OpView.Choose(Filled cell, body, [], None)
    | ForEach(collection, placeholder, body) -> OpView.Each(collection, placeholder, body)
    | ForRange(first, last, placeholder, body) -> OpView.Each(rangeElements first last, placeholder, body)
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
                | ForRange _ -> None
          Canonical = canonical
          Diff = diff
          View = view
          Undo = undo
          Substitute = substituteOp
          Placeholders = placeholdersOf }
      Walk = Unfilled
      Dispatch = Unfilled }

/// The region a class-B loop iterates, read as a collection — what an importer
/// resolving `For Each r In Range("orders")` at IMPORT time writes into a
/// literal `Each`. Phase 1991's store-bound collection is the construct that
/// reads it at RUN time instead.
let regionCollection (region: string) (grid: Grid) : JVal list =
    Map.tryFind region grid.Regions |> Option.defaultValue [] |> List.map JStr

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
