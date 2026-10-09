namespace Fuaran.Program.Server

open Fuaran.Core
open Fuaran.Program.Bounded

// ============================================================================
//  The program wire — this placement's half.
//
//  The handler declared form, the server-effect vocabulary and the outcome
//  report, as documents. The shared half — the canonical discipline, the
//  refusal vocabulary, the referenced positions and the replay classification
//  of an action — is `ProgramWire`, and everything below reaches it rather than
//  re-deriving any of it.
//
//  ── What the cut changed, and what it did not ───────────────────────────────
//  It changed no algorithm. A handler still runs exactly as it ran before: two
//  phases, one atomicity unit, one staged arm. What it changed is that a
//  handler can now ARRIVE from somewhere, and the consequence is a rule about
//  what this code is allowed to SAY rather than about what it does — every
//  string in a wire-carried handler is attacker-chosen, so a diagnostic carries
//  the derived capability and never a name the document supplied.
//
//  The compensation for that thinness is here too: the declared form is
//  CHECKABLE before it runs. The vocabularies are closed, the landing slot is
//  checked at decode, an undeclared member is refused rather than ignored, and
//  a handler's replay safety is derived from the document itself.
//
//  ── One structural property to preserve ─────────────────────────────────────
//  This package matches on an `Action` NOWHERE — the shared fold is the one
//  place this domain interprets one, and a test greps for it. So the two places
//  this file would naturally have reached for an action arm both go through
//  `ProgramWire` instead: decoding a compute stage's action (through the tree
//  codec's own reader), and classifying one for replay (over the encoded
//  document). Neither is a workaround; both are the same rule this repository
//  already applies to a foreign vocabulary.
// ============================================================================

/// What one handler run produced, as a document.
///
/// It is deliberately NOT `HandlerOutcome`: that carries the store the handler
/// ran against, which is this host's own state and not something a caller is
/// told. The report is the projection a caller receives, and `ofOutcome` is the
/// only way to build one from a run.
type HandlerReport<'Op> =
    {
        Committed: bool
        Diagnostics: ServerDiagnostic list
        Notifications: (string * JVal) list
        Patches: 'Op list
        /// EXECUTION order, not stage order — staging defers every host call to
        /// the perform phase, so one declared first appears after the
        /// capabilities the plan phase ran.
        Performed: string list
    }

module HandlerReport =

    /// The projection of a run. Everything it drops is state the caller does not
    /// receive; everything it keeps is what the specification's outcome document
    /// declares.
    let ofOutcome (outcome: HandlerOutcome<'Node, 'Store, 'Op, 'Effect>) : HandlerReport<'Op> =
        { Committed = outcome.Committed
          Diagnostics = outcome.Diagnostics
          Notifications = outcome.Notifications
          Patches = outcome.Patches
          Performed = outcome.Performed }

module HandlerWire =

    // Every codec here runs under EVERY composition (Phase 1982, H1): it reads
    // its dispatch position through `IDispatchPosition`, as the handler loop
    // and the demand walk already do, so a domain that fills only the state
    // axis encodes and decodes its handlers as Program documents. The fold is
    // read for exactly two things — a compute stage's action codec, which a
    // composition without one cannot hold, and the host-reserved namespace a
    // landing slot is checked against, which a composition without a binding
    // channel does not have.
    let private foldOf
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        : DispatchFold<'Action, 'Expr, 'Store, 'Effect> option =
        (witness.Dispatch :> IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>).Fold

    // ─── the server-effect vocabulary ────────────────────────────────────────

    let encodeEffect
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (effect: ServerEffect<'Op>)
        : Result<JVal, WireRefusal> =
        match effect with
        | ServerEffect.RunQuery(name, source, pipeline) ->
            ProgramWire.encodePipeline pipeline
            |> Result.map (fun steps ->
                Canon.typed
                    "RunQuery"
                    [ "name", JStr name
                      "pipeline", steps
                      "source", ProgramWire.encodeSource source ])

        | ServerEffect.ApplyOps ops ->
            ops
            |> ProgramWire.traverse (ProgramWire.encodeOp witness)
            |> Result.map (fun encoded -> Canon.typed "ApplyOps" [ "ops", JArr encoded ])

        | ServerEffect.EmitPatch ops ->
            ops
            |> ProgramWire.traverse (ProgramWire.encodeOp witness)
            |> Result.map (fun encoded -> Canon.typed "EmitPatch" [ "ops", JArr encoded ])

        | ServerEffect.HostCall(fn, args, into) ->
            // The capability is NOT emitted. It is derived — `host:` + fn — and
            // a document that carried it would put the untrusted side in charge
            // of a fact this host computes.
            Ok(
                Canon.typed
                    "HostCall"
                    ([ "args", args; "fn", JStr fn ]
                     @ (into |> Option.map (fun slot -> [ "into", JStr slot ]) |> Option.defaultValue []))
            )

        | ServerEffect.Notify(channel, payload) ->
            Ok(Canon.typed "Notify" [ "channel", JStr channel; "payload", payload ])

    let decodeEffect
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (value: JVal)
        : Result<ServerEffect<'Op>, WireRefusal> =
        match ProgramWire.tag value with
        | None -> ProgramWire.refuse RefusalClass.MissingMember "required member '$type' is absent"

        | Some "RunQuery" ->
            ProgramWire.declaredOnly [ "$type"; "name"; "pipeline"; "source" ] value
            |> Result.bind (fun () -> ProgramWire.requireString "name" value)
            |> Result.bind (fun name ->
                ProgramWire.requireMember "source" value
                |> Result.bind ProgramWire.decodeSource
                |> Result.bind (fun source ->
                    ProgramWire.requireMember "pipeline" value
                    |> Result.bind ProgramWire.decodePipeline
                    |> Result.map (fun pipeline -> ServerEffect.RunQuery(name, source, pipeline))))

        | Some "ApplyOps" ->
            ProgramWire.declaredOnly [ "$type"; "ops" ] value
            |> Result.bind (fun () -> ProgramWire.requireArray "ops" value)
            |> Result.bind (ProgramWire.traverse (ProgramWire.decodeOp witness))
            |> Result.map ServerEffect.ApplyOps

        | Some "EmitPatch" ->
            ProgramWire.declaredOnly [ "$type"; "ops" ] value
            |> Result.bind (fun () -> ProgramWire.requireArray "ops" value)
            |> Result.bind (ProgramWire.traverse (ProgramWire.decodeOp witness))
            |> Result.map ServerEffect.EmitPatch

        | Some "HostCall" ->
            ProgramWire.declaredOnly [ "$type"; "args"; "fn"; "into" ] value
            |> Result.bind (fun () -> ProgramWire.requireString "fn" value)
            |> Result.bind (fun fn ->
                if fn = "" then
                    ProgramWire.refuse RefusalClass.MissingMember "the host function name is empty"
                else
                    ProgramWire.requireMember "args" value |> Result.map (fun args -> fn, args))
            |> Result.bind (fun (fn, args) ->
                // The host-reserved namespace is closed at DECODE, which is one
                // step earlier than the interpreter closes it at PLAN. Both are
                // before anything external runs; refusing here means the
                // document never becomes a handler at all.
                //
                // A composition with no dispatch axis has no binding channel and
                // so no reserved namespace: its slot decodes, and the handler
                // refuses it while planning (`Handler.NoBindingChannel`), which is
                // still before anything external runs.
                match ProgramWire.tryMember "into" value, foldOf witness with
                | None, _ -> Ok(ServerEffect.HostCall(fn, args, None))
                | Some(JStr slot), Some fold when fold.Store.IsReserved slot ->
                    ProgramWire.refuse
                        RefusalClass.HostReservedLandingSlot
                        ("the landing slot is under the host-reserved '"
                         + fold.Store.ReservedPrefix
                         + "' namespace")
                | Some(JStr slot), _ -> Ok(ServerEffect.HostCall(fn, args, Some slot))
                | Some _, _ -> ProgramWire.refuse RefusalClass.MissingMember "member 'into' is not a string")

        | Some "Notify" ->
            ProgramWire.declaredOnly [ "$type"; "channel"; "payload" ] value
            |> Result.bind (fun () -> ProgramWire.requireString "channel" value)
            |> Result.bind (fun channel ->
                ProgramWire.requireMember "payload" value
                |> Result.map (fun payload -> ServerEffect.Notify(channel, payload)))

        | Some other ->
            ProgramWire.refuse
                RefusalClass.UnknownEffectArm
                ("'" + other + "' is not an arm of the closed server-effect vocabulary")

    // ─── the handler declared form ───────────────────────────────────────────

    /// The specification's cap on a registration key.
    [<Literal>]
    let MaxHandlerNameLength = 256

    let encodeStage
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (stage: HandlerStage<'Action, 'Op>)
        : Result<JVal, WireRefusal> =
        match stage with
        // An action exists only at a composition that fills the dispatch axis,
        // so the fold is there whenever this arm is reached.
        | Compute action ->
            Ok(Canon.typed "Compute" [ "action", (DispatchPosition.fold witness.Dispatch).Action.Encode action ])
        | Effect effect ->
            encodeEffect witness effect
            |> Result.map (fun e -> Canon.typed "Effect" [ "effect", e ])

    let decodeStage
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (value: JVal)
        : Result<HandlerStage<'Action, 'Op>, WireRefusal> =
        match ProgramWire.tag value with
        | Some "Compute" ->
            ProgramWire.declaredOnly [ "$type"; "action" ] value
            |> Result.bind (fun () -> ProgramWire.requireMember "action" value)
            |> Result.bind (fun action ->
                match foldOf witness with
                | Some fold -> ProgramWire.decodeActionIn fold.Action action
                // No dispatch axis, no action vocabulary: the referenced value
                // has nothing to decode it, which is the referenced-value
                // refusal the specification already names.
                | None ->
                    ProgramWire.refuse
                        RefusalClass.MalformedReferencedValue
                        "this composition fills no dispatch axis, so it holds no compute stage")
            |> Result.map Compute
        | Some "Effect" ->
            ProgramWire.declaredOnly [ "$type"; "effect" ] value
            |> Result.bind (fun () -> ProgramWire.requireMember "effect" value)
            |> Result.bind (decodeEffect witness)
            |> Result.map Effect
        | Some other ->
            ProgramWire.refuse
                RefusalClass.UnknownStageKind
                ("'"
                 + other
                 + "' is not a stage kind; there are exactly two, and sequencing is not a third")
        | None -> ProgramWire.refuse RefusalClass.MissingMember "a stage carries no '$type'"

    let encodeHandlerJson
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handler: Handler<'Action, 'Op>)
        : Result<JVal, WireRefusal> =
        handler.Stages
        |> ProgramWire.traverse (encodeStage witness)
        |> Result.map (fun stages -> Canon.typed "Handler" [ "name", JStr handler.Name; "stages", JArr stages ])

    let encodeHandler
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handler: Handler<'Action, 'Op>)
        : Result<string, WireRefusal> =
        encodeHandlerJson witness handler |> Result.map ProgramWire.render

    /// The content address of a handler's document (Phase 1982, H1): the
    /// specification's `sha256:` form over the canonical encoding, so the
    /// same handler addresses the same on every runtime. What a signed
    /// envelope over a handler registration binds the approval to
    /// (`SignedEnvelope.signAddressed`) when there is no tree to hash.
    let contentAddress
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handler: Handler<'Action, 'Op>)
        : Result<string, WireRefusal> =
        encodeHandler witness handler
        |> Result.map (fun document -> ProgramWire.ContentAddressPrefix + Hash.sha256Hex document)

    let decodeHandlerJson
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (value: JVal)
        : Result<Handler<'Action, 'Op>, WireRefusal> =
        ProgramWire.declaredOnly [ "$type"; "name"; "stages" ] value
        |> Result.bind (fun () ->
            match ProgramWire.tag value with
            | Some "Handler" -> Ok()
            | Some other -> ProgramWire.refuse RefusalClass.MissingMember ("'" + other + "' is not a handler document")
            | None -> ProgramWire.refuse RefusalClass.MissingMember "required member '$type' is absent")
        |> Result.bind (fun () -> ProgramWire.requireString "name" value)
        |> Result.bind (fun name ->
            // Empty and absent are different claims and are refused on different
            // classes: one says the document forgot the key, the other says it
            // supplied one that cannot address anything.
            if name = "" then
                ProgramWire.refuse RefusalClass.EmptyName "the registration key is empty"
            elif name.Length > MaxHandlerNameLength then
                ProgramWire.refuse
                    RefusalClass.NameTooLong
                    ("the registration key exceeds " + string MaxHandlerNameLength + " characters")
            else
                Ok name)
        |> Result.bind (fun name ->
            ProgramWire.requireArray "stages" value
            |> Result.bind (ProgramWire.traverse (decodeStage witness))
            |> Result.map (fun stages -> { Name = name; Stages = stages }))

    let decodeHandler
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (json: string)
        : Result<Handler<'Action, 'Op>, WireRefusal> =
        ProgramWire.parseDocument json |> Result.bind (decodeHandlerJson witness)

    // ─── the derived replay classification ───────────────────────────────────

    /// WHY a handler is not provably re-runnable, in stage order — the
    /// primitive from which the classification below is derived.
    ///
    /// A verdict alone says a handler cannot be resumed; it does not say what to
    /// change. These do, and they do it without naming anything the document
    /// supplied: a closed defect vocabulary and a stage ORDINAL, so a reason is
    /// as log-safe as the verdict it explains.
    ///
    /// Reasons within one stage are DISTINCT — an `ApplyOps` carrying nine
    /// relatively-addressed ops is one fact about that stage, not nine — while
    /// two stages with the same defect keep both entries, because they are two
    /// places to go and look.
    ///
    /// The action half runs over the ENCODED action rather than over the action
    /// value, which is what keeps this package free of a second `Action` match
    /// AND keeps this rule literally identical to the one the conformance
    /// corpus's own emitter applies.
    ///
    /// `query` is the HOST's declaration of its query evaluator
    /// (`ServerEffectRegistry.queryPosture`) — the one input that is not the
    /// declared form (D44). A read answered by an evaluator the host could not
    /// declare a pure read is staged like a host call (D34), and the durable
    /// interpreter journals it as one, so it reports `staged-query`; under a
    /// pure read, and under the in-memory fold, a read reports nothing, as it
    /// always has. A required parameter rather than a default, so a host
    /// cannot publish a posture without saying which evaluator it runs.
    let replayReasons
        (query: QueryPosture)
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handler: Handler<'Action, 'Op>)
        : ReplayReason list =
        let ofEffect effect =
            match effect with
            | ServerEffect.RunQuery _ ->
                match query with
                | QueryPosture.Reaching -> [ ReplayDefect.StagedQuery ]
                | QueryPosture.PureRead -> []
            | ServerEffect.ApplyOps ops
            | ServerEffect.EmitPatch ops -> ops |> List.collect (ProgramWire.replayDefectsOfOp witness)
            // Both reach outside: one commits somewhere this host does not own,
            // the other ships a message a second run would duplicate.
            | ServerEffect.HostCall _ -> [ ReplayDefect.OpaqueHostCall ]
            | ServerEffect.Notify _ -> [ ReplayDefect.OutboundNotification ]

        handler.Stages
        |> List.mapi (fun index stage ->
            let defects =
                match stage with
                | Effect effect -> ofEffect effect
                | Compute action ->
                    ProgramWire.replayDefectsOfActionIn (DispatchPosition.fold witness.Dispatch).Action action

            // Qualified: the projection document declares a record with the same
            // two field names, and its `Defect` is the wire TOKEN rather than
            // the value. The two are one conversion apart and inference cannot
            // be left to pick.
            defects
            |> List.distinct
            |> List.map (fun defect ->
                { ReplayReason.Stage = index
                  Defect = defect }))
        |> List.concat

    /// A handler's replay safety, DERIVED from its declared form.
    ///
    /// It is never declared beside the handler: a declaration is free to drift
    /// from the thing it describes, and this one need not exist at all.
    ///
    /// Derived from `replayReasons` rather than walked a second time. The two
    /// therefore cannot disagree by construction, which matters more than it
    /// looks: the corpus pins this function's answer per vector, so a reasons
    /// walk that drifted from it would be a set of explanations for a verdict
    /// nobody reached.
    let replaySafety
        (query: QueryPosture)
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (handler: Handler<'Action, 'Op>)
        : ReplaySafety =
        replayReasons query witness handler |> ProgramWire.verdictOfReasons

    // ─── the outcome report ──────────────────────────────────────────────────

    let private encodeBounded (diagnostic: BoundedDiagnostic) : JVal =
        match diagnostic with
        | BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, action) ->
            Canon.typed "UnsupportedOnBoundedPath" [ "action", JStr action; "nodeId", JStr nodeId ]
        | BoundedDiagnostic.Refused(nodeId, action, reason) ->
            Canon.typed "Refused" [ "action", JStr action; "nodeId", JStr nodeId; "reason", JStr reason ]

    let private decodeBounded (value: JVal) : Result<BoundedDiagnostic, WireRefusal> =
        match ProgramWire.tag value with
        | Some "UnsupportedOnBoundedPath" ->
            ProgramWire.declaredOnly [ "$type"; "action"; "nodeId" ] value
            |> Result.bind (fun () -> ProgramWire.requireString "action" value)
            |> Result.bind (fun action ->
                ProgramWire.requireString "nodeId" value
                |> Result.map (fun nodeId -> BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, action)))
        | Some "Refused" ->
            ProgramWire.declaredOnly [ "$type"; "action"; "nodeId"; "reason" ] value
            |> Result.bind (fun () -> ProgramWire.requireString "action" value)
            |> Result.bind (fun action ->
                ProgramWire.requireString "nodeId" value
                |> Result.bind (fun nodeId ->
                    ProgramWire.requireString "reason" value
                    |> Result.map (fun reason -> BoundedDiagnostic.Refused(nodeId, action, reason))))
        | _ -> ProgramWire.refuse RefusalClass.UnknownEffectArm "not an arm of the shared diagnostic vocabulary"

    let private encodeDenial (denial: ServerEffectDenial) : JVal =
        match denial with
        | ServerEffectDenial.Unregistered capability -> Canon.typed "Unregistered" [ "capability", JStr capability ]
        | ServerEffectDenial.GateRefused capability -> Canon.typed "GateRefused" [ "capability", JStr capability ]

    let private decodeDenial (value: JVal) : Result<ServerEffectDenial, WireRefusal> =
        ProgramWire.declaredOnly [ "$type"; "capability" ] value
        |> Result.bind (fun () -> ProgramWire.requireString "capability" value)
        |> Result.bind (fun capability ->
            match ProgramWire.tag value with
            | Some "Unregistered" -> Ok(ServerEffectDenial.Unregistered capability)
            | Some "GateRefused" -> Ok(ServerEffectDenial.GateRefused capability)
            | _ -> ProgramWire.refuse RefusalClass.UnknownEffectArm "not an arm of the denial vocabulary")

    let private encodeDiagnostic (diagnostic: ServerDiagnostic) : JVal =
        match diagnostic with
        | ServerDiagnostic.Bounded inner -> Canon.typed "Bounded" [ "diagnostic", encodeBounded inner ]
        | ServerDiagnostic.Denied denial -> Canon.typed "Denied" [ "denial", encodeDenial denial ]
        | ServerDiagnostic.Failed(capability, reason) ->
            Canon.typed "Failed" [ "capability", JStr capability; "reason", JStr reason ]
        | ServerDiagnostic.PerformFailed(capability, reason) ->
            Canon.typed "PerformFailed" [ "capability", JStr capability; "reason", JStr reason ]
        // It carries NO member, and that is the rule rather than an omission:
        // the endpoint it could not resolve is the one string that always comes
        // off the wire.
        | ServerDiagnostic.HandlerUnregistered -> Canon.typed "HandlerUnregistered" []

    let private decodeDiagnostic (value: JVal) : Result<ServerDiagnostic, WireRefusal> =
        match ProgramWire.tag value with
        | Some "Bounded" ->
            ProgramWire.declaredOnly [ "$type"; "diagnostic" ] value
            |> Result.bind (fun () -> ProgramWire.requireMember "diagnostic" value)
            |> Result.bind decodeBounded
            |> Result.map ServerDiagnostic.Bounded
        | Some "Denied" ->
            ProgramWire.declaredOnly [ "$type"; "denial" ] value
            |> Result.bind (fun () -> ProgramWire.requireMember "denial" value)
            |> Result.bind decodeDenial
            |> Result.map ServerDiagnostic.Denied
        | Some "Failed" ->
            ProgramWire.declaredOnly [ "$type"; "capability"; "reason" ] value
            |> Result.bind (fun () -> ProgramWire.requireString "capability" value)
            |> Result.bind (fun capability ->
                ProgramWire.requireString "reason" value
                |> Result.map (fun reason -> ServerDiagnostic.Failed(capability, reason)))
        | Some "PerformFailed" ->
            ProgramWire.declaredOnly [ "$type"; "capability"; "reason" ] value
            |> Result.bind (fun () -> ProgramWire.requireString "capability" value)
            |> Result.bind (fun capability ->
                ProgramWire.requireString "reason" value
                |> Result.map (fun reason -> ServerDiagnostic.PerformFailed(capability, reason)))
        | Some "HandlerUnregistered" ->
            // Its own class, not the generic undeclared-member one. A reader
            // told "there is an extra member here" would go looking for a schema
            // mistake; a reader told the endpoint was echoed knows a wire-carried
            // string reached a log, which is a different and worse problem.
            match value with
            | JObj [ _ ] -> Ok ServerDiagnostic.HandlerUnregistered
            | _ ->
                ProgramWire.refuse
                    RefusalClass.EndpointEchoed
                    "the unregistered-handler diagnostic carries a member; it names no endpoint"
        | Some other -> ProgramWire.refuse RefusalClass.UnknownEffectArm ("'" + other + "' is not a diagnostic arm")
        | None -> ProgramWire.refuse RefusalClass.MissingMember "a diagnostic carries no '$type'"

    let private encodeNotification (channel: string, payload: JVal) : JVal =
        JObj [ "channel", JStr channel; "payload", payload ]

    let private decodeNotification (value: JVal) : Result<string * JVal, WireRefusal> =
        ProgramWire.declaredOnly [ "channel"; "payload" ] value
        |> Result.bind (fun () -> ProgramWire.requireString "channel" value)
        |> Result.bind (fun channel ->
            ProgramWire.requireMember "payload" value
            |> Result.map (fun payload -> channel, payload))

    let encodeReportJson
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (report: HandlerReport<'Op>)
        : Result<JVal, WireRefusal> =
        report.Patches
        |> ProgramWire.traverse (ProgramWire.encodeOp witness)
        |> Result.map (fun patches ->
            Canon.typed
                "HandlerReport"
                [ "committed", JBool report.Committed
                  "diagnostics", JArr(report.Diagnostics |> List.map encodeDiagnostic)
                  "notifications", JArr(report.Notifications |> List.map encodeNotification)
                  "patches", JArr patches
                  "performed", JArr(report.Performed |> List.map JStr) ])

    let encodeReport
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (report: HandlerReport<'Op>)
        : Result<string, WireRefusal> =
        encodeReportJson witness report |> Result.map ProgramWire.render

    let decodeReportJson
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (value: JVal)
        : Result<HandlerReport<'Op>, WireRefusal> =
        // Every member is required even when empty: an omitted array and an
        // empty array would be two spellings of one fact, and a reader would
        // have to guess which producer it was talking to.
        ProgramWire.declaredOnly [ "$type"; "committed"; "diagnostics"; "notifications"; "patches"; "performed" ] value
        |> Result.bind (fun () -> ProgramWire.requireBool "committed" value)
        |> Result.bind (fun committed ->
            ProgramWire.requireArray "diagnostics" value
            |> Result.bind (ProgramWire.traverse decodeDiagnostic)
            |> Result.bind (fun diagnostics ->
                ProgramWire.requireArray "notifications" value
                |> Result.bind (ProgramWire.traverse decodeNotification)
                |> Result.bind (fun notifications ->
                    ProgramWire.requireArray "patches" value
                    |> Result.bind (ProgramWire.traverse (ProgramWire.decodeOp witness))
                    |> Result.bind (fun patches ->
                        ProgramWire.requireArray "performed" value
                        |> Result.bind (
                            ProgramWire.traverse (fun item ->
                                match item with
                                | JStr capability -> Ok capability
                                | _ ->
                                    ProgramWire.refuse
                                        RefusalClass.MissingMember
                                        "a performed entry is not a capability string")
                        )
                        |> Result.map (fun performed ->
                            { Committed = committed
                              Diagnostics = diagnostics
                              Notifications = notifications
                              Patches = patches
                              Performed = performed })))))
        |> Result.bind (fun report ->
            // The impossible pairing. Two-phase staging leaves exactly one case
            // in which an uncommitted run performed anything, and it is the case
            // a perform-phase failure names — so an uncommitted report claiming
            // work with nothing to explain it is not a document any conformant
            // run produces.
            let performFailed =
                report.Diagnostics
                |> List.exists (fun d ->
                    match d with
                    | ServerDiagnostic.PerformFailed _ -> true
                    | _ -> false)

            if not report.Committed && not (List.isEmpty report.Performed) && not performFailed then
                ProgramWire.refuse
                    RefusalClass.ImpossibleOutcome
                    "an uncommitted outcome reports work performed with no perform-phase failure to explain it"
            else
                Ok report)

    let decodeReport
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (json: string)
        : Result<HandlerReport<'Op>, WireRefusal> =
        ProgramWire.parseDocument json |> Result.bind (decodeReportJson witness)
