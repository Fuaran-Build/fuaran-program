/// A toy domain with its OWN node, action, expression, store, op and effect
/// types, and no UI-tier type anywhere — the second witness of the generic
/// core (Phase 1896). A core that has only ever been instantiated at one
/// domain's types has not been shown to be generic: this is the instantiation
/// that shows it, and the one the proof oracle's generic differential runs
/// through.
module Fuaran.Program.Tests.ToyDomain

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Runtime

/// An expression over the toy store.
type ToyExpr =
    | Const of JVal
    | Read of key: string
    | Fail of message: string
    | Missing

/// The toy's action vocabulary. `Seq`, `Put`, `Ring`, `Need`, `Pick` and
/// `Times` are its control structure; `Beep` and `Hush` are its domain acts.
/// `Ring`'s `onAnswer` is a CLOSURE — the core must never invoke it. `Need` is
/// the halting guard (Phase 1967): its condition resolves against the store,
/// and a guard that does not hold halts the enclosing sequence. `Pick` is the
/// two-arm branch and `Times` the bounded repeat (Phase 1976), viewed as the
/// core's `Choose` and `Repeat`.
type ToyAction =
    | Seq of ToyAction list
    | Put of key: string * value: JVal option * from: ToyExpr option
    | Ring of endpoint: string * targeted: bool * onAnswer: (unit -> unit)
    | Need of condition: ToyExpr
    | Pick of entry: ToyExpr * whenTrue: ToyAction * whenFalse: ToyAction * exit: ToyExpr option
    | Times of bound: Bound<ToyExpr> * body: ToyAction
    | Beep of volume: int
    | Hush

/// The toy's node: an id, a label that may be bound to the store, the actions
/// it carries by event, and its children.
type ToyNode =
    { Id: string
      Label: ToyExpr
      Handlers: (string * ToyAction) list
      Children: ToyNode list }

/// The toy's store: one keyed state channel (K4).
type ToyStore = Map<string, JVal>

/// The toy's op: relabel a node.
type ToyOp = Relabel of id: string * label: string

/// The toy's one effect.
type ToyEffect = Sound of nodeId: string * volume: int

/// The reserved namespace (K5).
[<Literal>]
let ReservedPrefix = "sys."

/// Resolve an expression through a store lookup. Written over the lookup
/// rather than over `ToyStore` so the proof oracle's association-list store
/// resolves it exactly as the core's map does.
let resolveWith (lookup: string -> JVal option) (expr: ToyExpr) : ExprResolution =
    match expr with
    | Const value -> ExprResolution.Resolved value
    | Read key ->
        match lookup key with
        | Some value -> ExprResolution.Resolved value
        | None -> ExprResolution.NotResolved
    | Fail message -> ExprResolution.Errored message
    | Missing -> ExprResolution.NotResolved

/// What a leaf does, through a store lookup. `Beep` above ten is refused, and
/// `Hush` is a documented decline.
let lowerWith (lookup: string -> JVal option) (nodeId: string) (action: ToyAction) : LeafOutcome<ToyEffect> =
    match action with
    | Beep volume when volume > 10 -> LeafOutcome.Refuse(sprintf "volume %d is above the ceiling" volume)
    | Beep volume ->
        // A leaf may READ the store (never write it, K3): a muted store turns
        // every beep into a decline.
        match lookup "muted" with
        | Some(JBool true) -> LeafOutcome.Decline
        | _ -> LeafOutcome.Emit(Sound(nodeId, volume))
    | Hush -> LeafOutcome.Decline
    | Seq _
    | Put _
    | Ring _
    | Need _
    | Pick _
    | Times _ -> LeafOutcome.Decline

let describe (action: ToyAction) : string =
    match action with
    | Seq _ -> "Seq"
    | Put(key, _, _) -> sprintf "Put(%s)" key
    | Ring(endpoint, _, _) -> sprintf "Ring(%s)" endpoint
    | Need _ -> "Need"
    | Pick _ -> "Pick"
    | Times _ -> "Times"
    | Beep volume -> sprintf "Beep(%d)" volume
    | Hush -> "Hush"

let view (action: ToyAction) : ActionView<ToyAction, ToyExpr> =
    match action with
    | Seq actions -> ActionView.Sequence actions
    | Put(key, value, from) -> ActionView.Assign(key, value, from)
    | Ring(endpoint, targeted, _) -> ActionView.Call(endpoint, targeted)
    | Need condition -> ActionView.Require condition
    | Pick(entry, whenTrue, whenFalse, exit) -> ActionView.Choose(entry, whenTrue, whenFalse, exit)
    | Times(bound, body) -> ActionView.Repeat(bound, body)
    | Beep _ ->
        ActionView.Leaf
            { EffectKinds = [ "Sound" ]
              HostCalls = [] }
    | Hush ->
        ActionView.Leaf
            { EffectKinds = []
              HostCalls = [ { Channel = "Hush"; Name = "quiet" } ] }

let rec private encodeAction (action: ToyAction) : JVal =
    match action with
    | Seq actions -> Canon.typed "Seq" [ "ops", JArr(actions |> List.map encodeAction) ]
    | Put(key, _, _) -> Canon.typed "Put" [ "key", JStr key ]
    | Ring(endpoint, targeted, _) -> Canon.typed "Ring" [ "endpoint", JStr endpoint; "targeted", JBool targeted ]
    | Need _ -> Canon.typed "Need" []
    | Pick(_, whenTrue, whenFalse, exit) ->
        Canon.typed
            "Pick"
            [ "exit", JBool(Option.isSome exit)
              "whenFalse", encodeAction whenFalse
              "whenTrue", encodeAction whenTrue ]
    | Times(bound, body) ->
        let encodedBound =
            match bound with
            | Bound.Literal count -> JInt count
            | Bound.Parameter(_, lo, hi) -> JArr [ JInt lo; JInt hi ]

        Canon.typed "Times" [ "body", encodeAction body; "bound", encodedBound ]
    | Beep volume -> Canon.typed "Beep" [ "volume", JInt volume ]
    | Hush -> Canon.typed "Hush" []

let rec private canonical (node: ToyNode) : string =
    Canon.render (
        JObj
            [ "id", JStr node.Id
              "children", JArr(node.Children |> List.map (canonical >> JStr)) ]
    )

let private resolveNode (store: ToyStore) (node: ToyNode) : ToyNode =
    match resolveWith (fun k -> Map.tryFind k store) node.Label with
    | ExprResolution.Resolved value -> { node with Label = Const value }
    | _ -> node

let private relabel (id: string) (label: string) (root: ToyNode) : Result<ToyNode, string> =
    let rec go (n: ToyNode) =
        if n.Id = id then
            { n with Label = Const(JStr label) }
        else
            { n with
                Children = n.Children |> List.map go }

    Ok(go root)

/// The toy witness: all three axes, like the UI tier, at its own types.
let witness: FullWitness<ToyNode, ToyAction, ToyExpr, ToyStore, ToyOp, ToyEffect> =
    { State =
        { Stream =
            { Apply =
                fun op tree ->
                    match op with
                    | Relabel(id, label) -> relabel id label tree
              Encode =
                fun op ->
                    match op with
                    | Relabel(id, label) ->
                        Canon.render (Canon.typed "Relabel" [ "target", JStr id; "label", JStr label ])
              Decode = fun _ -> Error "the toy decodes no op" }
          Diff = fun _ _ -> []
          AbsoluteTarget =
            fun op ->
                match op with
                | Relabel(id, _) -> Some id
          Reach =
            fun op ->
                match op with
                | Relabel(id, _) ->
                    { Arguments = [ "target", id ]
                      Destination = EffectDestination.Absent }
          Canonical = canonical
          View = OpView.edits }
      Walk =
        { Nodes =
            { Id = fun n -> n.Id
              KindTag = fun _ -> "Toy"
              Children = fun n -> n.Children
              ReplaceChildren = fun n kids -> { n with Children = kids } }
          Traverse = fun n -> n.Children
          Cost = fun n -> List.length n.Handlers
          QueryReaders = fun _ -> [] }
      Dispatch =
        { Handlers = fun n -> n.Handlers
          Events = fun n -> n.Handlers |> List.map fst
          Resolve = resolveNode
          Action =
            { View = view
              Lower = fun nodeId action store -> lowerWith (fun k -> Map.tryFind k store) nodeId action
              Describe = describe
              Encode = encodeAction
              Decode =
                fun _ ->
                    Error
                        { Class = "malformed-referenced-value"
                          Detail = "the toy decodes nothing" } }
          Expr =
            { Resolve = fun store expr -> resolveWith (fun k -> Map.tryFind k store) expr
              Uses =
                fun expr ->
                    match expr with
                    | Read key -> [ BindingUse.State key ]
                    | _ -> [] }
          Store =
            { Assign = Map.add
              Read = Map.tryFind
              LandQuery = fun slot table store -> Map.add slot (JInt(List.length table.Columns)) store
              IsReserved = fun key -> key.StartsWith ReservedPrefix
              ReservedPrefix = ReservedPrefix }
          Effect =
            { Kind = fun _ -> "Sound"
              Destination = fun _ -> EffectDestination.Absent
              Encode =
                fun effect ->
                    match effect with
                    | Sound(nodeId, volume) ->
                        sprintf "{\"kind\":\"Sound\",\"nodeId\":\"%s\",\"volume\":%d}" nodeId volume
              Decode =
                fun _ ->
                    Error
                        { Class = "unknown-effect-arm"
                          Detail = "the toy decodes no effect" } } } }
