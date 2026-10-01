/// The second witness, exercised against the four findings Phase 1967
/// answered (`DECISIONS.md` D19), one adversary per finding:
///
///   W3/W4  an off-list publish target is refused BEFORE anything performs,
///          and the demanded document names the paths and the target;
///   F1     a guard that does not hold halts the handler with NOTHING
///          performed, while a leaf's refusal does not halt;
///   F2     a part-way performance failure is reported with the prefix that
///          ran and the position it failed at;
///   W5     a typed refusal crosses the op channel as text and parses back.
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

let private args (note: string) : VerbStore = Map.ofList [ "args.note", JStr note ]

/// The verb: require a note, write a shard, delete the old one, publish.
let private archive: Handler<VerbAction, FileOp> =
    { Name = "archive"
      Stages =
        [ Compute(Need(NonEmpty "args.note"))
          Effect(ServerEffect.ApplyOps [ Read "notes/x.md" ])
          Effect(ServerEffect.ApplyOps [ Write("notes/archive/x.md", "archived"); Delete "notes/x.md" ])
          Effect(ServerEffect.ApplyOps [ Publish "origin" ]) ] }

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

let private run
    (world: World)
    (failAt: int option)
    (handler: Handler<VerbAction, FileOp>)
    (store: VerbStore)
    : HandlerOutcome<FileMap, VerbStore, FileOp, VerbEffect> =
    Handler.runWith
        witness
        registry
        (OpPerformance.performedBy (world.Performer failAt))
        DataFrame.noResolve
        "verb"
        handler
        { Tree = { empty with Files = world.Files }
          Bindings = store }

[<Tests>]
let tests =
    testList
        "Phase 1967 — the second witness: a verb over a file map"
        [ test "the verb runs: a guard that holds, a plan applied, the plan performed after it commits" {
              let world = seeded ()
              let outcome = run world None archive (args "archived by the test")

              Expect.isTrue outcome.Committed "the handler commits"

              Expect.equal
                  outcome.Performed
                  [ "ApplyOps"; "ApplyOps"; "ApplyOps"; "ApplyOps" ]
                  "four ops performed, each its own staged call, in plan order — nothing at plan time"

              Expect.equal
                  (world.Files |> Map.toList)
                  [ "notes/archive/x.md", "archived" ]
                  "the world was written and the old shard deleted — AFTER the plan committed"

              Expect.equal world.Published [ "origin" ] "and published"
              Expect.equal outcome.Store.Tree.Published [ "origin" ] "the committed plan agrees with the world"
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
                  "every path, the target, and the two destination classes — distinct and sorted"

              // The document's bytes, pinned: this projection is this
              // repository's own artefact (docs/generic-tier.md §6), and the
              // reach is what version 5 added to it.
              Expect.equal
                  (Demanded.encode projection)
                  ("{\"kind\":\"demanded\",\"version\":5,\"effects\":[],\"hostCalls\":[],"
                   + "\"stateNamespaces\":[{\"namespace\":\"args\",\"written\":false,\"read\":true}],"
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

              let outcome = run world None elsewhere (args "note")

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
                  Handler.runWith
                      witness
                      byClass
                      (OpPerformance.performedBy (world.Performer None))
                      DataFrame.noResolve
                      "verb"
                      { Name = "push"
                        Stages = [ Effect(ServerEffect.ApplyOps [ Write("notes/x.md", "x"); Publish "origin" ]) ] }
                      { Tree = empty; Bindings = Map.empty }

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
                  Handler.runWith
                      witness
                      bounded
                      (OpPerformance.performedBy (world.Performer None))
                      DataFrame.noResolve
                      "verb"
                      { Name = "write"
                        Stages = [ Effect(ServerEffect.ApplyOps [ Write("a.md", content) ]) ] }
                      { Tree = empty; Bindings = Map.empty }

              Expect.isTrue (outcome "small").Committed "under the ceiling"
              Expect.isFalse (outcome (String.replicate 64 "x")).Committed "over it"

              Expect.equal
                  ((outcome (String.replicate 64 "x")).Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "payload-over-ceiling:64"))
                  "naming the limit, never the size"
          }

          // ── F1: a guard that halts ──

          test "ADVERSARY F1 — a guard that does not hold halts the handler with nothing performed" {
              let world = seeded ()
              let outcome = run world None archive (args "")

              Expect.isFalse outcome.Committed "halted"
              Expect.isEmpty outcome.Performed "nothing performed"
              Expect.isEmpty world.Invocations "the performer was never asked"
              Expect.equal (world.Files |> Map.toList) [ "notes/x.md", "live" ] "the world is untouched"

              Expect.equal
                  outcome.Diagnostics
                  [ ServerDiagnostic.Bounded(BoundedDiagnostic.Refused("verb", "Need", "the guard did not hold")) ]
                  "the fold's own refusal is the whole record of why"

              Expect.equal outcome.Store.Bindings (args "") "the store is the entry store"
              Expect.equal outcome.Store.Tree.Files world.Files "and so is the tree"

              // The guard that halts and the leaf that only refuses, in one
              // handler: the leaf's refusal is a diagnostic the handler carries
              // on past, and the guard's is the one that stops it.
              let both =
                  { Name = "both"
                    Stages =
                      [ Compute(Steps [ Say ""; Say "going on"; Need(Lit(JBool false)); Say "never" ])
                        Effect(ServerEffect.ApplyOps [ Publish "origin" ]) ] }

              let outcome = run world None both Map.empty

              Expect.isFalse outcome.Committed "the guard halted the handler"
              Expect.isEmpty outcome.ClientEffects "rolled back — including the leaf that did emit"

              Expect.equal
                  outcome.Diagnostics
                  [ ServerDiagnostic.Bounded(BoundedDiagnostic.Refused("verb", "Say", "nothing to say"))
                    ServerDiagnostic.Bounded(BoundedDiagnostic.Refused("verb", "Need", "the guard did not hold")) ]
                  "the leaf's refusal did not halt; the guard's did; `Say never` never ran"

              Expect.isEmpty world.Published "and the publish after the guard never performed"
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
                        Stages =
                          [ Compute(Need(Refusing refusal))
                            Effect(ServerEffect.ApplyOps [ Publish "origin" ]) ] }
                      Map.empty

              Expect.isFalse outcome.Committed "halted"
              Expect.isEmpty world.Invocations "nothing performed"

              match outcome.Diagnostics with
              | [ ServerDiagnostic.Bounded(BoundedDiagnostic.Refused(_, "Need", reason)) ] ->
                  Expect.equal
                      (VerbRefusal.parse reason)
                      (Some refusal)
                      "the domain's typed refusal, back from the text"
              | other -> failtestf "expected the guard's refusal, got %A" other
          }

          // ── F2: the plan is performed, and a part-way failure is positioned ──

          test "ADVERSARY F2 — a part-way performance failure reports the prefix that ran and where it stopped" {
              let world = seeded ()
              let outcome = run world (Some 2) archive (args "note")

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
                        Bindings = args "note" }

              Expect.isTrue outcome.Committed "committed"
              Expect.equal outcome.Performed [ "ApplyOps"; "ApplyOps"; "ApplyOps" ] "once per effect, at plan time"
              Expect.isEmpty world.Invocations "no performer was registered, so none ran"
              Expect.equal (world.Files |> Map.toList) [ "notes/x.md", "live" ] "the world is untouched"
              Expect.equal outcome.Store.Tree.Published [ "origin" ] "the tree — the in-memory plan — is the state"
          }

          test "an apply refusal halts while planning, so a performer registered or not sees nothing" {
              let world = World()
              let outcome = run world None archive (args "note")

              Expect.isFalse outcome.Committed "the read of a missing shard refuses the apply"
              Expect.isEmpty world.Invocations "nothing performed"

              Expect.equal
                  (outcome.Diagnostics |> List.last)
                  (ServerDiagnostic.Failed("ApplyOps", "no such file: notes/x.md"))
                  "the apply's own refusal"
          } ]
