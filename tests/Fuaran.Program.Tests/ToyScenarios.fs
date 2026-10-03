/// The `driver-semantics-toy` scenario family (fuaran#2011): the conformance
/// corpus's step traces over the TOY witness, read, driven and compared here.
///
/// Program could certify its wire format only through the UI witness: the
/// `driver-semantics` family's trees are UI trees, so every leg that ran it ran
/// a UI-tier type. This module is the other family's harness, and it names no
/// UI-tier type at all — the toy witness is the whole domain — which is what
/// lets Program run a green gate on its own format once the UI adapters leave
/// (`PROGRAM_WIRE.md` §10.6; the boundary test in `CoreBoundaryTests.fs`).
///
/// Fable-clean and IO-free, like the UI family's runner: the client placement
/// below is compiled to JavaScript by the Fable parity leg, which reads the SAME
/// files and runs the SAME comparison. Reading a file is the one per-host part,
/// so each host passes text in. The server-logic placement is .NET-only and
/// lives in `ToyServerPlacement.fs`.
module Fuaran.Program.Tests.ToyScenarios

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Tests.ToyDomain

// ─── The corpus vocabulary this harness reads ───────────────────────────────

/// The family's name in the manifest, and its directory.
[<Literal>]
let Family = "driver-semantics-toy"

/// The UI family's name, which this harness never runs — named here only so the
/// selection below can say which families exist.
[<Literal>]
let UiFamily = "driver-semantics"

/// §10.2's two obligations. `bounded-loop` is the one every scenario presumed
/// until this family; `handler-loop` (§10.6) presumes a placement that ANSWERS
/// a call with a host-registered handler, which a placement with no handler
/// registry is out of scope for — not non-conformant.
[<Literal>]
let BoundedLoop = "bounded-loop"

[<Literal>]
let HandlerLoop = "handler-loop"

/// §10.6's one registered handler-set name. A scenario NAMES a handler set and
/// every host CONSTRUCTS what the name denotes — the corpus never carries
/// handlers as data, for the reason it never carries a host policy.
[<Literal>]
let ToyRelabel = "toy-relabel"

/// The environment variable a gate run SELECTS scenario families with. Unset
/// or empty selects every family the manifest declares; otherwise a comma list
/// of family names, every one of which must be declared.
[<Literal>]
let SelectionVariable = "FUARAN_PROGRAM_FAMILIES"

/// Which families a run selects, given the raw variable and the manifest's
/// declared scenario families. An undeclared name is REFUSED rather than
/// ignored: a selection that quietly matched nothing would run an empty family
/// and report green, which is the vacuous pass every harness obligation in
/// §10.1 exists to refuse.
let selection (raw: string option) (declared: string list) : Result<string list, string> =
    match raw with
    | None -> Ok declared
    | Some text when text.Trim() = "" -> Ok declared
    | Some text ->
        let names =
            text.Split ','
            |> Array.map (fun s -> s.Trim())
            |> Array.filter ((<>) "")
            |> List.ofArray

        match names |> List.filter (fun n -> not (List.contains n declared)) with
        | [] -> Ok names
        | unknown ->
            Error(
                sprintf
                    "%s names scenario famil%s the manifest does not declare: %s (declared: %s)"
                    SelectionVariable
                    (if List.length unknown = 1 then "y" else "ies")
                    (String.concat ", " unknown)
                    (String.concat ", " declared)
            )

// ─── A scenario ──────────────────────────────────────────────────────────────

/// One scripted event. The toy transport reads no payload — the toy has no
/// input node — but the script still carries one, in §10.3's shape.
type ToyEvent =
    { NodeId: string
      Event: string
      Payload: (string * string) list }

/// What one step records: the resolved tree's CANONICAL bytes (this
/// specification owns the toy tree, so after decoding a recorded tree and
/// re-encoding it, bytes are the comparison), the effects as emitted, and
/// whether the EVENT was refused.
type ToyStep =
    { Tree: string
      Effects: string list
      Refused: bool }

/// A scenario as the manifest enumerates it, with its three documents decoded.
type ToyScenario =
    { Name: string
      Requires: string
      HostHandlers: string option
      Tree: ToyNode
      Events: ToyEvent list
      Expected: ToyStep list }

/// One manifest entry of this family: what to read, and what it presumes.
type ToyEntry =
    { Name: string
      Requires: string
      HostHandlers: string option
      HostPolicy: string option
      TreeFile: string
      EventsFile: string
      ExpectationFile: string
      Steps: int }

let private member' (name: string) (value: JVal) : JVal option =
    match value with
    | JObj members -> members |> List.tryFind (fst >> (=) name) |> Option.map snd
    | _ -> None

let private str (name: string) (value: JVal) : Result<string, string> =
    match member' name value with
    | Some(JStr s) -> Ok s
    | _ -> Error(sprintf "member '%s' is absent or not a string" name)

let private optStr (name: string) (value: JVal) : Result<string option, string> =
    match member' name value with
    | None -> Ok None
    | Some(JStr s) -> Ok(Some s)
    | Some _ -> Error(sprintf "member '%s' is not a string" name)

let private traverse (f: 'a -> Result<'b, string>) (items: 'a list) : Result<'b list, string> =
    (Ok [], items)
    ||> List.fold (fun acc item -> acc |> Result.bind (fun done_ -> f item |> Result.map (fun v -> v :: done_)))
    |> Result.map List.rev

/// The manifest's declared scenario families.
let declaredFamilies (manifestJson: string) : Result<string list, string> =
    match Json.parse manifestJson with
    | Error e -> Error("the manifest does not parse: " + e)
    | Ok manifest ->
        match member' "scenarioFamilies" manifest with
        | Some(JArr families) ->
            families
            |> traverse (fun f ->
                match f with
                | JStr s -> Ok s
                | _ -> Error "a scenario family is not a string")
        | _ -> Error "the manifest declares no scenarioFamilies array"

/// This family's enumeration, in declared order. The MANIFEST is enumerated,
/// never the directory: a scenario on disk the manifest forgot is a behaviour
/// nobody is required to reproduce.
let entries (manifestJson: string) : Result<ToyEntry list, string> =
    match Json.parse manifestJson with
    | Error e -> Error("the manifest does not parse: " + e)
    | Ok manifest ->
        match member' "scenarios" manifest with
        | Some(JArr all) ->
            all
            |> List.filter (fun s -> member' "family" s = Some(JStr Family))
            |> traverse (fun s ->
                let files = member' "files" s |> Option.defaultValue (JObj [])

                str "name" s
                |> Result.bind (fun name ->
                    let within (r: Result<'T, string>) =
                        r |> Result.mapError (fun e -> name + ": " + e)

                    within (str "requires" s)
                    |> Result.bind (fun requires ->
                        within (optStr "hostHandlers" s)
                        |> Result.bind (fun handlers ->
                            within (optStr "hostPolicy" s)
                            |> Result.bind (fun policy ->
                                within (str "tree" files)
                                |> Result.bind (fun tree ->
                                    within (str "events" files)
                                    |> Result.bind (fun events ->
                                        within (str "expectation" files)
                                        |> Result.bind (fun expectation ->
                                            match member' "steps" s with
                                            | Some(JInt steps) ->
                                                Ok
                                                    { Name = name
                                                      Requires = requires
                                                      HostHandlers = handlers
                                                      HostPolicy = policy
                                                      TreeFile = tree
                                                      EventsFile = events
                                                      ExpectationFile = expectation
                                                      Steps = steps }
                                            | _ -> Error(name + ": no integer 'steps'")))))))))
        | _ -> Error "the manifest declares no scenario array"

let private parseEvents (name: string) (json: string) : Result<ToyEvent list, string> =
    match Json.parse json with
    | Error e -> Error(sprintf "%s: the event script does not parse: %s" name e)
    | Ok(JArr events) ->
        events
        |> traverse (fun ev ->
            str "nodeId" ev
            |> Result.bind (fun nodeId ->
                str "event" ev
                |> Result.bind (fun event ->
                    match member' "payload" ev with
                    | Some(JObj members) ->
                        members
                        |> traverse (fun (k, v) ->
                            match v with
                            | JStr s -> Ok(k, s)
                            | _ -> Error "a payload value is not a string")
                        |> Result.map (fun payload ->
                            { NodeId = nodeId
                              Event = event
                              Payload = payload })
                    | _ -> Error "an event carries no payload object")))
        |> Result.mapError (fun e -> name + ": " + e)
    | Ok _ -> Error(name + ": the event script is not an array")

let private decodeNodeValue (tree: JVal) : Result<ToyNode, string> =
    ToyWire.decodeNode tree |> Result.mapError (fun r -> r.Class + ": " + r.Detail)

/// A recorded step, brought into THIS host's terms: the tree is decoded with
/// the toy decoder and re-encoded canonically, and every effect is decoded and
/// re-emitted — so an effect outside the toy's vocabulary, or one whose bytes
/// are not the emitter's, fails to LOAD rather than travelling on as an opaque
/// string somebody expected to be honoured. A `denials` member is refused: the
/// toy family registers no host policy (§10.6), so a trace recording one is a
/// fact about a policy nobody could construct.
let private parseExpectation (name: string) (json: string) : Result<ToyStep list, string> =
    match Json.parse json with
    | Error e -> Error(sprintf "%s: the expectation does not parse: %s" name e)
    | Ok(JArr steps) ->
        steps
        |> List.mapi (fun i s -> i, s)
        |> traverse (fun (i, step) ->
            let at (e: string) = sprintf "%s: step %d: %s" name i e

            match member' "denials" step with
            | Some _ -> Error(at "records denials, and the toy family registers no host policy")
            | None ->
                match member' "tree" step, member' "effects" step, member' "refused" step with
                | Some tree, Some(JArr effects), Some(JBool refused) ->
                    match decodeNodeValue tree with
                    | Error e -> Error(at ("the tree does not decode: " + e))
                    | Ok node ->
                        effects
                        |> traverse (fun e ->
                            match e with
                            | JStr bytes ->
                                match ToyWire.decodeEffect bytes with
                                | Ok effect when ToyWire.encodeEffect effect = bytes -> Ok bytes
                                | Ok _ -> Error(at "an effect's bytes are not the emitter's")
                                | Error r -> Error(at ("an effect does not decode: " + r.Detail))
                            | _ -> Error(at "an effect is not recorded as a string"))
                        |> Result.map (fun effects ->
                            { Tree = ToyWire.renderNode node
                              Effects = effects
                              Refused = refused })
                | _ -> Error(at "a step declares tree / effects / refused, and one is missing or mistyped"))
    | Ok _ -> Error(name + ": the expectation is not an array")

/// Assemble a scenario from its entry and its three files' TEXT. The read is
/// the host's; everything after it is shared.
let load
    (entry: ToyEntry)
    (treeJson: string)
    (eventsJson: string)
    (expectationJson: string)
    : Result<ToyScenario, string> =
    match entry.HostPolicy with
    | Some policy ->
        Error(sprintf "%s: names hostPolicy '%s', which the toy family does not register" entry.Name policy)
    | None ->
        match entry.Requires, entry.HostHandlers with
        | r, _ when r <> BoundedLoop && r <> HandlerLoop ->
            Error(sprintf "%s: requires '%s', which is not an obligation §10.2 or §10.6 names" entry.Name r)
        | r, None when r = HandlerLoop -> Error(entry.Name + ": requires handler-loop but names no hostHandlers")
        | r, Some _ when r = BoundedLoop ->
            Error(
                entry.Name
                + ": names hostHandlers but presumes only the bounded loop, which answers no call"
            )
        | _, Some name when name <> ToyRelabel ->
            Error(sprintf "%s: hostHandlers '%s' is not a name §10.6 registers" entry.Name name)
        | _ ->
            ToyWire.parseWith ToyWire.decodeNode treeJson
            |> Result.mapError (fun e -> entry.Name + ": the tree does not decode: " + e)
            |> Result.bind (fun tree ->
                parseEvents entry.Name eventsJson
                |> Result.bind (fun events ->
                    parseExpectation entry.Name expectationJson
                    |> Result.bind (fun expected ->
                        if List.length expected <> entry.Steps then
                            Error(
                                sprintf
                                    "%s: the manifest declares %d step(s) and the trace carries %d"
                                    entry.Name
                                    entry.Steps
                                    (List.length expected)
                            )
                        else
                            Ok
                                { Name = entry.Name
                                  Requires = entry.Requires
                                  HostHandlers = entry.HostHandlers
                                  Tree = tree
                                  Events = events
                                  Expected = expected })))

// ─── The toy transport ───────────────────────────────────────────────────────

/// What the toy transport makes of an event, before any algebra runs: the
/// event names no node of the tree (the trust boundary REFUSES it), the node
/// carries no handler for it (admitted, nothing to run — not a refusal,
/// §10.5), or the action to run.
type Dispatched =
    | NoSuchNode
    | NoAction
    | Run of ToyAction

let rec private findNode (id: string) (node: ToyNode) : ToyNode option =
    if node.Id = id then
        Some node
    else
        node.Children |> List.tryPick (findNode id)

/// The transport's gate (K8): which action, if any, an event selects.
let dispatch (tree: ToyNode) (event: ToyEvent) : Dispatched =
    match findNode event.NodeId tree with
    | None -> NoSuchNode
    | Some node ->
        match node.Handlers |> List.tryFind (fst >> (=) event.Event) with
        | None -> NoAction
        | Some(_, action) -> Run action

// ─── The client placement ────────────────────────────────────────────────────

/// The per-interaction budget the family presumes: the core's defaults, as
/// every placement's. A scenario over it is refused at the EVENT (§10.5).
let budget: InteractionBudget = InteractionBudget.defaults

/// Drive a scenario through the toy's bounded loop at a placement that runs NO
/// handlers: gate, budget, the shared fold (`BoundedActions.runInert`),
/// re-resolution of the FIXED base tree, and the closure-free effects as the
/// witness emits them. Everything after the gate is the core's; the gate is
/// the toy transport's.
let runClient (scenario: ToyScenario) : ToyStep list =
    let tree = scenario.Tree
    let nodes = Budget.treeCost witness budget.MaxNodes tree

    let resolve (store: ToyStore) =
        if nodes > budget.MaxNodes then
            tree
        else
            Resolve.resolveTree witness store tree

    let initial =
        { Tree = ToyWire.renderNode (resolve Map.empty)
          Effects = []
          Refused = false }

    let steps, _ =
        scenario.Events
        |> List.mapFold
            (fun (store: ToyStore, previous: ToyStep) event ->
                let unchanged refused =
                    { previous with
                        Effects = []
                        Refused = refused },
                    (store, previous)

                match dispatch tree event with
                | NoSuchNode -> unchanged true
                | NoAction -> unchanged false
                | Run action ->
                    if
                        Budget.actionCascadeCost witness action > budget.MaxActions
                        || nodes > budget.MaxNodes
                    then
                        unchanged true
                    else
                        let outcome = BoundedActions.runInert witness event.NodeId action store

                        let step =
                            { Tree = ToyWire.renderNode (resolve outcome.Store)
                              Effects = outcome.Effects |> List.map ToyWire.encodeEffect
                              Refused = false }

                        step, (outcome.Store, step))
            (Map.empty, initial)

    initial :: steps

// ─── The comparison ──────────────────────────────────────────────────────────

/// The first step at which two runs disagree, and on what — §10.3's
/// first-divergence obligation. The tree first, then the effects, then the
/// refusal: a divergence in the tree explains a later one in the effects.
let firstDivergence (legA: string) (a: ToyStep list) (legB: string) (b: ToyStep list) : string option =
    if List.length a <> List.length b then
        Some(sprintf "%s has %d step(s) and %s has %d" legA (List.length a) legB (List.length b))
    else
        List.zip a b
        |> List.indexed
        |> List.tryPick (fun (i, (x, y)) ->
            let differ field va vb =
                Some(
                    sprintf "%s and %s diverge at step %d on %s\n  %s: %s\n  %s: %s" legA legB i field legA va legB vb
                )

            if x.Tree <> y.Tree then
                differ "the resolved tree" x.Tree y.Tree
            elif x.Effects <> y.Effects then
                differ "the effects" (String.concat "," x.Effects) (String.concat "," y.Effects)
            elif x.Refused <> y.Refused then
                differ "the refusal" (string x.Refused) (string y.Refused)
            else
                None)

/// Whether a placement with no handler registry is IN SCOPE for a scenario:
/// only the bounded-loop ones. A handler-loop scenario is out of scope for it,
/// which §10.2 distinguishes from failing it.
let clientInScope (scenario: ToyScenario) : bool = scenario.Requires = BoundedLoop

// ─── Writing a scenario: the resident emitter's half ────────────────────────

/// §2.7's escaping for a string written into a scenario file.
let private quoted (s: string) : string = "\"" + Json.escape s + "\""

/// The event script, in §10.3's layout — one event per line, `nodeId`, `event`,
/// `payload` in that order — and no trailing newline.
let renderEvents (events: ToyEvent list) : string =
    let line (ev: ToyEvent) =
        let payload =
            ev.Payload
            |> List.map (fun (k, v) -> quoted k + ": " + quoted v)
            |> String.concat ", "

        sprintf "  { \"nodeId\": %s, \"event\": %s, \"payload\": {%s} }" (quoted ev.NodeId) (quoted ev.Event) payload

    "[\n" + (events |> List.map line |> String.concat ",\n") + "\n]"

/// A trace, one step per entry: the tree spliced in as a DOCUMENT, the effects
/// as escaped strings of the emitter's bytes, and no `denials` member.
let renderTrace (steps: ToyStep list) : string =
    let entry (s: ToyStep) =
        String.concat
            "\n"
            [ "  {"
              sprintf "    \"tree\": %s," s.Tree
              sprintf "    \"effects\": [%s]," (s.Effects |> List.map quoted |> String.concat ", ")
              sprintf "    \"refused\": %s" (if s.Refused then "true" else "false")
              "  }" ]

    "[\n" + (steps |> List.map entry |> String.concat ",\n") + "\n]"
