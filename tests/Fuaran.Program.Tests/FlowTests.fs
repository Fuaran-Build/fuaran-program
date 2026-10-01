/// Phase 1976 — the two flow shapes on the DISPATCH axis, at the toy witness:
/// a selection and a bounded iteration run forwards; the reversible fragment
/// is decided from the tree alone; a traced run is undone by its inverse; a
/// violated exit assertion halts forwards with the assertion named; the
/// budget prices the shapes as `proofs/BoundedFold.fst` says; and the replay
/// classification reads both arms and the bound.
module Fuaran.Program.Tests.FlowTests

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Tests.ToyDomain

let private run (action: ToyAction) (store: ToyStore) : BoundedOutcome<ToyStore, ToyEffect> =
    BoundedActions.runInert witness "n1" action store

/// Forwards with a trace, then backwards: the outcome, the trace, the inverse,
/// and the store the inverse leaves.
let private thereAndBack (action: ToyAction) (store: ToyStore) =
    let outcome, (), trace =
        BoundedActions.runTraced witness HandlerArm.inert "n1" action store ()

    let inverse = BoundedActions.reverse witness action trace
    let back = BoundedActions.runReversed witness "n1" inverse outcome.Store
    outcome, trace, inverse, back

let private literal (key: string) (value: JVal) : ToyAction = Put(key, Some value, None)

[<Tests>]
let tests =
    testList
        "Phase 1976 — selection and bounded iteration on the dispatch axis"
        [ test "a two-arm branch takes the arm its entry condition picks, and both arms continue" {
              let branch =
                  Pick(Read "flag", literal "took" (JStr "true"), literal "took" (JStr "false"), None)

              let onTrue = run (Seq [ branch; Beep 1 ]) (Map.ofList [ "flag", JBool true ])
              Expect.equal (Map.tryFind "took" onTrue.Store) (Some(JStr "true")) "the true arm wrote"
              Expect.equal (List.length onTrue.Effects) 1 "the sequence continued past the branch"
              Expect.isFalse onTrue.Halted "the true arm continues"

              let onFalse = run (Seq [ branch; Beep 1 ]) (Map.ofList [ "flag", JBool false ])
              Expect.equal (Map.tryFind "took" onFalse.Store) (Some(JStr "false")) "the false arm wrote"
              Expect.equal (List.length onFalse.Effects) 1 "and the sequence continued past it too"
              Expect.isFalse onFalse.Halted "the false arm continues"
              Expect.notEqual onTrue.Store onFalse.Store "two arms, two outcomes"
          }

          test "a branch is run forwards then backwards to the starting store" {
              let start = Map.ofList [ "flag", JBool true; "x", JStr "before" ]

              let branch =
                  Pick(
                      Read "flag",
                      Seq [ literal "x" (JStr "t1"); literal "x" (JStr "t2") ],
                      literal "x" (JStr "f"),
                      Some(Read "flag")
                  )

              Expect.isTrue
                  (BoundedActions.reversible witness branch)
                  "a branch with an exit assertion is in the fragment"

              let outcome, trace, _, back = thereAndBack branch start
              Expect.isFalse outcome.Halted "forwards ran"
              Expect.equal (Map.tryFind "x" outcome.Store) (Some(JStr "t2")) "forwards wrote, twice"
              Expect.isTrue (Trace.restorable trace) "every overwritten key was present"
              Expect.isFalse back.Halted "backwards ran"
              Expect.equal back.Store start "backwards restored the starting store"
          }

          test "a bounded repeat is run forwards then backwards to the starting store" {
              let start = Map.ofList [ "n", JInt 0; "log", JStr "" ]
              let body = Seq [ literal "n" (JInt 1); literal "log" (JStr "x") ]
              let repeat = Times(Bound.Literal 3, body)
              Expect.isTrue (BoundedActions.reversible witness repeat) "a literal repeat is in the fragment"
              let outcome, trace, inverse, back = thereAndBack repeat start
              Expect.isFalse outcome.Halted "forwards ran"

              match trace with
              | Trace.Repeat iterations -> Expect.equal (List.length iterations) 3 "three iterations were recorded"
              | other -> failtestf "expected a repeat trace, got %A" other

              Expect.equal back.Store start "backwards restored the starting store"

              // The inverse of a repeat is a SEQUENCE of its iterations'
              // inverses — each iteration overwrote different values.
              match inverse with
              | BoundedActions.Reversed.Sequence(_, members) ->
                  Expect.equal (List.length members) 3 "one inverse per iteration"
              | other -> failtestf "expected a sequence, got %A" other
          }

          test "a branch without an exit assertion is classed outside the reversible fragment" {
              let noExit =
                  Pick(Const(JBool true), literal "a" (JStr "1"), literal "a" (JStr "2"), None)

              Expect.isFalse
                  (BoundedActions.reversible witness noExit)
                  "no exit assertion, nothing says which arm to undo"

              let withExit =
                  Pick(Const(JBool true), literal "a" (JStr "1"), literal "a" (JStr "2"), Some(Const(JBool true)))

              Expect.isTrue (BoundedActions.reversible witness withExit) "with one, it is in"

              Expect.isFalse
                  (BoundedActions.reversible witness (Times(Bound.Parameter(Read "k", 0, 5), literal "a" (JStr "1"))))
                  "a parameter bound is read from the store the body may overwrite — outside"

              Expect.isFalse
                  (BoundedActions.reversible witness (Seq [ literal "a" (JStr "1"); Beep 1 ]))
                  "a leaf is an effect — outside"

              Expect.isTrue
                  (BoundedActions.reversible witness (Seq [ literal "a" (JStr "1"); Need(Const(JBool true)) ]))
                  "a guard is its own inverse — in"
          }

          test "a violated exit assertion halts forwards, after the arm, with the assertion named" {
              let violated =
                  Seq
                      [ Pick(Const(JBool true), literal "a" (JStr "t"), Hush, Some(Const(JBool false)))
                        Beep 5 ]

              let outcome = run violated Map.empty
              Expect.isTrue outcome.Halted "halted"
              Expect.equal (Map.tryFind "a" outcome.Store) (Some(JStr "t")) "the arm's write stands as of the halt"
              Expect.isEmpty outcome.Effects "nothing after the branch ran"

              match outcome.Diagnostics with
              | [ BoundedDiagnostic.Refused(_, "Pick", reason) ] ->
                  Expect.equal reason "the exit assertion did not hold after the true arm" "the assertion is named"
              | other -> failtestf "unexpected diagnostics %A" other

              let heldAfterFalse =
                  run (Pick(Const(JBool false), Beep 1, Hush, Some(Const(JBool true)))) Map.empty

              Expect.isTrue heldAfterFalse.Halted "an exit assertion that held after the false arm halts too"

              match heldAfterFalse.Diagnostics with
              | [ BoundedDiagnostic.UnsupportedOnBoundedPath _; BoundedDiagnostic.Refused(_, "Pick", reason) ] ->
                  Expect.equal reason "the exit assertion held after the false arm" "named the other way"
              | other -> failtestf "unexpected diagnostics %A" other
          }

          test "a condition that fails to resolve halts the branch before either arm" {
              let outcome =
                  run (Seq [ Pick(Missing, literal "a" (JStr "t"), literal "a" (JStr "f"), None); Beep 1 ]) Map.empty

              Expect.isTrue outcome.Halted "halted"
              Expect.isNone (Map.tryFind "a" outcome.Store) "neither arm ran"
              Expect.isEmpty outcome.Effects "nothing after it ran"

              match outcome.Diagnostics with
              | [ BoundedDiagnostic.Refused(_, _, reason) ] ->
                  Expect.equal reason "the branch condition did not resolve to a value" "named"
              | other -> failtestf "unexpected diagnostics %A" other

              let errored = run (Pick(Fail "typed refusal", Beep 1, Beep 2, None)) Map.empty

              match errored.Diagnostics with
              | [ BoundedDiagnostic.Refused(_, _, reason) ] ->
                  Expect.equal reason "typed refusal" "an errored condition halts with the domain's text"
              | other -> failtestf "unexpected diagnostics %A" other
          }

          test "an over-bound parameter repeat is refused before its body runs" {
              let outcome =
                  run (Seq [ Times(Bound.Parameter(Const(JInt 9), 0, 5), Beep 1); Beep 2 ]) Map.empty

              Expect.isTrue outcome.Halted "halted"
              Expect.isEmpty outcome.Effects "no iteration ran, and nothing after"

              match outcome.Diagnostics with
              | [ BoundedDiagnostic.Refused(_, "Times", reason) ] ->
                  Expect.equal reason "the repeat's bound is outside its declared range" "named"
              | other -> failtestf "unexpected diagnostics %A" other

              let inRange = run (Times(Bound.Parameter(Const(JInt 2), 0, 5), Beep 1)) Map.empty
              Expect.equal (List.length inRange.Effects) 2 "two iterations in range"
              Expect.isTrue (run (Times(Bound.Literal -1, Beep 1)) Map.empty).Halted "a negative literal is refused"
          }

          test "a repeat that halts inside its body stops there; the forward run records nothing" {
              let outcome =
                  run (Seq [ Times(Bound.Literal 3, Seq [ Beep 1; Need(Const(JBool false)) ]); Beep 2 ]) Map.empty

              Expect.isTrue outcome.Halted "halted inside the first iteration"
              Expect.equal (List.length outcome.Effects) 1 "one beep, then the guard"

              // The forward `run` is the fold with tracing off: what it answers
              // is what the traced run answers (the model's `traced_agrees`),
              // and it reads the store for no overwritten value.
              let traced, (), trace =
                  BoundedActions.runTraced
                      witness
                      HandlerArm.inert
                      "n1"
                      (Seq [ literal "a" (JStr "1"); literal "a" (JStr "2") ])
                      Map.empty
                      ()

              let plain = run (Seq [ literal "a" (JStr "1"); literal "a" (JStr "2") ]) Map.empty
              Expect.equal plain traced "the traced run and the plain run agree"

              Expect.equal
                  trace
                  (Trace.Seq [ Trace.Wrote None; Trace.Wrote(Some(JStr "1")) ])
                  "the trace records each overwritten value: absent, then the first write"
          }

          test "an absent key cannot be restored, and the inverse is refused rather than built wrong" {
              let _, trace, _, _ = thereAndBack (literal "fresh" (JStr "v")) Map.empty
              Expect.isFalse (Trace.restorable trace) "the key was absent before the run"
          }

          test "the budget prices a branch at its condition plus the dearer arm, a repeat at its bound times its body" {
              let cost = Budget.actionCascadeCost witness

              Expect.equal
                  (cost (Pick(Const(JBool true), Seq [ Beep 1; Beep 2; Beep 3 ], Beep 1, None)))
                  4
                  "1 + max(3, 1)"

              Expect.equal (cost (Times(Bound.Literal 4, Seq [ Beep 1; Beep 2 ]))) 9 "1 + 4 × 2"
              Expect.equal (cost (Times(Bound.Parameter(Read "k", 0, 10), Beep 1))) 11 "1 + hi × 1, without the store"
          }

          test "the replay classification reads both arms and the bound" {
              let defects = ProgramWire.replayDefectsOfAction witness

              Expect.equal
                  (defects (Pick(Const(JBool true), literal "a" (JStr "1"), literal "b" (JStr "2"), None)))
                  [ ReplayDefect.UndecidableAction ]
                  "a branch's condition is undecidable; literal arms add nothing"

              Expect.contains
                  (defects (Pick(Const(JBool true), Put("a", None, Some(Read "x")), Hush, None)))
                  ReplayDefect.NonLiteralWrite
                  "an arm's derived write is reported, whichever arm"

              Expect.equal
                  (defects (Times(Bound.Literal 2, literal "a" (JStr "1"))))
                  []
                  "a literal repeat of a literal write re-runs"

              Expect.equal
                  (defects (Times(Bound.Parameter(Read "k", 0, 5), literal "a" (JStr "1"))))
                  [ ReplayDefect.UndecidableAction ]
                  "a parameter bound is undecidable"
          } ]
