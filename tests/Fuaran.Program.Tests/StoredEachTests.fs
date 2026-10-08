/// Phase 1991 — `Each` over a collection the STORE holds (DECISIONS.md D36),
/// on the dispatch axis, through the toy domain: a store-bound `Each` agrees
/// with a literal `Each` over the same extent through every walk; an extent
/// over the ceiling is refused before anything performs; the extent is read
/// ONCE, at entry, so a body that grows its own collection does not extend the
/// running loop; the inverse runs the RECORDED number of iterations whatever
/// the store holds at reversal; the demanded projection names the collection
/// and the ceiling; and under the durable interpreter the extent read is a
/// journaled step, served from the record on replay. The op axis — the grid's
/// regions — is `GridWitnessTests` class B.
module Fuaran.Program.Tests.StoredEachTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain

let private run (action: ToyAction) (store: ToyStore) : BoundedOutcome<ToyStore, ToyEffect> =
    BoundedActions.runInert witness "n1" action store

let private traced (action: ToyAction) (store: ToyStore) =
    BoundedActions.runTraced witness HandlerArm.inert "n1" action store ()

/// A body that reads the element, emits, and reads the key the first write
/// left — so the lowered form threads the store between elements.
let private body: ToyAction =
    Seq
        [ Put("last", None, Some(Hole "x"))
          Beep 2
          Put("echo", None, Some(Read "last")) ]

/// The same body without its leaf — what the reversible fragment admits.
let private writesOnly: ToyAction =
    Seq [ Put("last", None, Some(Hole "x")); Put("echo", None, Some(Read "last")) ]

let private elements: JVal list = [ JStr "a"; JInt 2; JBool true ]

let private withRows (rows: JVal list) : ToyStore =
    Map.ofList [ "rows", JArr rows; "last", JStr "none"; "echo", JStr "none" ]

/// The loop under test: every row the store holds, at most five.
let private stored: ToyAction = ForEachOf(Read "rows", 5, "x", body)

let private root: ToyNode =
    { Id = "n1"
      Label = Const(JStr "x")
      Handlers = []
      Children = [] }

let private registry: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll

/// The reason a refusal carries, off the last diagnostic.
let private lastReason (outcome: BoundedOutcome<ToyStore, ToyEffect>) : string =
    match List.tryLast outcome.Diagnostics with
    | Some(BoundedDiagnostic.Refused(_, _, reason)) -> reason
    | other -> failtestf "expected a refusal, got %A" other

[<Tests>]
let dispatchTests =
    testList
        "Phase 1991 — Each over a store-bound collection, the dispatch axis"
        [ test "a store-bound Each agrees with a literal Each over the same extent, through every walk" {
              let literal = ForEach(elements, "x", body)
              let start = withRows elements

              Expect.equal (run stored start) (run literal start) "the fold: same store, effects, diagnostics, halt"

              let outcome, (), trace = traced stored start
              let outcome', (), trace' = traced literal start
              Expect.equal outcome outcome' "the traced fold agrees with the literal's"

              match trace, trace' with
              | Trace.EachOf(extent, steps), Trace.Each steps' ->
                  Expect.equal extent elements "the trace records the extent the run READ"
                  Expect.equal steps steps' "and the elements' steps, exactly as the literal's"
              | _ -> failtestf "expected EachOf beside Each, got %A and %A" trace trace'

              Expect.equal
                  (BoundedActions.reversible witness stored)
                  (BoundedActions.reversible witness literal)
                  "the fragment: as the literal"

              Expect.isTrue
                  (BoundedActions.reversible witness (ForEachOf(Read "rows", 5, "x", writesOnly)))
                  "writes and nothing else: IN the fragment — the extent is recorded, not re-read, where a parameter bound is not"

              Expect.isFalse
                  (BoundedActions.reversible witness (Times(Bound.Parameter(Read "n", 0, 5), writesOnly)))
                  "the parameter-bound repeat it is priced as is NOT"

              Expect.equal
                  (Budget.actionCascadeCost witness stored)
                  (1 + 5 * Budget.actionCascadeCost witness body)
                  "priced at the CEILING without the store: one step for the read, the body five times"

              Expect.equal
                  (ProgramWire.replayDefectsOfAction witness stored)
                  (ReplayDefect.UndecidableAction :: ProgramWire.replayDefectsOfAction witness body
                   |> List.distinct)
                  "classified as a parameter bound is: resolved at dispatch, beside the body's own defects"
          }

          test "the demanded projection names the collection and its ceiling: iterates THIS collection, at most N" {
              let projection = Demanded.ofAction witness stored

              Expect.equal
                  projection.Iterations
                  [ { Collection = "bindings:rows"
                      Ceiling = 5 } ]
                  "the iteration demand"

              Expect.contains
                  projection.StateNamespaces
                  { Namespace = Demanded.namespaceOf "rows"
                    Written = false
                    Read = true }
                  "and the source's read, as a parameter bound's"

              let document = Demanded.encode projection

              Expect.stringContains
                  document
                  "\"iterations\":[{\"collection\":\"bindings:rows\",\"ceiling\":5}]"
                  "on the wire"

              Expect.equal (Demanded.decode document) (Ok projection) "and back"

              Expect.isEmpty
                  (Demanded.ofAction witness (ForEach(elements, "x", body))).Iterations
                  "a literal collection demands no iteration: its elements are in the tree"
          }

          test "an extent over the ceiling is refused before the first element: nothing runs, writes or emits" {
              let start = withRows [ JStr "1"; JStr "2"; JStr "3"; JStr "4"; JStr "5"; JStr "6" ]
              let outcome, (), trace = traced stored start
              Expect.isTrue outcome.Halted "halted"
              Expect.equal outcome.Store start "the store untouched"
              Expect.isEmpty outcome.Effects "nothing emitted"
              Expect.equal (lastReason outcome) "the collection's extent is over its declared ceiling" "named"
              Expect.equal trace Trace.Nothing "nothing recorded: no element ran"
          }

          test "a negative ceiling, a source that is not a list, and one that does not resolve are each refused, named" {
              let start = withRows elements

              Expect.equal
                  (lastReason (run (ForEachOf(Read "rows", -1, "x", body)) start))
                  "the collection's ceiling is negative"
                  "negative ceiling"

              Expect.equal
                  (lastReason (run (ForEachOf(Read "last", 5, "x", body)) start))
                  "the collection did not resolve to a list"
                  "not a list"

              Expect.equal
                  (lastReason (run (ForEachOf(Read "absent", 5, "x", body)) start))
                  "the collection did not resolve to a value"
                  "unresolved"

              Expect.equal (lastReason (run (ForEachOf(Fail "boom", 5, "x", body)) start)) "boom" "errored, verbatim"

              for action in
                  [ ForEachOf(Read "rows", -1, "x", body)
                    ForEachOf(Read "last", 5, "x", body)
                    ForEachOf(Read "absent", 5, "x", body) ] do
                  let outcome = run action start
                  Expect.isTrue outcome.Halted "each halts"
                  Expect.equal outcome.Store start "with the store untouched"
          }

          test "the extent is read ONCE at entry: a body that grows its own collection does not extend the running loop" {
              let growing =
                  ForEachOf(
                      Read "rows",
                      10,
                      "x",
                      Seq
                          [ Put("last", None, Some(Hole "x"))
                            Put("rows", Some(JArr [ JStr "p"; JStr "q"; JStr "r"; JStr "s" ]), None)
                            Beep 1 ]
                  )

              let outcome, (), trace = traced growing (withRows [ JStr "a"; JStr "b" ])
              Expect.isFalse outcome.Halted "ran"
              Expect.equal (List.length outcome.Effects) 2 "two elements ran: the two the entry read"
              Expect.equal (Map.tryFind "last" outcome.Store) (Some(JStr "b")) "the second element was the last"

              Expect.equal
                  (Map.tryFind "rows" outcome.Store)
                  (Some(JArr [ JStr "p"; JStr "q"; JStr "r"; JStr "s" ]))
                  "the store's collection grew under the loop"

              match trace with
              | Trace.EachOf(extent, steps) ->
                  Expect.equal extent [ JStr "a"; JStr "b" ] "the record is the extent at ENTRY"
                  Expect.equal (List.length steps) 2 "two elements ran"
              | other -> failtestf "expected EachOf, got %A" other

              Expect.equal (FlowDecision.ofTrace trace) [ FlowDecision.Iterated 2 ] "reported as two"
          }

          test "the inverse runs the RECORDED number of iterations, lowered over the record and not the live store" {
              let start = withRows [ JStr "a"; JStr "b" ]
              let stored = ForEachOf(Read "rows", 5, "x", writesOnly)
              Expect.isTrue (BoundedActions.reversible witness stored) "in the fragment"
              let outcome, (), trace = traced stored start
              Expect.isFalse outcome.Halted "forwards ran"
              Expect.equal (Map.tryFind "last" outcome.Store) (Some(JStr "b")) "the last element's write stands"

              // Between the run and its reversal the store's collection grows.
              let grown =
                  Map.add "rows" (JArr [ JStr "a"; JStr "b"; JStr "c"; JStr "d" ]) outcome.Store

              let inverse = BoundedActions.reverse witness stored trace

              match inverse with
              | BoundedActions.Reversed.Sequence(_, members) ->
                  Expect.equal (List.length members) 2 "one inverse per RECORDED element — the two that ran"
              | other -> failtestf "expected a sequence, got %A" other

              let back = BoundedActions.runReversed witness "n1" inverse grown
              Expect.isFalse back.Halted "backwards ran"

              Expect.equal
                  (Map.remove "rows" back.Store)
                  (Map.remove "rows" start)
                  "backwards restored every key the run overwrote; the live collection was not consulted"

              Expect.equal
                  (Map.tryFind "rows" back.Store)
                  (Map.tryFind "rows" grown)
                  "and left the grown collection as it found it"
          }

          test
              "under the durable interpreter the extent read is a journaled step: a replay is SERVED the recorded extent, not the live store" {
              let handler: Handler<ToyAction, ToyOp> =
                  { Name = "mark-rows"
                    Stages =
                      [ Compute(ForEachOf(Read "rows", 5, "x", Seq [ Put("last", None, Some(Hole "x")); Beep 1 ])) ] }

              let services =
                  DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ())) DurableServices.create

              let durable (rows: JVal list) =
                  Durable.runWith
                      witness
                      services
                      "inv"
                      registry
                      OpPerformance.InMemory
                      DataFrame.noResolve
                      "n1"
                      handler
                      { Tree = root
                        Bindings = withRows rows }

              let first = durable [ JStr "a"; JStr "b" ]
              Expect.isTrue first.Outcome.Committed "the first run commits"
              Expect.equal first.Outcome.Flow [ FlowDecision.Iterated 2 ] "two rows"
              Expect.equal first.Invoked [ 0 ] "the extent read took the first ordinal"
              Expect.isEmpty first.Replayed "nothing served: nothing recorded yet"

              let entries = services.Journal.Read "inv"

              Expect.exists
                  entries
                  (fun e ->
                      e.Step = 0
                      && e.Capability = ExtentReader.Capability
                      && e.Subject = Some "bindings:rows"
                      && e.Phase = JournalPhase.Completed(JArr [ JStr "a"; JStr "b" ]))
                  "journaled under ReadExtent, subject the binding key, the extent as its completed value"

              // The collection has grown by the time the invocation is replayed.
              let replay = durable [ JStr "a"; JStr "b"; JStr "c" ]
              Expect.isTrue replay.Outcome.Committed "the replay commits"

              Expect.equal
                  replay.Outcome.Flow
                  [ FlowDecision.Iterated 2 ]
                  "the replay iterates the RECORDED two, not the live three"

              Expect.equal replay.Replayed [ 0 ] "served from the journal"
              Expect.isEmpty replay.Invoked "and read nothing live"
              Expect.equal (List.length replay.Outcome.ClientEffects) 2 "two beeps, as recorded"

              Expect.equal
                  (Map.tryFind "last" replay.Outcome.Store.Bindings)
                  (Some(JStr "b"))
                  "the last element is the recorded run's"

              // A fresh invocation of the same handler sees the live three.
              let fresh =
                  Durable.runWith
                      witness
                      services
                      "inv-2"
                      registry
                      OpPerformance.InMemory
                      DataFrame.noResolve
                      "n1"
                      handler
                      { Tree = root
                        Bindings = withRows [ JStr "a"; JStr "b"; JStr "c" ] }

              Expect.equal fresh.Outcome.Flow [ FlowDecision.Iterated 3 ] "a new invocation reads the store as it is"
          }

          test
              "a journal written before this phase replays unchanged: a handler with no store-bound Each never reaches the reader" {
              let handler: Handler<ToyAction, ToyOp> =
                  { Name = "literal"
                    Stages = [ Compute(ForEach(elements, "x", Seq [ Put("last", None, Some(Hole "x")); Beep 1 ])) ] }

              let services =
                  DurableServices.withJournal (Journal.declaringDurable (Journal.inMemory ())) DurableServices.create

              let durable () =
                  Durable.runWith
                      witness
                      services
                      "inv"
                      registry
                      OpPerformance.InMemory
                      DataFrame.noResolve
                      "n1"
                      handler
                      { Tree = root
                        Bindings = withRows elements }

              let first = durable ()
              let second = durable ()
              Expect.isEmpty first.Invoked "no ordinal: the reader was never reached"
              Expect.isEmpty second.Replayed "and none served"
              Expect.equal second.Outcome.Flow first.Outcome.Flow "the same decisions"

              Expect.isFalse
                  (services.Journal.Read "inv"
                   |> List.exists (fun e -> e.Capability = ExtentReader.Capability))
                  "no ReadExtent step in the journal"
          } ]
