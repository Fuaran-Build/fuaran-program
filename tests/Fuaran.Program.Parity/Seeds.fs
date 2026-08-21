module Fuaran.Program.Parity.Seeds

open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.OpStream.Abstractions
open Fuaran.Program.Parity.Runner

// ============================================================================
//  The seed fixtures.
//
//  Coverage is chosen to pin the SHARED semantics, one concern per fixture:
//  the store mutation, composition, the documented no-ops, the closure-free
//  effects, the re-resolution coverage floor, and — the one people forget — the
//  kinds that deliberately DO NOT re-resolve. A pass-through fixtured as a
//  pass-through is what turns "the floor moved" from a silent behaviour change
//  into a failing test.
// ============================================================================

let private jstr (s: string) = Fuaran.Core.JStr s

let private bound (id: string) (key: string) (dflt: string) : Node<obj> =
    let n = Fuaran.markdown id "placeholder"

    { n with
        Kind = NodeKind.Markdown({ Text = TextSource.Bound(Binding.State(key, Some dflt)) }) }

let private button (id: string) (action: Action<obj>) : Node<obj> =
    Fuaran.button
        id
        { Defaults.button<obj> with
            Label = TextSource.Literal id
            OnClick = action }

let private dash (children: Node<obj> list) : Node<obj> =
    Fuaran.dashboard
        "root"
        { Defaults.dashboard<obj> with
            Children = children }

let private click (id: string) : ScriptedEvent =
    { NodeId = id
      Event = "click"
      Payload = Map.empty }

let private fixture (name: string) (tree: Node<obj>) (events: ScriptedEvent list) : Fixture =
    { Name = name
      TreeJson = CanonicalJson.encodeNode tree
      Events = events
      Expected = [] }

/// The store mutation, and the re-resolution that makes it visible.
let private setStateRebinds =
    fixture
        "setstate-rebinds"
        (dash
            [ button "set" (Action.SetState("msg", Some(jstr "updated"), None))
              bound "readout" "msg" "init" ])
        [ click "set" ]

/// Composition: a `Chain` folds in order, threading the store. The second write
/// must win, which is what pins "in order" rather than merely "both applied".
let private chainFolds =
    fixture
        "chain-folds"
        (dash
            [ button
                  "set"
                  (Action.Chain
                      [ Action.SetState("msg", Some(jstr "first"), None)
                        Action.SetState("msg", Some(jstr "second"), None) ])
              bound "readout" "msg" "init" ])
        [ click "set" ]

/// The documented no-ops. Each is inert on the bounded path BY DESIGN, and both
/// placements must be inert in the same way — a placement that quietly grew a
/// `Notify` implementation would diverge here.
let private documentedNoOps =
    fixture
        "documented-no-ops"
        (dash
            [ button "notify" (Action.Notify("channel", jstr "payload"))
              button "dispatch" (Action.Dispatch(box "msg" |> Unchecked.nonNull))
              button "call" (Action.Call("/api/thing", None, None))
              bound "readout" "msg" "init" ])
        [ click "notify"; click "dispatch"; click "call" ]

/// The closure-free effects: emitted by both placements, shipped by one and
/// performed by the other. The EFFECT is the shared part; the transport is not.
let private closureFreeEffects =
    fixture
        "closure-free-effects"
        (dash
            [ button "go" (Action.Navigate "/next")
              button "copy" (Action.WriteToClipboard "text")
              bound "readout" "msg" "init" ])
        [ click "go"; click "copy" ]

/// An unsafe route is REFUSED, not silently neutered — and refused identically
/// at both placements, since the sanitiser lives in the shared interpreter.
let private refusedNavigate =
    fixture
        "refused-navigate"
        (dash
            [ button "go" (Action.Navigate "javascript:alert(1)")
              bound "readout" "msg" "init" ])
        [ click "go" ]

/// The re-resolution coverage FLOOR: the reactive kinds `resolveTree` covers.
/// One state write, several bound fields, all of which must re-resolve.
let private coverageFloorReactive =
    let metric =
        Fuaran.metric
            "metric"
            { Defaults.metric with
                Label = TextSource.Bound(Binding.State("msg", Some "init")) }

    let callout =
        Fuaran.callout
            "callout"
            { Defaults.callout with
                Body = TextSource.Bound(Binding.State("msg", Some "init")) }

    fixture
        "coverage-floor-reactive"
        (dash
            [ button "set" (Action.SetState("msg", Some(jstr "resolved"), None))
              metric
              callout ])
        [ click "set" ]

/// The documented PASS-THROUGHS. `resolveTree`'s floor deliberately does not
/// cover every kind, and this fixture records which — so a later phase that
/// extends the floor changes an expectation on purpose rather than moving
/// behaviour silently. It is a negative result, and the most reusable kind.
let private coverageFloorPassThrough =
    let upload = Fuaran.fileUpload "upload" Defaults.fileUpload<obj>

    fixture
        "coverage-floor-passthrough"
        (dash
            [ button "set" (Action.SetState("rows", Some(jstr "ignored"), None))
              upload
              bound "readout" "rows" "init" ])
        [ click "set" ]

/// A call action naming a SERVER HANDLER. At the two placements this module's
/// legs drive, the arm is a documented no-op — and that is precisely what this
/// fixture pins, from the server placement's birth: a tree naming a handler must
/// behave identically everywhere a handler cannot run, so the third placement's
/// divergence from the other two is exactly the handler and nothing else.
///
/// The server placement's own suite drives this same triple with a handler
/// registered for the endpoint, which is where the server-effect arms are
/// exercised. Two readings of one fixture: inert here, effectful there.
let private serverHandlerCall =
    fixture
        "server-handler-call"
        (dash
            [ button "refresh" (Action.Call("/handlers/refresh", None, None))
              bound "readout" "rows" "init" ])
        [ click "refresh" ]

let all: Fixture list =
    [ setStateRebinds
      chainFolds
      documentedNoOps
      closureFreeEffects
      refusedNavigate
      coverageFloorReactive
      coverageFloorPassThrough
      serverHandlerCall ]
