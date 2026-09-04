module Fuaran.Program.Bounded.Tests.BoundedActionsTests

// ─── The bounded-Action interpreter ────────────────────────────────
//
// `runBoundedAction` drives an emitted, wire-decoded tree's state store with
// NO hand-authored `update` and NO `'Msg`. These tests pin the bounded subset
// (SetState is the only mutation; Navigate / clipboard / file-read are
// closure-free ClientEffects; Notify / AiTool / Dispatch / Call / CommitLocal
// are no-ops) and — load-bearing — the SAFETY PROPERTY: the interpreter never
// invokes a closure carried by an Action, so a server driving a *generated*
// tree has no arbitrary-code-execution surface.

open Expecto
open Fuaran.Core
open Fuaran.UI.Types
open Fuaran.UI.ServerDriven
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded

/// Non-null box (F# 10 nullness: `box` yields `objnull`;
/// `Map<string, obj>` wants non-null — same `boxNN` posture as TreeOpDiff).
let private o (v: 'T) : obj = box v |> Unchecked.nonNull

/// The empty per-connection store (an empty `BindingSources`).
let private store0: BoundedStore = empty

/// Scalar → `JVal` for test payloads. Numbers land in the bounded store as
/// floats (JSON-number semantics — the store holds the lowered wire population).
let private jv (v: obj) : JVal =
    match v with
    | :? int as i -> JInt i
    | :? string as s -> JStr s
    | :? bool as b -> JBool b
    | :? float as f -> JFloat f
    | other -> failwith (sprintf "jv: unsupported test payload %A" other)

[<Tests>]
let tests =
    testList
        "Phase 153 — bounded-action interpreter"
        [ test "SetState writes the State channel; no client effect" {
              let out =
                  BoundedActions.runBoundedAction "n" (Action.SetState("count", Some(jv 3), None)) store0

              Expect.equal
                  out.Store.State
                  (Map.ofList [ "count", o 3.0 ])
                  "State['count'] = 3 (a JSON number lowers to float)"

              Expect.isEmpty out.Effects "SetState emits no client effect"
          }

          test "SetState overwrites an existing key" {
              let s1 =
                  { store0 with
                      State = Map.ofList [ "mode", o "cash" ] }

              let out =
                  BoundedActions.runBoundedAction "n" (Action.SetState("mode", Some(jv "real"), None)) s1

              Expect.equal out.Store.State (Map.ofList [ "mode", o "real" ]) "mode overwritten to 'real'"
          }

          test "Phase 818 — SetState.valueFrom derives the write from the store at dispatch time" {
              // The selected row's `id` field becomes the written value — the
              // derived state write, evaluated against the BoundedStore itself.
              let selectedRow: Fuaran.Core.Row = Map.ofList [ "id", o "ORD-17"; "total", o 42.5 ]

              let s1 =
                  { store0 with
                      Selections = Map.ofList [ NodeId "orders-grid", o selectedRow ] }

              let valueFrom: Binding<JVal> =
                  Binding.Selection("orders-grid", Binding.projectSelectionField<JVal> "id", None, Some "id")

              let out =
                  BoundedActions.runBoundedAction "n" (Action.SetState("chosen-id", None, Some valueFrom)) s1

              Expect.equal
                  (Map.tryFind "chosen-id" out.Store.State)
                  (Some(o "ORD-17"))
                  "the derived value is written under the key"

              Expect.isEmpty out.Effects "no client effect"
          }

          test "Phase 818 — an unresolved SetState.valueFrom performs NO write and is diagnosed" {
              let valueFrom: Binding<JVal> =
                  Binding.Selection("orders-grid", Binding.projectSelectionField<JVal> "id", None, Some "id")

              let out =
                  BoundedActions.runBoundedAction "n" (Action.SetState("chosen-id", None, Some valueFrom)) store0

              Expect.isFalse (Map.containsKey "chosen-id" out.Store.State) "no write"
              Expect.isNonEmpty out.Diagnostics "the missed write is observable, never silent"
          }

          test "Navigate → closure-free ClientEffect; store unchanged" {
              let out = BoundedActions.runBoundedAction "n" (Action.Navigate "/next") store0
              Expect.equal out.Effects [ ClientEffect.Navigate "/next" ] "one Navigate effect"
              Expect.equal out.Store.State store0.State "store unchanged"
          }

          test "WriteToClipboard → closure-free ClientEffect" {
              let out =
                  BoundedActions.runBoundedAction "n" (Action.WriteToClipboard(TextSource.Literal "copied")) store0

              Expect.equal out.Effects [ ClientEffect.WriteToClipboard "copied" ] "one clipboard effect"
          }

          test "WriteToClipboard resolves a bound payload against the store at dispatch time" {
              let s1 =
                  { store0 with
                      State = Map.ofList [ "share.url", o "https://example.test/x" ] }

              let out =
                  BoundedActions.runBoundedAction
                      "n"
                      (Action.WriteToClipboard(TextSource.Bound(Binding.State("share.url", None))))
                      s1

              Expect.equal
                  out.Effects
                  [ ClientEffect.WriteToClipboard "https://example.test/x" ]
                  "the effect carries the RESOLVED text, not the declaration"

              Expect.isEmpty out.Diagnostics "a payload that resolves is not a diagnostic"
          }

          test "WriteToClipboard REFUSES an unresolved payload rather than copying nothing" {
              // The divergence from the tier's total `resolveTextSource`, pinned
              // rather than asserted in a comment: "" is the right answer for a
              // text slot the reader can see, and the wrong one for a clipboard
              // they cannot. A silent empty write is discovered on paste, which
              // is somewhere else and later.
              //
              // The binding is a SELECTION on a grid with no selection — the
              // same shape the `SetState.valueFrom` refusal above uses, and for
              // the same reason. A `Binding.State` would NOT do: the tier
              // resolves an unwritten key to the empty value rather than to
              // `NotResolved`, deliberately, so it is not an unresolved payload
              // at all and this arm must not refuse it.
              let unselected: Binding<string> =
                  Binding.Selection("orders-grid", Binding.projectSelectionField<string> "id", None, Some "id")

              let out =
                  BoundedActions.runBoundedAction "n" (Action.WriteToClipboard(TextSource.Bound unselected)) store0

              Expect.isEmpty out.Effects "nothing reaches the clipboard"
              Expect.equal out.Store.State store0.State "store unchanged"

              match out.Diagnostics with
              | [ BoundedDiagnostic.Refused(nodeId, _, _) ] -> Expect.equal nodeId "n" "the refusal names the node"
              | other -> failtestf "expected one Refused diagnostic, got %A" other
          }

          test "WriteToClipboard does NOT refuse a State key nothing has written yet" {
              // The other side of that boundary, pinned so a later tightening of
              // the refusal fails here rather than silently breaking every
              // document that copies a slot the reader has not filled in.
              let out =
                  BoundedActions.runBoundedAction
                      "n"
                      (Action.WriteToClipboard(TextSource.Bound(Binding.State("never.written", None))))
                      store0

              Expect.equal
                  out.Effects
                  [ ClientEffect.WriteToClipboard "" ]
                  "the empty steady state is copied, not refused"

              Expect.isEmpty out.Diagnostics "not a refusal"
          }

          test "Print → payload-free ClientEffect; store unchanged" {
              let out = BoundedActions.runBoundedAction "n" Action.Print store0
              Expect.equal out.Effects [ ClientEffect.Print ] "one Print effect"
              Expect.equal out.Store.State store0.State "store unchanged"

              // Lowered, not refused and not a no-op: printing is an act of the
              // machine the document is read on, so this loop hands it on rather
              // than answering for its own process.
              Expect.isEmpty out.Diagnostics "lowering is not a diagnosed no-op"
          }

          test "ReadFileBody → node-addressed ClientEffect; onRead never invoked" {
              let mutable invoked = false

              let onRead (_: string) : obj =
                  invoked <- true
                  o "<closure>"

              let action = Action.ReadFileBody("f1", None, FileReadEncoding.Text, Some onRead)

              let out = BoundedActions.runBoundedAction "upload" action store0
              Expect.equal out.Effects [ ClientEffect.ReadFileBody("upload", "Text") ] "node-addressed read effect"
              Expect.isFalse invoked "the onRead closure must NOT be invoked server-side"
          }

          test "Chain threads the store and concatenates effects in order" {
              let action =
                  Action.Chain
                      [ Action.SetState("a", Some(jv 1), None)
                        Action.Navigate "/go"
                        Action.SetState("b", Some(jv 2), None) ]

              let out = BoundedActions.runBoundedAction "n" action store0

              Expect.equal
                  out.Store.State
                  (Map.ofList [ "a", o 1.0; "b", o 2.0 ])
                  "both SetStateS applied (JSON numbers lower to float)"

              Expect.equal out.Effects [ ClientEffect.Navigate "/go" ] "Navigate effect preserved in order"
          }

          test "Notify / AiTool / Dispatch / CommitLocal are no-ops with a readable diagnostic" {
              let s1 =
                  { store0 with
                      State = Map.ofList [ "k", o 1 ] }

              for action in
                  [ Action.Notify("ch", jv "p")
                    Action.AiTool("tool", jv "args")
                    Action.Dispatch(o "msg")
                    Action.CommitLocal "field" ] do
                  let out = BoundedActions.runBoundedAction "n" action s1
                  Expect.equal out.Store.State s1.State "store unchanged"
                  Expect.isEmpty out.Effects "no client effect"

                  // Phase 212 — the no-op is observable: one diagnostic naming
                  // the inert action, with a non-empty human-readable describe.
                  match out.Diagnostics with
                  | [ BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, _) as d ] ->
                      Expect.equal nodeId "n" "diagnostic names the originating node"
                      Expect.isNotEmpty (BoundedDiagnostic.describe d) "describe is readable"
                  | other -> failtestf "expected one UnsupportedOnBoundedPath diagnostic, got %A" other
          }

          test "SetState / Navigate emit no diagnostic (only the no-op arms do)" {
              let setOut =
                  BoundedActions.runBoundedAction "n" (Action.SetState("k", Some(jv 1), None)) store0

              Expect.isEmpty setOut.Diagnostics "SetState is a real mutation — no diagnostic"
              let navOut = BoundedActions.runBoundedAction "n" (Action.Navigate "/x") store0
              Expect.isEmpty navOut.Diagnostics "Navigate has a client effect — no diagnostic"
          }

          // ── The load-bearing safety property (Phase 153) ──────────────────
          test "SAFETY: a Call's onResult closure is never invoked (no ACE surface)" {
              let mutable invoked = false

              let onResult (_: obj) : obj =
                  invoked <- true
                  o "<closure>"

              let out =
                  BoundedActions.runBoundedAction "n" (Action.Call("https://evil", Some onResult, None)) store0

              Expect.isFalse invoked "the Call onResult closure must NEVER execute on the bounded path"
              Expect.equal out.Store.State store0.State "Call is a store-level no-op"
              Expect.isEmpty out.Effects "Call emits no client effect on the bounded path"
          }

          test "SAFETY: closures buried inside a Chain are never invoked" {
              let mutable calls = 0

              let throwing (_: obj) : obj =
                  calls <- calls + 1
                  failwith "closure executed!"

              let throwingRead (_: string) : obj =
                  calls <- calls + 1
                  failwith "closure executed!"

              let action =
                  Action.Chain
                      [ Action.SetState("ok", Some(jv 1), None)
                        Action.Call("e", Some throwing, None)
                        Action.ReadFileBody("f", None, FileReadEncoding.Base64, Some throwingRead) ]

              // The whole interpretation must complete without invoking any closure.
              let out = BoundedActions.runBoundedAction "up" action store0
              Expect.equal calls 0 "NO closure carried by any chained action was invoked"
              Expect.equal out.Store.State (Map.ofList [ "ok", o 1.0 ]) "the SetState still applied"
              Expect.equal out.Effects [ ClientEffect.ReadFileBody("up", "Base64") ] "only the closure-free read effect"
          }

          // ─── The handler-effect arm (DECISIONS.md D7 / D9) ──────────────────

          test "the inert arm declines, which is the documented no-op every arm-free placement gets" {
              let out =
                  BoundedActions.runBoundedAction "n" (Action.Call("/api/x", None, None)) store0

              match out.Diagnostics with
              | [ BoundedDiagnostic.UnsupportedOnBoundedPath _ ] -> ()
              | other -> failtestf "expected the inert-path diagnostic, got %A" other

              Expect.equal out.Store.State store0.State "and the store is untouched"
          }

          test "an arm that answers is folded IN PLACE inside a chain" {
              // The property the whole decision rests on: the answer's store is
              // threaded into the rest of the chain, so a call sees the write
              // before it and is seen by the write after it. A placement that
              // collected calls and ran them afterwards could not produce this.
              let arm: HandlerArm<string list> =
                  { Answer =
                      fun _ endpoint s seen ->
                          Some
                              { Store =
                                  { s with
                                      State = Map.add "k" (o "from the arm") s.State }
                                Effects = []
                                Diagnostics = []
                                Placement = seen @ [ endpoint ] } }

              let action =
                  Action.Chain
                      [ Action.SetState("k", Some(jv "before"), None)
                        Action.Call("/api/x", None, None)
                        Action.SetState("trailing", Some(jv "after"), None) ]

              let out, seen = BoundedActions.runBoundedActionWith arm "n" action store0 []

              Expect.equal seen [ "/api/x" ] "the arm was consulted once, from inside the chain"

              Expect.equal
                  out.Store.State
                  (Map.ofList [ "k", o "from the arm"; "trailing", o "after" ])
                  "the arm overwrote the write before it, and the write after it still landed"
          }

          test "an arm is consulted at every depth, not only at the top" {
              let arm: HandlerArm<string list> =
                  { Answer =
                      fun _ endpoint s seen ->
                          Some
                              { Store = s
                                Effects = []
                                Diagnostics = []
                                Placement = seen @ [ endpoint ] } }

              let action =
                  Action.Chain
                      [ Action.Call("/api/one", None, None)
                        Action.Chain [ Action.Call("/api/two", None, None) ] ]

              let _, seen = BoundedActions.runBoundedActionWith arm "n" action store0 []

              Expect.equal seen [ "/api/one"; "/api/two" ] "a call nested two chains deep reaches the same arm"
          }

          test "a call declaring its own result target is REFUSED, and the arm is never consulted" {
              // D9: the handler declares where its results land. A tree that
              // declares one too is refused rather than ignored — and refused
              // BEFORE the arm, so no placement can quietly honour it.
              let arm: HandlerArm<int> =
                  { Answer =
                      fun _ _ s consulted ->
                          Some
                              { Store = s
                                Effects = []
                                Diagnostics = []
                                Placement = consulted + 1 } }

              for target in [ CallResultTarget.State "orders.selected"; CallResultTarget.Query "recent" ] do
                  let action = Action.Call("/api/x", None, Some target)
                  let out, consulted = BoundedActions.runBoundedActionWith arm "n" action store0 0

                  Expect.equal consulted 0 "the arm was never consulted"
                  Expect.equal out.Store.State store0.State "and nothing was written"

                  match out.Diagnostics with
                  | [ BoundedDiagnostic.Refused(_, _, reason) ] ->
                      Expect.isFalse
                          (reason.Contains "orders.selected")
                          "the reason does not echo the wire-supplied key"

                      Expect.isFalse (reason.Contains "recent") "nor the wire-supplied query name"
                  | other -> failtestf "expected a single Refused diagnostic, got %A" other
          }

          // ─── Phase 782 — the server EFFECT path is a URL sink too ───────────
          //
          // A `ClientEffect.Navigate` is performed by the shim with whatever
          // router the host wired, so an unsafe route emitted here lands on the
          // client exactly as if the client had produced it. Sanitising only the
          // client action path would have left this half open.
          test "a javascript: route is neutralised on the server effect path" {
              let unsafeRoutes =
                  [ "javascript:alert(1)"
                    "JaVaScRiPt:alert(1)"
                    "vbscript:msgbox(1)"
                    "//evil.example/x" ]

              for route in unsafeRoutes do
                  let out = BoundedActions.runBoundedAction "n" (Action.Navigate route) store0

                  Expect.isEmpty out.Effects (sprintf "'%s' emits NO client effect" route)

                  Expect.equal out.Diagnostics.Length 1 (sprintf "'%s' refusal is recorded, not silent" route)

                  match out.Diagnostics with
                  | [ BoundedDiagnostic.Refused(nodeId, _, reason) ] ->
                      Expect.equal nodeId "n" "the diagnostic names the originating node"
                      Expect.stringContains reason "safe URL" "the diagnostic says why"
                  | other -> failtestf "expected a Refused diagnostic, got %A" other

              // A legitimate route still ships, sanitised.
              let ok = BoundedActions.runBoundedAction "n" (Action.Navigate "  /next  ") store0
              Expect.equal ok.Effects [ ClientEffect.Navigate "/next" ] "a safe route ships trimmed"
              Expect.isEmpty ok.Diagnostics "no diagnostic for a safe route"
          }

          test "a host-reserved State key is refused on the bounded path" {
              let out =
                  BoundedActions.runBoundedAction
                      "n"
                      (Action.SetState("host.session-token", Some(jv "stolen"), None))
                      store0

              Expect.equal out.Store.State store0.State "the host-reserved slot is NOT written"

              match out.Diagnostics with
              | [ BoundedDiagnostic.Refused(_, _, reason) ] ->
                  Expect.stringContains reason "host-reserved" "the diagnostic names the namespace"
              | other -> failtestf "expected a Refused diagnostic, got %A" other

              // Ordinary keys are unaffected — this is a namespace, not a ban.
              let ok =
                  BoundedActions.runBoundedAction "n" (Action.SetState("theme", Some(jv "dark"), None)) store0

              Expect.equal ok.Store.State (Map.ofList [ "theme", o "dark" ]) "an ordinary key writes normally"
              Expect.isEmpty ok.Diagnostics "no diagnostic for an ordinary key"
          } ]
