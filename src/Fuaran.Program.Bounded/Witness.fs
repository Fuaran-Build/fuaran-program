namespace Fuaran.Program.Runtime

// ============================================================================
//  The destination vocabulary a client effect resolves to. Declared here, in
//  the core, because the effect witness below names it (`EffectWitness.
//  Destination`); it keeps its `Fuaran.Program.Runtime` namespace because the
//  client placement is where a policy reasons about it, and a consumer that
//  spelled it there before still does.
// ============================================================================

/// What an effect's payload resolves to, once the scheme floor has spoken.
[<RequireQualifiedAccess>]
type EffectDestination =
    /// The effect names no destination at all (`WriteToClipboard`, `Focus`).
    /// The discriminator gate is the whole policy for these.
    | Absent
    /// Same-origin: a relative route, a fragment, an empty URL. Also where
    /// `ReadFileBody` lands — see `destinationOf`.
    | Local
    /// An absolute network destination at this host, normalised.
    | Remote of host: string
    /// A scheme with no network host for a rule to name.
    | NonNetwork of scheme: string
    /// The scheme floor rejected it, or it declares a network scheme with no
    /// extractable host.
    | Rejected

module EffectDestination =

    /// The log-safe name of a destination. `Remote` yields the HOST — never the
    /// path or query, which is exactly where an exfiltrated payload sits.
    let describe (d: EffectDestination) : string =
        match d with
        | EffectDestination.Absent -> "none"
        | EffectDestination.Local -> "local"
        | EffectDestination.Remote host -> host
        | EffectDestination.NonNetwork scheme -> scheme + ":"
        | EffectDestination.Rejected -> "unparseable"

namespace Fuaran.Program.Bounded

open Fuaran.Core
open Fuaran.Program.Runtime

// ============================================================================
//  The witness contract — what the program algebra asks of a domain, and
//  nothing more (DECISIONS.md D18, docs/generic-tier.md §3).
//
//  The core is parameterised by one record, `ProgramWitness`, built from
//  sub-records so a placement that needs only part of it takes only that part.
//  Every member is a TOTAL, PURE arrow over the domain's own types; what each
//  theorem of the fold assumes of each member is stated in proofs/README.md
//  ("Every parameter of the model is an assumption").
//
//  The small program-owned records the contract names — the query reader, the
//  host-call demand, the wire refusal, the effect destination — are declared
//  here, before the contract, because the contract is the first thing the core
//  compiles.
// ============================================================================

/// Why a document was refused. `Class` is the specification's own refusal class
/// (Appendix A) and is the only part a conformance harness compares; `Detail` is
/// for a human reading a log.
type WireRefusal = { Class: string; Detail: string }

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

/// Which query a finding is about. Both fields are HOST-declared strings: a
/// handler is registered by the host, so its name and the slot its query lands
/// in are the host's own vocabulary, not anything a generated tree supplied.
type QueryOrigin = { Handler: string; Slot: string }

/// A node that reads a query slot, and the columns it needs.
///
/// **`NodeId` and `Fields` come OFF THE WIRE**, unlike everything on
/// `QueryOrigin`. They are carried in full because this family's whole purpose
/// is to be detailed where the runtime cannot be — but a host that pipes these
/// findings verbatim into a shared log is echoing a generated tree's strings,
/// and should know it is choosing to.
type QueryReader =
    {
        /// The reading node.
        NodeId: string
        /// The query slot it is bound to.
        Slot: string
        /// The columns it names.
        Fields: string list
        /// True when the node ALSO projects rows through a closure, so `Fields`
        /// is a lower bound on what it reads.
        ClosureHeld: bool
    }

/// What an expression reads when it is resolved: a query slot or a state key.
/// Program-owned; the demanded projection turns the first into a `Query` host
/// call and the second into a state-namespace read.
[<RequireQualifiedAccess>]
type BindingUse =
    | Query of name: string
    | State of key: string

/// The answer an expression gives against a store (§3.3). Three cases: a
/// domain whose own resolution has more (the UI tier's missing-catalogue-key
/// case) folds them into `Errored` inside its own witness, with the message it
/// would have reported.
[<RequireQualifiedAccess>]
type ExprResolution =
    | Resolved of JVal
    | NotResolved
    | Errored of string

// ─── §3.1 the tree ──────────────────────────────────────────────────────────

/// What the algebra reads of a tree. `Resolve`, `Cost` and `QueryReaders` are
/// PER NODE: the core walks the tree itself, so the walk is written once and
/// only the per-node step is the domain's.
///
/// A tree has TWO child surfaces, and the core walks each where it always has.
/// `Nodes` (Core's `NodeWitness`, paired with its `ReplaceChildren`) is the
/// STRUCTURAL surface — the one the budget prices and re-resolution rebuilds.
/// `Traverse` is the whole traversal surface — structural children AND every
/// other position a node holds one step below it — which the read-only walks
/// (the demanded projection, the query readers) enumerate.
type TreeWitness<'Node, 'Action, 'Store> =
    {
        /// Core's node witness, reused as-is with string ids (K1).
        Nodes: NodeWitness<'Node, string>
        /// Every node held one step below this one, structural or not.
        Traverse: 'Node -> 'Node list
        /// The actions this node carries that survive the wire, named by the
        /// event that dispatches each.
        Handlers: 'Node -> (string * 'Action) list
        /// The events this node legitimately accepts. A node that accepts one
        /// but carries no wire-surviving action is an OPAQUE handler.
        Events: 'Node -> string list
        /// Re-resolve ONE node's own bound fields against the store.
        Resolve: 'Store -> 'Node -> 'Node
        /// The node's own data cost, 0 if it carries none. The core adds the
        /// node itself.
        Cost: 'Node -> int
        /// The query slots this node reads, and the columns it names.
        QueryReaders: 'Node -> QueryReader list
        /// The node's canonical encoding — the preimage of the tree hash.
        Canonical: 'Node -> string
    }

// ─── §3.2 the action view ──────────────────────────────────────────────────

/// What a leaf may demand, for the static projection. The fold never reads it.
type LeafDeclaration =
    {
        /// The client-effect kinds the leaf may emit.
        EffectKinds: string list
        /// The host channels the leaf names.
        HostCalls: HostCallDemand list
    }

/// One level of an action, seen by the core. Five shapes, and the fold names
/// all five: control structure is sequence + assign + call + require, and
/// everything else is a leaf (K2, as amended by Phase 1967).
[<RequireQualifiedAccess>]
type ActionView<'Action, 'Expr> =
    /// The composition arm.
    | Sequence of 'Action list
    /// The one store write: a literal value, or an expression resolved at
    /// dispatch time.
    | Assign of key: string * value: JVal option * from: 'Expr option
    /// A call to a named endpoint. A call that declares its own result target
    /// is refused (D9).
    | Call of endpoint: string * declaresTarget: bool
    /// The HALTING guard (Phase 1967, the second witness's F1). `condition`
    /// resolves against the store at dispatch, through the same
    /// `ExprWitness.Resolve` an `Assign`'s `from` resolves through. The
    /// boolean `true` holds and changes nothing; any other value, an
    /// unresolved condition and an errored one HALT the fold — nothing after
    /// the guard in the enclosing sequence runs, the outcome says so, and a
    /// handler that meets a halted compute stage rolls back (D8). An errored
    /// condition's message is the halt's reason verbatim, which is how a
    /// domain's typed refusal reaches the diagnostic (§3.5, W5). A guard
    /// writes nothing and emits nothing. Distinct from a leaf's `Refuse`,
    /// which is a diagnostic the sequence carries on past — the UI tier's
    /// refusals are all of that kind, and none of its arms views as a guard.
    | Require of condition: 'Expr
    /// Every other domain act.
    | Leaf of LeafDeclaration

/// What a leaf does at dispatch. Its shape IS the kept assumption K3: at most
/// one effect, or a refusal, or a decline — and no case that returns a store.
[<RequireQualifiedAccess>]
type LeafOutcome<'Effect> =
    | Emit of 'Effect
    | Refuse of reason: string
    | Decline

type ActionWitness<'Action, 'Expr, 'Store, 'Effect> =
    {
        /// One level of the action. Applied repeatedly it must unfold a FINITE
        /// tree — the obligation proofs/README.md states on `w_view`.
        View: 'Action -> ActionView<'Action, 'Expr>
        /// What a LEAF does at dispatch. Never handed a non-leaf by the core.
        Lower: string -> 'Action -> 'Store -> LeafOutcome<'Effect>
        /// The action's log-safe description; diagnostics carry it verbatim.
        Describe: 'Action -> string
        /// The action vocabulary's canonical encoder, spliced verbatim (K6).
        Encode: 'Action -> JVal
        /// Its decoder. The core applies the program's own refusals (D9's
        /// declared result target) before it asks.
        Decode: JVal -> Result<'Action, WireRefusal>
    }

// ─── §3.3 expressions ───────────────────────────────────────────────────────

type ExprWitness<'Expr, 'Store> =
    {
        /// Resolve an expression against the store, at dispatch time.
        Resolve: 'Store -> 'Expr -> ExprResolution
        /// What the expression reads — for the demanded projection.
        Uses: 'Expr -> BindingUse list
    }

// ─── §3.4 the store ─────────────────────────────────────────────────────────

type StoreWitness<'Store> =
    {
        /// Write one key of the state channel (K4). Converting the value to
        /// the store's own representation is the store's business.
        Assign: string -> JVal -> 'Store -> 'Store
        /// Land a server read's table in a query slot.
        LandQuery: string -> Table -> 'Store -> 'Store
        /// The reserved namespace the fold refuses to write into (K5).
        IsReserved: string -> bool
        /// The reserved namespace's prefix, for the refusal's text.
        ReservedPrefix: string
    }

// ─── §3.5 ops ───────────────────────────────────────────────────────────────

/// What one op REACHES (Phase 1967, the second witness's W3 and W4): its
/// named arguments and, where it points outside the tree the host holds, the
/// destination's class.
///
/// `Arguments` are AUTHOR-DECLARED names paired with the values the op names
/// under them — a node id under `target`, a path under `path`, a remote under
/// `remote` — in exactly the shape the server placement's argument policy
/// already reads off a host call, so an allow-list over `ApplyOps` binds an
/// op's arguments as one over `host:<fn>` binds a call's, and the demanded
/// document carries them beside the capability. A value here is a NAME the op
/// reaches, never its payload: a witness that put an op's written content
/// under an argument would be putting it into every log the document reaches.
///
/// `Destination` is the op's reach in the vocabulary the client-effect egress
/// policy already reasons in — `Local` for a write the host performs on its
/// own store, `Remote host` for a push — so a policy can bound the CLASS of
/// an op's reach without knowing how the domain names it. `Absent` for an op
/// that names no destination at all, which is every tree op.
type OpReach =
    { Arguments: (string * string) list
      Destination: EffectDestination }

module OpReach =
    /// An op reaching nothing a policy can name: no arguments, no destination.
    let nothing: OpReach =
        { Arguments = []
          Destination = EffectDestination.Absent }

type OpWitness<'Node, 'Op> =
    {
        /// Core's stream witness, reused: apply one op to a tree, and the op
        /// vocabulary's canonical codec (K6). A refusal is reported by the
        /// string the handler's halt carries — a string, deliberately, and
        /// §3.5 says what a typed refusal does with it.
        Stream: StreamWitness<'Op, 'Node, string>
        /// The ops that turn one tree into the other.
        Diff: 'Node -> 'Node -> 'Op list
        /// The node an op addresses absolutely, if it names one — an op that
        /// does is re-runnable on replay.
        AbsoluteTarget: 'Op -> string option
        /// What the op reaches — read by the server placement's argument
        /// policy and its demanded projection for the two op-carrying arms,
        /// so a handler's whole reach, its domain ops included, is one
        /// document Program itself produces and enforces.
        Reach: 'Op -> OpReach
    }

// ─── §3.6 effects and claims ───────────────────────────────────────────────

type EffectWitness<'Effect> =
    {
        /// The capability name the effect gate decides on (D3).
        Kind: 'Effect -> string
        /// Where the effect's payload points, for the egress policy.
        Destination: 'Effect -> EffectDestination
        /// The client-effect family's SHIPPED encoding, as bytes. A string and
        /// not a `JVal`: this family is the specification's one envelope
        /// exception (a `kind` discriminator, declaration-ordered members), so
        /// re-rendering it canonically would change the bytes.
        Encode: 'Effect -> string
        /// Its decoder.
        Decode: JVal -> Result<'Effect, WireRefusal>
    }

/// A claim-signature verifier, generic in the key. The envelope verifier reads
/// nothing from a key except its id, so the core does not need to know what a
/// key is.
type ClaimVerifier<'Key> =
    { KeyId: 'Key -> string
      Verify: string -> string -> 'Key -> Async<bool> }

/// What the algebra asks of a domain, and nothing more.
type ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect> =
    { Tree: TreeWitness<'Node, 'Action, 'Store>
      Action: ActionWitness<'Action, 'Expr, 'Store, 'Effect>
      Expr: ExprWitness<'Expr, 'Store>
      Store: StoreWitness<'Store>
      Op: OpWitness<'Node, 'Op>
      Effect: EffectWitness<'Effect> }
