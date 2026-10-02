namespace Fuaran.Program.Server


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
type ServerEffect<'Op> =
    /// Evaluate `pipeline` over `source` and land the resulting table in the
    /// session's query slot `name`. A read: it touches no domain state.
    | RunQuery of name: string * source: Fuaran.Core.DataSource * pipeline: Fuaran.Core.Transform list
    /// Apply a `TreeOp` sequence to the domain tree through the apply engine.
    /// **The only domain-state mutation in the whole placement.**
    | ApplyOps of ops: 'Op list
    /// Call a named host performer with declarative arguments, optionally
    /// landing its result in the session's state slot `into`. The escape hatch
    /// the total algebra needs (D2), held behind registration and policy.
    | HostCall of fn: string * args: Fuaran.Core.JVal * into: string option
    /// Ship ops to the connected client without touching domain state.
    | EmitPatch of ops: 'Op list
    /// Send a host-channel message. The host performs the delivery; this
    /// placement records that the handler asked for it.
    | Notify of channel: string * payload: Fuaran.Core.JVal

module ServerEffect =

    /// The effect's discriminator — log-safe, and the coarse unit a gate can
    /// reason about ("this session may read, but may not mutate").
    let kind (effect: ServerEffect<'Op>) : string =
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
    let capability (effect: ServerEffect<'Op>) : string =
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

/// A host-declared post-condition on a performer's RESULT — SCIO*'s `import`
/// wrapper in this estate's shape (Phase 1759). A performer is unverified code;
/// what the handler can do about that is check what it ANSWERS before the
/// answer re-enters the interpreter's state, and that check is this: a name,
/// which is the host's own vocabulary and so safe to surface, and the
/// predicate. `ServerEffectRegistry.registerChecked` composes it with a
/// performer at registration, so the handler's perform phase never sees a
/// result the contract rejected — it sees a refusal, and rolls back.
///
/// `proofs/EffectGate.fst` (`contract`, `check_return`) models this record and
/// the wrapper and proves `return_contract`: every result that reaches the
/// store honours its contract, and a rejected one is a typed `PerformFailed`
/// naming the capability and the contract's NAME, never the value.
type ReturnContract =
    {
        /// What the host calls this contract — the one thing a refusal says.
        Name: string
        /// Whether a result satisfies it.
        Holds: Fuaran.Core.JVal -> bool
    }

module ReturnContract =

    /// The refusal's text, on the terms a denial keeps: the NAME and never the
    /// value. A result the contract rejected is not echoed anywhere.
    let describe (contract: ReturnContract) : string = "return-contract:" + contract.Name

    /// The wrapper: a result the contract rejects becomes the host's own
    /// refusal, carrying the contract's name; a raw refusal passes through
    /// unchanged. `EffectGate.check_return` is this, clause for clause.
    let check
        (contract: ReturnContract)
        (performer: Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>)
        : Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string> =
        fun args ->
            match performer args with
            | Error reason -> Error reason
            | Ok result ->
                if contract.Holds result then
                    Ok result
                else
                    Error(describe contract)

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

    /// Register a `HostCall` performer under a function name WITH a return
    /// contract: the performer the registry holds is `ReturnContract.check
    /// contract performer`, so a result the contract rejects reaches the
    /// handler as a refusal and never as a value. Registering does NOT permit,
    /// on exactly `register`'s terms.
    let registerChecked
        (fn: string)
        (contract: ReturnContract)
        (performer: Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>)
        (registry: ServerEffectRegistry)
        : ServerEffectRegistry =
        register fn (ReturnContract.check contract performer) registry

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
    /// for that argument does not permit, or that a declared deny-list for it
    /// refuses (Phase 1975). One token for both, deliberately: the refusal names
    /// the argument the host constrained, and which list excluded the value is a
    /// fact about the value this type never carries.
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
/// ── What an allow-list (or a deny-list) ranges over ─────────────────────────
/// The effect's NAMED arguments, as `arguments` below derives them. All five
/// arms carry some: a host call's declarative argument object, a notification's
/// channel, the by-reference sources a query reads, and — since Phase 1967 —
/// what the ops of the two op-carrying arms REACH, read through the op
/// witness's `Reach` (`StateWitness`, §3.1): each op's named arguments, plus its
/// destination's class under the reserved `destination` argument where the op
/// names one. Before that member existed an op sequence named nothing a policy
/// could see, and a handler that wrote files and pushed a branch was bounded
/// by nothing but the word `ApplyOps`; the second witness found that gap (W3,
/// W4) and this is where it closes.
///
/// ── What is deliberately NOT claimed ────────────────────────────────────────
/// A host call's arguments are read ONE LEVEL DEEP: a top-level member whose
/// value is a string is a named argument, and since Phase 1982 so is one whose
/// value is an integer, as its decimal text, so that an `AtMost` declared on a
/// number sees the number. A value nested inside an object or an array is not,
/// and neither is a boolean or a fractional number. A host whose performer reads a nested member cannot express
/// an allow-list over it, and will get an admission rather than a refusal — so
/// the bound belongs on the top-level argument the performer takes, or the
/// performer belongs behind a narrower registration. An op's arguments are
/// whatever its witness declares them to be, and a witness that declares none
/// gets the same admission for the same reason.
///
/// A CEILING bounds the declarative payload a capability carries — a host call's
/// arguments, a notification's payload, and, through the op witness's canonical
/// encoder, the ops an op-carrying arm carries, measured as the sum of their
/// encoded bytes. A query's pipeline is still not measured: it is a
/// host-authored declaration, not a payload, and the sources it reaches are
/// what the allow-list ranges over.
module ServerArgumentPolicy =

    /// The argument name a query's by-reference sources are allow-listed under.
    /// A constant rather than a spelling at each end, for the reason the
    /// capability itself is computed rather than written twice.
    [<Literal>]
    let SourceArgument = "source"

    /// The argument name a notification's channel is allow-listed under.
    [<Literal>]
    let ChannelArgument = "channel"

    /// The argument name an op's DESTINATION CLASS is allow-listed under
    /// (Phase 1967): the log-safe description of its `EffectDestination` —
    /// `local`, a remote's host, a non-network scheme — so a policy can bound
    /// where an op may reach without knowing how the domain names it.
    /// Reserved: an op witness that declares an argument of this name itself
    /// has the two read as one list, and an allow-list must admit both.
    [<Literal>]
    let DestinationArgument = "destination"

    /// What one op reaches, as `(argument, value)` pairs: its declared
    /// arguments, then its destination class where it names one — for the op
    /// itself AND every op beneath it in its view (Phase 1976: a branch's
    /// entry, both arms and exit, a repeat's body; Phase 1990: an `Each`'s
    /// lowered elements, each with its element substituted, so the policy
    /// sees every address a placeholder stands for), because an untaken arm's
    /// reach is still reach, and a sequence that reaches an off-list path in
    /// either arm has reached it. The one place an op's reach is read into
    /// the policy's vocabulary, so the demanded projection reads it here too
    /// rather than deriving its own.
    let reachOfOp (ops: Fuaran.Program.Bounded.StateWitness<'Node, 'Op>) (op: 'Op) : (string * string) list =
        let ofOne (op: 'Op) =
            let reach = ops.Reach op

            match reach.Destination with
            | Fuaran.Program.Runtime.EffectDestination.Absent -> reach.Arguments
            | destination ->
                reach.Arguments
                @ [ DestinationArgument, Fuaran.Program.Runtime.EffectDestination.describe destination ]

        op :: Fuaran.Program.Bounded.OpView.beneath ops.View ops.Substitute op
        |> List.collect ofOne

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
    let arguments
        (ops: Fuaran.Program.Bounded.StateWitness<'Node, 'Op>)
        (effect: ServerEffect<'Op>)
        : (string * string) list =
        match effect with
        // A host call's top-level string AND integer members (the integer as
        // its decimal text, Phase 1982): an `AtMost` declared on a numeric
        // argument must see the number, and a clause that could not see it
        // would pass vacuously over exactly the value it bounds.
        | ServerEffect.HostCall(_, args, _) ->
            match args with
            | Fuaran.Core.JObj members ->
                members
                |> List.choose (fun (key, value) ->
                    match value with
                    | Fuaran.Core.JStr text -> Some(key, text)
                    | Fuaran.Core.JInt number -> Some(key, string number)
                    | _ -> None)
            | _ -> []
        | ServerEffect.Notify(channel, _) -> [ ChannelArgument, channel ]
        | ServerEffect.RunQuery(_, source, pipeline) ->
            (refsOfSource source @ (pipeline |> List.collect refsOfTransform))
            |> List.map (fun name -> SourceArgument, name)
        // Every op's reach, in op order: an arm carrying three writes yields
        // three `path` pairs, and an allow-list must admit every one of them,
        // because a sequence that reaches one off-list path has reached it.
        | ServerEffect.ApplyOps opList
        | ServerEffect.EmitPatch opList -> opList |> List.collect (reachOfOp ops)

    /// The size of the effect's declarative payload, in bytes of its canonical
    /// encoding — the same bytes the wire carries, so a ceiling a deployer reads
    /// bounds the thing they would meet rather than an in-memory estimate of it.
    /// Zero for the arms that carry no such payload; see the module header.
    let payloadBytes (ops: Fuaran.Program.Bounded.StateWitness<'Node, 'Op>) (effect: ServerEffect<'Op>) : int =
        let sizeOf (value: Fuaran.Core.JVal) =
            System.Text.Encoding.UTF8.GetByteCount(Fuaran.Program.Bounded.ProgramWire.render value)

        match effect with
        | ServerEffect.HostCall(_, args, _) -> sizeOf args
        | ServerEffect.Notify(_, payload) -> sizeOf payload
        // The ops' canonical bytes, summed — the same encoder that splices
        // them into the handler's wire form (K6), so the ceiling bounds the
        // bytes a handler document carries rather than an in-memory estimate.
        | ServerEffect.ApplyOps opList
        | ServerEffect.EmitPatch opList ->
            opList
            |> List.sumBy (fun op -> System.Text.Encoding.UTF8.GetByteCount(ops.Stream.Encode op))
        | ServerEffect.RunQuery _ -> 0

    /// An argument's value read as an integer, in its CANONICAL decimal
    /// spelling only (Phase 1982): an optional minus sign and digits, no sign
    /// on zero, no leading zero, no whitespace, at most eighteen digits. A
    /// second spelling of one number would let a value pass the bound under
    /// one reading and reach the performer under another, so anything else is
    /// not an integer here. Written out rather than parsed by the platform so
    /// it reads the same under Fable.
    let private integerOf (value: string) : int64 option =
        let negative = value.StartsWith "-"
        let digits = if negative then value.Substring 1 else value

        if
            digits.Length = 0
            || digits.Length > 18
            || not (digits |> Seq.forall (fun c -> c >= '0' && c <= '9'))
            || (digits.Length > 1 && digits.[0] = '0')
            || (negative && digits = "0")
        then
            None
        else
            let magnitude =
                digits |> Seq.fold (fun acc c -> acc * 10L + int64 (int c - int '0')) 0L

            Some(if negative then -magnitude else magnitude)

    /// Check one effect's arguments against one declared clause.
    ///
    /// `Label` decides NOTHING and admits everything, deliberately: it is a word
    /// for a deployer and an auditor, and a check that invented a meaning for it
    /// would be enforcing a policy nobody wrote.
    let private checkClause
        (ops: Fuaran.Program.Bounded.StateWitness<'Node, 'Op>)
        (effect: ServerEffect<'Op>)
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
                arguments ops effect
                |> List.forall (fun (name, value) -> name <> argument || List.contains value permitted)
            then
                Ok()
            else
                Error(ServerConstraintDefect.OffList argument)
        | Fuaran.Program.Bounded.ServerConstraintClause.Ceiling limit ->
            if payloadBytes ops effect <= limit then
                Ok()
            else
                Error(ServerConstraintDefect.OverCeiling limit)
        | Fuaran.Program.Bounded.ServerConstraintClause.Label _ -> Ok()
        | Fuaran.Program.Bounded.ServerConstraintClause.DenyList(argument, refused) ->
            // Phase 1975 — the complement of the allow-list, on the SAME reading:
            // vacuously true when the effect carries no value under this
            // argument, because a deny-list bounds what the effect reaches
            // there and an effect naming nothing there reaches nothing there.
            // What it does NOT need is a universe: a name the handler created
            // during this run is admitted unless the list names it, which is
            // the case an allow-list over the names that existed before the run
            // refuses.
            if
                arguments ops effect
                |> List.forall (fun (name, value) -> name <> argument || not (List.contains value refused))
            then
                Ok()
            else
                Error(ServerConstraintDefect.OffList argument)
        | Fuaran.Program.Bounded.ServerConstraintClause.AtMost(argument, limit) ->
            // Phase 1982 — the allow-list's reading over a number: vacuously
            // true when the effect names nothing under the argument, and every
            // value it does name must read as an integer no greater than the
            // limit. A value that does not read as one is refused under the
            // same token, which names the argument and never the value.
            if
                arguments ops effect
                |> List.forall (fun (name, value) ->
                    name <> argument
                    || (match integerOf value with
                        | Some n -> n <= int64 limit
                        | None -> false))
            then
                Ok()
            else
                Error(ServerConstraintDefect.OffList argument)

    /// Check an effect against every clause declared for its capability, in the
    /// order they are declared, reporting the FIRST that refuses.
    ///
    /// A deny-list and an allow-list on ONE argument compose as a domain gate
    /// composes a lock over a writable set — the lock wins, so a value on both
    /// is refused — and that holds whatever order a host declared them in: each
    /// clause must admit every value, so a value either one excludes is refused,
    /// and both refuse with the same `OffList argument`. Declaration order
    /// decides only WHICH defect a value failing clauses of DIFFERENT kinds is
    /// reported under — a `Ceiling` declared before a `DenyList` is reported
    /// first, and after it, second.
    ///
    /// An effect whose capability the registry does not constrain passes — the
    /// list is empty, so the fold is vacuous, and "unconstrained" needs no arm of
    /// its own anywhere in this module.
    let check
        (ops: Fuaran.Program.Bounded.StateWitness<'Node, 'Op>)
        (registry: ServerEffectRegistry)
        (effect: ServerEffect<'Op>)
        : Result<unit, ServerConstraintDefect> =
        ServerEffectRegistry.constraintsFor (ServerEffect.capability effect) registry
        |> List.fold (fun state clause -> state |> Result.bind (fun () -> checkClause ops effect clause)) (Ok())

    /// The refusal as the outcome document carries it: a closed token naming the
    /// CLAUSE that refused and the host's own declaration, never the value or the
    /// size that met it. Log-safe on exactly the terms a denial is.
    let describe (defect: ServerConstraintDefect) : string =
        match defect with
        | ServerConstraintDefect.OffList argument -> "argument-not-allowed:" + argument
        | ServerConstraintDefect.OverCeiling limit -> "payload-over-ceiling:" + string limit

/// How this placement PERFORMS an op (Phase 1967, the second witness's F2).
///
/// `ApplyOps` has always applied its ops to the in-memory tree while planning,
/// and for a placement whose tree IS its state — the UI tier — that apply is
/// the whole effect. A placement whose ops reach the world (a file written, a
/// branch pushed) needs the apply to be a PLAN and the world act to come
/// after, with the same two-phase discipline a host call gets: nothing outside
/// runs until the plan committed, and a failure part-way is reported as the
/// prefix that ran. Before this type existed that discipline was the
/// placement's to build beside Program; now it is Program's, by registration.
///
/// **The performer is handed the STATE as of the op (Phase 1974, the third
/// witness's F-PERFORM).** A verb whose op IS the act needs only the op; a
/// tail that persists what the plan produced — render the document, commit
/// the ops — needs the state, and before this it could have it only by
/// folding the ops itself from the entry state, in the trusted base: the plan
/// phase run twice, once inside Program and once outside it. The state handed
/// with an op is the planned state WITH THAT OP APPLIED, so the state handed
/// with the last op performed is the state the plan produced
/// (`performer_handed_the_plan` in `proofs/Staging.fst`); the state before an
/// op is the state handed with the one before it, or the entry state the host
/// passed in. A guard (`OpView.Require`) is never handed to the performer.
[<RequireQualifiedAccess>]
type OpPerformance<'Node, 'Op> =
    /// The in-memory apply is the whole effect, performed in the plan phase —
    /// the UI tier's placement, and every placement's default. `Performed`
    /// carries `ApplyOps` once per effect, at plan time, as it always has.
    | InMemory
    /// The in-memory apply is a plan; this performer performs it. Each op of a
    /// committed plan is STAGED as its own call and performed in the perform
    /// phase, in plan order, interleaved with the host calls in stage order —
    /// so `Performed` carries `ApplyOps` once per op PERFORMED, in execution
    /// order, and a part-way failure reports exactly the ops that ran before it
    /// (D8's residual, unchanged in shape) under a `PerformFailed` naming the
    /// capability and the performer's own reason. Handed the state as of the
    /// op, then the op, and answers a RECEIPT (Phase 1981): what it says it
    /// did, as a `JVal` — the paths it wrote, the sha it committed, or the
    /// inert empty object for a performer with nothing to say. The receipt
    /// lands in no slot and reaches no wire; the durable journal records it
    /// as the step's completed value (D23), and a contract declared at
    /// registration (`performedChecked`) checks it against the planned state
    /// and the op before the op is reported as performed. Constrained on the
    /// terms a host function is (D24): the gate and the policy decide what
    /// reaches it, and the contract decides what it may claim to have done.
    | Performed of ('Node -> 'Op -> Result<Fuaran.Core.JVal, string>)

/// A host-declared post-condition on an op performer's RECEIPT (Phase 1981) —
/// `ReturnContract` keyed by the state and the op: a name, which is the host's
/// own vocabulary and so safe to surface, and a predicate over the planned
/// state the performer was handed, the op, and what it answered. The op's
/// REACH (`StateWitness.Reach`, D19) is a declaration the policy enforces
/// against; a receipt is the domain's route to checking that the declaration
/// held — a contract that reads the paths a receipt names and the paths the
/// op's reach names is what turns "the reach covers what the performer
/// touches" from an obligation stated into one checked.
///
/// `proofs/EffectGate.fst` (`op_contract`, `check_op`) models this record and
/// the wrapper: `check_op_is_check_return` says it is the return contract at
/// the state and the op, and `op_return_contract` says every landed receipt
/// honours it there and a rejected one is a typed `PerformFailed` naming the
/// contract, never the receipt, with the op never reported as performed.
type OpContract<'Node, 'Op> =
    {
        /// What the host calls this contract — the one thing a refusal says.
        Name: string
        /// Whether a receipt satisfies it, at the planned state and the op.
        Holds: 'Node -> 'Op -> Fuaran.Core.JVal -> bool
    }

module OpContract =

    /// The refusal's text, in the ONE vocabulary a return contract's refusal
    /// has (`ReturnContract.describe`): the NAME and never the receipt.
    let describe (contract: OpContract<'Node, 'Op>) : string = "return-contract:" + contract.Name

    /// The wrapper `OpPerformance.performedChecked` composes: a receipt the
    /// contract rejects becomes the host's own refusal, carrying the
    /// contract's name; a raw refusal passes through unchanged.
    /// `EffectGate.check_op` is this, clause for clause.
    let check
        (contract: OpContract<'Node, 'Op>)
        (perform: 'Node -> 'Op -> Result<Fuaran.Core.JVal, string>)
        : 'Node -> 'Op -> Result<Fuaran.Core.JVal, string> =
        fun state op ->
            match perform state op with
            | Error reason -> Error reason
            | Ok receipt ->
                if contract.Holds state op receipt then
                    Ok receipt
                else
                    Error(describe contract)

module OpPerformance =

    /// **The op performer's registration key (Phase 1983, D26)** — the name an
    /// operator control withdraws it by: `Controls.revoke actor reason
    /// OpPerformance.RegistrationKey`. A host performer is revoked by the
    /// function name it was registered under; the op performer is revoked by
    /// this.
    ///
    /// FIXED and reserved rather than declared by the host at registration,
    /// because a placement registers at most one op performer (the slot
    /// `PerformerFacets.OpPerformer` is one slot for the same reason) and a
    /// control stream is scoped to one session, so the key has nothing to tell
    /// apart but the host's own function names. It is the capability the op
    /// performer's stages are already gated, throttled, journaled and reported
    /// under, so an operator meets one name for the arm everywhere. A host
    /// function registered under this same name is withdrawn with it by one
    /// revoke — the over-broad direction, which is the safe one for a kill
    /// switch.
    [<Literal>]
    let RegistrationKey = "ApplyOps"

    /// The default: ops are performed by being applied.
    let inMemory<'Node, 'Op> : OpPerformance<'Node, 'Op> = OpPerformance.InMemory

    /// Register an op performer: handed the state as of each op and the op,
    /// answering a receipt. No contract: the receipt is recorded and nothing
    /// checks it (`uncontracted_is_direct` in `proofs/EffectGate.fst`).
    let performedBy (perform: 'Node -> 'Op -> Result<Fuaran.Core.JVal, string>) : OpPerformance<'Node, 'Op> =
        OpPerformance.Performed perform

    /// Register an op performer WITH a contract over its receipt: the
    /// performer the placement holds is `OpContract.check contract perform`,
    /// so a receipt the contract rejects reaches the handler as a refusal
    /// naming the contract and never as a performed op — the `registerChecked`
    /// shape, on the op axis.
    let performedChecked
        (contract: OpContract<'Node, 'Op>)
        (perform: 'Node -> 'Op -> Result<Fuaran.Core.JVal, string>)
        : OpPerformance<'Node, 'Op> =
        OpPerformance.Performed(OpContract.check contract perform)

    /// Register an op performer with nothing to say: its receipt is the inert
    /// empty object — the honest spelling of "an op lands nothing", since the
    /// wire value has no null — which no contract can be declared over.
    let performedWithoutReceipt (perform: 'Node -> 'Op -> Result<unit, string>) : OpPerformance<'Node, 'Op> =
        OpPerformance.Performed(fun state op -> perform state op |> Result.map (fun () -> Fuaran.Core.JObj []))
