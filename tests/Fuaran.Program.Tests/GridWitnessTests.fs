/// Phase 1992 — the grid witness (`GridDomain.fs`) under per-element iteration:
/// the class-A loops a spreadsheet's automation writes — a column of fixed
/// identities, a two-variable grid as a nested `Each`, a copy-values loop,
/// `For i = a To b`, an `If` per element — each RUN, REVERSED through the undo
/// run, and REPLAYED from the durable journal; the witness's own obligations
/// (codec, `Diff`, `Substitute`); and the class-B gap, pinned: a region read at
/// import time is a literal, so a row appended after it is invisible to every
/// later run until Phase 1991's store-bound collection reads it at run time.
module Fuaran.Program.Tests.GridWitnessTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.GridDomain

type private GridHandler = Handler<Nothing, CellOp>

let private empty: Grid =
    { Cells = Map.empty
      Regions = Map.empty }

/// The column `c3` holds a value on rows 2 and 4 and none on row 3; `c4` holds
/// an old value on row 3 — so a copy loop over rows 2..4 overwrites one cell,
/// writes two and clears none, and its inverse must restore all three.
let private seeded: Grid =
    { Cells = Map.ofList [ "r2c3", "10"; "r4c3", "40"; "r3c4", "old" ]
      Regions = Map.ofList [ "orders", [ "o1"; "o2" ] ] }

let private registry: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll

let private handler (name: string) (ops: CellOp list) : GridHandler =
    { Name = name
      Stages = [ Effect(ServerEffect.ApplyOps ops) ] }

let private storeOf (grid: Grid) = { Tree = grid; Bindings = () }

/// The ops of a one-stage handler.
let private opsOf (h: GridHandler) : CellOp list =
    match h.Stages with
    | [ Effect(ServerEffect.ApplyOps ops) ] -> ops
    | _ -> failwith "a one-stage ApplyOps handler"

/// Run a handler against the workbook and keep its plan for the undo run.
let private planned (book: Workbook) (h: GridHandler) =
    Handler.runPlanned
        witness
        registry
        (OpPerformance.performedBy book.Performer)
        DataFrame.noResolve
        "grid"
        h
        (storeOf book.Grid)

/// Run, then undo the committed plan through the same workbook; answer the
/// forward outcome and what the workbook held after each leg.
let private runAndReverse (h: GridHandler) =
    let book = Workbook seeded
    let outcome, plan = planned book h
    let afterRun = book.Grid

    match
        Undo.run
            witness
            registry
            (OpPerformance.performedBy book.Performer)
            DataFrame.noResolve
            "grid"
            plan
            outcome.Store
    with
    | Error refusal -> failtestf "the undo run was refused: %s" (Undo.describe refusal)
    | Ok undone ->
        Expect.isTrue undone.Committed "the undo run commits"
        outcome, afterRun, book.Grid

/// Run a handler twice under ONE durable journal and invocation — the second
/// run is the replay. Answer both durable outcomes and the invocations the
/// performer saw in each.
let private runAndReplay (h: GridHandler) =
    let book = Workbook seeded

    let services =
        DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ())) DurableServices.create

    let durable () =
        Durable.runWith
            witness
            services
            "inv"
            registry
            (OpPerformance.performedBy book.Performer)
            DataFrame.noResolve
            "grid"
            h
            (storeOf seeded)

    let first = durable ()
    let performedFirst = book.Invocations
    let second = durable ()
    first, second, performedFirst, book.Invocations

/// The obligations every class-A fixture meets: it commits; undoing it puts
/// the workbook back to the seed, byte for byte; replaying it serves every
/// step from the journal, invokes no performer, and reaches the same plan; and
/// its posture says so before it runs.
let private classA (h: GridHandler) =
    let outcome, afterRun, afterUndo = runAndReverse h
    Expect.isTrue outcome.Committed "the run commits"
    Expect.equal (canonical afterRun) (canonical outcome.Store.Tree) "the workbook holds what the plan planned"
    Expect.equal (canonical afterUndo) (canonical seeded) "reversed: the inverses fold back to the seed"
    Expect.equal (Undo.posture witness h) UndoVerdict.Reversible "every edit is exactly inverted"

    let first, second, performedFirst, performedBoth = runAndReplay h
    Expect.isTrue first.Outcome.Committed "the durable run commits"
    Expect.isNonEmpty first.Invoked "the first run performed"
    Expect.equal second.Replayed first.Invoked "replayed: every step the first run performed is served"
    Expect.isEmpty second.Invoked "and none performed again"
    Expect.equal performedBoth performedFirst "the performer saw nothing new"

    Expect.equal
        (canonical second.Outcome.Store.Tree)
        (canonical first.Outcome.Store.Tree)
        "the replay planned the same grid"

    outcome

[<Tests>]
let classATests =
    testList
        "Phase 1992 — the grid witness, class A: Each over literal collections"
        [ test "a single column of fixed identities: each identity written, in order, reversed and replayed" {
              let column =
                  handler
                      "zero-column"
                      [ ForEach([ JStr "r2c3"; JStr "r3c3"; JStr "r4c3" ], "id", [ Set("{id}", "0") ]) ]

              let outcome = classA column
              Expect.equal outcome.Flow [ FlowDecision.Iterated 3 ] "three elements"

              Expect.equal
                  ([ "r2c3"; "r3c3"; "r4c3" ]
                   |> List.map (fun c -> Map.tryFind c outcome.Store.Tree.Cells))
                  [ Some "0"; Some "0"; Some "0" ]
                  "every identity written"
          }

          test
              "a two-variable grid as a nested Each: every (row, column) pair written, row-major, reversed and replayed" {
              let grid =
                  handler
                      "fill-grid"
                      [ ForEach(
                            [ JInt 2; JInt 3 ],
                            "i",
                            [ ForEach([ JStr "a"; JStr "b"; JStr "c" ], "j", [ Set("r{i}c{j}", "{i}{j}") ]) ]
                        ) ]

              let outcome = classA grid

              Expect.equal
                  outcome.Flow
                  [ FlowDecision.Iterated 2; FlowDecision.Iterated 3; FlowDecision.Iterated 3 ]
                  "the outer Each, then each inner one, in pre-order"

              Expect.equal
                  (opsOf grid
                   |> List.collect (OpView.beneath witness.State.View witness.State.Substitute)
                   |> List.filter (fun op -> view op = OpView.Edit))
                  [ Set("r2ca", "2a")
                    Set("r2cb", "2b")
                    Set("r2cc", "2c")
                    Set("r3ca", "3a")
                    Set("r3cb", "3b")
                    Set("r3cc", "3c") ]
                  "both placeholders substituted, the outer element fixed across the inner loop"
          }

          test "a copy-values loop: each row reads one identity and writes another, reversed and replayed" {
              let copy =
                  handler "copy-column" [ ForRange(2, 4, "i", [ Copy("r{i}c3", "r{i}c4") ]) ]

              let outcome = classA copy
              let cells = outcome.Store.Tree.Cells

              Expect.equal
                  ([ 2..4 ] |> List.map (fun i -> Map.tryFind (sprintf "r%dc4" i) cells))
                  [ Some "10"; None; Some "40" ]
                  "row 2 and 4 copied; row 3's empty source cleared the old target"

              Expect.equal outcome.Flow [ FlowDecision.Iterated 3 ] "For i = 2 To 4 is three elements"
          }

          test "an If per element: the guard reads the plan as of its position, and the untaken arm writes nothing" {
              let guarded =
                  handler "copy-filled" [ ForRange(2, 4, "i", [ WhenFilled("r{i}c3", [ Copy("r{i}c3", "r{i}c4") ]) ]) ]

              let outcome = classA guarded

              Expect.equal
                  outcome.Flow
                  [ FlowDecision.Iterated 3
                    FlowDecision.Chose true
                    FlowDecision.Chose false
                    FlowDecision.Chose true ]
                  "row 3's source is empty, so its arm is not taken"

              Expect.equal (Map.tryFind "r3c4" outcome.Store.Tree.Cells) (Some "old") "the untaken row is untouched"
          }

          test
              "For i = 2 To 100 is domain sugar for an Each: the policy and the demanded document see all 99 rows' cells" {
              let long =
                  handler "copy-long" [ ForRange(2, 100, "i", [ Copy("r{i}c3", "r{i}c4") ]) ]

              let projection = ServerDemanded.ofHandler witness long

              let cells =
                  match projection.Server with
                  | Some tier ->
                      tier.Reach
                      |> List.filter (fun r -> r.Argument = "cell")
                      |> List.map (fun r -> r.Name)
                  | None -> failtest "the walk ran, so the document must carry a server tier"

              Expect.equal (List.length cells) 198 "both cells of every one of the 99 rows, distinct"
              Expect.contains cells "r100c4" "the last row's target"

              // An allow-list over the first column's rows refuses the loop
              // before anything performs: a lowered body cannot hide a cell.
              let narrow =
                  registry
                  |> ServerEffectRegistry.constrain
                      "ApplyOps"
                      [ ServerConstraintClause.AllowList("cell", [ "r2c3"; "r2c4" ])
                        ServerConstraintClause.AllowList(ServerArgumentPolicy.DestinationArgument, [ "local" ]) ]

              let book = Workbook seeded

              let refused =
                  Handler.runWith
                      witness
                      narrow
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      long
                      (storeOf seeded)

              Expect.isFalse refused.Committed "refused"
              Expect.isEmpty book.Invocations "before anything performs"

              Expect.equal
                  (refused.Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "argument-not-allowed:cell"))
                  "the argument named, never the value"

              Expect.equal
                  (ProgramWire.replaySafetyOfOp witness (List.head (opsOf long)))
                  ReplaySafety.Safe
                  "every lowered element names its target absolutely"
          }

          test "a placeholder read outside its loop is refused before the first op plans, named" {
              let book = Workbook seeded

              let outcome =
                  Handler.runWith
                      witness
                      registry
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      (handler "stray" [ Set("r2c3", "x"); ForRange(2, 3, "i", []); Copy("r{i}c3", "r{i}c4") ])
                      (storeOf seeded)

              Expect.isFalse outcome.Committed "refused"
              Expect.isEmpty book.Invocations "nothing performed"

              Expect.equal
                  outcome.Diagnostics
                  [ ServerDiagnostic.Failed("ApplyOps", "placeholder 'i' is read outside any Each that binds it") ]
                  "named"
          } ]

[<Tests>]
let witnessTests =
    testList
        "Phase 1992 — the grid witness's own obligations"
        [ test "the codec round-trips every op shape" {
              let every =
                  [ Set("r2c3", "10")
                    Clear "r2c3"
                    Copy("r2c3", "r2c4")
                    AppendRow("orders", "o3")
                    DropRow "orders"
                    Filled "r2c3"
                    WhenFilled("r{i}c3", [ Copy("r{i}c3", "r{i}c4") ])
                    ForEach([ JStr "a"; JInt 2 ], "p", [ Set("{p}", "x") ])
                    ForRange(2, 100, "i", [ ForEach([ JStr "a" ], "j", [ Clear "r{i}c{j}" ]) ])
                    ForRegion("orders", 10, "r", [ Set("{r}.seen", "yes") ]) ]

              for op in every do
                  Expect.equal (decodeOp (encodeOp op)) (Ok op) (encodeOp op)
          }

          test "Diff is meaningful: applying the diff of two grids to the first reaches the second" {
              let target =
                  { Cells = Map.ofList [ "r2c3", "11"; "r5c3", "50"; "r3c4", "old" ]
                    Regions = Map.ofList [ "orders", [ "o1"; "o9"; "o10" ]; "returns", [ "x1" ] ] }

              let fold (ops: CellOp list) (grid: Grid) =
                  ops |> List.fold (fun acc op -> acc |> Result.bind (apply op)) (Ok grid)

              for a, b in [ seeded, target; target, seeded; seeded, empty; empty, target; seeded, seeded ] do
                  match fold (diff a b) a with
                  | Ok reached -> Expect.equal (canonical reached) (canonical b) "the diff reaches its target"
                  | Error reason -> failtestf "a diff did not apply: %s" reason

              Expect.isEmpty (diff seeded seeded) "no diff between equal grids"
          }

          test
              "Substitute keeps the shape and replaces the placeholder in every operand, and a nested loop keeps its own" {
              let body = [ Copy("r{i}c{j}", "r{i}c4") ]
              let nested = ForEach([ JStr "a" ], "j", body)

              Expect.equal
                  (substituteOp "i" (JInt 7) nested)
                  (ForEach([ JStr "a" ], "j", [ Copy("r7c{j}", "r7c4") ]))
                  "the outer element filled; the inner placeholder left for the inner loop"

              match view nested, view (substituteOp "i" (JInt 7) nested) with
              | OpView.Each(collection, placeholder, _), OpView.Each(collection', placeholder', body') ->
                  Expect.equal (collection', placeholder') (collection, placeholder) "the same Each, shape for shape"
                  Expect.equal body' [ Copy("r7c{j}", "r7c4") ] "over the substituted body"
              | _ -> failtest "the substituted op views as the original does"

              Expect.equal (placeholdersOf (Copy("r{i}c{j}", "x"))) [ "i"; "j" ] "both operands' placeholders"
          }

          test "a region append and its drop are exact inverses, so a region's growth is reversible" {
              let grow = handler "grow" [ AppendRow("orders", "o3"); AppendRow("returns", "x1") ]
              let _, afterRun, afterUndo = runAndReverse grow

              Expect.equal
                  (Map.tryFind "returns" afterRun.Regions)
                  (Some [ "x1" ])
                  "a new region created by its first row"

              Expect.equal (canonical afterUndo) (canonical seeded) "and gone again, the old one back to two rows"
          } ]

[<Tests>]
let classBTests =
    testList
        "Phase 1992 — the grid witness, class B (Phase 1991): a region iterated at RUN time"
        [ test
              "a region is read when the loop is entered: a row appended after one run is seen by the next, and a replay of the earlier run is served the two it recorded" {
              // `For Each r In Range("orders")`, resolved at RUN time: the
              // region as the grid holds it when the loop is entered, at most
              // ten rows.
              let marking =
                  handler "mark-orders" [ ForRegion("orders", 10, "r", [ Set("{r}.seen", "yes") ]) ]

              let book = Workbook seeded

              let services =
                  DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ())) DurableServices.create

              let durable (invocation: string) (grid: Grid) =
                  Durable.runWith
                      witness
                      services
                      invocation
                      registry
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      marking
                      (storeOf grid)

              let first = durable "inv-1" book.Grid
              Expect.isTrue first.Outcome.Committed "the first run commits"
              Expect.equal first.Outcome.Flow [ FlowDecision.Iterated 2 ] "the region held two rows"
              Expect.equal (Map.tryFind "o1.seen" book.Grid.Cells) (Some "yes") "o1 marked"
              Expect.equal (Map.tryFind "o2.seen" book.Grid.Cells) (Some "yes") "o2 marked"

              Expect.exists
                  (services.Journal.Read "inv-1")
                  (fun e ->
                      e.Step = 0
                      && e.Capability = ExtentReader.Capability
                      && e.Subject = Some "orders"
                      && e.Phase = JournalPhase.Completed(JArr [ JStr "o1"; JStr "o2" ]))
                  "the extent read is the first journaled step, under ReadExtent, subject the region, the rows its value"

              Expect.equal first.Invoked [ 0; 1; 2 ] "the read, then the two performed edits, in one ordinal sequence"

              // A later handler appends a row to the region.
              let appended =
                  Handler.runWith
                      witness
                      registry
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      (handler "new-order" [ AppendRow("orders", "o3") ])
                      (storeOf book.Grid)

              Expect.isTrue appended.Committed "the append commits"
              Expect.equal (Map.tryFind "orders" book.Grid.Regions) (Some [ "o1"; "o2"; "o3" ]) "the region grew"

              // A REPLAY of the earlier run does NOT see it: it is served the
              // extent the record holds, over the grid as it is NOW — three
              // rows, the third unmarked.
              let performedBefore = book.Invocations
              let replay = durable "inv-1" book.Grid
              Expect.isTrue replay.Outcome.Committed "the replay commits"

              Expect.equal
                  replay.Outcome.Flow
                  [ FlowDecision.Iterated 2 ]
                  "the replay iterates the RECORDED two, not the live three"

              Expect.equal replay.Replayed [ 0; 1; 2 ] "every step served from the journal, the extent read first"
              Expect.isEmpty replay.Invoked "nothing read or performed live"
              Expect.equal book.Invocations performedBefore "the performer saw nothing new"

              Expect.equal
                  (replay.Outcome.Store.Tree.Cells
                   |> Map.filter (fun k _ -> k.EndsWith ".seen")
                   |> Map.toList)
                  [ "o1.seen", "yes"; "o2.seen", "yes" ]
                  "the replay planned the two marks the record names and left the appended row unmarked"

              Expect.isFalse
                  (Map.containsKey "o3.seen" book.Grid.Cells)
                  "the workbook's new row is unmarked: the replay performed nothing"

              // The NEXT run sees it.
              let next = durable "inv-2" book.Grid
              Expect.isTrue next.Outcome.Committed "the next run commits"

              Expect.equal
                  next.Outcome.Flow
                  [ FlowDecision.Iterated 3 ]
                  "the next run iterates three rows: the appended row is seen"

              Expect.equal (Map.tryFind "o3.seen" book.Grid.Cells) (Some "yes") "and marked"
          }

          test "an extent over the ceiling is refused before anything performs" {
              let tight =
                  handler "mark-one" [ ForRegion("orders", 1, "r", [ Set("{r}.seen", "yes") ]) ]

              let book = Workbook seeded

              let outcome =
                  Handler.runWith
                      witness
                      registry
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      tight
                      (storeOf book.Grid)

              Expect.isFalse outcome.Committed "refused: two rows, a ceiling of one"
              Expect.isEmpty book.Invocations "the performer was never reached"
              Expect.isEmpty outcome.Flow "no decision: the loop was refused whole, before its first element"
              Expect.equal (canonical book.Grid) (canonical seeded) "the workbook untouched"

              Expect.isTrue
                  (outcome.Diagnostics
                   |> List.exists (fun d -> (string d).Contains "over its declared ceiling"))
                  "named"
          }

          test
              "the demanded document names the region and its ceiling, so the envelope reads: iterates orders, at most ten" {
              let marking =
                  handler "mark-orders" [ ForRegion("orders", 10, "r", [ Set("{r}.seen", "yes") ]) ]

              let projection = ServerDemanded.ofHandler witness marking

              Expect.equal projection.Iterations [ { Collection = "orders"; Ceiling = 10 } ] "the iteration demand"

              Expect.stringContains
                  (Demanded.encode projection)
                  "\"iterations\":[{\"collection\":\"orders\",\"ceiling\":10}]"
                  "on the wire"

              Expect.isEmpty
                  (ServerDemanded.ofHandler
                      witness
                      (handler "frozen" [ ForEach(regionCollection "orders" seeded, "r", [ Set("{r}.seen", "yes") ]) ]))
                      .Iterations
                  "a region frozen at import demands no iteration: its rows are in the tree"
          }

          test
              "reversal runs the recorded number of iterations, from the record; an undo over a state the region grew under is refused as drift, never re-read" {
              let marking =
                  handler "mark-orders" [ ForRegion("orders", 10, "r", [ Set("{r}.seen", "yes") ]) ]

              Expect.equal (Undo.posture witness marking) UndoVerdict.Reversible "every edit is exactly inverted"

              // From the record: two rows marked, two inverses performed.
              let outcome, afterRun, afterUndo = runAndReverse marking
              Expect.isTrue outcome.Committed "the run commits"
              Expect.equal (Map.tryFind "o1.seen" afterRun.Cells) (Some "yes") "o1 marked"
              Expect.equal (Map.tryFind "o2.seen" afterRun.Cells) (Some "yes") "o2 marked"

              Expect.equal
                  (canonical afterUndo)
                  (canonical seeded)
                  "two inverses, one per recorded row, back to the seed"

              // The region grows between the run and its undo. The undo reads
              // the RECORD — every lowered edit with its pre-state — and the
              // record's inverses do not fold the moved state back to the
              // recorded entry, so the undo is refused as drift before it
              // performs anything; it does not re-read the region and undo a
              // third row the run never marked.
              let book = Workbook seeded
              let outcome, plan = planned book marking
              Expect.isTrue outcome.Committed "the run commits"

              let appended =
                  Handler.runWith
                      witness
                      registry
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      (handler "new-order" [ AppendRow("orders", "o3") ])
                      outcome.Store

              Expect.isTrue appended.Committed "the append commits"
              let performedBefore = book.Invocations

              match
                  Undo.run
                      witness
                      registry
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      plan
                      appended.Store
              with
              | Ok _ -> failtest "an undo over a state that moved under the record was performed"
              | Error refusal ->
                  Expect.stringContains (Undo.describe refusal) "undo-inverse-drift" "refused as drift, by name"
                  Expect.equal book.Invocations performedBefore "and nothing performed"
                  Expect.equal (Map.tryFind "o1.seen" book.Grid.Cells) (Some "yes") "the marks stand"

                  Expect.equal
                      (Map.tryFind "orders" book.Grid.Regions)
                      (Some [ "o1"; "o2"; "o3" ])
                      "as does the grown region"
          } ]

/// Phase 2186 — the VALUE CHANNEL on the state axis (DECISIONS.md D42): a
/// computed write, `Cells(i, 4) = Cells(i, 3) * 2`, spelled as a `LetValue`
/// whose expression the core resolves against the plan as of its position
/// and substitutes into a body that reads the placeholder — with no
/// expression inside the write op and nothing evaluated by `apply`.
[<Tests>]
let valueChannelTests =
    /// `For i = 2 To 4: If Cells(i, 3) <> "" Then Cells(i, 4) = Cells(i, 3) * 2`.
    let double =
        handler
            "double-column"
            [ ForRange(
                  2,
                  4,
                  "i",
                  [ WhenFilled("r{i}c3", [ LetValue("v", Times(CellValue "r{i}c3", 2), [ Set("r{i}c4", "{v}") ]) ]) ]
              ) ]

    /// One computed write, unguarded, over a filled source.
    let scale =
        handler "scale" [ LetValue("v", Times(CellValue "r2c3", 2), [ Set("r2c4", "{v}") ]) ]

    let refusedWith (h: GridHandler) (narrow: ServerEffectRegistry) =
        let book = Workbook seeded

        let outcome =
            Handler.runWith
                witness
                narrow
                (OpPerformance.performedBy book.Performer)
                DataFrame.noResolve
                "grid"
                h
                (storeOf seeded)

        Expect.isFalse outcome.Committed "refused"
        Expect.isEmpty book.Invocations "nothing performed"
        outcome.Diagnostics |> List.last

    testList
        "Phase 2186 — the grid witness: the value channel on the state axis"
        [ test
              "a computed write: a column derived from another, run, reversed and replayed, with no expression in the write op" {
              let outcome = classA double
              let cells = outcome.Store.Tree.Cells

              Expect.equal
                  ([ 2..4 ] |> List.map (fun i -> Map.tryFind (sprintf "r%dc4" i) cells))
                  [ Some "20"; Some "old"; Some "80" ]
                  "rows 2 and 4 doubled from column 3; row 3's empty source left its target as the guard found it"

              Expect.equal
                  outcome.Flow
                  [ FlowDecision.Iterated 3
                    FlowDecision.Chose true
                    FlowDecision.Chose false
                    FlowDecision.Chose true ]
                  "the flow is the loop's and the guard's: a binding takes no decision"
          }

          test "an unresolved value refuses the effect before the body plans, named, with nothing performed" {
              Expect.equal
                  (refusedWith
                      (handler "unresolved" [ LetValue("v", CellValue "r3c3", [ Set("r3c4", "{v}") ]) ])
                      registry)
                  (ServerDiagnostic.Failed("ApplyOps", "the value did not resolve: state:r3c3"))
                  "never a default"
          }

          test "an errored value halts with the domain's reason verbatim" {
              Expect.equal
                  (refusedWith
                      (handler "not-a-number" [ LetValue("v", Times(CellValue "r3c4", 2), [ Set("r3c5", "{v}") ]) ])
                      registry)
                  (ServerDiagnostic.Failed("ApplyOps", "'old' is not a number"))
                  "the grid's typed refusal reaches the diagnostic"
          }

          test "every analysis sees the computed write, and none reads it as a literal" {
              // The demanded document names the value by what it reads and
              // the targets its body writes, with the loop's placeholder
              // standing — a member a literal write never has.
              let projection = ServerDemanded.ofHandler witness double

              Expect.equal
                  projection.Values
                  [ { Value = "state:r{i}c3"
                      Targets = [ "r{i}c4" ] } ]
                  "writes THESE from THIS"

              Expect.isEmpty
                  (ServerDemanded.ofHandler witness (handler "literal" [ Set("r2c4", "20") ])).Values
                  "a literal write demands no value"

              // The replay classifier: a value resolved at dispatch against a
              // state that has moved is `non-literal-write`, undecidable, as an
              // Assign with `from` is — where the literal loop was `Safe`.
              Expect.contains
                  (ProgramWire.replayDefectsOfOp witness (List.head (opsOf double)))
                  ReplayDefect.NonLiteralWrite
                  "non-literal-write"

              Expect.equal
                  (ProgramWire.replaySafetyOfOp witness (List.head (opsOf double)))
                  ReplaySafety.Unknown
                  "undecidable, not unsafe"

              // The argument policy reads the value's reads: an allow-list
              // over the target alone refuses the computed write, and one that
              // admits the source as well commits it.
              let over cells =
                  registry
                  |> ServerEffectRegistry.constrain
                      "ApplyOps"
                      [ ServerConstraintClause.AllowList("cell", cells)
                        ServerConstraintClause.AllowList(ServerArgumentPolicy.DestinationArgument, [ "local" ]) ]

              Expect.equal
                  (refusedWith scale (over [ "r2c4" ]))
                  (ServerDiagnostic.Failed("ApplyOps", "argument-not-allowed:cell"))
                  "the source the expression reads is a reach the policy bounds"

              let book = Workbook seeded

              let admitted =
                  Handler.runWith
                      witness
                      (over [ "r2c3"; "r2c4" ])
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      scale
                      (storeOf seeded)

              Expect.isTrue admitted.Committed "source and target admitted"
              Expect.equal (Map.tryFind "r2c4" book.Grid.Cells) (Some "20") "and the computed value written"

              let reach =
                  match (ServerDemanded.ofHandler witness scale).Server with
                  | Some tier -> tier.Reach |> List.map (fun r -> r.Argument, r.Name)
                  | None -> failtest "the walk ran"

              Expect.contains reach ("cell", "r2c3") "the document names the read beside the write"
              Expect.contains reach ("cell", "r2c4") "and the write"
          }

          test "the resolved value is journaled under its name and a replay is served it, not a re-resolution" {
              let book = Workbook seeded

              let services =
                  DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ())) DurableServices.create

              let durable (grid: Grid) =
                  Durable.runWith
                      witness
                      services
                      "inv"
                      registry
                      (OpPerformance.performedBy book.Performer)
                      DataFrame.noResolve
                      "grid"
                      scale
                      (storeOf grid)

              let first = durable seeded
              Expect.isTrue first.Outcome.Committed "the durable run commits"

              Expect.exists
                  (services.Journal.Read "inv")
                  (fun e ->
                      e.Step = 0
                      && e.Capability = ExtentReader.Capability
                      && e.Subject = Some "state:r2c3"
                      && e.Phase = JournalPhase.Completed(JArr [ JInt 20 ]))
                  "the resolution is the first journaled step, under ReadExtent, subject the value's name, the value recorded"

              Expect.equal first.Invoked [ 0; 1 ] "the read, then the one performed write"

              // The source moved under the record: the replay is served the
              // recorded 20, performs nothing, and does not consult the plan.
              let moved =
                  { seeded with
                      Cells = Map.add "r2c3" "11" seeded.Cells }

              let second = durable moved
              Expect.isTrue second.Outcome.Committed "the replay commits"
              Expect.equal second.Replayed [ 0; 1 ] "every step served from the journal"
              Expect.isEmpty second.Invoked "nothing resolved or performed live"
              Expect.equal (Map.tryFind "r2c4" second.Outcome.Store.Tree.Cells) (Some "20") "the recorded value, not 22"
          }

          test "a copy through the channel is Copy without its default, which is why Copy stays" {
              let viaChannel =
                  handler
                      "copy-via-channel"
                      [ ForRange(
                            2,
                            4,
                            "i",
                            [ WhenFilled("r{i}c3", [ LetValue("v", CellValue "r{i}c3", [ Set("r{i}c4", "{v}") ]) ]) ]
                        ) ]

              let outcome = classA viaChannel

              Expect.equal
                  ([ 2..4 ]
                   |> List.map (fun i -> Map.tryFind (sprintf "r%dc4" i) outcome.Store.Tree.Cells))
                  [ Some "10"; Some "old"; Some "40" ]
                  "the filled rows copied; the empty row is NOT cleared: an unresolved value refuses, and a guard is how the loop says so"
          }

          test "a Let binds lexically: its placeholder is refused outside it, and rebinding a loop's name is a shadow" {
              Expect.equal
                  (refusedWith (handler "stray-v" [ LetValue("v", CellValue "r2c3", []); Set("r2c4", "{v}") ]) registry)
                  (ServerDiagnostic.Failed("ApplyOps", "placeholder 'v' is read outside any Each that binds it"))
                  "named, before the first op plans"

              Expect.equal
                  (refusedWith
                      (handler "shadow-i" [ ForRange(2, 3, "i", [ LetValue("i", CellValue "r2c3", []) ]) ])
                      registry)
                  (ServerDiagnostic.Failed("ApplyOps", ScopeDefect.describe (ScopeDefect.ShadowedPlaceholder "i")))
                  "a shadow is refused"
          }

          test
              "the witness's own obligations: the codec round-trips, substitution reaches the expression, the expression witness answers the three outcomes" {
              let op = LetValue("v", Times(CellValue "r{i}c3", 2), [ Set("r{i}c4", "{v}") ])

              Expect.equal (decodeOp (encodeOp op)) (Ok op) "round trip"

              Expect.equal
                  (substituteOp "i" (JInt 7) op)
                  (LetValue("v", Times(CellValue "r7c3", 2), [ Set("r7c4", "{v}") ]))
                  "the loop's element fills the expression's cell and the body alike"

              Expect.equal
                  (placeholdersOf op)
                  [ "i" ]
                  "the expression's cell reads the loop's placeholder; the bound name is the body's"

              Expect.equal (exprWitness.Uses(Times(CellValue "r2c3", 2))) [ BindingUse.State "r2c3" ] "what it reads"

              Expect.equal
                  (exprWitness.Resolve seeded (Times(CellValue "r2c3", 3)))
                  (ExprResolution.Resolved(JInt 30))
                  "resolved"

              Expect.equal (exprWitness.Resolve seeded (CellValue "r3c3")) ExprResolution.NotResolved "an empty cell"

              Expect.equal
                  (exprWitness.Resolve seeded (Times(CellValue "r3c4", 2)))
                  (ExprResolution.Errored "'old' is not a number")
                  "errored"

              Expect.equal
                  (Undo.posture witness double)
                  UndoVerdict.Reversible
                  "the binding is not an edit; every edit beneath it is exactly inverted"
          } ]
