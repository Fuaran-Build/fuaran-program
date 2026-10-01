/// The second witness, exercised against the four findings Phase 1967
/// answered (`DECISIONS.md` D19), one adversary per finding:
///
///   W3/W4  an off-list publish target is refused BEFORE anything performs,
///          and the demanded document names the paths and the target;
///   F1     a guard that does not hold halts the handler with NOTHING
///          performed;
///   F2     a part-way performance failure is reported with the prefix that
///          ran and the position it failed at;
///   W5     a typed refusal crosses the op channel as text and parses back.
///
/// Since Phase 1974 (`DECISIONS.md` D20) the verb fills the STATE AXIS ONLY,
/// and every test here runs it so: its handlers carry no compute stage (its
/// action type has no values), its guards are op-channel guards resolved
/// against the plan, and its performer is handed the planned state.
///
/// This project references the three core packages and no UI-tier package,
/// so the verb is the whole domain here, as the toy is beside it.
module Fuaran.Program.Tests.VerbWitnessTests

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.VerbDomain

let private empty: FileMap = { Files = Map.empty; Published = [] }

/// A handler under a witness that fills no dispatch axis: effect stages only.
type private VerbHandler = Handler<Nothing, FileOp>

/// The verb: check the shard exists, read it, write the archive copy, delete
/// the old one, check it is gone, publish.
let private archive: VerbHandler =
    { Name = "archive"
      Stages =
        [ Effect(ServerEffect.ApplyOps [ Check(Exists "notes/x.md"); Read "notes/x.md" ])
          Effect(ServerEffect.ApplyOps [ Write("notes/archive/x.md", "archived"); Delete "notes/x.md" ])
          Effect(ServerEffect.ApplyOps [ Check(Missing "notes/x.md"); Publish "origin" ]) ] }

/// A registry admitting every capability, with the verb's own policy: ops
/// may reach the notes store, locally, and publish to `origin` and nowhere
/// else — the destination CLASS bound beside the named target (W4).
let private registry: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.constrain
        "ApplyOps"
        [ ServerConstraintClause.AllowList("path", [ "notes/x.md"; "notes/archive/x.md" ])
          ServerConstraintClause.AllowList("target", [ "origin" ])
          ServerConstraintClause.AllowList(ServerArgumentPolicy.DestinationArgument, [ "local"; "origin" ]) ]

let private seeded () =
    let world = World()
    world.Seed("notes/x.md", "live")
    world

let private runIn
    (registry: ServerEffectRegistry)
    (world: World)
    (failAt: int option)
    (handler: VerbHandler)
    (tree: FileMap)
    : HandlerOutcome<FileMap, unit, FileOp, Nothing> =
    Handler.runWith
        witness
        registry
        (OpPerformance.performedBy (world.Performer failAt))
        DataFrame.noResolve
        "verb"
        handler
        { Tree = tree; Bindings = () }

let private run (world: World) (failAt: int option) (handler: VerbHandler) =
    runIn registry world failAt handler { empty with Files = world.Files }

// ─── Phase 1976: the two flow shapes on the op axis ─────────────────────────

/// The archive policy, widened for the flow tests: the marker the false arm
/// writes, and a CEILING on a repeat's count — the verb names the count as an
/// argument of the repeat's reach, so an allow-list bounds how many times a
/// body may run, and an over-bound repeat is refused before anything performs.
let private flowRegistry: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.constrain
        "ApplyOps"
        [ ServerConstraintClause.AllowList("path", [ "notes/x.md"; "notes/archive/x.md"; "notes/created.marker" ])
          ServerConstraintClause.AllowList("target", [ "origin" ])
          ServerConstraintClause.AllowList(ServerArgumentPolicy.DestinationArgument, [ "local"; "origin" ])
          ServerConstraintClause.AllowList("count", [ "1"; "2"; "3" ]) ]

/// The verb's two-arm construct, IN Program: migrate the shard if it is there,
/// create it (and leave a marker) if not, then publish. The exit assertion —
/// no marker — holds after the true arm and fails after the false arm, which
/// is the Janus discipline the inverse rests on.
let private migrate: VerbHandler =
    { Name = "migrate"
      Stages =
        [ Effect(
              ServerEffect.ApplyOps
                  [ Branch(
                        Exists "notes/x.md",
                        [ Write("notes/x.md", "migrated") ],
                        [ Write("notes/x.md", "created"); Write("notes/created.marker", "") ],
                        Some(Missing "notes/created.marker")
                    )
                    Publish "origin" ]
          ) ] }

let private flowRun (world: World) (handler: VerbHandler) =
    runIn flowRegistry world None handler { empty with Files = world.Files }

[<Tests>]
let tests =
    testList
        "Phase 1967 — the second witness: a verb over a file map"
        [ test "the verb runs: guards that hold, a plan applied, the plan performed after it commits" {
              let world = seeded ()
              let outcome = run world None archive

              Expect.isTrue outcome.Committed "the handler commits"

              Expect.equal
                  outcome.Performed
                  [ "ApplyOps"; "ApplyOps"; "ApplyOps"; "ApplyOps" ]
                  "four edits performed, each its own staged call, in plan order — the two checks are guards and never performed"

              Expect.equal
                  (world.Files |> Map.toList)
                  [ "notes/archive/x.md", "archived" ]
                  "the world was written and the old shard deleted — AFTER the plan committed"

              Expect.equal world.Published [ "origin" ] "and published"
              Expect.equal outcome.Store.Tree.Published [ "origin" ] "the committed plan agrees with the world"

              Expect.isFalse
                  (world.Invocations
                   |> List.exists (fun op -> op.Contains "Exists" || op.Contains "Missing"))
                  "no guard reached the performer"
          }

          // ── W3 / W4: the envelope says what the ops reach, and the policy binds it ──

          test "the demanded document names the paths and the target the verb's ops reach — and is pinned" {
              let projection = ServerDemanded.ofHandler witness archive

              let tier =
                  match projection.Server with
                  | Some tier -> tier
                  | None -> failtest "the walk ran, so the document must carry a server tier"

              Expect.equal
                  (tier.Reach |> List.map (fun r -> r.Capability, r.Argument, r.Name))
                  [ "ApplyOps", "destination", "local"
                    "ApplyOps", "destination", "origin"
                    "ApplyOps", "path", "notes/archive/x.md"
                    "ApplyOps", "path", "notes/x.md"
                    "ApplyOps", "target", "origin" ]
                  "every path, the target, and the two destination classes — distinct and sorted; the guards' paths are among them"

              // The document's bytes, pinned: this projection is this
              // repository's own artefact (docs/generic-tier.md §6), and the
              // reach is what version 5 added to it. A state-only verb reads
              // no state namespace — it has no binding store (Phase 1974).
              Expect.equal
                  (Demanded.encode projection)
                  ("{\"kind\":\"demanded\",\"version\":5,\"effects\":[],\"hostCalls\":[],"
                   + "\"stateNamespaces\":[],"
                   + "\"opaqueHandlers\":[],\"server\":{\"effects\":[\"ApplyOps\"],\"capabilities\":[\"ApplyOps\"],"
                   + "\"functions\":[],\"channels\":[],"
                   + "\"reach\":[{\"capability\":\"ApplyOps\",\"argument\":\"destination\",\"name\":\"local\"},"
                   + "{\"capability\":\"ApplyOps\",\"argument\":\"destination\",\"name\":\"origin\"},"
                   + "{\"capability\":\"ApplyOps\",\"argument\":\"path\",\"name\":\"notes/archive/x.md\"},"
                   + "{\"capability\":\"ApplyOps\",\"argument\":\"path\",\"name\":\"notes/x.md\"},"
                   + "{\"capability\":\"ApplyOps\",\"argument\":\"target\",\"name\":\"origin\"}],"
                   + "\"replay\":[],\"constraints\":[]}}")
                  "the document's bytes"

              // And read back: the reach survives the round trip, so a reader
              // holding only the document sees what the handler holds.
              match Demanded.decode (Demanded.encode projection) with
              | Ok read -> Expect.equal read projection "the document reads back as the projection"
              | Error failure -> failtestf "the document does not read back: %A" failure

              // The policy travels beside the reach it bounds.
              let withPolicy = ServerDemanded.withConstraints registry projection

              Expect.equal
                  (withPolicy.Server
                   |> Option.map (fun s -> s.Constraints |> List.map _.Capability))
                  (Some [ "ApplyOps" ])
                  "the host's bound on the arm rides the same document"
          }

          test "ADVERSARY W3 — an off-list publish target is refused before anything performs" {
              let world = seeded ()

              let elsewhere =
                  { archive with
                      Stages = archive.Stages @ [ Effect(ServerEffect.ApplyOps [ Publish "upstream" ]) ] }

              let outcome = run world None elsewhere

              Expect.isFalse outcome.Committed "refused"
              Expect.isEmpty outcome.Performed "nothing performed"
              Expect.isEmpty world.Invocations "the performer was never asked"
              Expect.equal (world.Files |> Map.toList) [ "notes/x.md", "live" ] "the world is untouched"
              Expect.isEmpty world.Published "nothing published"

              Expect.equal
                  (outcome.Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "argument-not-allowed:target"))
                  "refused while planning, naming the argument the host bounded and never the value"
          }

          test "ADVERSARY W4 — the destination CLASS binds: a remote the policy does not name is refused by class" {
              // A policy that names no `target` at all but bounds the
              // destination class still refuses a publish to an unnamed remote:
              // the bound is on where the op reaches, not on how the domain
              // spells it.
              let byClass =
                  ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
                  |> ServerEffectRegistry.constrain
                      "ApplyOps"
                      [ ServerConstraintClause.AllowList(ServerArgumentPolicy.DestinationArgument, [ "local" ]) ]

              let world = seeded ()

              let outcome =
                  runIn
                      byClass
                      world
                      None
                      { Name = "push"
                        Stages = [ Effect(ServerEffect.ApplyOps [ Write("notes/x.md", "x"); Publish "origin" ]) ] }
                      empty

              Expect.isFalse outcome.Committed "refused"
              Expect.isEmpty world.Invocations "before anything performs"

              Expect.equal
                  (outcome.Diagnostics |> List.last)
                  (ServerDiagnostic.Failed(
                      "ApplyOps",
                      "argument-not-allowed:" + ServerArgumentPolicy.DestinationArgument
                  ))
                  "the local write passed; the remote publish is off the class list"
          }

          test "a ceiling on the arm measures the ops' canonical bytes" {
              let bounded =
                  ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
                  |> ServerEffectRegistry.constrain "ApplyOps" [ ServerConstraintClause.Ceiling 64 ]

              let world = World()

              let outcome (content: string) =
                  runIn
                      bounded
                      world
                      None
                      { Name = "write"
                        Stages = [ Effect(ServerEffect.ApplyOps [ Write("a.md", content) ]) ] }
                      empty

              Expect.isTrue (outcome "small").Committed "under the ceiling"
              Expect.isFalse (outcome (String.replicate 64 "x")).Committed "over it"

              Expect.equal
                  ((outcome (String.replicate 64 "x")).Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "payload-over-ceiling:64"))
                  "naming the limit, never the size"
          }

          // ── F1: a guard that halts — on the op channel, over the plan ──

          test "ADVERSARY F1 — a guard that does not hold halts the handler with nothing performed" {
              // The archive copy already exists in the world, so the plan's
              // `Missing` check after the delete holds — but a run whose
              // delete is absent leaves the shard in the PLAN, and the guard
              // sees the plan.
              let world = seeded ()

              let keeps =
                  { Name = "keeps"
                    Stages =
                      [ Effect(ServerEffect.ApplyOps [ Write("notes/archive/x.md", "archived") ])
                        Effect(ServerEffect.ApplyOps [ Check(Missing "notes/x.md"); Publish "origin" ]) ] }

              let outcome = run world None keeps

              Expect.isFalse outcome.Committed "halted"
              Expect.isEmpty outcome.Performed "nothing performed"
              Expect.isEmpty world.Invocations "the performer was never asked — not even for the write before the guard"
              Expect.equal (world.Files |> Map.toList) [ "notes/x.md", "live" ] "the world is untouched"

              Expect.equal
                  outcome.Diagnostics
                  [ ServerDiagnostic.Failed(
                        "ApplyOps",
                        VerbRefusal.render
                            { Code = "present"
                              Detail = "notes/x.md" }
                    ) ]
                  "the guard's own refusal is the whole record of why"

              Expect.equal outcome.Store.Tree.Files world.Files "the state is the entry state"
              Expect.isEmpty world.Published "and the publish after the guard never performed"
          }

          test "a guard reads the PLANNED state, not the entry state" {
              // The entry world holds the shard; the plan deletes it; a check
              // that it exists, placed after the delete, refuses — because it
              // reads the state the ops before it produced.
              let world = seeded ()

              let outcome =
                  run
                      world
                      None
                      { Name = "after-delete"
                        Stages = [ Effect(ServerEffect.ApplyOps [ Delete "notes/x.md"; Check(Exists "notes/x.md") ]) ] }

              Expect.isFalse outcome.Committed "the guard saw the delete"
              Expect.isEmpty world.Invocations "nothing performed"

              Expect.equal
                  outcome.Diagnostics
                  [ ServerDiagnostic.Failed(
                        "ApplyOps",
                        VerbRefusal.render
                            { Code = "missing"
                              Detail = "notes/x.md" }
                    ) ]
                  "refused on the plan"
          }

          // ── W5: a typed refusal crosses as text ──

          test "W5 — a typed refusal crosses the guard as canonical JSON and parses back on the far side" {
              let refusal =
                  { Code = "bundle-ambiguous"
                    Detail = "x matches 2 bundles: x-a, x-b" }

              let world = seeded ()

              let outcome =
                  run
                      world
                      None
                      { Name = "ambiguous"
                        Stages = [ Effect(ServerEffect.ApplyOps [ Check(Refused refusal); Publish "origin" ]) ] }

              Expect.isFalse outcome.Committed "halted"
              Expect.isEmpty world.Invocations "nothing performed"

              match outcome.Diagnostics with
              | [ ServerDiagnostic.Failed("ApplyOps", reason) ] ->
                  Expect.equal
                      (VerbRefusal.parse reason)
                      (Some refusal)
                      "the domain's typed refusal, back from the text"
              | other -> failtestf "expected the guard's refusal, got %A" other
          }

          // ── F2: the plan is performed, and a part-way failure is positioned ──

          test "ADVERSARY F2 — a part-way performance failure reports the prefix that ran and where it stopped" {
              let world = seeded ()
              let outcome = run world (Some 2) archive

              Expect.isFalse outcome.Committed "rolled back"

              Expect.equal
                  outcome.Performed
                  [ "ApplyOps"; "ApplyOps" ]
                  "the two ops that ran before the refusal — the read and the write — and nothing else"

              Expect.equal
                  (outcome.Diagnostics |> List.last)
                  (ServerDiagnostic.PerformFailed("ApplyOps", "the world refused op 2"))
                  "a `PerformFailed` under the op's capability, with the performer's own reason"

              Expect.equal
                  (world.Files |> Map.toList)
                  [ "notes/archive/x.md", "archived"; "notes/x.md", "live" ]
                  "the world holds the residue: written, not yet deleted"

              Expect.isEmpty world.Published "the publish after the failure never ran"
              Expect.equal outcome.Store.Tree.Files (Map.ofList [ "notes/x.md", "live" ]) "the plan is discarded"
              Expect.equal (List.length world.Invocations) 3 "the performer was asked three times and stopped"
          }

          test "F-PERFORM — the performer is handed the planned state as of each op, and the last is the plan" {
              let world = seeded ()
              let outcome = run world None archive

              Expect.isTrue outcome.Committed "committed"

              Expect.equal
                  (world.Handed |> List.map (fun s -> s.Files |> Map.toList, s.Published))
                  [ [ "notes/x.md", "live" ], []
                    [ "notes/archive/x.md", "archived"; "notes/x.md", "live" ], []
                    [ "notes/archive/x.md", "archived" ], []
                    [ "notes/archive/x.md", "archived" ], [ "origin" ] ]
                  "each edit handed the state it produced"

              Expect.equal (List.last world.Handed) outcome.Store.Tree "and the last handed state IS the committed plan"
          }

          test "in memory, the same verb performs nothing outside — the apply is the effect" {
              let world = seeded ()

              let outcome =
                  Handler.run
                      witness
                      registry
                      DataFrame.noResolve
                      "verb"
                      archive
                      { Tree = { empty with Files = world.Files }
                        Bindings = () }

              Expect.isTrue outcome.Committed "committed"
              Expect.equal outcome.Performed [ "ApplyOps"; "ApplyOps"; "ApplyOps" ] "once per effect, at plan time"
              Expect.isEmpty world.Invocations "no performer was registered, so none ran"
              Expect.equal (world.Files |> Map.toList) [ "notes/x.md", "live" ] "the world is untouched"
              Expect.equal outcome.Store.Tree.Published [ "origin" ] "the tree — the in-memory plan — is the state"
          }

          test "an apply refusal halts while planning, so a performer registered or not sees nothing" {
              let world = World()

              let outcome =
                  run
                      world
                      None
                      { Name = "read-missing"
                        Stages = [ Effect(ServerEffect.ApplyOps [ Read "notes/x.md" ]) ] }

              Expect.isFalse outcome.Committed "the read of a missing shard refuses the apply"
              Expect.isEmpty world.Invocations "nothing performed"

              Expect.equal
                  (outcome.Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "no such file: notes/x.md"))
                  "the apply's own refusal"
          }

          // ── no dispatch axis: no binding channel ──

          test "a landing slot under a state-only witness is refused while planning — there is no binding channel" {
              let world = seeded ()
              let called = ref false

              let withHost =
                  ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
                  |> ServerEffectRegistry.register "fetch" (fun _ ->
                      called.Value <- true
                      Ok(JStr "ran"))

              let landing =
                  runIn
                      withHost
                      world
                      None
                      { Name = "lands"
                        Stages = [ Effect(ServerEffect.HostCall("fetch", JObj [], Some "result")) ] }
                      empty

              Expect.isFalse landing.Committed "refused"
              Expect.isFalse called.Value "the host function was never invoked"

              Expect.equal
                  landing.Diagnostics
                  [ ServerDiagnostic.Failed("host:fetch", Handler.NoBindingChannel) ]
                  "named, through the landing-slot halt"

              let query =
                  runIn
                      withHost
                      world
                      None
                      { Name = "reads"
                        Stages = [ Effect(ServerEffect.RunQuery("rows", Fuaran.Core.Ref "anything", [])) ] }
                      empty

              Expect.equal
                  query.Diagnostics
                  [ ServerDiagnostic.Failed("RunQuery", Handler.NoBindingChannel) ]
                  "a query has nowhere to land either, so it is refused before it reads"

              let noLanding =
                  runIn
                      withHost
                      world
                      None
                      { Name = "calls"
                        Stages = [ Effect(ServerEffect.HostCall("fetch", JObj [], None)) ] }
                      empty

              Expect.isTrue noLanding.Committed "a host call that lands nothing needs no channel"
              Expect.isTrue called.Value "and runs"
          }

          // ── Phase 1976: selection and bounded iteration on the op axis ──────

          test "a two-arm branch whose arms both continue, planned IN Program, ends in different outcomes" {
              let present = seeded ()
              let took = flowRun present migrate
              Expect.isTrue took.Committed "the true arm commits"

              Expect.equal
                  (present.Files |> Map.toList)
                  [ "notes/x.md", "migrated" ]
                  "the true arm migrated, and left no marker"

              Expect.equal
                  took.Performed
                  [ "ApplyOps"; "ApplyOps" ]
                  "one edit and the publish performed; the conditions never"

              Expect.equal present.Published [ "origin" ] "and the sequence continued past the branch"

              let absent = World()
              let created = flowRun absent migrate
              Expect.isTrue created.Committed "the false arm commits"

              Expect.equal
                  (absent.Files |> Map.toList)
                  [ "notes/created.marker", ""; "notes/x.md", "created" ]
                  "the false arm created, with the marker"

              Expect.equal created.Performed [ "ApplyOps"; "ApplyOps"; "ApplyOps" ] "two edits and the publish"
              Expect.notEqual took.Store.Tree created.Store.Tree "two arms, two outcomes"

              Expect.isFalse
                  (absent.Invocations
                   |> List.exists (fun op -> op.Contains "Exists" || op.Contains "Missing"))
                  "no condition reached the performer"
          }

          test
              "a violated exit assertion refuses the effect after the arm planned, naming the assertion, with nothing performed" {
              let world = seeded ()

              // The true arm leaves the marker it must not: the exit assertion
              // fails after the true arm.
              let broken =
                  { Name = "broken"
                    Stages =
                      [ Effect(
                            ServerEffect.ApplyOps
                                [ Branch(
                                      Exists "notes/x.md",
                                      [ Write("notes/created.marker", "") ],
                                      [],
                                      Some(Missing "notes/created.marker")
                                  )
                                  Publish "origin" ]
                        ) ] }

              let outcome = flowRun world broken
              Expect.isFalse outcome.Committed "halted"
              Expect.isEmpty outcome.Performed "nothing performed"
              Expect.isEmpty world.Invocations "the performer was never asked"
              Expect.equal (world.Files |> Map.toList) [ "notes/x.md", "live" ] "the world is untouched"

              Expect.equal
                  outcome.Diagnostics
                  [ ServerDiagnostic.Failed(
                        "ApplyOps",
                        "the exit assertion did not hold after the true arm: "
                        + VerbRefusal.render
                            { Code = "present"
                              Detail = "notes/created.marker" }
                    ) ]
                  "the assertion's own refusal names it"

              // And the other way: an exit assertion that HOLDS after the
              // false arm is the same defect.
              let heldAfterFalse =
                  { Name = "held"
                    Stages =
                      [ Effect(
                            ServerEffect.ApplyOps
                                [ Branch(Missing "notes/x.md", [], [ Publish "origin" ], Some(Exists "notes/x.md")) ]
                        ) ] }

              let world' = seeded ()
              let outcome' = flowRun world' heldAfterFalse
              Expect.isFalse outcome'.Committed "halted"
              Expect.isEmpty world'.Published "the arm's publish was planned, never performed"

              Expect.equal
                  outcome'.Diagnostics
                  [ ServerDiagnostic.Failed("ApplyOps", "the exit assertion held after the false arm") ]
                  "named the other way"
          }

          test
              "a condition that refuses takes the false arm: on the op channel a typed refusal is the false value, not a halt" {
              let world = seeded ()

              let refusing =
                  { Name = "refusing"
                    Stages =
                      [ Effect(
                            ServerEffect.ApplyOps
                                [ Branch(
                                      Refused { Code = "undecided"; Detail = "x" },
                                      [ Write("notes/x.md", "true") ],
                                      [ Write("notes/x.md", "false") ],
                                      None
                                  ) ]
                        ) ] }

              let outcome = flowRun world refusing
              Expect.isTrue outcome.Committed "no halt"
              Expect.equal world.Files.["notes/x.md"] "false" "the false arm ran"
              Expect.isEmpty outcome.Diagnostics "and the refusal is not a diagnostic — it was the answer"
          }

          test
              "a bounded repeat plans its body that many times; an over-bound count is refused by the policy before anything performs" {
              let world = seeded ()

              let thrice =
                  { Name = "thrice"
                    Stages = [ Effect(ServerEffect.ApplyOps [ Times(3, [ Publish "origin" ]) ]) ] }

              let outcome = flowRun world thrice
              Expect.isTrue outcome.Committed "commits"
              Expect.equal world.Published [ "origin"; "origin"; "origin" ] "three publishes, in order"
              Expect.equal outcome.Performed [ "ApplyOps"; "ApplyOps"; "ApplyOps" ] "three staged edits"

              let world' = seeded ()

              let tooMany =
                  { Name = "too-many"
                    Stages = [ Effect(ServerEffect.ApplyOps [ Times(4, [ Publish "origin" ]) ]) ] }

              let refused = flowRun world' tooMany
              Expect.isFalse refused.Committed "refused"
              Expect.isEmpty refused.Performed "nothing performed"
              Expect.isEmpty world'.Invocations "the performer was never asked"
              Expect.isEmpty world'.Published "nothing published"

              Expect.equal
                  (refused.Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "argument-not-allowed:count"))
                  "refused while planning, naming the argument the host bounded — the count — and never the value"
          }

          test "the demanded document carries BOTH arms' reach, and the policy binds the untaken arm" {
              let projection = ServerDemanded.ofHandler witness migrate

              let tier =
                  match projection.Server with
                  | Some tier -> tier
                  | None -> failtest "the walk ran, so the document must carry a server tier"

              let reach = tier.Reach |> List.map (fun r -> r.Argument, r.Name)
              Expect.contains reach ("path", "notes/created.marker") "the false arm's marker path is in the document"
              Expect.contains reach ("path", "notes/x.md") "and the shared path"
              Expect.contains reach ("target", "origin") "and the publish target after the branch"

              // An arm the run would NOT take still reaches: under the archive
              // policy, which does not name the marker path, the branch is
              // refused even though the true arm — which never touches the
              // marker — is the one that would run.
              let present = seeded ()
              let outcome = run present None migrate
              Expect.isFalse outcome.Committed "refused by the policy: the untaken arm reaches an off-list path"
              Expect.isEmpty present.Invocations "before anything performed"
              Expect.equal (present.Files |> Map.toList) [ "notes/x.md", "live" ] "the world is untouched"

              Expect.equal
                  (outcome.Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "argument-not-allowed:path"))
                  "naming the argument"
          } ]
