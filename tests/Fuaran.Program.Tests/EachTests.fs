/// Phase 1990 — `Each` over a LITERAL collection, on both axes: per-element
/// iteration as vocabulary that lowers to the core by substitution. At the toy
/// witness (the dispatch axis) an `Each` and its hand-written unrolling agree
/// on result, trace, budget, demanded projection and replay classification
/// over generated collections; a reversible run is undone by its inverse; a
/// placeholder read outside any `Each` that binds it is refused at validation,
/// naming the placeholder, before anything runs; and on a tree with no `Each`
/// the lowering is never invoked. At the verb witness (the op axis) an `Each`
/// over paths plans and performs each substituted address, the argument policy
/// and the demanded document see every element's address, the undo posture and
/// the replay classification read the lowered elements, and the scope refusal
/// stops the effect before its first op plans.
module Fuaran.Program.Tests.EachTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.Program.Bounded
open Fuaran.Program.Server

// ─── the dispatch axis, through the toy domain ──────────────────────────────

module private Toy =
    open Fuaran.Program.Tests.ToyDomain

    let run (action: ToyAction) (store: ToyStore) : BoundedOutcome<ToyStore, ToyEffect> =
        BoundedActions.runInert witness "n1" action store

    let traced (action: ToyAction) (store: ToyStore) =
        BoundedActions.runTraced witness HandlerArm.inert "n1" action store ()

    /// Forwards with a trace, then backwards.
    let thereAndBack (action: ToyAction) (store: ToyStore) =
        let outcome, (), trace = traced action store
        let inverse = BoundedActions.reverse witness action trace
        let back = BoundedActions.runReversed witness "n1" inverse outcome.Store
        outcome, trace, inverse, back

    /// The hand-written unrolling of an `Each`: the body with each element
    /// substituted, as a sequence — what the construct lowers to.
    let unrolled (collection: JVal list) (placeholder: string) (body: ToyAction) : ToyAction =
        Seq(collection |> List.map (fun element -> substitute placeholder element body))

    /// Deterministic generated collections of every small size, mixing the
    /// value kinds a placeholder may stand for. A linear congruential walk, so
    /// the corpus is the same on every machine and the failing case is
    /// reproducible by its index.
    let collections: (string * JVal list) list =
        let mutable seed = 2026

        let next () =
            seed <- (seed * 1103515245 + 12345) % 2147483647
            seed

        [ for size in 0..6 do
              for repeat in 1..3 do
                  let elements =
                      [ for _ in 1..size do
                            match next () % 3 with
                            | 0 -> JStr(sprintf "s%d" (next () % 50))
                            | 1 -> JInt(next () % 100)
                            | _ -> JBool(next () % 2 = 0) ]

                  yield sprintf "size %d, draw %d" size repeat, elements ]

    /// A body that reads the element two ways and emits per element: a write
    /// of the element, a leaf, and a write reading the key the first write
    /// left — so the lowered form threads the store between elements.
    let body: ToyAction =
        Seq
            [ Put("last", None, Some(Hole "x"))
              Beep 2
              Put("echo", None, Some(Read "last")) ]

    let root: ToyNode =
        { Id = "n1"
          Label = Const(JStr "x")
          Handlers = []
          Children = [] }

    let registry: ServerEffectRegistry =
        ServerEffectRegistry.permissive ServerEffectRegistry.denyAll

    let runHandler (handler: Handler<ToyAction, ToyOp>) (bindings: ToyStore) =
        Handler.run witness registry DataFrame.noResolve "n1" handler { Tree = root; Bindings = bindings }

    /// The toy witness with a `Substitute` that THROWS: a run over it that
    /// completes is a run that never lowered anything.
    let neverSubstituting: FullWitness<ToyNode, ToyAction, ToyExpr, ToyStore, ToyOp, ToyEffect> =
        { witness with
            Dispatch =
                { witness.Dispatch with
                    Action =
                        { witness.Dispatch.Action with
                            Substitute =
                                fun placeholder _ _ -> failwithf "the lowering was invoked for '%s'" placeholder } } }

    /// Every shape but `Each`, nested: the corpus the lowering must leave alone.
    let eachFree: ToyAction list =
        [ Put("a", Some(JStr "1"), None)
          Seq [ Put("b", None, Some(Read "a")); Beep 1; Hush ]
          Need(Const(JBool true))
          Pick(Read "flag", Put("p", Some(JStr "t"), None), Put("p", Some(JStr "f"), None), Some(Read "flag"))
          Times(Bound.Literal 3, Seq [ Put("n", Some(JInt 1), None); Beep 2 ])
          Times(Bound.Parameter(Read "k", 0, 5), Beep 1)
          Seq
              [ Ring("/nowhere", false, (fun () -> ()))
                Seq [ Beep 1; Need(Const(JBool false)) ]
                Beep 2 ] ]

// ─── the op axis, through the verb domain ───────────────────────────────────

module private Verb =
    open Fuaran.Program.Tests.VerbDomain

    let empty: FileMap = { Files = Map.empty; Published = [] }

    /// The policy: three note paths, two targets, both destination classes.
    /// What an `Each` over paths must stay inside, element by element.
    let registry: ServerEffectRegistry =
        ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
        |> ServerEffectRegistry.constrain
            "ApplyOps"
            [ ServerConstraintClause.AllowList("path", [ "notes/a.md"; "notes/b.md"; "notes/c.md" ])
              ServerConstraintClause.AllowList("target", [ "origin"; "staging" ])
              ServerConstraintClause.AllowList(
                  ServerArgumentPolicy.DestinationArgument,
                  [ "local"; "origin"; "staging" ]
              ) ]

    let runIn (registry: ServerEffectRegistry) (world: World) (handler: Handler<Nothing, FileOp>) =
        Handler.runWith
            witness
            registry
            (OpPerformance.performedWithDetail (world.Performer None))
            DataFrame.noResolve
            "verb"
            handler
            { Tree = { empty with Files = world.Files }
              Bindings = () }

    let run (world: World) (handler: Handler<Nothing, FileOp>) = runIn registry world handler

    let handler (name: string) (ops: FileOp list) : Handler<Nothing, FileOp> =
        { Name = name
          Stages = [ Effect(ServerEffect.ApplyOps ops) ] }

    /// `For Each p In {a, b}: Write notes/p.md` — the loop a spreadsheet's
    /// importer resolves to a fixed array of addresses and one body.
    let writeEach (elements: string list) : FileOp =
        ForEach(elements |> List.map JStr, "p", [ Write("notes/{p}.md", "row {p}") ])

open Fuaran.Program.Tests.ToyDomain

[<Tests>]
let dispatchTests =
    testList
        "Phase 1990 — Each over a literal collection, on the dispatch axis"
        [ test
              "an Each and its hand-written unrolling agree on result, trace, budget, demands and replay, over generated collections" {
              let start = Map.ofList [ "last", JStr "before"; "echo", JStr "before" ]

              for (label, collection) in Toy.collections do
                  let each = ForEach(collection, "x", Toy.body)
                  let unrolled = Toy.unrolled collection "x" Toy.body

                  Expect.equal (Toy.run each start) (Toy.run unrolled start) (sprintf "%s: the outcome" label)

                  let eachOutcome, (), eachTrace = Toy.traced each start
                  let unrolledOutcome, (), unrolledTrace = Toy.traced unrolled start
                  Expect.equal eachOutcome unrolledOutcome (sprintf "%s: the traced outcome" label)

                  match eachTrace, unrolledTrace with
                  | Trace.Each elements, Trace.Seq steps ->
                      Expect.equal elements steps (sprintf "%s: the trace records the lowered form's steps" label)
                      Expect.equal (List.length elements) (List.length collection) (sprintf "%s: one per element" label)
                  | other -> failtestf "%s: expected an Each trace beside a Seq trace, got %A" label other

                  Expect.equal
                      (Budget.actionCascadeCost witness each)
                      (Budget.actionCascadeCost witness unrolled)
                      (sprintf "%s: the budget prices the lowered form" label)

                  Expect.equal
                      (Budget.actionCascadeCost witness each)
                      (3 * List.length collection)
                      (sprintf "%s: the body's three steps once per element, no step for a bound" label)

                  Expect.equal
                      (Demanded.ofAction witness each)
                      (Demanded.ofAction witness unrolled)
                      (sprintf "%s: the demanded projection reads the lowered form" label)

                  Expect.equal
                      (ProgramWire.replayDefectsOfAction witness each)
                      (ProgramWire.replayDefectsOfAction witness unrolled)
                      (sprintf "%s: the replay classification reads the lowered form" label)

                  Expect.equal
                      (BoundedActions.reversible witness each)
                      (BoundedActions.reversible witness unrolled)
                      (sprintf "%s: the fragment is decided on the lowered form" label)
          }

          test
              "a reversible Each is run forwards then backwards to the starting store, its inverse one member per element" {
              let start = Map.ofList [ "k", JStr "before"; "n", JInt 0 ]

              let each =
                  ForEach(
                      [ JStr "one"; JStr "two"; JStr "three" ],
                      "x",
                      Seq [ Put("k", None, Some(Hole "x")); Put("n", Some(JInt 1), None) ]
                  )

              Expect.isTrue (BoundedActions.reversible witness each) "writes and nothing else: in the fragment"
              let outcome, trace, inverse, back = Toy.thereAndBack each start
              Expect.isFalse outcome.Halted "forwards ran"
              Expect.equal (Map.tryFind "k" outcome.Store) (Some(JStr "three")) "the last element's write stands"
              Expect.isTrue (Trace.restorable trace) "every overwritten key was present"
              Expect.isFalse back.Halted "backwards ran"
              Expect.equal back.Store start "backwards restored the starting store"

              match inverse with
              | BoundedActions.Reversed.Sequence(_, members) ->
                  Expect.equal (List.length members) 3 "the inverse of an Each is a SEQUENCE, one inverse per element"
              | other -> failtestf "expected a sequence, got %A" other
          }

          test "an Each over an empty collection runs nothing, costs nothing and is in the fragment" {
              let each = ForEach([], "x", Seq [ Beep 9; Ring("/answer", false, (fun () -> ())) ])
              let outcome = Toy.run each Map.empty
              Expect.equal outcome (Toy.run (Seq []) Map.empty) "the empty sequence"
              Expect.equal (Budget.actionCascadeCost witness each) 0 "no element, no step"
              Expect.isTrue (BoundedActions.reversible witness each) "a leaf never reached is not an effect"
              let _, trace, _, _ = Toy.thereAndBack each Map.empty
              Expect.equal trace (Trace.Each []) "no element ran"
          }

          test "an Each halts at the element whose body halts; the elements after it never run" {
              let each =
                  Seq
                      [ ForEach([ JBool true; JBool false; JBool true ], "ok", Seq [ Need(Hole "ok"); Beep 1 ])
                        Beep 5 ]

              let outcome, (), trace = Toy.traced each Map.empty
              Expect.isTrue outcome.Halted "the second element's guard did not hold"

              Expect.equal
                  (List.length outcome.Effects)
                  1
                  "the first element's beep, then the halt; the third never ran"

              match trace with
              | Trace.Seq [ Trace.Each elements ] -> Expect.equal (List.length elements) 2 "the elements that RAN"
              | other -> failtestf "unexpected trace %A" other
          }

          test "nested Eachs with distinct placeholders fold as the doubly-lowered sequence" {
              let rows = [ JStr "r1"; JStr "r2" ]
              let cols = [ JInt 1; JInt 2; JInt 3 ]

              let cell =
                  Seq
                      [ Put("row", None, Some(Hole "row"))
                        Put("col", None, Some(Hole "col"))
                        Beep 1 ]

              let nested = ForEach(rows, "row", ForEach(cols, "col", cell))

              let lowered =
                  Seq(
                      rows
                      |> List.map (fun r ->
                          Seq(cols |> List.map (fun c -> substitute "col" c (substitute "row" r cell))))
                  )

              Expect.equal (Toy.run nested Map.empty) (Toy.run lowered Map.empty) "the outcome"
              Expect.equal (List.length (Toy.run nested Map.empty).Effects) 6 "rows × cols"
              Expect.equal (Budget.actionCascadeCost witness nested) 18 "3 steps × 6 elements"
              Expect.isEmpty (BoundedActions.scopeDefects witness nested) "well scoped"
          }

          test "a placeholder read outside any Each that binds it is refused at validation, named, with nothing run" {
              let unbound = Seq [ Beep 1; Put("a", None, Some(Hole "j")) ]

              Expect.equal
                  (BoundedActions.scopeDefects witness unbound)
                  [ ScopeDefect.UnboundPlaceholder "j" ]
                  "decided from the tree"

              let outcome = Toy.run unbound Map.empty
              Expect.isTrue outcome.Halted "refused"
              Expect.isEmpty outcome.Effects "the beep BEFORE the read never ran: validation, not run time"
              Expect.isEmpty (Map.toList outcome.Store) "nothing written"

              Expect.equal
                  outcome.Diagnostics
                  [ BoundedDiagnostic.Refused("n1", "Seq", "placeholder 'j' is read outside any Each that binds it") ]
                  "the placeholder is named"

              let traced, (), trace = Toy.traced unbound Map.empty
              Expect.equal traced outcome "the traced run refuses alike"
              Expect.equal trace Trace.Nothing "and records nothing"

              // Inside an Each that binds ANOTHER name is still outside.
              let wrongName = ForEach([ JInt 1 ], "i", Put("a", None, Some(Hole "j")))

              Expect.equal
                  (BoundedActions.scopeDefects witness wrongName)
                  [ ScopeDefect.UnboundPlaceholder "j" ]
                  "another binder does not bind it"

              Expect.isTrue (Toy.run wrongName Map.empty).Halted "refused"

              // Inside the Each that binds it is inside — and the body is
              // walked once, unsubstituted, so a well-scoped program over no
              // elements is well scoped too.
              Expect.isEmpty
                  (BoundedActions.scopeDefects witness (ForEach([], "j", Put("a", None, Some(Hole "j")))))
                  "bound by its Each"
          }

          test "an Each that rebinds a name an enclosing Each binds is refused rather than shadowed" {
              let shadowing =
                  ForEach([ JInt 1 ], "i", ForEach([ JInt 2 ], "i", Put("a", None, Some(Hole "i"))))

              Expect.equal
                  (BoundedActions.scopeDefects witness shadowing)
                  [ ScopeDefect.ShadowedPlaceholder "i" ]
                  "named"

              let outcome = Toy.run shadowing Map.empty
              Expect.isTrue outcome.Halted "refused"
              Expect.isEmpty (Map.toList outcome.Store) "nothing written"

              Expect.equal
                  outcome.Diagnostics
                  [ BoundedDiagnostic.Refused(
                        "n1",
                        "ForEach(i)",
                        "placeholder 'i' is already bound by an enclosing Each"
                    ) ]
                  "the placeholder is named"
          }

          test "on a tree containing no Each the lowering is never invoked" {
              // A witness whose `Substitute` throws: every walk — the fold, the
              // traced fold, the budget, the demanded projection, the replay
              // classification, the fragment, the inverse — completes over the
              // Each-free corpus and answers what the real witness answers.
              let start = Map.ofList [ "a", JStr "0"; "flag", JBool true; "k", JInt 2 ]

              for action in Toy.eachFree do
                  let plain = BoundedActions.runInert witness "n1" action start
                  let never = BoundedActions.runInert Toy.neverSubstituting "n1" action start
                  Expect.equal never plain (sprintf "%s: the fold" (describe action))

                  let _, (), traceA =
                      BoundedActions.runTraced witness HandlerArm.inert "n1" action start ()

                  let _, (), traceB =
                      BoundedActions.runTraced Toy.neverSubstituting HandlerArm.inert "n1" action start ()

                  Expect.equal traceB traceA (sprintf "%s: the trace" (describe action))

                  Expect.equal
                      (Budget.actionCascadeCost Toy.neverSubstituting action)
                      (Budget.actionCascadeCost witness action)
                      (sprintf "%s: the budget" (describe action))

                  Expect.equal
                      (Demanded.ofAction Toy.neverSubstituting action)
                      (Demanded.ofAction witness action)
                      (sprintf "%s: the demands" (describe action))

                  Expect.equal
                      (ProgramWire.replayDefectsOfAction Toy.neverSubstituting action)
                      (ProgramWire.replayDefectsOfAction witness action)
                      (sprintf "%s: the replay classification" (describe action))

                  Expect.equal
                      (BoundedActions.reversible Toy.neverSubstituting action)
                      (BoundedActions.reversible witness action)
                      (sprintf "%s: the fragment" (describe action))

                  Expect.equal
                      (BoundedActions.reverse Toy.neverSubstituting action traceA)
                      (BoundedActions.reverse witness action traceA)
                      (sprintf "%s: the inverse" (describe action))

              // And the moment an Each appears, it IS invoked.
              Expect.throws
                  (fun () ->
                      BoundedActions.runInert Toy.neverSubstituting "n1" (ForEach([ JInt 1 ], "x", Beep 1)) start
                      |> ignore)
                  "an Each lowers through Substitute"
          }

          test "a compute stage's Each reports how many elements ran, in pre-order with the decisions inside it" {
              let handler: Handler<ToyAction, ToyOp> =
                  { Name = "each"
                    Stages =
                      [ Compute(
                            ForEach(
                                [ JBool true; JBool false ],
                                "f",
                                Pick(Hole "f", Put("t", Some(JStr "t"), None), Put("f", Some(JStr "f"), None), None)
                            )
                        ) ] }

              let outcome = Toy.runHandler handler Map.empty
              Expect.isTrue outcome.Committed "commits"

              Expect.equal
                  outcome.Flow
                  [ FlowDecision.Iterated 2; FlowDecision.Chose true; FlowDecision.Chose false ]
                  "the element count, then each element's branch"
          } ]

open Fuaran.Program.Tests.VerbDomain

[<Tests>]
let opTests =
    testList
        "Phase 1990 — Each over a literal collection, on the op axis"
        [ test
              "an Each over paths writes each substituted address, performed once per element in order, and the outcome counts the elements" {
              let world = World()

              let outcome =
                  Verb.run world (Verb.handler "write-each" [ Verb.writeEach [ "a"; "b" ] ])

              Expect.isTrue outcome.Committed "commits"
              Expect.equal outcome.Performed [ "ApplyOps"; "ApplyOps" ] "one staged call per element"

              Expect.equal
                  (world.Files |> Map.toList)
                  [ "notes/a.md", "row a"; "notes/b.md", "row b" ]
                  "the element stood in the path AND the content"

              Expect.equal outcome.Flow [ FlowDecision.Iterated 2 ] "the element count"

              Expect.equal
                  world.Invocations
                  [ encodeOp (Write("notes/a.md", "row a"))
                    encodeOp (Write("notes/b.md", "row b")) ]
                  "the performer was handed the SUBSTITUTED ops, in collection order"
          }

          test
              "the argument policy sees every element's address: one off the allow-list refuses the effect before anything performs" {
              let world = World()

              let outcome =
                  Verb.run world (Verb.handler "write-each" [ Verb.writeEach [ "a"; "zzz" ] ])

              Expect.isFalse outcome.Committed "refused"
              Expect.isEmpty world.Invocations "before anything performs"
              Expect.isEmpty (world.Files |> Map.toList) "the first, admitted element did not run either"

              Expect.equal
                  (outcome.Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "argument-not-allowed:path"))
                  "the argument named, never the value"
          }

          test "the demanded document names every element's path, and the reach beneath the Each is the lowered ops'" {
              let handler = Verb.handler "write-each" [ Verb.writeEach [ "a"; "b"; "c" ] ]
              let projection = ServerDemanded.ofHandler witness handler

              let tier =
                  match projection.Server with
                  | Some tier -> tier
                  | None -> failtest "the walk ran, so the document must carry a server tier"

              Expect.equal
                  (tier.Reach |> List.map (fun r -> r.Capability, r.Argument, r.Name))
                  [ "ApplyOps", "destination", "local"
                    "ApplyOps", "path", "notes/a.md"
                    "ApplyOps", "path", "notes/b.md"
                    "ApplyOps", "path", "notes/c.md" ]
                  "every element's address, distinct and sorted; the Each itself reaches nothing"

              Expect.equal
                  (OpView.beneath witness.State.View witness.State.Substitute (Verb.writeEach [ "a"; "b" ]))
                  [ Write("notes/a.md", "row a"); Write("notes/b.md", "row b") ]
                  "beneath an Each are its lowered elements"
          }

          test "a placeholder read outside any Each is refused on the op axis before the first op plans, named" {
              let world = World()
              world.Seed("notes/a.md", "live")

              let outcome =
                  Verb.run world (Verb.handler "stray" [ Write("notes/a.md", "x"); Write("notes/{q}.md", "x") ])

              Expect.isFalse outcome.Committed "refused"
              Expect.isEmpty world.Invocations "nothing performed"

              Expect.equal
                  (world.Files |> Map.toList)
                  [ "notes/a.md", "live" ]
                  "the first op, well formed, did not plan either"

              Expect.equal
                  outcome.Diagnostics
                  [ ServerDiagnostic.Failed("ApplyOps", "placeholder 'q' is read outside any Each that binds it") ]
                  "the placeholder is named"

              Expect.equal
                  (StateWitness.scopeDefects witness.State [ Write("notes/{q}.md", "x") ])
                  [ ScopeDefect.UnboundPlaceholder "q" ]
                  "decided from the form"

              // A nested Each rebinding its parent's name is refused too.
              let shadowing =
                  Verb.handler
                      "shadow"
                      [ ForEach([ JStr "a" ], "p", [ ForEach([ JStr "b" ], "p", [ Write("notes/{p}.md", "x") ]) ]) ]

              let shadowed = Verb.run (World()) shadowing
              Expect.isFalse shadowed.Committed "refused"

              Expect.equal
                  shadowed.Diagnostics
                  [ ServerDiagnostic.Failed("ApplyOps", "placeholder 'p' is already bound by an enclosing Each") ]
                  "named"
          }

          test "the undo posture and the replay classification read the lowered elements" {
              // A publish per target: to `staging` compensable, to `origin`
              // one-way — the class of the SUBSTITUTED op, which the
              // unsubstituted body cannot show.
              let publishEach (targets: string list) =
                  Verb.handler "publish-each" [ ForEach(targets |> List.map JStr, "t", [ Publish "{t}" ]) ]

              Expect.equal (Undo.posture witness (publishEach [ "staging" ])) UndoVerdict.Compensable "retractable"

              Expect.equal
                  (Undo.posture witness (publishEach [ "staging"; "origin" ]))
                  UndoVerdict.OneWay
                  "one element is public"

              Expect.equal (Undo.posture witness (publishEach [])) UndoVerdict.Reversible "nothing to undo"

              Expect.equal
                  (ProgramWire.replayDefectsOfOp witness (Verb.writeEach [ "a"; "b" ]))
                  []
                  "every element names its target absolutely"

              Expect.equal
                  (ProgramWire.replaySafetyOfOp witness (Verb.writeEach [ "a" ]))
                  ReplaySafety.Safe
                  "and so re-runs"
          }

          test "an Each over nothing plans nothing and performs nothing, and the verb's codec round-trips the shape" {
              let world = World()
              let outcome = Verb.run world (Verb.handler "none" [ Verb.writeEach [] ])
              Expect.isTrue outcome.Committed "commits"
              Expect.isEmpty outcome.Performed "nothing staged"
              Expect.equal outcome.Flow [ FlowDecision.Iterated 0 ] "zero elements, said so"

              let op = Verb.writeEach [ "a"; "b" ]
              Expect.equal (decodeOp (encodeOp op)) (Ok op) "the verb's own codec carries the construct"
          } ]
