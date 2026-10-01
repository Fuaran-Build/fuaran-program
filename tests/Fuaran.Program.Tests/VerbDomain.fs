/// A SECOND witness of the generic core, written for Phase 1967: a domain
/// whose handler is a store-mutating VERB — read a file map, write and delete
/// files, publish to a named target — rather than a UI event handler. No UI
/// type anywhere, like the toy beside it; unlike the toy, its ops reach the
/// world, its guards are typed refusals, and its placement performs the plan
/// after the handler commits. It reproduces, in miniature, what the first
/// second-domain instantiation found and had to build beside Program (the
/// four findings `DECISIONS.md` D19 records), so each is a test here rather
/// than a report elsewhere.
///
/// Since Phase 1974 it fills the STATE AXIS ONLY (`DECISIONS.md` D20): a verb
/// has no events, no node tree to walk and no binding store, so its
/// composition is `ProgramWitness<FileMap, FileOp, Unfilled, Unfilled>` — six
/// members, where 0.7.0 asked it for thirty-two and found twenty-five of them
/// vacuous. Its guards are ops (`Check`), which the state witness VIEWS as
/// guards: resolved against the plan as of their position, never performed —
/// the shape the first second witness reached for before Program had one.
module Fuaran.Program.Tests.VerbDomain

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Runtime

// ─── the typed refusal, and how it crosses (W5) ─────────────────────────────

/// A refusal the verb's guards raise, as the domain types it.
type VerbRefusal = { Code: string; Detail: string }

/// The op channel's rejection stays `string` (D19): a typed refusal crosses
/// as its canonical JSON and is parsed back on the far side, which is the one
/// answer the decision names.
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

/// The verb's state: an in-memory file map and what has been published. This
/// is the PLAN the handler edits; the world is the performer's.
type FileMap =
    { Files: Map<string, string>
      Published: string list }

/// What a guard checks of the plan.
type FileCheck =
    /// The plan holds this file.
    | Exists of path: string
    /// The plan does not hold this file.
    | Missing of path: string
    /// Refuses, always, with this typed refusal — the W5 crossing in one op.
    | Refused of VerbRefusal

/// The verb's ops. A read, a write, a delete and a publish — the four shapes
/// a store-mutating verb has — and a check, its guard.
type FileOp =
    | Read of path: string
    | Write of path: string * content: string
    | Delete of path: string
    | Publish of target: string
    | Check of FileCheck

// ─── the state witness ──────────────────────────────────────────────────────

/// The op codec: canonical, and the content a write carries is IN it, so a
/// ceiling on the arm measures what the handler document would carry.
let encodeOp (op: FileOp) : string =
    match op with
    | Read path -> Canon.render (Canon.typed "Read" [ "path", JStr path ])
    | Write(path, content) -> Canon.render (Canon.typed "Write" [ "content", JStr content; "path", JStr path ])
    | Delete path -> Canon.render (Canon.typed "Delete" [ "path", JStr path ])
    | Publish target -> Canon.render (Canon.typed "Publish" [ "target", JStr target ])
    | Check(Exists path) -> Canon.render (Canon.typed "Exists" [ "path", JStr path ])
    | Check(Missing path) -> Canon.render (Canon.typed "Missing" [ "path", JStr path ])
    | Check(Refused refusal) -> Canon.render (Canon.typed "Refused" [ "refusal", JStr(VerbRefusal.render refusal) ])

/// Apply one op to the PLAN. A read reads the plan; a delete of a file the
/// plan does not hold is an apply refusal, which halts the handler as every
/// apply refusal does. A check answers whether the plan holds what it names,
/// with a typed refusal rendered into the reason when it does not — and the
/// handler discards what a check answers, so a check cannot write.
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
    | Check(Exists path) ->
        if Map.containsKey path tree.Files then
            Ok tree
        else
            Error(VerbRefusal.render { Code = "missing"; Detail = path })
    | Check(Missing path) ->
        if Map.containsKey path tree.Files then
            Error(VerbRefusal.render { Code = "present"; Detail = path })
        else
            Ok tree
    | Check(Refused refusal) -> Error(VerbRefusal.render refusal)

/// What an op REACHES (W3, W4): a path under `path`, local; a publish target
/// under `target`, and REMOTE — the class a policy can bound without knowing
/// how this domain names its targets. A check reaches the path it reads, and
/// no destination: it acts on nothing.
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
    | Check(Exists path)
    | Check(Missing path) ->
        { Arguments = [ "path", path ]
          Destination = EffectDestination.Absent }
    | Check(Refused _) -> OpReach.nothing

let private canonical (tree: FileMap) : string =
    Canon.render (
        JObj
            [ "files", JObj(tree.Files |> Map.toList |> List.map (fun (path, content) -> path, JStr content))
              "published", JArr(tree.Published |> List.map JStr) ]
    )

/// The verb witness: the state axis, and nothing else.
let witness: ProgramWitness<FileMap, FileOp, Unfilled, Unfilled> =
    { State =
        { Stream =
            { Apply = apply
              Encode = encodeOp
              Decode = fun _ -> Error "the verb decodes no op" }
          Reach = reach
          AbsoluteTarget =
            fun op ->
                match op with
                | Read path
                | Write(path, _)
                | Delete path
                | Check(Exists path)
                | Check(Missing path) -> Some path
                | Publish target -> Some target
                | Check(Refused _) -> None
          Canonical = canonical
          Diff = fun _ _ -> []
          View =
            fun op ->
                match op with
                | Check _ -> OpView.Require
                | Read _
                | Write _
                | Delete _
                | Publish _ -> OpView.Edit }
      Walk = Unfilled
      Dispatch = Unfilled }

// ─── the world, and the performer over it ───────────────────────────────────

/// The WORLD the verb's performer acts on — what the plan is performed
/// against once the handler commits. Mutable on purpose: a performer is an
/// effect, and the tests read the world to see what actually happened.
type World() =
    let files = System.Collections.Generic.Dictionary<string, string>()
    let published = System.Collections.Generic.List<string>()
    let invocations = System.Collections.Generic.List<string>()
    let handed = System.Collections.Generic.List<FileMap>()

    member _.Files = files |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
    member _.Published = List.ofSeq published
    /// Every op the performer was asked to perform, in order — the ground
    /// truth `Performed` is checked against.
    member _.Invocations = List.ofSeq invocations
    /// The planned state handed with each op, in order (Phase 1974). A verb
    /// whose op IS the act does not need it; the test reads it to see what a
    /// tail that persisted the plan would have been handed.
    member _.Handed = List.ofSeq handed

    member _.Seed(path: string, content: string) = files.[path] <- content

    /// The performer: performs each op against the world, refusing at ONE
    /// position (zero-based, counted over its own invocations) or never. A
    /// check is a guard and never reaches it; one that did would be refused,
    /// so a test would see it.
    member _.Performer(failAt: int option) : FileMap -> FileOp -> Result<unit, string> =
        fun state op ->
            let position = invocations.Count
            invocations.Add(encodeOp op)
            handed.Add state

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
                | Check _ -> Error "a guard reached the performer"
