namespace Fuaran.Program.Runtime

open Fuaran.UI.ServerDriven

// ============================================================================
//  The client host-effect seam — closed vocabulary, registered performers,
//  default-deny gate.
//
//  This is DECISIONS.md D3 as code, for the browser placement. Read it there
//  rather than re-deriving it here; what this file adds is the mechanism:
//
//    - The effect vocabulary is a CLOSED DU (`ClientEffect`). A program tree can
//      *name* only what that DU can express, and nothing on the wire widens it.
//    - Extensibility is a HOST act: an embedding host registers a named
//      performer. Until it does, the effect has nowhere to go.
//    - Every dispatch passes a policy gate FIRST. The gate defaults to refusing
//      everything, so a host that registers nothing performs nothing — the same
//      posture the dispatch gate and the server placement's services take.
//    - A refusal is RECORDED, never silently dropped. "Nothing happened" and
//      "the host refused that" are different facts, and a generated app being
//      debugged needs to tell them apart.
//
//  The server placement reaches the same vocabulary through a different
//  transport: it ships `ClientEffect`s to a browser shim that performs them.
//  One effect vocabulary, two transports — so a host registering performers
//  here should match the shim's behaviour arm for arm, and the parity family
//  asserts it where the behaviour is observable.
// ============================================================================

/// Why an effect did not run. Both arms are recorded through `OnDenied`; the
/// distinction matters to whoever is debugging an emission, because one says
/// "this host does not offer that capability" and the other says "this host
/// has it and refused this use of it".
[<RequireQualifiedAccess>]
type EffectDenial =
    /// No performer is registered under this effect's name — the capability is
    /// absent from this host, so the program's reach never extended to it.
    | Unregistered of effect: string
    /// A performer exists, and the policy gate refused this dispatch.
    | GateRefused of effect: string

module EffectDenial =
    /// Human-readable, log-safe description. Carries the effect's DISCRIMINATOR
    /// only, never its payload — a denied `Navigate` must not log the route it
    /// wanted, and a denied `WriteToClipboard` must not log the text.
    let describe (d: EffectDenial) : string =
        match d with
        | EffectDenial.Unregistered name ->
            sprintf "effect '%s' was not performed: no performer is registered for it on this host" name
        | EffectDenial.GateRefused name -> sprintf "effect '%s' was not performed: the policy gate refused it" name

/// A closed, default-deny registry of host-performed effects.
type EffectRegistry =
    {
        /// Performers by effect discriminator (`ClientEffect.kind`). An effect
        /// whose name is absent is `Unregistered` — never performed, always
        /// recorded.
        Performers: Map<string, ClientEffect -> unit>
        /// The policy gate, consulted BEFORE any performer runs. Takes the
        /// effect's discriminator so a host can allow `Navigate` and refuse
        /// `WriteToClipboard` without inspecting payloads.
        Gate: string -> bool
        /// Denial sink. Fired for every refusal, so a denied dispatch is
        /// observable rather than a silent nothing.
        OnDenied: EffectDenial -> unit
    }

module EffectRegistry =

    /// The empty registry: no performers, gate refuses everything, denials
    /// dropped. This is the DEFAULT a host starts from — a host that wires
    /// nothing performs nothing, which is the only defensible default for a
    /// loop whose whole premise is that the tree is untrusted.
    let denyAll: EffectRegistry =
        { Performers = Map.empty
          Gate = fun _ -> false
          OnDenied = ignore }

    /// Register a performer for one effect discriminator (`ClientEffect.kind`:
    /// `"Navigate"`, `"WriteToClipboard"`, `"PushState"`, `"Focus"`,
    /// `"Download"`, `"ReadFileBody"`). Registering does NOT permit — the gate
    /// still decides. The two are separate on purpose: a host can register its
    /// whole capability set once and vary the policy per session.
    let register (name: string) (performer: ClientEffect -> unit) (registry: EffectRegistry) : EffectRegistry =
        { registry with
            Performers = Map.add name performer registry.Performers }

    /// Replace the policy gate.
    let withGate (gate: string -> bool) (registry: EffectRegistry) : EffectRegistry = { registry with Gate = gate }

    /// Allow every REGISTERED effect. Named, not default — reaching permissive
    /// is a deliberate act, and it still cannot reach an unregistered effect,
    /// because the vocabulary is closed by registration rather than by policy.
    let permissive (registry: EffectRegistry) : EffectRegistry = withGate (fun _ -> true) registry

    /// Set the denial sink.
    let onDenied (sink: EffectDenial -> unit) (registry: EffectRegistry) : EffectRegistry =
        { registry with OnDenied = sink }

    /// The registered effect names, for host introspection.
    let registered (registry: EffectRegistry) : string list =
        registry.Performers |> Map.toList |> List.map fst

    /// Perform one effect, or record why not. The order is load-bearing: the
    /// gate is consulted BEFORE the performer is even looked up as a callable,
    /// so no performer side-effect can precede the policy decision.
    let perform (registry: EffectRegistry) (effect: ClientEffect) : unit =
        let name = ClientEffect.kind effect

        match Map.tryFind name registry.Performers with
        | None -> registry.OnDenied(EffectDenial.Unregistered name)
        | Some performer ->
            if registry.Gate name then
                performer effect
            else
                registry.OnDenied(EffectDenial.GateRefused name)
