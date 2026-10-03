/// The seeds of the `driver-semantics-toy` family (fuaran#2011): each
/// scenario's starting tree and event script, from which the resident emitter
/// (`--emit-toy-scenarios`) writes the corpus files. The TRACES are not here —
/// they are what a conformant loop produces, and the emitter records them only
/// when every placement in scope agrees.
///
/// One seed per piece of vocabulary the family pins (§10.6's coverage table):
/// sequence, `Choose`, `Repeat`, `Each`, `Require` halting, a leaf refusal, a
/// closure-free call, the event-level refusals (trust boundary and budget),
/// fixed-base re-resolution — and, at a placement that answers calls, the op
/// channel, the argument-policy gate, a staged perform and a nested call.
module Fuaran.Program.Tests.ToySeeds

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Tests.ToyDomain
open Fuaran.Program.Tests.ToyScenarios
open Fuaran.Program.Tests.ToyServerPlacement

type ToySeed =
    { Name: string
      Description: string
      Requires: string
      HostHandlers: string option
      Tree: ToyNode
      Events: ToyEvent list }

let private node (id: string) (label: ToyExpr) (handlers: (string * ToyAction) list) (children: ToyNode list) =
    { Id = id
      Label = label
      Handlers = handlers
      Children = children }

let private lit (s: string) = Const(JStr s)

let private button (id: string) (action: ToyAction) = node id (lit id) [ "tap", action ] []

let private readout (id: string) (key: string) = node id (Read key) [] []

let private root (children: ToyNode list) = node "root" (lit "toy") [] children

let private tap (id: string) =
    { NodeId = id
      Event = "tap"
      Payload = [] }

/// A call's answer slot. The wire carries no closure, so the corpus files never
/// hold one; the seed's is never invoked either.
let private never () =
    failwith "a seed's answer slot was invoked"

let private put (key: string) (value: string) = Put(key, Some(JStr value), None)

let private bounded name description tree events =
    { Name = name
      Description = description
      Requires = BoundedLoop
      HostHandlers = None
      Tree = tree
      Events = events }

let private handled name description tree events =
    { Name = name
      Description = description
      Requires = HandlerLoop
      HostHandlers = Some ToyRelabel
      Tree = tree
      Events = events }

/// The tree every handler-loop seed starts from: two relabel targets, a
/// readout of the status the handlers write, and the seed's own buttons.
let private handlerTree (buttons: ToyNode list) =
    root (
        [ node "title" (lit "Title") [] []
          node "footer" (lit "Footer") [] []
          readout "status" "status" ]
        @ buttons
    )

let seeds: ToySeed list =
    [ bounded
          "sequence-folds"
          "A sequence of two writes to one key and a leaf, where the second write wins and the leaf emits after both. Ordered composition pinned as ORDER, at the toy witness."
          (root
              [ button "set" (Seq [ put "msg" "first"; put "msg" "second"; Beep 3 ])
                readout "readout" "msg" ])
          [ tap "set" ]

      bounded
          "choose-takes-an-arm"
          "A two-arm branch on a store key: unresolved, its condition halts before either arm; false, it takes the false arm; true, the true arm. The UI witness views no action as a branch, so this is the family's only Choose."
          (root
              [ button "choose" (Pick(Read "flag", put "arm" "true-arm", put "arm" "false-arm", None))
                button "lower" (Put("flag", Some(JBool false), None))
                button "raise" (Put("flag", Some(JBool true), None))
                readout "readout" "arm" ])
          [ tap "choose"; tap "lower"; tap "choose"; tap "raise"; tap "choose" ]

      bounded
          "repeat-bounded"
          "A bounded repeat three ways: a literal bound runs its body that many times; a parameter bound read from the store runs within its range; a parameter over its declared range halts before the body, and nothing after it in the sequence runs."
          (root
              [ button "thrice" (Times(Bound.Literal 3, Seq [ Beep 1; put "seen" "yes" ]))
                button "param" (Seq [ Put("n", Some(JInt 2), None); Times(Bound.Parameter(Read "n", 0, 5), Beep 2) ])
                button "over" (Seq [ Times(Bound.Parameter(Const(JInt 9), 0, 5), Beep 1); Beep 4 ])
                readout "readout" "seen" ])
          [ tap "thrice"; tap "param"; tap "over" ]

      bounded
          "each-over-literal"
          "Per-element iteration over a literal collection: the placeholder is substituted element by element, so the last write wins with the last element; and an element whose guard fails halts the iteration there, the elements after it never running."
          (root
              [ button
                    "each"
                    (ForEach([ JStr "a"; JStr "b"; JStr "c" ], "x", Seq [ Put("last", None, Some(Hole "x")); Beep 1 ]))
                button
                    "each-halt"
                    (ForEach(
                        [ JBool true; JBool false; JBool true ],
                        "ok",
                        Seq [ Need(Hole "ok"); Put("reached", None, Some(Hole "ok")); Beep 2 ]
                    ))
                readout "last" "last"
                readout "reached" "reached" ])
          [ tap "each"; tap "each-halt" ]

      bounded
          "require-halts"
          "A guard over the store halts the sequence it sits in: the write before it stands, the write and the leaf after it never run. Once the guarded key holds, the same event runs to the end."
          (root
              [ button "guarded" (Seq [ put "before" "ran"; Need(Read "armed"); put "after" "ran"; Beep 2 ])
                button "arm" (Put("armed", Some(JBool true), None))
                readout "before" "before"
                readout "after" "after" ])
          [ tap "guarded"; tap "arm"; tap "guarded" ]

      bounded
          "leaf-refusal-continues"
          "A refused leaf, a write under the host-reserved namespace and a documented decline each do nothing, and the sequence carries on past all three — unlike a guard. A leaf reads the store: once muted, the same leaf declines."
          (root
              [ button "noisy" (Seq [ Beep 11; put "sys.secret" "no"; put "after" "ran"; Hush; Beep 3 ])
                button "mute" (Put("muted", Some(JBool true), None))
                readout "after" "after"
                readout "secret" "sys.secret" ])
          [ tap "noisy"; tap "mute"; tap "noisy" ]

      bounded
          "closure-free-call"
          "A call naming an endpoint, alone and declaring a result target. Where no handler is registered the first reaches nothing; the second is refused within the event and the write after it still runs. The answer slot is a closure, has no wire form, and is never invoked."
          (root
              [ button "call" (Ring(RelabelEndpoint, false, never))
                button "targeted" (Seq [ Ring(RelabelEndpoint, true, never); put "after" "ran" ])
                node "title" (lit "Title") [] []
                readout "status" "status"
                readout "after" "after" ])
          [ tap "call"; tap "targeted" ]

      bounded
          "refused-event"
          "The trust boundary, at the toy transport: an event naming no node of the tree is REFUSED at the event and changes nothing; an event the node carries no handler for is admitted and inert — not a refusal (§10.5); the node's own event then runs."
          (root [ button "button" (put "hit" "yes"); readout "readout" "hit" ])
          [ tap "ghost"
            { NodeId = "button"
              Event = "hover"
              Payload = [] }
            tap "button" ]

      bounded
          "fixed-base-reresolution"
          "Two events writing one key. Re-resolution is against the FIXED tree the program started from, so the second write reaches the binding the first resolved; folding each step's tree into the next would leave it reading the first."
          (root
              [ button "first" (put "msg" "first")
                button "second" (put "msg" "second")
                readout "readout" "msg" ])
          [ tap "first"; tap "second" ]

      bounded
          "budget-refusal"
          "The resource budget refuses an EVENT whose action cascade costs more than the per-interaction ceiling — a repeat prices at its bound times its body — before anything runs; an event within budget then runs."
          (root
              [ button "flood" (Times(Bound.Literal 100, Beep 1))
                button "small" (Times(Bound.Literal 2, Beep 1)) ])
          [ tap "flood"; tap "small" ]

      handled
          "handler-op-channel"
          "A call answered by a registered handler: its compute stage writes the store and emits through the shared fold, its op stage relabels a node through the op channel, and its staged host call performs — so the plan commits and the tree shows the edit."
          (handlerTree [ button "relabel" (Ring(RelabelEndpoint, false, never)) ])
          [ tap "relabel" ]

      handled
          "handler-argument-policy"
          "A handler whose op addresses a target the host's argument policy does not admit. The gate refuses the op stage while planning, the handler halts, and nothing it planned commits — the store write before the op included."
          (handlerTree [ button "off-list" (Ring(OffListEndpoint, false, never)) ])
          [ tap "off-list" ]

      handled
          "handler-staged-perform"
          "A handler whose staged host call is declined when it is performed. The plan was complete, but the handler is the unit of atomicity: the write and the relabel it planned do not commit."
          (handlerTree [ button "declined" (Ring(DeclinedEndpoint, false, never)) ])
          [ tap "declined" ]

      handled
          "handler-nested-call"
          "The handler's call nested in a sequence: it sees the write before it and is seen by the write after it, which reads the status the handler wrote — the answer spliced in place."
          (handlerTree
              [ button
                    "nested"
                    (Seq
                        [ put "before" "ran"
                          Ring(RelabelEndpoint, false, never)
                          Put("after", None, Some(Read "status")) ])
                readout "before" "before"
                readout "after" "after" ])
          [ tap "nested" ] ]

/// A seed as a scenario with no recorded trace — what the emitter drives.
let asScenario (seed: ToySeed) : ToyScenario =
    { Name = seed.Name
      Requires = seed.Requires
      HostHandlers = seed.HostHandlers
      Tree = seed.Tree
      Events = seed.Events
      Expected = [] }
