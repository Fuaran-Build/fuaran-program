module Fuaran.Program.Parity.Tests.ProofOracleTests

// ============================================================================
//  Phase 1715 — the differential host for the proved bounded fold.
//
//  `proofs/BoundedFold.fst` is a model of `BoundedActions.runBoundedActionWith`
//  and four theorems about it. A model is a claim about the code only if
//  something runs the two side by side, and this is that something: it runs the
//  EXTRACTION of the model (`proofs/oracle/BoundedFold.fs`, byte-identical to
//  what the prover emitted — `proofs/check.ps1` step 4) beside production over
//  two corpora, comparing the store, the effect list and the diagnostics at
//  once, and reporting the FIRST divergence with the case that produced it.
//
//  What is being compared, exactly. The model axiomatises the binding
//  resolver, the URL floor, the host-reserved predicate and the log-safe route
//  projection: they are total arrows it takes as parameters. This host supplies
//  those arrows by calling PRODUCTION's own implementations, so the only thing
//  that can disagree here is the FOLD — which is what the theorems are about.
//  An axiom wired to a second implementation would be certifying the wrong
//  half.
//
//  The two corpora answer different questions.
//
//    * The conformance corpus's driver-semantics family is the documents this
//      repository is certified against. Every scripted event is resolved to the
//      `Action` the trust boundary would hand the fold, and each is run through
//      both, threading the store from step to step. It answers "does the model
//      agree on the actions real documents contain".
//    * The arm-complete corpus names every arm of the closed union and every
//      refusal path inside the three arms that have one. It answers "does the
//      model agree on the actions that are HARD", which the first corpus has no
//      reason to contain and mostly does not.
//
//  And the go-red case is what says the comparison can lose at all. It commits
//  a fold that INVOKES a carried closure — the one defect the model's law 2
//  rules out and the one a bounded interpreter must never have — and requires
//  the comparison against the oracle to fail on it. Without that, a comparison
//  that silently agreed with everything would report the same green.
// ============================================================================

open Expecto
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Ops
open Fuaran.UI.Ops.Types
open Fuaran.UI.OpStream.Replay
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded
open Fuaran.Program.Parity

/// Non-null box (F# 10 nullness: `box` yields `objnull`; the store's
/// `Map<string, obj>` wants non-null — the same `o` posture the bounded
/// interpreter's own suite uses).
let private o (v: 'T) : obj = box v |> Unchecked.nonNull

// ─── Translation: production ⇄ the model ────────────────────────────────────
//
// The model owns small closed types of its own so its extraction references
// `Prims` and nothing else (`proofs/oracle/Prims.fs` is that whole runtime), so
// a translation is unavoidable. It is deliberately DUMB — a constructor for a
// constructor, with no decision in it — because a translation that decided
// anything would be a third implementation of the fold, sitting between the two
// this file exists to compare.

/// Aliases, and not for brevity: `unbox<Binding<string>>` closes two angle
/// brackets in a row and `unbox<Map<string, JVal>>` closes two of its own, both
/// of which F# lexes as the shift and composition operators before it lexes
/// them as type arguments.
type private TextBinding = Binding<string>
type private JValBinding = Binding<JVal>
type private I18nArgs = Map<string, JVal>

let private modelOpt (x: 'a option) : BoundedFold.opt<'a> =
    match x with
    | Some v -> BoundedFold.OSome v
    | None -> BoundedFold.ONone

let private modelTarget (t: NavigateTarget) : BoundedFold.nav_target =
    match t with
    | NavigateTarget.Self -> BoundedFold.NSelf
    | NavigateTarget.Blank -> BoundedFold.NBlank

let private prodTarget (t: BoundedFold.nav_target) : NavigateTarget =
    match t with
    | BoundedFold.NSelf -> NavigateTarget.Self
    | BoundedFold.NBlank -> NavigateTarget.Blank

let private modelEncoding (e: FileReadEncoding) : BoundedFold.file_encoding =
    match e with
    | FileReadEncoding.Text -> BoundedFold.FText
    | FileReadEncoding.Base64 -> BoundedFold.FBase64
    | FileReadEncoding.DataUrl -> BoundedFold.FDataUrl

let private modelText (t: TextSource) : BoundedFold.text_source<obj> =
    match t with
    | TextSource.Literal text -> BoundedFold.TLiteral text
    | TextSource.Bound binding -> BoundedFold.TBound(o binding)
    | TextSource.I18n(key, args) -> BoundedFold.TI18n(key, o args)

let private prodText (t: BoundedFold.text_source<obj>) : TextSource =
    match t with
    | BoundedFold.TLiteral text -> TextSource.Literal text
    | BoundedFold.TBound binding -> TextSource.Bound(unbox<TextBinding> binding)
    | BoundedFold.TI18n(key, args) -> TextSource.I18n(key, unbox<I18nArgs> args)

let private modelCallTarget (t: CallResultTarget) : BoundedFold.call_target =
    match t with
    | CallResultTarget.State key -> BoundedFold.CTState key
    | CallResultTarget.Query name -> BoundedFold.CTQuery name

// `Action.Dispatch` is marked in-process-only upstream, so mentioning it raises
// FS0044. The translation below is a TOTAL analysis of the closed union: it
// must name every case that exists, and naming one is not authoring one.
// Scoped to the one declaration, and reopened immediately after it.
#nowarn "44"

let rec private modelAction (a: Action<obj>) : BoundedFold.action<obj, obj, obj> =
    match a with
    | Action.Chain ops -> BoundedFold.AChain(ops |> List.map modelAction)
    | Action.WriteToClipboard text -> BoundedFold.AWriteToClipboard(modelText text)
    | Action.Dispatch msg -> BoundedFold.ADispatch(o msg)
    | Action.Invoke(capabilityId, args) -> BoundedFold.AInvoke(capabilityId, o args)
    | Action.ReadFileBody(fileRef, fileHandle, encoding, onRead) ->
        BoundedFold.AReadFileBody(fileRef, o fileHandle, modelEncoding encoding, o onRead)
    | Action.Call(endpoint, onResult, into) ->
        BoundedFold.ACall(endpoint, o onResult, modelOpt (into |> Option.map modelCallTarget))
    | Action.Navigate(route, target) -> BoundedFold.ANavigate(modelText route, modelTarget target)
    | Action.CommitLocal nodeId -> BoundedFold.ACommitLocal nodeId
    | Action.Notify(channel, payload) -> BoundedFold.ANotify(channel, o payload)
    | Action.SetState(key, value, valueFrom) ->
        // `JValObj.toObj` is the model's lowering AXIOM, applied here rather
        // than modelled — the literal payload reaches the model already lowered,
        // exactly as the resolver arrow's resolved value does.
        BoundedFold.ASetState(
            key,
            modelOpt (value |> Option.map (fun jv -> JValObj.toObj jv)),
            modelOpt (valueFrom |> Option.map o)
        )
    | Action.AiTool(toolName, args) -> BoundedFold.AAiTool(toolName, o args)
    | Action.Print -> BoundedFold.APrint
    | Action.Confirm(prompt, onConfirm, onCancel) ->
        BoundedFold.AConfirm(modelText prompt, modelAction onConfirm, modelOpt (onCancel |> Option.map modelAction))
    | Action.Focus nodeId -> BoundedFold.AFocus nodeId

#warnon "44"

let private modelEffect (e: ClientEffect) : BoundedFold.client_effect =
    match e with
    | ClientEffect.Navigate(route, target) -> BoundedFold.ENavigate(route, modelTarget target)
    | ClientEffect.WriteToClipboard text -> BoundedFold.EClipboard text
    | ClientEffect.Print -> BoundedFold.EPrint
    | ClientEffect.Focus nodeId -> BoundedFold.EFocus nodeId
    | ClientEffect.ReadFileBody(nodeId, encoding) -> BoundedFold.EReadFileBody(nodeId, encoding)
    | other ->
        // The model's effect union carries exactly the arms the bounded fold
        // emits, which is a CLAIM about the fold rather than a convenience —
        // and this is where it is checked. A `PushState`, a `Download` or a
        // `Confirm` reaching here means the fold gained a reach the model does
        // not describe, and failing loudly is the only honest answer: mapping
        // it to something would make the model agree by discarding the
        // disagreement.
        failwithf
            "the bounded fold emitted %A, which proofs/BoundedFold.fst's effect union does not carry. Either the fold gained an arm or the model is stale — do not widen this match without widening the model."
            other

let private prodEffect (e: BoundedFold.client_effect) : ClientEffect =
    match e with
    | BoundedFold.ENavigate(route, target) -> ClientEffect.Navigate(route, prodTarget target)
    | BoundedFold.EClipboard text -> ClientEffect.WriteToClipboard text
    | BoundedFold.EPrint -> ClientEffect.Print
    | BoundedFold.EFocus nodeId -> ClientEffect.Focus nodeId
    | BoundedFold.EReadFileBody(nodeId, encoding) -> ClientEffect.ReadFileBody(nodeId, encoding)

let private modelDiagnostic (d: BoundedDiagnostic) : BoundedFold.diagnostic =
    match d with
    | BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, action) -> BoundedFold.DUnsupported(nodeId, action)
    | BoundedDiagnostic.Refused(nodeId, action, reason) -> BoundedFold.DRefused(nodeId, action, reason)

let private prodDiagnostic (d: BoundedFold.diagnostic) : BoundedDiagnostic =
    match d with
    | BoundedFold.DUnsupported(nodeId, action) -> BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, action)
    | BoundedFold.DRefused(nodeId, action, reason) -> BoundedDiagnostic.Refused(nodeId, action, reason)

/// The model's store is the `State` channel alone, as an association list. The
/// other channels are host context the fold never writes — they reach the model
/// only through the axioms below, which rebuild the real `BindingSources` from
/// the step's own store.
let private modelStore (s: BoundedStore) : BoundedFold.store<obj> = s.State |> Map.toList

let private prodStore (template: BoundedStore) (ms: BoundedFold.store<obj>) : BoundedStore =
    { template with State = Map.ofList ms }

// ─── The axioms, wired to production ────────────────────────────────────────

let private axiomsFor (template: BoundedStore) : BoundedFold.axioms<obj, obj> =
    { is_reserved = Fuaran.UI.Renderer.StateKeys.isHostReserved
      reserved_prefix = Fuaran.UI.Renderer.StateKeys.HostReservedPrefix
      resolve_jval =
        fun ms binding ->
            match resolveJVal (prodStore template ms) (unbox<JValBinding> binding) with
            | Resolved jv -> BoundedFold.JResolved(JValObj.toObj jv)
            | NotResolved -> BoundedFold.JNotResolved
            | Errored m -> BoundedFold.JErrored m
            | I18nUnresolved k -> BoundedFold.JI18nUnresolved k
      resolve_scalar =
        fun ms binding ->
            match resolveScalarText (prodStore template ms) (unbox<TextBinding> binding) with
            // The resolved-but-NULL value is the unwritten-`State` steady state,
            // and the two `TextSource` arms treat it differently — so it is
            // carried into the model rather than collapsed here.
            | Resolved value ->
                BoundedFold.SResolved(
                    if isNull (box value) then
                        BoundedFold.ONone
                    else
                        BoundedFold.OSome value
                )
            | NotResolved -> BoundedFold.SNotResolved
            | Errored m -> BoundedFold.SErrored m
            | I18nUnresolved k -> BoundedFold.SI18nUnresolved k
      i18n_has = fun ms key -> Map.containsKey key (prodStore template ms).I18n
      resolve_text = fun ms text -> resolveTextSource (prodStore template ms) (prodText text)
      sanitize_url =
        fun url ->
            match Fuaran.UI.Renderer.Sanitize.sanitizeUrl url with
            | Some safe -> BoundedFold.OSome safe
            | None -> BoundedFold.ONone
      route_path = Fuaran.UI.Ops.ActionInvocation.ActionInvocation.routePath }

let private modelArm (template: BoundedStore) (h: HandlerArm<obj>) : BoundedFold.arm<obj, obj> =
    { answer =
        fun nodeId endpoint ms placement ->
            match h.Answer nodeId endpoint (prodStore template ms) placement with
            | None -> BoundedFold.ONone
            | Some answer ->
                BoundedFold.OSome
                    { h_store = modelStore answer.Store
                      h_effects = answer.Effects |> List.map modelEffect
                      h_diagnostics = answer.Diagnostics |> List.map modelDiagnostic
                      h_placement = answer.Placement } }

// ─── The comparison ─────────────────────────────────────────────────────────

/// The fold under comparison. Production is one; the go-red case below is
/// another, and it is deliberately wrong.
type private Fold = HandlerArm<obj> -> string -> Action<obj> -> BoundedStore -> obj -> BoundedOutcome * obj

let private production: Fold = BoundedActions.runBoundedActionWith

/// The store, the effects and the diagnostics at once — a fold that got the
/// store right and the diagnostics wrong is still a fold that disagrees, and a
/// comparison that looked at one field would not say so.
let private divergence (where: string) (prod: BoundedOutcome) (model: BoundedFold.outcome<obj>) : string option =
    let prodState = prod.Store.State |> Map.toList
    let modelState = model.o_store |> List.sortBy fst
    let modelEffects = model.o_effects |> List.map prodEffect
    let modelDiagnostics = model.o_diagnostics |> List.map prodDiagnostic

    if prodState <> modelState then
        Some(sprintf "%s: STORE\n  production: %A\n  oracle:     %A" where prodState modelState)
    elif prod.Effects <> modelEffects then
        Some(sprintf "%s: EFFECTS\n  production: %A\n  oracle:     %A" where prod.Effects modelEffects)
    elif prod.Diagnostics <> modelDiagnostics then
        Some(sprintf "%s: DIAGNOSTICS\n  production: %A\n  oracle:     %A" where prod.Diagnostics modelDiagnostics)
    else
        None

/// Run one action through both folds against the same store, and report the
/// divergence if there is one. Returns the store production produced, so a
/// caller can thread a script of events through it.
let private step
    (fold: Fold)
    (arm: HandlerArm<obj>)
    (where: string)
    (nodeId: string)
    (action: Action<obj>)
    (s: BoundedStore)
    : BoundedStore * string option =
    let prod, _ = fold arm nodeId action s (o ())

    let modelOutcome, _ =
        BoundedFold.run (axiomsFor s) (modelArm s arm) nodeId (modelAction action) (modelStore s) (o ())

    prod.Store, divergence where prod modelOutcome

let private runScript
    (fold: Fold)
    (arm: HandlerArm<obj>)
    (cases: (string * string * Action<obj>) list)
    (initial: BoundedStore)
    : string list =
    cases
    |> List.fold
        (fun (s, found) (where, nodeId, action) ->
            let next, d = step fold arm where nodeId action s

            next,
            (match d with
             | Some m -> m :: found
             | None -> found))
        (initial, [])
    |> snd
    |> List.rev

// ─── Corpus A: the conformance corpus's driver-semantics family ─────────────

/// Every scripted event of every driver-semantics scenario, resolved to the
/// `Action` the trust boundary would hand the fold. The gate is opened (`fun _
/// -> true`) deliberately: the dispatch policy decides WHETHER an action is
/// offered to the fold, and this family is about what the fold does with one.
let private corpusCases () : (string * string * Action<obj>) list =
    FixtureIo.load FixtureIo.fixturesRoot
    |> List.collect (fun fixture ->
        match JsonDecode.decodeNode fixture.TreeJson with
        | Error err -> failwithf "%s: the corpus's tree did not decode: %A" fixture.Name err
        | Ok wire ->
            let tree = WireTree.reify wire

            fixture.Events
            |> List.mapi (fun index ev ->
                let live: LiveEvent =
                    { ConnId = "proof-oracle"
                      NodeId = ev.NodeId
                      Event = ev.Event
                      Payload = ev.Payload |> Map.map (fun _ v -> LiveValue.Str v)
                      LastSeq = index }

                match Validation.validate (fun _ -> true) tree live with
                | Ok validated ->
                    validated.Action
                    |> Option.map (fun action ->
                        (sprintf "%s step %d (%s)" fixture.Name index ev.NodeId, ev.NodeId, action))
                | Error _ -> None)
            |> List.choose id)

// ─── Corpus B: every arm, and every refusal path inside one ─────────────────

let private jstr (s: string) = JStr s

/// A store with something in every channel the axioms read, so a binding that
/// resolves and a binding that does not are both reachable.
let private seeded: BoundedStore =
    { empty with
        State = Map.ofList [ "greeting", o "hello"; "route", o "/orders/7" ]
        I18n = Map.ofList [ "title", "Orders" ] }

let private armCompleteCases: (string * string * Action<obj>) list =
    let case name action = (name, "n1", action)

    [ // The one mutation, and its three refusal paths.
      case "SetState literal" (Action.SetState("written", Some(jstr "v"), None))
      case "SetState overwrite" (Action.SetState("greeting", Some(jstr "bonjour"), None))
      case
          "SetState host-reserved key"
          (Action.SetState(Fuaran.UI.Renderer.StateKeys.HostReservedPrefix + "secret", Some(jstr "v"), None))
      case
          "SetState valueFrom resolved"
          (Action.SetState("copy", None, Some(Binding.State("greeting", Some(jstr "x")))))
      case "SetState valueFrom unresolved" (Action.SetState("copy", None, Some(Binding.Filter("absent", None))))
      case "SetState no payload at all" (Action.SetState("copy", None, None))

      // Navigate: literal, bound, i18n, and the floor.
      case "Navigate literal" (Action.Navigate(TextSource.Literal "/orders", NavigateTarget.Self))
      case "Navigate literal blank" (Action.Navigate(TextSource.Literal "/orders", NavigateTarget.Blank))
      case "Navigate unsafe scheme" (Action.Navigate(TextSource.Literal "javascript:alert(1)", NavigateTarget.Self))
      case
          "Navigate bound resolved"
          (Action.Navigate(TextSource.Bound(Binding.State("route", Some "/fallback")), NavigateTarget.Self))
      case
          "Navigate bound unresolved"
          (Action.Navigate(TextSource.Bound(Binding.Filter("absent", None)), NavigateTarget.Self))
      case "Navigate i18n present" (Action.Navigate(TextSource.I18n("title", Map.empty), NavigateTarget.Self))
      case "Navigate i18n absent" (Action.Navigate(TextSource.I18n("missing", Map.empty), NavigateTarget.Self))

      // Clipboard: the same three sources, with the opposite answer for a
      // resolved-but-empty value.
      case "Clipboard literal" (Action.WriteToClipboard(TextSource.Literal "copied"))
      case "Clipboard bound resolved" (Action.WriteToClipboard(TextSource.Bound(Binding.State("greeting", Some "d"))))
      case
          "Clipboard bound unwritten state"
          (Action.WriteToClipboard(TextSource.Bound(Binding.State("never-written", None))))
      case "Clipboard bound unresolved" (Action.WriteToClipboard(TextSource.Bound(Binding.Filter("absent", None))))
      case "Clipboard i18n present" (Action.WriteToClipboard(TextSource.I18n("title", Map.empty)))
      case "Clipboard i18n absent" (Action.WriteToClipboard(TextSource.I18n("missing", Map.empty)))

      // The payload-free and node-addressed arms.
      case "Print" Action.Print
      case "Focus" (Action.Focus "target-node")
      case "ReadFileBody Text" (Action.ReadFileBody("f", None, FileReadEncoding.Text, None))
      case "ReadFileBody Base64" (Action.ReadFileBody("f", None, FileReadEncoding.Base64, Some(fun _ -> o "never")))
      case "ReadFileBody DataUrl" (Action.ReadFileBody("f", None, FileReadEncoding.DataUrl, None))

      // The documented no-ops.
      case "Notify" (Action.Notify("audit", jstr "p"))
      case "AiTool" (Action.AiTool("summarise", jstr "p"))
      case "Invoke" (Action.Invoke("cap", []))
      case "CommitLocal" (Action.CommitLocal "field")
      case
          "Confirm"
          (Action.Confirm(TextSource.Literal "sure?", Action.SetState("confirmed", Some(jstr "y"), None), None))

      // The call arms: declined, and refused for declaring its own target.
      case "Call declined" (Action.Call("/api/x", None, None))
      case "Call with closure declined" (Action.Call("/api/x", Some(fun _ -> o "never"), None))
      case "Call with result target" (Action.Call("/api/x", None, Some(CallResultTarget.State "slot")))

      // Composition, including the nesting a chain is for.
      case
          "Chain of two writes"
          (Action.Chain
              [ Action.SetState("a", Some(jstr "1"), None)
                Action.SetState("b", Some(jstr "2"), None) ])
      case
          "Chain splicing a write between two effects"
          (Action.Chain
              [ Action.Navigate(TextSource.Literal "/first", NavigateTarget.Self)
                Action.SetState("spliced", Some(jstr "mid"), None)
                Action.WriteToClipboard(TextSource.Bound(Binding.State("spliced", Some "d"))) ])
      case
          "Chain carrying closures at depth"
          (Action.Chain
              [ Action.Call("/api/y", Some(fun _ -> o "never"), None)
                Action.Chain [ Action.ReadFileBody("f", None, FileReadEncoding.Text, Some(fun _ -> o "never")) ] ])
      case "Empty chain" (Action.Chain []) ]

/// The `Dispatch` arm needs its own binding: constructing one raises FS0044,
/// and the suppression is scoped to the declaration that needs it rather than
/// to the whole corpus above.
#nowarn "44"

let private dispatchCase: string * string * Action<obj> =
    ("Dispatch", "n1", Action.Dispatch(o "a host message"))

#warnon "44"

// ─── A placement that ANSWERS, so the seam is exercised rather than assumed ──

/// An arm that answers one endpoint by writing into the store and emitting an
/// effect, and declines everything else. The fold threads its answer in place,
/// which is what makes a call inside a chain see the writes before it — so an
/// arm that answers is the only way the chain arm's interesting case is
/// reached at all.
let private answeringArm: HandlerArm<obj> =
    { Answer =
        fun _ endpoint s placement ->
            if endpoint = "/api/answered" then
                Some
                    { Store =
                        { s with
                            State = Map.add "answered" (o "yes") s.State }
                      Effects = [ ClientEffect.WriteToClipboard "from the handler" ]
                      Diagnostics =
                        [ BoundedDiagnostic.Refused("n1", "Call(/api/answered)", "a reason the handler chose") ]
                      Placement = placement }
            else
                None }

let private answeredCases: (string * string * Action<obj>) list =
    [ ("Call answered", "n1", Action.Call("/api/answered", None, None))
      ("Call declined by an arm that answers something else", "n1", Action.Call("/api/other", None, None))
      ("Answered call spliced inside a chain",
       "n1",
       Action.Chain
           [ Action.SetState("before", Some(jstr "1"), None)
             Action.Call("/api/answered", Some(fun _ -> o "never"), None)
             Action.WriteToClipboard(TextSource.Bound(Binding.State("answered", Some "d"))) ]) ]

// ─── The go-red case ────────────────────────────────────────────────────────

/// A fold that INVOKES the closure a `Call` carries and lets its answer reach
/// the store. This is precisely what `run_no_closure` rules out and what the
/// wire decoder's inert sentinels exist to make harmless, and it is committed
/// here on purpose: a differential that cannot be made to fail is not evidence
/// of anything. Everything else defers to production, so the ONLY difference
/// between this fold and the real one is the defect.
let private closureInvoking: Fold =
    fun arm nodeId action s placement ->
        match action with
        | Action.Call(_, Some onResult, None) ->
            let produced = onResult (o "the handler's answer")

            { Store =
                { s with
                    State = Map.add "invoked" (Unchecked.nonNull produced) s.State }
              Effects = []
              Diagnostics = [] },
            placement
        | _ -> BoundedActions.runBoundedActionWith arm nodeId action s placement

// ─── The tests ──────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    let corpus = corpusCases ()

    testList
        "Phase 1715 - the proved bounded fold as oracle"
        [ test "the driver-semantics family yields actions to compare" {
              // A corpus that silently resolved to nothing would report the
              // same green as one that ran every scenario. The floor is the
              // number of scenarios the family declares, since every scenario
              // scripts at least one event.
              let declared = FixtureIo.scenarios FixtureIo.fixturesRoot

              Expect.isNonEmpty
                  declared
                  $"the corpus enumerates no driver-semantics scenario under {FixtureIo.fixturesRoot}"

              Expect.isGreaterThanOrEqual
                  corpus.Length
                  declared.Length
                  "every declared scenario contributed at least one action for the oracle to be compared over"
          }

          test "the oracle agrees with production over the driver-semantics family" {
              let divergences = runScript production HandlerArm.inert corpus empty

              match divergences with
              | [] -> ()
              | first :: rest ->
                  failtestf
                      "the extracted model and production disagree on %d of %d corpus action(s). First divergence:\n%s"
                      (List.length rest + 1)
                      corpus.Length
                      first
          }

          test "the oracle agrees with production over every arm and every refusal path" {
              let cases = armCompleteCases @ [ dispatchCase ]
              let divergences = runScript production HandlerArm.inert cases seeded

              // The union has fourteen arms and this corpus must name all of
              // them, or the "arm-complete" claim in its name is false. Counted
              // rather than asserted in prose: a case deleted in a later edit
              // would otherwise leave the name standing over a corpus that no
              // longer earns it.
              let armsCovered =
                  cases
                  |> List.map (fun (_, _, a) -> Fuaran.UI.Ops.ActionInvocation.ActionInvocation.describe a)
                  |> List.map (fun name ->
                      match name.IndexOf '(' with
                      | -1 -> name
                      | i -> name.Substring(0, i))
                  |> List.distinct

              Expect.equal
                  armsCovered.Length
                  14
                  $"every arm of the closed action union is exercised (covered: %A{List.sort armsCovered})"

              match divergences with
              | [] -> ()
              | first :: rest ->
                  failtestf
                      "the extracted model and production disagree on %d of %d arm-complete case(s). First divergence:\n%s"
                      (List.length rest + 1)
                      cases.Length
                      first
          }

          test "the oracle agrees with production when the placement ANSWERS a call" {
              let divergences = runScript production answeringArm answeredCases seeded

              match divergences with
              | [] -> ()
              | first :: _ ->
                  failtestf "the extracted model and production disagree on the answered-call seam:\n%s" first
          }

          test "GO RED: a fold that invokes a carried closure loses the comparison" {
              // The oracle CANNOT invoke a closure — the model's closure slots
              // have an abstract type with no elimination form, which is what
              // `run_no_closure` turns into a theorem. So a fold that does
              // invoke one must diverge from it, and if it does not, this whole
              // file is comparing nothing.
              let carrying =
                  [ ("go-red", "n1", Action.Call("/api/x", Some(fun _ -> o "the closure ran"), None)) ]

              let honest = runScript production HandlerArm.inert carrying seeded

              Expect.isEmpty honest "production agrees with the oracle on the very case the defect is committed against"

              let defective = runScript closureInvoking HandlerArm.inert carrying seeded

              Expect.isNonEmpty
                  defective
                  "a fold that invokes the closure a Call carries MUST diverge from the proved model — a comparison that cannot lose is not evidence"
          } ]
