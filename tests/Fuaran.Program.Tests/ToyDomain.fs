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

/// An expression over the toy store. `Hole` reads a PLACEHOLDER by name
/// (Phase 1990): inside the body of a `ForEach` that binds the name it stands
/// for the current element, which the toy's `Substitute` writes over it as a
/// `Const`; resolved unsubstituted it is a typed error naming the placeholder
/// — the run-time fallback the scope check exists to make unreachable.
type ToyExpr =
    | Const of JVal
    | Read of key: string
    | Fail of message: string
    | Missing
    | Hole of placeholder: string

/// The toy's action vocabulary. `Seq`, `Put`, `Ring`, `Need`, `Pick`, `Times`
/// and `ForEach` are its control structure; `Beep` and `Hush` are its domain
/// acts. `Ring`'s `onAnswer` is a CLOSURE — the core must never invoke it.
/// `Need` is the halting guard (Phase 1967): its condition resolves against
/// the store, and a guard that does not hold halts the enclosing sequence.
/// `Pick` is the two-arm branch and `Times` the bounded repeat (Phase 1976),
/// viewed as the core's `Choose` and `Repeat`. `ForEach` is per-element
/// iteration over a literal collection (Phase 1990), viewed as the core's
/// `Each`: its body reads the element through `Hole`.
type ToyAction =
    | Seq of ToyAction list
    | Put of key: string * value: JVal option * from: ToyExpr option
    | Ring of endpoint: string * targeted: bool * onAnswer: (unit -> unit)
    | Need of condition: ToyExpr
    | Pick of entry: ToyExpr * whenTrue: ToyAction * whenFalse: ToyAction * exit: ToyExpr option
    | Times of bound: Bound<ToyExpr> * body: ToyAction
    | ForEach of collection: JVal list * placeholder: string * body: ToyAction
    /// Per-element iteration over a collection the BINDING STORE holds (Phase
    /// 1991): `source` resolves at entry to the elements, under `ceiling`.
    | ForEachOf of source: ToyExpr * ceiling: int * placeholder: string * body: ToyAction
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
    | Hole placeholder -> ExprResolution.Errored(sprintf "placeholder '%s' was not substituted" placeholder)

/// The placeholders an expression reads: a `Hole`'s name, nothing else.
let holesOf (expr: ToyExpr) : string list =
    match expr with
    | Hole placeholder -> [ placeholder ]
    | Const _
    | Read _
    | Fail _
    | Missing -> []

/// Substitute a value for a placeholder in an expression: the `Hole` of that
/// name becomes the value, as a literal; every other expression is unchanged.
let substituteExpr (placeholder: string) (element: JVal) (expr: ToyExpr) : ToyExpr =
    match expr with
    | Hole name when name = placeholder -> Const element
    | other -> other

/// The toy's `Substitute` (Phase 1990): the value written over the placeholder
/// in every expression of the action and of the actions beneath it — the
/// shape of the action untouched, so the substituted action views as the
/// original does (the witness obligation the differential host checks). A
/// nested `ForEach` keeps its own collection and placeholder; its body is
/// substituted like any other, because the scope check has already refused a
/// body that rebinds an enclosing name.
let rec substitute (placeholder: string) (element: JVal) (action: ToyAction) : ToyAction =
    let expr = substituteExpr placeholder element
    let sub = substitute placeholder element

    match action with
    | Seq actions -> Seq(actions |> List.map sub)
    | Put(key, value, from) -> Put(key, value, from |> Option.map expr)
    | Ring _ -> action
    | Need condition -> Need(expr condition)
    | Pick(entry, whenTrue, whenFalse, exit) -> Pick(expr entry, sub whenTrue, sub whenFalse, exit |> Option.map expr)
    | Times(Bound.Parameter(count, lo, hi), body) -> Times(Bound.Parameter(expr count, lo, hi), sub body)
    | Times(Bound.Literal count, body) -> Times(Bound.Literal count, sub body)
    | ForEach(collection, name, body) -> ForEach(collection, name, sub body)
    | ForEachOf(source, ceiling, name, body) -> ForEachOf(expr source, ceiling, name, sub body)
    | Beep _
    | Hush -> action

/// The placeholders an action's OWN operands read (Phase 1990): the
/// expressions this level holds — a write's source, a guard's condition, a
/// branch's entry and exit, a parameter bound — and not those of the actions
/// beneath it, which the core reaches through the view.
let placeholders (action: ToyAction) : string list =
    match action with
    | Put(_, _, Some from) -> holesOf from
    | Need condition -> holesOf condition
    | Pick(entry, _, _, exit) -> holesOf entry @ (exit |> Option.toList |> List.collect holesOf)
    | Times(Bound.Parameter(count, _, _), _) -> holesOf count
    | ForEachOf(source, _, _, _) -> holesOf source
    | Put(_, _, None)
    | Times(Bound.Literal _, _)
    | ForEach _
    | Seq _
    | Ring _
    | Beep _
    | Hush -> []

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
    | Times _
    | ForEach _
    | ForEachOf _ -> LeafOutcome.Decline

let describe (action: ToyAction) : string =
    match action with
    | Seq _ -> "Seq"
    | Put(key, _, _) -> sprintf "Put(%s)" key
    | Ring(endpoint, _, _) -> sprintf "Ring(%s)" endpoint
    | Need _ -> "Need"
    | Pick _ -> "Pick"
    | Times _ -> "Times"
    | ForEach(_, placeholder, _) -> sprintf "ForEach(%s)" placeholder
    | ForEachOf(_, ceiling, placeholder, _) -> sprintf "ForEachOf(%s, at most %d)" placeholder ceiling
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
    | ForEach(collection, placeholder, body) -> ActionView.Each(Collection.Literal collection, placeholder, body)
    | ForEachOf(source, ceiling, placeholder, body) ->
        ActionView.Each(Collection.Stored(source, ceiling), placeholder, body)
    | Beep _ ->
        ActionView.Leaf
            { LeafDeclaration.none with
                EffectKinds = [ "Sound" ] }
    | Hush ->
        ActionView.Leaf
            { LeafDeclaration.none with
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
    | ForEach(collection, placeholder, body) ->
        Canon.typed
            "ForEach"
            [ "body", encodeAction body
              "collection", JArr collection
              "placeholder", JStr placeholder ]
    | ForEachOf(source, ceiling, placeholder, body) ->
        Canon.typed
            "ForEachOf"
            [ "body", encodeAction body
              "ceiling", JInt ceiling
              "placeholder", JStr placeholder
              // The source as a parameter bound is encoded: by what it reads.
              "source",
              (match source with
               | Read key -> JStr key
               | _ -> JStr "") ]
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

/// The literal label a node holds, if it is a node of this tree with a literal
/// label — what a relabel's inverse restores. A label bound to the store has no
/// literal to restore, so the inverse answers nothing there, and the undo run's
/// drift check refuses the plan rather than restoring it wrong (Phase 1977).
let private labelOf (id: string) (root: ToyNode) : string option =
    let rec go (n: ToyNode) =
        if n.Id = id then
            match n.Label with
            | Const(JStr label) -> Some label
            | _ -> None
        else
            n.Children |> List.tryPick go

    go root

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
          View = OpView.edits
          // The toy's one op is never viewed as an `Each` (Phase 1990): the
          // identity, and no placeholder.
          Substitute = fun _ _ op -> op
          Placeholders = fun _ -> []
          // A relabel's exact inverse (Phase 1977): relabel back to the label
          // the pre-state held, read off the node the op addresses.
          Undo =
            fun op ->
                match op with
                | Relabel(id, _) ->
                    UndoClass.Inverse(fun pre ->
                        match labelOf id pre with
                        | Some label -> [ Relabel(id, label) ]
                        | None -> []) }
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
                          Detail = "the toy decodes nothing" }
              Substitute = substitute
              Placeholders = placeholders }
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
