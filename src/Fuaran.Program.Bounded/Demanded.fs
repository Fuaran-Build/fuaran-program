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
type HostCoverage =
    { Effects: Set<string>
      Gate: string -> bool
      HostCalls: Set<string> option
      StateNamespaces: Set<string> option }

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
          StateNamespaces = None }

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

module Demanded =

    // ─── the projection ──────────────────────────────────────────────────────

    /// The namespace a state key belongs to: the segment before its first `.`,
    /// or the whole key when it carries none.
    let private namespaceOf (key: string) : string =
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
            effects, [ { Channel = "Call"; Name = endpoint } ], []

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

    /// Compute a program tree's complete demanded-effect set.
    ///
    /// Total: every tree has a projection, and no input is refused. The walk
    /// covers the whole traversal surface (`Introspect.descendantNodes` — the
    /// structural children AND the non-list slots such as a `StateBehaviour`
    /// branch), so a demand parked in a loading state is not missed.
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

        // One entry per namespace, its two flags OR'd across every touch.
        let merged =
            namespaces
            |> List.fold
                (fun acc (ns, written) ->
                    let w, r = Map.tryFind ns acc |> Option.defaultValue (false, false)
                    Map.add ns ((w || written), (r || not written)) acc)
                Map.empty

        { Effects = effects |> List.distinct |> List.sort
          HostCalls = hostCalls |> List.distinct |> List.sortBy (fun c -> c.Channel, c.Name)
          StateNamespaces =
            merged
            |> Map.toList
            |> List.map (fun (ns, (w, r)) ->
                { Namespace = ns
                  Written = w
                  Read = r })
            |> List.sortBy _.Namespace
          OpaqueHandlers = opaque |> List.distinct |> List.sort }

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

        $"""{{"kind":"demanded","version":1,"effects":{effects},"hostCalls":{hostCalls},"stateNamespaces":{namespaces},"opaqueHandlers":{opaque}}}"""

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

        effectFindings @ callFindings @ namespaceFindings

    /// Answer, for one tree and one host, every demand the host cannot cover —
    /// BEFORE any event runs. An empty list means the host can serve everything
    /// this program is able to ask for.
    let check (coverage: HostCoverage) (tree: Node<obj>) : CoverageFinding list =
        ofTree tree |> checkProjection coverage
