module Fuaran.Program.Server.Tests.ProgramWireConformanceTests

// ─── Certification against the program wire conformance corpus ───────────────
//
// The corpus is the oracle, not this suite. Every assertion below is driven by
// `manifest.json`: which vectors exist, which document each is, what a reject
// vector must be refused FOR, and what derived classification a handler vector
// carries. Nothing here enumerates a fixture by hand, because a hand-kept list
// is exactly how a corpus and a host quietly stop describing the same thing.
//
// Four properties, and the last two are the ones that make the first two mean
// something:
//
//   1. every `round-trip` vector decodes and re-encodes BYTE-IDENTICALLY;
//   2. every `reject` vector is refused FOR THE CLASS the manifest names — a
//      refusal for some other reason is not a pass, because "my parser threw"
//      and "my reader applied the rule" are different facts;
//   3. the number of vectors this suite ran equals the number the manifest
//      enumerates — a harness that silently skipped a family reports the same
//      green as one that passed it;
//   4. a mutated fixture makes this harness go red. A conformance harness is
//      exactly the kind of code that passes by doing nothing, so its ability to
//      fail is asserted rather than assumed.
//
// ── The corpus is a BUILD INPUT, and its absence is a failure ────────────────
// The corpus lives in a sibling clone. A suite that skipped when it was missing
// would be the "passes by doing nothing" shape the corpus itself forbids, so an
// absent corpus fails loudly and names both the expected path and the override.

open System
open System.IO
open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server

/// Where the corpus is. Resolved from THIS source file rather than the working
/// directory, for the same reason the parity leg's fixture root is: a test's
/// cwd is whatever the runner chose, and a path that depends on it is a path
/// that works on one machine.
let private corpusRoot: string =
    match Environment.GetEnvironmentVariable "FUARAN_PROGRAM_SPEC" with
    | null
    | "" ->
        Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Fuaran-UI", "fuaran-program-spec", "wire-fixtures")
        |> Path.GetFullPath
    | declared -> Path.Combine(declared, "wire-fixtures") |> Path.GetFullPath

/// One vector, as the manifest declares it.
type private Vector =
    { Id: string
      Kind: string
      Document: string
      File: string
      Reject: string option
      ReplaySafety: string option }

let private str (name: string) (value: JVal) : string =
    match ProgramWire.tryString name value with
    | Some s -> s
    | None -> failwithf "manifest vector has no '%s'" name

let private vectors () : Vector list =
    let manifestPath = Path.Combine(corpusRoot, "manifest.json")

    if not (File.Exists manifestPath) then
        failwithf
            "the conformance corpus is not present at '%s'. It is a sibling clone and a BUILD INPUT to this gate, \
             not an optional extra — clone it beside this repository, or point FUARAN_PROGRAM_SPEC at it. \
             This suite fails rather than skipping, deliberately: a conformance check that passes when its \
             oracle is missing is worse than no check."
            corpusRoot

    match Json.parse (File.ReadAllText manifestPath) with
    | Error message -> failwithf "the corpus manifest does not parse: %s" message
    | Ok manifest ->
        match ProgramWire.tryMember "vectors" manifest with
        | Some(JArr entries) ->
            entries
            |> List.map (fun entry ->
                { Id = str "id" entry
                  Kind = str "kind" entry
                  Document = str "document" entry
                  File = str "file" entry
                  Reject = ProgramWire.tryString "reject" entry
                  ReplaySafety = ProgramWire.tryString "replaySafety" entry })
        | _ -> failwith "the corpus manifest declares no vector array"

/// Decode a document of the named kind and re-encode it. `Ok` carries the bytes
/// a conformant host emits; `Error` carries the refusal class.
///
/// The dispatch is on the vector's DOCUMENT, never on its family: `cross-layer`
/// deliberately carries a handler document, because a rule about a call action
/// can only be demonstrated by a document that contains one.
let private roundTrip (document: string) (bytes: string) : Result<string, WireRefusal> =
    match document with
    | "client-effect" ->
        // The one family whose bytes come from the SHIPPED emitter rather than
        // from the canonical renderer — which is the specification's enumerated
        // envelope exception, and the reason this arm looks different.
        ProgramWire.parseDocument bytes
        |> Result.bind ProgramWire.decodeClientEffect
        |> Result.map ProgramWire.encodeClientEffect
    | _ ->
        ProgramWire.parseDocument bytes
        |> Result.bind (fun value ->
            match document with
            | "handler" -> HandlerWire.decodeHandlerJson value |> Result.bind HandlerWire.encodeHandlerJson
            | "server-effect" -> HandlerWire.decodeEffect value |> Result.bind HandlerWire.encodeEffect
            | "invocation" -> ProgramWire.decodeInvocation value |> Result.map ProgramWire.encodeInvocation
            | "outcome" -> HandlerWire.decodeReportJson value |> Result.bind HandlerWire.encodeReportJson
            | "logic-tree-ref" ->
                ProgramWire.decodeLogicTreeRef value
                |> Result.map ProgramWire.encodeLogicTreeRef
            | other -> failwithf "the manifest names document kind '%s', which this host does not implement" other)
        |> Result.map ProgramWire.render

let private read (vector: Vector) : string =
    File.ReadAllText(Path.Combine(corpusRoot, vector.File))

[<Tests>]
let tests =
    let all = vectors ()

    testList
        "program wire conformance"
        [ test "the corpus is present and enumerates vectors" {
              Expect.isTrue (Directory.Exists corpusRoot) $"the corpus is at {corpusRoot}"
              Expect.isNonEmpty all "the manifest enumerates vectors"
          }

          test "every round-trip vector re-encodes byte-identically" {
              let ran =
                  all
                  |> List.filter (fun v -> v.Kind = "round-trip")
                  |> List.map (fun v ->
                      let committed = read v

                      match roundTrip v.Document committed with
                      | Error refusal -> failtestf "%s was refused (%s: %s)" v.Id refusal.Class refusal.Detail
                      | Ok emitted ->
                          Expect.equal emitted committed $"{v.Id} re-encodes to its committed bytes"
                          v.Id)

              // Property 3, on this half: the count is asserted rather than
              // hoped for, so a filter that matched nothing is a failure rather
              // than a silent success.
              Expect.equal
                  ran.Length
                  (all |> List.filter (fun v -> v.Kind = "round-trip") |> List.length)
                  "every enumerated round-trip vector was run"

              Expect.isNonEmpty ran "…and there were some to run"
          }

          test "every reject vector is refused for the class the manifest names" {
              let rejects = all |> List.filter (fun v -> v.Kind = "reject")

              let ran =
                  rejects
                  |> List.map (fun v ->
                      let expected =
                          match v.Reject with
                          | Some c -> c
                          | None -> failtestf "%s is a reject vector naming no class" v.Id

                      match roundTrip v.Document (read v) with
                      | Ok _ -> failtestf "%s was ACCEPTED; it must be refused for '%s'" v.Id expected
                      | Error refusal ->
                          // The class, not merely the fact of refusal. Refusing
                          // for the wrong reason would let this host certify by
                          // being broken in a convenient way.
                          Expect.equal refusal.Class expected $"{v.Id} is refused for the right class"
                          v.Id)

              Expect.equal ran.Length rejects.Length "every enumerated reject vector was run"
              Expect.isNonEmpty ran "…and there were some to run"
          }

          test "every handler vector's declared replay safety is reproduced" {
              let expectations = all |> List.filter (fun v -> v.ReplaySafety.IsSome)

              let ran =
                  expectations
                  |> List.map (fun v ->
                      match ProgramWire.parseDocument (read v) |> Result.bind HandlerWire.decodeHandlerJson with
                      | Error refusal -> failtestf "%s was refused (%s)" v.Id refusal.Class
                      | Ok handler ->
                          // RECOMPUTED from the decoded document. Reading the
                          // manifest's value back at it would certify nothing:
                          // a derived value nobody re-derives is a constant with
                          // a longer name.
                          let derived = ProgramWire.replaySafetyTag (HandlerWire.replaySafety handler)
                          Expect.equal derived v.ReplaySafety.Value $"{v.Id} classifies as declared"
                          v.Id)

              Expect.isNonEmpty ran "the manifest declares replay-safety expectations"

              // All three values appear, so a classifier that returned one
              // constant could not pass this suite.
              let distinct =
                  expectations |> List.choose _.ReplaySafety |> List.distinct |> List.sort

              Expect.equal distinct [ "safe"; "unknown"; "unsafe" ] "and the expectations span all three values"
          }

          test "the harness can go red: a mutated fixture is not accepted unchanged" {
              // Property 4, and the reason the three tests above mean anything.
              // The mutation happens in memory; the committed corpus is never
              // touched.
              let subject = all |> List.find (fun v -> v.Id = "handler/read-compute-write")

              let committed = read subject
              let mutated = committed.Replace("orders.refresh", "orders.refreshed")

              Expect.notEqual mutated committed "the mutation actually changed the bytes"

              match roundTrip subject.Document mutated with
              | Error _ -> () // refused outright is also red
              | Ok emitted ->
                  Expect.notEqual emitted committed "a mutated fixture does not re-encode to the committed bytes"
          }

          test "the manifest's vector count is what this suite ran in total" {
              // The whole-corpus assertion, separate from the per-kind ones
              // above so that a vector of a kind this suite does not handle
              // would surface here rather than vanish between two filters.
              let handled =
                  all
                  |> List.filter (fun v -> v.Kind = "round-trip" || v.Kind = "reject")
                  |> List.length

              Expect.equal handled all.Length "every enumerated vector is of a kind this suite runs"
          } ]
