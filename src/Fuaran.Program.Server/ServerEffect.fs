namespace Fuaran.Program.Server

open Fuaran.UI.Ops.Types

// ============================================================================
//  The SERVER placement's host-effect seam — closed vocabulary, registered
//  performers, default-deny gate.
//
//  This is DECISIONS.md D3 as code for a third placement, and it is deliberately
//  the same shape as the browser placement's registry rather than a new idea:
//  a closed DU the program can name, a policy gate consulted before anything
//  runs, and a denial that is RECORDED rather than silently dropped. What
//  differs is the vocabulary, because a server session can do things a browser
//  tab cannot.
//
//  ── The five arms ───────────────────────────────────────────────────────────
//    RunQuery    read: evaluate a declarative pipeline over a source and land
//                the result in the session's query slot. Reads only.
//    ApplyOps    the ONLY mutation of domain state — a `TreeOp` sequence
//                through the apply engine. Nothing else in this file, and
//                nothing in the shared interpreter, edits the domain tree.
//    HostCall    the named, registered, policy-gated escape to computation the
//                total algebra cannot express (D2). The one arm that needs a
//                performer as well as a permission.
//    EmitPatch   ops shipped to a connected client WITHOUT touching domain
//                state — a transport act, kept distinct from ApplyOps so
//                "what the client sees" and "what is durable" can never be
//                confused for one another.
//    Notify      an out-of-band host-channel message. Not the bounded
//                interpreter's `Action.Notify`, which stays a documented no-op
//                at every placement; this one is a handler's own declaration.
//
//  The case names are the F# spelling of that vocabulary. They are NOT a wire
//  commitment — this placement has no wire form yet, and inventing one here
//  would be the expensive kind of premature decision.
//
//  ── What a denial may say ───────────────────────────────────────────────────
//  A denial carries the CAPABILITY, never the payload: a refused RunQuery must
//  not log the pipeline it wanted to run, and a refused Notify must not log the
//  message. That is the browser placement's rule, and it applies unchanged.
//
//  A capability name is safe to record here for a reason worth stating: a
//  handler's stages are registered by the HOST, so a host-function name is the
//  host's own vocabulary rather than anything a generated tree supplied. The one
//  string in this subsystem that DOES come off the wire — the endpoint a tree's
//  `Action.Call` names — is never echoed anywhere. See `ServerDiagnostic`.
// ============================================================================

/// The closed vocabulary of effects the server placement's interpreter can emit.
/// Extensibility is a host act (`HostCall` + a registered performer), never a
/// widening of this DU — D3.
[<RequireQualifiedAccess>]
type ServerEffect =
    /// Evaluate `pipeline` over `source` and land the resulting table in the
    /// session's query slot `name`. A read: it touches no domain state.
    | RunQuery of name: string * source: Fuaran.Core.DataSource * pipeline: Fuaran.Core.Transform list
    /// Apply a `TreeOp` sequence to the domain tree through the apply engine.
    /// **The only domain-state mutation in the whole placement.**
    | ApplyOps of ops: TreeOp<obj> list
    /// Call a named host performer with declarative arguments, optionally
    /// landing its result in the session's state slot `into`. The escape hatch
    /// the total algebra needs (D2), held behind registration and policy.
    | HostCall of fn: string * args: Fuaran.Core.JVal * into: string option
    /// Ship ops to the connected client without touching domain state.
    | EmitPatch of ops: TreeOp<obj> list
    /// Send a host-channel message. The host performs the delivery; this
    /// placement records that the handler asked for it.
    | Notify of channel: string * payload: Fuaran.Core.JVal

module ServerEffect =

    /// The effect's discriminator — log-safe, and the coarse unit a gate can
    /// reason about ("this session may read, but may not mutate").
    let kind (effect: ServerEffect) : string =
        match effect with
        | ServerEffect.RunQuery _ -> "RunQuery"
        | ServerEffect.ApplyOps _ -> "ApplyOps"
        | ServerEffect.HostCall _ -> "HostCall"
        | ServerEffect.EmitPatch _ -> "EmitPatch"
        | ServerEffect.Notify _ -> "Notify"

    /// The whole closed vocabulary, for host introspection — a host wiring a
    /// gate should be able to enumerate what it is deciding about rather than
    /// discovering the arms one refusal at a time.
    let kinds: string list =
        [ "RunQuery"; "ApplyOps"; "HostCall"; "EmitPatch"; "Notify" ]

    /// The capability a gate is asked about. For four arms that is the
    /// discriminator; a `HostCall` is namespaced by its function name
    /// (`host:<fn>`), because "may this session call host functions at all" and
    /// "may it call THIS one" are different questions and a gate that could only
    /// ask the first would be useless the moment a host registered two.
    ///
    /// The `host:` prefix keeps the two namespaces disjoint, so a host function
    /// named `ApplyOps` can never be permitted by a rule about the built-in arm.
    let capability (effect: ServerEffect) : string =
        match effect with
        | ServerEffect.HostCall(fn, _, _) -> "host:" + fn
        | other -> kind other

/// Why an effect did not run. Both arms carry the CAPABILITY only — never the
/// pipeline, the ops, the arguments or the message. One says "this host does not
/// offer that capability"; the other says "this host has it and refused this use
/// of it", and collapsing them would lose the more useful fact.
[<RequireQualifiedAccess>]
type ServerEffectDenial =
    /// A `HostCall` named a function no performer is registered under. The
    /// capability is absent from this host, so the handler's reach never
    /// extended to it.
    | Unregistered of capability: string
    /// The policy gate refused this capability.
    | GateRefused of capability: string

module ServerEffectDenial =
    /// Human-readable, log-safe description.
    let describe (denial: ServerEffectDenial) : string =
        match denial with
        | ServerEffectDenial.Unregistered capability ->
            sprintf "effect '%s' was not performed: no performer is registered for it on this host" capability
        | ServerEffectDenial.GateRefused capability ->
            sprintf "effect '%s' was not performed: the policy gate refused it" capability

/// A closed, default-deny registry of the server placement's effect
/// permissions, plus the performers for the one arm that needs them.
type ServerEffectRegistry =
    {
        /// Performers for `HostCall`, by function name. A call naming an absent
        /// function is `Unregistered` — never performed, always recorded. The
        /// performer returns a value or a reason; the reason is the HOST's own
        /// text, so unlike an engine error it is safe to surface verbatim.
        HostFunctions: Map<string, Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>>
        /// The policy gate, consulted BEFORE any effect runs and before any
        /// performer is looked up. Takes the capability (`ServerEffect.capability`)
        /// so a host can permit reads and refuse mutations without inspecting a
        /// single payload.
        Gate: string -> bool
        /// Denial sink. Fired for every refusal, so a denied effect is
        /// observable rather than a silent nothing.
        OnDenied: ServerEffectDenial -> unit
    }

module ServerEffectRegistry =

    /// The empty registry: no host functions, gate refuses everything, denials
    /// dropped. This is the DEFAULT, for the same reason it is the default at
    /// the other placements — the tree that reached this handler is untrusted,
    /// and a host that wires nothing must perform nothing.
    let denyAll: ServerEffectRegistry =
        { HostFunctions = Map.empty
          Gate = fun _ -> false
          OnDenied = ignore }

    /// Register a `HostCall` performer under a function name. Registering does
    /// NOT permit: the gate still decides, and it is asked about `host:<fn>`.
    let register
        (fn: string)
        (performer: Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>)
        (registry: ServerEffectRegistry)
        : ServerEffectRegistry =
        { registry with
            HostFunctions = Map.add fn performer registry.HostFunctions }

    /// Replace the policy gate.
    let withGate (gate: string -> bool) (registry: ServerEffectRegistry) : ServerEffectRegistry =
        { registry with Gate = gate }

    /// Allow every capability. Named, not default — reaching permissive is a
    /// deliberate act, and it still cannot reach an unregistered host function,
    /// because that half of the vocabulary is closed by registration rather than
    /// by policy.
    let permissive (registry: ServerEffectRegistry) : ServerEffectRegistry = withGate (fun _ -> true) registry

    /// Set the denial sink.
    let onDenied (sink: ServerEffectDenial -> unit) (registry: ServerEffectRegistry) : ServerEffectRegistry =
        { registry with OnDenied = sink }

    /// The registered host-function names, for host introspection.
    let registered (registry: ServerEffectRegistry) : string list =
        registry.HostFunctions |> Map.toList |> List.map fst
