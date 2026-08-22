namespace Fuaran.Program.Bounded

open Fuaran.Core
open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open Fuaran.UI.ServerDriven

// ============================================================================
//  The program wire — the shared half.
//
//  This file is the placement-neutral part of the codec for the program wire
//  specification: the canonical-JSON discipline, the refusal vocabulary, the
//  three REFERENCED positions the specification does not respecify, the
//  cross-layer reference, the invocation record, the client-effect reader, and
//  the replay classification of an action.
//
//  The placement-specific half — the handler declared form, the server-effect
//  vocabulary and the outcome report — lives with the placement that owns those
//  types.
//
//  ── Three properties worth stating before the code ──────────────────────────
//
//  1. A REFUSAL CARRIES A CLASS, not a message. The specification's Appendix A
//     enumerates them, the corpus names one per reject vector, and a conformant
//     reader must refuse FOR THE NAMED CLASS — because "my parser threw" and "my
//     reader applied the rule" are different facts and only the second is
//     conformance. The `Detail` beside it is for a human and is never compared.
//
//  2. NOTHING HERE RE-SPELLS A REFERENCED VOCABULARY. An action, a tree-op, a
//     tabular source and a pipeline are encoded and decoded by their OWN
//     canonical codecs and spliced. That is not laziness: a second spelling of a
//     shape is exactly the drift the specification's §3 exists to forbid, and it
//     is why `decodeAction` reaches the tree codec through the one public entry
//     point that reaches it rather than reimplementing an action reader.
//
//  3. THE REPLAY CLASSIFICATION READS THE DOCUMENT, not the value. The
//     specification derives it "from the declared form", and the declared form
//     IS the document — so classifying the encoded JSON rather than matching the
//     action DU keeps this side and the corpus's own emitter running literally
//     the same rule, and adds no new walk over a closed vocabulary.
// ============================================================================

/// Why a document was refused. `Class` is the specification's own refusal class
/// (Appendix A) and is the only part a conformance harness compares; `Detail` is
/// for a human reading a log.
type WireRefusal = { Class: string; Detail: string }

/// The refusal classes, as the specification's Appendix A enumerates them. They
/// are `Literal`s so a caller can match on them and a typo is a compile error
/// rather than a silently-never-equal string.
[<RequireQualifiedAccess>]
module RefusalClass =

    [<Literal>]
    let NullMember = "null-member"

    [<Literal>]
    let UndeclaredMember = "undeclared-member"

    [<Literal>]
    let MissingMember = "missing-member"

    [<Literal>]
    let UnknownStageKind = "unknown-stage-kind"

    [<Literal>]
    let UnknownEffectArm = "unknown-effect-arm"

    [<Literal>]
    let EmptyName = "empty-name"

    [<Literal>]
    let NameTooLong = "name-too-long"

    [<Literal>]
    let HostReservedLandingSlot = "host-reserved-landing-slot"

    [<Literal>]
    let EmptyIdempotencyKey = "empty-idempotency-key"

    [<Literal>]
    let IdempotencyKeyTooLong = "idempotency-key-too-long"

    [<Literal>]
    let ImpossibleOutcome = "impossible-outcome"

    [<Literal>]
    let EndpointEchoed = "endpoint-echoed"

    [<Literal>]
    let UnknownSlot = "unknown-slot"

    [<Literal>]
    let TreeDeclaredResultTarget = "tree-declared-result-target"

    [<Literal>]
    let MalformedReferencedValue = "malformed-referenced-value"

/// The specification's three-valued replay classification. `Unknown` is not a
/// failure and is never rounded to a neighbour: only a PROOF is a finding, and a
/// classification that fired on ordinary correct handlers would be one people
/// learn to scroll past.
[<RequireQualifiedAccess>]
type ReplaySafety =
    | Safe
    | Unsafe
    | Unknown

/// WHY a handler is not provably re-runnable — a closed vocabulary of DERIVED
/// facts.
///
/// Every arm is something the walk demonstrated about a declared form, and not
/// one of them is a string a document supplied. That is what lets a reason
/// travel out of this subsystem — into a diagnostic, a projection, a capability
/// manifest — under exactly the log-safety rule the rest of it keeps: a
/// diagnostic carries the derived capability, never a name off the wire.
///
/// Two arms are PROOFS that a re-run reaches outside; the other four are places
/// the walk cannot decide. The split is not cosmetic — it is what `gradeOfDefect`
/// reads, and it is why `unknown` is never rounded to a neighbour.
[<RequireQualifiedAccess>]
type ReplayDefect =
    /// An op is not provably absolutely addressed: it names no non-empty target
    /// node, so this walk cannot tell an absolute address from a position
    /// relative to a sibling count.
    | RelativeAddressing
    /// An op does not encode at all, so there is no document to read an address
    /// off. Distinct from the above because "cannot decide the addressing" and
    /// "cannot read the op" send a reader to different places.
    | UnencodableOp
    /// A state write takes its value from a binding, resolved at dispatch
    /// against a store that has moved.
    | NonLiteralWrite
    /// An action arm this walk does not decide.
    | UndecidableAction
    /// A host call: it commits somewhere this host does not own.
    | OpaqueHostCall
    /// A notification: a second run ships the message a second time.
    | OutboundNotification

/// One reason, positioned.
///
/// The stage is an ORDINAL, not a name. A stage carries no identifier of its
/// own, and a position is the only thing that addresses one without echoing a
/// string the document chose — which is the same rule as the vocabulary above,
/// applied to the locator rather than to the finding.
type ReplayReason = { Stage: int; Defect: ReplayDefect }

/// A composition surface naming a program by identifier. One bounded reference
/// and nothing else — the opaqueness is enforced by what this record OMITS,
/// which is why the decoder refuses an undeclared member rather than consulting
/// a list of forbidden ones.
type LogicTreeRef = { Ref: string }

/// What reaches a handler, and under what identity. `Endpoint` and `NodeId` are
/// exactly what the shared fold already hands a placement's handler arm; the key
/// is the one thing the wire cut decided.
type InvocationRecord =
    { Endpoint: string
      IdempotencyKey: string option
      NodeId: string }

module ProgramWire =

    // ─── refusal helpers ─────────────────────────────────────────────────────

    let refuse (cls: string) (detail: string) : Result<'T, WireRefusal> = Error { Class = cls; Detail = detail }

    // ─── parsing ─────────────────────────────────────────────────────────────

    /// Parse a wire document under the canonical discipline.
    ///
    /// The parser's default null policy is REJECT, so the specification's
    /// no-null rule is enforced by the parser at every nesting depth — including
    /// inside an opaque payload position this codec never decomposes, which is
    /// the one place a hand-rolled check would have missed it. The rejection is
    /// re-classified here so it arrives as the specification's own class rather
    /// than as a parse error.
    let parseDocument (json: string) : Result<JVal, WireRefusal> =
        match Json.parseDetailed json with
        | Ok value -> Ok value
        | Error err when err.Kind = NullNotRepresentable -> refuse RefusalClass.NullMember err.Message
        | Error err -> refuse RefusalClass.MalformedReferencedValue err.Message

    /// The canonical rendering of a document: `$type` first (U+0024 sorts before
    /// every lower-case key), members Ordinal-ordered, no trailing newline.
    let render (value: JVal) : string = Canon.render value

    // ─── member access ───────────────────────────────────────────────────────

    let private fieldsOf (value: JVal) : (string * JVal) list option =
        match value with
        | JObj fields -> Some fields
        | _ -> None

    let tryMember (name: string) (value: JVal) : JVal option =
        fieldsOf value |> Option.bind (List.tryFind (fst >> (=) name)) |> Option.map snd

    let requireObject (what: string) (value: JVal) : Result<(string * JVal) list, WireRefusal> =
        match fieldsOf value with
        | Some fields -> Ok fields
        | None -> refuse RefusalClass.MissingMember (what + ": expected an object")

    let requireMember (name: string) (value: JVal) : Result<JVal, WireRefusal> =
        match tryMember name value with
        | Some v -> Ok v
        | None -> refuse RefusalClass.MissingMember ("required member '" + name + "' is absent")

    let requireString (name: string) (value: JVal) : Result<string, WireRefusal> =
        requireMember name value
        |> Result.bind (fun v ->
            match v with
            | JStr s -> Ok s
            | _ -> refuse RefusalClass.MissingMember ("member '" + name + "' is not a string"))

    let tryString (name: string) (value: JVal) : string option =
        match tryMember name value with
        | Some(JStr s) -> Some s
        | _ -> None

    let requireBool (name: string) (value: JVal) : Result<bool, WireRefusal> =
        requireMember name value
        |> Result.bind (fun v ->
            match v with
            | JBool b -> Ok b
            | _ -> refuse RefusalClass.MissingMember ("member '" + name + "' is not a boolean"))

    let requireArray (name: string) (value: JVal) : Result<JVal list, WireRefusal> =
        requireMember name value
        |> Result.bind (fun v ->
            match v with
            | JArr xs -> Ok xs
            | _ -> refuse RefusalClass.MissingMember ("member '" + name + "' is not an array"))

    /// The tag of a `$type`-discriminated object, or `None` where there is none.
    let tag (value: JVal) : string option = tryString "$type" value

    /// Refuse any member the specification does not declare for this document.
    ///
    /// This is stricter than a must-ignore rule and deliberately so: a
    /// must-ignore rule invites a document to carry a member some future version
    /// might honour, and here the document is untrusted. It is also the whole
    /// enforcement of two separate rules — a self-declared capability, and an
    /// inline body in a reference — neither of which needs a check of its own.
    let declaredOnly (allowed: string list) (value: JVal) : Result<unit, WireRefusal> =
        match fieldsOf value with
        | None -> refuse RefusalClass.MissingMember "expected an object"
        | Some fields ->
            match fields |> List.map fst |> List.filter (fun k -> not (List.contains k allowed)) with
            | [] -> Ok()
            | extra -> refuse RefusalClass.UndeclaredMember ("undeclared member(s): " + String.concat ", " extra)

    /// Traverse a `Result` list, short-circuiting on the first refusal so a
    /// document with two defects reports the first rather than an aggregate the
    /// caller has to unpick.
    let traverse (f: 'a -> Result<'b, WireRefusal>) (items: 'a list) : Result<'b list, WireRefusal> =
        (Ok [], items)
        ||> List.fold (fun acc item -> acc |> Result.bind (fun done_ -> f item |> Result.map (fun v -> v :: done_)))
        |> Result.map List.rev

    // ─── referenced position: the action algebra ─────────────────────────────

    /// A call action declaring a result target is REFUSED, at any depth.
    ///
    /// Checked on the DOCUMENT rather than after decoding, for two reasons. The
    /// tree codec accepts the target — it is legal in the tree wire format, and
    /// this rule belongs to this specification, not that one — and the refusal
    /// must reach a reader before anything is constructed from the document.
    /// The detail names neither the endpoint nor the target: both come off the
    /// wire.
    let rec private refuseResultTarget (value: JVal) : Result<unit, WireRefusal> =
        let here =
            match tag value, tryMember "into" value with
            | Some "Call", Some _ ->
                refuse
                    RefusalClass.TreeDeclaredResultTarget
                    "the call declares a result target; a handler declares where its own results land"
            | _ -> Ok()

        here
        |> Result.bind (fun () ->
            match value with
            | JObj fields -> fields |> List.map snd |> traverse refuseResultTarget |> Result.map ignore
            | JArr items -> items |> traverse refuseResultTarget |> Result.map ignore
            | _ -> Ok())

    /// Encode an action for a compute-stage position — through the tree codec's
    /// own encoder, so this file spells no action case.
    let encodeAction (action: Action<obj>) : JVal =
        Fuaran.UI.Generated.encodeActionJson action

    /// Decode an action from a compute-stage position.
    ///
    /// The tree codec exposes no standalone action reader, so the action is
    /// decoded THROUGH the tree codec by riding a minimal carrier node. That is
    /// deliberate rather than a workaround: reimplementing an action reader here
    /// would put a second spelling of a foreign vocabulary in this repository,
    /// which is exactly what §3 forbids — and the carrier costs one object
    /// literal, where a second reader would cost a permanent drift risk.
    let decodeAction (value: JVal) : Result<Action<obj>, WireRefusal> =
        refuseResultTarget value
        |> Result.bind (fun () ->
            let carrier =
                JObj
                    [ "id", JStr "carrier"
                      "kind",
                      JObj
                          [ "$type", JStr "Button"
                            "label", JStr "carrier"
                            "onClick", value
                            "variant", JStr "Primary" ] ]

            match Fuaran.UI.Ops.JsonDecode.decodeNodeObj (Canon.render carrier) with
            | Error err ->
                refuse RefusalClass.MalformedReferencedValue ("the action does not decode: " + string err.Code)
            | Ok node ->
                match node.Kind with
                | NodeKind.Button spec -> Ok spec.OnClick
                | _ -> refuse RefusalClass.MalformedReferencedValue "the action carrier did not decode as expected")

    // ─── referenced position: the tree-op algebra ────────────────────────────

    let encodeOp (op: TreeOp<obj>) : Result<JVal, WireRefusal> =
        match Json.parse (Fuaran.UI.OpStream.Abstractions.CanonicalJson.encodeOp op) with
        | Ok value -> Ok value
        | Error message -> refuse RefusalClass.MalformedReferencedValue message

    let decodeOp (value: JVal) : Result<TreeOp<obj>, WireRefusal> =
        match Fuaran.UI.Ops.JsonDecode.decodeOp (Canon.render value) with
        | Ok op -> Ok op
        | Error err -> refuse RefusalClass.MalformedReferencedValue ("the op does not decode: " + string err.Code)

    // ─── referenced position: the tabular source and pipeline ────────────────

    let encodeSource (source: DataSource) : JVal = ColumnCodec.encodeJson source

    let decodeSource (value: JVal) : Result<DataSource, WireRefusal> =
        match ColumnCodec.decodeJson value with
        | Ok source -> Ok source
        | Error err ->
            refuse RefusalClass.MalformedReferencedValue ("the source does not decode: " + ColumnCodec.errorString err)

    let encodePipeline (pipeline: Transform list) : Result<JVal, WireRefusal> =
        match Json.parse (DataFrameCodec.encodePipeline pipeline) with
        | Ok value -> Ok value
        | Error message -> refuse RefusalClass.MalformedReferencedValue message

    let decodePipeline (value: JVal) : Result<Transform list, WireRefusal> =
        match DataFrameCodec.decodePipelineJson value with
        | Ok pipeline -> Ok pipeline
        | Error err ->
            refuse
                RefusalClass.MalformedReferencedValue
                ("the pipeline does not decode: " + ColumnCodec.errorString err)

    // ─── the replay classification (the action half) ─────────────────────────

    /// `Unsafe` dominates `Unknown`, which dominates `Safe`: a classification is
    /// the strongest claim its weakest part permits.
    let worst (a: ReplaySafety) (b: ReplaySafety) : ReplaySafety =
        match a, b with
        | ReplaySafety.Unsafe, _
        | _, ReplaySafety.Unsafe -> ReplaySafety.Unsafe
        | ReplaySafety.Unknown, _
        | _, ReplaySafety.Unknown -> ReplaySafety.Unknown
        | _ -> ReplaySafety.Safe

    /// The grade one defect forces.
    ///
    /// The two outward-reaching arms are the only PROOFS; everything else is a
    /// place the walk could not decide, and the specification is explicit that
    /// such a place is reported as undecided rather than rounded to either
    /// neighbour. A classification that fired on ordinary correct handlers would
    /// be one people learn to scroll past.
    let gradeOfDefect (defect: ReplayDefect) : ReplaySafety =
        match defect with
        | ReplayDefect.OpaqueHostCall
        | ReplayDefect.OutboundNotification -> ReplaySafety.Unsafe
        | ReplayDefect.RelativeAddressing
        | ReplayDefect.UnencodableOp
        | ReplayDefect.NonLiteralWrite
        | ReplayDefect.UndecidableAction -> ReplaySafety.Unknown

    /// The verdict a defect list carries: no defect is the only proof of `Safe`.
    let verdictOfDefects (defects: ReplayDefect list) : ReplaySafety =
        defects
        |> List.fold (fun acc d -> worst acc (gradeOfDefect d)) ReplaySafety.Safe

    /// The verdict a reason list carries. `worst` is associative and
    /// commutative, so a verdict computed over reasons gathered from every stage
    /// is the same one computed stage by stage — which is what lets the reasons
    /// be the primitive and the classification be derived from them.
    let verdictOfReasons (reasons: ReplayReason list) : ReplaySafety =
        reasons |> List.map _.Defect |> verdictOfDefects

    /// The defects of an ENCODED action.
    ///
    ///   Call      — inert inside a handler stage, so re-running it changes
    ///               nothing. That is not a property of the action; it is D7's
    ///               deliberate boundary, and the classification reads it.
    ///   Chain     — the defects of its parts.
    ///   SetState  — a literal write is re-runnable; one taking its value from a
    ///               binding is resolved at dispatch against a store that has
    ///               moved, so it is undecidable rather than unsafe.
    ///   otherwise — undecidable, and reported as such.
    let rec replayDefectsOfAction (action: JVal) : ReplayDefect list =
        match tag action with
        | Some "Call" -> []
        | Some "Chain" ->
            match tryMember "ops" action with
            | Some(JArr items) -> items |> List.collect replayDefectsOfAction |> List.distinct
            | _ -> [ ReplayDefect.UndecidableAction ]
        | Some "SetState" ->
            match tryMember "valueFrom" action with
            | Some _ -> [ ReplayDefect.NonLiteralWrite ]
            | None -> []
        | _ -> [ ReplayDefect.UndecidableAction ]

    /// The defects of an ENCODED tree-op: an op naming a target node addresses
    /// it absolutely, and anything else this walk cannot decide.
    let replayDefectsOfOp (op: JVal) : ReplayDefect list =
        match tryString "target" op with
        | Some target when target <> "" -> []
        | _ -> [ ReplayDefect.RelativeAddressing ]

    /// The classification of an ENCODED action — the verdict its defects carry.
    ///
    /// Defined THROUGH the defect walk rather than beside it. A second walk
    /// producing the verdict directly would be a second copy of the rule, free
    /// to drift from the reasons that are supposed to explain it, and a reason
    /// that disagrees with its own verdict is worse than no reason at all.
    let replaySafetyOfAction (action: JVal) : ReplaySafety =
        replayDefectsOfAction action |> verdictOfDefects

    /// The classification of an ENCODED tree-op — the verdict its defects carry.
    let replaySafetyOfOp (op: JVal) : ReplaySafety =
        replayDefectsOfOp op |> verdictOfDefects

    /// The wire spelling of a classification, as a manifest declares it.
    let replaySafetyTag (safety: ReplaySafety) : string =
        match safety with
        | ReplaySafety.Safe -> "safe"
        | ReplaySafety.Unsafe -> "unsafe"
        | ReplaySafety.Unknown -> "unknown"

    /// The wire spelling of a defect. A stable token rather than prose: a reason
    /// is carried in a projection document a consumer compares by value, so the
    /// spelling is part of the contract and not a message.
    let replayDefectTag (defect: ReplayDefect) : string =
        match defect with
        | ReplayDefect.RelativeAddressing -> "relative-addressing"
        | ReplayDefect.UnencodableOp -> "unencodable-op"
        | ReplayDefect.NonLiteralWrite -> "non-literal-write"
        | ReplayDefect.UndecidableAction -> "undecidable-action"
        | ReplayDefect.OpaqueHostCall -> "opaque-host-call"
        | ReplayDefect.OutboundNotification -> "outbound-notification"

    // ─── the cross-layer reference ───────────────────────────────────────────

    /// The slot's identity is the (namespace, kind) PAIR. Both renderings are
    /// DERIVED from it rather than stored twice — a composition surface's own
    /// registry joins the same pair with its own separator, and two stored
    /// spellings of one datum is what a later reader "fixes" in whichever
    /// direction they happened to read first.
    [<Literal>]
    let LogicTreeNamespace = "fuaran.program"

    [<Literal>]
    let LogicTreeKind = "logic-tree"

    /// The rendered slot identity this specification uses.
    let logicTreeSlot: string = LogicTreeNamespace + "/" + LogicTreeKind

    let encodeLogicTreeRef (reference: LogicTreeRef) : JVal =
        Canon.typed "LogicTreeRef" [ "ref", JStr reference.Ref; "slot", JStr logicTreeSlot ]

    let decodeLogicTreeRef (value: JVal) : Result<LogicTreeRef, WireRefusal> =
        declaredOnly [ "$type"; "ref"; "slot" ] value
        |> Result.bind (fun () -> requireString "slot" value)
        |> Result.bind (fun slot ->
            if slot <> logicTreeSlot then
                refuse RefusalClass.UnknownSlot ("slot '" + slot + "' is not a registered reference slot")
            else
                requireString "ref" value)
        |> Result.bind (fun reference ->
            if reference = "" then
                refuse RefusalClass.MissingMember "the reference is empty"
            elif reference.Length > 256 then
                refuse RefusalClass.NameTooLong "the reference exceeds 256 characters"
            else
                Ok { Ref = reference })

    // ─── the invocation record ───────────────────────────────────────────────

    /// The specification's cap on an idempotency key. An unbounded key is an
    /// unbounded index entry on an untrusted input.
    [<Literal>]
    let MaxIdempotencyKeyLength = 128

    let encodeInvocation (invocation: InvocationRecord) : JVal =
        Canon.typed
            "Invocation"
            ([ "endpoint", JStr invocation.Endpoint; "nodeId", JStr invocation.NodeId ]
             @ (invocation.IdempotencyKey
                |> Option.map (fun key -> [ "idempotencyKey", JStr key ])
                |> Option.defaultValue []))

    let decodeInvocation (value: JVal) : Result<InvocationRecord, WireRefusal> =
        declaredOnly [ "$type"; "endpoint"; "idempotencyKey"; "nodeId" ] value
        |> Result.bind (fun () -> requireString "endpoint" value)
        |> Result.bind (fun endpoint ->
            requireString "nodeId" value
            |> Result.bind (fun nodeId ->
                // Present-and-empty is a DIFFERENT claim from absent: only
                // omission means "this caller is not asking for deduplication",
                // so an empty key is refused rather than read as absence.
                match tryMember "idempotencyKey" value with
                | None ->
                    Ok
                        { Endpoint = endpoint
                          IdempotencyKey = None
                          NodeId = nodeId }
                | Some(JStr "") -> refuse RefusalClass.EmptyIdempotencyKey "the idempotency key is present and empty"
                | Some(JStr key) when key.Length > MaxIdempotencyKeyLength ->
                    refuse
                        RefusalClass.IdempotencyKeyTooLong
                        ("the idempotency key exceeds " + string MaxIdempotencyKeyLength + " characters")
                | Some(JStr key) ->
                    Ok
                        { Endpoint = endpoint
                          IdempotencyKey = Some key
                          NodeId = nodeId }
                | Some _ -> refuse RefusalClass.MissingMember "member 'idempotencyKey' is not a string"))

    // ─── the client-effect vocabulary ────────────────────────────────────────

    /// Encoded by the SHIPPED emitter, never re-spelled here.
    ///
    /// That emitter is the reason this family is the specification's one
    /// envelope exception — a `kind` discriminator, declaration-ordered members,
    /// short control escapes — and calling it is what keeps this codec on the
    /// right side of that: the exception is pinned by the corpus against the
    /// bytes something actually ships, not against a second implementation of
    /// them here.
    let encodeClientEffect (effect: ClientEffect) : string = ClientEffect.encode effect

    /// The reader the shipped emitter never had. A rendering surface decodes
    /// these in its own runtime; nothing on this side did, which is why a
    /// round-trip of this family was uncertifiable before the wire cut.
    let decodeClientEffect (value: JVal) : Result<ClientEffect, WireRefusal> =
        let kind =
            match tryString "kind" value with
            | Some k -> Ok k
            | None -> refuse RefusalClass.MissingMember "required member 'kind' is absent"

        kind
        |> Result.bind (fun kind ->
            let one name ctor =
                declaredOnly [ "kind"; name ] value
                |> Result.bind (fun () -> requireString name value)
                |> Result.map ctor

            match kind with
            | "Navigate" -> one "route" ClientEffect.Navigate
            | "PushState" -> one "route" ClientEffect.PushState
            | "WriteToClipboard" -> one "text" ClientEffect.WriteToClipboard
            | "Focus" -> one "nodeId" ClientEffect.Focus
            | "Download" ->
                declaredOnly [ "kind"; "url"; "name" ] value
                |> Result.bind (fun () -> requireString "url" value)
                |> Result.bind (fun url -> requireString "name" value |> Result.map (fun name -> url, name))
                |> Result.map ClientEffect.Download
            | "ReadFileBody" ->
                declaredOnly [ "kind"; "nodeId"; "encoding" ] value
                |> Result.bind (fun () -> requireString "nodeId" value)
                |> Result.bind (fun nodeId ->
                    requireString "encoding" value
                    |> Result.bind (fun encoding ->
                        if List.contains encoding [ "Text"; "Base64"; "DataUrl" ] then
                            Ok(nodeId, encoding)
                        else
                            refuse
                                RefusalClass.UndeclaredMember
                                ("encoding '" + encoding + "' is not one of the three")))
                |> Result.map ClientEffect.ReadFileBody
            | other -> refuse RefusalClass.UnknownEffectArm ("'" + other + "' is not an arm of the closed vocabulary"))
