/// The third witness, run against the cut it asked for (Phase 1974,
/// `DECISIONS.md` D20): a document pipeline — read, guard, mutate, render,
/// commit — under the server handler, at a composition that fills the state
/// and walk axes and no dispatch axis.
///
/// The two F-GUARD tests are the third instantiation's, INVERTED: there, a
/// guard over the binding store could not see the tree, so a handler that
/// planned a banned edit and then required the pack committed and published
/// the banned term; here the guard is on the op channel and refuses it. The
/// F-PERFORM test is its re-fold, REMOVED: there, the performer folded the
/// ops itself from the entry document to know what to render; here it is
/// handed the planned document, and the sink's document is the one it was
/// handed.
module Fuaran.Program.Tests.DocWitnessTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.DocDomain

let private para (id: string) (text: string) : Block =
    { Id = BlockId id
      Kind = "para"
      Text = text
      Children = [] }

/// The entry document: a section of two paragraphs whose text names two
/// bound fields the document does not yet hold.
let private entry: Document =
    { Root =
        { Id = BlockId "doc"
          Kind = "section"
          Text = "Agreement"
          Children =
            [ para "p1" "Between {{party}} and us."
              para "p2" "Effective {{effectiveDate}}." ] }
      Context = Map.empty }

let private withBannedTerm (doc: Document) : Document =
    { doc with
        Root =
            { doc.Root with
                Children = [ para "p1" "We leverage synergy."; para "p2" "Effective {{effectiveDate}}." ] } }

/// A handler under a witness that fills no dispatch axis: effect stages only.
type private DocHandler = Handler<Nothing, DocStep>

let private binds =
    [ DocStep.Bind("party", "Acme"); DocStep.Bind("effectiveDate", "2026-10-01") ]

let private tail =
    [ DocStep.Render "markdown"; DocStep.Render "html"; DocStep.Commit "pipeline" ]

let private step (ops: DocStep list) : HandlerStage<Nothing, DocStep> = Effect(ServerEffect.ApplyOps ops)

/// The pipeline: read (bind the fields), mutate, guard, render and commit.
let private pipeline (edits: DocStep list) : DocHandler =
    { Name = "pipeline"
      Stages = [ step binds; step edits; step [ DocStep.RequirePack HouseStyle ]; step tail ] }

let private rewrite =
    [ DocStep.SetText(BlockId "p1", "Between {{party}} and the supplier.") ]

/// Every capability admitted; the tail may reach the host's own store and
/// nothing beyond it.
let private registry: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.constrain
        "ApplyOps"
        [ ServerConstraintClause.AllowList(ServerArgumentPolicy.DestinationArgument, [ "local" ]) ]

let private run (sink: Sink) (handler: DocHandler) (doc: Document) : HandlerOutcome<Document, unit, DocStep, Nothing> =
    Handler.runWith
        witness
        registry
        (OpPerformance.performedWithoutReceipt sink.Perform)
        DataFrame.noResolve
        "doc"
        handler
        { Tree = doc; Bindings = () }

let private refusalOf (outcome: HandlerOutcome<Document, unit, DocStep, Nothing>) : DocRejection option =
    match outcome.Diagnostics with
    | [ ServerDiagnostic.Failed("ApplyOps", reason) ] -> DocRejection.parse reason
    | _ -> None

[<Tests>]
let tests =
    testList
        "Phase 1974 — the third witness: a document pipeline whose state is its tree"
        [ test "the pipeline runs: binds, an edit, a guard that holds, and the tail performed after the plan" {
              let sink = Sink None
              let outcome = run sink (pipeline rewrite) entry

              Expect.isTrue outcome.Committed "the handler commits"

              Expect.equal
                  outcome.Performed
                  (List.replicate 6 "ApplyOps")
                  "two binds, one edit and three tail steps performed — the guard is never performed"

              Expect.equal (List.length sink.Invocations) 6 "the performer was asked six times, never for the guard"

              Expect.equal
                  (sink.Rendered |> Map.find "markdown")
                  "[section] Agreement\n[para] Between Acme and the supplier.\n[para] Effective 2026-10-01."
                  "the bound values, which live in the document, rendered"

              Expect.equal
                  sink.Committed
                  (Some("pipeline", (binds @ rewrite) |> List.map encodeStep))
                  "the commit carries the edits, in plan order"
          }

          // ── F-GUARD, inverted ──

          test "F-GUARD — the op-channel guard halts on a dirty ENTRY document, carrying the typed defects across" {
              let sink = Sink None

              let outcome =
                  run
                      sink
                      { Name = "guard-first"
                        Stages = [ step [ DocStep.RequirePack HouseStyle ]; step tail ] }
                      (withBannedTerm entry)

              Expect.isFalse outcome.Committed "halted"

              Expect.equal
                  (refusalOf outcome)
                  (Some(DocRejection.PackFailed [ "p1:synergy"; "p1:leverage" ]))
                  "the typed defects came back across the halt's reason"

              Expect.isEmpty sink.Invocations "nothing performed"
              Expect.isEmpty sink.Rendered "nothing rendered"
          }

          test "F-GUARD — the guard SEES an edit the handler has already planned, and refuses it" {
              // The third instantiation's guard over the binding store passed
              // this handler and published the banned term. On the op channel
              // the guard reads the document as the edit before it left it.
              let banned = [ DocStep.SetText(BlockId "p1", "We leverage synergy.") ]
              let sink = Sink None
              let outcome = run sink (pipeline banned) entry

              Expect.isFalse outcome.Committed "the banned edit is refused"

              Expect.equal
                  (refusalOf outcome)
                  (Some(DocRejection.PackFailed [ "p1:synergy"; "p1:leverage" ]))
                  "refused by the pack, on the PLANNED document"

              Expect.isEmpty sink.Invocations "nothing performed — not even the binds before it"
              Expect.isNone sink.Committed "nothing committed"
              Expect.isEmpty sink.Rendered "and the banned term was published nowhere"
              Expect.equal outcome.Store.Tree entry "the state is the entry state"

              // Its position is the whole of its meaning: the same guard placed
              // BEFORE the edit sees the clean entry document, holds, and the
              // edit goes through.
              let before =
                  { Name = "guard-before-edit"
                    Stages = [ step [ DocStep.RequirePack HouseStyle ]; step banned; step tail ] }

              let sink' = Sink None
              let early = run sink' before entry
              Expect.isTrue early.Committed "a guard before the edit holds"
              Expect.stringContains (sink'.Rendered |> Map.find "markdown") "leverage" "and checked nothing after it"
          }

          // ── F-PERFORM: no re-fold ──

          test "F-PERFORM — the performer is handed the planned document; the sink's document is the one it was handed" {
              let sink = Sink None
              let outcome = run sink (pipeline rewrite) entry

              Expect.isTrue outcome.Committed "committed"

              Expect.equal
                  sink.Document
                  (Some outcome.Store.Tree)
                  "the document the sink holds is the plan's own — it folded nothing to get it"

              for format in [ "markdown"; "html" ] do
                  Expect.equal
                      (sink.Rendered |> Map.find format)
                      (render format outcome.Store.Tree)
                      (sprintf "%s: rendered from the handed document, which is the committed plan" format)
          }

          test "the perform phase stops at the first failure and reports the prefix that ran" {
              let sink = Sink(Some 2)
              let outcome = run sink (pipeline rewrite) entry

              Expect.isFalse outcome.Committed "rolled back"
              Expect.equal outcome.Performed [ "ApplyOps"; "ApplyOps" ] "the two binds that ran"

              Expect.equal
                  (outcome.Diagnostics |> List.last)
                  (ServerDiagnostic.PerformFailed("ApplyOps", "the world refused op 2"))
                  "positioned, under the op's capability"

              Expect.isNone sink.Committed "nothing committed"
          }

          // ── the rest of what the witness says ──

          test "a typed op rejection crosses the op channel as canonical JSON and parses back (W5)" {
              let sink = Sink None

              let outcome =
                  run
                      sink
                      { Name = "nowhere"
                        Stages = [ step [ DocStep.SetText(BlockId "nope", "x") ] ] }
                      entry

              Expect.equal (refusalOf outcome) (Some(DocRejection.UnknownBlock(BlockId "nope"))) "typed-equal"
              Expect.isEmpty sink.Invocations "nothing performed"
          }

          test "the walk axis is filled and read; ids have a faithful string form (K1)" {
              Expect.equal (Budget.treeCost witness System.Int32.MaxValue entry) 3 "three blocks priced"
              Expect.isEmpty (QuerySchema.readersOfTree witness entry) "no block reads a query slot"

              let rec ids (d: Document) =
                  witness.Walk.Nodes.Id d :: (witness.Walk.Nodes.Children d |> List.collect ids)

              Expect.equal (ids entry) [ "doc"; "p1"; "p2" ] "the walk enumerates every block"

              for id in ids entry do
                  Expect.equal (BlockId.value (BlockId.ofString id)) id "the string form round-trips"

              let replaced =
                  witness.Walk.Nodes.ReplaceChildren entry (witness.Walk.Nodes.Children entry)

              Expect.equal replaced entry "ReplaceChildren inverts Children"
          }

          test "Program's demanded document says what the pipeline reaches, with no domain-side walk" {
              let projection = ServerDemanded.ofHandler witness (pipeline rewrite)

              let reach =
                  projection.Server
                  |> Option.map (fun tier -> tier.Reach |> List.map (fun r -> r.Argument, r.Name))
                  |> Option.defaultValue []

              Expect.equal
                  reach
                  [ "destination", "local"
                    "field", "effectiveDate"
                    "field", "party"
                    "format", "html"
                    "format", "markdown"
                    "pack", HouseStyle
                    "stream", "pipeline"
                    "target", "p1" ]
                  "every name the pipeline reaches — the guard's pack among them"

              Expect.isEmpty projection.StateNamespaces "no binding store: the state is the document"
          } ]
