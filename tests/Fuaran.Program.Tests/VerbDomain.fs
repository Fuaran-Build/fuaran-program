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
open Fuaran.Program.Server

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
/// a store-mutating verb has — a check, its guard, and since Phase 1976 a
/// branch over two arms of ops and a bounded repeat, which the state witness
/// views as the core's `Choose` and `Repeat`: the two-arm construct the
/// second witness's re-run had to carry beside the core, now in it.
type FileOp =
    | Read of path: string
    | Write of path: string * content: string
    | Delete of path: string
    | Publish of target: string
    /// Withdraw a publish — the declared COMPENSATION of a publish to a target
    /// the domain can retract (Phase 1977). A compensation and not an
    /// inverse: the world saw the publish, and a retraction undoes it in
    /// effect, never in history.
    | Retract of target: string
    | Check of FileCheck
    | Branch of entry: FileCheck * whenTrue: FileOp list * whenFalse: FileOp list * exit: FileCheck option
    | Times of count: int * body: FileOp list
    /// Per-element iteration over a literal collection (Phase 1990), viewed
    /// as the core's `Each`: the body's paths and targets carry the
    /// placeholder as a `{name}` token, and the element is substituted into
    /// them — the address-dependent body D21's index-free repeat could not
    /// express, written as the loop a spreadsheet's automation actually
    /// writes: one fixed array of addresses, one body.
    | ForEach of collection: JVal list * placeholder: string * body: FileOp list

// ─── placeholders in operands (Phase 1990) ──────────────────────────────────

/// A placeholder in an operand: a path, a target or a write's content carrying
/// `{name}` reads the placeholder `name`. The domain's own spelling — the core
/// never sees it; it asks `Placeholders` which names an op reads and
/// `Substitute` to write an element over them.
module Placeholder =
    let private pattern =
        System.Text.RegularExpressions.Regex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")

    /// The placeholder names a string reads, in order of appearance.
    let names (text: string) : string list =
        pattern.Matches text |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq

    /// The string with the element written over every `{placeholder}`: a
    /// string element verbatim, any other element in its canonical rendering.
    let fill (placeholder: string) (element: JVal) (text: string) : string =
        let value =
            match element with
            | JStr s -> s
            | other -> Canon.render other

        text.Replace("{" + placeholder + "}", value)

/// The placeholders a CHECK's operand reads.
let private checkPlaceholders (check: FileCheck) : string list =
    match check with
    | Exists path
    | Missing path -> Placeholder.names path
    | Refused _ -> []

let private substituteCheck (placeholder: string) (element: JVal) (check: FileCheck) : FileCheck =
    match check with
    | Exists path -> Exists(Placeholder.fill placeholder element path)
    | Missing path -> Missing(Placeholder.fill placeholder element path)
    | Refused _ -> check

/// The verb's `Substitute` (Phase 1990): the element written over the
/// placeholder in every operand of the op and of the ops beneath it, the shape
/// of the op untouched — so the substituted op views as the original does,
/// which is the witness obligation. A nested `ForEach` keeps its own
/// collection and placeholder.
let rec substituteOp (placeholder: string) (element: JVal) (op: FileOp) : FileOp =
    let fill = Placeholder.fill placeholder element
    let sub = substituteOp placeholder element

    match op with
    | Read path -> Read(fill path)
    | Write(path, content) -> Write(fill path, fill content)
    | Delete path -> Delete(fill path)
    | Publish target -> Publish(fill target)
    | Retract target -> Retract(fill target)
    | Check check -> Check(substituteCheck placeholder element check)
    | Branch(entry, whenTrue, whenFalse, exit) ->
        Branch(
            substituteCheck placeholder element entry,
            whenTrue |> List.map sub,
            whenFalse |> List.map sub,
            exit |> Option.map (substituteCheck placeholder element)
        )
    | Times(count, body) -> Times(count, body |> List.map sub)
    | ForEach(collection, name, body) -> ForEach(collection, name, body |> List.map sub)

/// The verb's `Placeholders` (Phase 1990): the names an op's OWN operands read
/// — its path, target or content — and not those of the ops beneath a flow
/// op, which the core reaches through the view (a branch's conditions are ops
/// and are asked like any other).
let placeholdersOf (op: FileOp) : string list =
    match op with
    | Read path
    | Delete path -> Placeholder.names path
    | Write(path, content) -> Placeholder.names path @ Placeholder.names content
    | Publish target
    | Retract target -> Placeholder.names target
    | Check check -> checkPlaceholders check
    | Branch _
    | Times _
    | ForEach _ -> []

// ─── the state witness ──────────────────────────────────────────────────────

/// The op codec: canonical, and the content a write carries is IN it, so a
/// ceiling on the arm measures what the handler document would carry.
let rec encodeOp (op: FileOp) : string =
    match op with
    | Read path -> Canon.render (Canon.typed "Read" [ "path", JStr path ])
    | Write(path, content) -> Canon.render (Canon.typed "Write" [ "content", JStr content; "path", JStr path ])
    | Delete path -> Canon.render (Canon.typed "Delete" [ "path", JStr path ])
    | Publish target -> Canon.render (Canon.typed "Publish" [ "target", JStr target ])
    | Retract target -> Canon.render (Canon.typed "Retract" [ "target", JStr target ])
    | Check(Exists path) -> Canon.render (Canon.typed "Exists" [ "path", JStr path ])
    | Check(Missing path) -> Canon.render (Canon.typed "Missing" [ "path", JStr path ])
    | Check(Refused refusal) -> Canon.render (Canon.typed "Refused" [ "refusal", JStr(VerbRefusal.render refusal) ])
    | Branch(entry, whenTrue, whenFalse, exit) ->
        let arm (ops: FileOp list) =
            JArr(ops |> List.map (encodeOp >> JStr))

        let encodedExit =
            match exit with
            | Some e -> JStr(encodeOp (Check e))
            | None -> JBool false

        Canon.render (
            Canon.typed
                "Branch"
                [ "entry", JStr(encodeOp (Check entry))
                  "exit", encodedExit
                  "whenFalse", arm whenFalse
                  "whenTrue", arm whenTrue ]
        )
    | Times(count, body) ->
        Canon.render (Canon.typed "Times" [ "body", JArr(body |> List.map (encodeOp >> JStr)); "count", JInt count ])
    | ForEach(collection, placeholder, body) ->
        Canon.render (
            Canon.typed
                "ForEach"
                [ "body", JArr(body |> List.map (encodeOp >> JStr))
                  "collection", JArr collection
                  "placeholder", JStr placeholder ]
        )

/// The op decoder (Phase 1982, H1): `encodeOp`'s inverse, so the verb's
/// handlers round-trip as Program documents. Total: anything `encodeOp` did not
/// produce is a refusal naming what it is not.
let rec decodeOp (text: string) : Result<FileOp, string> =
    let field (name: string) (members: (string * JVal) list) =
        members |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

    let str name members =
        match field name members with
        | Some(JStr s) -> Ok s
        | _ -> Error(sprintf "member '%s' is not a string" name)

    let check (text: string) =
        match decodeOp text with
        | Ok(Check c) -> Ok c
        | Ok _ -> Error "a branch condition is not a check"
        | Error e -> Error e

    let ops (name: string) members =
        match field name members with
        | Some(JArr items) ->
            items
            |> List.fold
                (fun acc item ->
                    match acc, item with
                    | Ok decoded, JStr t -> decodeOp t |> Result.map (fun op -> decoded @ [ op ])
                    | Ok _, _ -> Error(sprintf "member '%s' holds a non-string op" name)
                    | Error e, _ -> Error e)
                (Ok [])
        | _ -> Error(sprintf "member '%s' is not an array" name)

    match Json.parse text with
    | Ok(JObj members) ->
        match field "$type" members with
        | Some(JStr "Read") -> str "path" members |> Result.map Read
        | Some(JStr "Write") ->
            str "path" members
            |> Result.bind (fun path -> str "content" members |> Result.map (fun c -> Write(path, c)))
        | Some(JStr "Delete") -> str "path" members |> Result.map Delete
        | Some(JStr "Publish") -> str "target" members |> Result.map Publish
        | Some(JStr "Retract") -> str "target" members |> Result.map Retract
        | Some(JStr "Exists") -> str "path" members |> Result.map (Exists >> Check)
        | Some(JStr "Missing") -> str "path" members |> Result.map (Missing >> Check)
        | Some(JStr "Refused") ->
            str "refusal" members
            |> Result.bind (fun r ->
                match VerbRefusal.parse r with
                | Some refusal -> Ok(Check(Refused refusal))
                | None -> Error "member 'refusal' is not a refusal")
        | Some(JStr "Branch") ->
            str "entry" members
            |> Result.bind check
            |> Result.bind (fun entry ->
                let exit =
                    match field "exit" members with
                    | Some(JBool false) -> Ok None
                    | Some(JStr t) -> check t |> Result.map Some
                    | _ -> Error "member 'exit' is neither a check nor false"

                exit
                |> Result.bind (fun exit ->
                    ops "whenTrue" members
                    |> Result.bind (fun whenTrue ->
                        ops "whenFalse" members
                        |> Result.map (fun whenFalse -> Branch(entry, whenTrue, whenFalse, exit)))))
        | Some(JStr "Times") ->
            match field "count" members with
            | Some(JInt count) -> ops "body" members |> Result.map (fun body -> Times(int count, body))
            | _ -> Error "member 'count' is not an integer"
        | Some(JStr "ForEach") ->
            match field "collection" members with
            | Some(JArr collection) ->
                str "placeholder" members
                |> Result.bind (fun placeholder ->
                    ops "body" members
                    |> Result.map (fun body -> ForEach(collection, placeholder, body)))
            | _ -> Error "member 'collection' is not an array"
        | Some(JStr other) -> Error(sprintf "'%s' is not a verb op" other)
        | _ -> Error "the op carries no '$type'"
    | _ -> Error "the op is not a JSON object"

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
    | Retract target ->
        Ok
            { tree with
                Published = tree.Published |> List.filter (fun t -> t <> target) }
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
    // A flow op is planned through its VIEW — its conditions and its arms are
    // what the handler applies — and is never applied itself.
    | Branch _
    | Times _
    | ForEach _ -> Error "a flow op is planned through its view and never applied"

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
    | Publish target
    | Retract target ->
        { Arguments = [ "target", target ]
          Destination = EffectDestination.Remote target }
    | Check(Exists path)
    | Check(Missing path) ->
        { Arguments = [ "path", path ]
          Destination = EffectDestination.Absent }
    | Check(Refused _) -> OpReach.nothing
    // A branch reaches nothing of its own: its conditions and arms reach, and
    // the policy reads them beneath it. A repeat names its COUNT as an
    // argument, which is how a deployer's allow-list bounds how many times a
    // body may run — the over-bound refusal, before anything performs.
    | Branch _ -> OpReach.nothing
    | Times(count, _) ->
        { Arguments = [ "count", string count ]
          Destination = EffectDestination.Absent }
    // A per-element iteration reaches nothing of its own: the policy reads
    // its LOWERED body beneath it — every element's substituted paths — so a
    // placeholder cannot hide an address from an allow-list.
    | ForEach _ -> OpReach.nothing

let canonical (tree: FileMap) : string =
    Canon.render (
        JObj
            [ "files", JObj(tree.Files |> Map.toList |> List.map (fun (path, content) -> path, JStr content))
              "published", JArr(tree.Published |> List.map JStr) ]
    )

/// The targets a publish can be withdrawn from — the domain's declaration of
/// which publishes carry a compensation (Phase 1977). A publish to `origin`
/// is public and carries none: it is the one-way case a verb that ends in a
/// push is the expected instance of.
let retractable: Set<string> = Set.ofList [ "staging" ]

/// What undoes each op (Phase 1977): a read nothing; a write the write of the
/// bytes the pre-state held, or the delete of a file it did not; a delete the
/// write of the bytes it removed; a publish to a retractable target its
/// retraction, declared, and to any other target NOTHING, with the reason; a
/// retraction the publish back. The CLASS reads the op alone; only the
/// inverse reads the pre-state, which is what makes the posture readable
/// before the verb runs.
let undo (op: FileOp) : UndoClass<FileMap, FileOp> =
    match op with
    | Read _ -> UndoClass.Inverse(fun _ -> [])
    | Write(path, _) ->
        UndoClass.Inverse(fun pre ->
            match Map.tryFind path pre.Files with
            | Some old -> [ Write(path, old) ]
            | None -> [ Delete path ])
    | Delete path ->
        UndoClass.Inverse(fun pre ->
            match Map.tryFind path pre.Files with
            | Some old -> [ Write(path, old) ]
            | None -> [])
    | Publish target when Set.contains target retractable -> UndoClass.Compensate(fun _ -> [ Retract target ])
    | Publish target -> UndoClass.OneWay(sprintf "a publish to %s is public" target)
    | Retract target -> UndoClass.Compensate(fun _ -> [ Publish target ])
    // Never an edit, so never asked; refused if ever it were.
    | Check _
    | Branch _
    | Times _
    | ForEach _ -> UndoClass.OneWay "a guard or a flow op is not an edit"

/// The verb witness: the state axis, and nothing else.
let witness: ProgramWitness<FileMap, FileOp, Unfilled, Unfilled> =
    { State =
        { Stream =
            { Apply = apply
              Encode = encodeOp
              Decode = decodeOp }
          Reach = reach
          AbsoluteTarget =
            fun op ->
                match op with
                | Read path
                | Write(path, _)
                | Delete path
                | Check(Exists path)
                | Check(Missing path) -> Some path
                | Publish target
                | Retract target -> Some target
                | Check(Refused _)
                | Branch _
                | Times _
                | ForEach _ -> None
          Canonical = canonical
          Diff = fun _ _ -> []
          View =
            fun op ->
                match op with
                | Check _ -> OpView.Require
                | Branch(entry, whenTrue, whenFalse, exit) ->
                    OpView.Choose(Check entry, whenTrue, whenFalse, exit |> Option.map Check)
                | Times(count, body) -> OpView.Repeat(count, body)
                | ForEach(collection, placeholder, body) ->
                    OpView.Each(Collection.Literal collection, placeholder, body)
                | Read _
                | Write _
                | Delete _
                | Publish _
                | Retract _ -> OpView.Edit
          Undo = undo
          Substitute = substituteOp
          Placeholders = placeholdersOf }
      Walk = Unfilled
      Dispatch = Unfilled }

// ─── the world, and the performer over it ───────────────────────────────────

/// The receipt vocabulary the verb's performer answers in, and the contract
/// over it (Phase 1981): a receipt names the paths and the targets the
/// performer touched; `withinReach` is the domain's statement that an op's
/// reach covers what its performer touches, CHECKED — every path and target a
/// receipt names is one the op's reach names.
module Receipt =
    let paths (named: string list) : JVal =
        JObj [ "paths", JArr(named |> List.map JStr) ]

    let targets (named: string list) : JVal =
        JObj [ "targets", JArr(named |> List.map JStr) ]

    /// The names a receipt claims under `key`; a receipt in any other shape
    /// claims nothing, so a contract over it refuses nothing it cannot read
    /// and admits nothing it did not.
    let private claimed (key: string) (receipt: JVal) : string list option =
        match receipt with
        | JObj members ->
            match members |> List.tryFind (fun (k, _) -> k = key) with
            | None -> Some []
            | Some(_, JArr items) ->
                items
                |> List.map (fun item ->
                    match item with
                    | JStr s -> Some s
                    | _ -> None)
                |> List.fold
                    (fun acc item ->
                        match acc, item with
                        | Some xs, Some s -> Some(xs @ [ s ])
                        | _ -> None)
                    (Some [])
            | Some _ -> None
        | _ -> None

    /// The contract: every path and every target the receipt names is one
    /// the op's reach names under the same argument, and the receipt is
    /// readable. The reach is the DECLARATION the policy enforced against
    /// before anything performed; this is the check that what was done
    /// stayed inside it.
    let withinReach: OpContract<FileMap, FileOp> =
        { Name = "within-reach"
          Holds =
            fun _ op receipt ->
                let declared (argument: string) =
                    (reach op).Arguments |> List.filter (fun (a, _) -> a = argument) |> List.map snd

                match claimed "paths" receipt, claimed "targets" receipt with
                | Some ps, Some ts ->
                    ps |> List.forall (fun p -> List.contains p (declared "path"))
                    && ts |> List.forall (fun t -> List.contains t (declared "target"))
                | _ -> false }

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
    /// position (zero-based, counted over its own invocations) or never, and
    /// answering a RECEIPT (Phase 1981) that names what it touched — the
    /// paths it read, wrote or deleted, the targets it published or
    /// retracted — in the shape `Receipt` reads back. A check is a guard and
    /// never reaches it; one that did would be refused, so a test would see
    /// it.
    member _.Performer(failAt: int option) : FileMap -> FileOp -> Result<JVal, string> =
        fun state op ->
            let position = invocations.Count
            invocations.Add(encodeOp op)
            handed.Add state

            match failAt with
            | Some k when k = position -> Error(sprintf "the world refused op %d" k)
            | _ ->
                match op with
                | Read path -> Ok(Receipt.paths [ path ])
                | Write(path, content) ->
                    files.[path] <- content
                    Ok(Receipt.paths [ path ])
                | Delete path ->
                    files.Remove path |> ignore
                    Ok(Receipt.paths [ path ])
                | Publish target ->
                    published.Add target
                    Ok(Receipt.targets [ target ])
                | Retract target ->
                    published.Remove target |> ignore
                    Ok(Receipt.targets [ target ])
                | Check _ -> Error "a guard reached the performer"
                | Branch _
                | Times _
                | ForEach _ -> Error "a flow op reached the performer"

    /// The ADVERSARY (Phase 1981): performs every op as `Performer` does, and
    /// on every write ALSO writes `escape` — a path the op's reach does not
    /// name — and says so in its receipt. The receipt is the performer's own
    /// account, and an honest account of an overreach is exactly what a
    /// contract over the reach refuses; a performer that overreached and
    /// said nothing is outside what any contract can see, which is the
    /// boundary `docs/performer-boundary.md` records.
    member this.Escaping (escape: string) (failAt: int option) : FileMap -> FileOp -> Result<JVal, string> =
        let honest = this.Performer failAt

        fun state op ->
            match honest state op, op with
            | Ok _, Write(path, content) ->
                files.[escape] <- content
                Ok(Receipt.paths [ path; escape ])
            | answer, _ -> answer
