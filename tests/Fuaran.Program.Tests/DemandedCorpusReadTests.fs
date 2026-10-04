/// The demanded corpus, certified at its authority with no UI type in reach (fuaran#2012).
///
/// `conformance/demanded-effect-projection.json` pairs each demanded document
/// with what the pinned reader, `Demanded.decode`, makes of it — the corpus a
/// consumer's own reader is conformant against. Two of its documents are real
/// harvests of a UI program, which only the UI adapters can produce, so its
/// EMITTER moved with them to the UI tier's repository (fuaran#2012), which
/// writes the corpus and compares its own emission with a declared byte copy of
/// this file.
///
/// What stays here is the half that is about the CODEC, and it is the half that
/// makes the file evidence: every vector's recorded `read` is re-derived from
/// this repository's own `Demanded.decode` over the vector's document, rendered
/// exactly as the emitter renders it, and compared byte for byte. A codec change
/// that moves any reading turns this red here, where the codec is, rather than
/// in another repository after a release. The rendering below is the emitter's,
/// verbatim; the byte copy of this file that the emitter compares against is
/// what keeps the two renderings from drifting apart without a red somewhere.
module Fuaran.Program.Tests.DemandedCorpusReadTests

open System.IO
open System.Text.Json
open Expecto
open Fuaran.Program.Bounded

/// The committed corpus, resolved from this source file.
let private corpusPath =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "conformance", "demanded-effect-projection.json")
    |> Path.GetFullPath

// ── what the pinned reader makes of each (the emitter's rendering, verbatim) ──

let private esc (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

let private q (s: string) = "\"" + esc s + "\""
let private arr (xs: string list) = "[" + String.concat "," xs + "]"

let private reasons (rs: (int * string) list) =
    rs
    |> List.map (fun (stage, defect) -> "{\"stage\":" + string stage + ",\"defect\":" + q defect + "}")
    |> arr

/// The tier as the corpus carries it — every member the reader reads, so a
/// consumer whose reader dropped one disagrees with this corpus.
///
/// A walk that did NOT run is `"serverWalked":false` with no `server` member, rather than
/// `"server":null`. The corpus is read by consumers whose JSON model has no null — the wire this
/// whole family lives on has none either — so emitting one would make the corpus unreadable to
/// exactly the readers it exists to certify. The two facts stay distinguishable because the boolean
/// is always present.
let private renderTier (tier: ServerDemand option) =
    match tier with
    | None -> "\"serverWalked\":false"
    | Some t ->
        let fns =
            t.Functions
            |> List.map (fun f -> "{\"function\":" + q f.Function + ",\"capability\":" + q f.Capability + "}")
            |> arr

        let chans =
            t.Channels
            |> List.map (fun c -> "{\"channel\":" + q c.Channel + ",\"name\":" + q c.Name + "}")
            |> arr

        let reach =
            t.Reach
            |> List.map (fun r ->
                "{\"capability\":"
                + q r.Capability
                + ",\"argument\":"
                + q r.Argument
                + ",\"name\":"
                + q r.Name
                + "}")
            |> arr

        let replay =
            t.Replay
            |> List.map (fun p ->
                "{\"handler\":"
                + q p.Handler
                + ",\"safety\":"
                + q p.Safety
                + ",\"reasons\":"
                + reasons (p.Reasons |> List.map (fun r -> r.Stage, r.Defect))
                + "}")
            |> arr

        let undo =
            t.Undo
            |> List.map (fun p ->
                "{\"handler\":"
                + q p.Handler
                + ",\"undo\":"
                + q p.Undo
                + ",\"reasons\":"
                + reasons (p.Reasons |> List.map (fun r -> r.Stage, r.Defect))
                + "}")
            |> arr

        // The declared argument policy, rendered clause by clause. A consumer
        // whose reader silently dropped a bound would otherwise agree with this
        // corpus while reading "unconstrained" off a constrained document — the
        // one misreading this member makes dangerous rather than merely lossy.
        let constraints =
            t.Constraints
            |> List.map (fun c ->
                let clauses =
                    c.Clauses
                    |> List.map (fun clause ->
                        match clause with
                        | ServerConstraintClause.AllowList(argument, permitted) ->
                            "{\"clause\":\"allowList\",\"argument\":"
                            + q argument
                            + ",\"permitted\":"
                            + arr (permitted |> List.map q)
                            + "}"
                        | ServerConstraintClause.DenyList(argument, refused) ->
                            "{\"clause\":\"denyList\",\"argument\":"
                            + q argument
                            + ",\"refused\":"
                            + arr (refused |> List.map q)
                            + "}"
                        | ServerConstraintClause.AtMost(argument, limit) ->
                            "{\"clause\":\"atMost\",\"argument\":"
                            + q argument
                            + ",\"limit\":"
                            + string limit
                            + "}"
                        | ServerConstraintClause.Ceiling bytes ->
                            "{\"clause\":\"ceiling\",\"bytes\":" + string bytes + "}"
                        | ServerConstraintClause.Label label -> "{\"clause\":\"label\",\"label\":" + q label + "}")
                    |> arr

                "{\"capability\":" + q c.Capability + ",\"clauses\":" + clauses + "}")
            |> arr

        "\"serverWalked\":true,\"server\":{\"effects\":"
        + arr (t.Effects |> List.map q)
        + ",\"capabilities\":"
        + arr (t.Capabilities |> List.map q)
        + ",\"functions\":"
        + fns
        + ",\"channels\":"
        + chans
        + ",\"reach\":"
        + reach
        + ",\"replay\":"
        + replay
        + ",\"undo\":"
        + undo
        + ",\"constraints\":"
        + constraints
        + "}"

/// A document's reading, rendered as the corpus records it.
let private readingOf (document: string) : string =
    match Demanded.decode document with
    | Ok projection -> "{\"verdict\":\"ok\"," + renderTier projection.Server + "}"
    | Error failure ->
        "{\"verdict\":\"refused\",\"defect\":"
        + q (string failure.Defect)
        + ",\"field\":"
        + q failure.Field
        + "}"

/// Every vector whose recorded reading differs from this decoder's, as
/// `(id, recorded, derived)`.
let private misreadings (corpus: JsonDocument) : (string * string * string) list =
    [ for v in corpus.RootElement.GetProperty("vectors").EnumerateArray() do
          let id = v.GetProperty("id").GetString() |> string
          let document = v.GetProperty("document").GetString() |> string
          let recorded = v.GetProperty("read").GetRawText()
          let derived = readingOf document

          if recorded <> derived then
              yield id, recorded, derived ]

[<Tests>]
let tests =
    testList
        "the demanded corpus, read at its authority (fuaran#2012)"
        [ test "every recorded reading is this decoder's" {
              Expect.isTrue (File.Exists corpusPath) $"the corpus is committed at {corpusPath}"
              use corpus = JsonDocument.Parse(File.ReadAllText corpusPath)

              let count = corpus.RootElement.GetProperty("vectors").GetArrayLength()
              Expect.isGreaterThanOrEqual count 20 "the corpus carries its vectors"

              match misreadings corpus with
              | [] -> ()
              | (id, recorded, derived) :: _ ->
                  failtestf
                      "the corpus records a reading this decoder does not make — regenerate it with the emitter in the UI tier's repository, against this decoder's release:
  vector:   %s
  recorded: %s
  derived:  %s"
                      id
                      recorded
                      derived
          }

          test "the corpus declares the document this decoder reads" {
              use corpus = JsonDocument.Parse(File.ReadAllText corpusPath)
              let root = corpus.RootElement
              Expect.equal (root.GetProperty("corpus").GetString()) "demanded-effect-projection" "the corpus name"
              Expect.equal (root.GetProperty("documentKind").GetString()) Demanded.Kind "the document kind"

              Expect.equal
                  [ for v in root.GetProperty("decodableVersions").EnumerateArray() -> v.GetInt32() ]
                  Demanded.decodableVersions
                  "the versions the reader reads"
          }

          test "the richest vector is a real harvest carrying reach, replay and undo" {
              use corpus = JsonDocument.Parse(File.ReadAllText corpusPath)

              let harvested =
                  corpus.RootElement.GetProperty("vectors").EnumerateArray()
                  |> Seq.find (fun v -> v.GetProperty("id").GetString() = "harvest-full")
                  |> fun v -> v.GetProperty("document").GetString() |> string

              match Demanded.decode harvested with
              | Error failure -> failtestf "the harvest does not decode: %A" failure
              | Ok projection ->
                  match projection.Server with
                  | None -> failtest "the harvest walked the server tier"
                  | Some tier ->
                      Expect.isNonEmpty tier.Reach "its op names a node"
                      Expect.isNonEmpty tier.Replay "a replay posture"
                      Expect.isNonEmpty tier.Undo "an undo posture"
          }

          // The comparison, proven able to lose: a recorded reading moved by one
          // byte is caught and named by its vector.
          test "GO RED: a recorded reading this decoder does not make is caught, and named" {
              let text = File.ReadAllText corpusPath
              let honest = "\"id\":\"not-json\""
              Expect.stringContains text honest "the probe perturbs a vector the corpus declares"

              let perturbed =
                  text.Replace(
                      "\"read\":{\"verdict\":\"refused\",\"defect\":\"NotJson\"",
                      "\"read\":{\"verdict\":\"refused\",\"defect\":\"NotAnObject\""
                  )

              Expect.notEqual perturbed text "the perturbation moved a byte"
              use corpus = JsonDocument.Parse perturbed

              match misreadings corpus with
              | [ (id, _, _) ] -> Expect.equal id "not-json" "the perturbed vector, by name"
              | other -> failtestf "expected exactly the perturbed vector, got %A" other
          } ]
