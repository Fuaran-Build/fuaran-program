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
/// Since fuaran#2012 (DECISIONS.md D32) the rule is the REPOSITORY's, not only
/// the core's: the two UI adapter packages (`Fuaran.Program.UI`,
/// `Fuaran.Program.Server.UI`) and their suites moved to the UI tier's own
/// repository, which depends on this core's released packages, so nothing here
/// pins, declares or names the tier at all. The checks below hold that — the
/// central package file, every project file in the tree, and the evidence this
/// repository's gate certifies.
///
/// Each reading is proven able to fail. Until fuaran#2012 the probe subject was
/// the UI adapter, which referenced the tier by design; with the adapter gone
/// the probes run over SYNTHETIC inputs that reference it — a project file's
/// text, a source line, a restore graph and a resolved name — because a scan
/// that has never been seen to go red is a scan whose mechanism nobody has
/// verified, and the one subject that used to make it red has left.
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

/// A UI-tier package id or namespace: `Fuaran.UI` itself or anything under it,
/// or one of the two adapter packages that instantiate the core at it — whose
/// names do not match `Fuaran.UI` by spelling, so they are named explicitly.
///
/// Since Phase 1905 (DECISIONS.md D34) the rule is wider than the UI tier: ANY
/// `Fuaran.*` name outside the substrate (`Fuaran.Core`, `Fuaran.Compute`) and
/// this domain's own packages is refused on the same terms. The query
/// evaluator seam is generic, and whatever domain answers a query through it —
/// a database, a file store — is a consumer of this core, never a dependency
/// of it. Written as the complement of what is allowed rather than as a list
/// of what is not, so a domain nobody has named yet is refused too.
let private uiName =
    Regex(
        @"\bFuaran\.((Program\.(Server\.)?)?UI|(?!(Core|Compute|Program)\b)[A-Z][A-Za-z0-9]*)(\.|\b|"")",
        RegexOptions.Compiled
    )

/// The same test over a resolved library or assembly name.
let private isUiTier (name: string) =
    name.StartsWith "Fuaran.UI"
    || name.StartsWith "Fuaran.Program.UI"
    || name.StartsWith "Fuaran.Program.Server.UI"
    || (name.StartsWith "Fuaran."
        // The segment after `Fuaran.`, up to the next `.` or a resolved
        // name's `/version`, is the root that decides.
        && not (
            [ "Core"; "Compute"; "Program" ]
            |> List.contains (name.Substring("Fuaran.".Length).Split([| '.'; '/' |]).[0])
        ))

/// Declared references in project-file TEXT: a `PackageReference`, a
/// `ProjectReference` or a central `PackageVersion` naming the tier.
let private declaredInText (label: string) (lines: string array) : string list =
    lines
    |> Array.filter (fun line ->
        (line.Contains "PackageReference"
         || line.Contains "ProjectReference"
         || line.Contains "PackageVersion")
        && uiName.IsMatch line)
    |> Array.map (fun line -> sprintf "%s: %s" label (line.Trim()))
    |> List.ofArray

/// Declared references in a project (or central package) file.
let private declaredIn (file: string) : string list =
    declaredInText (Path.GetFileName file) (File.ReadAllLines file)

/// Source lines naming the tier, with line comments stripped first — an
/// `open` and a fully qualified identifier are both caught; a sentence in a
/// comment is not a reference.
let private namedInLines (label: string) (lines: string array) : string list =
    lines
    |> Array.indexed
    |> Array.choose (fun (i, line) ->
        let code =
            match line.IndexOf "//" with
            | -1 -> line
            | at -> line.Substring(0, at)

        if uiName.IsMatch code then
            Some(sprintf "%s:%d %s" label (i + 1) (line.Trim()))
        else
            None)
    |> List.ofArray

let private namedIn (dir: string) : string list =
    Directory.GetFiles(dir, "*.fs")
    |> Array.collect (fun path -> namedInLines (Path.GetFileName path) (File.ReadAllLines path) |> Array.ofList)
    |> List.ofArray

/// `namedIn` over named files of one directory rather than all of it.
let private namedInFiles (dir: string) (files: string list) : string list =
    files
    |> List.collect (fun file -> namedInLines file (File.ReadAllLines(Path.Combine(dir, file))))

/// Libraries a restore graph RESOLVED — every target's library keys, so a
/// transitive arrival is caught as surely as a declared one.
let private resolvedInAssets (assets: string) : string list =
    use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText assets)

    doc.RootElement.GetProperty("targets").EnumerateObject()
    |> Seq.collect (fun target -> target.Value.EnumerateObject() |> Seq.map _.Name)
    |> Seq.filter isUiTier
    |> Seq.distinct
    |> List.ofSeq

/// The same, read from a project's own `obj/project.assets.json`.
let private resolvedIn (dir: string) : Result<string list, string> =
    let assets = Path.Combine(dir, "obj", "project.assets.json")

    if not (File.Exists assets) then
        Error(sprintf "no restore graph at %s — build first; an absent graph is not a clean one" assets)
    else
        Ok(resolvedInAssets assets)

/// Assemblies the BUILT assembly references that a predicate selects, read from
/// its own metadata.
let private referencesOf (keep: string -> bool) (dir: string) (assembly: string) : Result<string list, string> =
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
        |> Seq.filter keep
        |> List.ofSeq
        |> Ok

let private referencedBy = referencesOf isUiTier

let private expectNone (label: string) (hits: string list) =
    Expect.isEmpty hits (sprintf "%s reaches the UI tier: %s" label (String.Join("; ", hits)))

let private expectRead (label: string) (result: Result<string list, string>) : string list =
    match result with
    | Ok hits -> hits
    | Error why -> failtestf "%s: %s" label why

/// Every project file in the repository's tree — sources, tests, the proof
/// oracle, and anything a later change adds — skipping build output.
let private projectFiles () : string list =
    Directory.GetFiles(repoRoot, "*.*proj", SearchOption.AllDirectories)
    |> Array.filter (fun path ->
        let parts =
            path.Substring(repoRoot.Length).Split([| '\\'; '/' |], StringSplitOptions.RemoveEmptyEntries)

        not (
            parts
            |> Array.exists (fun p -> p = "bin" || p = "obj" || p = "node_modules" || p.StartsWith ".")
        ))
    |> Array.sort
    |> List.ofArray

/// The repository's central package file.
let private packagesProps = Path.Combine(repoRoot, "Directory.Packages.props")

/// A scratch directory under the system temp root, removed after use.
let private withScratch (body: string -> 'a) : 'a =
    let dir =
        Path.Combine(Path.GetTempPath(), "fuaran-program-boundary-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        body dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

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
          // tier on purpose (the probes below spell it), and the claim is about
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

          // fuaran#2012 — the repository pins no UI-tier package, and no project
          // in it declares one. The central file first: a pin there is the
          // first step back towards a reference, and it is the one place a
          // version would be chosen for every project at once.
          test "Directory.Packages.props pins no UI-tier package (fuaran#2012)" {
              Expect.isTrue (File.Exists packagesProps) "the central package file is where it is named"
              expectNone "Directory.Packages.props" (declaredIn packagesProps)
          }

          test "no project in the repository declares a UI-tier reference (fuaran#2012)" {
              let projects = projectFiles ()

              // The walk is proven to see the tree: every core package, the test
              // project and the proof oracle are among what it found.
              for package in corePackages do
                  Expect.exists projects (fun p -> p.EndsWith(package + ".fsproj")) (package + " is walked")

              Expect.exists projects (fun p -> p.EndsWith "Fuaran.Program.Tests.fsproj") "the test project is walked"
              Expect.exists projects (fun p -> p.EndsWith "Fuaran.Program.Proofs.Oracle.fsproj") "the oracle is walked"

              expectNone "the repository's project files" (projects |> List.collect declaredIn)
          }

          // The probes, proven able to fail. Until fuaran#2012 every reading
          // found the tier in the UI adapter, which referenced it by design; the
          // adapter left with that fuaran#2012, so each reading is now shown red
          // over a synthetic input that references the tier.
          test "the probes find the tier where it is" {
              Expect.isNonEmpty
                  (declaredInText "probe.fsproj" [| """    <PackageReference Include="Fuaran.UI.Ops" />""" |])
                  "the declared-reference probe, on a package reference"

              Expect.isNonEmpty
                  (declaredInText
                      "Directory.Packages.props"
                      [| """    <PackageVersion Include="Fuaran.UI" Version="0.90.0" />""" |])
                  "the declared-reference probe, on a central pin"

              Expect.isNonEmpty (namedInLines "Probe.fs" [| "open Fuaran.UI.Types" |]) "the source probe, on an open"

              Expect.isNonEmpty
                  (namedInLines "Probe.fs" [| "let n = Fuaran.UI.Types.NodeId \"x\"" |])
                  "the source probe, on a fully qualified identifier"

              Expect.isEmpty
                  (namedInLines "Probe.fs" [| "// a comment about Fuaran.UI is not a reference" |])
                  "and not on a comment"

              // Phase 1905: any other domain is refused on the same terms, and
              // the substrate and this domain's own packages are not.
              Expect.isNonEmpty
                  (namedInLines "Probe.fs" [| "open Fuaran.Example.Store" |])
                  "the source probe, on another domain"

              Expect.isNonEmpty
                  (declaredInText "probe.fsproj" [| """    <PackageReference Include="Fuaran.Example" />""" |])
                  "the declared-reference probe, on another domain"

              Expect.isTrue (isUiTier "Fuaran.Example.Adapter") "the resolved-name probe, on another domain"

              Expect.isEmpty
                  (namedInLines
                      "Probe.fs"
                      [| "open Fuaran.Core.Wire"
                         "open Fuaran.Compute"
                         "open Fuaran.Program.Bounded" |])
                  "and not on the substrate or this domain"

              Expect.isFalse (isUiTier "Fuaran.Compute.DataFrame") "the resolved-name probe passes the substrate"

              withScratch (fun dir ->
                  let assets = Path.Combine(dir, "project.assets.json")

                  File.WriteAllText(
                      assets,
                      """{"targets":{"net10.0":{"Fuaran.Program.Bounded/0.7.1":{},"Fuaran.UI.Ops/0.90.0":{}}}}"""
                  )

                  Expect.equal (resolvedInAssets assets) [ "Fuaran.UI.Ops/0.90.0" ] "the resolved-graph probe")

              // The built-assembly reader, shown to READ references: it finds the
              // runtime library this project's own assembly references, so the
              // empty UI-tier answer above is a reading rather than a miss.
              let runtime =
                  expectRead
                      "the test assembly"
                      (referencesOf (fun n -> n = "FSharp.Core") __SOURCE_DIRECTORY__ "Fuaran.Program.Tests")

              Expect.equal runtime [ "FSharp.Core" ] "the built-assembly reader sees a reference that is there"

              Expect.isTrue (isUiTier "Fuaran.UI.Renderer.Core") "a UI-tier assembly name is one"
              Expect.isFalse (isUiTier "Fuaran.Program.Bounded") "and a core one is not"
          }

          // And the adapters' own names are caught: they do not match the
          // `Fuaran.UI` spelling, so a reference to one would otherwise pass.
          test "the probes find a reference to the adapters themselves" {
              Expect.isNonEmpty
                  (declaredInText
                      "probe.fsproj"
                      [| """    <ProjectReference Include="..\Fuaran.Program.UI\Fuaran.Program.UI.fsproj" />""" |])
                  "the declared-reference probe names the client adapter's project"

              Expect.isNonEmpty
                  (declaredInText "probe.fsproj" [| """    <PackageReference Include="Fuaran.Program.Server.UI" />""" |])
                  "and the server adapter's package"

              Expect.isTrue (isUiTier "Fuaran.Program.UI") "the client adapter's resolved name"
              Expect.isTrue (isUiTier "Fuaran.Program.Server.UI") "the server adapter's resolved name"
          } ]

// ─── fuaran#2012 — no evidence this repository claims is certified only through the UI tier ───

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

/// A row of the proof ladder (proofs.json): its id, level, evidence host, and
/// the repository its evidence lives in when that is not this one (the
/// ladder's `repo` field).
type private LadderRow =
    { Id: string
      Level: string
      Host: string option
      Repo: string option }

let private ladderRows () : LadderRow list =
    use doc =
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "proofs.json")))

    let str (name: string) (element: System.Text.Json.JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Option.ofObj (value.GetString())
        | _ -> None

    [ for claim in doc.RootElement.GetProperty("claims").EnumerateArray() ->
          { Id = claim.GetProperty("id").GetString() |> string
            Level = claim.GetProperty("level").GetString() |> string
            Host =
              match claim.TryGetProperty "evidence" with
              | true, evidence -> str "host" evidence
              | _ -> None
            Repo = str "repo" claim } ]

/// The proof ladder's `tested` claims whose evidence lives in THIS repository,
/// as (claim id, host path). A row whose evidence moved to another repository
/// says so with `repo`, and is that repository's gate's to certify.
let private testedClaims () : (string * string) list =
    ladderRows ()
    |> List.filter (fun row -> row.Level = "tested" && row.Repo.IsNone)
    |> List.map (fun row ->
        row.Id,
        match row.Host with
        | Some host -> host
        | None -> failwithf "%s is a tested claim with no host" row.Id)

/// The suite a host path names: `tests/<suite>/<file>`.
let private suiteOf (host: string) : string option =
    match host.Split '/' with
    | [| "tests"; suite; _ |] -> Some suite
    | _ -> None

/// What this repository's gate certifies ONLY through the UI tier, by suite —
/// the record fuaran#2012 made when it was first dispatched to move the UI
/// adapters and their suites to the UI tier's own repository, shrunk by
/// fuaran#2017 to the evidence that was ABOUT the UI tier, and emptied by
/// fuaran#2012 when that evidence left with the adapters:
///
///   * `model-agrees-with-shipped-code` (the bounded fold over the UI
///     witness's fourteen arms) and `budget-model-agrees-with-shipped-code`
///     (the UI witness's data-bearing weighing and the UI BoundedDriver's G2
///     gate) are hosted in the UI tier's repository beside the adapter they
///     are about, and say so on their ladder rows with `repo`; the core's half
///     of each is compared here, at the toy witness;
///   * the UI-vocabulary wire vectors are certified there by the server UI
///     suite; this repository certifies the codec against the toy-subject
///     vectors (fuaran#2017).
///
/// It is EMPTY, and the tests below hold it empty: no evidence this repository
/// claims is certified only through the UI tier.
let uiHostedEvidence: (string * string list) list = []

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

/// The evidence reachable only through the UI tier, by suite, for a given set
/// of tested claims, given how a suite name resolves to its project file — a
/// pure function of its inputs, so the guard's ability to see a claim moved
/// BACK onto a UI-reaching suite can be demonstrated rather than trusted.
let private uiHostedOfWith
    (projectOf: string -> string)
    (claims: (string * string) list)
    (vectorSuites: string list)
    : (string * string list) list =
    let hosted =
        claims
        |> List.choose (fun (id, host) ->
            suiteOf host
            |> Option.filter (projectOf >> reachesUiTier)
            |> Option.map (fun suite -> suite, id))

    let vectors =
        vectorSuites
        |> List.filter (projectOf >> reachesUiTier)
        |> List.map (fun suite -> suite, "wire-vectors")

    hosted @ vectors
    |> List.groupBy fst
    |> List.map (fun (suite, entries) -> suite, entries |> List.map snd |> List.sort)
    |> List.sortBy fst

let private uiHostedOf = uiHostedOfWith suiteProject

/// A claim of the ladder, its host pointed somewhere else.
let private rehostedTo (claim: string) (host: string) (claims: (string * string) list) =
    claims
    |> List.map (fun (id, current) -> if id = claim then id, host else id, current)

/// Two synthetic suites under a scratch root, for the probes: one that declares
/// a UI-tier package reference itself, and one that reaches the tier only
/// through a project reference to the first. Returns the resolver that maps
/// their suite names to their project files, and every real suite to its own.
let private withUiReachingSuites (body: (string -> string) -> unit) =
    withScratch (fun dir ->
        let direct = Path.Combine(dir, "Probe.Ui.Tests", "Probe.Ui.Tests.fsproj")

        let viaReference =
            Path.Combine(dir, "Probe.ViaReference.Tests", "Probe.ViaReference.Tests.fsproj")

        Directory.CreateDirectory(Path.GetDirectoryName direct) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName viaReference) |> ignore

        File.WriteAllText(
            direct,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Fuaran.UI.Ops\" />\n  </ItemGroup>\n</Project>\n"
        )

        File.WriteAllText(
            viaReference,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"..\\Probe.Ui.Tests\\Probe.Ui.Tests.fsproj\" />\n  </ItemGroup>\n</Project>\n"
        )

        body (fun suite ->
            match suite with
            | "Probe.Ui.Tests" -> direct
            | "Probe.ViaReference.Tests" -> viaReference
            | other -> suiteProject other))

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

          test "no evidence this repository claims is certified only through the UI tier" {
              Expect.isEmpty uiHostedEvidence "the recorded list is empty"

              Expect.equal
                  (uiHostedOf (testedClaims ()) (wireVectorSuites ()))
                  uiHostedEvidence
                  "the UI-hosted evidence, by suite"
          }

          // The two claims that were ABOUT the UI adapter moved with it. Their
          // rows stay on this ladder as pointers — the claim, where it is hosted
          // now, and that it is another repository's gate that runs it — rather
          // than being deleted, so the ladder's history reads as a move and not
          // as a claim that stopped being made.
          test "the claims about the UI adapter point at the repository that hosts them now" {
              let moved =
                  ladderRows ()
                  |> List.filter (fun row -> row.Repo.IsSome)
                  |> List.map (fun row -> row.Id, row.Level, row.Repo, row.Host)
                  |> List.sort

              Expect.equal
                  moved
                  [ "budget-model-agrees-with-shipped-code",
                    "tested",
                    Some "Fuaran/Fuaran-UI/fuaran-dotnet",
                    Some "src/Fuaran.Program.UI.Parity.Tests/ProofOracleTests.fs"
                    "model-agrees-with-shipped-code",
                    "tested",
                    Some "Fuaran/Fuaran-UI/fuaran-dotnet",
                    Some "src/Fuaran.Program.UI.Parity.Tests/ProofOracleTests.fs" ]
                  "exactly the two claims, each naming its new host"
          }

          test "the wire vectors are certified by some suite of this repository" {
              Expect.isNonEmpty (wireVectorSuites ()) "a suite reads the corpus manifest's vectors"
          }

          // fuaran#2017: the codec is certified by a suite that reaches no UI
          // tier, so moving the UI-typed certification out left one behind.
          test "the wire vectors are certified by a suite that reaches no UI tier (fuaran#2017)" {
              let uiFree =
                  wireVectorSuites () |> List.filter (suiteProject >> reachesUiTier >> not)

              Expect.contains uiFree "Fuaran.Program.Tests" "the toy-witness conformance leg reads the vectors"
          }

          // The guard, shown able to fail in the direction that matters. A
          // claim's host pointed back at a UI-reaching suite must change what
          // the guard computes — a suite that declares the tier itself, and one
          // that reaches it only through a project reference. No such suite is
          // left in this repository, which is the point, so the two are
          // synthetic; a guard that did not see them would let a later edit
          // move evidence back behind the tier with every test green.
          test "the guard goes red when a claim's host points back at a UI-reaching suite" {
              let claims = testedClaims ()
              let staging = "staging-model-agrees-with-shipped-code"
              let toyFamily = "toy-family-model-agrees-with-shipped-code"

              for claim in [ staging; toyFamily ] do
                  Expect.isTrue
                      (claims |> List.exists (fst >> (=) claim))
                      (sprintf "the probe moves %s, a claim the ladder declares" claim)

              let pairs (found: (string * string list) list) =
                  found
                  |> List.collect (fun (suite, ids) -> ids |> List.map (fun id -> suite, id))

              withUiReachingSuites (fun projectOf ->
                  let direct =
                      uiHostedOfWith
                          projectOf
                          (claims |> rehostedTo staging "tests/Probe.Ui.Tests/ProofOracleTests.fs")
                          (wireVectorSuites ())

                  Expect.notEqual direct uiHostedEvidence "a claim moved to a UI-reaching suite is caught"

                  Expect.contains (pairs direct) ("Probe.Ui.Tests", staging) "and is named, at the suite it moved to"

                  let throughReference =
                      uiHostedOfWith
                          projectOf
                          (claims
                           |> rehostedTo toyFamily "tests/Probe.ViaReference.Tests/ProofOracleTests.fs")
                          (wireVectorSuites ())

                  Expect.contains
                      (pairs throughReference)
                      ("Probe.ViaReference.Tests", toyFamily)
                      "so is one moved to a suite that reaches the tier only through a project reference"

                  Expect.equal
                      (uiHostedOfWith
                          projectOf
                          (claims
                           |> rehostedTo staging "tests/Fuaran.Program.Tests/ToyStagingOracleTests.fs")
                          (wireVectorSuites ()))
                      uiHostedEvidence
                      "while the same claim at its UI-free host changes nothing")
          }

          // The reachability probe, proven able to answer both ways: a suite that
          // reaches the tier only through a referenced project is caught, and the
          // toy harness's project is not.
          test "the reachability probe finds the tier through a project reference, and only there" {
              withUiReachingSuites (fun projectOf ->
                  let viaReference = projectOf "Probe.ViaReference.Tests"
                  Expect.isEmpty (declaredIn viaReference) "the probe suite declares no UI reference itself"
                  Expect.isTrue (reachesUiTier viaReference) "and reaches the tier through the project it references")

              Expect.isFalse
                  (reachesUiTier (suiteProject "Fuaran.Program.Tests"))
                  "the toy harness's project reaches no UI tier"
          } ]
