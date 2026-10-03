/// The toy witness's WIRE FORM (fuaran#2011): its tree, action, expression,
/// op, state and effect as documents, in the specification's own canonical
/// discipline — `PROGRAM_WIRE.md` §10.6 is the normative text, and
/// `schemas/v2/toy-*.schema.json` are its schemas.
///
/// Written beside `ToyDomain.fs` rather than inside the witness, and on
/// purpose: the witness's own `Encode` members are the shapes the core HASHES
/// (an action's canonical form names only what the replay classification
/// reads), and widening them would move every digest the proof hosts and the
/// genericity suite already pin. This module is the scenario family's codec —
/// the fifth artefact of §11.1's forward coupling for the toy subject — and the
/// witness stays exactly as the core sees it.
///
/// Fable-clean and IO-free: the Fable parity leg compiles it as source and runs
/// the SAME decoder over the same scenario files. It names no UI-tier type,
/// which the boundary test in `CoreBoundaryTests.fs` checks.
module Fuaran.Program.Tests.ToyWire

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Tests.ToyDomain

// ─── Encoding ────────────────────────────────────────────────────────────────

/// An expression, as a `$type`-discriminated object.
let encodeExpr (expr: ToyExpr) : JVal =
    match expr with
    | Const value -> Canon.typed "Const" [ "value", value ]
    | Read key -> Canon.typed "Read" [ "key", JStr key ]
    | Fail message -> Canon.typed "Fail" [ "message", JStr message ]
    | Missing -> Canon.typed "Missing" []
    | Hole placeholder -> Canon.typed "Hole" [ "placeholder", JStr placeholder ]

/// A repeat's bound: a literal count is a bare integer, a parameter bound an
/// object naming its expression and its declared range.
let encodeBound (bound: Bound<ToyExpr>) : JVal =
    match bound with
    | Bound.Literal count -> JInt count
    | Bound.Parameter(count, lo, hi) ->
        Canon.typed "Parameter" [ "count", encodeExpr count; "hi", JInt hi; "lo", JInt lo ]

/// An action. A `Ring`'s answer closure has no wire form and is not written:
/// the wire carries the endpoint and whether the call declares a target, and
/// nothing a host could recover behaviour from (§10.5).
let rec encodeAction (action: ToyAction) : JVal =
    match action with
    | Seq actions -> Canon.typed "Seq" [ "actions", JArr(actions |> List.map encodeAction) ]
    | Put(key, value, from) ->
        Canon.typed
            "Put"
            ([ "key", JStr key ]
             @ (value |> Option.map (fun v -> "value", v) |> Option.toList)
             @ (from |> Option.map (fun e -> "from", encodeExpr e) |> Option.toList))
    | Ring(endpoint, targeted, _) -> Canon.typed "Ring" [ "endpoint", JStr endpoint; "targeted", JBool targeted ]
    | Need condition -> Canon.typed "Need" [ "condition", encodeExpr condition ]
    | Pick(entry, whenTrue, whenFalse, exit) ->
        Canon.typed
            "Pick"
            ([ "entry", encodeExpr entry
               "whenFalse", encodeAction whenFalse
               "whenTrue", encodeAction whenTrue ]
             @ (exit |> Option.map (fun e -> "exit", encodeExpr e) |> Option.toList))
    | Times(bound, body) -> Canon.typed "Times" [ "body", encodeAction body; "bound", encodeBound bound ]
    | ForEach(collection, placeholder, body) ->
        Canon.typed
            "ForEach"
            [ "body", encodeAction body
              "collection", JArr collection
              "placeholder", JStr placeholder ]
    | Beep volume -> Canon.typed "Beep" [ "volume", JInt volume ]
    | Hush -> Canon.typed "Hush" []

/// A node. All four members are always present — an empty `children` or
/// `handlers` renders `[]` — so a fact has one spelling.
let rec encodeNode (node: ToyNode) : JVal =
    JObj
        [ "children", JArr(node.Children |> List.map encodeNode)
          "handlers",
          JArr(
              node.Handlers
              |> List.map (fun (event, action) -> JObj [ "action", encodeAction action; "event", JStr event ])
          )
          "id", JStr node.Id
          "label", encodeExpr node.Label ]

/// A tree document's canonical bytes: §2's discipline, members Ordinal.
let renderNode (node: ToyNode) : string = Canon.render (encodeNode node)

/// An op. The same bytes the witness's own stream encoder writes, which the
/// codec test pins, so an op this module encodes and one the core journals
/// cannot be two spellings.
let encodeOp (op: ToyOp) : JVal =
    match op with
    | Relabel(target, label) -> Canon.typed "Relabel" [ "label", JStr label; "target", JStr target ]

/// The store: one object, a member per key.
let encodeState (store: ToyStore) : JVal = JObj(Map.toList store)

/// An effect, through the witness's SHIPPED emitter — the bytes a step trace
/// records (§10.6 states them). Never re-rendered here.
let encodeEffect (effect: ToyEffect) : string = witness.Dispatch.Effect.Encode effect

// ─── Decoding ────────────────────────────────────────────────────────────────

type private R<'T> = Result<'T, WireRefusal>

let private refuse (cls: string) (detail: string) : R<'T> = ProgramWire.refuse cls detail

/// A toy identifier — a node id, which the effect emitter writes into its
/// bytes unescaped — is non-empty and drawn from letters, digits, `.`, `_` and
/// `-`. Refused otherwise, so the emitter's bytes are always a JSON document.
let isIdentifier (text: string) : bool =
    text.Length > 0
    && text
       |> Seq.forall (fun c ->
           (c >= 'a' && c <= 'z')
           || (c >= 'A' && c <= 'Z')
           || (c >= '0' && c <= '9')
           || c = '.'
           || c = '_'
           || c = '-')

let private nonEmpty (name: string) (value: JVal) : R<string> =
    ProgramWire.requireString name value
    |> Result.bind (fun s ->
        if s.Length = 0 then
            refuse RefusalClass.MissingMember ("member '" + name + "' is empty")
        else
            Ok s)

let private requireInt (name: string) (value: JVal) : R<int> =
    ProgramWire.requireMember name value
    |> Result.bind (fun v ->
        match v with
        | JInt n -> Ok n
        | _ -> refuse RefusalClass.MissingMember ("member '" + name + "' is not an integer"))

let private requireCount (name: string) (value: JVal) : R<int> =
    requireInt name value
    |> Result.bind (fun n ->
        if n < 0 then
            refuse RefusalClass.MissingMember ("member '" + name + "' is negative")
        else
            Ok n)

let private optional (name: string) (decode: JVal -> R<'T>) (value: JVal) : R<'T option> =
    match ProgramWire.tryMember name value with
    | None -> Ok None
    | Some v -> decode v |> Result.map Some

let private typed (what: string) (value: JVal) : R<string> =
    match ProgramWire.tag value with
    | Some t -> Ok t
    | None -> refuse RefusalClass.MissingMember (what + ": no '$type'")

let private unknown (what: string) (tag: string) : R<'T> =
    refuse RefusalClass.MalformedReferencedValue (what + ": '" + tag + "' is not a case of the toy vocabulary")

let decodeExpr (value: JVal) : R<ToyExpr> =
    typed "expression" value
    |> Result.bind (fun tag ->
        match tag with
        | "Const" ->
            ProgramWire.declaredOnly [ "$type"; "value" ] value
            |> Result.bind (fun () -> ProgramWire.requireMember "value" value)
            |> Result.map Const
        | "Read" ->
            ProgramWire.declaredOnly [ "$type"; "key" ] value
            |> Result.bind (fun () -> nonEmpty "key" value)
            |> Result.map Read
        | "Fail" ->
            ProgramWire.declaredOnly [ "$type"; "message" ] value
            |> Result.bind (fun () -> ProgramWire.requireString "message" value)
            |> Result.map Fail
        | "Missing" -> ProgramWire.declaredOnly [ "$type" ] value |> Result.map (fun () -> Missing)
        | "Hole" ->
            ProgramWire.declaredOnly [ "$type"; "placeholder" ] value
            |> Result.bind (fun () -> nonEmpty "placeholder" value)
            |> Result.map Hole
        | other -> unknown "expression" other)

let decodeBound (value: JVal) : R<Bound<ToyExpr>> =
    match value with
    | JInt n when n >= 0 -> Ok(Bound.Literal n)
    | JInt _ -> refuse RefusalClass.MissingMember "a literal bound is negative"
    | _ ->
        typed "bound" value
        |> Result.bind (fun tag ->
            match tag with
            | "Parameter" ->
                ProgramWire.declaredOnly [ "$type"; "count"; "hi"; "lo" ] value
                |> Result.bind (fun () -> ProgramWire.requireMember "count" value |> Result.bind decodeExpr)
                |> Result.bind (fun count ->
                    requireCount "lo" value
                    |> Result.bind (fun lo ->
                        requireCount "hi" value
                        |> Result.bind (fun hi ->
                            if lo > hi then
                                refuse RefusalClass.MissingMember "a parameter bound's 'lo' exceeds its 'hi'"
                            else
                                Ok(Bound.Parameter(count, lo, hi)))))
            | other -> unknown "bound" other)

/// The closure a decoded `Ring` carries. The core never invokes a carried
/// closure (K1), so this one FAILS rather than doing nothing: a fold that
/// recovered behaviour from the slot would be caught here rather than reading
/// as an inert step.
let private noAnswer () : unit =
    failwith "a decoded Ring's answer slot was invoked; the wire carries no closure"

let rec decodeAction (value: JVal) : R<ToyAction> =
    typed "action" value
    |> Result.bind (fun tag ->
        let only members =
            ProgramWire.declaredOnly ("$type" :: members) value

        match tag with
        | "Seq" ->
            only [ "actions" ]
            |> Result.bind (fun () -> ProgramWire.requireArray "actions" value)
            |> Result.bind (ProgramWire.traverse decodeAction)
            |> Result.map Seq
        | "Put" ->
            only [ "from"; "key"; "value" ]
            |> Result.bind (fun () -> nonEmpty "key" value)
            |> Result.bind (fun key ->
                optional "value" Ok value
                |> Result.bind (fun literal ->
                    optional "from" decodeExpr value
                    |> Result.map (fun from -> Put(key, literal, from))))
        | "Ring" ->
            only [ "endpoint"; "targeted" ]
            |> Result.bind (fun () -> nonEmpty "endpoint" value)
            |> Result.bind (fun endpoint ->
                ProgramWire.requireBool "targeted" value
                |> Result.map (fun targeted -> Ring(endpoint, targeted, noAnswer)))
        | "Need" ->
            only [ "condition" ]
            |> Result.bind (fun () -> ProgramWire.requireMember "condition" value |> Result.bind decodeExpr)
            |> Result.map Need
        | "Pick" ->
            only [ "entry"; "exit"; "whenFalse"; "whenTrue" ]
            |> Result.bind (fun () -> ProgramWire.requireMember "entry" value |> Result.bind decodeExpr)
            |> Result.bind (fun entry ->
                ProgramWire.requireMember "whenTrue" value
                |> Result.bind decodeAction
                |> Result.bind (fun whenTrue ->
                    ProgramWire.requireMember "whenFalse" value
                    |> Result.bind decodeAction
                    |> Result.bind (fun whenFalse ->
                        optional "exit" decodeExpr value
                        |> Result.map (fun exit -> Pick(entry, whenTrue, whenFalse, exit)))))
        | "Times" ->
            only [ "body"; "bound" ]
            |> Result.bind (fun () -> ProgramWire.requireMember "bound" value |> Result.bind decodeBound)
            |> Result.bind (fun bound ->
                ProgramWire.requireMember "body" value
                |> Result.bind decodeAction
                |> Result.map (fun body -> Times(bound, body)))
        | "ForEach" ->
            only [ "body"; "collection"; "placeholder" ]
            |> Result.bind (fun () -> ProgramWire.requireArray "collection" value)
            |> Result.bind (fun collection ->
                nonEmpty "placeholder" value
                |> Result.bind (fun placeholder ->
                    ProgramWire.requireMember "body" value
                    |> Result.bind decodeAction
                    |> Result.map (fun body -> ForEach(collection, placeholder, body))))
        | "Beep" ->
            only [ "volume" ]
            |> Result.bind (fun () -> requireCount "volume" value)
            |> Result.map Beep
        | "Hush" -> only [] |> Result.map (fun () -> Hush)
        | other -> unknown "action" other)

let rec decodeNode (value: JVal) : R<ToyNode> =
    ProgramWire.declaredOnly [ "children"; "handlers"; "id"; "label" ] value
    |> Result.bind (fun () -> ProgramWire.requireString "id" value)
    |> Result.bind (fun id ->
        if not (isIdentifier id) then
            refuse RefusalClass.MissingMember "a node id is empty or not an identifier"
        else
            ProgramWire.requireMember "label" value
            |> Result.bind decodeExpr
            |> Result.bind (fun label ->
                ProgramWire.requireArray "handlers" value
                |> Result.bind (
                    ProgramWire.traverse (fun h ->
                        ProgramWire.declaredOnly [ "action"; "event" ] h
                        |> Result.bind (fun () -> nonEmpty "event" h)
                        |> Result.bind (fun event ->
                            ProgramWire.requireMember "action" h
                            |> Result.bind decodeAction
                            |> Result.map (fun action -> event, action)))
                )
                |> Result.bind (fun handlers ->
                    ProgramWire.requireArray "children" value
                    |> Result.bind (ProgramWire.traverse decodeNode)
                    |> Result.map (fun children ->
                        { Id = id
                          Label = label
                          Handlers = handlers
                          Children = children }))))

let decodeOp (value: JVal) : R<ToyOp> =
    match ProgramWire.tag value with
    | Some "Relabel" ->
        ProgramWire.declaredOnly [ "$type"; "label"; "target" ] value
        |> Result.bind (fun () -> ProgramWire.requireString "target" value)
        |> Result.bind (fun target ->
            if not (isIdentifier target) then
                refuse RefusalClass.MissingMember "an op's target is not an identifier"
            else
                ProgramWire.requireString "label" value
                |> Result.map (fun label -> Relabel(target, label)))
    | Some other -> unknown "op" other
    | None -> refuse RefusalClass.MissingMember "op: no '$type'"

/// The store. A repeated key is refused rather than resolved by position: two
/// readers that kept different occurrences would hold different stores.
let decodeState (value: JVal) : R<ToyStore> =
    match value with
    | JObj members ->
        let keys = members |> List.map fst

        if List.length (List.distinct keys) <> List.length keys then
            refuse RefusalClass.MissingMember "the state repeats a key"
        elif keys |> List.exists (fun k -> k.Length = 0) then
            refuse RefusalClass.MissingMember "the state carries an empty key"
        else
            Ok(Map.ofList members)
    | _ -> refuse RefusalClass.MissingMember "the state is not an object"

/// An effect, read back from the bytes a trace records. The members are read
/// IN THE ORDER the emitter writes them — `kind`, `nodeId`, `volume` — and a
/// document spelling them otherwise is refused: the order is part of the
/// envelope §10.6 adopts, and a reader that accepted any order could not tell a
/// host that re-rendered the effect from one that emitted it.
let decodeEffect (bytes: string) : R<ToyEffect> =
    ProgramWire.parseDocument bytes
    |> Result.bind (fun value ->
        match value with
        | JObj [ "kind", JStr "Sound"; "nodeId", JStr nodeId; "volume", JInt volume ] when
            isIdentifier nodeId && volume >= 0
            ->
            Ok(Sound(nodeId, volume))
        | JObj(("kind", JStr kind) :: _) when kind <> "Sound" ->
            refuse RefusalClass.UnknownEffectArm "the toy's effect vocabulary is { Sound }"
        | _ -> refuse RefusalClass.MissingMember "a Sound effect spells exactly kind, nodeId, volume, in that order")

/// Parse a document and decode it.
let parseWith (decode: JVal -> R<'T>) (json: string) : Result<'T, string> =
    ProgramWire.parseDocument json
    |> Result.bind decode
    |> Result.mapError (fun r -> r.Class + ": " + r.Detail)
