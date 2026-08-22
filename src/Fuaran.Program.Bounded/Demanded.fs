namespace Fuaran.Program.Bounded

open Fuaran.UI.Types
open Fuaran.UI.Ops.Introspect
open Fuaran.UI.ServerDriven

// ============================================================================
//  The demanded-effect projection, and the host-coverage validator over it.
//
//  The interpreter beside this file refuses a capability the host does not
//  offer AT DISPATCH TIME, one effect at a time, once the program is already
//  running. That is the right place for the refusal and the wrong place for the
//  QUESTION: by then the answer arrives as a denial in a log, per event, on
//  whichever paths a session happened to take. This file asks it once, up
//  front, of the whole tree — "what can this program EVER ask for" — and
//  answers as data.
//
//  ── Why a static walk can answer that exactly ───────────────────────────────
//  Because the vocabulary is closed and the tree carries no code. The action
//  set is a closed DU; the effect set is a closed DU; and the wire cannot carry
//  a closure, so a decoded tree's handler slots hold inert placeholders. There
//  is nothing to infer and no fixpoint to reach: the walk enumerates, it does
//  not analyse. That is a property of the design decisions (DECISIONS.md D2
//  totality, D3 closed vocabularies, D4 no foreign code), not a limitation
//  worked around here.
//
//  ── What the walk reads, and what it deliberately does not ──────────────────
//  A wire-decoded tree holds exactly THREE action slots the wire preserves —
//  `Button.OnClick`, `Form.OnSubmit`, `Modal.OnDismiss`. Every other handler
//  (a `Select.OnChange`, a form field's, a tab's) is a closure slot, and the
//  decoder substitutes an inert placeholder for it. So on a DECODED tree those
//  three slots are the complete reachable action surface and this projection is
//  EXACT.
//
//  On a HAND-AUTHORED tree they are not: a real closure there may carry
//  anything, and no walk can see inside it. That case is not silently
//  mis-reported — `OpaqueHandlers` names every node that accepts events but
//  whose actions are closure-held, so a reader can tell an exact projection
//  from a lower bound. It is data rather than a finding on purpose: on the
//  decoded trees this loop exists to run, those slots are inert, so raising
//  them as coverage failures would fire on every tree carrying a `Select` and
//  the whole check would learn to be ignored.
//
//  FORWARD-COUPLING: a new wire-survivable action slot, a new `Action` arm
//  reaching a host, or a new `ClientEffect` arm extends `wireSurvivableActions`
//  / `demandsOfAction` here. The compiler catches the second and third (both
//  matches are over closed DUs with the catch-all carrying a stated reason);
//  the first is a slot addition the compiler cannot see, and is the one to
//  remember.
//
//  ── Two placements, one document ────────────────────────────────────────────
//  A tree is only half of what a session can ask for. At the SERVER placement a
//  tree's call action reaches a host-registered handler, and that handler's
//  stage list names effects, host functions and channels of its own — an
//  envelope this walk cannot see, because a handler is not in the tree.
//
//  So the document carries a second, OPTIONAL tier. The tier's SHAPES live
//  here, beside the client tier's, because a projection is a wire document and
//  a document with half its vocabulary in another package is not one. The WALK
//  that fills it does not: it reads a placement's own handler and effect
//  vocabulary, so it lives with that placement, and reaches this file only
//  through `ofAction` / `union` / `withServer`. That split is why this package
//  still knows nothing about handlers while the document describes them.
// ============================================================================

/// One host call a program tree names. Both fields are AUTHOR-DECLARED names,
/// never payload values — an endpoint path, a capability id, a notification
/// channel, a tool name, a query slot. The projection is therefore log-safe by
/// construction, the same posture `EffectDenial.describe` and
/// `BoundedDiagnostic.describe` take.
type HostCallDemand =
    {
        /// The channel the call goes out on, named by the action arm that makes
        /// it: `"Call"` (an endpoint), `"Invoke"` (a capability), `"Notify"` (a
        /// notification channel), `"AiTool"` (a tool), `"Query"` (a query slot
        /// a result lands in or a dispatch-time binding reads).
        Channel: string
        /// The name the tree names within that channel.
        Name: string
    }

/// One state namespace a program tree touches — the segment of a state key
/// before its first `.`, or the whole key when it carries none. Namespaces
/// rather than keys because that is the granularity a host actually owns:
/// `host.` is already a reserved namespace the interpreter refuses writes to,
/// and a host declaring coverage declares areas, not individual slots.
type StateNamespaceDemand =
    {
        Namespace: string
        /// The tree can write into this namespace (`Action.SetState` — the only
        /// write the bounded vocabulary offers a tree; a handler's landing slots
        /// are host-declared and so are not a demand OF the tree).
        Written: bool
        /// The tree reads this namespace at DISPATCH time (a `SetState.valueFrom`
        /// binding). Display-time reads are deliberately absent — see
        /// `DemandedProjection`.
        Read: bool
    }

/// One host function a server-placement handler names.
///
/// Both strings are host-authored — a handler is host-registered data — and they
/// are carried TOGETHER because they are checked against different halves of a
/// host's offer and neither is derivable from the other without re-spelling the
/// namespace prefix by hand, which is the drift this projection exists to make
/// impossible.
type ServerFunctionDemand =
    {
        /// The REGISTRATION key: the name a performer is registered under.
        Function: string
        /// The CAPABILITY the policy gate is asked about for this call —
        /// the function's name inside the host-function namespace. Produced by
        /// the placement's own `capability` function on the effect itself, so a
        /// demanded capability and a gated one are the same string by
        /// construction rather than by agreement.
        Capability: string
    }

/// One reason a handler is not provably re-runnable, as the document carries
/// it: a stage ORDINAL and a token from the derivation's closed defect
/// vocabulary. Both are derived facts, so a posture is log-safe on exactly the
/// terms the rest of this document is.
type ReplayReasonDemand =
    {
        /// The stage's position in the declared list, zero-based.
        Stage: int
        /// The defect's wire token — `relative-addressing`, `non-literal-write`,
        /// `opaque-host-call`, and the rest of the closed set.
        Defect: string
    }

/// One handler's replay posture, derived from its declared form.
///
/// Carried per handler rather than aggregated, because that is the granularity
/// the decision is made at: a host resuming a session resumes particular
/// handlers, and a tier-wide verdict would say only that SOMETHING in the
/// registration reaches outside.
///
/// `Reasons` is empty exactly when `Safety` is `safe`. It is present for
/// `unknown` as well as for `unsafe`, and that is the point of carrying reasons
/// at all: `unknown` is the honest answer where no proof is available, and a
/// consumer that cannot see WHICH stage was undecidable has been told nothing it
/// can act on.
type ReplayPosture =
    {
        /// The handler's registration key — an author-declared name, the same
        /// class of string as an endpoint or a capability id, never a payload.
        Handler: string
        /// `safe` | `unsafe` | `unknown`.
        Safety: string
        /// Why, in stage order. Empty for `safe`.
        Reasons: ReplayReasonDemand list
    }

/// What a SERVER placement's handler registration can ever ask of its host —
/// the second tier of the document, and the half a program tree cannot express.
///
/// Every list is DISTINCT and SORTED, on the same terms as the client tier.
type ServerDemand =
    {
        /// The server-effect discriminators the reachable handlers can emit —
        /// the coarse vocabulary, a host function's arm included. DESCRIPTIVE
        /// rather than checked: it answers "does this ever mutate, ever reach
        /// outside" for a reader or a capability manifest, and the two lists
        /// below are what a verdict is computed from.
        Effects: string list
        /// The gate-facing capabilities of the arms whose capability IS their
        /// discriminator — every arm but the host-function one, which is
        /// namespaced per function and so appears in `Functions` instead.
        /// Checked against the host's gate.
        Capabilities: string list
        /// The host functions the handlers name.
        Functions: ServerFunctionDemand list
        /// Named channels the handlers reach beyond their host functions: a
        /// notification channel, a by-reference data source a query names. Same
        /// shape as the client tier's host calls, and checked the same way —
        /// only against a host that DECLARED a surface.
        Channels: HostCallDemand list
        /// The replay posture of each handler that contributed to this tier.
        ///
        /// DESCRIPTIVE, like `Effects` and unlike `Capabilities`: no coverage
        /// finding is computed from it, because a posture is not a demand on a
        /// host — it is a property of the handler that a host consults when it
        /// decides whether a session may be RESUMED. The enforcement of that
        /// decision lives at the placement, where the mode is known; this is the
        /// fact it enforces against, published so a capability manifest can
        /// state the posture without re-deriving it.
        Replay: ReplayPosture list
    }

/// What a program tree can ever ask of its host, as data.
///
/// **This is a document, not a view onto a tree.** It is self-describing
/// (`Demanded.encode` emits its own `kind` and `version`), carries no reference
/// back to the tree it came from, and names no consumer — so it can be emitted,
/// stored, shipped and checked somewhere the tree never travels. A capability
/// manifest for the program tier consumes it as-is.
///
/// Every list is DISTINCT and SORTED, so two runs over the same tree produce
/// byte-identical documents and two documents compare by value.
type DemandedProjection =
    {
        /// The `ClientEffect` discriminators the tree can cause — the same
        /// strings a host registry is keyed on, so a demanded name and a
        /// registered name are directly comparable rather than merely similar.
        Effects: string list
        /// The host calls the tree names.
        HostCalls: HostCallDemand list
        /// The state namespaces the tree touches.
        ///
        /// DISPATCH-time reads and writes only. A `Binding.State` read in a
        /// display slot is NOT a demand: it reads the store the host already
        /// owns and supplied, and asking a host to "cover" its own store would
        /// make the projection a description of the tree rather than of what
        /// the tree needs from anyone.
        StateNamespaces: StateNamespaceDemand list
        /// Node ids that accept events but whose actions are closure-held, so
        /// the wire cannot see them. Empty for a decoded tree — see the header.
        OpaqueHandlers: string list
        /// The SERVER placement's tier, when one was projected.
        ///
        /// `None` and `Some` with empty lists are DIFFERENT facts, deliberately:
        /// `None` says no server walk was performed — this document describes a
        /// tree and nothing else — while an empty tier says a walk ran and the
        /// reachable handlers demand nothing. A consumer that collapsed the two
        /// would read "not asked" as "asked, and the answer was nothing", which
        /// is the reading a coverage check must never make.
        Server: ServerDemand option
    }

/// What a host offers, against which a projection is checked.
///
/// `Effects` and `Gate` mirror the two independent facts a host effect registry
/// already separates: registration is the VOCABULARY, the gate is POLICY. An
/// unregistered effect cannot run however permissive the policy, which is why
/// the two produce different findings below rather than one.
///
/// `HostCalls` and `StateNamespaces` are OPTIONAL declarations (`None` =
/// unconstrained, the default). The four host-call arms are documented no-ops
/// on the bounded path — they never reach a host at all — so refusing a tree
/// for naming one would refuse most trees for a capability nothing was going to
/// perform. A host that genuinely brokers those calls declares its surface and
/// gets the check; one that does not is not nagged about it.
/// What a SERVER host offers, against which a projection's server tier is
/// checked. The same two-fact split as `HostCoverage` above, because the server
/// placement's registry draws the same line: a performer is registered under a
/// function name (the VOCABULARY), and a gate decides per capability (the
/// POLICY). The four arms that need no performer are always available and only
/// ever meet the gate.
///
/// `Channels` is an OPTIONAL declaration (`None` = unconstrained), for the
/// reason the client tier's `HostCalls` is: a host brokering notifications and
/// named data sources through opaque functions cannot enumerate them, and
/// refusing a handler for naming one against a host that never claimed a surface
/// would be a finding nobody can act on.
type ServerCoverage =
    { HostFunctions: Set<string>
      Gate: string -> bool
      Channels: Set<string> option }

type HostCoverage =
    {
        Effects: Set<string>
        Gate: string -> bool
        HostCalls: Set<string> option
        StateNamespaces: Set<string> option
        /// The SERVER tier's offer. `None` — the default — means this host made no
        /// server-side declaration, and NO server finding is ever produced against
        /// it. That is deliberately "unconstrained" rather than "covers nothing":
        /// most hosts have no server placement at all, and a client host checked
        /// against a document carrying a server tier should not be told it fails
        /// to offer capabilities it was never asked to have. A server host builds
        /// from `ServerCoverage.nothing`, which IS default-deny.
        Server: ServerCoverage option
    }

/// Why a host cannot cover a program tree. Each names the demanded thing; none
/// carries a payload value.
[<RequireQualifiedAccess>]
type CoverageFinding =
    /// The tree can cause this effect and the host registered no performer for
    /// it. The capability is absent: no policy makes it reachable.
    | UnregisteredEffect of kind: string
    /// The host has a performer for this effect and its policy gate refuses it.
    /// Distinct from `UnregisteredEffect` for the same reason the runtime
    /// denial DU distinguishes them: "this host has no such capability" and
    /// "this host has it and refused this use of it" are different facts, and
    /// only the second can be resolved by changing policy.
    | GateRefusesEffect of kind: string
    /// The tree names a host call outside the host's DECLARED call surface.
    /// Only ever produced when the host declared one.
    | UncoveredHostCall of channel: string * name: string
    /// The tree touches a state namespace outside the host's DECLARED set.
    /// Only ever produced when the host declared one.
    | UncoveredStateNamespace of ns: string
    /// A reachable handler names a host function this host registered no
    /// performer for. The server-tier counterpart of `UnregisteredEffect`, and a
    /// separate arm from it because `Notify` exists in BOTH vocabularies — a
    /// finding that named only the string would leave a host guessing which
    /// placement it had failed to serve.
    | UnregisteredServerFunction of fn: string
    /// A reachable handler names a capability this host's server policy gate
    /// refuses. Separate from `UnregisteredServerFunction` for the reason the
    /// runtime denial DU keeps its two arms apart: only this one is resolved by
    /// changing policy.
    | ServerGateRefusesCapability of capability: string
    /// A reachable handler names a channel outside the host's DECLARED server
    /// channel surface. Only ever produced when the host declared one.
    | UncoveredServerChannel of channel: string * name: string

module ServerCoverage =

    /// Covers nothing: no registered host function, a gate that refuses every
    /// capability, and no declared channel surface. The DEFAULT to build from,
    /// matching the server placement's own registry default.
    let nothing: ServerCoverage =
        { HostFunctions = Set.empty
          Gate = fun _ -> false
          Channels = None }

    /// Declare the host functions the server registered. Registering does not
    /// permit — the gate still decides, exactly as at dispatch time.
    let withFunctions (names: string seq) (coverage: ServerCoverage) : ServerCoverage =
        { coverage with
            HostFunctions = Set.ofSeq names }

    /// Replace the policy gate.
    let withGate (gate: string -> bool) (coverage: ServerCoverage) : ServerCoverage = { coverage with Gate = gate }

    /// Allow every capability. Named, not default — and it still cannot reach an
    /// unregistered host function, because that half of the vocabulary is closed
    /// by registration rather than by policy.
    let permissive (coverage: ServerCoverage) : ServerCoverage = withGate (fun _ -> true) coverage

    /// Declare the server channel surface. Until this is called the surface is
    /// unconstrained and no channel finding is ever produced.
    let withChannels (names: string seq) (coverage: ServerCoverage) : ServerCoverage =
        { coverage with
            Channels = Some(Set.ofSeq names) }

module HostCoverage =

    /// Covers nothing: no effect performer, a gate that refuses everything, and
    /// no declared call / namespace surface.
    ///
    /// This is the DEFAULT to build from, matching the default-deny posture
    /// every other seam in the bounded stack takes. A host that declares
    /// nothing covers nothing, and the strict construction paths say so before
    /// the program runs rather than after.
    let nothing: HostCoverage =
        { Effects = Set.empty
          Gate = fun _ -> false
          HostCalls = None
          StateNamespaces = None
          Server = None }

    /// Declare the effect performers the host registered. Registering does not
    /// permit — the gate still decides, exactly as at dispatch time.
    let withEffects (names: string seq) (coverage: HostCoverage) : HostCoverage =
        { coverage with
            Effects = Set.ofSeq names }

    /// Replace the policy gate.
    let withGate (gate: string -> bool) (coverage: HostCoverage) : HostCoverage = { coverage with Gate = gate }

    /// Allow every REGISTERED effect. Named, not default: reaching permissive
    /// is a deliberate act, and it still cannot reach an unregistered effect,
    /// because the vocabulary is closed by registration rather than by policy.
    let permissive (coverage: HostCoverage) : HostCoverage = withGate (fun _ -> true) coverage

    /// Declare the host-call surface. Until this is called the surface is
    /// unconstrained and no host-call finding is ever produced.
    let withHostCalls (names: string seq) (coverage: HostCoverage) : HostCoverage =
        { coverage with
            HostCalls = Some(Set.ofSeq names) }

    /// Declare the state namespaces the host owns. Until this is called the set
    /// is unconstrained and no namespace finding is ever produced.
    let withStateNamespaces (names: string seq) (coverage: HostCoverage) : HostCoverage =
        { coverage with
            StateNamespaces = Some(Set.ofSeq names) }

    /// Attach a server-tier offer. Until this is called no server finding is
    /// ever produced — see the field's note for why that is unconstrained
    /// rather than default-deny.
    let withServer (server: ServerCoverage) (coverage: HostCoverage) : HostCoverage =
        { coverage with Server = Some server }

module Demanded =

    // ─── the projection ──────────────────────────────────────────────────────

    /// The channel a call action's endpoint is recorded under.
    ///
    /// Public, and the one channel name that is: at the server placement a call
    /// action is what REACHES a handler, so the walk over handler stages has to
    /// tell an endpoint apart from every other host call — and it must do so
    /// without spelling the string a second time.
    [<Literal>]
    let CallChannel = "Call"

    /// The namespace a state key belongs to: the segment before its first `.`,
    /// or the whole key when it carries none.
    ///
    /// Public because the SERVER placement writes into the same store through a
    /// channel this file cannot see — a handler's landing slot — and a second
    /// spelling of this rule would put two answers to "which namespace is this"
    /// into one document.
    let namespaceOf (key: string) : string =
        let i = key.IndexOf '.'
        if i < 0 then key else key.Substring(0, i)

    /// The `ClientEffect` discriminator an action arm demands, if any.
    ///
    /// Named THROUGH `ClientEffect.kind` on a canonical sample rather than as a
    /// string literal, so a demanded name and a registry key cannot drift apart
    /// — the registry is keyed on exactly this discriminator, and two
    /// hand-written spellings of one string is precisely how a coverage check
    /// silently starts reporting nothing.
    ///
    /// The other three effect arms (`PushState` / `Focus` / `Download`) are
    /// absent deliberately: no `Action` produces them. They reach a host from
    /// the navigation layer, which is not a program tree's to demand.
    let private effectKindOf (action: Action<obj>) : string option =
        match action with
        | Action.Navigate _ -> Some(ClientEffect.kind (ClientEffect.Navigate ""))
        | Action.WriteToClipboard _ -> Some(ClientEffect.kind (ClientEffect.WriteToClipboard ""))
        | Action.ReadFileBody _ -> Some(ClientEffect.kind (ClientEffect.ReadFileBody("", "")))
        | _ -> None

    /// The host calls a dispatch-time binding source names. A `Binding.Query`
    /// read asks the host's query channel for a named slot; the other binding
    /// cases read context the host already supplied.
    let private hostCallsOfBinding (binding: Binding<Fuaran.Core.JVal>) : HostCallDemand list =
        Fuaran.UI.BindingWalk.usesOfBinding binding
        |> List.choose (fun u ->
            match u with
            | Fuaran.UI.BindingWalk.BindingUse.Query(name, _) -> Some { Channel = "Query"; Name = name }
            | _ -> None)

    /// The state namespaces a dispatch-time binding source READS.
    let private readsOfBinding (binding: Binding<Fuaran.Core.JVal>) : string list =
        Fuaran.UI.BindingWalk.usesOfBinding binding
        |> List.choose (fun u ->
            match u with
            | Fuaran.UI.BindingWalk.BindingUse.State key -> Some(namespaceOf key)
            | _ -> None)

    /// What one action arm demands: effect discriminators, host calls, and
    /// `(namespace, written)` touches. Total over the closed action DU; `Chain`
    /// recurses.
    let rec private demandsOfAction (action: Action<obj>) : string list * HostCallDemand list * (string * bool) list =
        let effects = effectKindOf action |> Option.toList

        match action with
        | Action.Chain actions ->
            actions
            |> List.fold
                (fun (accE, accH, accN) a ->
                    let e, h, n = demandsOfAction a
                    accE @ e, accH @ h, accN @ n)
                ([], [], [])

        | Action.SetState(key, _, valueFrom) ->
            // The write is the key's namespace. `valueFrom` is
            // evaluated at DISPATCH time against the store, so what it reads is
            // a genuine demand of the running program — unlike a display-slot
            // read, which is only the host reading back what it supplied.
            let reads =
                match valueFrom with
                | Some b -> readsOfBinding b |> List.map (fun ns -> ns, false)
                | None -> []

            let calls =
                match valueFrom with
                | Some b -> hostCallsOfBinding b
                | None -> []

            effects, calls, (namespaceOf key, true) :: reads

        | Action.Call(endpoint, _, _) ->
            // The endpoint is the demand, and the whole of it. A tree-declared
            // result target demands nothing, because it IS nothing here: result-
            // target ownership sits with the handler (DECISIONS.md D9), and the
            // fold refuses a call that declares one — so a target could only
            // ever ride on a call that reaches no host at all. Projecting it
            // would report a demand for a slot no host will ever be asked to
            // cover.
            effects,
            [ { Channel = CallChannel
                Name = endpoint } ],
            []

        | Action.Invoke(capabilityId, _) ->
            effects,
            [ { Channel = "Invoke"
                Name = capabilityId } ],
            []

        | Action.Notify(channel, _) -> effects, [ { Channel = "Notify"; Name = channel } ], []
        | Action.AiTool(toolName, _) -> effects, [ { Channel = "AiTool"; Name = toolName } ], []

        // The remaining arms demand no host call and touch no namespace.
        // `Navigate` / `WriteToClipboard` / `ReadFileBody` contributed their
        // effect above; `Dispatch` has no `update` to reach on this path and
        // `CommitLocal` flushes a per-node client-side buffer.
        | Action.Navigate _
        | Action.WriteToClipboard _
        | Action.ReadFileBody _
        | Action.Dispatch _
        | Action.CommitLocal _ -> effects, [], []

    /// The action slots the WIRE preserves. Every other handler slot is a
    /// closure the decoder replaces with an inert placeholder, so it can demand
    /// nothing on a decoded tree — see the header, and `opaqueHandler` below for
    /// how the hand-authored case is reported rather than assumed away.
    let private wireSurvivableActions (node: Node<obj>) : Action<obj> list =
        match node.Kind with
        | NodeKind.Button spec -> [ spec.OnClick ]
        | NodeKind.Form spec -> [ spec.OnSubmit ]
        | NodeKind.Modal spec -> Option.toList spec.OnDismiss
        | _ -> []

    /// True when the node accepts inbound events but resolves them through
    /// closure-held handlers, so its demands are invisible to this walk.
    ///
    /// Derived from `Validation.legitimateEvents` rather than from a second
    /// hand-kept list of interactive kinds: that function already decides which
    /// kinds accept events, and a kind added there without a wire-survivable
    /// slot is exactly the case this must report.
    let private opaqueHandler (node: Node<obj>) : bool =
        not (Set.isEmpty (Validation.legitimateEvents node))
        && List.isEmpty (wireSurvivableActions node)

    /// Merge namespace touches into one entry per namespace, its two flags OR'd
    /// across every touch.
    let private mergeNamespaces (touches: StateNamespaceDemand list) : StateNamespaceDemand list =
        touches
        |> List.fold
            (fun acc t ->
                let w, r = Map.tryFind t.Namespace acc |> Option.defaultValue (false, false)
                Map.add t.Namespace ((w || t.Written), (r || t.Read)) acc)
            Map.empty
        |> Map.toList
        |> List.map (fun (ns, (w, r)) ->
            { Namespace = ns
              Written = w
              Read = r })
        |> List.sortBy _.Namespace

    /// Put a projection's every list into the canonical form the document
    /// promises: distinct, sorted, one entry per namespace. The single place
    /// that ordering is decided, so `ofTree`, `ofAction` and `union` cannot
    /// disagree about what "deterministic" means.
    let private normalise (projection: DemandedProjection) : DemandedProjection =
        { Effects = projection.Effects |> List.distinct |> List.sort
          HostCalls =
            projection.HostCalls
            |> List.distinct
            |> List.sortBy (fun c -> c.Channel, c.Name)
          StateNamespaces = mergeNamespaces projection.StateNamespaces
          OpaqueHandlers = projection.OpaqueHandlers |> List.distinct |> List.sort
          Server =
            projection.Server
            |> Option.map (fun s ->
                { Effects = s.Effects |> List.distinct |> List.sort
                  Capabilities = s.Capabilities |> List.distinct |> List.sort
                  Functions = s.Functions |> List.distinct |> List.sortBy (fun f -> f.Function, f.Capability)
                  Channels = s.Channels |> List.distinct |> List.sortBy (fun c -> c.Channel, c.Name)
                  // Sorted by NAME only, and the reasons within a posture are
                  // left in stage order: they are a sequence through one
                  // handler, not a set, and sorting them would destroy the one
                  // thing that makes a stage ordinal useful.
                  Replay = s.Replay |> List.distinct |> List.sortBy _.Handler }) }

    /// The projection that demands nothing. The identity of `union`, and what a
    /// tree with no reachable handler slot projects.
    let empty: DemandedProjection =
        { Effects = []
          HostCalls = []
          StateNamespaces = []
          OpaqueHandlers = []
          Server = None }

    /// What ONE action can ever ask for, as a projection — the client tier only,
    /// since an action carries no handler.
    ///
    /// Exposed for a placement that interprets actions somewhere OTHER than a
    /// tree slot: the server placement's handler stages hold actions the shared
    /// fold runs, and they demand exactly what the same action demands anywhere
    /// else. That parity is the point — it is the one algebra claim, read at the
    /// projection rather than at the interpreter — and it is why the walk over
    /// stages calls this rather than matching an `Action` a second time.
    let ofAction (action: Action<obj>) : DemandedProjection =
        let effects, hostCalls, namespaces = demandsOfAction action

        normalise
            { empty with
                Effects = effects
                HostCalls = hostCalls
                StateNamespaces =
                    namespaces
                    |> List.map (fun (ns, written) ->
                        { Namespace = ns
                          Written = written
                          Read = not written }) }

    /// Combine projections into one, re-normalised. Order-independent and
    /// idempotent: `union` of the same documents in any order is the same
    /// document, which is what lets a walk accumulate without deciding anything
    /// about ordering.
    ///
    /// A server tier survives if ANY input carried one — `None` means "not
    /// asked", so a document that WAS asked cannot be silenced by union with one
    /// that was not.
    let union (projections: DemandedProjection list) : DemandedProjection =
        let server =
            if projections |> List.exists (fun p -> Option.isSome p.Server) then
                Some
                    { Effects =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Effects |> Option.defaultValue [])
                      Capabilities =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Capabilities |> Option.defaultValue [])
                      Functions =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Functions |> Option.defaultValue [])
                      Channels =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Channels |> Option.defaultValue [])
                      Replay =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Replay |> Option.defaultValue []) }
            else
                None

        normalise
            { Effects = projections |> List.collect _.Effects
              HostCalls = projections |> List.collect _.HostCalls
              StateNamespaces = projections |> List.collect _.StateNamespaces
              OpaqueHandlers = projections |> List.collect _.OpaqueHandlers
              Server = server }

    /// Attach (or replace) the server tier, re-normalised.
    let withServer (server: ServerDemand) (projection: DemandedProjection) : DemandedProjection =
        normalise { projection with Server = Some server }

    /// The endpoints a projection's tree names through a call action.
    ///
    /// The one place a channel is filtered by name, and it reads the same
    /// constant the producer writes — so the two cannot drift. A server
    /// placement uses it to decide which registered handlers a given tree can
    /// actually reach.
    let calledEndpoints (projection: DemandedProjection) : string list =
        projection.HostCalls
        |> List.filter (fun c -> c.Channel = CallChannel)
        |> List.map _.Name
        |> List.distinct
        |> List.sort

    /// Compute a program tree's complete demanded-effect set.
    ///
    /// Total: every tree has a projection, and no input is refused. The walk
    /// covers the whole traversal surface (`Introspect.descendantNodes` — the
    /// structural children AND the non-list slots such as a `StateBehaviour`
    /// branch), so a demand parked in a loading state is not missed.
    ///
    /// The server tier is `None`: this walk sees a tree, and a handler is not in
    /// the tree. A placement that HAS a handler registration projects it and
    /// attaches the result with `withServer`.
    let ofTree (root: Node<obj>) : DemandedProjection =
        let rec walk (node: Node<obj>) =
            let own =
                node
                |> wireSurvivableActions
                |> List.fold
                    (fun (accE, accH, accN) a ->
                        let e, h, n = demandsOfAction a
                        accE @ e, accH @ h, accN @ n)
                    ([], [], [])

            let ownE, ownH, ownN = own
            let ownO = if opaqueHandler node then [ node.Id ] else []

            descendantNodes node
            |> List.fold
                (fun (accE, accH, accN, accO) child ->
                    let e, h, n, o = walk child
                    accE @ e, accH @ h, accN @ n, accO @ o)
                (ownE, ownH, ownN, ownO)

        let effects, hostCalls, namespaces, opaque = walk root

        normalise
            { Effects = effects
              HostCalls = hostCalls
              StateNamespaces =
                namespaces
                |> List.map (fun (ns, written) ->
                    { Namespace = ns
                      Written = written
                      Read = not written })
              OpaqueHandlers = opaque
              Server = None }

    // ─── the projection as a wire document ───────────────────────────────────

    let private esc (s: string) : string =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

    let private q (s: string) : string = "\"" + esc s + "\""

    let private arr (items: string list) : string =
        "[" + (items |> String.concat ",") + "]"

    /// Encode the projection as a self-describing JSON document — the same wire
    /// discipline the effect vocabulary uses (tagged object, camelCase keys,
    /// `FSharp.Core`-only, Fable-clean).
    ///
    /// `kind` and `version` are carried IN the document rather than agreed out
    /// of band, so a consumer that finds one of these on disk can tell what it
    /// is and whether it understands it, without knowing who wrote it.
    /// Deterministic: the projection's lists are already distinct and sorted, so
    /// the same tree encodes to the same bytes every time.
    ///
    /// **Version 2 adds the server tier**, and the bump is deliberate rather
    /// than incidental: the `server` key is present on EVERY document from here
    /// on — `null` where no server walk ran — so a v1 reader meets a shape it
    /// was not written for. Emitting the key only when a tier exists would have
    /// let v1 readers keep working on client-only documents and fail on the
    /// others, which is a worse contract than one honest number: a consumer
    /// could not tell "this producer is older than the server tier" from "this
    /// program has no server side", and those need different answers.
    ///
    /// **Version 3 adds the replay posture**, on exactly that argument rather
    /// than by analogy to it. The `replay` key is present on EVERY server tier
    /// from here on — `[]` where the walk contributed no posture — so the same
    /// two facts stay distinguishable one level down: an ABSENT `replay` says
    /// "this producer predates the posture", an EMPTY one says "this
    /// registration was walked and no handler contributed". Emitting the key
    /// only when a posture exists would collapse those, and a consumer deciding
    /// whether a session may be resumed is the last consumer that should have to
    /// guess which of the two it is holding. The number is in-band and cheap;
    /// the ambiguity would not have been.
    let encode (projection: DemandedProjection) : string =
        let effects = projection.Effects |> List.map q |> arr

        let hostCalls =
            projection.HostCalls
            |> List.map (fun c -> $"""{{"channel":{q c.Channel},"name":{q c.Name}}}""")
            |> arr

        let namespaces =
            projection.StateNamespaces
            |> List.map (fun n ->
                let w = if n.Written then "true" else "false"
                let r = if n.Read then "true" else "false"
                $"""{{"namespace":{q n.Namespace},"written":{w},"read":{r}}}""")
            |> arr

        let opaque = projection.OpaqueHandlers |> List.map q |> arr

        let server =
            match projection.Server with
            | None -> "null"
            | Some s ->
                let se = s.Effects |> List.map q |> arr
                let sc = s.Capabilities |> List.map q |> arr

                let fns =
                    s.Functions
                    |> List.map (fun f -> $"""{{"function":{q f.Function},"capability":{q f.Capability}}}""")
                    |> arr

                let channels =
                    s.Channels
                    |> List.map (fun c -> $"""{{"channel":{q c.Channel},"name":{q c.Name}}}""")
                    |> arr

                let replay =
                    s.Replay
                    |> List.map (fun p ->
                        let reasons =
                            p.Reasons
                            |> List.map (fun r -> $"""{{"stage":{r.Stage},"defect":{q r.Defect}}}""")
                            |> arr

                        $"""{{"handler":{q p.Handler},"safety":{q p.Safety},"reasons":{reasons}}}""")
                    |> arr

                $"""{{"effects":{se},"capabilities":{sc},"functions":{fns},"channels":{channels},"replay":{replay}}}"""

        $"""{{"kind":"demanded","version":3,"effects":{effects},"hostCalls":{hostCalls},"stateNamespaces":{namespaces},"opaqueHandlers":{opaque},"server":{server}}}"""

    // ─── the coverage validator ──────────────────────────────────────────────

    /// Human-readable, log-safe description of a finding.
    let describe (finding: CoverageFinding) : string =
        match finding with
        | CoverageFinding.UnregisteredEffect kind ->
            $"the program can cause effect '%s{kind}', for which this host registered no performer"
        | CoverageFinding.GateRefusesEffect kind ->
            $"the program can cause effect '%s{kind}', which this host's policy gate refuses"
        | CoverageFinding.UncoveredHostCall(channel, name) ->
            $"the program names %s{channel} '%s{name}', which is outside this host's declared call surface"
        | CoverageFinding.UncoveredStateNamespace ns ->
            $"the program touches state namespace '%s{ns}', which is outside this host's declared namespaces"
        | CoverageFinding.UnregisteredServerFunction fn ->
            $"a handler this program can reach calls host function '%s{fn}', for which this host registered no performer"
        | CoverageFinding.ServerGateRefusesCapability capability ->
            $"a handler this program can reach needs server capability '%s{capability}', which this host's policy gate refuses"
        | CoverageFinding.UncoveredServerChannel(channel, name) ->
            $"a handler this program can reach names %s{channel} '%s{name}', which is outside this host's declared server channels"

    /// Check an already-computed projection against a host's coverage.
    ///
    /// Split from `check` so a projection emitted elsewhere — stored, shipped,
    /// consumed by a capability manifest — can be validated against a host
    /// without that host ever holding the tree.
    ///
    /// Findings are ordered as declared on `CoverageFinding` and then by name,
    /// so a refusal message reads the same way on every run.
    let checkProjection (coverage: HostCoverage) (projection: DemandedProjection) : CoverageFinding list =
        let effectFindings =
            projection.Effects
            |> List.choose (fun kind ->
                if not (coverage.Effects.Contains kind) then
                    Some(CoverageFinding.UnregisteredEffect kind)
                elif not (coverage.Gate kind) then
                    Some(CoverageFinding.GateRefusesEffect kind)
                else
                    None)

        let callFindings =
            match coverage.HostCalls with
            | None -> []
            | Some declared ->
                projection.HostCalls
                |> List.choose (fun c ->
                    if declared.Contains c.Name then
                        None
                    else
                        Some(CoverageFinding.UncoveredHostCall(c.Channel, c.Name)))

        let namespaceFindings =
            match coverage.StateNamespaces with
            | None -> []
            | Some declared ->
                projection.StateNamespaces
                |> List.choose (fun n ->
                    if declared.Contains n.Namespace then
                        None
                    else
                        Some(CoverageFinding.UncoveredStateNamespace n.Namespace))

        // The server tier is checked only where BOTH sides exist: a document
        // that was never asked about a server placement, or a host that never
        // declared one, produces nothing here. Neither absence is evidence of a
        // failure, and reporting one as such would make the check fire on every
        // client host that ever met a two-tier document.
        let serverFindings =
            match projection.Server, coverage.Server with
            | Some demand, Some offer ->
                // Registration before policy, which is the OPPOSITE of the
                // order the interpreter uses — and deliberately so. At dispatch
                // the gate runs first so that no lookup of any kind precedes the
                // policy decision; that is an ordering of SIDE EFFECTS, and this
                // check has none. What a coverage report owes its reader is the
                // fact policy cannot fix, first: an unregistered performer is
                // absent however the gate is written.
                let functionFindings =
                    demand.Functions
                    |> List.collect (fun f ->
                        if not (offer.HostFunctions.Contains f.Function) then
                            [ CoverageFinding.UnregisteredServerFunction f.Function ]
                        elif not (offer.Gate f.Capability) then
                            [ CoverageFinding.ServerGateRefusesCapability f.Capability ]
                        else
                            [])

                let capabilityFindings =
                    demand.Capabilities
                    |> List.choose (fun c ->
                        if offer.Gate c then
                            None
                        else
                            Some(CoverageFinding.ServerGateRefusesCapability c))

                let channelFindings =
                    match offer.Channels with
                    | None -> []
                    | Some declared ->
                        demand.Channels
                        |> List.choose (fun c ->
                            if declared.Contains c.Name then
                                None
                            else
                                Some(CoverageFinding.UncoveredServerChannel(c.Channel, c.Name)))

                functionFindings @ capabilityFindings @ channelFindings
            | _ -> []

        effectFindings @ callFindings @ namespaceFindings @ serverFindings

    /// Answer, for one tree and one host, every demand the host cannot cover —
    /// BEFORE any event runs. An empty list means the host can serve everything
    /// this program is able to ask for.
    let check (coverage: HostCoverage) (tree: Node<obj>) : CoverageFinding list =
        ofTree tree |> checkProjection coverage
