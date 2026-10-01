/// The THIRD witness of the generic core, written for Phase 1974: a document
/// pipeline under a server placement — a domain whose state IS its tree. It
/// restates, in miniature and with no package beyond this repository's core,
/// the shape the third instantiation measured: a typed tree whose bound values
/// live INSIDE the document (its context), node ids that are typed and have a
/// faithful string form (K1), an op algebra whose rejection is typed and
/// crosses the op channel as canonical JSON (W5), no events and no handlers on
/// any node, a pack-style guard, and a tail that renders the document and
/// commits the ops.
///
/// It fills the STATE axis and the WALK axis, and no dispatch axis:
/// `ProgramWitness<Document, DocStep, WalkWitness<Document>, Unfilled>`. The
/// walk is real — the tree has children the budget can price — and nothing
/// is dispatched from a node. Its guard is an op (`RequirePack`), which the
/// state witness views as a guard, so it reads the document AS PLANNED; its
/// performer is handed that document, so the tail renders and commits what
/// the plan produced without folding the ops a second time (`DECISIONS.md`
/// D20, F-GUARD and F-PERFORM).
module Fuaran.Program.Tests.DocDomain

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Runtime

// ─── ids: typed, with a faithful string form (K1) ───────────────────────────

/// A block's id. Typed, so a string is not a block id by accident.
type BlockId = BlockId of string

module BlockId =
    let value (BlockId id) = id
    let ofString (id: string) = BlockId id

// ─── the document: a tree, and the bound values inside it ───────────────────

/// One block: an id, a kind, its text, and its children. A block's text may
/// name a bound field as `{{field}}`, resolved from the document's own
/// context at render time.
type Block =
    { Id: BlockId
      Kind: string
      Text: string
      Children: Block list }

/// The document — the state the ops apply to. Its bound values are IN it
/// (`Context`), not in any store beside it: the state is the tree.
type Document =
    { Root: Block
      Context: Map<string, string> }

// ─── the typed rejection, and how it crosses (W5) ───────────────────────────

/// Why an op was refused, as the domain types it.
[<RequireQualifiedAccess>]
type DocRejection =
    | UnknownBlock of BlockId
    | UnknownPack of pack: string
    /// The pack's defects: `blockId:term`, in document order.
    | PackFailed of defects: string list

module DocRejection =

    /// The canonical JSON a rejection crosses the op channel as.
    let render (r: DocRejection) : string =
        match r with
        | DocRejection.UnknownBlock id -> Canon.render (Canon.typed "UnknownBlock" [ "block", JStr(BlockId.value id) ])
        | DocRejection.UnknownPack pack -> Canon.render (Canon.typed "UnknownPack" [ "pack", JStr pack ])
        | DocRejection.PackFailed defects ->
            Canon.render (Canon.typed "PackFailed" [ "defects", JArr(defects |> List.map JStr) ])

    /// And back, on the far side.
    let parse (text: string) : DocRejection option =
        match Json.parse text with
        | Ok(JObj members) ->
            let get name =
                members |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

            match get "$type" with
            | Some(JStr "UnknownBlock") ->
                match get "block" with
                | Some(JStr id) -> Some(DocRejection.UnknownBlock(BlockId id))
                | _ -> None
            | Some(JStr "UnknownPack") ->
                match get "pack" with
                | Some(JStr pack) -> Some(DocRejection.UnknownPack pack)
                | _ -> None
            | Some(JStr "PackFailed") ->
                match get "defects" with
                | Some(JArr items) ->
                    items
                    |> List.map (function
                        | JStr d -> Some d
                        | _ -> None)
                    |> fun parsed ->
                        if List.forall Option.isSome parsed then
                            Some(DocRejection.PackFailed(List.choose id parsed))
                        else
                            None
                | _ -> None
            | _ -> None
        | _ -> None

// ─── the op algebra: edits, a guard, and the tail ───────────────────────────

/// The pipeline's ops. `SetText` and `Bind` are the domain's edits; the
/// guard is `RequirePack`; `Render` and `Commit` are the tail — acts the
/// performer carries out on the document the plan produced, which move the
/// plan not at all.
[<RequireQualifiedAccess>]
type DocStep =
    /// Rewrite one block's text: the MUTATION.
    | SetText of BlockId * text: string
    /// Bind a field in the document's own context: the READ's result.
    | Bind of field: string * value: string
    /// The named pack must pass over the document as it stands: the GUARD.
    | RequirePack of pack: string
    /// Render the document as it stands, in the named format.
    | Render of format: string
    /// Commit the pipeline's edits to the named stream.
    | Commit of stream: string

/// The packs the host holds, by name: each a list of banned terms.
type Packs = Map<string, string list>

/// The house style the tests require.
[<Literal>]
let HouseStyle = "house-style"

let packs: Packs = Map.ofList [ HouseStyle, [ "synergy"; "leverage" ] ]

let rec private blocks (b: Block) : Block list =
    b :: (b.Children |> List.collect blocks)

/// The named pack's verdict over a document: every block whose text holds a
/// banned term, as `blockId:term`, in document order.
let packVerdict (pack: string) (doc: Document) : Result<unit, DocRejection> =
    match Map.tryFind pack packs with
    | None -> Error(DocRejection.UnknownPack pack)
    | Some banned ->
        let defects =
            blocks doc.Root
            |> List.collect (fun b ->
                banned
                |> List.filter (fun term -> b.Text.ToLowerInvariant().Contains term)
                |> List.map (fun term -> BlockId.value b.Id + ":" + term))

        if List.isEmpty defects then
            Ok()
        else
            Error(DocRejection.PackFailed defects)

let rec private setText (id: BlockId) (text: string) (b: Block) : Block option =
    if b.Id = id then
        Some { b with Text = text }
    else
        let rec go (kids: Block list) =
            match kids with
            | [] -> None
            | k :: rest ->
                match setText id text k with
                | Some k' -> Some(k' :: rest)
                | None -> go rest |> Option.map (fun rest' -> k :: rest')

        go b.Children |> Option.map (fun kids -> { b with Children = kids })

/// Apply one op to the document, typed. A guard answers the document as it
/// stood or the pack's typed refusal; the tail changes nothing here — it is
/// what the performer does with the document the plan produced.
let applyTyped (step: DocStep) (doc: Document) : Result<Document, DocRejection> =
    match step with
    | DocStep.SetText(id, text) ->
        match setText id text doc.Root with
        | Some root -> Ok { doc with Root = root }
        | None -> Error(DocRejection.UnknownBlock id)
    | DocStep.Bind(field, value) ->
        Ok
            { doc with
                Context = Map.add field value doc.Context }
    | DocStep.RequirePack pack -> packVerdict pack doc |> Result.map (fun () -> doc)
    | DocStep.Render _
    | DocStep.Commit _ -> Ok doc

/// The op channel's apply: typed inside, a string outside (W5).
let apply (step: DocStep) (doc: Document) : Result<Document, string> =
    applyTyped step doc |> Result.mapError DocRejection.render

/// The op codec: canonical (K6).
let encodeStep (step: DocStep) : string =
    match step with
    | DocStep.SetText(id, text) ->
        Canon.render (Canon.typed "SetText" [ "block", JStr(BlockId.value id); "text", JStr text ])
    | DocStep.Bind(field, value) -> Canon.render (Canon.typed "Bind" [ "field", JStr field; "value", JStr value ])
    | DocStep.RequirePack pack -> Canon.render (Canon.typed "RequirePack" [ "pack", JStr pack ])
    | DocStep.Render format -> Canon.render (Canon.typed "Render" [ "format", JStr format ])
    | DocStep.Commit stream -> Canon.render (Canon.typed "Commit" [ "stream", JStr stream ])

/// What a step reaches: an edit the block it addresses; a bind the field; a
/// guard the pack it names; the tail the host's own store, locally.
let reach (step: DocStep) : OpReach =
    match step with
    | DocStep.SetText(id, _) ->
        { Arguments = [ "target", BlockId.value id ]
          Destination = EffectDestination.Absent }
    | DocStep.Bind(field, _) ->
        { Arguments = [ "field", field ]
          Destination = EffectDestination.Absent }
    | DocStep.RequirePack pack ->
        { Arguments = [ "pack", pack ]
          Destination = EffectDestination.Absent }
    | DocStep.Render format ->
        { Arguments = [ "format", format ]
          Destination = EffectDestination.Local }
    | DocStep.Commit stream ->
        { Arguments = [ "stream", stream ]
          Destination = EffectDestination.Local }

// ─── rendering, and the canonical form ──────────────────────────────────────

/// The document's text with every `{{field}}` resolved from its own context —
/// the bound values that live in the tree.
let render (format: string) (doc: Document) : string =
    let resolve (text: string) =
        doc.Context
        |> Map.fold (fun (t: string) field value -> t.Replace("{{" + field + "}}", value)) text

    let line (b: Block) =
        match format with
        | "html" -> sprintf "<%s id=\"%s\">%s</%s>" b.Kind (BlockId.value b.Id) (resolve b.Text) b.Kind
        | _ -> sprintf "[%s] %s" b.Kind (resolve b.Text)

    blocks doc.Root |> List.map line |> String.concat "\n"

let rec private encodeBlock (b: Block) : JVal =
    JObj
        [ "children", JArr(b.Children |> List.map encodeBlock)
          "id", JStr(BlockId.value b.Id)
          "kind", JStr b.Kind
          "text", JStr b.Text ]

/// The document's canonical bytes — the tree AND its context, because both
/// are the state.
let canonical (doc: Document) : string =
    Canon.render (
        JObj
            [ "context", JObj(doc.Context |> Map.toList |> List.map (fun (k, v) -> k, JStr v))
              "root", encodeBlock doc.Root ]
    )

// ─── the witness: state + walk ──────────────────────────────────────────────

/// A child block seen as a document over the SAME context — the embedding a
/// domain with one state type and a tree inside it uses for its walk.
let private asDocument (context: Map<string, string>) (b: Block) : Document = { Root = b; Context = context }

/// The walk axis: Core's node witness over the document's blocks, through the
/// id's faithful string form (K1).
let walk: WalkWitness<Document> =
    let nodes: NodeWitness<Document, string> =
        { Id = fun d -> BlockId.value d.Root.Id
          KindTag = fun d -> d.Root.Kind
          Children = fun d -> d.Root.Children |> List.map (asDocument d.Context)
          ReplaceChildren =
            fun d children ->
                { d with
                    Root =
                        { d.Root with
                            Children = children |> List.map _.Root } } }

    { Nodes = nodes
      Traverse = nodes.Children
      Cost = fun _ -> 0
      QueryReaders = fun _ -> [] }

/// The document witness: the state axis and the walk axis.
let witness: ProgramWitness<Document, DocStep, WalkWitness<Document>, Unfilled> =
    { State =
        { Stream =
            { Apply = apply
              Encode = encodeStep
              Decode = fun _ -> Error "the document pipeline decodes no step" }
          Reach = reach
          AbsoluteTarget =
            fun step ->
                match step with
                | DocStep.SetText(id, _) -> Some(BlockId.value id)
                | _ -> None
          Canonical = canonical
          Diff = fun _ _ -> []
          View =
            fun step ->
                match step with
                | DocStep.RequirePack _ -> OpView.Require
                | _ -> OpView.Edit }
      Walk = walk
      Dispatch = Unfilled }

// ─── the tail's performer ───────────────────────────────────────────────────

/// The op performer's world: the renderings written, the commit made, the
/// edits it was asked to commit, and the document it was handed last. It
/// holds NO copy of the document of its own and never applies an op: it is
/// handed the planned document with every op, so a render renders what it
/// was handed and a commit commits the edits it was handed — the plan phase
/// is not run a second time here (F-PERFORM).
type Sink(failAt: int option) =
    let rendered = System.Collections.Generic.Dictionary<string, string>()
    let edits = System.Collections.Generic.List<string>()
    let invocations = System.Collections.Generic.List<string>()
    let mutable committed: (string * string list) option = None
    let mutable last: Document option = None

    member _.Rendered = rendered |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
    member _.Committed = committed
    /// Every op the performer was asked to perform, in order.
    member _.Invocations = List.ofSeq invocations
    /// The document handed with the last op performed.
    member _.Document = last

    member _.Perform (state: Document) (step: DocStep) : Result<unit, string> =
        let position = invocations.Count
        invocations.Add(encodeStep step)
        last <- Some state

        match failAt with
        | Some k when k = position -> Error(sprintf "the world refused op %d" k)
        | _ ->
            match step with
            | DocStep.SetText _
            | DocStep.Bind _ ->
                edits.Add(encodeStep step)
                Ok()
            | DocStep.Render format ->
                rendered.[format] <- render format state
                Ok()
            | DocStep.Commit stream ->
                committed <- Some(stream, List.ofSeq edits)
                Ok()
            | DocStep.RequirePack _ -> Error "a guard reached the performer"
