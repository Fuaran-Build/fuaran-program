/// The boundary DECISIONS.md D18 draws, enforced by a test rather than asserted
/// by a document (Phase 1896): no core package — `Fuaran.Program.Bounded`,
/// `.Runtime`, `.Server` — references the UI tier, declared OR resolved.
///
/// Three readings, because a reference can arrive three ways:
///   * the project file declares one (a package or a project reference);
///   * a source names the tier (an `open`, or a fully qualified identifier —
///     the shape an `open`-only scan misses);
///   * the RESOLVED graph holds one that nothing declared — a transitive
///     arrival through another reference — which only the restore's
///     `project.assets.json` and the built assembly's own references show.
///
/// Each reading is proven able to fail on the UI adapter, which does reference
/// the tier: a scan that has never been seen to go red is a scan whose
/// mechanism nobody has verified.
///
/// Since Phase 1897 the adapter is two packages under `src/`, beside the core
/// (`Fuaran.Program.UI`, `Fuaran.Program.Server.UI`). They count as the UI
/// tier here too: a core package that referenced its own adapter would reach
/// the tier through it, and the adapter's names do not match `Fuaran.UI` by
/// spelling, so they are named explicitly.
module Fuaran.Program.Tests.CoreBoundaryTests

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Text.RegularExpressions
open Expecto

/// The repository root, found by walking up from this source file to the
/// solution, so the test reads the tree it was compiled from.
let private repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failwith "no Fuaran.Program.slnx above this source file"
        elif File.Exists(Path.Combine(dir.FullName, "Fuaran.Program.slnx")) then
            dir.FullName
        else
            up dir.Parent

    up (DirectoryInfo __SOURCE_DIRECTORY__)

let private corePackages =
    [ "Fuaran.Program.Bounded"; "Fuaran.Program.Runtime"; "Fuaran.Program.Server" ]

let private coreDir (name: string) = Path.Combine(repoRoot, "src", name)

let private adapterDir = Path.Combine(repoRoot, "src", "Fuaran.Program.UI")

let private serverAdapterDir =
    Path.Combine(repoRoot, "src", "Fuaran.Program.Server.UI")

/// A UI-tier package id or namespace: `Fuaran.UI` itself or anything under it,
/// or one of the two adapter packages that instantiate the core at it.
let private uiName =
    Regex(@"\bFuaran\.(Program\.(Server\.)?)?UI(\.|\b|"")", RegexOptions.Compiled)

/// The same test over a resolved library or assembly name.
let private isUiTier (name: string) =
    name.StartsWith "Fuaran.UI"
    || name.StartsWith "Fuaran.Program.UI"
    || name.StartsWith "Fuaran.Program.Server.UI"

/// Declared references in a project file: a `PackageReference` or a
/// `ProjectReference` naming the tier.
let private declaredIn (fsproj: string) : string list =
    File.ReadAllLines fsproj
    |> Array.filter (fun line ->
        (line.Contains "PackageReference" || line.Contains "ProjectReference")
        && uiName.IsMatch line)
    |> Array.map (fun line -> sprintf "%s: %s" (Path.GetFileName fsproj) (line.Trim()))
    |> List.ofArray

/// Source lines naming the tier, with line comments stripped first — an
/// `open` and a fully qualified identifier are both caught; a sentence in a
/// comment is not a reference.
let private namedIn (dir: string) : string list =
    Directory.GetFiles(dir, "*.fs")
    |> Array.collect (fun path ->
        File.ReadAllLines path
        |> Array.indexed
        |> Array.choose (fun (i, line) ->
            let code =
                match line.IndexOf "//" with
                | -1 -> line
                | at -> line.Substring(0, at)

            if uiName.IsMatch code then
                Some(sprintf "%s:%d %s" (Path.GetFileName path) (i + 1) (line.Trim()))
            else
                None))
    |> List.ofArray

/// `namedIn` over named files of one directory rather than all of it.
let private namedInFiles (dir: string) (files: string list) : string list =
    files
    |> List.collect (fun file ->
        File.ReadAllLines(Path.Combine(dir, file))
        |> Array.indexed
        |> Array.choose (fun (i, line) ->
            let code =
                match line.IndexOf "//" with
                | -1 -> line
                | at -> line.Substring(0, at)

            if uiName.IsMatch code then
                Some(sprintf "%s:%d %s" file (i + 1) (line.Trim()))
            else
                None)
        |> List.ofArray)

/// Libraries the restore RESOLVED into the project's graph, read from its
/// `obj/project.assets.json` — every target's library keys, so a transitive
/// arrival is caught as surely as a declared one.
let private resolvedIn (dir: string) : Result<string list, string> =
    let assets = Path.Combine(dir, "obj", "project.assets.json")

    if not (File.Exists assets) then
        Error(sprintf "no restore graph at %s — build first; an absent graph is not a clean one" assets)
    else
        use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText assets)

        doc.RootElement.GetProperty("targets").EnumerateObject()
        |> Seq.collect (fun target -> target.Value.EnumerateObject() |> Seq.map _.Name)
        |> Seq.filter isUiTier
        |> Seq.distinct
        |> List.ofSeq
        |> Ok

/// Assemblies the BUILT assembly references, read from its own metadata.
let private referencedBy (dir: string) (assembly: string) : Result<string list, string> =
    let dll =
        Directory.GetFiles(Path.Combine(dir, "bin"), assembly + ".dll", SearchOption.AllDirectories)
        |> Array.sortByDescending File.GetLastWriteTimeUtc
        |> Array.tryHead

    match dll with
    | None -> Error(sprintf "no built %s.dll under %s — build first" assembly dir)
    | Some path ->
        use stream = File.OpenRead path
        use reader = new PEReader(stream)
        let metadata = reader.GetMetadataReader()

        metadata.AssemblyReferences
        |> Seq.map (fun handle -> metadata.GetString(metadata.GetAssemblyReference(handle).Name))
        |> Seq.filter isUiTier
        |> List.ofSeq
        |> Ok

let private expectNone (label: string) (hits: string list) =
    Expect.isEmpty hits (sprintf "%s reaches the UI tier: %s" label (String.Join("; ", hits)))

let private expectRead (label: string) (result: Result<string list, string>) : string list =
    match result with
    | Ok hits -> hits
    | Error why -> failtestf "%s: %s" label why

[<Tests>]
let tests =
    testList
        "the core references no UI-tier package (D18)"
        [ for package in corePackages do
              test $"{package}: no declared reference" {
                  expectNone package (declaredIn (Path.Combine(coreDir package, package + ".fsproj")))
              }

              test $"{package}: no source names the tier" {
                  let sources = Directory.GetFiles(coreDir package, "*.fs")
                  Expect.isNonEmpty sources "the package has sources to scan"
                  expectNone package (namedIn (coreDir package))
              }

              test $"{package}: nothing reaches it transitively" {
                  expectNone $"{package} (resolved graph)" (expectRead package (resolvedIn (coreDir package)))

                  expectNone $"{package} (built assembly)" (expectRead package (referencedBy (coreDir package) package))
              }

          // fuaran#2011 — the toy family's HARNESS reaches no UI-tier type either.
          // Named file by file, because this project's other sources name the
          // tier on purpose (the probes above spell it), and the claim is about
          // the files the conformance leg, the Fable leg and the proof host run.
          test "the driver-semantics-toy harness: no source names the tier" {
              let harness =
                  [ "ToyDomain.fs"
                    "ToyWire.fs"
                    "ToyScenarios.fs"
                    "ToyServerPlacement.fs"
                    "ToySeeds.fs"
                    "ToyCorpus.fs"
                    "ToyFamilyTests.fs" ]

              for file in harness do
                  Expect.isTrue (File.Exists(Path.Combine(__SOURCE_DIRECTORY__, file))) (file + " is where it is named")

              expectNone "the toy harness" (namedInFiles __SOURCE_DIRECTORY__ harness)
          }

          test "the driver-semantics-toy harness: its project neither declares nor resolves the tier" {
              let project = __SOURCE_DIRECTORY__
              expectNone "Fuaran.Program.Tests" (declaredIn (Path.Combine(project, "Fuaran.Program.Tests.fsproj")))
              expectNone "Fuaran.Program.Tests (resolved graph)" (expectRead "toy harness" (resolvedIn project))

              expectNone
                  "Fuaran.Program.Tests (built assembly)"
                  (expectRead "toy harness" (referencedBy project "Fuaran.Program.Tests"))
          }

          // The probes, proven able to fail: every reading finds the tier in the
          // UI adapter, which references it by design.
          test "the probes find the tier where it is" {
              Expect.isNonEmpty
                  (declaredIn (Path.Combine(adapterDir, "Fuaran.Program.UI.fsproj")))
                  "the declared-reference probe"

              Expect.isNonEmpty (namedIn adapterDir) "the source probe"
              Expect.isNonEmpty (expectRead "UI adapter" (resolvedIn adapterDir)) "the resolved-graph probe"

              Expect.isNonEmpty
                  (expectRead "UI adapter" (referencedBy adapterDir "Fuaran.Program.UI"))
                  "the built-assembly probe"
          }

          // And the adapter's own names are caught: the server adapter declares a
          // reference to the client adapter, which the `Fuaran.UI` spelling alone
          // would not match.
          test "the probes find a reference to the adapter itself" {
              let declared =
                  declaredIn (Path.Combine(serverAdapterDir, "Fuaran.Program.Server.UI.fsproj"))

              Expect.exists
                  declared
                  (fun line -> line.Contains "Fuaran.Program.UI.fsproj")
                  "the declared-reference probe names the adapter project"

              let resolved = expectRead "server UI adapter" (resolvedIn serverAdapterDir)

              Expect.exists
                  resolved
                  (fun library -> library.StartsWith "Fuaran.Program.UI/")
                  "the resolved-graph probe names the adapter project"
          } ]

// ─── fuaran#2012 — the evidence that cannot leave with the UI tier yet ──────

/// The project references a project file declares, as full paths.
let private projectReferencesOf (fsproj: string) : string list =
    let dir = Path.GetDirectoryName fsproj

    Regex.Matches(File.ReadAllText fsproj, @"<ProjectReference\s+Include=""([^""]+)""")
    |> Seq.map (fun m ->
        Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))))
    |> List.ofSeq

/// Whether a project reaches the UI tier through a declared reference: its own,
/// or one of the projects it references, transitively.
let rec private reachesUiTier (fsproj: string) : bool =
    not (List.isEmpty (declaredIn fsproj))
    || (projectReferencesOf fsproj |> List.exists reachesUiTier)

/// The project file of a suite under `tests/`.
let private suiteProject (suite: string) : string =
    match Directory.GetFiles(Path.Combine(repoRoot, "tests", suite), "*.fsproj") with
    | [| fsproj |] -> fsproj
    | found -> failwithf "tests/%s holds %d project files, not one" suite found.Length

/// The proof ladder's `tested` claims (proofs.json), as (claim id, host path).
let private testedClaims () : (string * string) list =
    use doc =
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "proofs.json")))

    doc.RootElement.GetProperty("claims").EnumerateArray()
    |> Seq.filter (fun claim -> claim.GetProperty("level").GetString() = "tested")
    |> Seq.map (fun claim ->
        claim.GetProperty("id").GetString(), claim.GetProperty("evidence").GetProperty("host").GetString())
    |> List.ofSeq

/// The suite a host path names: `tests/<suite>/<file>`.
let private suiteOf (host: string) : string option =
    match host.Split '/' with
    | [| "tests"; suite; _ |] -> Some suite
    | _ -> None

/// What this repository's gate certifies ONLY through the UI tier, by suite —
/// the record fuaran#2012 made when it was dispatched to move the UI adapters
/// and their suites to the UI tier's own repository, and the reason that move
/// is sequenced after this list is empty rather than before it.
///
/// Two kinds of evidence sit here, and neither can follow the suites out:
///
///   * `tested` claims of the proof ladder. Their differential hosts compile
///     the extracted models (`proofs/oracle`), which is not a package and stays
///     something this repository alone builds — so a host that moved would
///     leave its claim naming a file this repository does not hold, and the
///     core the models describe would no longer be compared with them on any
///     gate of its own. The flow-shapes, each and toy-family claims run over
///     non-UI witnesses already and need only a project that does not reach
///     the tier; the rest are written against the UI witness and need
///     re-hosting over a non-UI one (re-homing D7's proof-host leg).
///   * the program wire format's own conformance: the corpus manifest's
///     `vectors` are documents in the UI vocabulary, so the one host that
///     certifies this repository's codec against them is UI-typed. Moved, the
///     codec would be certified against a released version only, never against
///     the tree a change is made in.
///
/// Shrink this list as the evidence is re-hosted over a non-UI witness. The
/// tests below go red when a host moves, or a claim is added or re-hosted,
/// without the list saying so.
let uiHostedEvidence: (string * string list) list =
    [ "Fuaran.Program.Parity.Tests",
      [ "budget-model-agrees-with-shipped-code"
        "each-model-agrees-with-shipped-code"
        "flow-shapes-model-agrees-with-shipped-code"
        "model-agrees-with-shipped-code"
        "toy-family-model-agrees-with-shipped-code" ]
      "Fuaran.Program.Server.Tests",
      [ "durable-replay-model-agrees-with-shipped-code"
        "effect-gate-model-agrees-with-shipped-code"
        "op-contract-keyed-agrees-at-the-handler"
        "op-contract-wrapper-agrees-with-shipped-code"
        "staging-model-agrees-with-shipped-code"
        "undo-model-agrees-with-shipped-code"
        "wire-vectors" ] ]

/// The suites whose sources read the corpus manifest's wire `vectors`.
let private wireVectorSuites () : string list =
    Directory.GetDirectories(Path.Combine(repoRoot, "tests"))
    |> Array.filter (fun dir ->
        Directory.GetFiles(dir, "*.fs")
        |> Array.exists (fun path ->
            File.ReadAllLines path
            |> Array.exists (fun line ->
                let code =
                    match line.IndexOf "//" with
                    | -1 -> line
                    | at -> line.Substring(0, at)

                code.Contains "\"vectors\"")))
    |> Array.map Path.GetFileName
    |> Array.sort
    |> List.ofArray

[<Tests>]
let evidenceTests =
    testList
        "fuaran#2012 - the evidence certified only through the UI tier"
        [ test "every tested claim is hosted in a suite this repository's gate runs" {
              let claims = testedClaims ()
              Expect.isNonEmpty claims "proofs.json declares tested claims"

              for id, host in claims do
                  Expect.isTrue
                      (File.Exists(Path.Combine(repoRoot, host)))
                      (sprintf "%s: its host %s is in this repository" id host)

                  match suiteOf host with
                  | None -> failtestf "%s: its host %s is not a file of a suite under tests/" id host
                  | Some suite ->
                      Expect.stringContains
                          (File.ReadAllText(suiteProject suite))
                          "<OutputType>Exe</OutputType>"
                          (sprintf "%s: %s is a runner the gate runs" id suite)
          }

          test "the evidence certified only through the UI tier is exactly the recorded list" {
              let claims =
                  testedClaims ()
                  |> List.choose (fun (id, host) ->
                      suiteOf host
                      |> Option.filter (suiteProject >> reachesUiTier)
                      |> Option.map (fun suite -> suite, id))

              let vectors =
                  wireVectorSuites ()
                  |> List.filter (suiteProject >> reachesUiTier)
                  |> List.map (fun suite -> suite, "wire-vectors")

              let found =
                  claims @ vectors
                  |> List.groupBy fst
                  |> List.map (fun (suite, entries) -> suite, entries |> List.map snd |> List.sort)
                  |> List.sortBy fst

              Expect.equal found uiHostedEvidence "the UI-hosted evidence, by suite"
          }

          test "the wire vectors are certified by some suite of this repository" {
              Expect.isNonEmpty (wireVectorSuites ()) "a suite reads the corpus manifest's vectors"
          }

          // The reachability probe, proven able to answer both ways: a suite that
          // reaches the tier only through a referenced project is caught, and the
          // toy harness's project is not.
          test "the reachability probe finds the tier through a project reference, and only there" {
              let parityTests = suiteProject "Fuaran.Program.Parity.Tests"
              Expect.isEmpty (declaredIn parityTests) "the parity runner declares no UI reference itself"
              Expect.isTrue (reachesUiTier parityTests) "and reaches the tier through the parity project"

              Expect.isFalse
                  (reachesUiTier (suiteProject "Fuaran.Program.Tests"))
                  "the toy harness's project reaches no UI tier"
          } ]
