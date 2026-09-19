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
        /// The declared ARGUMENT policy, by capability — what the gate decides
        /// about once it has admitted the capability itself.
        ///
        /// Declared DATA rather than a second predicate, and that is the whole
        /// difference between this and `Gate`: a bound written down can be read
        /// back, carried in the demanded document, and put in front of a deployer
        /// before anything runs, where a closure can only ever be asked. It is
        /// why "HTTP allowed" becomes "HTTP to api.example.com, ≤ 64 KB" in the
        /// envelope rather than staying a promise the host makes to itself.
        ///
        /// **A capability absent from this map is UNCONSTRAINED** — exactly as a
        /// host that declares no call surface is not checked against one — so a
        /// host that declares nothing behaves as it did before this existed. The
        /// gate remains the default-deny half; this half narrows a capability the
        /// gate has already admitted and can never widen one it refused.
        Constraints: Map<string, Fuaran.Program.Bounded.ServerConstraintClause list>
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
          // Empty is UNCONSTRAINED, not "nothing permitted", and that is not a
          // hole in the default-deny posture: the gate above already refuses
          // every capability, so there is nothing for an argument bound to
          // narrow. Default-denying here as well would make the empty registry
          // deny twice and say nothing more.
          Constraints = Map.empty
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

    /// Declare the argument policy for one capability. Replaces any clause list
    /// already declared for it, so the policy for a capability is one statement
    /// rather than an accumulation a reader would have to fold.
    ///
    /// Declaring does NOT permit, on the same terms `register` does not: the gate
    /// is still asked first, and a bound on a capability the gate refuses is
    /// never reached.
    let constrain
        (capability: string)
        (clauses: Fuaran.Program.Bounded.ServerConstraintClause list)
        (registry: ServerEffectRegistry)
        : ServerEffectRegistry =
        { registry with
            Constraints = Map.add capability clauses registry.Constraints }

    /// What this host declared about a capability's arguments. An EMPTY list is
    /// returned for a capability nobody constrained — the caller's check then
    /// passes vacuously, which is "unconstrained" arriving as the absence of
    /// clauses rather than as a special case anyone has to remember.
    let constraintsFor
        (capability: string)
        (registry: ServerEffectRegistry)
        : Fuaran.Program.Bounded.ServerConstraintClause list =
        registry.Constraints |> Map.tryFind capability |> Option.defaultValue []

/// Why an effect's ARGUMENTS did not satisfy the policy declared for its
/// capability — the refusal the gate produces once it has admitted the
/// capability itself and gone on to look at what the effect carries.
///
/// Both arms name the DECLARATION and never the value. That is the same rule
/// the denial DU keeps and it bites harder here, because an argument check is
/// the one place in this subsystem that reads a wire-supplied string: an
/// off-list refusal names the ARGUMENT the host constrained, never the value
/// found there, and an over-ceiling refusal names the host's LIMIT, never the
/// size measured. A measured size is not the payload, but it is a fact about it,
/// and this is not the seam to start conceding those at.
[<RequireQualifiedAccess>]
type ServerConstraintDefect =
    /// The effect carries a value for `argument` that the declared allow-list
    /// for that argument does not permit.
    | OffList of argument: string
    /// The effect's declarative payload is larger than the declared ceiling.
    | OverCeiling of limit: int

/// The ARGUMENT half of the policy gate: what an effect's arguments are, and
/// whether a capability's declared clauses admit them.
///
/// ── Why this is a separate half ─────────────────────────────────────────────
/// `ServerEffectRegistry.Gate` decides about a CAPABILITY — a name, derived from
/// the effect's own discriminator. That is the whole decision for an effect
/// whose arguments a host authored, and it is not the whole decision for one
/// whose arguments carry a value that came off the wire: a permitted capability
/// reaching an endpoint nobody permitted is the confused deputy, and "may this
/// session call out" is a different question from "may it call out THERE".
///
/// ── What an allow-list ranges over ──────────────────────────────────────────
/// The effect's NAMED arguments, as `arguments` below derives them. Three of the
/// five arms carry one: a host call's declarative argument object, a
/// notification's channel, and the by-reference sources a query reads. The other
/// two carry op sequences, which name nothing anyone registers — the same
/// reason the demanded projection's walk finds nothing in them.
///
/// ── What is deliberately NOT claimed ────────────────────────────────────────
/// A host call's arguments are read ONE LEVEL DEEP: a top-level member whose
/// value is a string is a named argument, and a value nested inside an object or
/// an array is not. A host whose performer reads a nested member cannot express
/// an allow-list over it, and will get an admission rather than a refusal — so
/// the bound belongs on the top-level argument the performer takes, or the
/// performer belongs behind a narrower registration.
///
/// A CEILING bounds the declarative payload a capability carries — a host call's
/// arguments, a notification's payload. It does not bound an op sequence: those
/// are not a `JVal` at this point in compile order, measuring them would need
/// the wire codec that compiles after this file, and a ceiling that silently
/// meant one thing for three arms and another for two would be worse than one
/// that says which two it does not cover.
module ServerArgumentPolicy =

    /// The argument name a query's by-reference sources are allow-listed under.
    /// A constant rather than a spelling at each end, for the reason the
    /// capability itself is computed rather than written twice.
    [<Literal>]
    let SourceArgument = "source"

    /// The argument name a notification's channel is allow-listed under.
    [<Literal>]
    let ChannelArgument = "channel"

    /// The by-reference source names a data source reads. An embedded table
    /// carries its own rows and asks the host for nothing.
    let internal refsOfSource (source: Fuaran.Core.DataSource) : string list =
        match source with
        | Fuaran.Core.Embedded _ -> []
        | Fuaran.Core.Ref name -> [ name ]

    /// The by-reference source names one pipeline stage reads. Four arms of the
    /// pinned vocabulary take a second source; the rest work on the table they
    /// are handed. A stage whose second source went unread here would be a name
    /// reaching the host that neither the allow-list nor the demanded projection
    /// could see, so the arms are enumerated and there is deliberately no
    /// wildcard: raising the substrate pin surfaces a new source-bearing arm as
    /// an incomplete-match warning against a gate that runs at zero warnings.
    let internal refsOfTransform (transform: Fuaran.Core.Transform) : string list =
        match transform with
        | Fuaran.Core.Join(source, _, _) -> refsOfSource source
        | Fuaran.Core.Union source
        | Fuaran.Core.Intersect source
        | Fuaran.Core.Except source -> refsOfSource source
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

    /// The effect's named arguments, as `argument, value` pairs.
    ///
    /// Derived from the effect VALUE, so an allow-list is checked against what
    /// the effect will actually carry rather than against a description of it.
    /// An argument may appear more than once — a query reading three sources
    /// yields three `source` pairs — and an allow-list must admit every one of
    /// them, because a pipeline that reaches one off-list table has reached it.
    let arguments (effect: ServerEffect) : (string * string) list =
        match effect with
        | ServerEffect.HostCall(_, args, _) ->
            match args with
            | Fuaran.Core.JObj members ->
                members
                |> List.choose (fun (key, value) ->
                    match value with
                    | Fuaran.Core.JStr text -> Some(key, text)
                    | _ -> None)
            | _ -> []
        | ServerEffect.Notify(channel, _) -> [ ChannelArgument, channel ]
        | ServerEffect.RunQuery(_, source, pipeline) ->
            (refsOfSource source @ (pipeline |> List.collect refsOfTransform))
            |> List.map (fun name -> SourceArgument, name)
        | ServerEffect.ApplyOps _
        | ServerEffect.EmitPatch _ -> []

    /// The size of the effect's declarative payload, in bytes of its canonical
    /// encoding — the same bytes the wire carries, so a ceiling a deployer reads
    /// bounds the thing they would meet rather than an in-memory estimate of it.
    /// Zero for the arms that carry no such payload; see the module header.
    let payloadBytes (effect: ServerEffect) : int =
        let sizeOf (value: Fuaran.Core.JVal) =
            System.Text.Encoding.UTF8.GetByteCount(Fuaran.Program.Bounded.ProgramWire.render value)

        match effect with
        | ServerEffect.HostCall(_, args, _) -> sizeOf args
        | ServerEffect.Notify(_, payload) -> sizeOf payload
        | ServerEffect.RunQuery _
        | ServerEffect.ApplyOps _
        | ServerEffect.EmitPatch _ -> 0

    /// Check one effect's arguments against one declared clause.
    ///
    /// `Label` decides NOTHING and admits everything, deliberately: it is a word
    /// for a deployer and an auditor, and a check that invented a meaning for it
    /// would be enforcing a policy nobody wrote.
    let private checkClause
        (effect: ServerEffect)
        (clause: Fuaran.Program.Bounded.ServerConstraintClause)
        : Result<unit, ServerConstraintDefect> =
        match clause with
        | Fuaran.Program.Bounded.ServerConstraintClause.AllowList(argument, permitted) ->
            // VACUOUSLY TRUE when the effect carries no value under this
            // argument, and that is the correct reading rather than a lenient
            // one: an allow-list bounds what the effect REACHES under that name,
            // and an effect naming nothing there reaches nothing there. A host
            // that wants the argument to be mandatory is asking for a different
            // clause than the one it declared.
            if
                arguments effect
                |> List.forall (fun (name, value) -> name <> argument || List.contains value permitted)
            then
                Ok()
            else
                Error(ServerConstraintDefect.OffList argument)
        | Fuaran.Program.Bounded.ServerConstraintClause.Ceiling limit ->
            if payloadBytes effect <= limit then
                Ok()
            else
                Error(ServerConstraintDefect.OverCeiling limit)
        | Fuaran.Program.Bounded.ServerConstraintClause.Label _ -> Ok()

    /// Check an effect against every clause declared for its capability, in the
    /// order they are declared, reporting the FIRST that refuses.
    ///
    /// An effect whose capability the registry does not constrain passes — the
    /// list is empty, so the fold is vacuous, and "unconstrained" needs no arm of
    /// its own anywhere in this module.
    let check (registry: ServerEffectRegistry) (effect: ServerEffect) : Result<unit, ServerConstraintDefect> =
        ServerEffectRegistry.constraintsFor (ServerEffect.capability effect) registry
        |> List.fold (fun state clause -> state |> Result.bind (fun () -> checkClause effect clause)) (Ok())

    /// The refusal as the outcome document carries it: a closed token naming the
    /// CLAUSE that refused and the host's own declaration, never the value or the
    /// size that met it. Log-safe on exactly the terms a denial is.
    let describe (defect: ServerConstraintDefect) : string =
        match defect with
        | ServerConstraintDefect.OffList argument -> "argument-not-allowed:" + argument
        | ServerConstraintDefect.OverCeiling limit -> "payload-over-ceiling:" + string limit
