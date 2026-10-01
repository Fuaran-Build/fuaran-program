/// A SECOND witness of the generic core, written for Phase 1967: a domain
/// whose handler is a store-mutating VERB — read a file map, write and delete
/// files, publish to a named target — rather than a UI event handler. No UI
/// type anywhere, like the toy beside it; unlike the toy, its ops reach the
/// world, its guards are typed refusals, and its placement performs the plan
/// after the handler commits. It reproduces, in miniature, what the first
/// second-domain instantiation found and had to build beside Program (the
/// four findings `DECISIONS.md` D19 records), so each is a test here rather
/// than a report elsewhere.
module Fuaran.Program.Tests.VerbDomain

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Runtime

// ─── the typed refusal, and how it crosses (W5) ─────────────────────────────

/// A refusal the verb's guards raise, as the domain types it.
type VerbRefusal = { Code: string; Detail: string }

/// The op channel's and the guard's rejection stay `string` (D19): a typed
/// refusal crosses as its canonical JSON and is parsed back on the far side,
/// which is the one answer the decision names.
module VerbRefusal =
    let render (r: VerbRefusal) : string =
        Canon.render (JObj [ "code", JStr r.Code; "detail", JStr r.Detail ])

    let parse (text: string) : VerbRefusal option =
        match Json.parse text with
        | Ok(JObj members) ->
            match
                List.tryFind (fun (k, _) -> k = "code") members, List.tryFind (fun (k, _) -> k = "detail") members
            with
            | Some(_, JStr code), Some(_, JStr detail) -> Some { Code = code; Detail = detail }
            | _ -> None
        | _ -> None

// ─── the vocabulary ─────────────────────────────────────────────────────────

/// An expression over the verb's ARGUMENTS — the state channel is where a
/// verb's arguments live, and a guard reads them there (K4).
type VerbExpr =
    | Lit of JVal
    | Arg of key: string
    /// True exactly when the argument is a non-empty string; unresolved when
    /// it is absent.
    | NonEmpty of key: string
    /// A typed refusal, carried as the guard's errored text.
    | Refusing of VerbRefusal

/// The verb's actions. `Steps`, `Set` and `Need` are control structure the
/// core owns — `Need` is the halting guard; `Say` is its one leaf, and an
/// empty `Say` is REFUSED without halting, so the two kinds of refusal can be
/// told apart in one sequence.
type VerbAction =
    | Steps of VerbAction list
    | Set of key: string * value: JVal option * from: VerbExpr option
    | Need of condition: VerbExpr
    | Say of string

/// The verb's tree: an in-memory file map and what has been published. This
/// is the PLAN the handler edits; the world is the performer's.
type FileMap =
    { Files: Map<string, string>
      Published: string list }

/// The state channel: the verb's arguments.
type VerbStore = Map<string, JVal>

/// The verb's ops. A read, a write, a delete and a publish — the four shapes
/// a store-mutating verb has.
type FileOp =
    | Read of path: string
    | Write of path: string * content: string
    | Delete of path: string
    | Publish of target: string

type VerbEffect = Said of string

[<Literal>]
let ReservedPrefix = "host."

// ─── the witness ────────────────────────────────────────────────────────────

let private lookup (store: VerbStore) (key: string) = Map.tryFind key store

let resolve (store: VerbStore) (expr: VerbExpr) : ExprResolution =
    match expr with
    | Lit value -> ExprResolution.Resolved value
    | Arg key ->
        match lookup store key with
        | Some value -> ExprResolution.Resolved value
        | None -> ExprResolution.NotResolved
    | NonEmpty key ->
        match lookup store key with
        | Some(JStr text) -> ExprResolution.Resolved(JBool(text <> ""))
        | Some _ -> ExprResolution.Resolved(JBool false)
        | None -> ExprResolution.NotResolved
    | Refusing refusal -> ExprResolution.Errored(VerbRefusal.render refusal)

let describe (action: VerbAction) : string =
    match action with
    | Steps _ -> "Steps"
    | Set(key, _, _) -> sprintf "Set(%s)" key
    | Need _ -> "Need"
    | Say _ -> "Say"

let view (action: VerbAction) : ActionView<VerbAction, VerbExpr> =
    match action with
    | Steps actions -> ActionView.Sequence actions
    | Set(key, value, from) -> ActionView.Assign(key, value, from)
    | Need condition -> ActionView.Require condition
    | Say _ ->
        ActionView.Leaf
            { EffectKinds = [ "Said" ]
              HostCalls = [] }

let lower (_: string) (action: VerbAction) (_: VerbStore) : LeafOutcome<VerbEffect> =
    match action with
    | Say "" -> LeafOutcome.Refuse "nothing to say"
    | Say text -> LeafOutcome.Emit(Said text)
    | Steps _
    | Set _
    | Need _ -> LeafOutcome.Decline

let rec private encodeAction (action: VerbAction) : JVal =
    match action with
    | Steps actions -> Canon.typed "Steps" [ "ops", JArr(actions |> List.map encodeAction) ]
    | Set(key, _, _) -> Canon.typed "Set" [ "key", JStr key ]
    | Need _ -> Canon.typed "Need" []
    | Say text -> Canon.typed "Say" [ "text", JStr text ]

/// The op codec: canonical, and the content a write carries is IN it, so a
/// ceiling on the arm measures what the handler document would carry.
let encodeOp (op: FileOp) : string =
    match op with
    | Read path -> Canon.render (Canon.typed "Read" [ "path", JStr path ])
    | Write(path, content) -> Canon.render (Canon.typed "Write" [ "content", JStr content; "path", JStr path ])
    | Delete path -> Canon.render (Canon.typed "Delete" [ "path", JStr path ])
    | Publish target -> Canon.render (Canon.typed "Publish" [ "target", JStr target ])

/// Apply one op to the PLAN. A read reads the plan; a delete of a file the
/// plan does not hold is an apply refusal, which halts the handler as every
/// apply refusal does.
let apply (op: FileOp) (tree: FileMap) : Result<FileMap, string> =
    match op with
    | Read path ->
        if Map.containsKey path tree.Files then
            Ok tree
        else
            Error(sprintf "no such file: %s" path)
    | Write(path, content) ->
        Ok
            { tree with
                Files = Map.add path content tree.Files }
    | Delete path ->
        if Map.containsKey path tree.Files then
            Ok
                { tree with
                    Files = Map.remove path tree.Files }
        else
            Error(sprintf "no such file: %s" path)
    | Publish target ->
        Ok
            { tree with
                Published = tree.Published @ [ target ] }

/// What an op REACHES (W3, W4): a path under `path`, local; a publish target
/// under `target`, and REMOTE — the class a policy can bound without knowing
/// how this domain names its targets.
let reach (op: FileOp) : OpReach =
    match op with
    | Read path
    | Write(path, _)
    | Delete path ->
        { Arguments = [ "path", path ]
          Destination = EffectDestination.Local }
    | Publish target ->
        { Arguments = [ "target", target ]
          Destination = EffectDestination.Remote target }

let private canonical (tree: FileMap) : string =
    Canon.render (
        JObj
            [ "files", JObj(tree.Files |> Map.toList |> List.map (fun (path, content) -> path, JStr content))
              "published", JArr(tree.Published |> List.map JStr) ]
    )

/// The verb witness. The tree half is as vacuous as a verb makes it — one
/// node, no children, no handlers — and that vacuity is the finding, not a
/// defect: a verb is not reached from a node tree.
let witness: ProgramWitness<FileMap, VerbAction, VerbExpr, VerbStore, FileOp, VerbEffect> =
    { Tree =
        { Nodes =
            { Id = fun _ -> "store"
              KindTag = fun _ -> "FileMap"
              Children = fun _ -> []
              ReplaceChildren = fun tree _ -> tree }
          Traverse = fun _ -> []
          Handlers = fun _ -> []
          Events = fun _ -> []
          Resolve = fun _ tree -> tree
          Cost = fun _ -> 0
          QueryReaders = fun _ -> []
          Canonical = canonical }
      Action =
        { View = view
          Lower = lower
          Describe = describe
          Encode = encodeAction
          Decode =
            fun _ ->
                Error
                    { Class = "malformed-referenced-value"
                      Detail = "the verb decodes nothing" } }
      Expr =
        { Resolve = resolve
          Uses =
            fun expr ->
                match expr with
                | Arg key
                | NonEmpty key -> [ BindingUse.State key ]
                | Lit _
                | Refusing _ -> [] }
      Store =
        { Assign = Map.add
          LandQuery = fun slot table store -> Map.add slot (JInt(List.length table.Columns)) store
          IsReserved = fun key -> key.StartsWith ReservedPrefix
          ReservedPrefix = ReservedPrefix }
      Op =
        { Stream =
            { Apply = apply
              Encode = encodeOp
              Decode = fun _ -> Error "the verb decodes no op" }
          Diff = fun _ _ -> []
          AbsoluteTarget =
            fun op ->
                match op with
                | Read path
                | Write(path, _)
                | Delete path -> Some path
                | Publish target -> Some target
          Reach = reach }
      Effect =
        { Kind = fun _ -> "Said"
          Destination = fun _ -> EffectDestination.Absent
          Encode =
            fun effect ->
                match effect with
                | Said text -> Canon.render (JObj [ "kind", JStr "Said"; "text", JStr text ])
          Decode =
            fun _ ->
                Error
                    { Class = "unknown-effect-arm"
                      Detail = "the verb decodes no effect" } } }

// ─── the world, and the performer over it ───────────────────────────────────

/// The WORLD the verb's performer acts on — what the plan is performed
/// against once the handler commits. Mutable on purpose: a performer is an
/// effect, and the tests read the world to see what actually happened.
type World() =
    let files = System.Collections.Generic.Dictionary<string, string>()
    let published = System.Collections.Generic.List<string>()
    let invocations = System.Collections.Generic.List<string>()

    member _.Files = files |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
    member _.Published = List.ofSeq published
    /// Every op the performer was asked to perform, in order — the ground
    /// truth `Performed` is checked against.
    member _.Invocations = List.ofSeq invocations

    member _.Seed(path: string, content: string) = files.[path] <- content

    /// The performer: performs each op against the world, refusing at ONE
    /// position (zero-based, counted over its own invocations) or never.
    member _.Performer(failAt: int option) : FileOp -> Result<unit, string> =
        fun op ->
            let position = invocations.Count
            invocations.Add(encodeOp op)

            match failAt with
            | Some k when k = position -> Error(sprintf "the world refused op %d" k)
            | _ ->
                match op with
                | Read _ -> Ok()
                | Write(path, content) ->
                    files.[path] <- content
                    Ok()
                | Delete path ->
                    files.Remove path |> ignore
                    Ok()
                | Publish target ->
                    published.Add target
                    Ok()
