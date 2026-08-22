namespace Fuaran.Program.Server

open Fuaran.UI.Types
open Fuaran.Program.Bounded

// ============================================================================
//  The demanded projection's SERVER tier — what a handler registration can ever
//  ask of this host, answered once, before any event runs.
//
//  The client tier beside this (`Demanded.ofTree`) answers the same question of
//  a program tree, and it is exact there because a decoded tree carries no code.
//  It is exact HERE for a different and stronger reason: a handler is
//  host-registered data whose stage list is fixed before any untrusted tree
//  arrives, so the walk is not inferring a capability envelope — it is reading
//  one the host already wrote down.
//
//  ── What contributes, and what deliberately does not ────────────────────────
//  A handler's stages are of two kinds and each contributes to the tier it
//  belongs to:
//
//    Effect e    the SERVER tier. Its discriminator, the capability the gate
//                will be asked about, the host function it names, the channels
//                it reaches, and the state namespace a landing slot writes.
//
//    Compute a   the CLIENT tier, unchanged. The shared fold runs it and its
//                closure-free effects travel out to the connected client, so
//                what it demands is what the same action demands at any
//                placement — read through `Demanded.ofAction` rather than by
//                matching an action here, which is what keeps this package free
//                of a second evaluator (DECISIONS.md D1/D7, and a test greps
//                for it).
//
//  Two things a reader might expect are absent, and both for the reason
//  DECISIONS.md D9 gives for dropping the tree-declared result target — a demand
//  no host will ever be asked to cover is a false demand, and reporting it makes
//  the whole check less believable:
//
//    A CALL ACTION INSIDE A COMPUTE STAGE. It runs against the inert arm and is
//    the documented no-op (D7's one deliberate boundary), so it reaches no host
//    and names no endpoint anyone must serve. Its channel is stripped.
//
//    A QUERY'S LANDING SLOT. A query lands its table in the session's own query
//    results, which is state this placement already owns — not something asked
//    of anyone. The SOURCE it reads is the demand, and that is projected.
//
//  ── Reachability is one hop, and that is structural ─────────────────────────
//  Only handlers a tree can actually name contribute. Resolution stops there:
//  a stage's call action is inert, so no handler can reach another, and the walk
//  needs no cycle guard because there is no cycle to guard against — the same
//  fact that keeps the domain total (D2).
//
//  An endpoint with NO registered handler contributes nothing and produces no
//  finding. At dispatch that case is `ServerDiagnostic.HandlerUnregistered`,
//  which deliberately does not repeat the endpoint back: it is the one string in
//  this subsystem that comes off the wire. A pre-execution finding naming it
//  would put exactly that string into a host's logs, which is the leak the whole
//  projection is shaped to avoid — so the silence here is the same rule, not an
//  omission.
//
//  FORWARD-COUPLING: a new `ServerEffect` arm extends the match below, and the
//  compiler says so. A new source-bearing arm in the substrate's pipeline
//  vocabulary extends `refsOfTransform` — that match carries NO catch-all
//  precisely so raising the substrate pin surfaces the addition as an
//  incomplete-match warning against a gate that runs at zero warnings, rather
//  than as silent under-reporting by a walk that quietly ignored it. That is
//  what the arms enumerated but unused below are for; deleting them for
//  tidiness would delete the coupling.
// ============================================================================

module ServerDemanded =

    /// A server tier demanding nothing.
    let private noDemand: ServerDemand =
        { Effects = []
          Capabilities = []
          Functions = []
          Channels = []
          // The replay posture is joined AFTER this walk, by the placement's
          // replay module, and every projection this file produces therefore
          // carries an empty one. The derivation it needs sits above the wire
          // codec in compile order and the walk sits below it, which is a real
          // constraint rather than an inconvenience: it keeps this file's job
          // "what does this ask of a host" and leaves "may this be re-run"
          // where the enforcement is.
          Replay = [] }

    /// The projection a server walk starts from: nothing demanded, but a server
    /// tier PRESENT. Seeding with it is what makes "a walk ran and found
    /// nothing" a different document from "no walk ran" — see the field's note
    /// on `DemandedProjection.Server`.
    let private seed: DemandedProjection =
        { Demanded.empty with
            Server = Some noDemand }

    /// A projection carrying only this server tier.
    let private tier (demand: ServerDemand) : DemandedProjection = { seed with Server = Some demand }

    /// The by-reference source names a data source reads. An embedded table
    /// carries its own rows and asks the host for nothing.
    let private refsOfSource (source: Fuaran.Core.DataSource) : string list =
        match source with
        | Fuaran.Core.Embedded _ -> []
        | Fuaran.Core.Ref name -> [ name ]

    /// The by-reference source names one pipeline stage reads. Two arms of the
    /// pinned vocabulary take a second source; the rest work on the table they
    /// are handed.
    let private refsOfTransform (transform: Fuaran.Core.Transform) : string list =
        match transform with
        | Fuaran.Core.Join(source, _, _) -> refsOfSource source
        | Fuaran.Core.Union source -> refsOfSource source
        | Fuaran.Core.Filter _
        | Fuaran.Core.Project _
        | Fuaran.Core.Derive _
        | Fuaran.Core.GroupBy _
        | Fuaran.Core.Window _
        | Fuaran.Core.Pivot _
        | Fuaran.Core.Unpivot _
        | Fuaran.Core.Sort _
        | Fuaran.Core.Distinct
        | Fuaran.Core.Limit _ -> []

    /// What one server effect demands.
    ///
    /// Every NAME here is read off the effect value itself, through the same two
    /// functions the interpreter and the gate use — `ServerEffect.kind` for the
    /// discriminator, `ServerEffect.capability` for what the gate is asked. Not
    /// one string literal, which is the 892 discipline applied to a second
    /// vocabulary: a demanded capability and a gated one cannot be two spellings
    /// of an intention, because they are one call to one function.
    let private ofEffect (effect: ServerEffect) : DemandedProjection =
        let kind = ServerEffect.kind effect
        let capability = ServerEffect.capability effect

        match effect with
        | ServerEffect.HostCall(fn, _, into) ->
            // The one arm needing a PERFORMER as well as a permission, so the
            // one whose demand is a registration key paired with a capability.
            // Its landing slot is a write into the shared binding store, so it
            // lands in the CLIENT tier's namespaces beside the fold's own
            // writes — the same store, therefore the same list.
            { tier
                  { noDemand with
                      Effects = [ kind ]
                      Functions =
                          [ { Function = fn
                              Capability = capability } ] } with
                StateNamespaces =
                    into
                    |> Option.toList
                    |> List.map (fun key ->
                        { Namespace = Demanded.namespaceOf key
                          Written = true
                          Read = false }) }

        | ServerEffect.RunQuery(_, source, pipeline) ->
            // The demand is every by-reference source the query reads — the
            // names the host's source resolver will be handed. Its landing slot
            // is not one; see the header.
            tier
                { noDemand with
                    Effects = [ kind ]
                    Capabilities = [ capability ]
                    Channels =
                        (refsOfSource source @ (pipeline |> List.collect refsOfTransform))
                        |> List.map (fun name -> { Channel = kind; Name = name }) }

        | ServerEffect.Notify(channel, _) ->
            tier
                { noDemand with
                    Effects = [ kind ]
                    Capabilities = [ capability ]
                    Channels = [ { Channel = kind; Name = channel } ] }

        // The remaining two arms name nothing a host must be able to serve
        // beyond the capability itself: the ops they carry are applied to an
        // in-memory tree or shipped to whatever client is connected, and neither
        // is a name anyone registers.
        | ServerEffect.ApplyOps _
        | ServerEffect.EmitPatch _ ->
            tier
                { noDemand with
                    Effects = [ kind ]
                    Capabilities = [ capability ] }

    /// What one stage demands.
    let private ofStage (stage: HandlerStage) : DemandedProjection =
        match stage with
        | Effect effect -> ofEffect effect
        | Compute action ->
            let projection = Demanded.ofAction action

            { projection with
                // The inert call channel, stripped — see the header.
                HostCalls = projection.HostCalls |> List.filter (fun c -> c.Channel <> Demanded.CallChannel)
                Server = Some noDemand }

    /// What one handler can ever ask for, across both tiers.
    let ofHandler (handler: Handler) : DemandedProjection =
        Demanded.union (seed :: (handler.Stages |> List.map ofStage))

    /// What a set of handlers can ever ask for, as one document.
    let ofHandlers (handlers: Handler seq) : DemandedProjection =
        Demanded.union (seed :: (handlers |> Seq.map ofHandler |> List.ofSeq))

    /// **The one call**: what a program tree and the handler registration behind
    /// it can ever ask for, across both placements, as a single document.
    ///
    /// The tree's own demands and the demands of every handler it can NAME,
    /// unioned — so the answer is about this program on this host, not about
    /// either in isolation. A registered handler no tree reaches contributes
    /// nothing: it is a capability the host holds, not one this program can
    /// exercise.
    /// The registered handlers a tree can actually NAME.
    ///
    /// One hop, and structurally so — see the header. Exposed because the replay
    /// posture is joined onto this document by a later module and must be joined
    /// for exactly the same handler set: two reachability rules would be two
    /// answers to "which handlers is this document about", and the projection
    /// would then describe a set nobody computed.
    let reachable (handlers: Map<string, Handler>) (root: Node<obj>) : Handler list =
        Demanded.calledEndpoints (Demanded.ofTree root)
        |> List.choose (fun endpoint -> Map.tryFind endpoint handlers)

    let ofTreeAndHandlers (handlers: Map<string, Handler>) (root: Node<obj>) : DemandedProjection =
        let tree = Demanded.ofTree root

        Demanded.union (tree :: seed :: (reachable handlers root |> List.map ofHandler))

    /// A server host's coverage, read off the effect registry it was wired with
    /// rather than declared a second time.
    ///
    /// The registry ALREADY is that declaration — registered performers are the
    /// vocabulary, its gate is the policy — so reading it removes the way the
    /// two could disagree, and a pre-execution verdict and a dispatch-time one
    /// are computed from exactly the same two facts.
    ///
    /// The channel surface stays UNDECLARED. A host's notification channels and
    /// data sources reach it through functions, not through an enumeration, so
    /// there is nothing here to read; asserting a surface the host never stated
    /// would be this function inventing a declaration. A host that can enumerate
    /// its channels says so with `ServerCoverage.withChannels`.
    let coverageOfRegistry (registry: ServerEffectRegistry) : ServerCoverage =
        ServerCoverage.nothing
        |> ServerCoverage.withFunctions (ServerEffectRegistry.registered registry)
        |> ServerCoverage.withGate registry.Gate
