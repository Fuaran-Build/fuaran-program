module Fuaran.Program.Server.Tests.OperatorControlTests

// ─── The operator's controls, as recorded replayable ops ─────────────────────
//
// Four claims, and each is asserted in the form that could go red rather than in
// the form that reads well.
//
//  1. THE ACT IS A RECORD, AND THE STATE IS A FOLD OF IT. Not "there is a
//     suspend flag somewhere" — the state is `Controls.fold` of the stream and
//     of nothing else, so every prefix of a stream folds to the state that
//     prefix's reader gets. That is what makes a resume a replay rather than a
//     second implementation of the same decision, and it is checked over prefixes
//     rather than over one happy path.
//
//  2. REVOCATION IS MONOTONE. Asserted across every prefix of a stream that
//     deliberately contains a `Resume` after the revoke, because the only way
//     this claim fails is if some arm is quietly made to clear the set.
//
//  3. A CONTROL REFUSES IN THE REGISTRY'S OWN VOCABULARY. A revoked performer
//     reads as `Unregistered` — not as a new denial arm — which is what carries
//     it through to the coverage check with nothing new to teach. The coverage
//     assertion is here and not only in the demanded suite for that reason: it
//     is the end of the thread, and a thread is what breaks.
//
//  4. A SUSPENDED-THEN-RESUMED SESSION CONTINUES BYTE-IDENTICALLY. Compared as
//     canonical JSON against an uninterrupted run of the same events, plus a
//     count of the host performer's own invocations — a count being the only
//     form of "no duplicate effect" that an implementation which merely looks
//     careful cannot satisfy.
//
// ── The rule this suite must never quietly relax ─────────────────────────────
// A `Resume` lifts the SUSPEND and nothing else. Making it clear throttles or
// revocations would read as tidier and would turn the mildest word in the
// vocabulary into the most consequential one. A future edit that does it fails
// here, on purpose.

open Expecto
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Ops
open Fuaran.UI.Ops.Types
open Fuaran.UI.Ops.Introspect
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.OpStream.Abstractions
open Fuaran.UI.OpStream.Replay
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded
open Fuaran.Program.Server

// ─── fixtures ────────────────────────────────────────────────────────────────

let private jstr (s: string) = Fuaran.Core.JStr s

let private endpoint = "/handlers/work"

let private scope = "session-1"

let private ops = ControlActor.operator "ops"

let private detector = ControlActor.machine "denial-patterns"

/// A counting performer: what the host actually ran, which is the whole subject
/// of the revoke and throttle families.
type private Counter() =
    let mutable count = 0
    member _.Count = count

    member _.Performer: Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string> =
        fun _ ->
            count <- count + 1
            Ok(jstr "recorded")

/// A performer that commits and then the process dies — the crash inside the
/// indeterminate window, where the effect happened and the record of it did not.
exception private ProcessDied of string

let private boundMarkdown (id: string) (key: string) (dflt: string) : Node<obj> =
    let n = Fuaran.markdown id "placeholder"

    { n with
        Kind = NodeKind.Markdown({ Text = TextSource.Bound(Binding.State(key, Some dflt)) }) }

let private treeNode (onClick: Action<obj>) : Node<obj> =
    Fuaran.dashboard
        "root"
        { Defaults.dashboard<obj> with
            Children =
                [ Fuaran.button
                      "call"
                      { Defaults.button<obj> with
                          Label = TextSource.Literal "call"
                          OnClick = onClick }
                  boundMarkdown "readout" "status" "init" ] }

let private wire (onClick: Action<obj>) : WireTree = WireTree.ofDecoded (treeNode onClick)

let private callWire = wire (Action.Call(endpoint, None, None))

let private clickEv (seq: int) : LiveEvent =
    { ConnId = "server"
      NodeId = "call"
      Event = "click"
      Payload = Map.empty
      LastSeq = seq }

let private readout (tree: Node<obj>) : string option =
    match findNode (NodeId "readout") tree with
    | Some node ->
        match node.Kind with
        | NodeKind.Markdown({ Text = TextSource.Literal s }) -> Some s
        | _ -> None
    | None -> None

/// One host call, then a compute whose write is visible through `readout`.
let private auditing (answer: string) : Handler =
    { Name = "work"
      Stages =
        [ Effect(ServerEffect.HostCall("audit", jstr "x", Some "note"))
          Compute(Action.SetState("status", Some(jstr answer), None)) ] }

/// Three host calls to the same performer — enough for a window of two to be
/// crossed exactly once.
let private thrice: Handler =
    { Name = "work"
      Stages =
        [ Effect(ServerEffect.HostCall("audit", jstr "1", None))
          Effect(ServerEffect.HostCall("audit", jstr "2", None))
          Effect(ServerEffect.HostCall("audit", jstr "3", None)) ] }

let private registryOf (performer: Fuaran.Core.JVal -> Result<Fuaran.Core.JVal, string>) =
    ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.register "audit" performer
    |> ServerEffectRegistry.permissive

let private servicesOf (registry: ServerEffectRegistry) (handler: Handler) : ServerServices =
    { ServerServices.createPermissive with
        Handlers = Map.ofList [ endpoint, handler ]
        Effects = registry }

let private durableWith (journal: EffectJournal) =
    DurableServices.create |> DurableServices.withJournal journal

let private controlsOn (journal: ControlJournal) : ControlServices =
    ControlServices.create scope |> ControlServices.withJournal journal

let private store: ServerStore =
    { Tree = treeNode (Action.Call(endpoint, None, None))
      Bindings = empty }

let private entriesOf (journal: ControlJournal) = journal.Read scope

/// Every prefix of a list, shortest first — what "any reader of any prefix"
/// means as a value.
let private prefixes (items: 'a list) : 'a list list =
    [ for n in 0 .. List.length items -> List.truncate n items ]

// ─── tests ───────────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList
        "Phase 1746 — suspend, throttle, revoke and resume as recorded ops"
        [

          // ── the record ───────────────────────────────────────────────

          test "every op carries its reason and its actor, and its ordinal is the stream's" {
              let journal = Controls.inMemory ()
              let controls = controlsOn journal

              DurableControls.record controls (Controls.suspend ops "spend spike") |> ignore

              DurableControls.record
                  controls
                  (Controls.throttle
                      ops
                      "slow the mailer"
                      { Capability = "host:audit"
                        MaxPerInvocation = 2 })
              |> ignore

              DurableControls.record controls (Controls.revoke detector "repeated denials" "audit")
              |> ignore

              DurableControls.record controls (Controls.resume ops "reviewed") |> ignore

              let entries = entriesOf journal

              Expect.equal
                  (entries |> List.map _.Sequence)
                  [ 0; 1; 2; 3 ]
                  "ordinals come off the stream, not the raiser"

              Expect.equal
                  (entries |> List.map _.Reason)
                  [ "spend spike"; "slow the mailer"; "repeated denials"; "reviewed" ]
                  "every op carries its reason — the resume included"

              Expect.equal
                  (entries |> List.map (fun e -> ControlActor.tag e.Actor, e.Actor.Id))
                  [ "operator", "ops"
                    "operator", "ops"
                    "machine", "denial-patterns"
                    "operator", "ops" ]
                  "and its actor, with the machine raiser recorded as one"

              Expect.equal
                  (entries |> List.map (fun e -> ControlOp.tag e.Op))
                  ControlOp.tags
                  "the four acts, in the order the closed vocabulary names them"
          }

          test "a machine-raised suspend and an operator-raised one are the SAME op" {
              let machineRaised = Controls.suspend detector "denial pattern"
              let handRaised = Controls.suspend ops "denial pattern"

              Expect.equal machineRaised.Op handRaised.Op "one op, not two mechanisms"

              // And the fold cannot tell them apart either — which is the point:
              // a detector's suspension is exactly as binding as a person's, and
              // only the record says which raised it.
              let foldOf request =
                  Controls.fold
                      [ { Sequence = 0
                          Op = request.Op
                          Actor = request.Actor
                          Reason = request.Reason
                          MidStage = [] } ]

              Expect.isTrue (Controls.isSuspended (foldOf machineRaised)) "the machine's suspend binds"
              Expect.isTrue (Controls.isSuspended (foldOf handRaised)) "so does the operator's"

              Expect.equal
                  ((foldOf machineRaised).Suspended |> Option.map (fst >> ControlActor.tag))
                  (Some "machine")
                  "and the state carries the attribution through"
          }

          // ── the fold ─────────────────────────────────────────────────

          test "a resume lifts the SUSPEND and nothing else" {
              let journal = Controls.inMemory ()
              let controls = controlsOn journal

              DurableControls.record controls (Controls.suspend ops "halt") |> ignore

              DurableControls.record
                  controls
                  (Controls.throttle
                      ops
                      "slow it"
                      { Capability = "host:audit"
                        MaxPerInvocation = 1 })
              |> ignore

              DurableControls.record controls (Controls.revoke ops "withdraw" "audit")
              |> ignore

              DurableControls.record controls (Controls.resume ops "reviewed") |> ignore

              let state = DurableControls.stateOf controls

              Expect.isFalse (Controls.isSuspended state) "the suspend is lifted"

              Expect.equal
                  (state.Throttles |> Map.toList |> List.map fst)
                  [ "host:audit" ]
                  "the throttle stands — a standing limit is not a suspension"

              Expect.equal
                  (state.Revoked |> Map.toList |> List.map fst)
                  [ "audit" ]
                  "and the revocation stands, because it is monotone"
          }

          test "revocation is monotone across EVERY prefix of a stream" {
              let raised =
                  [ Controls.revoke detector "denials" "audit"
                    Controls.suspend ops "halt"
                    Controls.throttle
                        ops
                        "slow"
                        { Capability = "ApplyOps"
                          MaxPerInvocation = 1 }
                    Controls.resume ops "reviewed"
                    Controls.revoke ops "withdraw the second" "mailer"
                    Controls.resume ops "reviewed again" ]

              let entries =
                  raised
                  |> List.mapi (fun i r ->
                      { Sequence = i
                        Op = r.Op
                        Actor = r.Actor
                        Reason = r.Reason
                        MidStage = [] })

              let revokedAt =
                  prefixes entries
                  |> List.map (fun prefix -> (Controls.fold prefix).Revoked |> Map.toList |> List.map fst |> Set.ofList)

              revokedAt
              |> List.pairwise
              |> List.iteri (fun i (before, after) ->
                  Expect.isTrue
                      (Set.isSubset before after)
                      (sprintf "prefix %d shrank the revoked set — revocation is not monotone" (i + 1)))

              Expect.equal (List.last revokedAt) (Set.ofList [ "audit"; "mailer" ]) "and both withdrawals survive"
          }

          test "a later throttle of the same capability REPLACES the earlier one" {
              let entryOf i (request: ControlRequest) =
                  { Sequence = i
                    Op = request.Op
                    Actor = request.Actor
                    Reason = request.Reason
                    MidStage = [] }

              let state =
                  Controls.fold
                      [ entryOf
                            0
                            (Controls.throttle
                                ops
                                "first"
                                { Capability = "host:audit"
                                  MaxPerInvocation = 5 })
                        entryOf
                            1
                            (Controls.throttle
                                ops
                                "tighter"
                                { Capability = "host:audit"
                                  MaxPerInvocation = 1 }) ]

              Expect.equal
                  (state.Throttles |> Map.tryFind "host:audit" |> Option.map _.MaxPerInvocation)
                  (Some 1)
                  "one standing window per capability, and the newest wins"
          }

          test "folding a PREFIX gives that prefix's reader the same state, every time" {
              // The replayability claim in the only form that can go red: the
              // state is a function of the entries, so folding a prefix in one
              // step and reaching it incrementally must agree at every position.
              let entries =
                  [ Controls.suspend ops "halt"
                    Controls.revoke ops "withdraw" "audit"
                    Controls.resume ops "reviewed"
                    Controls.throttle
                        ops
                        "slow"
                        { Capability = "Notify"
                          MaxPerInvocation = 0 } ]
                  |> List.mapi (fun i r ->
                      { Sequence = i
                        Op = r.Op
                        Actor = r.Actor
                        Reason = r.Reason
                        MidStage = [] })

              let incremental = entries |> List.scan Controls.step Controls.initial

              let replayed = prefixes entries |> List.map Controls.fold

              Expect.equal replayed incremental "a replay of any prefix reaches the state that prefix produced"

              Expect.equal (replayed |> List.map _.Folded) [ 0..4 ] "and the state says which prefix it is about"
          }

          // ── the effect at the gate ───────────────────────────────────

          test "a suspend refuses every dispatch, and a resume lifts it" {
              let counter = Counter()
              let services = servicesOf (registryOf counter.Performer) (auditing "done")
              let journal = Controls.inMemory ()
              let controls = controlsOn journal
              let durable = durableWith (Journal.inMemory ())
              let session = ServerSession.init services empty callWire

              DurableControls.record controls (Controls.suspend detector "denial pattern")
              |> ignore

              let suspended = DurableControls.step durable controls "inv-0" session (clickEv 0)

              match suspended.Output.Rejected with
              | Some(Gate _) -> ()
              | other -> failtestf "expected a gate rejection from a suspended session, got %A" other

              Expect.equal (readout suspended.Session.Resolved) (Some "init") "and nothing ran"
              Expect.equal counter.Count 0 "the performer was never reached"

              Expect.equal
                  (suspended.Refusals |> List.map Controls.describeRefusal |> List.length)
                  1
                  "the refusal is recorded, not merely enacted"

              match suspended.Refusals with
              | [ ControlRefusal.Suspended(capability, actor, reason) ] ->
                  Expect.equal capability ControlCode.DispatchCapability "refused at the dispatch, not at an effect"
                  Expect.equal (ControlActor.tag actor) "machine" "by the detector"
                  Expect.equal reason "denial pattern" "with its reason on the record"
              | other -> failtestf "expected one suspension refusal, got %A" other

              DurableControls.record controls (Controls.resume ops "reviewed") |> ignore

              let resumed =
                  DurableControls.step durable controls "inv-1" suspended.Session (clickEv 1)

              Expect.isNone resumed.Output.Rejected "the resume lifts the refusal"
              Expect.equal (readout resumed.Session.Resolved) (Some "done") "and the event now does its work"
              Expect.equal counter.Count 1 "the performer ran exactly once"
              Expect.isEmpty resumed.Refusals "and no control refused anything"
          }

          test "a throttled effect refuses with the window NAMED, past the window and not before" {
              let counter = Counter()
              let services = servicesOf (registryOf counter.Performer) thrice
              let journal = Controls.inMemory ()
              let controls = controlsOn journal

              DurableControls.record
                  controls
                  (Controls.throttle
                      ops
                      "the mailer is over budget"
                      { Capability = "host:audit"
                        MaxPerInvocation = 2 })
              |> ignore

              let outcome =
                  DurableControls.run
                      (durableWith (Journal.inMemory ()))
                      controls
                      "inv-0"
                      services.Effects
                      services.Sources
                      "call"
                      thrice
                      store

              match outcome.Refusals with
              | [ ControlRefusal.Throttled(capability, window, attempt) ] ->
                  Expect.equal capability "host:audit" "the capability the window is about"
                  Expect.equal window.MaxPerInvocation 2 "the window itself, on the record"
                  Expect.equal attempt 3 "and where it was crossed"

                  Expect.stringContains
                      (Controls.describeRefusal outcome.Refusals.Head)
                      ControlCode.CapabilityThrottled
                      "the description carries the stable code"
              | other -> failtestf "expected exactly one throttle refusal, got %A" other

              // **And no partial mutation.** A throttle breach is the ordinary
              // structured denial, so it HALTS the handler in the plan phase —
              // and every host call is staged to the perform phase (D8), so a
              // breach at the third leaves none of the three performed. That is
              // the honest reading of "refuses, never a partial mutation", and it
              // is stronger than "the first two ran": a throttled handler commits
              // nothing at all.
              Expect.isFalse outcome.Durable.Outcome.Committed "the handler rolled back"
              Expect.equal counter.Count 0 "and nothing reached the performer"
              Expect.isEmpty outcome.Durable.Outcome.Performed "so there is no residue to take back"

              // Inside the window, the same handler performs every call — the
              // throttle is a limit, not a blanket refusal, and a test that never
              // saw it permit anything would not distinguish the two.
              let inside = Counter()

              let permitted =
                  DurableControls.run
                      (durableWith (Journal.inMemory ()))
                      (controlsOn (Controls.inMemory ()))
                      "inv-1"
                      (registryOf inside.Performer)
                      services.Sources
                      "call"
                      thrice
                      store

              Expect.isEmpty permitted.Refusals "no window, no refusal"
              Expect.equal inside.Count 3 "and all three calls performed"

              // A fresh invocation is a fresh window: the throttle is per
              // invocation by construction, not a running total nobody resets.
              let again = Counter()

              let second =
                  DurableControls.run
                      (durableWith (Journal.inMemory ()))
                      controls
                      "inv-2"
                      (registryOf again.Performer)
                      services.Sources
                      "call"
                      thrice
                      store

              Expect.equal
                  (List.length second.Refusals)
                  1
                  "the next invocation crosses its own window in the same place"

              match second.Refusals with
              | [ ControlRefusal.Throttled(_, _, attempt) ] ->
                  Expect.equal attempt 3 "the count restarted, it did not carry"
              | other -> failtestf "expected one throttle refusal, got %A" other
          }

          test "a revoked performer reads as UNREGISTERED, with the withdrawal on the record" {
              let counter = Counter()
              let denials = ResizeArray<ServerEffectDenial>()

              let registry =
                  registryOf counter.Performer |> ServerEffectRegistry.onDenied denials.Add

              let journal = Controls.inMemory ()
              let controls = controlsOn journal

              DurableControls.record controls (Controls.revoke detector "three refusals in a row" "audit")
              |> ignore

              let outcome =
                  DurableControls.run
                      (durableWith (Journal.inMemory ()))
                      controls
                      "inv-0"
                      registry
                      Fuaran.Core.DataFrame.noResolve
                      "call"
                      (auditing "done")
                      store

              Expect.equal counter.Count 0 "the withdrawn performer was never reached"

              Expect.equal
                  (List.ofSeq denials)
                  [ ServerEffectDenial.Unregistered "host:audit" ]
                  "and the effect reads as ABSENT, not as gate-refused — the registry's own distinction, unchanged"

              match outcome.Refusals with
              | [ ControlRefusal.Revoked(capability, actor, reason) ] ->
                  Expect.equal capability "host:audit" "the capability behind the withdrawn performer"
                  Expect.equal (ControlActor.tag actor) "machine" "withdrawn by the detector"
                  Expect.equal reason "three refusals in a row" "with its reason on the record"
              | other -> failtestf "expected one revocation refusal, got %A" other
          }

          test "the COVERAGE check reports a revoked performer, by name" {
              let counter = Counter()
              let services = servicesOf (registryOf counter.Performer) (auditing "done")
              let journal = Controls.inMemory ()
              let controls = controlsOn journal

              let projection =
                  ServerDemanded.ofTreeAndHandlers services.Handlers (treeNode (Action.Call(endpoint, None, None)))

              let hostWith (coverage: ServerCoverage) =
                  HostCoverage.nothing |> HostCoverage.withServer coverage

              Expect.isEmpty
                  (Demanded.checkProjection (hostWith (DurableControls.coverage controls services)) projection)
                  "before the withdrawal the host covers the handler"

              DurableControls.record controls (Controls.revoke ops "withdrawn" "audit")
              |> ignore

              Expect.equal
                  (Demanded.checkProjection (hostWith (DurableControls.coverage controls services)) projection)
                  [ CoverageFinding.UnregisteredServerFunction "audit" ]
                  "afterwards it does not, and the finding NAMES the performer — this is the end of the thread"

              // A suspension is a different finding for a different reason, and
              // collapsing the two would lose the more useful fact: only one of
              // them is resolved by changing policy. It is asserted on its OWN
              // stream, because absence outranks policy — a withdrawn performer
              // reads as unregistered whatever the gate would have said, which
              // is exactly right and is why the two cannot share a fixture.
              let suspendedOnly = controlsOn (Controls.inMemory ())

              DurableControls.record suspendedOnly (Controls.suspend ops "halt") |> ignore

              Expect.equal
                  (Demanded.checkProjection (hostWith (DurableControls.coverage suspendedOnly services)) projection)
                  [ CoverageFinding.ServerGateRefusesCapability "host:audit" ]
                  "a suspended session has the capability and refuses it — a gate finding, not an absence"
          }

          test "a session that receives NO control costs nothing and runs identically" {
              let uncontrolled = Counter()
              let controlled = Counter()

              let project (outcome: HandlerOutcome) =
                  {| Tree = CanonicalJson.encodeNode outcome.Store.Tree
                     Committed = outcome.Committed
                     Performed = outcome.Performed
                     Diagnostics = outcome.Diagnostics |> List.map (sprintf "%A") |}

              let direct =
                  Durable.run
                      (durableWith (Journal.inMemory ()))
                      "inv-0"
                      (registryOf uncontrolled.Performer)
                      Fuaran.Core.DataFrame.noResolve
                      "call"
                      (auditing "done")
                      store

              let withControls =
                  DurableControls.run
                      (durableWith (Journal.inMemory ()))
                      (ControlServices.create scope)
                      "inv-0"
                      (registryOf controlled.Performer)
                      Fuaran.Core.DataFrame.noResolve
                      "call"
                      (auditing "done")
                      store

              Expect.equal
                  (project withControls.Durable.Outcome)
                  (project direct.Outcome)
                  "the controlled run IS the durable run when no act was recorded"

              Expect.isEmpty withControls.Refusals "nothing refused"
              Expect.equal withControls.Controls Controls.initial "and the fold of an empty stream is the initial state"
              Expect.equal controlled.Count uncontrolled.Count "the performer ran the same number of times"
          }

          // ── suspend, resume, and the replay between them ─────────────

          test "a resumed session's continuation is BYTE-IDENTICAL to an uninterrupted one" {
              let run (interrupt: bool) =
                  let counter = Counter()
                  let services = servicesOf (registryOf counter.Performer) (auditing "done")
                  let controls = controlsOn (Controls.inMemory ())
                  let durable = durableWith (Journal.inMemory ())
                  let session = ServerSession.init services empty callWire

                  let first = DurableControls.step durable controls "inv-0" session (clickEv 0)

                  let resumedFrom =
                      if interrupt then
                          DurableControls.record controls (Controls.suspend detector "denial pattern")
                          |> ignore

                          // The event a suspended session refuses. It must leave
                          // no trace at all — that is what makes the resume a
                          // continuation rather than a reconstruction.
                          let refused =
                              DurableControls.step durable controls "inv-1" first.Session (clickEv 1)

                          Expect.isTrue refused.Output.Rejected.IsSome "the suspended step was refused"

                          DurableControls.record controls (Controls.resume ops "reviewed") |> ignore
                          refused.Session
                      else
                          first.Session

                  let second = DurableControls.step durable controls "inv-2" resumedFrom (clickEv 2)

                  {| Tree = CanonicalJson.encodeNode second.Session.Resolved
                     Readout = readout second.Session.Resolved
                     Performed = second.Output.Performed
                     Ops = second.Output.Ops |> List.map (sprintf "%A")
                     Calls = counter.Count |}

              let uninterrupted = run false
              let interrupted = run true

              Expect.equal
                  interrupted.Tree
                  uninterrupted.Tree
                  "the resumed continuation is the canonical bytes of the uninterrupted one"

              Expect.equal interrupted.Performed uninterrupted.Performed "the same capabilities, in the same order"
              Expect.equal interrupted.Ops uninterrupted.Ops "and the same diff shipped"
              Expect.equal interrupted.Readout (Some "done") "the work happened"

              Expect.equal
                  interrupted.Calls
                  uninterrupted.Calls
                  "and the suspension cost the host performer nothing — no duplicate, no loss"
          }

          test "a MID-STAGE suspend records the indeterminate window, and the resume refuses to guess" {
              let journal = Journal.inMemory ()
              let services = durableWith journal
              let controls = controlsOn (Controls.inMemory ())
              let mutable calls = 0

              let dying =
                  ServerEffectRegistry.denyAll
                  |> ServerEffectRegistry.register "audit" (fun _ ->
                      calls <- calls + 1
                      raise (ProcessDied "after the effect"))
                  |> ServerEffectRegistry.permissive

              let died =
                  try
                      DurableControls.run
                          services
                          controls
                          "inv-0"
                          dying
                          Fuaran.Core.DataFrame.noResolve
                          "call"
                          (auditing "done")
                          store
                      |> ignore

                      false
                  with ProcessDied _ ->
                      true

              Expect.isTrue died "the fixture's crash must actually have happened"
              Expect.equal calls 1 "the effect committed, and the record of it did not"

              // The operator suspends across the live invocation. The window is
              // READ from the effect journal rather than asserted by the raiser,
              // which is the only way it can be right.
              let entry =
                  DurableControls.suspendMidStage services controls [ "inv-0" ] ops "crashed mid-handler"

              match entry.MidStage with
              | [ window ] ->
                  Expect.equal window.Invocation "inv-0" "the invocation that was live"
                  Expect.equal window.Step 0 "the step ordinal it stopped at"
                  Expect.equal window.Capability "host:audit" "and the capability nobody can decide"
              | other -> failtestf "expected exactly one indeterminate window on the record, got %A" other

              Expect.isTrue (Controls.isSuspended (DurableControls.stateOf controls)) "and the session is suspended"

              // A suspend raised with nothing open records NO window, and that is
              // a statement rather than an omission.
              let quiet =
                  DurableControls.suspendMidStage services controls [ "inv-never-ran" ] ops "quiet"

              Expect.isEmpty quiet.MidStage "no window was open, and the record says so"

              // Resuming re-runs the invocation. The recorded step is
              // indeterminate, so the default REFUSES rather than re-invoking —
              // rounding it either way publishes a guarantee the substrate does
              // not provide.
              DurableControls.record controls (Controls.resume ops "reviewed") |> ignore

              let counter = Counter()

              let resumed =
                  DurableControls.run
                      services
                      controls
                      "inv-0"
                      (registryOf counter.Performer)
                      Fuaran.Core.DataFrame.noResolve
                      "call"
                      (auditing "done")
                      store

              Expect.equal resumed.Durable.Indeterminate [ 0 ] "the resume names the step it cannot decide"
              Expect.equal counter.Count 0 "and does not re-invoke it"

              Expect.isEmpty
                  resumed.Refusals
                  "no CONTROL refused this — the indeterminate window is D12's, not a control's"
          }

          // ── the wire ─────────────────────────────────────────────────

          test "every op round-trips through its canonical form" {
              let entries =
                  [ { Sequence = 0
                      Op = ControlOp.Suspend
                      Actor = detector
                      Reason = "denial pattern"
                      MidStage =
                        [ { Invocation = "inv-0"
                            Step = 2
                            Capability = "host:audit" } ] }
                    { Sequence = 1
                      Op =
                        ControlOp.Throttle
                            { Capability = "host:audit"
                              MaxPerInvocation = 2 }
                      Actor = ops
                      Reason = "over budget"
                      MidStage = [] }
                    { Sequence = 2
                      Op = ControlOp.Revoke "audit"
                      Actor = ops
                      Reason = "withdrawn"
                      MidStage = [] }
                    { Sequence = 3
                      Op = ControlOp.Resume
                      Actor = ops
                      Reason = "reviewed"
                      MidStage = [] } ]

              for entry in entries do
                  match Controls.decode (Controls.encode entry) with
                  | Ok decoded -> Expect.equal decoded entry (sprintf "%s does not round-trip" (ControlOp.tag entry.Op))
                  | Error refusal -> failtestf "%s was refused: %A" (ControlOp.tag entry.Op) refusal
          }

          test "the canonical rendering puts $type first and orders its members" {
              let rendered =
                  Controls.render
                      { Sequence = 7
                        Op =
                          ControlOp.Throttle
                              { Capability = "host:audit"
                                MaxPerInvocation = 2 }
                        Actor = ops
                        Reason = "over budget"
                        MidStage = [] }

              Expect.equal
                  rendered
                  "{\"$type\":\"Throttle\",\"actor\":{\"id\":\"ops\",\"kind\":\"operator\"},\"reason\":\"over budget\",\"sequence\":7,\"window\":{\"capability\":\"host:audit\",\"maxPerInvocation\":2}}"
                  "the bytes are the document"
          }

          test "an absent optional member is OMITTED, never rendered as a null" {
              let rendered =
                  Controls.render
                      { Sequence = 0
                        Op = ControlOp.Resume
                        Actor = ops
                        Reason = "reviewed"
                        MidStage = [] }

              Expect.isFalse (rendered.Contains "midStage") "an empty window list is absent, not empty-and-present"
              Expect.isFalse (rendered.Contains "null") "and there is no null on this wire"
          }

          test "the decoder REFUSES what the vocabulary does not admit" {
              // A check that has never been seen to refuse anything is a check
              // nobody has verified.
              let refusalOf (json: string) =
                  match ProgramWire.parseDocument json |> Result.bind Controls.decode with
                  | Ok entry -> failtestf "expected a refusal, decoded %A" entry
                  | Error refusal -> refusal.Class

              Expect.equal
                  (refusalOf
                      "{\"$type\":\"Detonate\",\"actor\":{\"id\":\"ops\",\"kind\":\"operator\"},\"reason\":\"x\",\"sequence\":0}")
                  RefusalClass.UnknownEffectArm
                  "a fifth control is a decision, not a document"

              Expect.equal
                  (refusalOf
                      "{\"$type\":\"Resume\",\"actor\":{\"id\":\"ops\",\"kind\":\"operator\"},\"reason\":\"x\",\"sequence\":0,\"extra\":1}")
                  RefusalClass.UndeclaredMember
                  "an undeclared member is refused"

              Expect.equal
                  (refusalOf "{\"$type\":\"Resume\",\"actor\":{\"id\":\"ops\",\"kind\":\"operator\"},\"sequence\":0}")
                  RefusalClass.MissingMember
                  "a control with no reason is not a control"

              Expect.equal
                  (refusalOf
                      "{\"$type\":\"Revoke\",\"actor\":{\"id\":\"ops\",\"kind\":\"operator\"},\"performer\":\"\",\"reason\":\"x\",\"sequence\":0}")
                  RefusalClass.EmptyName
                  "a revocation must name a performer"

              Expect.equal
                  (refusalOf
                      "{\"$type\":\"Suspend\",\"actor\":{\"id\":\"ops\",\"kind\":\"robot\"},\"reason\":\"x\",\"sequence\":0}")
                  RefusalClass.UnknownEffectArm
                  "and an actor is a person or a machine, with no third reading"
          } ]
