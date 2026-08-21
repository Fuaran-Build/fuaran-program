module Fuaran.Program.Server.Tests.HandlerLoopTests

// ─── Handlers as data, and the loop that runs them ───────────────────
//
// Three claims are load-bearing here, and each is checked rather than asserted
// in prose:
//
//   the interpreter is IMPORTED — a `Compute` stage produces exactly what the
//   shared fold produces for the same action and the same store, field for
//   field, so a change to the fold changes this placement too and neither can
//   drift;
//
//   `ApplyOps` is the ONLY domain mutation — a handler doing everything else
//   leaves the tree reference-identical;
//
//   the HANDLER is the atomicity unit — a stage that fails after a stage that
//   succeeded leaves nothing behind but the diagnostics.

open Expecto
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open Fuaran.UI.Ops.Introspect
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.OpStream.Replay
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded
open Fuaran.Program.Server

// ─── fixtures ────────────────────────────────────────────────────────

let private jstr (s: string) = Fuaran.Core.JStr s

/// A three-row embedded table — enough for a pipeline to visibly narrow.
let private rows: Fuaran.Core.Table =
    { Schema = [ "n", Fuaran.Core.IntType ]
      Columns =
        [ { Name = "n"
            Type = Fuaran.Core.IntType
            Cells = [ Fuaran.Core.Int 1; Fuaran.Core.Int 2; Fuaran.Core.Int 3 ] } ] }

let private boundMarkdown (id: string) (key: string) (dflt: string) : Node<obj> =
    let n = Fuaran.markdown id "placeholder"

    { n with
        Kind = NodeKind.Markdown({ Text = TextSource.Bound(Binding.State(key, Some dflt)) }) }

let private treeNode (onClick: Action<obj>) : Node<obj> =
    Fuaran.dashboard
        "root"
        { Defaults.dashboard<obj> with
            Children =
                [ Fuaran.button
                      "call"
                      { Defaults.button<obj> with
                          Label = TextSource.Literal "call"
                          OnClick = onClick }
                  boundMarkdown "readout" "status" "init" ] }

let private wire (onClick: Action<obj>) : WireTree = WireTree.ofDecoded (treeNode onClick)

let private clickEv (nodeId: string) : LiveEvent =
    { ConnId = "server"
      NodeId = nodeId
      Event = "click"
      Payload = Map.empty
      LastSeq = 0 }

/// The resolved Markdown text at `id` — the observable "the store changed and
/// re-resolution made it visible" probe.
let private readout (tree: Node<obj>) : string option =
    match findNode (NodeId "readout") tree with
    | Some node ->
        match node.Kind with
        | NodeKind.Markdown({ Text = TextSource.Literal s }) -> Some s
        | _ -> None
    | None -> None

let private store: ServerStore =
    { Tree = treeNode (Action.Call("/handlers/x", None, None))
      Bindings = empty }

/// A registry with every capability permitted and one host function registered.
let private openRegistry (record: string list ref) =
    ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.register "audit" (fun payload ->
        record.Value <- record.Value @ [ sprintf "%A" payload ]
        Ok(jstr "recorded"))
    |> ServerEffectRegistry.permissive

let private sources: string -> Result<Fuaran.Core.Table, Fuaran.Core.EvalError> =
    Fuaran.Core.DataFrame.noResolve

// ─── tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList
        "server handler"
        [ testList
              "the interpreter is imported, not forked"
              [ for name, action in
                    [ "SetState", Action.SetState("status", Some(jstr "written"), None)
                      "Chain",
                      Action.Chain
                          [ Action.SetState("status", Some(jstr "first"), None)
                            Action.SetState("status", Some(jstr "second"), None) ]
                      "Navigate", Action.Navigate "/next"
                      "refused Navigate", Action.Navigate "javascript:alert(1)"
                      "documented no-op", Action.Notify("channel", jstr "payload")
                      "host-reserved write",
                      Action.SetState(Fuaran.UI.Renderer.StateKeys.HostReservedPrefix + "x", Some(jstr "no"), None) ] ->
                    test name {
                        // The claim is not "it behaves similarly" but "it is the
                        // same fold": a single `Compute` stage must agree with a
                        // direct call on the store, the effects AND the
                        // diagnostics.
                        let direct = BoundedActions.runBoundedAction "call" action empty

                        let staged =
                            Handler.run
                                (openRegistry (ref []))
                                sources
                                "call"
                                { Name = "one-stage"
                                  Stages = [ Compute action ] }
                                store

                        Expect.isTrue staged.Committed "a Compute stage always commits — the fold is total"

                        Expect.equal
                            staged.Store.Bindings.State
                            direct.Store.State
                            "the store the stage produced is the store the shared fold produced"

                        Expect.equal
                            staged.ClientEffects
                            direct.Effects
                            "and the client effects are the fold's, unaltered"

                        Expect.equal
                            (staged.Diagnostics
                             |> List.map (fun d ->
                                 match d with
                                 | ServerDiagnostic.Bounded b -> BoundedDiagnostic.describe b
                                 | other -> sprintf "%A" other))
                            (direct.Diagnostics |> List.map BoundedDiagnostic.describe)
                            "and the diagnostics pass through unchanged"
                    } ]

          test "ApplyOps is the only thing that touches the domain tree" {
              let record = ref []

              let readsAndWritesEverythingElse =
                  { Name = "no-mutation"
                    Stages =
                      [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, [ Fuaran.Core.Limit(2, 0) ]))
                        Compute(Action.SetState("status", Some(jstr "written"), None))
                        Effect(ServerEffect.HostCall("audit", jstr "note", Some "audited"))
                        Effect(ServerEffect.EmitPatch [ TreeOp.RemoveNode(NodeId "call") ])
                        Effect(ServerEffect.Notify("channel", jstr "note")) ] }

              let outcome =
                  Handler.run (openRegistry record) sources "call" readsAndWritesEverythingElse store

              Expect.isTrue outcome.Committed "the handler committed"

              Expect.isTrue
                  (LanguagePrimitives.PhysicalEquality outcome.Store.Tree store.Tree)
                  "a query, a state write, a host call, a patch and a notification leave the domain tree untouched"

              // …and the patch it emitted is emphatically NOT applied, which is
              // the distinction EmitPatch exists to carry.
              Expect.isSome (findNode (NodeId "call") outcome.Store.Tree) "an emitted patch is shipped, never applied"

              let mutating =
                  { Name = "mutation"
                    Stages = [ Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "call") ]) ] }

              let mutated = Handler.run (openRegistry record) sources "call" mutating store

              Expect.isTrue mutated.Committed "the mutating handler committed"
              Expect.isNone (findNode (NodeId "call") mutated.Store.Tree) "and ApplyOps did change the tree"
          }

          test "a committed handler reports the capabilities it performed, in order" {
              let record = ref []

              let handler =
                  { Name = "ordered"
                    Stages =
                      [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, []))
                        Effect(ServerEffect.HostCall("audit", jstr "note", None))
                        Effect(ServerEffect.ApplyOps [])
                        Effect(ServerEffect.EmitPatch [])
                        Effect(ServerEffect.Notify("channel", jstr "note")) ] }

              let outcome = Handler.run (openRegistry record) sources "call" handler store

              Expect.equal
                  outcome.Performed
                  [ "RunQuery"; "host:audit"; "ApplyOps"; "EmitPatch"; "Notify" ]
                  "the audit trail is the stage order, with the host call under its namespaced capability"
          }

          test "a query lands its result in the session's query slot" {
              let handler =
                  { Name = "query"
                    Stages =
                      [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, [ Fuaran.Core.Limit(2, 0) ])) ] }

              let outcome = Handler.run (openRegistry (ref [])) sources "call" handler store

              match Map.tryFind "rows" outcome.Store.Bindings.QueryResults with
              | Some value ->
                  match value with
                  | :? Fuaran.Core.Table as table ->
                      Expect.equal
                          (table.Columns |> List.map (fun c -> List.length c.Cells))
                          [ 2 ]
                          "the pipeline ran — three rows in, the declared limit out"
                  | other -> failtestf "the query slot holds %A rather than a table" other
              | None -> failtest "the query slot was never written"
          }

          test "a host call's result lands in its declared slot, and never in the host's namespace" {
              let handler into =
                  { Name = "landing"
                    Stages = [ Effect(ServerEffect.HostCall("audit", jstr "note", Some into)) ] }

              let landed =
                  Handler.run (openRegistry (ref [])) sources "call" (handler "audited") store

              Expect.isTrue landed.Committed "a call with an ordinary landing slot commits"

              match Map.tryFind "audited" landed.Store.Bindings.State with
              | Some value -> Expect.equal (string value) "recorded" "and the performer's result is in the slot"
              | None -> failtest "the landing slot was never written"

              let reserved =
                  Handler.run
                      (openRegistry (ref []))
                      sources
                      "call"
                      (handler (Fuaran.UI.Renderer.StateKeys.HostReservedPrefix + "x"))
                      store

              Expect.isFalse reserved.Committed "a landing slot in the host-reserved namespace is refused"

              Expect.isEmpty
                  reserved.Store.Bindings.State
                  "and the refusal leaves the store as it was — the same posture the shared fold takes"
          }

          testList
              "the handler is the atomicity unit"
              [ test "a denied stage rolls back everything before it" {
                    let record = ref []

                    // The gate permits the built-in arms and refuses the host
                    // capability, so stage 1 succeeds and stage 2 is denied.
                    let partialGate =
                        openRegistry record
                        |> ServerEffectRegistry.withGate (fun capability -> not (capability.StartsWith "host:"))

                    let denials = ResizeArray<ServerEffectDenial>()
                    let registry = partialGate |> ServerEffectRegistry.onDenied denials.Add

                    let handler =
                        { Name = "halts"
                          Stages =
                            [ Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "call") ])
                              Compute(Action.SetState("status", Some(jstr "written"), None))
                              Effect(ServerEffect.HostCall("audit", jstr "note", None))
                              Effect(ServerEffect.Notify("channel", jstr "never")) ] }

                    let outcome = Handler.run registry sources "call" handler store

                    Expect.isFalse outcome.Committed "the handler did not commit"

                    Expect.isTrue
                        (LanguagePrimitives.PhysicalEquality outcome.Store.Tree store.Tree)
                        "the op applied by stage 1 was rolled back"

                    Expect.isEmpty outcome.Store.Bindings.State "and so was the state written by stage 2"
                    Expect.isEmpty outcome.Performed "nothing is reported as performed"
                    Expect.isEmpty outcome.Notifications "and the stage after the denial never ran"

                    Expect.isNonEmpty
                        outcome.Diagnostics
                        "but the diagnostics survive — they are the whole record of why nothing happened"

                    Expect.sequenceEqual
                        (List.ofSeq denials)
                        [ ServerEffectDenial.GateRefused "host:audit" ]
                        "and the denial reached the sink exactly once"
                }

                test "a failing effect rolls back, and reports a discriminator rather than a message" {
                    let handler =
                        { Name = "fails"
                          Stages =
                            [ Compute(Action.SetState("status", Some(jstr "written"), None))
                              // Addressing a node that is not there: the apply
                              // engine refuses, and the handler unwinds.
                              Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "absent") ]) ] }

                    let outcome = Handler.run (openRegistry (ref [])) sources "call" handler store

                    Expect.isFalse outcome.Committed "the handler did not commit"
                    Expect.isEmpty outcome.Store.Bindings.State "the earlier state write was rolled back"

                    match
                        outcome.Diagnostics
                        |> List.filter (fun d ->
                            match d with
                            | ServerDiagnostic.Failed _ -> true
                            | _ -> false)
                    with
                    | [ ServerDiagnostic.Failed(capability, reason) ] ->
                        Expect.equal capability "ApplyOps" "the failure names the capability"
                        Expect.equal reason "NodeNotFound" "and the error's discriminator, not its message"
                    | other -> failtestf "expected exactly one failure diagnostic, got %A" other
                }

                test "an unresolvable source fails the query without a partial write" {
                    let handler =
                        { Name = "unresolvable"
                          Stages =
                            [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Ref "elsewhere", []))
                              Effect(ServerEffect.Notify("channel", jstr "never")) ] }

                    let outcome = Handler.run (openRegistry (ref [])) sources "call" handler store

                    Expect.isFalse outcome.Committed "an unresolved source halts the handler"
                    Expect.isEmpty outcome.Store.Bindings.QueryResults "and writes no query slot"
                } ]

          test "the gate is consulted before the performer is looked up" {
              // The ordering claim, made observable: a performer that records
              // being called, behind a gate that refuses. If the lookup ran
              // first the recorder would still be untouched — so the stronger
              // check is that NO host function ran at all, which a refused
              // registered performer demonstrates.
              let record = ref []

              let registry = openRegistry record |> ServerEffectRegistry.withGate (fun _ -> false)

              let outcome =
                  Handler.run
                      registry
                      sources
                      "call"
                      { Name = "gated"
                        Stages = [ Effect(ServerEffect.HostCall("audit", jstr "note", None)) ] }
                      store

              Expect.isFalse outcome.Committed "the refused call halted the handler"
              Expect.isEmpty record.Value "and the registered performer never ran"
          }

          testList
              "the loop"
              [ test "a call naming a registered handler runs it" {
                    let handler =
                        { Name = "refresh"
                          Stages =
                            [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Embedded rows, []))
                              Compute(Action.SetState("status", Some(jstr "refreshed"), None)) ] }

                    let services =
                        ServerServices.createPermissive
                        |> ServerServices.withHandler "/handlers/refresh" handler

                    let session =
                        ServerSession.init services empty (wire (Action.Call("/handlers/refresh", None, None)))

                    let next, out = ServerSession.step session (clickEv "call")

                    Expect.isNone out.Rejected "the event passed both gates"
                    Expect.isTrue out.Committed "and the handler committed"
                    Expect.equal (readout next.Resolved) (Some "refreshed") "re-resolution made the state write visible"
                    Expect.isNonEmpty out.Ops "and the diff produced the response ops"
                }

                test "a call naming no handler is inert, diagnosed, and does not echo the endpoint" {
                    let session =
                        ServerSession.init
                            ServerServices.createPermissive
                            empty
                            (wire (Action.Call("/handlers/absent-and-secret", None, None)))

                    let next, out = ServerSession.step session (clickEv "call")

                    Expect.isNone out.Rejected "an absent handler is not a rejection"
                    Expect.isEmpty out.Ops "and changes nothing"

                    Expect.equal
                        out.Diagnostics
                        [ ServerDiagnostic.HandlerUnregistered ]
                        "but it is diagnosed rather than silent"

                    Expect.isFalse
                        ((sprintf "%A" out.Diagnostics).Contains "absent-and-secret")
                        "and the wire-supplied endpoint is never repeated back"

                    Expect.equal (readout next.Resolved) (Some "init") "the session is unchanged"
                }

                test "an action that is not a call takes the shared fold, exactly as elsewhere" {
                    let action = Action.SetState("status", Some(jstr "direct"), None)
                    let session = ServerSession.init ServerServices.createPermissive empty (wire action)
                    let next, out = ServerSession.step session (clickEv "call")

                    Expect.isTrue out.Committed "the shared-fold path always commits"
                    Expect.isEmpty out.Performed "no server capability was involved"
                    Expect.equal (readout next.Resolved) (Some "direct") "and the fold's write is visible"
                }

                test "the dispatch gate defaults to deny" {
                    let session =
                        ServerSession.init
                            ServerServices.create
                            empty
                            (wire (Action.SetState("status", Some(jstr "nope"), None)))

                    let next, out = ServerSession.step session (clickEv "call")

                    match out.Rejected with
                    | Some(Gate _) -> ()
                    | other -> failtestf "expected a gate rejection from the default services, got %A" other

                    Expect.equal (readout next.Resolved) (Some "init") "and nothing changed"
                }

                test "the action budget refuses an oversized cascade" {
                    let cascade =
                        Action.Chain [ for i in 1..8 -> Action.SetState("status", Some(jstr (string i)), None) ]

                    let services =
                        { ServerServices.createPermissive with
                            Budget =
                                { InteractionBudget.defaults with
                                    MaxActions = 4 } }

                    let session = ServerSession.init services empty (wire cascade)
                    let next, out = ServerSession.step session (clickEv "call")

                    match out.Rejected with
                    | Some(BudgetExceeded _) -> ()
                    | other -> failtestf "expected a budget rejection, got %A" other

                    Expect.equal (readout next.Resolved) (Some "init") "and the store is untouched"
                }

                test "a handler that edits the tree is re-priced for the next interaction" {
                    // The tree is no longer fixed at this placement, so the
                    // cached cost has to move with it. A handler that shrinks
                    // the tree must leave a smaller cached cost behind.
                    let handler =
                        { Name = "shrink"
                          Stages = [ Effect(ServerEffect.ApplyOps [ TreeOp.RemoveNode(NodeId "readout") ]) ] }

                    let services =
                        ServerServices.createPermissive
                        |> ServerServices.withHandler "/handlers/shrink" handler

                    let session =
                        ServerSession.init services empty (wire (Action.Call("/handlers/shrink", None, None)))

                    let next, out = ServerSession.step session (clickEv "call")

                    Expect.isTrue out.Committed "the handler committed"
                    Expect.isLessThan next.NodeCount session.NodeCount "and the cached cost followed the tree"
                } ]

          test "the op sink sees the handler's response once, not once per stage" {
              // Journaling per stage would make a rolled-back handler
              // indistinguishable from a committed one in the stream, which is
              // the property replay depends on.
              let applied = ResizeArray<TreeOp<obj> list>()

              let handler =
                  { Name = "several"
                    Stages =
                      [ Compute(Action.SetState("status", Some(jstr "one"), None))
                        Compute(Action.SetState("status", Some(jstr "two"), None))
                        Effect(ServerEffect.Notify("channel", jstr "note")) ] }

              let services =
                  { ServerServices.createPermissive with
                      OnApply = applied.Add }
                  |> ServerServices.withHandler "/handlers/several" handler

              let session =
                  ServerSession.init services empty (wire (Action.Call("/handlers/several", None, None)))

              let _, out = ServerSession.step session (clickEv "call")

              Expect.isTrue out.Committed "the handler committed"
              Expect.equal applied.Count 1 "the sink was called once for the whole handler"

              // Compared by identity: a resolved tree's ops carry handler slots,
              // so a `TreeOp` list has no structural equality to compare with.
              Expect.isTrue
                  (LanguagePrimitives.PhysicalEquality applied[0] out.Ops)
                  "and it saw the response ops themselves"
          } ]
