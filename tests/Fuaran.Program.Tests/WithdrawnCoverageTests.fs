module Fuaran.Program.Tests.WithdrawnCoverageTests

// ============================================================================
//  A revoked op performer reads as ABSENT in coverage, not as a policy
//  refusal (Phase 1993, DECISIONS.md D37, amending D28 item 2), at the toy
//  witness.
//
//  What is pinned here:
//    * the coverage model: `ServerCoverage.Withdrawn` is empty by default, a
//      demanded withdrawn capability is reported as `ServerCapabilityWithdrawn`
//      BEFORE the gate is consulted, and its description does not say a policy
//      change resolves it;
//    * the controls: with ops performed and `ApplyOps` revoked, coverage
//      carries the key in the slot and leaves the gate the registry's; a
//      suspended session still reads as a gate refusal; a revoked HOST
//      performer is unchanged; nothing revoked, and `OpPerformance.InMemory`,
//      give the coverage and the findings the registry alone gives;
//    * monotonicity: no `Resume` restores a withdrawn op performer, on any
//      prefix of the stream; the durable form reads the same answer.
// ============================================================================

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain
open Fuaran.Program.Tests.ToyServerPlacement

let private operator = ControlActor.operator "ops"

let private relabel = toyRelabel.Handlers |> Map.find RelabelEndpoint

/// What the relabel handler can ever ask of a server host: `ApplyOps` among
/// the gate-facing capabilities, and the host function `audit`.
let private projection = ServerDemanded.ofHandlers witness [ relabel ]

/// A host that covers the handler's client-tier effects (its compute stage
/// beeps), so every finding the check makes is about the server tier.
let private serverHost (coverage: ServerCoverage) =
    { HostCoverage.nothing with
        Effects = Set.ofList projection.Effects
        Gate = fun _ -> true }
    |> HostCoverage.withServer coverage

let private performed: OpPerformance<ToyNode, ToyOp> =
    OpPerformance.performedWithDetail (fun _ _ -> Ok(JObj []))

let private inMemory: OpPerformance<ToyNode, ToyOp> = OpPerformance.InMemory

let private registry = toyRelabel.Effects

/// The control state a sequence of acts folds to.
let private stateAfter (requests: ControlRequest list) : ControlState =
    let journal = Controls.inMemory ()

    for request in requests do
        Controls.record journal "session" request |> ignore

    Controls.stateOf journal "session"

let private revokeOps =
    Controls.revoke operator "ops reach the world; withdrawn" OpPerformance.RegistrationKey

let private findings (coverage: ServerCoverage) =
    Demanded.checkProjection (serverHost coverage) projection

[<Tests>]
let coverageModel =
    testList
        "Phase 1993 — ServerCoverage carries withdrawn arms"
        [

          test "the default withdraws nothing, and every builder keeps it so" {
              Expect.isEmpty ServerCoverage.nothing.Withdrawn "nothing is withdrawn by default"

              let built =
                  ServerCoverage.nothing
                  |> ServerCoverage.withFunctions [ "audit" ]
                  |> ServerCoverage.permissive
                  |> ServerCoverage.withChannels [ "mail" ]

              Expect.isEmpty built.Withdrawn "no builder but `withWithdrawn` touches the slot"
              Expect.isEmpty (ServerDemanded.coverageOfRegistry registry).Withdrawn "nor does the registry's coverage"
          }

          test "a demanded withdrawn capability is an ABSENCE, reported before the gate is asked" {
              let mutable asked = []

              let coverage =
                  ServerCoverage.nothing
                  |> ServerCoverage.withFunctions [ "audit" ]
                  |> ServerCoverage.withGate (fun capability ->
                      asked <- capability :: asked
                      true)
                  |> ServerCoverage.withWithdrawn [ OpPerformance.RegistrationKey ]

              Expect.equal
                  (findings coverage)
                  [ CoverageFinding.ServerCapabilityWithdrawn OpPerformance.RegistrationKey ]
                  "however permissive the gate, the withdrawn arm is absent"

              Expect.isFalse
                  (List.contains OpPerformance.RegistrationKey asked)
                  "the gate was never consulted about the withdrawn capability"
          }

          test "a withdrawn capability beats a refusing gate, and an undemanded one says nothing" {
              let refusing =
                  ServerCoverage.nothing
                  |> ServerCoverage.withFunctions [ "audit" ]
                  |> ServerCoverage.withGate (fun capability -> capability = "host:audit")
                  |> ServerCoverage.withWithdrawn [ OpPerformance.RegistrationKey; "Notify" ]

              Expect.equal
                  (findings refusing)
                  [ CoverageFinding.ServerCapabilityWithdrawn OpPerformance.RegistrationKey ]
                  "the fact policy cannot fix comes first, and `Notify` is not demanded here"
          }

          test "the description names the capability and does not say a policy change resolves it" {
              let text =
                  Demanded.describe (CoverageFinding.ServerCapabilityWithdrawn OpPerformance.RegistrationKey)

              Expect.stringContains text OpPerformance.RegistrationKey "the finding names the arm"
              Expect.stringContains text "withdrawn" "and says it was withdrawn"
              Expect.isFalse (text.Contains "policy") "and never sends the reader to policy"
              Expect.isFalse (text.Contains "host function") "nor to a host function the handler never names"
          } ]

[<Tests>]
let controlCoverage =
    testList
        "Phase 1993 — a revoked op performer reads as absent in control coverage"
        [

          test "with ops performed and ApplyOps revoked, the check reports the absence, not a gate refusal" {
              Expect.isEmpty
                  (findings (Controls.coverage (stateAfter []) registry performed))
                  "before the withdrawal the placement covers the relabel handler"

              let coverage = Controls.coverage (stateAfter [ revokeOps ]) registry performed

              Expect.equal
                  (findings coverage)
                  [ CoverageFinding.ServerCapabilityWithdrawn OpPerformance.RegistrationKey ]
                  "ApplyOps is absent, under the key the revoke named"

              Expect.equal
                  coverage.Withdrawn
                  (Set.singleton OpPerformance.RegistrationKey)
                  "the withdrawal is carried in the slot"

              Expect.isTrue (coverage.Gate OpPerformance.RegistrationKey) "and the gate is the registry's, untouched"

              Expect.equal
                  coverage.HostFunctions
                  (Set.ofList [ "audit"; "decline" ])
                  "the host performers are untouched: the op performer was never one of them"
          }

          test "a suspended session still reports ServerGateRefusesCapability" {
              let coverage =
                  Controls.coverage (stateAfter [ Controls.suspend operator "halt" ]) registry performed

              Expect.equal
                  (findings coverage)
                  [ CoverageFinding.ServerGateRefusesCapability "host:audit"
                    CoverageFinding.ServerGateRefusesCapability OpPerformance.RegistrationKey ]
                  "a suspend can be lifted, so it is policy"

              Expect.isEmpty coverage.Withdrawn "and withdraws nothing"
          }

          test "suspended AND revoked: the op arm reads as the absence, the host function as the suspend" {
              let coverage =
                  Controls.coverage (stateAfter [ revokeOps; Controls.suspend operator "halt" ]) registry performed

              Expect.equal
                  (findings coverage)
                  [ CoverageFinding.ServerGateRefusesCapability "host:audit"
                    CoverageFinding.ServerCapabilityWithdrawn OpPerformance.RegistrationKey ]
                  "no resume will restore the op performer, so the suspend does not speak for it"
          }

          test "a revoked HOST performer is unchanged: absent as a function, and ApplyOps is not named" {
              let coverage =
                  Controls.coverage (stateAfter [ Controls.revoke operator "withdrawn" "audit" ]) registry performed

              Expect.equal
                  (findings coverage)
                  [ CoverageFinding.UnregisteredServerFunction "audit" ]
                  "the host function is absent, as before"

              Expect.isEmpty coverage.Withdrawn "the slot is for the arms with no host function"
          }

          test "nothing revoked, and ops in memory, give the registry's coverage and findings unchanged" {
              let capabilities =
                  [ "ApplyOps"
                    "EmitPatch"
                    "host:audit"
                    "host:ApplyOps"
                    "Notify"
                    "RunQuery"
                    "SetState" ]

              // The pre-1986 formula, spelled out: the registry with the
              // controls applied, and nothing else.
              let registryOnly (state: ControlState) =
                  ServerDemanded.coverageOfRegistry (Controls.apply ignore state registry)

              let sameAs (expected: ServerCoverage) (actual: ServerCoverage) (label: string) =
                  Expect.equal actual.HostFunctions expected.HostFunctions $"{label}: the same host functions"
                  Expect.equal actual.Channels expected.Channels $"{label}: the same channel surface"
                  Expect.equal actual.Withdrawn expected.Withdrawn $"{label}: the same (empty) withdrawn set"

                  Expect.equal
                      (capabilities |> List.map actual.Gate)
                      (capabilities |> List.map expected.Gate)
                      $"{label}: the same gate, capability by capability"

                  Expect.equal (findings actual) (findings expected) $"{label}: the same findings"

              let states =
                  [ "nothing recorded", stateAfter [], [ performed; inMemory ]
                    "a host performer revoked",
                    stateAfter [ Controls.revoke operator "withdrawn" "audit" ],
                    [ performed; inMemory ]
                    "suspended", stateAfter [ Controls.suspend operator "halt" ], [ performed; inMemory ]
                    "throttled",
                    stateAfter
                        [ Controls.throttle
                              operator
                              "slow"
                              { Capability = OpPerformance.RegistrationKey
                                MaxPerInvocation = 2 } ],
                    [ performed; inMemory ]
                    // In memory the apply IS the effect: a revoke of the key
                    // withdraws nothing (D26 item 5).
                    "the op performer revoked, ops in memory", stateAfter [ revokeOps ], [ inMemory ] ]

              for label, state, performances in states do
                  Expect.isEmpty (registryOnly state).Withdrawn $"{label}: the registry withdraws no arm"

                  for performance in performances do
                      sameAs (registryOnly state) (Controls.coverage state registry performance) label
          }

          test "a Resume does not restore a withdrawn op performer, on any prefix of the stream" {
              let requests =
                  [ revokeOps
                    Controls.suspend operator "halt"
                    Controls.resume operator "reviewed" ]

              for length in 1 .. List.length requests do
                  let coverage =
                      Controls.coverage (stateAfter (List.truncate length requests)) registry performed

                  Expect.contains
                      (findings coverage)
                      (CoverageFinding.ServerCapabilityWithdrawn OpPerformance.RegistrationKey)
                      $"ApplyOps stays absent after {length} control(s)"

              Expect.equal
                  (findings (Controls.coverage (stateAfter requests) registry performed))
                  [ CoverageFinding.ServerCapabilityWithdrawn OpPerformance.RegistrationKey ]
                  "the resume lifted the suspend, and only the suspend"
          }

          test "the durable form reads the same answer from the host record" {
              let journal = Controls.inMemory ()

              let controls =
                  ControlServices.create "session" |> ControlServices.withJournal journal

              let services =
                  { toyRelabel with
                      OpPerformance = performed }

              DurableControls.record controls revokeOps |> ignore

              Expect.equal
                  (findings (DurableControls.coverage controls services))
                  [ CoverageFinding.ServerCapabilityWithdrawn OpPerformance.RegistrationKey ]
                  "DurableControls.coverage follows Controls.coverage, signature unchanged"
          } ]
