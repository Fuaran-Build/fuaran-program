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

/// The trace of a REVERSIBLE run (Phase 1976) — the Bennett history the
/// inverse is built from, mirroring the view: one leaf entry per
/// non-composition step (`Wrote old` for an assignment that wrote, carrying
/// the value it overwrote and `None` if the key was absent; `Nothing` for
/// every other step and for an assignment that was refused), the members
/// that RAN of a sequence, a repeat or a per-element iteration (a halted
/// prefix is shorter), and which arm a branch took. Recorded only by
/// `BoundedActions.runTraced`; the forward `run` records nothing, so a
/// program that never reverses pays nothing. The model's `trace`.
[<RequireQualifiedAccess>]
type Trace =
    | Nothing
    | Wrote of old: JVal option
    | Seq of steps: Trace list
    | Choose of tookTrue: bool * arm: Trace
    | Repeat of iterations: Trace list
    /// The elements that RAN of an `Each` (Phase 1990): the lowered form's
    /// steps — one per element, in collection order — under their own
    /// constructor, so a reader of the trace sees how many elements ran
    /// (`FlowDecision.Iterated`); inverted exactly as a sequence is.
    | Each of elements: Trace list
    /// The elements that RAN of an `Each` over a collection the STORE holds
    /// (Phase 1991, D36), beside the EXTENT the run READ at entry — the one
    /// record of it, which the inverse lowers the body over again; the store
    /// is never consulted for it a second time. The model's `TEachOf`.
    | EachOf of extent: JVal list * elements: Trace list

module Trace =
    /// Whether every write the trace recorded overwrote a PRESENT key, so
    /// every one can be undone by an assignment: the run-dependent half of
    /// reversibility. A key that was absent before the run cannot be restored
    /// — the store has no delete (K4) — and the inverse is refused rather
    /// than built wrong. The model's `restorable`.
    let rec restorable (trace: Trace) : bool =
        match trace with
        | Trace.Nothing -> true
        | Trace.Wrote None -> false
        | Trace.Wrote(Some _) -> true
        | Trace.Seq steps -> steps |> List.forall restorable
        | Trace.Choose(_, arm) -> restorable arm
        | Trace.Repeat iterations -> iterations |> List.forall restorable
        | Trace.Each elements -> elements |> List.forall restorable
        | Trace.EachOf(_, elements) -> elements |> List.forall restorable

/// A collection the STATE holds (Phase 1991, DECISIONS.md D36): the op-axis
/// source of a store-bound `Each`. `Name` is what the demanded projection
/// states ("iterates THIS collection") and what the durable journal subjects
/// the extent read under; `Read` is the domain's own enumeration of the state
/// the plan holds at the op's position — the state axis (D20) — so a domain
/// that cannot enumerate a collection never constructs one, and no member of
/// the state witness was added for it. Two collections are the SAME
/// collection when they are the same name: the name is the identity the
/// projection states and the journal keys on, and a domain that gives two
/// reads one name has misnamed one of them.
[<CustomEquality; NoComparison>]
type StateCollection<'Node> =
    { Name: string
      Read: 'Node -> JVal list }

    override this.Equals(other: obj) : bool =
        match other with
        | :? StateCollection<'Node> as that -> this.Name = that.Name
        | _ -> false

    override this.GetHashCode() : int = hash this.Name

/// A VALUE the planned STATE holds (Phase 2186, DECISIONS.md D42): the op-axis
/// source of a `Let` — the value channel on the state axis. `Name` is what
/// the demanded projection states ("resolves THIS value") and what the
/// durable journal subjects the read under; `Resolve` is the domain's own
/// resolution against the state the plan holds at the op's position — the
/// state axis (D20) — with the three outcomes an `Assign`'s `from` has
/// (`ExprResolution`): `Resolved` binds, and an unresolved or errored value
/// REFUSES the plan, never defaults. Built through `ExprWitness.value` from
/// the domain's own expression witness over its state, so the resolution is
/// `ExprWitness.Resolve` with the state as the store and no member of the
/// state witness was added for it. Two values are the SAME value when they
/// are the same name, on `StateCollection`'s terms.
[<CustomEquality; NoComparison>]
type StateValue<'Node> =
    { Name: string
      Resolve: 'Node -> ExprResolution }

    override this.Equals(other: obj) : bool =
        match other with
        | :? StateValue<'Node> as that -> this.Name = that.Name
        | _ -> false

    override this.GetHashCode() : int = hash this.Name

/// The SOURCE of an `Each`'s collection (Phase 1991, D36): a LITERAL list in
/// the tree (Phase 1990, D29 — its length is the bound, fixed by the tree,
/// and no store is consulted) or one the STORE holds, read ONCE when the
/// loop is entered under a `ceiling` the tree declares. What the source IS
/// differs per axis, as what a placeholder stands in does: on the dispatch
/// axis an expression resolved against the binding store (`'Source` =
/// `'Expr`, so a domain with no dispatch axis has nothing to put there); on
/// the state axis a `StateCollection` read from the planned state. The
/// ceiling is the operator's second condition: the budget prices at it, an
/// extent over it is refused BEFORE the first element, and the worst-case
/// cost stays a function of the tree (D2). The extent is recorded
/// (`Trace.EachOf`, the journal) and is the only extent ever used again —
/// the third condition, which is what keeps a store-bound loop total: a row
/// the body appends does not extend the running loop.
[<RequireQualifiedAccess>]
type Collection<'Source> =
    | Literal of elements: JVal list
    | Stored of source: 'Source * ceiling: int

/// What a store-bound `Each` DEMANDS (Phase 1991, D36, the operator's fourth
/// condition): the collection it iterates, by name, and the ceiling the tree
/// declares — so the demanded projection, and the signed effect envelope
/// over it (D15), read "iterates THIS collection, at most N". On the state
/// axis the name is the `StateCollection`'s; on the dispatch axis it is the
/// binding keys the source expression reads, under the same spelling the
/// durable journal subjects the read with.
type IterationDemand =
    {
        /// The collection, by the name the projection states.
        Collection: string
        /// The most elements the loop will ever walk.
        Ceiling: int
    }

/// What a `Let` over ops DEMANDS (Phase 2186, D42): the value it resolves
/// from the planned state at run time, by the name the projection states —
/// which spells what the expression READS — and the targets the ops beneath
/// it address with the value standing, so the demanded projection, and the
/// signed effect envelope over it (D15), read "writes THESE from THIS". A
/// literal operand demands nothing: its value is in the tree. State axis
/// only: a dispatch-axis derived write already names its reads and its key
/// under `StateNamespaces`.
type ValueDemand =
    {
        /// The value, by the name the projection states.
        Value: string
        /// The absolute targets of the ops beneath the binding, with the
        /// placeholder standing — every op that names one.
        Targets: string list
    }

/// A PLACEHOLDER read where no `Each` binds it (Phase 1990, DECISIONS.md
/// D29): the one defect of the construct that is decided from the tree
/// alone, and refused at validation — before the first step of a run or
/// the first op of a plan — never discovered mid-run. The binding rule: an
/// `Each` binds exactly one placeholder, lexically, over its body; an `Each`
/// nested inside another names its own, and a name already bound by an
/// enclosing `Each` is refused rather than shadowed, so no body ever reads
/// a placeholder two binders could mean. The core walks the view; which
/// placeholders an action's or an op's OWN operands read is the witness's
/// (`Placeholders`), because only the domain can see inside them.
[<RequireQualifiedAccess>]
type ScopeDefect =
    /// An operand reads this placeholder, and no enclosing `Each` binds it.
    | UnboundPlaceholder of placeholder: string
    /// An `Each` binds this placeholder inside another that already binds it.
    | ShadowedPlaceholder of placeholder: string

module ScopeDefect =
    /// The log-safe reason a refusal carries. A placeholder is an
    /// author-declared NAME, never a payload, so it is named in full.
    let describe (defect: ScopeDefect) : string =
        match defect with
        | ScopeDefect.UnboundPlaceholder placeholder ->
            sprintf "placeholder '%s' is read outside any Each that binds it" placeholder
        | ScopeDefect.ShadowedPlaceholder placeholder ->
            sprintf "placeholder '%s' is already bound by an enclosing Each" placeholder

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

/// What one op IS to the handler (Phase 1974, the third witness's F-GUARD;
/// Phase 1976, the two flow shapes the second witness's re-run found
/// missing; Phase 1990, per-element iteration; Phase 2186, the value
/// channel). Six shapes, and the handler's `ApplyOps` arm names all six.
/// One level, like `ActionView`: a
/// branch's arms are ops, and the handler re-views each as it reaches it;
/// the obligation that `View` unfolds a FINITE tree sits on the witness, as
/// it does for the action view.
[<RequireQualifiedAccess>]
type OpView<'Node, 'Op> =
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
    /// SELECTION over ops (Phase 1976). `entry` is an op applied for its
    /// ANSWER, exactly as a guard is: `Ok` takes `whenTrue`, `Error` takes
    /// `whenFalse` — on this channel a domain's typed refusal is the false
    /// value, not a halt, because the channel has two answers and no third.
    /// The arm runs as the ops of an `ApplyOps` effect run. `exit`, when
    /// carried, is applied against the state the arm left and must HOLD after
    /// the true arm and FAIL after the false arm (the Janus discipline that
    /// lets an inverse pick the arm to undo): violated, the whole effect is
    /// refused with the assertion named, after the arm planned — a defect in
    /// the program, caught. Neither condition is applied for its state,
    /// staged or performed. `choose_plans_the_taken_arm` and
    /// `exit_violation_halts` in `proofs/Staging.fst`.
    | Choose of entry: 'Op * whenTrue: 'Op list * whenFalse: 'Op list * exit: 'Op option
    /// BOUNDED ITERATION over ops (Phase 1976, D2). The body plans `count`
    /// times, threaded as a sequence is; `count` is a literal the domain's
    /// view produces — on the state axis there is no value channel to read a
    /// parameter from, so D2's range check is the domain's, at its codec, and
    /// a ceiling a deployer can bound is the argument policy's (an op whose
    /// reach names its count as an argument is bounded there). A negative
    /// count is refused. `repeat_plans_as_unrolling` in `proofs/Staging.fst`.
    | Repeat of count: int * body: 'Op list
    /// PER-ELEMENT ITERATION over a LITERAL collection (Phase 1990, D29).
    /// The body plans once per element of `collection`, in order, with that
    /// element substituted for `placeholder` in each op's operands through
    /// the state witness's `Substitute` — the lowering D1 asks for: what
    /// plans is the sequence of the substituted bodies, and nothing
    /// downstream learns a new shape (`each_plans_as_lowered` in
    /// `proofs/Staging.fst`). The bound is the collection's length, fixed by
    /// the tree (D2); the element is a value in the tree and not a cell in
    /// the state, so it cannot be overwritten, which is the case D21 refused
    /// an index for. A body reading a placeholder no enclosing `Each` binds
    /// is refused at validation (`OpView.scopeDefects`), never mid-plan. An
    /// empty collection plans nothing. The argument policy and the demanded
    /// projection read the reach of the SUBSTITUTED ops (`beneath`), so a
    /// deployer's allow-list binds every element's address.
    ///
    /// The collection may instead be one the STATE holds (Phase 1991, D36):
    /// `Collection.Stored (collection, ceiling)`, read ONCE from the planned
    /// state at the op's position, refused before the first element when its
    /// extent is over the ceiling, and journaled so a replay serves the
    /// recorded extent. Its lowered elements are not in the tree, so the
    /// policy and the projection read the body with the placeholder
    /// STANDING, and the projection names the collection and its ceiling.
    | Each of collection: Collection<StateCollection<'Node>> * placeholder: string * body: 'Op list
    /// The VALUE CHANNEL on the state axis (Phase 2186, D42): `value` is
    /// resolved ONCE against the planned state AS OF THIS POSITION — the
    /// state the ops before it produced — through the domain's own
    /// `StateValue.Resolve`, with the three outcomes an `Assign`'s `from`
    /// has: `Resolved v` substitutes `v` for `placeholder` in every op of
    /// `body` through the state witness's `Substitute`, and the body plans
    /// as a sequence from the same state; `NotResolved` and `Errored` REFUSE
    /// the effect before the first op of the body plans, an errored value's
    /// message the reason verbatim (never a default — an operand that cannot
    /// be read is a defect). The resolved value is journaled so a replay is
    /// SERVED it, as a store-bound extent is. What plans is the substituted
    /// body: nothing downstream learns a new shape, which is D1's lowering
    /// and D29's binding rule (`let_of_plans_as_body_when_resolved` in
    /// `proofs/Staging.fst`). The binding is lexical: a body reading a
    /// placeholder no enclosing `Each` or `Let` binds is refused at
    /// validation (`OpView.scopeDefects`), and a `Let` that rebinds a name
    /// an enclosing binder holds is refused as a shadow. The argument policy
    /// and the demanded projection read the body with the placeholder
    /// STANDING (`beneath`), as they read a store-bound loop's, and the
    /// value's reads through the binding op's own reach; the projection
    /// names the value and the body's targets (`values`); the replay
    /// classifier reports `non-literal-write` for it exactly as for an
    /// `Assign` with `from`.
    | Let of placeholder: string * value: StateValue<'Node> * body: 'Op list

module OpView =
    /// A state witness whose ops are all edits — a domain with no op-channel
    /// guard, branch, repeat or per-element iteration fills `View` with this.
    let edits (_: 'Op) : OpView<'Node, 'Op> = OpView.Edit

    /// The LOWERED form of an `Each` over ops (Phase 1990): its body once per
    /// element of the collection, in collection order, with that element
    /// substituted for the placeholder through `substitute` — the state
    /// witness's `Substitute`. One flat op list: what the handler plans, the
    /// policy bounds and the projections read.
    let lowered
        (substitute: string -> JVal -> 'Op -> 'Op)
        (collection: JVal list)
        (placeholder: string)
        (body: 'Op list)
        : 'Op list =
        collection
        |> List.collect (fun element -> body |> List.map (substitute placeholder element))

    /// Every op BENEATH an op in its view, to exhaustion: none for an edit or
    /// a guard; a branch's entry, both arms' ops (and theirs), and its exit; a
    /// repeat's body (and its ops); an `Each`'s LOWERED body — every element's
    /// substituted ops (and theirs), since a placeholder stands for an
    /// address and the policy must see each address. The argument policy and
    /// the demanded projection read an op's reach over itself AND these, so a
    /// sequence that reaches an off-list path in an UNTAKEN arm has reached
    /// it: an untaken arm's reach is still reach. A condition is applied,
    /// never viewed, so entry and exit are listed once and not opened.
    let rec beneath (view: 'Op -> OpView<'Node, 'Op>) (substitute: string -> JVal -> 'Op -> 'Op) (op: 'Op) : 'Op list =
        match view op with
        | OpView.Edit
        | OpView.Require -> []
        | OpView.Choose(entry, whenTrue, whenFalse, exit) ->
            let arms = whenTrue @ whenFalse

            (entry :: arms)
            @ Option.toList exit
            @ (arms |> List.collect (beneath view substitute))
        | OpView.Repeat(_, body) -> body @ (body |> List.collect (beneath view substitute))
        | OpView.Each(Collection.Literal elements, placeholder, body) ->
            let elements = lowered substitute elements placeholder body
            elements @ (elements |> List.collect (beneath view substitute))
        // A collection the state holds (Phase 1991) has no elements in the
        // tree, so the ops beneath it are the body with the placeholder
        // STANDING: the policy sees the shape of every address the loop will
        // write, and the demanded projection names the collection beside it.
        | OpView.Each(Collection.Stored _, _, body) -> body @ (body |> List.collect (beneath view substitute))
        // A value the state holds (Phase 2186) is not in the tree either, so
        // the ops beneath the binding are its body with the placeholder
        // STANDING: the policy sees the shape of every address the body
        // writes, and the demanded projection names the value beside it.
        | OpView.Let(_, _, body) -> body @ (body |> List.collect (beneath view substitute))

    /// The SCOPE defects of an op sequence (Phase 1990), decided from the
    /// tree alone: every placeholder an op's own operands read that no
    /// enclosing `Each` binds, and every `Each` that rebinds a name an
    /// enclosing one already binds. `placeholders` is the state witness's
    /// `Placeholders` — the names an op's OWN operands read, not those of the
    /// ops beneath it, which this walk reaches through the view. A branch's
    /// conditions are ops and are asked like any other; an `Each`'s own
    /// collection is a literal or a named read and binds nothing. Distinct,
    /// in walk order; the
    /// handler refuses an `ApplyOps` effect that has any, before its first op
    /// plans, naming the first.
    let scopeDefects
        (view: 'Op -> OpView<'Node, 'Op>)
        (placeholders: 'Op -> string list)
        (ops: 'Op list)
        : ScopeDefect list =
        let rec go (bound: Set<string>) (op: 'Op) : ScopeDefect list =
            let own =
                placeholders op
                |> List.filter (fun p -> not (Set.contains p bound))
                |> List.map ScopeDefect.UnboundPlaceholder

            let within =
                match view op with
                | OpView.Edit
                | OpView.Require -> []
                | OpView.Choose(entry, whenTrue, whenFalse, exit) ->
                    go bound entry
                    @ (whenTrue @ whenFalse |> List.collect (go bound))
                    @ (Option.toList exit |> List.collect (go bound))
                | OpView.Repeat(_, body) -> body |> List.collect (go bound)
                // An `Each` and a `Let` (Phase 2186) bind alike: one name,
                // lexically, over the body; a rebinding is a shadow.
                | OpView.Each(_, placeholder, body)
                | OpView.Let(placeholder, _, body) ->
                    (if Set.contains placeholder bound then
                         [ ScopeDefect.ShadowedPlaceholder placeholder ]
                     else
                         [])
                    @ (body |> List.collect (go (Set.add placeholder bound)))

            own @ within

        ops |> List.collect (go Set.empty) |> List.distinct

    /// The ITERATION demands of an op sequence (Phase 1991, D36): every
    /// collection the state holds that an `Each` beneath these ops reads at
    /// run time, with its ceiling — decided from the tree alone, walked to
    /// exhaustion through the view as `beneath` walks it. A literal
    /// collection demands nothing: its elements are in the tree. Distinct, in
    /// walk order; the demanded projection states them.
    let iterations (view: 'Op -> OpView<'Node, 'Op>) (ops: 'Op list) : IterationDemand list =
        let rec go (op: 'Op) : IterationDemand list =
            match view op with
            | OpView.Edit
            | OpView.Require -> []
            | OpView.Choose(_, whenTrue, whenFalse, _) -> (whenTrue @ whenFalse) |> List.collect go
            | OpView.Repeat(_, body) -> body |> List.collect go
            | OpView.Each(Collection.Literal _, _, body) -> body |> List.collect go
            | OpView.Each(Collection.Stored(collection, ceiling), _, body) ->
                { Collection = collection.Name
                  Ceiling = ceiling }
                :: (body |> List.collect go)
            | OpView.Let(_, _, body) -> body |> List.collect go

        ops |> List.collect go |> List.distinct

    /// The VALUE demands of an op sequence (Phase 2186, D42): every value the
    /// state holds that a `Let` beneath these ops resolves at run time, by
    /// name, with the absolute targets of the ops beneath the binding — read
    /// with the placeholder standing, every op `beneath` the body included,
    /// so a target inside a nested loop is named. Decided from the tree
    /// alone, walked to exhaustion through the view. A literal operand
    /// demands nothing. Distinct, in walk order; the demanded projection
    /// states them.
    let values
        (view: 'Op -> OpView<'Node, 'Op>)
        (substitute: string -> JVal -> 'Op -> 'Op)
        (absoluteTarget: 'Op -> string option)
        (ops: 'Op list)
        : ValueDemand list =
        let rec go (op: 'Op) : ValueDemand list =
            match view op with
            | OpView.Edit
            | OpView.Require -> []
            | OpView.Choose(_, whenTrue, whenFalse, _) -> (whenTrue @ whenFalse) |> List.collect go
            | OpView.Repeat(_, body)
            | OpView.Each(_, _, body) -> body |> List.collect go
            | OpView.Let(_, value, body) ->
                { Value = value.Name
                  Targets =
                    body
                    |> List.collect (fun op -> op :: beneath view substitute op)
                    |> List.choose absoluteTarget
                    |> List.distinct }
                :: (body |> List.collect go)

        ops |> List.collect go |> List.distinct

/// What one op IS to an UNDO (Phase 1977, DECISIONS.md D22): its exact
/// inverse, a declared compensation, or neither. The CLASS is a function of
/// the op alone — the undo posture is read before anything runs, from the
/// declared form, where no pre-state exists — and the INVERSE is a function
/// of the PRE-STATE, which the plan phase holds with every edit and nowhere
/// else: an inverse of a write needs the old bytes. The two live in one
/// member because the type then says which is which, and `undo_run_restores`
/// (`proofs/Undo.fst`) ties the static reading to the run only because the
/// class the posture read of an op is the class the undo meets for it,
/// whatever state it was applied to.
///
/// The inverse is a LIST because the UI witness's inverse is a diff, and a
/// diff is a list. Only an op the witness views as an EDIT is ever asked;
/// a guard, a branch and a repeat are never edits, and what the member
/// answers for them is never read.
[<RequireQualifiedAccess>]
type UndoClass<'Node, 'Op> =
    /// An EXACT inverse. The witness's obligation (K9, docs/generic-tier.md
    /// §3.5): applied to the state the op produced, the ops this answers for
    /// the state the op was applied to restore that state — byte for byte
    /// through `Canonical`. A reversible undo rests on it and on nothing else
    /// (`inverse_law` in `proofs/Undo.fst`); the undo run checks the whole
    /// plan folds back before anything performs, and refuses a witness whose
    /// inverse breaks the law rather than performing it.
    | Inverse of inverse: ('Node -> 'Op list)
    /// A declared COMPENSATION: ops that undo the op IN EFFECT and not in
    /// history — a revert after a commit, a retraction after a publish. No
    /// law: the compensated state is whatever the compensations reach, and
    /// the posture says `compensable`, never `reversible`, for a plan that
    /// carries one.
    | Compensate of compensation: ('Node -> 'Op list)
    /// Neither. The reason is the domain's — a push publishes — and travels
    /// into the undo run's refusal, so it is held to a reach's discipline:
    /// a name, never a payload.
    | OneWay of reason: string

/// The STATE axis: the state the ops apply to, and everything Program reads
/// of an op. Required — every domain fills it. Nine members; `Stream` is
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
        /// Which ops are guards (Phase 1974), branches or repeats (Phase
        /// 1976). `OpView.edits` for a domain with none.
        View: 'Op -> OpView<'Node, 'Op>
        /// What undoes an EDIT (Phase 1977): its exact inverse computed from
        /// the pre-state, a declared compensation, or neither with the reason.
        /// Read by the undo posture before a handler runs and by the undo run
        /// after one committed; never by the forward run.
        Undo: 'Op -> UndoClass<'Node, 'Op>
        /// Substitute a value for a PLACEHOLDER in an op's operands (Phase
        /// 1990): `Substitute placeholder element op` is `op` with `element`
        /// standing wherever the op's operands read `placeholder`, and
        /// nothing else moved — including in the ops beneath a flow op, so
        /// the handler can lower an `Each` by substituting into its body and
        /// re-viewing the result. The obligation: the substituted op VIEWS as
        /// the original does, shape for shape, with the placeholder replaced
        /// in every operand; a nested `Each` keeps its own placeholder and
        /// collection. Called only when an `Each` is met; a domain with none
        /// answers the op unchanged.
        Substitute: string -> JVal -> 'Op -> 'Op
        /// The placeholders an op's OWN operands read (Phase 1990) — not
        /// those of the ops beneath it, which the core reaches through the
        /// view. Read by the scope check before anything plans; a domain
        /// with no placeholders answers `[]`.
        Placeholders: 'Op -> string list
    }

module StateWitness =
    /// `OpView.lowered` through the state witness: the lowered form of an
    /// `Each` over ops — its body once per element, each substituted.
    let lowered
        (state: StateWitness<'Node, 'Op>)
        (collection: JVal list)
        (placeholder: string)
        (body: 'Op list)
        : 'Op list =
        OpView.lowered state.Substitute collection placeholder body

    /// `OpView.scopeDefects` through the state witness.
    let scopeDefects (state: StateWitness<'Node, 'Op>) (ops: 'Op list) : ScopeDefect list =
        OpView.scopeDefects state.View state.Placeholders ops

    /// `OpView.iterations` through the state witness.
    let iterations (state: StateWitness<'Node, 'Op>) (ops: 'Op list) : IterationDemand list =
        OpView.iterations state.View ops

    /// `OpView.values` through the state witness (Phase 2186).
    let values (state: StateWitness<'Node, 'Op>) (ops: 'Op list) : ValueDemand list =
        OpView.values state.View state.Substitute state.AbsoluteTarget ops

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

/// A leaf that cannot be analysed, declared as such (Phase 2130, D40): the
/// reason CLASS that makes it opaque — `in-process` for an act a host performs
/// in its own process with nothing the tree carries describing what it does —
/// and the name of the act. Both are DECLARED names, written by the witness,
/// never payload values, so the record is log-safe on the terms
/// `HostCallDemand` is.
type OpaqueLeaf =
    {
        /// The reason class. Compared by string: a host accepts the classes it
        /// is prepared to run (`HostCoverage.Opaque`), and one it has never
        /// heard of is one it has not accepted.
        Reason: string
        /// The act's name within the domain's vocabulary.
        Name: string
    }

/// What a leaf may demand, for the static projection. The fold never reads it.
type LeafDeclaration =
    {
        /// The client-effect kinds the leaf may emit.
        EffectKinds: string list
        /// The host channels the leaf names.
        HostCalls: HostCallDemand list
        /// Set when the leaf is an escape no walk can see into (Phase 2130,
        /// D40). `None` is the claim that the two lists above are the whole of
        /// what the leaf can do; `Some` says they are not, names the act and
        /// says why, so the demanded document tells "does nothing" apart from
        /// "cannot be analysed" — which a leaf declaring nothing could not.
        Opaque: OpaqueLeaf option
    }

module LeafDeclaration =

    /// The leaf that demands nothing and declares itself analysable. Build
    /// from this (`{ LeafDeclaration.none with EffectKinds = … }`) rather than
    /// a full record literal, so a member the declaration gains later is not
    /// a compile break at every construction.
    let none: LeafDeclaration =
        { EffectKinds = []
          HostCalls = []
          Opaque = None }

    /// A leaf that is an escape: it demands nothing a walk can name, and says
    /// so with a reason class and the act's name.
    let opaque (reason: string) (name: string) : LeafDeclaration =
        { none with
            Opaque = Some { Reason = reason; Name = name } }

/// The bound of a repeat (Phase 1976; D2: a literal count, or a parameter
/// checked against a range). `Literal` is known from the tree alone, which is
/// what admits a repeat to the reversible fragment. `Parameter` resolves at
/// dispatch through `ExprWitness.Resolve` to a `JInt`, once, at entry, and is
/// checked against `[lo, hi]`: outside it the repeat halts BEFORE its first
/// iteration — the over-bound refusal — so the budget prices it at `hi`
/// without consulting the store. The model's `bound`.
[<RequireQualifiedAccess>]
type Bound<'Expr> =
    | Literal of count: int
    | Parameter of count: 'Expr * lo: int * hi: int

/// One level of an action, seen by the core. Eight shapes, and the fold names
/// all eight: control structure is sequence + assign + call + require +
/// choose + repeat + each, and everything else is a leaf (K2, as amended by
/// Phase 1967, Phase 1976 and Phase 1990). Four are COMPOSITION shapes — a
/// sequence, a selection, a repeat, a per-element iteration — whose step is
/// their members' steps; the other four are one step each (`fold_total` in
/// `proofs/BoundedFold.fst`).
[<RequireQualifiedAccess>]
type ActionView<'Action, 'Expr> =
    /// The composition arm.
    | Sequence of 'Action list
    /// The one store write: a literal value, or an expression resolved at
    /// dispatch time.
    ///
    /// The literal may be one the domain's LOOP read from the event before the
    /// fold (D46). The view is computed from the action the loop hands the fold,
    /// so a write whose key is known only with the tree in view, and whose value
    /// arrives on the event, is resolved by the domain before the fold and
    /// viewed as this arm. The core reads nothing new, and the demanded
    /// projection names its key's namespace as it names any write's.
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
    /// SELECTION (Phase 1976, D1's typed branching). `entry` resolves against
    /// the store at dispatch exactly as a guard's condition does: the boolean
    /// `true` takes `whenTrue`, any other value takes `whenFalse`, and an
    /// unresolved or errored condition HALTS as a guard's would — a branch
    /// that cannot decide is a defect, not a default. The arm runs as a member
    /// of a sequence runs. `exit`, when carried, resolves against the store
    /// the arm left and must be `true` after the true arm and not after the
    /// false arm (the Janus discipline that lets an inverse pick the arm to
    /// undo): violated, unresolved or errored, it halts AFTER the arm — the
    /// arm's writes and effects stand as of the halt, the handler rolls back
    /// (D8) — with the failure named in the diagnostic. Absent, the branch
    /// runs forwards exactly the same and is outside the reversible fragment
    /// (`BoundedActions.reversible`). The model's `VChoose`.
    | Choose of entry: 'Expr * whenTrue: 'Action * whenFalse: 'Action * exit: 'Expr option
    /// BOUNDED ITERATION (Phase 1976, D2). The body runs `bound` times in
    /// sequence, stopping at the first halt, and sees NO index: its one
    /// channel to state is the store it writes, and an index it could
    /// overwrite would not be a function of the bound alone, which is what
    /// running an inverse the same number of times rests on. A repeat IS its
    /// unrolling (`repeat_is_unrolling`), so every sequence law covers it.
    /// A negative literal bound is refused. The model's `VRepeat`.
    | Repeat of bound: Bound<'Expr> * body: 'Action
    /// PER-ELEMENT ITERATION over a LITERAL collection (Phase 1990, D29). The
    /// body runs once per element of `collection`, in order, with that
    /// element substituted for `placeholder` — an expression of the domain's
    /// that reads the placeholder by name — through the action witness's
    /// `Substitute`. The lowering D1 asks for: what runs is the SEQUENCE of
    /// the substituted bodies (`each_is_lowering`), so nothing downstream
    /// learns a new shape — the budget prices the lowered form, the demanded
    /// projection reads it, the trace records it (`Trace.Each`) and the
    /// inverse is a sequence's. The bound is the collection's length, fixed
    /// by the tree (D2); the element is a value in the tree and not a cell
    /// in the store, so the body cannot overwrite it, which is the case D21
    /// refused an index for. A body reading a placeholder no enclosing
    /// `Each` binds is refused at validation (`BoundedActions.scopeDefects`),
    /// never mid-run. An empty collection runs nothing. The model's `VEach`,
    /// which carries the elements already lowered.
    ///
    /// The collection may instead be one the STORE holds (Phase 1991, D36):
    /// `Collection.Stored (source, ceiling)`, an expression resolved against
    /// the binding store ONCE at entry — as a parameter bound is — that must
    /// resolve to a list of at most `ceiling` elements, else the fold HALTS
    /// before the first element; the extent read is recorded in the trace
    /// (`Trace.EachOf`) and the inverse lowers the body over the RECORDED
    /// extent, never the live store, which is what keeps the shape in the
    /// reversible fragment where a parameter-bound repeat is not. The
    /// model's `VEachOf`.
    | Each of collection: Collection<'Expr> * placeholder: string * body: 'Action
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
        /// Substitute a value for a PLACEHOLDER throughout an action (Phase
        /// 1990): `Substitute placeholder element action` is `action` with
        /// `element` standing wherever its expressions — its own and those of
        /// the actions beneath it — read `placeholder`, and nothing else
        /// moved. The fold lowers an `Each` with it, once per element, and
        /// folds the results as a sequence. The obligation: the substituted
        /// action VIEWS as the original does, shape for shape, with the
        /// placeholder replaced in every expression; a nested `Each` keeps
        /// its own placeholder and collection. Called only when an `Each` is
        /// met; a domain with none answers the action unchanged.
        Substitute: string -> JVal -> 'Action -> 'Action
        /// The placeholders an action's OWN operands read (Phase 1990) — the
        /// expressions this level of the view holds, a leaf's operands — and
        /// not those of the actions beneath it, which the core reaches
        /// through the view. Read by the scope check before anything runs; a
        /// domain with no placeholders answers `[]`.
        Placeholders: 'Action -> string list
    }

module ActionWitness =
    /// The LOWERED form of an `Each` over actions (Phase 1990): its body once
    /// per element of the collection, in collection order, with that element
    /// substituted for the placeholder. What the fold runs, the budget
    /// prices and the projections read; the model's `elements`.
    let lowered
        (witness: ActionWitness<'Action, 'Expr, 'Store, 'Effect>)
        (collection: JVal list)
        (placeholder: string)
        (body: 'Action)
        : 'Action list =
        collection
        |> List.map (fun element -> witness.Substitute placeholder element body)

    /// The SCOPE defects of an action (Phase 1990), decided from the tree
    /// alone: every placeholder an action's own operands read that no
    /// enclosing `Each` binds, and every `Each` that rebinds a name an
    /// enclosing one already binds. Distinct, in walk order. The fold's entry
    /// refuses an action that has any, before its first step, naming the
    /// first; the body of an `Each` is walked ONCE, unsubstituted, because
    /// scope is a property of the form and not of any element.
    let scopeDefects (witness: ActionWitness<'Action, 'Expr, 'Store, 'Effect>) (action: 'Action) : ScopeDefect list =
        let rec go (bound: Set<string>) (a: 'Action) : ScopeDefect list =
            let own =
                witness.Placeholders a
                |> List.filter (fun p -> not (Set.contains p bound))
                |> List.map ScopeDefect.UnboundPlaceholder

            let within =
                match witness.View a with
                | ActionView.Sequence members -> members |> List.collect (go bound)
                | ActionView.Choose(_, whenTrue, whenFalse, _) -> go bound whenTrue @ go bound whenFalse
                | ActionView.Repeat(_, body) -> go bound body
                | ActionView.Each(_, placeholder, body) ->
                    (if Set.contains placeholder bound then
                         [ ScopeDefect.ShadowedPlaceholder placeholder ]
                     else
                         [])
                    @ go (Set.add placeholder bound) body
                | ActionView.Assign _
                | ActionView.Call _
                | ActionView.Require _
                | ActionView.Leaf _ -> []

            own @ within

        go Set.empty action |> List.distinct

type ExprWitness<'Expr, 'Store> =
    {
        /// Resolve an expression against the store, at dispatch time.
        Resolve: 'Store -> 'Expr -> ExprResolution
        /// What the expression reads — for the demanded projection.
        Uses: 'Expr -> BindingUse list
    }

module ExprWitness =
    /// The NAME of a dispatch-axis store-bound collection (Phase 1991, D36):
    /// the binding keys its source expression reads, through `Uses`, under
    /// one spelling — what the demanded projection states as the collection
    /// and what the durable journal subjects the extent read with, so the
    /// document and the record name one thing. A name, never a payload.
    let collectionName (expr: ExprWitness<'Expr, 'Store>) (source: 'Expr) : string =
        let keys =
            expr.Uses source
            |> List.choose (fun u ->
                match u with
                | BindingUse.State key -> Some key
                | BindingUse.Query _ -> None)

        "bindings:" + String.concat "," keys

    /// The NAME of a state-axis value (Phase 2186, D42): the state keys its
    /// expression reads, through `Uses`, under one spelling — what the
    /// demanded projection states as the value and what the durable journal
    /// subjects the read with, so the document and the record name one
    /// thing, and the name spells the reads. A name, never a payload.
    let valueName (expr: ExprWitness<'Expr, 'Store>) (source: 'Expr) : string =
        let keys =
            expr.Uses source
            |> List.choose (fun u ->
                match u with
                | BindingUse.State key -> Some key
                | BindingUse.Query _ -> None)

        "state:" + String.concat "," keys

    /// A `StateValue` over the domain's own expression witness INSTANTIATED
    /// AT ITS STATE (Phase 2186, D42): the resolution is `Resolve` with the
    /// planned state as the store — the one arrow the contract already had
    /// for a derived value, with its three outcomes — and the name is
    /// `valueName`. A domain whose expressions read its state builds every
    /// `OpView.Let`'s value through this, so no member of the state witness
    /// carries an expression type.
    let value (expr: ExprWitness<'Expr, 'Node>) (source: 'Expr) : StateValue<'Node> =
        { Name = valueName expr source
          Resolve = fun node -> expr.Resolve node source }

/// The binding store the fold writes: the STATE CHANNEL of K4, which since
/// Phase 1974 is a dispatch-axis fact — a domain whose state is its tree, or
/// whose handler is a verb, has none.
type StoreWitness<'Store> =
    {
        /// Write one key of the state channel (K4). Converting the value to
        /// the store's own representation is the store's business.
        Assign: string -> JVal -> 'Store -> 'Store
        /// Read one key of the state channel (Phase 1976): the value an
        /// assignment is about to overwrite, which a REVERSIBLE run records
        /// (the Bennett trace, `BoundedActions.runTraced`) and nothing else
        /// reads — the forward fold never calls it. K4's read-after-write
        /// law, `Read k (Assign k v s) = Some v`, is the witness's obligation;
        /// the model's `write_restore` is what a reversal rests on.
        Read: string -> 'Store -> JVal option
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
