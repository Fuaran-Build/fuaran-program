/// A program reports a FINDING into its trace (Phase 2195, D45), pinned at the
/// verb witness — a state-only composition, the shape a store-mutating verb
/// has:
///
///   * **the outcome from the trace**: a handler's `Report` stages land in the
///     outcome's `Findings` in program order, and a host derives the
///     invocation's outcome — typed by its OWN vocabulary — from them alone,
///     with no record kept beside the program; a finding reported before a
///     refusal survives the rollback, as the diagnostics do;
///   * **the host's vocabulary**: the codes and severities a host has are an
///     allow-list on the `Report` capability's arguments, so a program
///     reporting one the host does not have is refused while planning, before
///     anything performs, and the refusal names the argument, never the token;
///   * **the journal**: the durable tier journals each finding at its ordinal,
///     `Durable.findings` reads an invocation's findings off the journal alone,
///     a resumed run is SERVED them, and a resume that would report another
///     finding at that ordinal is refused as a divergence;
///   * **the static documents**: the handler document carries the arm and
///     round-trips; the demanded projection names that the handler reports,
///     with which codes and severities, beside the host's declared bound; the
///     replay classification is safe and the undo posture reversible, because
///     a report is not an act and has no inverse to run.
module Fuaran.Program.Tests.ReportTests

open Expecto
open Fuaran.Core
open Fuaran.Compute
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.VerbDomain

// ─── fixtures ────────────────────────────────────────────────────────────────

exception private ProcessDied of string

type private VerbHandler = Handler<Nothing, FileOp>

let private empty: FileMap = { Files = Map.empty; Published = [] }

/// The HOST's vocabulary — the types the host's own code reads a finding
/// through. Program never sees them: it carries the tokens, and the host's
/// allow-list on `Report` is what makes this decoder total over every finding
/// that can reach a trace.
type private Severity =
    | Info
    | Problem

type private Code =
    | ArchiveStarted
    | Archived
    | NoteMissing

type private HostFinding =
    { Kind: Code
      Level: Severity
      Text: string }

let private codes: (string * Code) list =
    [ "archive-started", ArchiveStarted
      "archived", Archived
      "note-missing", NoteMissing ]

let private severities: (string * Severity) list =
    [ "info", Info; "problem", Problem ]

/// The host's decoder over Program's tokens.
let private typed (finding: Finding) : Result<HostFinding, string> =
    match List.tryFind (fst >> (=) finding.Code) codes, List.tryFind (fst >> (=) finding.Severity) severities with
    | Some(_, code), Some(_, severity) ->
        Ok
            { Kind = code
              Level = severity
              Text = finding.Message }
    | _ -> Error "not this host's vocabulary"

/// The outcome a host derives from the trace ALONE: the verb's exit status,
/// non-zero when any finding is a problem.
let private exitOf (findings: Finding list) : int =
    let hostFindings =
        findings
        |> List.map (fun finding ->
            match typed finding with
            | Ok host -> host
            | Error reason -> failtestf "a finding reached the trace the host cannot type: %s" reason)

    if hostFindings |> List.exists (fun f -> f.Level = Problem) then
        1
    else
        0

let private report (code: string) (severity: string) (message: string) : HandlerStage<Nothing, FileOp> =
    Effect(
        ServerEffect.Report(
            { Code = code
              Severity = severity
              Message = message }
            : Finding
        )
    )

/// The verb, reporting as it goes: a finding at the start, the archive's
/// ops, and a finding at the end.
let private archive: VerbHandler =
    { Name = "archive"
      Stages =
        [ report "archive-started" "info" "archiving notes/x.md"
          Effect(ServerEffect.ApplyOps [ Check(Exists "notes/x.md"); Read "notes/x.md" ])
          Effect(ServerEffect.ApplyOps [ Write("notes/archive/x.md", "archived"); Delete "notes/x.md" ])
          report "archived" "info" "notes/x.md archived" ] }

/// The verb over a note that is not there: a finding, then the guard refuses.
let private archiveMissing: VerbHandler =
    { Name = "archive-missing"
      Stages =
        [ report "note-missing" "problem" "notes/x.md is not in the store"
          Effect(ServerEffect.ApplyOps [ Check(Exists "notes/x.md"); Read "notes/x.md" ]) ] }

/// The host: every capability admitted, its ops bounded to the notes store,
/// and its finding vocabulary DECLARED as the `Report` capability's bound.
let private registry: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.constrain
        "ApplyOps"
        [ ServerConstraintClause.AllowList("path", [ "notes/x.md"; "notes/archive/x.md" ])
          ServerConstraintClause.AllowList(ServerArgumentPolicy.DestinationArgument, [ "local" ]) ]
    |> ServerEffectRegistry.constrain
        FindingRecorder.Capability
        [ ServerConstraintClause.AllowList(Finding.CodeMember, codes |> List.map fst)
          ServerConstraintClause.AllowList(Finding.SeverityMember, severities |> List.map fst) ]

let private seeded () =
    let world = World()
    world.Seed("notes/x.md", "live")
    world

let private storeOf (world: World) : ServerStore<FileMap, unit> =
    { Tree = { empty with Files = world.Files }
      Bindings = () }

let private run (registry: ServerEffectRegistry) (world: World) (handler: VerbHandler) =
    Handler.runWith
        witness
        registry
        (OpPerformance.performedWithDetail (world.Performer None))
        DataFrame.noResolve
        "verb"
        handler
        (storeOf world)

let private freshJournal () =
    Journal.declaringDurable (Journal.inMemory ())

/// A journal that dies the moment the perform phase is about to attempt
/// `step` — after every plan-phase ordinal, the reports among them, completed.
let private dyingBeforeAttempting (step: int) (inner: EffectJournal) : EffectJournal =
    { inner with
        Append =
            fun entry ->
                match entry.Phase with
                | JournalPhase.Attempted when entry.Step = step -> raise (ProcessDied "between steps")
                | _ -> inner.Append entry }

let private durably (journal: EffectJournal) (world: World) (handler: VerbHandler) =
    Durable.runWith
        witness
        (DurableServices.create |> DurableServices.withJournal journal)
        "inv"
        registry
        (OpPerformance.performedWithDetail (world.Performer None))
        DataFrame.noResolve
        "verb"
        handler
        (storeOf world)

let private finding code severity message : Finding =
    ({ Code = code
       Severity = severity
       Message = message }
    : Finding)

let private archiveFindings =
    [ finding "archive-started" "info" "archiving notes/x.md"
      finding "archived" "info" "notes/x.md archived" ]

// ─── the outcome, from the trace ─────────────────────────────────────────────

let private outcome =
    testList
        "the outcome, from the trace"
        [ test "a handler's findings land in program order, and the host derives its outcome from them alone" {
              let world = seeded ()
              let outcome = run registry world archive

              Expect.isTrue outcome.Committed "the verb committed"
              Expect.equal outcome.Findings archiveFindings "both findings, in program order"

              Expect.equal
                  (outcome.Findings |> List.map typed)
                  [ Ok
                        { Kind = ArchiveStarted
                          Level = Info
                          Text = "archiving notes/x.md" }
                    Ok
                        { Kind = Archived
                          Level = Info
                          Text = "notes/x.md archived" } ]
                  "typed by the host's own vocabulary"

              Expect.equal (exitOf outcome.Findings) 0 "the outcome the host derives: success"

              Expect.equal
                  (List.length (List.filter ((=) "Report") outcome.Performed))
                  2
                  "each report is on the audit trail"
          }

          test "a finding reported before a refusal survives the rollback, and the outcome derives from it" {
              let world = World()
              let outcome = run registry world archiveMissing

              Expect.isFalse outcome.Committed "the guard refused"
              Expect.isEmpty outcome.Performed "nothing performed"
              Expect.isEmpty world.Invocations "nothing reached the world"

              Expect.equal
                  outcome.Findings
                  [ finding "note-missing" "problem" "notes/x.md is not in the store" ]
                  "the finding the program reported on its way to the refusal"

              Expect.equal (exitOf outcome.Findings) 1 "the outcome the host derives: a problem"
          }

          test "a finding outside the host's declared vocabulary is refused while planning, naming the argument" {
              let world = seeded ()

              let offList: VerbHandler =
                  { Name = "off-list"
                    Stages =
                      [ Effect(ServerEffect.ApplyOps [ Write("notes/archive/x.md", "archived") ])
                        report "secret-token-xyz" "info" "not a code this host has" ] }

              let outcome = run registry world offList

              Expect.isFalse outcome.Committed "refused"
              Expect.isEmpty world.Invocations "before anything performed"
              Expect.isEmpty outcome.Findings "the undeclared finding never reached the trace"

              match outcome.Diagnostics with
              | [ ServerDiagnostic.Failed(capability, reason) ] ->
                  Expect.equal capability "Report" "the arm's capability"
                  Expect.stringContains reason Finding.CodeMember "names the argument the host bounded"

                  Expect.isFalse (reason.Contains "secret-token-xyz") "never the token the document supplied"
              | other -> failtestf "expected one plan-time refusal, got %A" other
          }

          test "a host whose gate refuses `Report` denies it audibly, and the handler rolls back" {
              let world = seeded ()

              let refusing =
                  registry
                  |> ServerEffectRegistry.withGate (fun capability -> capability <> FindingRecorder.Capability)

              let outcome = run refusing world archive

              Expect.isFalse outcome.Committed "denied"
              Expect.isEmpty outcome.Findings "a denied report recorded nothing"

              Expect.equal
                  outcome.Diagnostics
                  [ ServerDiagnostic.Denied(ServerEffectDenial.GateRefused "Report") ]
                  "the denial carries the capability"
          } ]

// ─── the journal ─────────────────────────────────────────────────────────────

let private journal =
    testList
        "the journal"
        [ test "the durable tier journals each finding at its ordinal, and the journal alone yields them" {
              let world = seeded ()
              let journal = freshJournal ()
              let durable = durably journal world archive

              Expect.equal durable.Outcome.Findings archiveFindings "the run's findings"

              // The two reports are plan-phase ordinals, before every staged
              // op: one cursor, plan then perform (D39).
              Expect.equal durable.Invoked [ 0; 1; 2; 3; 4 ] "two reports, then three performed ops"

              Expect.equal (Durable.findings journal "inv") (Ok archiveFindings) "read off the journal, nothing re-run"
              Expect.equal (Durable.findings journal "other") (Ok []) "another invocation reported nothing"
          }

          test "a resumed run is SERVED the journaled findings, and journals none twice" {
              let world = seeded ()
              let journal = freshJournal ()

              Expect.throws
                  (fun () -> durably (dyingBeforeAttempting 2 journal) world archive |> ignore)
                  "the process dies before the first op is attempted"

              Expect.isEmpty world.Invocations "nothing was performed"

              Expect.equal
                  (Durable.findings journal "inv")
                  (Ok archiveFindings)
                  "the dead run's findings are in its journal already"

              let resumed = durably journal world archive

              Expect.isTrue resumed.Outcome.Committed "the resume completed"
              Expect.equal resumed.Replayed [ 0; 1 ] "both findings served from the journal"
              Expect.equal resumed.Invoked [ 2; 3; 4 ] "only the ops were performed"
              Expect.equal resumed.Outcome.Findings archiveFindings "the outcome carries the served findings"
              Expect.equal (Durable.findings journal "inv") (Ok archiveFindings) "and the journal holds each once"
              Expect.equal (exitOf resumed.Outcome.Findings) 0 "the outcome the host derives on the resume"
          }

          test "a resume that would report another finding at a journaled ordinal is refused as a divergence" {
              let world = seeded ()
              let journal = freshJournal ()

              Expect.throws
                  (fun () -> durably (dyingBeforeAttempting 2 journal) world archive |> ignore)
                  "the process dies before the first op is attempted"

              let reworded =
                  { archive with
                      Stages =
                          report "archive-started" "info" "a different message"
                          :: List.tail archive.Stages }

              let resumed = durably journal world reworded

              Expect.isFalse resumed.Outcome.Committed "refused"
              Expect.isEmpty world.Invocations "nothing performed"

              Expect.equal
                  resumed.Outcome.Diagnostics
                  [ ServerDiagnostic.Failed("Report", DurableCode.ReplayDivergence) ]
                  "the finding moved, so no recorded one is served for it"
          }

          test "a journal record at a report's ordinal that is not a finding is refused, never read as one" {
              Expect.equal (Finding.decode (JStr "not a finding")) (Error Finding.Malformed) "malformed"

              Expect.equal
                  (Finding.decode (Finding.encode (finding "archived" "info" "m")))
                  (Ok(finding "archived" "info" "m"))
                  "a finding round-trips through its record"
          } ]

// ─── the static documents ────────────────────────────────────────────────────

let private documented =
    testList
        "the static documents"
        [ test "the handler document carries the arm, in canonical member order, and round-trips" {
              let encoded =
                  match HandlerWire.encodeHandler witness archive with
                  | Ok document -> document
                  | Error refusal -> failtestf "does not encode: %A" refusal

              Expect.stringContains
                  encoded
                  """{"$type":"Effect","effect":{"$type":"Report","code":"archived","message":"notes/x.md archived","severity":"info"}}"""
                  "the finding's wire form"

              match HandlerWire.decodeHandler witness encoded with
              | Ok decoded -> Expect.equal decoded archive "decodes to itself"
              | Error refusal -> failtestf "does not decode: %A" refusal
          }

          test "the decoder refuses an empty token, a missing member and an undeclared one" {
              let decode (effect: string) =
                  HandlerWire.decodeHandler
                      witness
                      ("""{"$type":"Handler","name":"r","stages":[{"$type":"Effect","effect":"""
                       + effect
                       + "}]}")

              let classOf effect =
                  match decode effect with
                  | Ok _ -> failtestf "decoded: %s" effect
                  | Error refusal -> refusal.Class

              Expect.equal
                  (classOf """{"$type":"Report","code":"","message":"m","severity":"info"}""")
                  RefusalClass.MissingMember
                  "an empty code names nothing"

              Expect.equal
                  (classOf """{"$type":"Report","code":"archived","message":"m","severity":""}""")
                  RefusalClass.MissingMember
                  "an empty severity names nothing"

              Expect.equal
                  (classOf """{"$type":"Report","code":"archived","severity":"info"}""")
                  RefusalClass.MissingMember
                  "the message is required"

              Expect.equal
                  (classOf
                      """{"$type":"Report","capability":"Report","code":"archived","message":"m","severity":"info"}""")
                  RefusalClass.UndeclaredMember
                  "a carried capability is refused"

              Expect.isOk
                  (decode """{"$type":"Report","code":"archived","message":"","severity":"info"}""")
                  "an empty message is a message"
          }

          test
              "the demanded projection names that the handler reports, which codes and severities, and the host's bound" {
              let projection =
                  (Harvest.ofRegistration (ServerEffectRegistry.queryPosture registry) witness [ archive ]).Projection
                  |> ServerDemanded.withConstraints registry

              let server =
                  match projection.Server with
                  | Some server -> server
                  | None -> failtest "a server walk ran"

              Expect.contains server.Effects "Report" "the arm"
              Expect.contains server.Capabilities "Report" "the capability the gate is asked"

              let reported =
                  server.Reach
                  |> List.filter (fun reach -> reach.Capability = "Report")
                  |> List.map (fun reach -> reach.Argument, reach.Name)

              Expect.equal
                  (List.sort reported)
                  (List.sort [ "code", "archive-started"; "code", "archived"; "severity", "info" ])
                  "the codes and the severity it can report, never a message"

              Expect.isTrue
                  (server.Constraints |> List.exists (fun c -> c.Capability = "Report"))
                  "the host's declared vocabulary joins it as the capability's bound"

              let document = Demanded.encode projection
              Expect.stringContains document "\"version\":11" "the version that names the widened vocabulary"
              Expect.isFalse (document.Contains "notes/x.md archived") "no message reaches the document"
          }

          test "a report is replay-safe and has no inverse to run: the postures read it as nothing" {
              let reportOnly: VerbHandler =
                  { Name = "report-only"
                    Stages = [ report "archived" "info" "m" ] }

              Expect.isEmpty (HandlerWire.replayReasons QueryPosture.PureRead witness reportOnly) "no replay reason"

              Expect.equal
                  (HandlerWire.replaySafety QueryPosture.PureRead witness reportOnly)
                  ReplaySafety.Safe
                  "replay-safe"

              Expect.isEmpty (Undo.reasons QueryPosture.PureRead witness reportOnly) "no undo reason"
              Expect.equal (Undo.posture QueryPosture.PureRead witness reportOnly) UndoVerdict.Reversible "reversible"

              Expect.equal
                  (Undo.posture QueryPosture.PureRead witness archive)
                  (Undo.posture
                      QueryPosture.PureRead
                      witness
                      { archive with
                          Stages =
                              archive.Stages
                              |> List.filter (fun stage ->
                                  match stage with
                                  | Effect(ServerEffect.Report _) -> false
                                  | _ -> true) })
                  "reporting moves no handler's undo posture"
          }

          test "an undo of a reporting run runs its inverses and leaves the findings in the run it undoes" {
              let world = seeded ()

              let outcome, plan =
                  Handler.runPlanned
                      witness
                      registry
                      (OpPerformance.performedWithDetail (world.Performer None))
                      DataFrame.noResolve
                      "verb"
                      archive
                      (storeOf world)

              Expect.isTrue outcome.Committed "committed"

              Expect.isTrue
                  (plan.Steps
                   |> List.forall (fun step ->
                       match step with
                       | UndoStep.Reached _ -> false
                       | _ -> true))
                  "a report is on no step of the undo trail"

              match
                  Undo.run
                      witness
                      registry
                      (OpPerformance.performedWithDetail (world.Performer None))
                      DataFrame.noResolve
                      "verb"
                      plan
                      outcome.Store
              with
              | Error refusal -> failtestf "the undo was refused: %A" refusal
              | Ok undone ->
                  Expect.isTrue undone.Committed "the undo committed"
                  Expect.isEmpty undone.Findings "the undo reports nothing of its own"
                  Expect.equal world.Files.["notes/x.md"] "live" "the note is restored"
          } ]

[<Tests>]
let tests =
    testList "a program reports a finding into its trace (Phase 2195, D45)" [ outcome; journal; documented ]
