namespace Fuaran.Program.Bounded

open Fuaran.Core
open Fuaran.Compute

// ============================================================================
//  The program wire — the shared half.
//
//  This file is the placement-neutral part of the codec for the program wire
//  specification: the canonical-JSON discipline, the refusal vocabulary, the
//  three REFERENCED positions the specification does not respecify, the
//  cross-layer reference, the invocation record, and the replay classification
//  of an action. The referenced vocabularies themselves — the action, the
//  tree-op, the client effect — are a DOMAIN's, and reach this file only
//  through its witness's codecs (DECISIONS.md D18, K6).
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
//     is why `decodeAction` asks the witness's own action decoder rather than
//     reimplementing an action reader.
//
//  3. THE REPLAY CLASSIFICATION READS THE VIEW (since Phase 1896, D18). It read
//     the encoded document's tags until then — `Call`, `Chain`/`ops`,
//     `SetState`/`valueFrom` — which are one domain's spellings; the view is
//     the same four-way distinction without them. For every action an encoder
//     can produce the two agree: a `Chain` whose `ops` is not an array cannot
//     be encoded, and the classification only ever ran on encoded output. An
//     op is classified by the witness's `AbsoluteTarget`, and one that cannot
//     be encoded at all is its own defect.
// ============================================================================

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

    [<Literal>]
    let MalformedContentAddress = "malformed-content-address"

    /// The one class in Appendix A that no single document can exhibit: it needs
    /// a pinned reference AND the document published under its identifier, and
    /// the second is not in this corpus. Declared here anyway, because the
    /// consumer that holds both is exactly who the rule is for — and a class
    /// spelled at a call site rather than here is a string nothing checks.
    [<Literal>]
    let ContentAddressMismatch = "content-address-mismatch"

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
/// Three arms are PROOFS that a re-run reaches outside; the other four are places
/// the walk cannot decide. The split is not cosmetic — it is what `gradeOfDefect`
/// reads, and it is why `unknown` is never rounded to a neighbour.
///
/// One arm, `StagedQuery`, is not read off the declared form alone: it is the
/// declared form read under the HOST's declaration of its query evaluator
/// (Phase 2187, DECISIONS.md D44). It is still a derived fact — the host's
/// declaration is an input to the walk, never a string a document supplied.
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
    /// A read the host answers through an evaluator it could not declare a pure
    /// read: the query is staged like a host call (D34), so a re-run asks
    /// something this host does not own a second time.
    | StagedQuery

/// One reason, positioned.
///
/// The stage is an ORDINAL, not a name. A stage carries no identifier of its
/// own, and a position is the only thing that addresses one without echoing a
/// string the document chose — which is the same rule as the vocabulary above,
/// applied to the locator rather than to the finding.
type ReplayReason = { Stage: int; Defect: ReplayDefect }

/// A composition surface naming a program by identifier, optionally
/// content-addressed. A bounded reference and an optional address, and nothing
/// else — the opaqueness is enforced by what this record OMITS, which is why the
/// decoder refuses an undeclared member rather than consulting a list of
/// forbidden ones.
///
/// `Hash` is the content address of the demand projection published under `Ref`.
/// `None` means UNPINNED — the posture every reference took before the member
/// existed — and is a different fact from "any document will do": a reference
/// that declined to say has not said anything. Nothing in this package resolves
/// a published projection, so nothing here can check the address against one;
/// what this codec owes is that the value is well formed and that its absence
/// survives a round trip as an absence.
type LogicTreeRef = { Ref: string; Hash: string option }

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
    ///
    /// This check reads the REFERENCED subject's spelling (`Call` carrying
    /// `into`), so on its own it decides the rule at one subject only. The
    /// subject-independent half is `refuseViewedResultTarget` below, which
    /// reads the target through the witness's view after decoding (D38).
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
    let encodeAction
        (witness: ProgramWitness<'Node, 'Op, 'Walk, DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>>)
        (action: 'Action)
        : JVal =
        witness.Dispatch.Action.Encode action

    /// The same refusal read through the SUBJECT'S OWN declared reading
    /// (fuaran#2019, D38): a call the witness's view says declares a result
    /// target, at any depth the view unfolds. This is what makes §9.5 hold at
    /// every subject rather than only at the one whose spelling the document
    /// check above knows — the toy's call declares its target in `targeted`,
    /// not in `into`, and before this its handler documents decoded and only
    /// the fold refused them. It runs on the DECODED action, so nothing is
    /// constructed past the decoder and nothing runs; the view is the one the
    /// fold refuses through, so the codec and the fold cannot disagree about
    /// what a declared target is. A leaf is not decomposed, which is why the
    /// document check stays beside it: it reaches positions a domain views as
    /// a leaf (D38).
    let rec private refuseViewedResultTarget
        (action: ActionWitness<'Action, 'Expr, 'Store, 'Effect>)
        (value: 'Action)
        : Result<unit, WireRefusal> =
        match action.View value with
        | ActionView.Call(_, true) ->
            refuse
                RefusalClass.TreeDeclaredResultTarget
                "the call declares a result target; a handler declares where its own results land"
        | ActionView.Sequence members -> members |> traverse (refuseViewedResultTarget action) |> Result.map ignore
        | ActionView.Choose(_, whenTrue, whenFalse, _) ->
            refuseViewedResultTarget action whenTrue
            |> Result.bind (fun () -> refuseViewedResultTarget action whenFalse)
        | ActionView.Repeat(_, body)
        | ActionView.Each(_, _, body) -> refuseViewedResultTarget action body
        | ActionView.Call(_, false)
        | ActionView.Assign _
        | ActionView.Require _
        | ActionView.Leaf _ -> Ok()

    /// `decodeAction` over the action witness alone (Phase 1982, H1), for a
    /// path that reads its dispatch position through `IDispatchPosition` and
    /// holds the fold's action witness rather than the whole dispatch axis.
    let decodeActionIn
        (action: ActionWitness<'Action, 'Expr, 'Store, 'Effect>)
        (value: JVal)
        : Result<'Action, WireRefusal> =
        refuseResultTarget value
        |> Result.bind (fun () -> action.Decode value)
        |> Result.bind (fun decoded -> refuseViewedResultTarget action decoded |> Result.map (fun () -> decoded))

    let decodeAction
        (witness: ProgramWitness<'Node, 'Op, 'Walk, DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>>)
        (value: JVal)
        : Result<'Action, WireRefusal> =
        decodeActionIn witness.Dispatch.Action value

    let encodeOp (witness: ProgramWitness<'Node, 'Op, 'Walk, 'Dispatch>) (op: 'Op) : Result<JVal, WireRefusal> =
        match Json.parse (witness.State.Stream.Encode op) with
        | Ok value -> Ok value
        | Error message -> refuse RefusalClass.MalformedReferencedValue message

    let decodeOp (witness: ProgramWitness<'Node, 'Op, 'Walk, 'Dispatch>) (value: JVal) : Result<'Op, WireRefusal> =
        match witness.State.Stream.Decode(Canon.render value) with
        | Ok op -> Ok op
        | Error code -> refuse RefusalClass.MalformedReferencedValue ("the op does not decode: " + code)

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
    /// The three outward-reaching arms are the only PROOFS; everything else is a
    /// place the walk could not decide, and the specification is explicit that
    /// such a place is reported as undecided rather than rounded to either
    /// neighbour. A classification that fired on ordinary correct handlers would
    /// be one people learn to scroll past.
    let gradeOfDefect (defect: ReplayDefect) : ReplaySafety =
        match defect with
        | ReplayDefect.OpaqueHostCall
        | ReplayDefect.OutboundNotification
        | ReplayDefect.StagedQuery -> ReplaySafety.Unsafe
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

    /// The defects of an action, read through the witness's view.
    ///
    ///   Call      — inert inside a handler stage, so re-running it changes
    ///               nothing. That is not a property of the action; it is D7's
    ///               deliberate boundary, and the classification reads it.
    ///   Sequence  — the distinct union of the defects of its parts.
    ///   Assign    — a literal write is re-runnable; one taking its value from
    ///               an expression is resolved at dispatch against a store that
    ///               has moved, so it is undecidable rather than unsafe.
    ///   Require   — a guard is resolved at dispatch against a store that has
    ///               moved, exactly as a derived write is, and whether it holds
    ///               on a re-run is not decidable from the declared form: it
    ///               is undecidable, and reported as such (Phase 1967).
    ///   Choose    — its entry condition is resolved at dispatch against a
    ///               store that has moved, exactly as a guard's is, so which
    ///               arm re-runs is undecidable from the declared form; the
    ///               distinct union of both arms' defects beside it, because
    ///               either arm may be the one that re-runs (Phase 1976).
    ///   Repeat    — a literal bound re-runs the body the same number of
    ///               times, so its defects are the body's; a parameter bound
    ///               is resolved at dispatch, which is undecidable, beside the
    ///               body's (Phase 1976).
    ///   Each      — a literal collection re-runs the same lowered elements,
    ///               so its defects are the distinct union of theirs — the
    ///               body with each element substituted (Phase 1990); a
    ///               store-bound collection is resolved at dispatch, which is
    ///               undecidable, beside the body's (Phase 1991).
    ///   Leaf      — undecidable, and reported as such.
    ///
    /// `replayDefectsOfActionIn` is the same walk over the action witness alone
    /// (Phase 1982, H1); `replayDefectsOfAction` is it at a full dispatch axis.
    let rec replayDefectsOfActionIn
        (witness: ActionWitness<'Action, 'Expr, 'Store, 'Effect>)
        (action: 'Action)
        : ReplayDefect list =
        match witness.View action with
        | ActionView.Call _ -> []
        | ActionView.Sequence items -> items |> List.collect (replayDefectsOfActionIn witness) |> List.distinct
        | ActionView.Assign(_, _, Some _) -> [ ReplayDefect.NonLiteralWrite ]
        | ActionView.Assign(_, _, None) -> []
        | ActionView.Require _ -> [ ReplayDefect.UndecidableAction ]
        | ActionView.Choose(_, whenTrue, whenFalse, _) ->
            ReplayDefect.UndecidableAction
            :: (replayDefectsOfActionIn witness whenTrue
                @ replayDefectsOfActionIn witness whenFalse)
            |> List.distinct
        | ActionView.Repeat(Bound.Literal _, body) -> replayDefectsOfActionIn witness body
        | ActionView.Repeat(Bound.Parameter _, body) ->
            ReplayDefect.UndecidableAction :: replayDefectsOfActionIn witness body
            |> List.distinct
        | ActionView.Each(Collection.Literal elements, placeholder, body) ->
            ActionWitness.lowered witness elements placeholder body
            |> List.collect (replayDefectsOfActionIn witness)
            |> List.distinct
        // A store-bound collection (Phase 1991) is resolved at dispatch, as a
        // parameter bound is — undecidable from the declared form — beside
        // the body's own defects, read once with the placeholder standing.
        | ActionView.Each(Collection.Stored _, _, body) ->
            ReplayDefect.UndecidableAction :: replayDefectsOfActionIn witness body
            |> List.distinct
        | ActionView.Leaf _ -> [ ReplayDefect.UndecidableAction ]

    let replayDefectsOfAction
        (witness: ProgramWitness<'Node, 'Op, 'Walk, DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>>)
        (action: 'Action)
        : ReplayDefect list =
        replayDefectsOfActionIn witness.Dispatch.Action action

    /// The defects of an op: one that names its target absolutely re-runs
    /// against the same node; one addressed relative to where a previous op
    /// left things does not; and one the op codec cannot encode cannot be
    /// classified at all.
    let replayDefectsOfOp (witness: ProgramWitness<'Node, 'Op, 'Walk, 'Dispatch>) (op: 'Op) : ReplayDefect list =
        let ofOne (op: 'Op) : ReplayDefect list =
            match encodeOp witness op with
            | Error _ -> [ ReplayDefect.UnencodableOp ]
            | Ok _ ->
                match witness.State.AbsoluteTarget op with
                | Some target when target <> "" -> []
                | _ -> [ ReplayDefect.RelativeAddressing ]

        match witness.State.View op with
        | OpView.Edit
        | OpView.Require -> ofOne op
        // A branch, a repeat (Phase 1976) or a per-element iteration (Phase
        // 1990) re-runs through the ops beneath it, so its defects are the
        // distinct union of theirs — both arms, an untaken arm included, since
        // which arm re-runs is decided against a state that has moved; an
        // `Each`'s lowered elements — beside whether the op itself encodes.
        | OpView.Choose _
        | OpView.Repeat _
        | OpView.Each _
        | OpView.Let _ ->
            let below = OpView.beneath witness.State.View witness.State.Substitute op

            // A value the state holds (Phase 2186, D42) — this op, or one
            // beneath it — is resolved at dispatch against a state that has
            // moved, exactly as an `Assign`'s `from` is: `non-literal-write`,
            // undecidable rather than unsafe, beside the body's own defects,
            // read once with the placeholder standing.
            let bindings =
                op :: below
                |> List.collect (fun o ->
                    match witness.State.View o with
                    | OpView.Let _ -> [ ReplayDefect.NonLiteralWrite ]
                    | _ -> [])

            (match encodeOp witness op with
             | Error _ -> [ ReplayDefect.UnencodableOp ]
             | Ok _ -> [])
            @ bindings
            @ (below |> List.collect ofOne)
            |> List.distinct

    let replaySafetyOfAction
        (witness: ProgramWitness<'Node, 'Op, 'Walk, DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>>)
        (action: 'Action)
        : ReplaySafety =
        replayDefectsOfAction witness action |> verdictOfDefects

    let replaySafetyOfOp (witness: ProgramWitness<'Node, 'Op, 'Walk, 'Dispatch>) (op: 'Op) : ReplaySafety =
        replayDefectsOfOp witness op |> verdictOfDefects

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
        | ReplayDefect.StagedQuery -> "staged-query"

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

    /// The content address prefix, and the digest length behind it. The
    /// algorithm rides IN the value rather than being agreed out of band, so a
    /// later digest is a value change with a visible name rather than a silent
    /// reinterpretation of the same 64 characters.
    [<Literal>]
    let ContentAddressPrefix = "sha256:"

    [<Literal>]
    let ContentAddressDigestLength = 64

    /// Whether a content address is well formed: the prefix, then exactly 64
    /// LOWER-CASE hex digits.
    ///
    /// The case rule is part of the value and not a nicety. An address is
    /// compared for equality against one a consumer recomputed, and two
    /// spellings of one digest compare unequal — so admitting both would make a
    /// pinned reference fail against the very document it addresses, which is
    /// the one failure mode a pin exists to rule out.
    let isContentAddress (value: string) : bool =
        value.StartsWith ContentAddressPrefix
        && value.Length = ContentAddressPrefix.Length + ContentAddressDigestLength
        && value
           |> Seq.skip ContentAddressPrefix.Length
           |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    let encodeLogicTreeRef (reference: LogicTreeRef) : JVal =
        // OMITTED when absent, never nulled: there is no null on this wire, and
        // an always-emitted member would also change the bytes — and therefore
        // the address — of every reference ever written without one.
        Canon.typed
            "LogicTreeRef"
            ([ "ref", JStr reference.Ref; "slot", JStr logicTreeSlot ]
             @ (reference.Hash
                |> Option.map (fun h -> [ "hash", JStr h ])
                |> Option.defaultValue []))

    let decodeLogicTreeRef (value: JVal) : Result<LogicTreeRef, WireRefusal> =
        declaredOnly [ "$type"; "hash"; "ref"; "slot" ] value
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
                Ok reference)
        |> Result.bind (fun reference ->
            // Absent is a posture; present-and-unreadable is a broken record.
            // Collapsing the second into the first would let a malformed value
            // buy the unpinned treatment, which is the reading that makes
            // pinning worth defeating.
            match tryString "hash" value with
            | None ->
                match tryMember "hash" value with
                | Some _ -> refuse RefusalClass.MalformedContentAddress "member 'hash' is not a string"
                | None -> Ok { Ref = reference; Hash = None }
            | Some address when isContentAddress address -> Ok { Ref = reference; Hash = Some address }
            | Some _ ->
                refuse
                    RefusalClass.MalformedContentAddress
                    ("member 'hash' is not '"
                     + ContentAddressPrefix
                     + "' followed by "
                     + string ContentAddressDigestLength
                     + " lower-case hex digits"))

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

    /// Encoded by the witness's SHIPPED emitter, never re-spelled here.
    ///
    /// That emitter is the reason this family is the specification's one
    /// envelope exception — a `kind` discriminator, declaration-ordered members,
    /// short control escapes — and calling it is what keeps this codec on the
    /// right side of that: the exception is pinned by the corpus against the
    /// bytes something actually ships, not against a second implementation of
    /// them here.
    let encodeClientEffect
        (witness: ProgramWitness<'Node, 'Op, 'Walk, DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>>)
        (effect: 'Effect)
        : string =
        witness.Dispatch.Effect.Encode effect

    /// The witness's reader for the same family.
    let decodeClientEffect
        (witness: ProgramWitness<'Node, 'Op, 'Walk, DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>>)
        (value: JVal)
        : Result<'Effect, WireRefusal> =
        witness.Dispatch.Effect.Decode value
