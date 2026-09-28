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
