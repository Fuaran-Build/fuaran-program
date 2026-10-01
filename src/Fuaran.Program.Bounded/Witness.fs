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
//  nothing more (DECISIONS.md D18 and D20, docs/generic-tier.md §3).
//
//  THREE AXES (Phase 1974). A domain fills one record, two or three:
//
//    STATE     `StateWitness<'Node, 'Op>` — REQUIRED. The state the ops apply
//              to, what an op reaches, its canonical form, how a refusal
//              crosses, and which ops are guards. Every domain fills it; it
//              is the centre all three witnesses share.
//    WALK      `WalkWitness<'Node>` — optional. The read-only walks over a
//              tree: the budget, the demanded projection, the query-reader
//              census. A domain whose state is a tree it wants walked fills
//              it; a verb does not.
//    DISPATCH  `DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>` —
//              optional. Handlers and events on nodes, the action view, the
//              binding store the fold writes, the client effects. What an
//              event-driven fold over a tree needs; a domain that fills it is
//              saying it has events.
//
//  `ProgramWitness` composes them, and its TYPE says which are filled: the
//  walk and dispatch positions hold the axis record, or `Unfilled`. A core
//  path that reads an axis names it in its signature, so handing it a
//  composition that does not fill the axis is a COMPILE error — never a
//  runtime default standing in for a member nobody wrote. The handler-level
//  paths that run under every composition (`Handler.run`, `ServerDemanded.
//  ofHandler`) read the dispatch position through `IDispatchPosition`, which
//  `Unfilled` implements at action and effect types that have NO VALUES
//  (`Nothing`): such a handler cannot hold a compute stage, because there is
//  nothing to put in one.
//
//  Every member is a TOTAL, PURE arrow over the domain's own types; what each
//  theorem of the fold, the budget and the handler assumes of each member,
//  and so which axes it needs, is stated in proofs/README.md ("The three
//  axes").
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

// ═══ THE STATE AXIS — required (§3.1) ═══════════════════════════════════════

/// What one op REACHES (Phase 1967, the second witness's W3 and W4): its
/// named arguments and, where it points outside the state the host holds, the
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

/// What one op IS to the handler (Phase 1974, the third witness's F-GUARD).
/// Two shapes, and the handler's `ApplyOps` arm names both.
[<RequireQualifiedAccess>]
type OpView =
    /// An op that moves the state. Applied while planning, so a later op and
    /// a later stage read the state it produced; under a registered op
    /// performer it is staged, with that state, and performed after the plan
    /// commits.
    | Edit
    /// The op-channel GUARD. Resolved through the op's own `Stream.Apply`
    /// against the state AS OF ITS POSITION IN THE PLAN — the state the ops
    /// before it produced, not the entry state: `Ok` holds and the state does
    /// NOT move (the answer is discarded, so a guard cannot write), and
    /// `Error reason` halts the handler with `reason` verbatim, which is how a
    /// domain's typed refusal reaches the halt (§3.5, W5). Never staged and
    /// never performed. The fold's `ActionView.Require` resolves against the
    /// dispatch axis's store and cannot see a tree; this one resolves against
    /// the state and cannot see a store — each is the guard for a domain whose
    /// guards are over that thing. `guard_holds_moves_nothing` and
    /// `guard_refusal_halts` in `proofs/Staging.fst`.
    | Require

module OpView =
    /// A state witness whose ops are all edits — a domain with no op-channel
    /// guard fills `View` with this.
    let edits (_: 'Op) : OpView = OpView.Edit

/// The STATE axis: the state the ops apply to, and everything Program reads
/// of an op. Required — every domain fills it. Six members; `Stream` is
/// Core's own stream witness, reused.
type StateWitness<'Node, 'Op> =
    {
        /// Core's stream witness, reused: apply one op to the state, and the
        /// op vocabulary's canonical codec (K6). A refusal is reported by the
        /// string the handler's halt carries — a string, deliberately, and
        /// §3.5 says what a typed refusal does with it.
        Stream: StreamWitness<'Op, 'Node, string>
        /// What the op reaches — read by the server placement's argument
        /// policy and its demanded projection for the two op-carrying arms,
        /// so a handler's whole reach, its domain ops included, is one
        /// document Program itself produces and enforces.
        Reach: 'Op -> OpReach
        /// The node an op addresses absolutely, if it names one — an op that
        /// does is re-runnable on replay.
        AbsoluteTarget: 'Op -> string option
        /// The state's canonical encoding — the preimage of the tree hash a
        /// signed envelope binds.
        Canonical: 'Node -> string
        /// The ops that turn one state into the other.
        Diff: 'Node -> 'Node -> 'Op list
        /// Which ops are guards (Phase 1974). `OpView.edits` for a domain
        /// with none.
        View: 'Op -> OpView
    }

// ═══ THE WALK AXIS — optional (§3.2) ════════════════════════════════════════

/// The WALK axis: the read-only walks over a tree. `Cost` and `QueryReaders`
/// are PER NODE: the core walks the tree itself, so the walk is written once
/// and only the per-node step is the domain's.
///
/// A tree has TWO child surfaces, and the core walks each where it always has.
/// `Nodes` (Core's `NodeWitness`, paired with its `ReplaceChildren`) is the
/// STRUCTURAL surface — the one the budget prices and re-resolution rebuilds.
/// `Traverse` is the whole traversal surface — structural children AND every
/// other position a node holds one step below it — which the read-only walks
/// (the demanded projection, the query readers) enumerate.
type WalkWitness<'Node> =
    {
        /// Core's node witness, reused as-is with string ids (K1).
        Nodes: NodeWitness<'Node, string>
        /// Every node held one step below this one, structural or not.
        Traverse: 'Node -> 'Node list
        /// The node's own data cost, 0 if it carries none. The core adds the
        /// node itself.
        Cost: 'Node -> int
        /// The query slots this node reads, and the columns it names.
        QueryReaders: 'Node -> QueryReader list
    }

// ═══ THE DISPATCH AXIS — optional (§3.3) ════════════════════════════════════

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
    /// The HALTING guard over the STORE (Phase 1967, the second witness's
    /// F1). `condition` resolves against the store at dispatch, through the
    /// same `ExprWitness.Resolve` an `Assign`'s `from` resolves through. The
    /// boolean `true` holds and changes nothing; any other value, an
    /// unresolved condition and an errored one HALT the fold — nothing after
    /// the guard in the enclosing sequence runs, the outcome says so, and a
    /// handler that meets a halted compute stage rolls back (D8). An errored
    /// condition's message is the halt's reason verbatim, which is how a
    /// domain's typed refusal reaches the diagnostic (§3.5, W5). A guard
    /// writes nothing and emits nothing. Distinct from a leaf's `Refuse`,
    /// which is a diagnostic the sequence carries on past — the UI tier's
    /// refusals are all of that kind, and none of its arms views as a guard.
    /// A guard over the STATE is the op channel's (`OpView.Require`).
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

type ExprWitness<'Expr, 'Store> =
    {
        /// Resolve an expression against the store, at dispatch time.
        Resolve: 'Store -> 'Expr -> ExprResolution
        /// What the expression reads — for the demanded projection.
        Uses: 'Expr -> BindingUse list
    }

/// The binding store the fold writes: the STATE CHANNEL of K4, which since
/// Phase 1974 is a dispatch-axis fact — a domain whose state is its tree, or
/// whose handler is a verb, has none.
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

/// The part of the dispatch axis the FOLD reads — the action view, the
/// expressions it resolves and the store it writes. What a compute stage
/// needs, and nothing that reads a node.
type DispatchFold<'Action, 'Expr, 'Store, 'Effect> =
    { Action: ActionWitness<'Action, 'Expr, 'Store, 'Effect>
      Expr: ExprWitness<'Expr, 'Store>
      Store: StoreWitness<'Store> }

/// What a core path that runs under EVERY composition reads of its dispatch
/// position: the fold's three sub-records, or nothing. `DispatchWitness`
/// answers its own; `Unfilled` answers `None`, and does so at action and
/// effect types that have no values and a store that is `unit` — so a path
/// that would need the fold for an action can never be handed one, and the
/// only reachable consequence of `None` is that there is no binding channel
/// for a landing slot, which the handler refuses while planning.
type IDispatchPosition<'Action, 'Expr, 'Store, 'Effect> =
    abstract Fold: DispatchFold<'Action, 'Expr, 'Store, 'Effect> option

/// The DISPATCH axis: what an event-driven fold over a tree needs. Optional —
/// a domain that fills it is saying it has events. Three members read a node
/// (`Handlers`, `Events`, `Resolve`); the four sub-records are the fold's and
/// the client effects'.
type DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect> =
    {
        /// The actions this node carries that survive the wire, named by the
        /// event that dispatches each.
        Handlers: 'Node -> (string * 'Action) list
        /// The events this node legitimately accepts. A node that accepts one
        /// but carries no wire-surviving action is an OPAQUE handler.
        Events: 'Node -> string list
        /// Re-resolve ONE node's own bound fields against the store.
        Resolve: 'Store -> 'Node -> 'Node
        /// The action view (§3.3.1).
        Action: ActionWitness<'Action, 'Expr, 'Store, 'Effect>
        /// Expressions (§3.3.2).
        Expr: ExprWitness<'Expr, 'Store>
        /// The binding store (§3.3.3).
        Store: StoreWitness<'Store>
        /// Client effects (§3.3.4).
        Effect: EffectWitness<'Effect>
    }

    interface IDispatchPosition<'Action, 'Expr, 'Store, 'Effect> with
        member this.Fold =
            Some
                { DispatchFold.Action = this.Action
                  Expr = this.Expr
                  Store = this.Store }

// ═══ claims ═════════════════════════════════════════════════════════════════

/// A claim-signature verifier, generic in the key. The envelope verifier reads
/// nothing from a key except its id, so the core does not need to know what a
/// key is.
type ClaimVerifier<'Key> =
    { KeyId: 'Key -> string
      Verify: string -> string -> 'Key -> Async<bool> }

// ═══ the composition ════════════════════════════════════════════════════════

/// A type with NO VALUES: the action and effect types of a composition that
/// fills no dispatch axis. Its constructor is private and never called, so a
/// handler over it cannot hold a compute stage and a fold over it emits no
/// effect.
[<Sealed>]
type Nothing private () =
    /// A branch holding a `Nothing` is unreachable by construction.
    static member Absurd(_: Nothing) : 'T =
        invalidOp "unreachable: no value of Nothing exists"

/// What an unfilled walk or dispatch position holds. As a dispatch position
/// it answers no fold, at the `Nothing` action and effect types and the
/// `unit` store (`IDispatchPosition`).
type Unfilled =
    | Unfilled

    interface IDispatchPosition<Nothing, Nothing, unit, Nothing> with
        member _.Fold = None

/// What the algebra asks of a domain, and nothing more: a required state
/// axis and two optional ones. `'Walk` is `WalkWitness<'Node>` or `Unfilled`;
/// `'Dispatch` is `DispatchWitness<'Node, …>` or `Unfilled`. The type says
/// which axes a domain filled, and a core path's signature says which it
/// reads.
type ProgramWitness<'Node, 'Op, 'Walk, 'Dispatch> =
    { State: StateWitness<'Node, 'Op>
      Walk: 'Walk
      Dispatch: 'Dispatch }

/// A composition filling all three axes — the UI tier's shape, and the one
/// the read-only tree walks over handlers (the demanded projection of a tree,
/// re-resolution) need.
type FullWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect> =
    ProgramWitness<'Node, 'Op, WalkWitness<'Node>, DispatchWitness<'Node, 'Action, 'Expr, 'Store, 'Effect>>

module DispatchPosition =

    /// The fold a dispatch position answers, for a core path that has been
    /// handed an ACTION. Every caller holds one, and an action exists only at
    /// a composition that fills the dispatch axis — `Unfilled`'s action type
    /// has no values — so the `None` branch is unreachable rather than
    /// defaulted.
    let fold
        (position: #IDispatchPosition<'Action, 'Expr, 'Store, 'Effect>)
        : DispatchFold<'Action, 'Expr, 'Store, 'Effect> =
        match position.Fold with
        | Some fold -> fold
        | None ->
            invalidOp
                "unreachable: a composition that fills no dispatch axis holds no action, so no path that reads the fold is reached for it"
