namespace Fuaran.Program.Bounded

open Fuaran.Core

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
/// vocabulary (`staged-query` since version 10, D44). Both are derived facts, so a posture is log-safe on exactly the
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

/// One reason a handler is not provably reversible, as the document carries it
/// (Phase 1977): a stage ORDINAL and a token from the undo classification's
/// closed defect vocabulary — `compensated-op`, `one-way-op`,
/// `opaque-host-call`, `outbound-notification`, `emitted-patch`,
/// `compute-outside-fragment`, and since version 10 `staged-query` (D44). Derived facts both, so a posture is log-safe on
/// the terms the replay posture is.
type UndoReasonDemand =
    {
        /// The stage's position in the declared list, zero-based.
        Stage: int
        /// The defect's wire token.
        Defect: string
    }

/// One handler's UNDO posture, derived from its declared form and the state
/// witness's `Undo` member (Phase 1977, DECISIONS.md D22): whether, before it
/// runs, a deployer can read that its run will be undoable to the byte, in
/// effect only, or not at all — and from which stage.
///
/// Carried per handler, as the replay posture is, because that is the
/// granularity the decision is made at. `Reasons` is empty exactly when `Undo`
/// is `reversible`; a `one-way` posture's FIRST reason graded one-way names
/// the stage after which nothing can be undone.
type UndoPosture =
    {
        /// The handler's registration key.
        Handler: string
        /// `reversible` | `compensable` | `one-way` | `unknown`.
        Undo: string
        /// Why, in stage order. Empty for `reversible`.
        Reasons: UndoReasonDemand list
    }

/// One clause of a capability's declared argument policy — the vocabulary a
/// host writes a bound in, and the one this document carries it in.
///
/// **A closed set of five, and the closure is the point**: this is what a
/// policy gate can decide about an effect's ARGUMENTS without interpreting a
/// payload, so a further kind is a deliberate widening of what the gate reasons
/// over rather than a new option on a record. The fourth, `DenyList`, was such a
/// widening (Phase 1975): "everything except these" has no allow-list spelling
/// that survives a run which creates the names it later addresses. The fifth,
/// `AtMost`, was another (Phase 1982): "at most N" has an allow-list spelling
/// only as the N+1 literals it permits.
///
/// **A clause is PRESENT or it is not; there is no "unconstrained" clause.** An
/// argument nobody allow-listed, a payload nobody bounded and a capability
/// nobody labelled are all expressed by silence, which is why this is a list of
/// what was declared rather than a record of nullable bounds. A host that never
/// declared a bound has not declared an empty one, and the two must not be one
/// shape with a blank in it.
[<RequireQualifiedAccess>]
type ServerConstraintClause =
    /// The values permitted for ONE named argument. The argument is named rather
    /// than positional because an effect's arguments are a declarative object,
    /// not a tuple — and because "which value did the host constrain" is what a
    /// deployer reading this is asking. An EMPTY permitted list is a real
    /// declaration ("this argument may carry nothing"), never an absent one.
    | AllowList of argument: string * permitted: string list
    /// A ceiling on the payload's canonical encoded size, in bytes.
    | Ceiling of bytes: int
    /// A disclosure label. CARRIED, never checked — it is a word for a deployer
    /// and an auditor, and inventing a meaning for it here would make it a
    /// policy nobody wrote.
    | Label of label: string
    /// The values REFUSED for one named argument (Phase 1975) — the complement
    /// of `AllowList`, and the shape of a domain's locked set: every value is
    /// admitted except the ones named. It exists because an allow-list can only
    /// say "everything except these" over a universe read BEFORE the run, and
    /// that universe is stale the moment a handler creates a name and then
    /// addresses it. Beside an allow-list on the same argument it composes as a
    /// lock over a writable set: a value either list excludes is refused, so a
    /// name on both is refused. An EMPTY refused list is a real declaration that
    /// refuses nothing, never an absent one.
    | DenyList of argument: string * refused: string list
    /// A ceiling on one named argument's INTEGER value (Phase 1982, the second
    /// witness's R2): every value under the argument must be an integer no
    /// greater than `limit`. A value that is not an integer is refused rather
    /// than ignored, because a bound a host declared on a number is not met by
    /// a value the gate cannot read as one. Vacuously true where the effect
    /// names nothing under the argument, on the allow-list's reading. A
    /// repeat's `count` is the case that motivated it: "at most 90 polls" used
    /// to be an allow-list of the 91 literals `0`..`90`.
    | AtMost of argument: string * limit: int

/// One capability's declared argument policy, as the document carries it.
///
/// It is the HOST's declaration travelling beside the handlers' demand, which is
/// what makes the pair readable as "HTTP to api.example.com, ≤ 64 KB" rather
/// than as "HTTP". It is not itself a demand, and no coverage finding is
/// computed from it: the enforcement happens at the placement, where the
/// arguments are, and this is the fact that enforcement runs on — published so a
/// deployer reads the bound without holding the registry it came from.
type ServerConstraintDemand =
    {
        /// The capability the clauses govern — the same string the gate is asked
        /// about, so a constrained capability and a gated one cannot be two
        /// spellings of an intention.
        Capability: string
        /// What was declared for it. A capability with NO clauses never appears
        /// here at all, so this list is never empty in a normalised document.
        Clauses: ServerConstraintClause list
    }

/// One name an op-carrying arm REACHES, as the document carries it (Phase
/// 1967, the second witness's W3): the capability the op rides, the argument
/// the op names it under, and the name itself — a path, a remote, a node id,
/// or the `destination` class the op witness classified it as.
///
/// The same `(argument, name)` pair the server placement's argument policy
/// reads off the effect and an allow-list binds, so a reach the document
/// reports and a bound the host declares meet on one vocabulary. Every member
/// is an op witness's own answer about a host-registered handler's ops — an
/// author-declared name, never an op's payload (`OpReach`). A finding's two
/// tokens ride it too (Phase 2195, D45): under `Report`, the `code` and the
/// `severity` a handler can report — the pairs an allow-list on that
/// capability binds — and never its message.
type OpReachDemand =
    {
        /// The capability the op rides — `ApplyOps` or `EmitPatch`, or `Report`
        /// for a finding's tokens — which is
        /// the capability the gate was asked about and a clause is declared on.
        Capability: string
        /// The argument the op names the reach under.
        Argument: string
        /// What it names there.
        Name: string
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
        /// What the handlers' OPS reach — every named argument of every op the
        /// two op-carrying arms carry, with the capability it rides (Phase
        /// 1967). The half of a handler's envelope that was invisible before:
        /// a document that said only `ApplyOps` for a handler that writes
        /// files and pushes a branch now says which paths and which remote.
        ///
        /// DESCRIPTIVE, like `Replay` and `Constraints`: no coverage finding is
        /// computed from it. The enforcement is the argument policy at the
        /// placement, which reads the same reach off the same ops; this is the
        /// fact it enforces against, published so a deployer reads the bound
        /// (`Constraints`) beside what it bounds without holding the registry.
        Reach: OpReachDemand list
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
        /// The undo posture of each handler that contributed to this tier
        /// (Phase 1977), on exactly the terms `Replay` is carried: DESCRIPTIVE,
        /// no coverage finding computed from it, the fact a deployer reads
        /// before a program runs — "can this be undone, and up to where?" —
        /// beside what it can reach. The enforcement is the undo run's own
        /// refusal at the placement, which reads the same plan.
        Undo: UndoPosture list
        /// The argument policy the HOST declared for the capabilities this tier
        /// demands — one clause per constrained capability.
        ///
        /// DESCRIPTIVE here, exactly as `Replay` is, and for a sharper version
        /// of the same reason: it is not something a handler asks of a host, it
        /// is something the host answered in advance. A capability the host
        /// constrained but these handlers never name contributes nothing, so the
        /// document stays about this program on this host; a capability they
        /// name and the host did not constrain contributes nothing either, and
        /// that silence reads as unconstrained rather than as an empty bound.
        Constraints: ServerConstraintDemand list
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
        /// The collections the store holds that a store-bound `Each` iterates
        /// at run time, each with its ceiling (Phase 1991, D36) — on both
        /// tiers: a compute stage's or a handler's dispatch-axis loop names the
        /// binding keys its source reads, an op-channel loop names the state
        /// collection. "Iterates THIS collection, at most N" is what the
        /// signed envelope then says. Empty for every program with none,
        /// which is every program before this phase.
        Iterations: IterationDemand list
        /// The leaves the tree reaches that declare themselves OPAQUE (Phase
        /// 2130, D40): each an act no walk can see into, named with the reason
        /// class that makes it so. Distinct from `OpaqueHandlers`, which names
        /// NODES whose actions the wire could not carry: these are actions the
        /// wire DID carry, whose behaviour lives in the host. Empty for every
        /// program whose witness declares no opaque leaf.
        OpaqueLeaves: OpaqueLeaf list
        /// The values the planned state holds that a `Let` over ops resolves
        /// at run time (Phase 2186, D42), each by the name that spells its
        /// reads, with the absolute targets its body writes — the value
        /// channel on the state axis, named so a reader tells a computed
        /// write from a literal one. "Writes THESE from THIS" is what the
        /// signed envelope then says. Empty for every program with none,
        /// which is every program before this phase.
        Values: ValueDemand list
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
///
/// `Withdrawn` (Phase 1993, D37) is the registration fact for the arms that
/// need no host function: the gate-facing capabilities whose performer this
/// host has WITHDRAWN. It is empty by default, and empty means what it meant
/// before the member existed — every such arm meets the gate alone. A
/// withdrawal is an absence, not a policy: a capability named here is reported
/// as `ServerCapabilityWithdrawn` before the gate is asked about it, however
/// the gate is written.
type ServerCoverage =
    { HostFunctions: Set<string>
      Gate: string -> bool
      Channels: Set<string> option
      Withdrawn: Set<string> }

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
        /// The OPAQUE-LEAF reason classes this host accepts running (Phase
        /// 2130, D40). Empty by default, and empty REFUSES: every opaque leaf a
        /// tree reaches is reported as `UnacceptedOpaqueLeaf` until the host
        /// names its reason class here. Default-deny on the effects' terms
        /// rather than unconstrained on the host calls', because an opaque leaf
        /// is not a call that goes nowhere on the bounded path — it is
        /// behaviour the host performs and nothing in the tree describes, and
        /// accepting that is a statement the host makes, not one it is spared.
        Opaque: Set<string>
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
    /// The tree reaches a leaf that declares itself opaque, for a reason class
    /// this host has not accepted (`HostCoverage.Opaque`, Phase 2130). Not a
    /// policy refusal and not an absent capability: the host is being told
    /// that something it would run cannot be analysed, and is asked to say
    /// whether it runs such things at all.
    | UnacceptedOpaqueLeaf of reason: string * name: string
    /// A reachable handler names a host function this host registered no
    /// performer for. The server-tier counterpart of `UnregisteredEffect`, and a
    /// separate arm from it because `Notify` exists in BOTH vocabularies — a
    /// finding that named only the string would leave a host guessing which
    /// placement it had failed to serve.
    | UnregisteredServerFunction of fn: string
    /// A reachable handler can emit an arm whose capability this host has
    /// WITHDRAWN the performer of (`ServerCoverage.Withdrawn`, Phase 1993) —
    /// an arm that needs no host function, such as the op stage. The
    /// capability is absent: no policy makes it reachable. A separate arm from
    /// `UnregisteredServerFunction` because the handler names no host function
    /// here, and a finding that said it did would send a host looking for a
    /// registration it was never asked to make.
    | ServerCapabilityWithdrawn of capability: string
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
          Channels = None
          Withdrawn = Set.empty }

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

    /// Declare the gate-facing capabilities whose performer this host has
    /// withdrawn. Each reads as `ServerCapabilityWithdrawn` wherever a handler
    /// demands it, before the gate is consulted — a withdrawal is not a policy
    /// a gate can relax.
    let withWithdrawn (capabilities: string seq) (coverage: ServerCoverage) : ServerCoverage =
        { coverage with
            Withdrawn = Set.ofSeq capabilities }

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
          Server = None
          Opaque = Set.empty }

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

    /// Accept running opaque leaves of these reason classes. Until this is
    /// called every opaque leaf a tree reaches is a finding — see the field's
    /// note for why that is default-deny.
    let acceptingOpaque (reasons: string seq) (coverage: HostCoverage) : HostCoverage =
        { coverage with
            Opaque = Set.ofSeq reasons }

/// Why a demanded document was refused, as a CLASS rather than a message.
///
/// The same posture the rest of this subsystem takes: "my reader threw" and "my
/// reader applied the rule" are different facts, and only the second is
/// something a producer can act on. A caller branches on the class; the `Detail`
/// beside it is for a human reading a log and is never compared.
[<RequireQualifiedAccess>]
type DemandedDefect =
    /// The bytes are not readable JSON under the wire's own parsing discipline.
    | NotJson
    /// The document's root is not an object.
    | NotAnObject
    /// The document's `kind` is not this document's kind.
    | UnknownKind
    /// The document declares a version this reader does not read. REFUSED, and
    /// never read through an earlier version's lens — a shape read under the
    /// wrong lens produces a value that looks right and is not, which is the one
    /// failure a coverage consumer has no way to detect downstream.
    | UnknownVersion
    /// A member this version requires is absent.
    | MissingMember
    /// A member is present carrying the wrong JSON type.
    | WrongType
    /// The document carries a member this version does not declare.
    | UndeclaredMember
    /// Every member read, but the document's lists are not in the distinct,
    /// sorted order the document's own contract promises — so two such documents
    /// would not compare by value, which is the property the whole encoding
    /// exists to provide.
    | NotCanonical

/// One refusal: the class, the member it is at, the version it was read under,
/// and a human detail.
///
/// `Field` is a DERIVED path — dotted, with array positions as ordinals
/// (`server.replay[0].reasons[1].defect`) — so it names a place in the document
/// without echoing a string the document chose. `Version` is present from the
/// moment one has been read, which is the point at which every later refusal
/// becomes a statement about a particular version rather than about documents in
/// general.
type DemandedDecodeFailure =
    { Defect: DemandedDefect
      Field: string
      Version: int option
      Detail: string }

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

    /// What ONE action demands, read through the witness's view: the effect
    /// kinds, the host calls and the state-namespace touches (namespace,
    /// written).
    ///
    /// TOTAL over the four view shapes, with no wildcard. The vocabulary's own
    /// arms reach this function as `Leaf` declarations — the effect kinds and
    /// host channels each one names — so the enumeration of which arm demands
    /// what lives in the witness's `View`, where the closed union is matched
    /// exhaustively and a new arm cannot be added silently. What remains here
    /// is the control structure every domain shares:
    ///
    ///   - `Sequence` demands the union of its members, in order;
    ///   - `Assign` writes its key's namespace, and its `from` expression reads
    ///     what the witness's `Expr.Uses` says it reads — a state key is a
    ///     namespace read, a query slot a `Query` host call;
    ///   - `Call` names its endpoint on the call channel, whether or not it
    ///     declares a target (a declared target is refused at dispatch, and a
    ///     host that cannot serve the endpoint is still worth telling);
    ///   - `Leaf` demands exactly what it declares, and an opaque leaf is
    ///     named among the opaque leaves (Phase 2130).
    let rec private demandsOfAction
        (fold: DispatchFold<'Action, 'Expr, 'Store, 'Effect>)
        (action: 'Action)
        : string list * HostCallDemand list * (string * bool) list * IterationDemand list * OpaqueLeaf list =
        match fold.Action.View action with
        | ActionView.Sequence actions ->
            actions
            |> List.fold
                (fun (accE, accH, accN, accI, accO) a ->
                    let e, h, n, i, o = demandsOfAction fold a
                    accE @ e, accH @ h, accN @ n, accI @ i, accO @ o)
                ([], [], [], [], [])

        | ActionView.Assign(key, _, from) ->
            let uses =
                match from with
                | Some expr -> fold.Expr.Uses expr
                | None -> []

            let reads =
                uses
                |> List.choose (fun u ->
                    match u with
                    | BindingUse.State stateKey -> Some(namespaceOf stateKey, false)
                    | BindingUse.Query _ -> None)

            let calls =
                uses
                |> List.choose (fun u ->
                    match u with
                    | BindingUse.Query name -> Some { Channel = "Query"; Name = name }
                    | BindingUse.State _ -> None)

            [], calls, (namespaceOf key, true) :: reads, [], []

        | ActionView.Call(endpoint, _) ->
            [],
            [ { Channel = CallChannel
                Name = endpoint } ],
            [],
            [],
            []

        // A guard READS what its condition reads, exactly as an assignment's
        // `from` does, and writes nothing (Phase 1967).
        | ActionView.Require condition ->
            let calls, reads = usesOf fold condition
            [], calls, reads, [], []

        // A branch demands the UNION of its parts (Phase 1976): what its entry
        // condition reads, what BOTH arms demand — an untaken arm's reach is
        // still reach, and which arm runs is decided at dispatch against a
        // store this projection cannot see — and what its exit assertion
        // reads. In order: entry, true arm, false arm, exit.
        | ActionView.Choose(entry, whenTrue, whenFalse, exit) ->
            let entryCalls, entryReads = usesOf fold entry
            let tE, tH, tN, tI, tO = demandsOfAction fold whenTrue
            let fE, fH, fN, fI, fO = demandsOfAction fold whenFalse

            let exitCalls, exitReads =
                match exit with
                | Some assertion -> usesOf fold assertion
                | None -> [], []

            tE @ fE, entryCalls @ tH @ fH @ exitCalls, entryReads @ tN @ fN @ exitReads, tI @ fI, tO @ fO

        // A repeat demands what its bound reads — a parameter bound is an
        // expression resolved at dispatch, a literal reads nothing — and what
        // its body demands, ONCE: a demand is a name, and the body's names do
        // not change with the count (the budget prices the count).
        | ActionView.Repeat(bound, body) ->
            let boundCalls, boundReads =
                match bound with
                | Bound.Parameter(count, _, _) -> usesOf fold count
                | Bound.Literal _ -> [], []

            let bE, bH, bN, bI, bO = demandsOfAction fold body
            bE, boundCalls @ bH, boundReads @ bN, bI, bO

        // An `Each` demands what its LOWERED form demands (Phase 1990): the
        // union, in collection order, over its body with each element
        // substituted — read exactly as a sequence of those bodies is, so a
        // name an element's substitution makes reachable is reached. A literal
        // collection reads nothing of its own; an empty one demands nothing.
        | ActionView.Each(Collection.Literal elements, placeholder, body) ->
            ActionWitness.lowered fold.Action elements placeholder body
            |> List.fold
                (fun (accE, accH, accN, accI, accO) a ->
                    let e, h, n, i, o = demandsOfAction fold a
                    accE @ e, accH @ h, accN @ n, accI @ i, accO @ o)
                ([], [], [], [], [])

        // A store-bound `Each` (Phase 1991, D36) demands what its SOURCE reads
        // — an expression resolved at entry, as a parameter bound is — what
        // its body demands ONCE, with the placeholder standing (its elements
        // are not in the tree), and the ITERATION itself: the collection by
        // the name the source's binding keys give it, and the ceiling.
        | ActionView.Each(Collection.Stored(source, ceiling), _, body) ->
            let sourceCalls, sourceReads = usesOf fold source
            let bE, bH, bN, bI, bO = demandsOfAction fold body

            bE,
            sourceCalls @ bH,
            sourceReads @ bN,
            { Collection = ExprWitness.collectionName fold.Expr source
              Ceiling = ceiling }
            :: bI,
            bO

        // A leaf demands what it declares, and an opaque one is NAMED as such
        // (Phase 2130, D40): the escape is reported, never folded into a leaf
        // that reads as demanding nothing.
        | ActionView.Leaf declaration ->
            declaration.EffectKinds, declaration.HostCalls, [], [], Option.toList declaration.Opaque

    /// What an expression demands, through the witness's `Expr.Uses`: a state
    /// key is a namespace read, a query slot a `Query` host call.
    and private usesOf
        (fold: DispatchFold<'Action, 'Expr, 'Store, 'Effect>)
        (expr: 'Expr)
        : HostCallDemand list * (string * bool) list =
        let uses = fold.Expr.Uses expr

        let reads =
            uses
            |> List.choose (fun u ->
                match u with
                | BindingUse.State stateKey -> Some(namespaceOf stateKey, false)
                | BindingUse.Query _ -> None)

        let calls =
            uses
            |> List.choose (fun u ->
                match u with
                | BindingUse.Query name -> Some { Channel = "Query"; Name = name }
                | BindingUse.State _ -> None)

        calls, reads

    /// A node that accepts an event but carries no wire-surviving action for it
    /// — a hand-authored handler the decoder replaced with an inert
    /// placeholder, reported rather than assumed away. Which events a node
    /// accepts, and which of its actions survive the wire, are the witness's
    /// (the dispatch axis's `Events` and `Handlers`).
    let private opaqueHandler (dispatch: DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>) (node: 'Node) : bool =
        not (List.isEmpty (dispatch.Events node))
        && List.isEmpty (dispatch.Handlers node)

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

    /// The canonical position of one policy clause within a capability's list.
    ///
    /// Sorted by CLAUSE KIND first and by the argument within the allow-lists,
    /// not by the declaration order a host happened to write: two hosts with the
    /// same policy must encode to the same bytes, which is the property the
    /// whole document rests on. The ordinal is the clause's position in the DU's
    /// own declaration, so adding a clause extends this match rather than
    /// renumbering it, and the compiler says so.
    let private clauseOrder (clause: ServerConstraintClause) : int * string =
        match clause with
        | ServerConstraintClause.AllowList(argument, _) -> 0, argument
        | ServerConstraintClause.Ceiling _ -> 1, ""
        | ServerConstraintClause.Label _ -> 2, ""
        | ServerConstraintClause.DenyList(argument, _) -> 3, argument
        | ServerConstraintClause.AtMost(argument, _) -> 4, argument

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
          Iterations =
            projection.Iterations
            |> List.distinct
            |> List.sortBy (fun i -> i.Collection, i.Ceiling)
          OpaqueLeaves =
            projection.OpaqueLeaves
            |> List.distinct
            |> List.sortBy (fun o -> o.Reason, o.Name)
          Values =
            projection.Values
            |> List.map (fun v ->
                { v with
                    Targets = v.Targets |> List.distinct |> List.sort })
            |> List.distinct
            |> List.sortBy (fun v -> v.Value, v.Targets)
          Server =
            projection.Server
            |> Option.map (fun s ->
                { Effects = s.Effects |> List.distinct |> List.sort
                  Capabilities = s.Capabilities |> List.distinct |> List.sort
                  Functions = s.Functions |> List.distinct |> List.sortBy (fun f -> f.Function, f.Capability)
                  Channels = s.Channels |> List.distinct |> List.sortBy (fun c -> c.Channel, c.Name)
                  Reach =
                    s.Reach
                    |> List.distinct
                    |> List.sortBy (fun r -> r.Capability, r.Argument, r.Name)
                  // Sorted by NAME only, and the reasons within a posture are
                  // left in stage order: they are a sequence through one
                  // handler, not a set, and sorting them would destroy the one
                  // thing that makes a stage ordinal useful.
                  Replay = s.Replay |> List.distinct |> List.sortBy _.Handler
                  // The undo postures, on the replay postures' terms: by name,
                  // the reasons within one left in stage order.
                  Undo = s.Undo |> List.distinct |> List.sortBy _.Handler
                  // Sorted by CAPABILITY, and the permitted values within a
                  // clause sorted too: a permitted set is a set, unlike a
                  // posture's reasons, so leaving it in declaration order would
                  // make two hosts with the same policy encode to different
                  // bytes.
                  Constraints =
                    s.Constraints
                    |> List.map (fun c ->
                        { c with
                            Clauses =
                                c.Clauses
                                |> List.map (fun clause ->
                                    match clause with
                                    | ServerConstraintClause.AllowList(argument, permitted) ->
                                        ServerConstraintClause.AllowList(
                                            argument,
                                            permitted |> List.distinct |> List.sort
                                        )
                                    | ServerConstraintClause.DenyList(argument, refused) ->
                                        ServerConstraintClause.DenyList(
                                            argument,
                                            refused |> List.distinct |> List.sort
                                        )
                                    | other -> other)
                                |> List.distinct
                                |> List.sortBy clauseOrder })
                    |> List.distinct
                    |> List.sortBy _.Capability }) }

    /// The projection that demands nothing. The identity of `union`, and what a
    /// tree with no reachable handler slot projects.
    let empty: DemandedProjection =
        { Effects = []
          HostCalls = []
          StateNamespaces = []
          OpaqueHandlers = []
          Iterations = []
          OpaqueLeaves = []
          Values = []
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
    ///
    /// Reads the DISPATCH axis (the action view and the expressions' uses)
    /// and nothing else.
    let ofAction
        (witness: ProgramWitness<'Node, 'Op, 'Walk, #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>>)
        (action: 'Action)
        : DemandedProjection =
        let effects, hostCalls, namespaces, iterations, opaqueLeaves =
            demandsOfAction (DispatchPosition.fold witness.Dispatch) action

        normalise
            { empty with
                Effects = effects
                HostCalls = hostCalls
                StateNamespaces =
                    namespaces
                    |> List.map (fun (ns, written) ->
                        { Namespace = ns
                          Written = written
                          Read = not written })
                Iterations = iterations
                OpaqueLeaves = opaqueLeaves }

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
                      Reach =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Reach |> Option.defaultValue [])
                      Replay =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Replay |> Option.defaultValue [])
                      Undo =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Undo |> Option.defaultValue [])
                      Constraints =
                        projections
                        |> List.collect (fun p -> p.Server |> Option.map _.Constraints |> Option.defaultValue []) }
            else
                None

        normalise
            { Effects = projections |> List.collect _.Effects
              HostCalls = projections |> List.collect _.HostCalls
              StateNamespaces = projections |> List.collect _.StateNamespaces
              OpaqueHandlers = projections |> List.collect _.OpaqueHandlers
              Iterations = projections |> List.collect _.Iterations
              OpaqueLeaves = projections |> List.collect _.OpaqueLeaves
              Values = projections |> List.collect _.Values
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
    /// covers the whole traversal surface (the witness's `Tree.Traverse` — the
    /// structural children AND every other position a node holds, such as a
    /// state-behaviour branch), so a demand parked in a loading state is not
    /// missed.
    ///
    /// The server tier is `None`: this walk sees a tree, and a handler is not in
    /// the tree. A placement that HAS a handler registration projects it and
    /// attaches the result with `withServer`.
    ///
    /// Reads the WALK axis (the traversal) and the DISPATCH axis (the
    /// handlers and events on each node), so it takes a composition that
    /// fills both.
    let ofTree (witness: FullWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>) (root: 'Node) : DemandedProjection =
        let fold = DispatchPosition.fold witness.Dispatch

        let rec walk (node: 'Node) =
            let own =
                witness.Dispatch.Handlers node
                |> List.map snd
                |> List.fold
                    (fun (accE, accH, accN, accI, accL) a ->
                        let e, h, n, i, l = demandsOfAction fold a
                        accE @ e, accH @ h, accN @ n, accI @ i, accL @ l)
                    ([], [], [], [], [])

            let ownE, ownH, ownN, ownI, ownL = own

            let ownO =
                if opaqueHandler witness.Dispatch node then
                    [ witness.Walk.Nodes.Id node ]
                else
                    []

            witness.Walk.Traverse node
            |> List.fold
                (fun (accE, accH, accN, accO, accI, accL) child ->
                    let e, h, n, o, i, l = walk child
                    accE @ e, accH @ h, accN @ n, accO @ o, accI @ i, accL @ l)
                (ownE, ownH, ownN, ownO, ownI, ownL)

        let effects, hostCalls, namespaces, opaque, iterations, opaqueLeaves = walk root

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
              Iterations = iterations
              OpaqueLeaves = opaqueLeaves
              Values = []
              Server = None }

    // ─── the projection as a wire document ───────────────────────────────────

    /// The document's own `kind`. A `Literal` rather than two string constants,
    /// so the writer and the reader cannot come to disagree about what this
    /// document is called — the same discipline the effect names take from
    /// `ClientEffect.kind` rather than from a spelling.
    [<Literal>]
    let Kind = "demanded"

    /// The version this encoder emits, and — see `decodableVersions` — the only
    /// one this reader reads.
    [<Literal>]
    let Version = 11

    // The policy clause's discriminator, written once and read once. A literal
    // spelled at the encoder and again at the reader is the drift this document
    // otherwise has no detector for.
    [<Literal>]
    let ClauseAllowList = "allowList"

    [<Literal>]
    let ClauseCeiling = "ceiling"

    [<Literal>]
    let ClauseLabel = "label"

    [<Literal>]
    let ClauseDenyList = "denyList"

    [<Literal>]
    let ClauseAtMost = "atMost"

    /// The three common control characters keep their short escapes; every other
    /// control character (U+0000–U+001F) is escaped as `\u00XX`. A raw control
    /// byte inside a JSON string is invalid JSON, so this is a validity
    /// requirement rather than a style choice: the five-character escape set this
    /// replaced could emit a document its own decoder then refused as not-JSON,
    /// which is the one failure a self-describing projection must not have.
    ///
    /// No name this encoder writes carries a control character today — the
    /// projection's strings are effect names, host-call names, state namespaces
    /// and handler ids — so this moves no byte of any existing document. It
    /// closes the case where a host registers one, which nothing structurally
    /// prevents.
    let internal esc (s: string) : string =
        let sb = System.Text.StringBuilder(s.Length)

        for ch in s do
            match ch with
            | '\\' -> sb.Append "\\\\" |> ignore
            | '"' -> sb.Append "\\\"" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

    let internal q (s: string) : string = "\"" + esc s + "\""

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
    ///
    /// **Version 4 adds the declared argument policy**, on that same argument a
    /// third time rather than by analogy to it. The `constraints` key is present
    /// on EVERY server tier from here on — `[]` where no reachable capability
    /// was constrained — so an ABSENT `constraints` says "this producer predates
    /// the policy" and an EMPTY one says "this registration was read and nothing
    /// bounds these capabilities". Those need different answers, and this is the
    /// one member where reading the first as the second is actively dangerous: a
    /// deployer who takes "no clause" for "no bound" when the truth is "I could
    /// not see the bounds" has been told the opposite of the safe thing.
    ///
    /// **Version 5 adds the op reach** (Phase 1967), the fourth time on the
    /// same argument. The `reach` key is present on EVERY server tier from here
    /// on — `[]` where no reachable op named anything — so an ABSENT `reach`
    /// says "this producer predates the member" and an EMPTY one says "this
    /// registration was walked and its ops name nothing". A deployer reading a
    /// handler that writes files cannot be allowed to take the first for the
    /// second. The cost, stated: a signed envelope is verified by recomputing
    /// the document, so every envelope signed under version 4 reports
    /// `Unreadable` drift under this reader, naming the version — the honest
    /// refusal, and a re-sign is the remedy (STABILITY.md, 0.7.0).
    ///
    /// **The `denyList` clause rides version 5 rather than moving it** (Phase
    /// 1975), and the difference from every move above is the argument each one
    /// made. A move exists so a reader cannot take "predates the member" for
    /// "walked and empty"; a deny-list cannot be absent for the first reason,
    /// because no producer of a version-5 document before this clause could
    /// declare one, so a document's silence about deny-lists is true under every
    /// producer that wrote version 5. What a reader built before it does with a
    /// document that CARRIES one is refuse it — an unknown discriminator is a
    /// refusal at the clause, never a misreading — and version 5 belongs to a
    /// draft slot that no released reader reads. A move here would buy no
    /// reader a truer answer and would cost every envelope signed under the
    /// draft a re-sign.
    ///
    /// **The `atMost` clause rides version 6 on exactly that argument** (Phase
    /// 1982): no producer of a version-6 document before it could declare one,
    /// a reader built before it refuses a document that carries one at the
    /// clause, and version 6 belongs to the same unreleased draft slot.
    ///
    /// **Version 6 adds the undo posture** (Phase 1977), the fifth time on the
    /// argument version 3 made for the replay posture, and for the same
    /// member shape. The `undo` key is present on EVERY server tier from here
    /// on — `[]` where the walk contributed no posture — so an ABSENT `undo`
    /// says "this producer predates the posture" and an EMPTY one says "this
    /// registration was walked and no handler contributed". A deployer
    /// deciding whether a run can be rolled back is the last reader that should
    /// take the first for the second: "I could not see whether this can be
    /// undone" and "nothing here needs undoing" are the two answers that must
    /// never share a spelling. The cost is the one version 5 paid: every
    /// envelope signed under version 5 reports `Unreadable` drift naming the
    /// version, and a re-sign is the remedy (STABILITY.md, 0.7.0).
    ///
    /// **Version 10 widens the two defect vocabularies the postures carry**
    /// (Phase 2187, D44): `staged-query` joins both the replay and the undo
    /// reasons, for a read the host answers through an evaluator it could not
    /// declare a pure read. No member moves. The version moves anyway, because
    /// a reason's `defect` is a token from a CLOSED vocabulary and a reader
    /// pinned to version 9 holds a vocabulary that cannot name it; the number
    /// is what tells it which vocabulary it is reading. A document for a host
    /// with no evaluator, or a pure-read one, differs from its version-9 bytes
    /// in the version alone.
    ///
    /// **Version 11 widens the server-effect vocabulary the server tier names**
    /// (Phase 2195, D45): `Report` joins `effects` and `capabilities`, and a
    /// finding's `code` and `severity` join `reach` under it. No member moves.
    /// The version moves on version 10's argument: the server tier's effect
    /// kinds are the closed vocabulary of D3, read here as strings, so a reader
    /// pinned to version 10 would carry the new arm without being told the
    /// vocabulary grew. A document for a registration that reports nothing
    /// differs from its version-10 bytes in the version alone.
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

        let iterations =
            projection.Iterations
            |> List.map (fun i -> $"""{{"collection":{q i.Collection},"ceiling":{i.Ceiling}}}""")
            |> arr

        let opaqueLeaves =
            projection.OpaqueLeaves
            |> List.map (fun o -> $"""{{"reason":{q o.Reason},"name":{q o.Name}}}""")
            |> arr

        let values =
            projection.Values
            |> List.map (fun v ->
                let targets = v.Targets |> List.map q |> arr
                $"""{{"value":{q v.Value},"targets":{targets}}}""")
            |> arr

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

                let reach =
                    s.Reach
                    |> List.map (fun r ->
                        $"""{{"capability":{q r.Capability},"argument":{q r.Argument},"name":{q r.Name}}}""")
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

                let undo =
                    s.Undo
                    |> List.map (fun p ->
                        let reasons =
                            p.Reasons
                            |> List.map (fun r -> $"""{{"stage":{r.Stage},"defect":{q r.Defect}}}""")
                            |> arr

                        $"""{{"handler":{q p.Handler},"undo":{q p.Undo},"reasons":{reasons}}}""")
                    |> arr

                let constraints =
                    s.Constraints
                    |> List.map (fun c ->
                        let clauses =
                            c.Clauses
                            |> List.map (fun clause ->
                                match clause with
                                | ServerConstraintClause.AllowList(argument, permitted) ->
                                    let values = permitted |> List.map q |> arr

                                    $"""{{"clause":{q ClauseAllowList},"argument":{q argument},"permitted":{values}}}"""
                                | ServerConstraintClause.Ceiling bytes ->
                                    $"""{{"clause":{q ClauseCeiling},"bytes":{bytes}}}"""
                                | ServerConstraintClause.Label label ->
                                    $"""{{"clause":{q ClauseLabel},"label":{q label}}}"""
                                | ServerConstraintClause.DenyList(argument, refused) ->
                                    let values = refused |> List.map q |> arr

                                    $"""{{"clause":{q ClauseDenyList},"argument":{q argument},"refused":{values}}}"""
                                | ServerConstraintClause.AtMost(argument, limit) ->
                                    $"""{{"clause":{q ClauseAtMost},"argument":{q argument},"limit":{limit}}}""")
                            |> arr

                        $"""{{"capability":{q c.Capability},"clauses":{clauses}}}""")
                    |> arr

                $"""{{"effects":{se},"capabilities":{sc},"functions":{fns},"channels":{channels},"reach":{reach},"replay":{replay},"undo":{undo},"constraints":{constraints}}}"""

        $"""{{"kind":{q Kind},"version":{Version},"effects":{effects},"hostCalls":{hostCalls},"stateNamespaces":{namespaces},"opaqueHandlers":{opaque},"iterations":{iterations},"opaqueLeaves":{opaqueLeaves},"values":{values},"server":{server}}}"""

    // ─── the projection as a wire document: reading one back ─────────────────
    //
    //  `encode` had no inverse, and every consumer that wanted one wrote its
    //  own. Two of them exist already and a third is planned, each independently
    //  re-deriving an envelope this file is the only authority on — which is a
    //  drift risk with no detector: a reader that gets the envelope subtly wrong
    //  produces a projection that looks like a projection, and the coverage
    //  verdict computed from it is wrong in a way nothing downstream can see.
    //  `decode` is the pinned target those readers can point at instead.
    //
    //  ── What this reader claims, and what it deliberately refuses ────────────
    //  TOTAL over its input: every string is either a projection or a typed
    //  failure naming the member and the version, and no input throws.
    //
    //  NEVER PARTIAL: a document is read whole or refused whole. Every member of
    //  the version is required, every member the version does not declare is
    //  refused, and nothing is defaulted — a reader that quietly supplied `[]`
    //  for a member it could not find would report "this program demands
    //  nothing" for a document that says no such thing.
    //
    //  ONE VERSION, and the refusal of the others is the point. `encode` has
    //  emitted version 4 since the declared argument policy landed, and no
    //  encoder in this package emits 1, 2 or 3 — so "read every version still
    //  emitted" is, honestly read, "read 4". An older document meets
    //  `UnknownVersion` NAMING the version, never a v4 lens applied to a v3
    //  shape: reading a v3 document as v4 would find no `constraints` key and
    //  could only either fail on a well-formed document or invent an empty
    //  policy, and inventing one is exactly the collapse of "predates the
    //  member" into "read and empty" that the version numbers exist to prevent
    //  — the collapse that, for THIS member, turns "I cannot see the bounds"
    //  into "there are none".
    //
    //  DISCRIMINATORS ARE CARRIED, NOT INTERPRETED. An effect name, a safety
    //  word, a defect token: each is read as the string the document holds and
    //  handed back unchanged. A reader that dropped one it did not recognise
    //  would silently narrow the demand set — which is a coverage check reporting
    //  that a host covers something it does not.
    //
    //  FORWARD-COUPLING: a new member, a new version, or a new tier extends
    //  `encode` AND this reader AND the round-trip suite, in one change-set. The
    //  suite is what makes that couple hold — it asserts both directions, so
    //  extending one side alone goes red.

    /// The document versions this reader reads. Data rather than a match arm, so
    /// a caller can ask before it hands over a document, and so the round-trip
    /// suite can enumerate rather than restate.
    let decodableVersions: int list = [ Version ]

    let internal failWith
        (defect: DemandedDefect)
        (version: int option)
        (field: string)
        (detail: string)
        : Result<'T, DemandedDecodeFailure> =
        Error
            { Defect = defect
              Field = field
              Version = version
              Detail = detail }

    /// A child member's path within its parent's.
    let private child (path: string) (name: string) : string =
        if path = "" then name else path + "." + name

    /// An array element's path — an ORDINAL, which addresses a position without
    /// echoing anything the document chose.
    let private at (path: string) (index: int) : string = path + "[" + string index + "]"

    let private fieldsOf (value: Fuaran.Core.JVal) : (string * Fuaran.Core.JVal) list option =
        match value with
        | Fuaran.Core.JObj fields -> Some fields
        | _ -> None

    let private memberOf (name: string) (value: Fuaran.Core.JVal) : Fuaran.Core.JVal option =
        fieldsOf value
        |> Option.bind (List.tryFind (fun (k, _) -> k = name))
        |> Option.map snd

    /// The upper bound on member-nulls this reader will erase before refusing.
    ///
    /// A well-formed document carries exactly one — the server tier's absence
    /// marker. The bound exists because each erasure costs a re-parse, so an
    /// untrusted document full of nulls would otherwise buy quadratic work for
    /// the length of the input.
    [<Literal>]
    let private MaxErasedNulls = 8

    /// The text with the member whose value is the `null` token at `position`
    /// removed, or `None` where that position is not a member's value.
    ///
    /// A member spelled `null` IS an absent member: `{"a":null}` reads exactly as
    /// `{}`. That is not a rule invented here — it is the wire's own read policy
    /// for the token. Every OTHER
    /// position is refused rather than erased, on the policy's own reasoning:
    /// a bare root would make the whole document vanish and an array element
    /// would silently renumber every later index, so neither has an absence to
    /// erase to.
    ///
    /// It is driven BY THE PARSER and never by scanning for a token — the parser
    /// reports the position of the first null it meets and this rewrites at
    /// exactly that position or gives up — so a `null` inside a string value is
    /// structurally out of reach.
    ///
    /// **Why this stays, now that the parser has the policy of its own.** The
    /// pinned wire tier carries a null-tolerant read (`Json.parseTolerantOfNull`,
    /// `NullPolicy.EraseMemberNull`) which erases member-nulls at every depth —
    /// strictly better erasure than this does. What it does not do is SAY WHICH
    /// MEMBERS it erased, and that report is the whole reason this exists: under
    /// the tolerant read `{"server":null}` and a document with no `server` member
    /// at all parse to the same value, so the reader below could no longer tell a
    /// walk that did not run from a document that does not describe this version.
    /// The policy's arrival was expected to retire this; measured, it is necessary
    /// and not sufficient. Retiring it means the tier reporting its erasures, or
    /// this document carrying the tier's absence as something other than `null` —
    /// and the second is a wire change with a version, not a cleanup.
    let private eraseMemberNullAt (text: string) (position: int) : (string * string) option =
        let isWs (c: char) =
            c = ' ' || c = '\t' || c = '\n' || c = '\r'

        let rec backOverWs (i: int) =
            if i >= 0 && isWs text[i] then backOverWs (i - 1) else i

        let rec fwdOverWs (i: int) =
            if i < text.Length && isWs text[i] then
                fwdOverWs (i + 1)
            else
                i

        /// The opening quote of the key whose closing quote is before `i`. A
        /// quote opens the string only when an EVEN number of backslashes
        /// precedes it, so a key carrying an escaped quote is walked correctly.
        let rec keyStart (i: int) =
            if i < 0 then
                None
            elif text[i] = '"' then
                let rec slashes (j: int) (n: int) =
                    if j >= 0 && text[j] = '\\' then
                        slashes (j - 1) (n + 1)
                    else
                        n

                if slashes (i - 1) 0 % 2 = 0 then
                    Some i
                else
                    keyStart (i - 1)
            else
                keyStart (i - 1)

        if
            position < 0
            || position + 4 > text.Length
            || text.Substring(position, 4) <> "null"
        then
            None
        else
            let colon = backOverWs (position - 1)

            let keyEnd =
                if colon >= 0 && text[colon] = ':' then
                    backOverWs (colon - 1)
                else
                    -1

            if keyEnd < 0 || text[keyEnd] <> '"' then
                None
            else
                match keyStart (keyEnd - 1) with
                | None -> None
                | Some ks ->
                    let key = text.Substring(ks + 1, keyEnd - ks - 1)
                    let before = backOverWs (ks - 1)

                    if before < 0 then
                        None
                    elif text[before] = ',' then
                        // Drop the separating comma with the member it introduced.
                        Some(text.Substring(0, before) + text.Substring(position + 4), key)
                    elif text[before] = '{' then
                        // First member: drop the comma that FOLLOWS it, if any.
                        let after = fwdOverWs (position + 4)

                        if after < text.Length && text[after] = ',' then
                            Some(text.Substring(0, ks) + text.Substring(after + 1), key)
                        else
                            Some(text.Substring(0, ks) + text.Substring(position + 4), key)
                    else
                        None

    /// Parse a demanded document, erasing member-nulls to absence. The second
    /// component is the keys so erased, so the one member whose absence is a
    /// FACT rather than a defect can be told from a member that is simply
    /// missing.
    let internal parseDocument (json: string) : Result<Fuaran.Core.JVal * string list, DemandedDecodeFailure> =
        let notJson (err: Fuaran.Core.JsonError) =
            failWith DemandedDefect.NotJson None "" (err.Message + " at position " + string err.Position)

        let rec go (text: string) (erased: string list) =
            match Fuaran.Core.Json.parseDetailed text with
            | Ok value -> Ok(value, List.rev erased)
            | Error err when err.Kind = Fuaran.Core.NullNotRepresentable ->
                if List.length erased >= MaxErasedNulls then
                    failWith
                        DemandedDefect.NotJson
                        None
                        ""
                        ("the document carries more than "
                         + string MaxErasedNulls
                         + " members spelled null; this document declares one")
                else
                    match eraseMemberNullAt text err.Position with
                    | Some(rewritten, key) -> go rewritten (key :: erased)
                    | None -> notJson err
            | Error err -> notJson err

        go json []

    // ─── member readers ──────────────────────────────────────────────────────

    let private requireMember
        (version: int option)
        (path: string)
        (name: string)
        (value: Fuaran.Core.JVal)
        : Result<Fuaran.Core.JVal, DemandedDecodeFailure> =
        match memberOf name value with
        | Some v -> Ok v
        | None -> failWith DemandedDefect.MissingMember version path ("required member '" + path + "' is absent")

    let internal requireString version path name value : Result<string, DemandedDecodeFailure> =
        requireMember version path name value
        |> Result.bind (fun v ->
            match v with
            | Fuaran.Core.JStr s -> Ok s
            | _ -> failWith DemandedDefect.WrongType version path ("member '" + path + "' is not a string"))

    let private requireBool version path name value : Result<bool, DemandedDecodeFailure> =
        requireMember version path name value
        |> Result.bind (fun v ->
            match v with
            | Fuaran.Core.JBool b -> Ok b
            | _ -> failWith DemandedDefect.WrongType version path ("member '" + path + "' is not a boolean"))

    let internal requireInt version path name value : Result<int, DemandedDecodeFailure> =
        requireMember version path name value
        |> Result.bind (fun v ->
            match v with
            | Fuaran.Core.JInt i -> Ok i
            | _ -> failWith DemandedDefect.WrongType version path ("member '" + path + "' is not an integer"))

    let private requireArray version path name value : Result<Fuaran.Core.JVal list, DemandedDecodeFailure> =
        requireMember version path name value
        |> Result.bind (fun v ->
            match v with
            | Fuaran.Core.JArr xs -> Ok xs
            | _ -> failWith DemandedDefect.WrongType version path ("member '" + path + "' is not an array"))

    /// Refuse any member this version does not declare.
    ///
    /// Stricter than a must-ignore rule, and deliberately: this document is
    /// untrusted, and a member the reader does not understand means the producer
    /// and the reader disagree about what this version IS. Ignoring it is
    /// precisely reading the document through the wrong lens with the version
    /// number agreeing all the way.
    let internal declaredOnly
        (version: int option)
        (path: string)
        (allowed: string list)
        (value: Fuaran.Core.JVal)
        : Result<unit, DemandedDecodeFailure> =
        match fieldsOf value with
        | None ->
            failWith
                (if path = "" then
                     DemandedDefect.NotAnObject
                 else
                     DemandedDefect.WrongType)
                version
                path
                (if path = "" then
                     "the document's root is not an object"
                 else
                     "member '" + path + "' is not an object")
        | Some fields ->
            match fields |> List.map fst |> List.filter (fun k -> not (List.contains k allowed)) with
            | [] -> Ok()
            | extra ->
                failWith
                    DemandedDefect.UndeclaredMember
                    version
                    (child path (List.head extra))
                    ("this version declares no member(s): " + String.concat ", " extra)

    /// Traverse a list positionally, short-circuiting on the first refusal so a
    /// document with two defects reports the first rather than an aggregate the
    /// caller has to unpick.
    let private traverseIndexed
        (f: int -> 'a -> Result<'b, DemandedDecodeFailure>)
        (items: 'a list)
        : Result<'b list, DemandedDecodeFailure> =
        (Ok [], List.indexed items)
        ||> List.fold (fun acc (i, item) -> acc |> Result.bind (fun ok -> f i item |> Result.map (fun v -> v :: ok)))
        |> Result.map List.rev

    /// A required array of strings, each element addressed by its ordinal.
    let private requireStrings version path name value : Result<string list, DemandedDecodeFailure> =
        requireArray version path name value
        |> Result.bind (
            traverseIndexed (fun i x ->
                match x with
                | Fuaran.Core.JStr s -> Ok s
                | _ ->
                    failWith
                        DemandedDefect.WrongType
                        version
                        (at path i)
                        ("element '" + at path i + "' is not a string"))
        )

    /// A required array of objects, each decoded under its own indexed path.
    let private requireObjects
        version
        (path: string)
        (name: string)
        (value: Fuaran.Core.JVal)
        (decodeOne: int option -> string -> Fuaran.Core.JVal -> Result<'T, DemandedDecodeFailure>)
        : Result<'T list, DemandedDecodeFailure> =
        requireArray version path name value
        |> Result.bind (traverseIndexed (fun i x -> decodeOne version (at path i) x))

    // ─── element readers ─────────────────────────────────────────────────────

    let private decodeHostCall version path value : Result<HostCallDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "channel"; "name" ] value
        |> Result.bind (fun () -> requireString version (child path "channel") "channel" value)
        |> Result.bind (fun channel ->
            requireString version (child path "name") "name" value
            |> Result.map (fun name -> { Channel = channel; Name = name }))

    /// An iteration demand (Phase 1991): the collection, and the ceiling as an
    /// integer — read as the replay reasons' `stage` is.
    let private decodeIteration version path value : Result<IterationDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "collection"; "ceiling" ] value
        |> Result.bind (fun () -> requireString version (child path "collection") "collection" value)
        |> Result.bind (fun collection ->
            requireInt version (child path "ceiling") "ceiling" value
            |> Result.map (fun ceiling ->
                { Collection = collection
                  Ceiling = ceiling }))

    /// A value demand (Phase 2186): the value by name, and its targets as
    /// strings — each an absolute address the op witness answered, carried
    /// as written.
    let private decodeValue version path value : Result<ValueDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "value"; "targets" ] value
        |> Result.bind (fun () -> requireString version (child path "value") "value" value)
        |> Result.bind (fun name ->
            requireStrings version (child path "targets") "targets" value
            |> Result.map (fun targets -> { Value = name; Targets = targets }))

    /// An opaque leaf (Phase 2130): the reason class and the act's name, both
    /// carried as written — a reason class this reader has never heard of is
    /// handed back, never dropped, on the discriminators' terms above.
    let private decodeOpaqueLeaf version path value : Result<OpaqueLeaf, DemandedDecodeFailure> =
        declaredOnly version path [ "reason"; "name" ] value
        |> Result.bind (fun () -> requireString version (child path "reason") "reason" value)
        |> Result.bind (fun reason ->
            requireString version (child path "name") "name" value
            |> Result.map (fun name -> { Reason = reason; Name = name }))

    let private decodeNamespace version path value : Result<StateNamespaceDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "namespace"; "written"; "read" ] value
        |> Result.bind (fun () -> requireString version (child path "namespace") "namespace" value)
        |> Result.bind (fun ns ->
            requireBool version (child path "written") "written" value
            |> Result.bind (fun written ->
                requireBool version (child path "read") "read" value
                |> Result.map (fun read ->
                    { Namespace = ns
                      Written = written
                      Read = read })))

    let private decodeFunction version path value : Result<ServerFunctionDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "function"; "capability" ] value
        |> Result.bind (fun () -> requireString version (child path "function") "function" value)
        |> Result.bind (fun fn ->
            requireString version (child path "capability") "capability" value
            |> Result.map (fun capability ->
                { Function = fn
                  Capability = capability }))

    let private decodeReach version path value : Result<OpReachDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "capability"; "argument"; "name" ] value
        |> Result.bind (fun () -> requireString version (child path "capability") "capability" value)
        |> Result.bind (fun capability ->
            requireString version (child path "argument") "argument" value
            |> Result.bind (fun argument ->
                requireString version (child path "name") "name" value
                |> Result.map (fun name ->
                    { Capability = capability
                      Argument = argument
                      Name = name })))

    let private decodeReason version path value : Result<ReplayReasonDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "stage"; "defect" ] value
        |> Result.bind (fun () -> requireInt version (child path "stage") "stage" value)
        |> Result.bind (fun stage ->
            requireString version (child path "defect") "defect" value
            |> Result.map (fun defect -> { Stage = stage; Defect = defect }))

    let private decodePosture version path value : Result<ReplayPosture, DemandedDecodeFailure> =
        declaredOnly version path [ "handler"; "safety"; "reasons" ] value
        |> Result.bind (fun () -> requireString version (child path "handler") "handler" value)
        |> Result.bind (fun handler ->
            // The safety word is carried, not judged. It is a discriminator of a
            // closed set, and a reader that refused one it did not know would be
            // making a claim about the producer's vocabulary from inside a
            // document reader; one that silently normalised it would be worse.
            requireString version (child path "safety") "safety" value
            |> Result.bind (fun safety ->
                requireObjects version (child path "reasons") "reasons" value decodeReason
                |> Result.map (fun reasons ->
                    { Handler = handler
                      Safety = safety
                      Reasons = reasons })))

    let private decodeUndoReason version path value : Result<UndoReasonDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "stage"; "defect" ] value
        |> Result.bind (fun () -> requireInt version (child path "stage") "stage" value)
        |> Result.bind (fun stage ->
            requireString version (child path "defect") "defect" value
            |> Result.map (fun defect ->
                { UndoReasonDemand.Stage = stage
                  Defect = defect }))

    let private decodeUndoPosture version path value : Result<UndoPosture, DemandedDecodeFailure> =
        declaredOnly version path [ "handler"; "undo"; "reasons" ] value
        |> Result.bind (fun () -> requireString version (child path "handler") "handler" value)
        |> Result.bind (fun handler ->
            // Carried, not judged — the replay posture's reading of its own
            // discriminator, for the same reason.
            requireString version (child path "undo") "undo" value
            |> Result.bind (fun undo ->
                requireObjects version (child path "reasons") "reasons" value decodeUndoReason
                |> Result.map (fun reasons ->
                    { UndoPosture.Handler = handler
                      Undo = undo
                      Reasons = reasons })))

    /// One policy clause.
    ///
    /// The discriminator is read FIRST and decides which members this object
    /// declares, so a `ceiling` carrying a `permitted` array is refused as an
    /// undeclared member rather than read as an allow-list with a stray bound.
    /// An unknown discriminator is refused outright — unlike the safety word and
    /// the defect token above, which are carried uninterpreted, this one selects
    /// a SHAPE, so a reader that carried it could not have read the object it
    /// introduces.
    let private decodeClause version path value : Result<ServerConstraintClause, DemandedDecodeFailure> =
        requireString version (child path "clause") "clause" value
        |> Result.bind (fun clause ->
            match clause with
            | ClauseAllowList ->
                declaredOnly version path [ "clause"; "argument"; "permitted" ] value
                |> Result.bind (fun () -> requireString version (child path "argument") "argument" value)
                |> Result.bind (fun argument ->
                    requireStrings version (child path "permitted") "permitted" value
                    |> Result.map (fun permitted -> ServerConstraintClause.AllowList(argument, permitted)))
            | ClauseCeiling ->
                declaredOnly version path [ "clause"; "bytes" ] value
                |> Result.bind (fun () -> requireInt version (child path "bytes") "bytes" value)
                |> Result.map ServerConstraintClause.Ceiling
            | ClauseLabel ->
                declaredOnly version path [ "clause"; "label" ] value
                |> Result.bind (fun () -> requireString version (child path "label") "label" value)
                |> Result.map ServerConstraintClause.Label
            | ClauseDenyList ->
                declaredOnly version path [ "clause"; "argument"; "refused" ] value
                |> Result.bind (fun () -> requireString version (child path "argument") "argument" value)
                |> Result.bind (fun argument ->
                    requireStrings version (child path "refused") "refused" value
                    |> Result.map (fun refused -> ServerConstraintClause.DenyList(argument, refused)))
            | ClauseAtMost ->
                declaredOnly version path [ "clause"; "argument"; "limit" ] value
                |> Result.bind (fun () -> requireString version (child path "argument") "argument" value)
                |> Result.bind (fun argument ->
                    requireInt version (child path "limit") "limit" value
                    |> Result.map (fun limit -> ServerConstraintClause.AtMost(argument, limit)))
            | other ->
                failWith
                    DemandedDefect.WrongType
                    version
                    (child path "clause")
                    ("'"
                     + other
                     + "' is not a policy clause this version declares; the discriminator selects the members, so an unknown one cannot be carried"))

    let private decodeConstraint version path value : Result<ServerConstraintDemand, DemandedDecodeFailure> =
        declaredOnly version path [ "capability"; "clauses" ] value
        |> Result.bind (fun () -> requireString version (child path "capability") "capability" value)
        |> Result.bind (fun capability ->
            requireObjects version (child path "clauses") "clauses" value decodeClause
            |> Result.map (fun clauses ->
                { Capability = capability
                  Clauses = clauses }))

    let private decodeServer version path value : Result<ServerDemand, DemandedDecodeFailure> =
        declaredOnly
            version
            path
            [ "effects"
              "capabilities"
              "functions"
              "channels"
              "reach"
              "replay"
              "undo"
              "constraints" ]
            value
        |> Result.bind (fun () -> requireStrings version (child path "effects") "effects" value)
        |> Result.bind (fun effects ->
            requireStrings version (child path "capabilities") "capabilities" value
            |> Result.bind (fun capabilities ->
                requireObjects version (child path "functions") "functions" value decodeFunction
                |> Result.bind (fun functions ->
                    requireObjects version (child path "channels") "channels" value decodeHostCall
                    |> Result.bind (fun channels ->
                        requireObjects version (child path "reach") "reach" value decodeReach
                        |> Result.bind (fun reach ->
                            requireObjects version (child path "replay") "replay" value decodePosture
                            |> Result.bind (fun replay ->
                                requireObjects version (child path "undo") "undo" value decodeUndoPosture
                                |> Result.bind (fun undo ->
                                    requireObjects
                                        version
                                        (child path "constraints")
                                        "constraints"
                                        value
                                        decodeConstraint
                                    |> Result.map (fun constraints ->
                                        { Effects = effects
                                          Capabilities = capabilities
                                          Functions = functions
                                          Channels = channels
                                          Reach = reach
                                          Replay = replay
                                          Undo = undo
                                          Constraints = constraints }))))))))

    /// The four members every version of this document has carried, the
    /// fifth version 7 added (`iterations`, Phase 1991), the sixth version 8
    /// added (`opaqueLeaves`, Phase 2130) and the seventh version 9 added
    /// (`values`, Phase 2186).
    let private decodeClientTier version root : Result<DemandedProjection, DemandedDecodeFailure> =
        requireStrings version "effects" "effects" root
        |> Result.bind (fun effects ->
            requireObjects version "hostCalls" "hostCalls" root decodeHostCall
            |> Result.bind (fun hostCalls ->
                requireObjects version "stateNamespaces" "stateNamespaces" root decodeNamespace
                |> Result.bind (fun namespaces ->
                    requireStrings version "opaqueHandlers" "opaqueHandlers" root
                    |> Result.bind (fun opaque ->
                        requireObjects version "iterations" "iterations" root decodeIteration
                        |> Result.bind (fun iterations ->
                            requireObjects version "opaqueLeaves" "opaqueLeaves" root decodeOpaqueLeaf
                            |> Result.bind (fun opaqueLeaves ->
                                requireObjects version "values" "values" root decodeValue
                                |> Result.map (fun values ->
                                    { Effects = effects
                                      HostCalls = hostCalls
                                      StateNamespaces = namespaces
                                      OpaqueHandlers = opaque
                                      Iterations = iterations
                                      OpaqueLeaves = opaqueLeaves
                                      Values = values
                                      Server = None })))))))

    /// The first member whose read value is not in the canonical order the
    /// document promises. Compared against `normalise` rather than against a
    /// second sorting rule spelled here, so "canonical" has one definition and
    /// the writer and the reader share it.
    let private nonCanonicalField (projection: DemandedProjection) : string option =
        let n = normalise projection

        if n.Effects <> projection.Effects then
            Some "effects"
        elif n.HostCalls <> projection.HostCalls then
            Some "hostCalls"
        elif n.StateNamespaces <> projection.StateNamespaces then
            Some "stateNamespaces"
        elif n.OpaqueHandlers <> projection.OpaqueHandlers then
            Some "opaqueHandlers"
        elif n.Iterations <> projection.Iterations then
            Some "iterations"
        elif n.OpaqueLeaves <> projection.OpaqueLeaves then
            Some "opaqueLeaves"
        elif n.Values <> projection.Values then
            Some "values"
        elif n.Server <> projection.Server then
            Some "server"
        else
            None

    /// Read a demanded document back into the projection that produced it.
    ///
    /// The inverse of `encode` in both directions, and the round-trip suite
    /// pins both: `decode (encode p)` is `p`, and `encode` of a decoded document
    /// is the document's own bytes.
    let decode (json: string) : Result<DemandedProjection, DemandedDecodeFailure> =
        parseDocument json
        |> Result.bind (fun (root, erasedNulls) ->
            // The root's SHAPE is answered before its members, so a document
            // that is not an object is told what is wrong with it rather than
            // being reported as missing every member it could not have had.
            (match fieldsOf root with
             | Some _ -> Ok()
             | None -> failWith DemandedDefect.NotAnObject None "" "the document's root is not an object")
            // `kind` and `version` are read BEFORE anything else, and the version
            // gates everything after it: which members are declared, which are
            // required, and what a refusal is a statement about.
            |> Result.bind (fun () -> requireString None "kind" "kind" root)
            |> Result.bind (fun kind ->
                if kind <> Kind then
                    failWith
                        DemandedDefect.UnknownKind
                        None
                        "kind"
                        ("'" + kind + "' is not a demanded-effect projection")
                else
                    requireInt None "version" "version" root)
            |> Result.bind (fun version ->
                if not (List.contains version decodableVersions) then
                    failWith
                        DemandedDefect.UnknownVersion
                        (Some version)
                        "version"
                        ("version "
                         + string version
                         + " is not one this reader reads ("
                         + (decodableVersions |> List.map string |> String.concat ", ")
                         + "); it is refused rather than read through another version's lens")
                else
                    Ok version)
            |> Result.bind (fun version ->
                let v = Some version

                declaredOnly
                    v
                    ""
                    [ "kind"
                      "version"
                      "effects"
                      "hostCalls"
                      "stateNamespaces"
                      "opaqueHandlers"
                      "iterations"
                      "opaqueLeaves"
                      "values"
                      "server" ]
                    root
                |> Result.bind (fun () -> decodeClientTier v root)
                |> Result.bind (fun projection ->
                    // The server tier's three readings, and they are three
                    // FACTS rather than degrees of the same one. An object is a
                    // walk that ran; `null` — erased to absence above — is a
                    // walk that did not; and a member that is simply gone is a
                    // document this version does not describe.
                    //
                    // The one residue: an erased null tells this reader its KEY
                    // and not its depth, so a nested member also named `server`
                    // spelled `null`, in a document that omits the top-level
                    // one, would be read as the tier's absence. It is recorded
                    // rather than closed, and the route once expected to close
                    // it does not: the pinned wire tier now HAS its own
                    // null-erasing read policy, and adopting it loses the
                    // erased-key report these three readings rest on — see the
                    // note on `eraseMemberNullAt`. Closing it means the tier
                    // reporting WHERE it erased, not merely THAT it does.
                    match memberOf "server" root with
                    | Some(Fuaran.Core.JObj _ as tier) ->
                        decodeServer v "server" tier
                        |> Result.map (fun server -> { projection with Server = Some server })
                    | Some _ ->
                        failWith
                            DemandedDefect.WrongType
                            v
                            "server"
                            "member 'server' is neither an object nor absent"
                    | None when List.contains "server" erasedNulls -> Ok projection
                    | None ->
                        failWith
                            DemandedDefect.MissingMember
                            v
                            "server"
                            "required member 'server' is absent; this version carries it on every document, null where no server walk ran")
                |> Result.bind (fun projection ->
                    match nonCanonicalField projection with
                    | None -> Ok projection
                    | Some field ->
                        failWith
                            DemandedDefect.NotCanonical
                            v
                            field
                            ("member '"
                             + field
                             + "' is not distinct and sorted; two documents that compare by value is the property this encoding provides"))))

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
        | CoverageFinding.UnacceptedOpaqueLeaf(reason, name) ->
            $"the program reaches '%s{name}', which declares itself opaque (%s{reason}) — no walk can see what it does, and this host has not accepted running %s{reason} leaves"
        | CoverageFinding.UnregisteredServerFunction fn ->
            $"a handler this program can reach calls host function '%s{fn}', for which this host registered no performer"
        | CoverageFinding.ServerCapabilityWithdrawn capability ->
            $"a handler this program can reach needs server capability '%s{capability}', whose performer this host has withdrawn"
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

        // An opaque leaf is refused unless the host accepted its reason class
        // (Phase 2130, D40) — default-deny, so a host that never considered
        // the question is told about every escape rather than spared them.
        let opaqueFindings =
            projection.OpaqueLeaves
            |> List.choose (fun o ->
                if coverage.Opaque.Contains o.Reason then
                    None
                else
                    Some(CoverageFinding.UnacceptedOpaqueLeaf(o.Reason, o.Name)))

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

                // The same order for the arms that need no host function: a
                // withdrawn performer (Phase 1993, D37) is the absence, and is
                // reported before the gate is asked — a withdrawal no policy
                // can lift must not read as one a policy change resolves.
                let capabilityFindings =
                    demand.Capabilities
                    |> List.choose (fun c ->
                        if offer.Withdrawn.Contains c then
                            Some(CoverageFinding.ServerCapabilityWithdrawn c)
                        elif offer.Gate c then
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

        effectFindings
        @ callFindings
        @ namespaceFindings
        @ opaqueFindings
        @ serverFindings

    /// Answer, for one tree and one host, every demand the host cannot cover —
    /// BEFORE any event runs. An empty list means the host can serve everything
    /// this program is able to ask for.
    let check
        (witness: FullWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
        (coverage: HostCoverage)
        (tree: 'Node)
        : CoverageFinding list =
        ofTree witness tree |> checkProjection coverage
