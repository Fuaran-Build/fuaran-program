module Fuaran.Program.Tests.ToyWireConformanceTests

// ─── The program wire codec, certified at the toy witness (fuaran#2017) ──────
//
// `ProgramWire` and `HandlerWire` are GENERIC over a witness, and until this
// suite the only evidence that they certify against the conformance corpus was
// gathered at the UI tier's witness — which is to say, the evidence lived
// beside the one domain the codecs were first written for. The specification
// now carries its codec families at a second SUBJECT (§10.7): the same handler,
// server-effect, client-effect and outcome documents with the toy witness's
// vocabulary in their referenced positions. This suite runs the generic codecs
// at the toy witness against every vector the manifest assigns to that
// subject, and against the vocabulary-free documents a host of either subject
// certifies as they stand.
//
// The corpus is the oracle, not this suite. Every assertion is driven by
// `manifest.json` — which vectors exist, which subject each is at, which
// document each is, what a reject vector must be refused FOR, and what derived
// classification a handler vector carries. Nothing enumerates a fixture by
// hand, because a hand-kept list is exactly how a corpus and a host quietly
// stop describing the same thing.
//
// The same five properties the referenced subject's suite asserts:
//
//   1. every `round-trip` vector decodes and re-encodes BYTE-IDENTICALLY;
//   2. every `reject` vector is refused FOR THE CLASS the manifest names;
//   3. every derived value a vector declares — `replaySafety` (§7.4) and
//      `replayReasons` (§7.5) — is RECOMPUTED from the decoded document;
//   4. the number of vectors run equals the number the manifest enumerates
//      for THIS subject (§10.1 point 6, §10.7);
//   5. a mutated fixture turns the harness red.
//
// ── The witness ──────────────────────────────────────────────────────────────
// `ToyDomain.witness` is left exactly as it is: its `Encode` members are the
// shapes the core hashes, and other suites pin those digests. What the codecs
// need is a witness whose action, op and effect codecs are the toy's WIRE
// codec (`ToyWire`), so the variant is composed here, member by member, and
// every other member — the views, the store, the reserved namespace — is the
// toy's own. The replay classification therefore runs over the toy's real
// views, which is the whole of what a toy-subject certification shows.

open System
open System.IO
open System.Security.Cryptography
open FSharp.Reflection
open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain

/// The toy witness with the toy's wire codec in its action, op and effect
/// positions.
///
/// The effect decoder renders the parsed document back in its PARSED member
/// order before handing it to `ToyWire.decodeEffect`, which reads the members
/// in the order the toy's emitter writes them: §5.2's envelope is
/// declaration-ordered, and a canonical (Ordinal) rendering here would decide
/// the order for the decoder rather than letting it check one.
let private wireWitness: FullWitness<ToyNode, ToyAction, ToyExpr, ToyStore, ToyOp, ToyEffect> =
    let toy = ToyDomain.witness

    { toy with
        State =
            { toy.State with
                Stream =
                    { toy.State.Stream with
                        Encode = fun op -> Canon.render (ToyWire.encodeOp op)
                        Decode = ToyWire.parseWith ToyWire.decodeOp } }
        Dispatch =
            { toy.Dispatch with
                Action =
                    { toy.Dispatch.Action with
                        Encode = ToyWire.encodeAction
                        Decode = ToyWire.decodeAction }
                Effect =
                    { toy.Dispatch.Effect with
                        Encode = ToyWire.encodeEffect
                        Decode = fun value -> ToyWire.decodeEffect (Canon.renderOrdered value) } } }

/// The subject this suite certifies at, as the manifest spells it.
[<Literal>]
let private ToySubject = "toy"

/// One vector, as the manifest declares it.
type private Vector =
    {
        Id: string
        Kind: string
        Document: string
        File: string
        Sha256: string
        /// `None` at the referenced subject, which is what a vector naming no
        /// subject is at (§10.7).
        Subject: string option
        Reject: string option
        ReplaySafety: string option
        ReplayReasons: (int * string) list option
    }

let private str (name: string) (value: JVal) : string =
    match ProgramWire.tryString name value with
    | Some s -> s
    | None -> failwithf "manifest vector has no '%s'" name

/// The declared reasons of one vector, in order. A malformed entry FAILS rather
/// than being dropped: a reason this reader could not parse would otherwise
/// shrink the expectation it is supposed to raise.
let private reasonsOf (entry: JVal) : (int * string) list option =
    match ProgramWire.tryMember "replayReasons" entry with
    | None -> None
    | Some(JArr items) ->
        items
        |> List.map (fun item ->
            match ProgramWire.tryMember "stage" item, ProgramWire.tryString "defect" item with
            | Some(JInt stage), Some defect -> stage, defect
            | _ -> failwith "a manifest reason is not a stage ordinal and a defect token")
        |> Some
    | Some _ -> failwith "a manifest vector's replayReasons is not an array"

let private manifest: Lazy<JVal> =
    lazy
        (let path = Path.Combine(ToyCorpus.fixturesRoot, "manifest.json")

         if not (File.Exists path) then
             failwithf
                 "the conformance corpus is not present at '%s'. It is a sibling clone and a BUILD INPUT to this \
                  gate, not an optional extra — clone it beside this repository, or point FUARAN_PROGRAM_SPEC at \
                  it. This suite fails rather than skipping: a conformance check that passes when its oracle is \
                  missing is worse than no check."
                 ToyCorpus.fixturesRoot

         match Json.parse (File.ReadAllText path) with
         | Ok value -> value
         | Error message -> failwithf "the corpus manifest does not parse: %s" message)

/// Every vector the manifest enumerates, at every subject.
let private everyVector () : Vector list =
    match ProgramWire.tryMember "vectors" manifest.Value with
    | Some(JArr entries) ->
        entries
        |> List.map (fun entry ->
            { Id = str "id" entry
              Kind = str "kind" entry
              Document = str "document" entry
              File = str "file" entry
              Sha256 = str "sha256" entry
              Subject = ProgramWire.tryString "subject" entry
              Reject = ProgramWire.tryString "reject" entry
              ReplaySafety = ProgramWire.tryString "replaySafety" entry
              ReplayReasons = reasonsOf entry })
    | _ -> failwith "the corpus manifest declares no vector array"

/// §10.7 — the documents carrying no referenced vocabulary, which a toy-subject
/// host certifies from the referenced subject's vectors as they stand. Absent
/// FAILS: without it this suite could not compute what it owes the manifest.
let private vocabularyFree () : string list =
    match ProgramWire.tryMember "vocabularyFreeDocuments" manifest.Value with
    | Some(JArr items) ->
        items
        |> List.map (fun item ->
            match item with
            | JStr document -> document
            | _ -> failwith "vocabularyFreeDocuments carries a non-string")
    | _ -> failwith "the corpus manifest declares no vocabularyFreeDocuments list (§10.7)"

/// What a toy-subject host runs, by §10.7's rule: every vector at the toy
/// subject, and every referenced-subject vector whose DOCUMENT is
/// vocabulary-free. Selected by document, never by family — the cross-layer
/// family's reference vectors are vocabulary-free and its handler vector is not.
let private selectForToy (free: string list) (vectors: Vector list) : Vector list =
    vectors
    |> List.filter (fun v ->
        v.Subject = Some ToySubject
        || (v.Subject.IsNone && List.contains v.Document free))

let private read (vector: Vector) : string =
    File.ReadAllText(Path.Combine(ToyCorpus.fixturesRoot, vector.File))

let private digestOf (path: string) : string =
    SHA256.HashData(File.ReadAllBytes path)
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

/// Decode a document of the named kind with the GENERIC codecs at the toy
/// witness, and re-encode it. `Ok` carries the bytes a conformant host emits;
/// `Error` carries the refusal class. The dispatch is on the vector's
/// DOCUMENT, never on its family.
let private roundTrip (document: string) (bytes: string) : Result<string, WireRefusal> =
    match document with
    | "client-effect" ->
        // §5.2's envelope exception: the bytes come from the toy's emitter,
        // not from the canonical renderer.
        ProgramWire.parseDocument bytes
        |> Result.bind (ProgramWire.decodeClientEffect wireWitness)
        |> Result.map (ProgramWire.encodeClientEffect wireWitness)
    | _ ->
        ProgramWire.parseDocument bytes
        |> Result.bind (fun value ->
            match document with
            | "handler" ->
                HandlerWire.decodeHandlerJson wireWitness value
                |> Result.bind (HandlerWire.encodeHandlerJson wireWitness)
            | "server-effect" ->
                HandlerWire.decodeEffect wireWitness value
                |> Result.bind (HandlerWire.encodeEffect wireWitness)
            | "outcome" ->
                HandlerWire.decodeReportJson wireWitness value
                |> Result.bind (HandlerWire.encodeReportJson wireWitness)
            | "invocation" -> ProgramWire.decodeInvocation value |> Result.map ProgramWire.encodeInvocation
            | "logic-tree-ref" ->
                ProgramWire.decodeLogicTreeRef value
                |> Result.map ProgramWire.encodeLogicTreeRef
            | other -> failwithf "the manifest names document kind '%s', which this host does not implement" other)
        |> Result.map ProgramWire.render

let private decodeHandler (bytes: string) : Result<Handler<ToyAction, ToyOp>, WireRefusal> =
    ProgramWire.parseDocument bytes
    |> Result.bind (HandlerWire.decodeHandlerJson wireWitness)

/// The reasons this host derives for a handler, in the manifest's spelling.
let private derivedReasons (handler: Handler<ToyAction, ToyOp>) : (int * string) list =
    HandlerWire.replayReasons wireWitness handler
    |> List.map (fun reason -> reason.Stage, ProgramWire.replayDefectTag reason.Defect)

/// The verdict a round-trip harness reaches on one vector's bytes: green only
/// when they re-encode to themselves. A pure function so its ability to go red
/// is demonstrated against mutated bytes rather than trusted.
let private certifyRoundTrip (document: string) (bytes: string) : Result<unit, string> =
    match roundTrip document bytes with
    | Error refusal -> Error(sprintf "refused (%s: %s)" refusal.Class refusal.Detail)
    | Ok emitted when emitted = bytes -> Ok()
    | Ok emitted -> Error(sprintf "re-encodes to %s" emitted)

/// Every token `ReplayDefect` spells, enumerated FROM THE TYPE, so a seventh
/// arm arrives with its coverage or turns the pin red.
let private allDefectTokens: string list =
    FSharpType.GetUnionCases typeof<ReplayDefect>
    |> Array.map (fun case ->
        FSharpValue.MakeUnion(case, [||]) :?> ReplayDefect
        |> ProgramWire.replayDefectTag)
    |> Array.toList

/// The one arm NO value at the toy witness reaches, and why it is excused here
/// rather than covered: `unencodable-op` fires when a reader cannot put an op
/// back on the wire, and the toy's one op encodes through the canonical
/// renderer from two strings, which always produces a document. The referenced
/// subject's suite reaches it from a UI op that does not render; the toy has no
/// such op, and a witness rigged to fail its own encoder would be a case built
/// to pass rather than a toy value.
let private unreachableAtToy: string list = [ "unencodable-op" ]

/// The arms a covered-set leaves unaccounted for, beside the excused one.
let private uncoveredArms (covered: Set<string>) : string list =
    allDefectTokens
    |> List.filter (fun token -> not (covered.Contains token) && not (List.contains token unreachableAtToy))

/// Arms reachable at the toy witness only from a value a host built itself.
///
/// `relative-addressing`: a `Relabel` whose target is EMPTY names no node, so
/// the walk cannot read an absolute address off it. No toy DOCUMENT carries one
/// — the toy decoder refuses a target that is not an identifier, before
/// anything is classified (§10.7) — so the corpus cannot discriminate this arm
/// at this subject and the case is built here instead.
let private hostConstructedCases: (string * Handler<ToyAction, ToyOp>) list =
    [ "relative-addressing",
      { Name = "title.blank"
        Stages = [ Effect(ServerEffect.ApplyOps [ Relabel("", "Blank") ]) ] } ]

[<Tests>]
let tests =
    testList
        "fuaran#2017 - the program wire codec certified at the toy witness"
        [ test "the corpus is present and assigns vectors to the toy subject" {
              Expect.isTrue (Directory.Exists ToyCorpus.fixturesRoot) $"the corpus is at {ToyCorpus.fixturesRoot}"

              let all = everyVector ()
              let free = vocabularyFree ()
              let mine = selectForToy free all

              Expect.isNonEmpty free "the manifest names the vocabulary-free documents"
              Expect.isNonEmpty (all |> List.filter (fun v -> v.Subject = Some ToySubject)) "…and toy vectors"

              // A floor, so a manifest that lost most of the toy families reads
              // as a failure rather than as a smaller success.
              Expect.isGreaterThanOrEqual mine.Length 45 "a toy-subject host runs at least 45 vectors"

              // Every document kind a toy-subject host reads is exercised, both
              // ways round: one it decodes, one it refuses.
              for document in [ "handler"; "server-effect"; "client-effect"; "outcome" ] @ free do
                  for kind in [ "round-trip"; "reject" ] do
                      Expect.isTrue
                          (mine |> List.exists (fun v -> v.Document = document && v.Kind = kind))
                          $"the toy subject's run carries a {kind} {document} vector"

              // The one subject a vector may name is this one. A subject this
              // suite did not recognise would be a set of vectors it skipped
              // while reporting green.
              let subjects = all |> List.choose _.Subject |> List.distinct
              Expect.equal subjects [ ToySubject ] "the only subject a vector names is the toy"
          }

          test "a vocabulary-free document carries no toy copy" {
              // §10.7: a document on the list is certified as it stands, so a
              // toy vector of one would be a duplicate the two hosts' counts
              // disagree about.
              let free = vocabularyFree ()

              let duplicates =
                  everyVector ()
                  |> List.filter (fun v -> v.Subject.IsSome && List.contains v.Document free)
                  |> List.map _.Id

              Expect.isEmpty duplicates "no toy vector is of a vocabulary-free document"
          }

          test "every vector's file is the one the manifest digests" {
              // The harness's own guard against a fixture changed in place: a
              // round-trip vector whose bytes moved could still re-encode to
              // itself if the edit was canonical, and only the digest sees it.
              let mine = selectForToy (vocabularyFree ()) (everyVector ())

              let ran =
                  mine
                  |> List.map (fun v ->
                      Expect.equal
                          (digestOf (Path.Combine(ToyCorpus.fixturesRoot, v.File)))
                          v.Sha256
                          $"{v.Id} hashes to its manifest digest"

                      v.Id)

              Expect.equal ran.Length mine.Length "every vector of the toy subject's run was digested"
          }

          test "every round-trip vector re-encodes byte-identically" {
              let roundTrips =
                  selectForToy (vocabularyFree ()) (everyVector ())
                  |> List.filter (fun v -> v.Kind = "round-trip")

              let ran =
                  roundTrips
                  |> List.map (fun v ->
                      match certifyRoundTrip v.Document (read v) with
                      | Ok() -> v.Id
                      | Error why -> failtestf "%s %s" v.Id why)

              Expect.equal ran.Length roundTrips.Length "every enumerated round-trip vector was run"
              Expect.isNonEmpty ran "…and there were some to run"
          }

          test "every reject vector is refused for the class the manifest names" {
              let rejects =
                  selectForToy (vocabularyFree ()) (everyVector ())
                  |> List.filter (fun v -> v.Kind = "reject")

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
                          // The class, not merely the fact of refusal: a reader
                          // broken in a convenient way must not certify.
                          Expect.equal refusal.Class expected $"{v.Id} is refused for the right class"
                          v.Id)

              Expect.equal ran.Length rejects.Length "every enumerated reject vector was run"
              Expect.isNonEmpty ran "…and there were some to run"
          }

          test "every handler vector's declared replay safety is reproduced" {
              let expectations =
                  selectForToy (vocabularyFree ()) (everyVector ())
                  |> List.filter (fun v -> v.ReplaySafety.IsSome)

              let ran =
                  expectations
                  |> List.map (fun v ->
                      match decodeHandler (read v) with
                      | Error refusal -> failtestf "%s was refused (%s)" v.Id refusal.Class
                      | Ok handler ->
                          // RECOMPUTED, over the toy's own views: a value read
                          // back off the manifest certifies nothing.
                          let derived =
                              ProgramWire.replaySafetyTag (HandlerWire.replaySafety wireWitness handler)

                          Expect.equal derived v.ReplaySafety.Value $"{v.Id} classifies as declared"
                          v.Id)

              Expect.equal ran.Length expectations.Length "every declared classification was recomputed"

              // All three values appear, so a classifier returning one constant
              // could not pass.
              let distinct =
                  expectations |> List.choose _.ReplaySafety |> List.distinct |> List.sort

              Expect.equal distinct [ "safe"; "unknown"; "unsafe" ] "and the expectations span all three values"
          }

          test "every handler vector's declared replay reasons are reproduced" {
              let mine = selectForToy (vocabularyFree ()) (everyVector ())
              let expectations = mine |> List.filter (fun v -> v.ReplayReasons.IsSome)

              let ran =
                  expectations
                  |> List.map (fun v ->
                      match decodeHandler (read v) with
                      | Error refusal -> failtestf "%s was refused (%s)" v.Id refusal.Class
                      | Ok handler ->
                          // An ORDERED sequence: the ordinal is half a reason.
                          Expect.equal
                              (derivedReasons handler)
                              v.ReplayReasons.Value
                              $"{v.Id} reports the reasons it declares, in order"

                          v.Id)

              Expect.equal ran.Length expectations.Length "every declared reason list was recomputed"
              Expect.isNonEmpty ran "the manifest declares replay-reason expectations at this subject"

              let missing =
                  mine
                  |> List.filter (fun v -> v.ReplaySafety.IsSome && v.ReplayReasons.IsNone)
                  |> List.map _.Id

              Expect.isEmpty missing "every vector declaring a classification declares its reasons"
          }

          test "an arm reachable at the toy only from a host-built value is discriminated here" {
              for token, handler in hostConstructedCases do
                  Expect.equal
                      (derivedReasons handler)
                      [ 0, token ]
                      $"the host-constructed case for {token} reports exactly that reason"

                  Expect.equal
                      (ProgramWire.replaySafetyTag (HandlerWire.replaySafety wireWitness handler))
                      "unknown"
                      $"…and the verdict it carries is the one {token} forces"

              // …and it IS host-built only: the same op written as a document is
              // refused before anything is classified, so no corpus vector at
              // this subject could have carried it.
              let document =
                  """{"$type":"Handler","name":"title.blank","stages":[{"$type":"Effect","effect":{"$type":"ApplyOps","ops":[{"$type":"Relabel","label":"Blank","target":""}]}}]}"""

              match decodeHandler document with
              | Ok _ -> failtest "a Relabel with an empty target decoded; the toy's ops name their targets"
              | Error refusal ->
                  Expect.equal
                      refusal.Class
                      RefusalClass.MalformedReferencedValue
                      "an op that names no target does not decode under the toy's vocabulary"
          }

          test "every arm of the defect vocabulary is discriminated by something, or excused by name" {
              // DISCRIMINATED, not merely present: a vector whose reasons name
              // two tokens covers neither.
              let discriminatedByCorpus =
                  selectForToy (vocabularyFree ()) (everyVector ())
                  |> List.choose _.ReplayReasons
                  |> List.choose (fun reasons ->
                      match reasons |> List.map snd |> List.distinct with
                      | [ single ] -> Some single
                      | _ -> None)
                  |> Set.ofList

              let discriminatedByHost = hostConstructedCases |> List.map fst |> Set.ofList
              let covered = Set.union discriminatedByCorpus discriminatedByHost

              Expect.isEmpty
                  (uncoveredArms covered)
                  "every ReplayDefect arm the toy can reach has an expectation that fails when it alone is mis-classified"

              // The asymmetries §10.7 records, checked rather than commented: no
              // toy DOCUMENT reaches either of these, so finding one covered by
              // the corpus would mean the toy or the specification had moved.
              Expect.isFalse
                  (discriminatedByCorpus.Contains "relative-addressing")
                  "no toy vector discriminates relative-addressing"

              Expect.isFalse
                  (discriminatedByCorpus.Contains "unencodable-op")
                  "no toy vector discriminates unencodable-op"

              // The excuse is a closed list, not a wildcard: it names an arm the
              // DU still has.
              for token in unreachableAtToy do
                  Expect.contains allDefectTokens token $"the excused arm {token} is an arm of the vocabulary"

              // …and the pin can report a gap: hiding a covered arm makes it
              // name that arm, and only that arm.
              for token in covered do
                  Expect.equal
                      (uncoveredArms (Set.remove token covered))
                      [ token ]
                      $"hiding {token} makes the pin name it, and name only it"
          }

          test "the harness can go red: a mutated fixture is not accepted" {
              // Property 5. The mutation happens in memory; the committed corpus
              // is never touched. The subject is chosen FROM THE MANIFEST — the
              // first toy round-trip handler with stages — so the case cannot
              // outlive the vector it names.
              let subject =
                  everyVector ()
                  |> List.find (fun v ->
                      v.Subject = Some ToySubject
                      && v.Kind = "round-trip"
                      && v.Document = "handler"
                      && (read v).Contains "\"stages\":[{")

              let committed = read subject

              let name =
                  match ProgramWire.parseDocument committed |> Result.map (ProgramWire.tryString "name") with
                  | Ok(Some name) -> name
                  | _ -> failtestf "%s has no name to mutate" subject.Id

              // (a) a canonical edit: it still re-encodes to ITSELF, so only the
              // comparison with the committed bytes and the digest can see it.
              let renamed = committed.Replace("\"" + name + "\"", "\"" + name + "-mutated\"")
              Expect.notEqual renamed committed "the mutation actually changed the bytes"

              match roundTrip subject.Document renamed with
              | Error _ -> () // refused outright is also red
              | Ok emitted ->
                  Expect.notEqual emitted committed "a mutated fixture does not re-encode to the committed bytes"

              Expect.notEqual
                  (SHA256.HashData(Text.Encoding.UTF8.GetBytes renamed)
                   |> Convert.ToHexString
                   |> _.ToLowerInvariant())
                  subject.Sha256
                  "…and does not hash to the manifest's digest"

              // (b) a non-canonical edit: the harness's own round-trip verdict
              // turns red on it.
              let spaced = committed.Replace("\"stages\":[", "\"stages\": [")
              Expect.notEqual spaced committed "the second mutation actually changed the bytes"

              Expect.isError
                  (certifyRoundTrip subject.Document spaced)
                  "a fixture that is not in canonical form fails the round trip"

              Expect.isOk (certifyRoundTrip subject.Document committed) "…where the committed one passes"

              // (c) a corrupted EXPECTATION: a declared reason moved to another
              // token is not what the recomputation produces.
              let classified =
                  everyVector ()
                  |> List.find (fun v ->
                      v.Subject = Some ToySubject
                      && (v.ReplayReasons |> Option.exists (List.isEmpty >> not)))

              match decodeHandler (read classified) with
              | Error refusal -> failtestf "%s was refused (%s)" classified.Id refusal.Class
              | Ok handler ->
                  let corrupted =
                      classified.ReplayReasons.Value
                      |> List.map (fun (stage, defect) ->
                          stage,
                          (if defect = "undecidable-action" then
                               "non-literal-write"
                           else
                               "undecidable-action"))

                  Expect.notEqual
                      (derivedReasons handler)
                      corrupted
                      $"a corrupted expectation on {classified.Id} is not reproduced"
          }

          test "the run is exactly what the manifest enumerates for the toy subject" {
              // The whole-run assertion, separate from the per-kind ones so a
              // vector of a kind this suite does not handle surfaces here rather
              // than vanishing between two filters.
              let all = everyVector ()
              let free = vocabularyFree ()
              let mine = selectForToy free all

              let handled =
                  mine
                  |> List.filter (fun v -> v.Kind = "round-trip" || v.Kind = "reject")
                  |> List.length

              Expect.equal handled mine.Length "every vector of the toy subject is of a kind this suite runs"

              // §10.7's count, computed the other way round: everything at the
              // toy subject, plus the referenced subject's vocabulary-free
              // vectors. Two derivations of one number, so a selection that
              // drifted from the rule disagrees with its own arithmetic.
              let atToy = all |> List.filter (fun v -> v.Subject = Some ToySubject) |> List.length

              let shared =
                  all
                  |> List.filter (fun v -> v.Subject.IsNone && List.contains v.Document free)
                  |> List.length

              Expect.equal mine.Length (atToy + shared) "the toy subject's run is its own vectors plus the shared ones"
              Expect.isGreaterThan shared 0 "…and the shared ones are some"
          } ]

/// A toy handler document whose one compute stage holds `action`.
let private handlerWith (action: string) : string =
    """{"$type":"Handler","name":"title.relabel","stages":[{"$type":"Compute","action":"""
    + action
    + "}]}"

let private targetedRing =
    """{"$type":"Ring","endpoint":"/handlers/relabel","targeted":true}"""

let private untargetedRing =
    """{"$type":"Ring","endpoint":"/handlers/relabel","targeted":false}"""

/// The placements a call can sit at inside one compute stage: on its own, and
/// one level inside each composition shape the toy has.
let private placements (ring: string) : (string * string) list =
    [ "the stage's own action", ring
      "a sequence member",
      """{"$type":"Seq","actions":[{"$type":"Put","key":"mode","value":"on"},"""
      + ring
      + "]}"
      "a selection's true arm",
      """{"$type":"Pick","entry":{"$type":"Read","key":"ready"},"whenFalse":{"$type":"Hush"},"whenTrue":"""
      + ring
      + "}"
      "a selection's false arm",
      """{"$type":"Pick","entry":{"$type":"Read","key":"ready"},"whenFalse":"""
      + ring
      + ""","whenTrue":{"$type":"Hush"}}"""
      "a repeat's body", """{"$type":"Times","body":""" + ring + ""","bound":2}"""
      "an iteration's body",
      """{"$type":"ForEach","body":"""
      + ring
      + ""","collection":["a"],"placeholder":"item"}""" ]

[<Tests>]
let resultTargetTests =
    testList
        "fuaran#2019 - a declared result target is refused at the codec at the toy subject"
        [ test "a targeted Ring is refused as tree-declared-result-target at every placement in a handler document" {
              for where, action in placements targetedRing do
                  match decodeHandler (handlerWith action) with
                  | Ok _ -> failtestf "a targeted Ring at %s was ACCEPTED" where
                  | Error refusal ->
                      Expect.equal
                          refusal.Class
                          RefusalClass.TreeDeclaredResultTarget
                          $"a targeted Ring at {where} is refused for the declared result target"
          }

          test "the same placements with an untargeted Ring decode" {
              for where, action in placements untargetedRing do
                  match decodeHandler (handlerWith action) with
                  | Ok _ -> ()
                  | Error refusal ->
                      failtestf "an untargeted Ring at %s was refused (%s: %s)" where refusal.Class refusal.Detail
          }

          test "a Ring carrying a landing slot as an extra member is refused for the member, not the target" {
              let action =
                  """{"$type":"Ring","endpoint":"/handlers/relabel","into":{"$type":"State","key":"result"},"targeted":true}"""

              match decodeHandler (handlerWith action) with
              | Ok _ -> failtest "a Ring with an undeclared member was ACCEPTED"
              | Error refusal ->
                  Expect.equal
                      refusal.Class
                      RefusalClass.UndeclaredMember
                      "§2.9 refuses the undeclared member before any reading of the action"
          }

          test "the refusal is read through the witness's view, so a view that declares no target accepts" {
              // The falsifier: the rule must come from the subject's declared
              // reading and from nothing the codec spells. A witness whose view
              // says the targeted Ring declares no target makes the same bytes
              // decode, which a rule keyed on the document's spelling would not.
              let view = wireWitness.Dispatch.Action.View

              let blind =
                  { wireWitness with
                      Dispatch =
                          { wireWitness.Dispatch with
                              Action =
                                  { wireWitness.Dispatch.Action with
                                      View =
                                          fun action ->
                                              match view action with
                                              | ActionView.Call(endpoint, _) -> ActionView.Call(endpoint, false)
                                              | other -> other } } }

              let decoded =
                  ProgramWire.parseDocument (handlerWith targetedRing)
                  |> Result.bind (HandlerWire.decodeHandlerJson blind)

              Expect.isOk decoded "with no declared reading, nothing is refused for a target"
          } ]
