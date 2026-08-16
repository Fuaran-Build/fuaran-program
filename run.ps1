#Requires -Version 7.0
# fuaran-program — "drop into the repo, run one command, the thing works".
# Stage-0 shape (library-only): tool restore -> format -> build -> test [-> pack].
[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    # Skip the Fable parity leg (needs node + the Fable tool). The .NET legs
    # still run — a partial gate is honest; a silently-skipped one is not.
    [switch] $SkipFable,
    # Pack the shipping packages into the shared local feed for downstream
    # consumption. Off by default: packing is an inner-loop publication step,
    # not part of the verify gate.
    [switch] $Pack
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipFormat) {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet fantomas src tests
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $SkipBuild) {
    dotnet build Fuaran.Program.slnx --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $SkipTests) {
    # Every test project is its own Expecto assembly runner, so each is invoked
    # in turn and the first failure stops the gate.
    #
    # NOTE the absence of `--no-build`, and do not add it back. A solution build
    # refreshes each project's OWN output but does not reliably re-copy a
    # transitively-referenced assembly into a test project's bin — so a change to
    # a library two hops down leaves the test running against a stale copy and
    # reporting green. That was caught here by deliberately breaking the shared
    # interpreter and watching the parity family pass anyway; `dotnet run`
    # without `--no-build` does the up-to-date check per project and fixes it.
    $testProjects =
        Get-ChildItem -Path tests -Directory |
            Where-Object { Get-ChildItem -Path $_.FullName -Filter *.fsproj -File } |
            Where-Object { (Get-Content (Get-ChildItem -Path $_.FullName -Filter *.fsproj -File)[0].FullName -Raw) -match '<OutputType>Exe</OutputType>' } |
            Sort-Object Name
    foreach ($project in $testProjects) {
        Write-Host "── tests: $($project.Name) ──"
        dotnet run --project $project.FullName
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

if (-not $SkipTests -and -not $SkipFable) {
    # Leg (c) of the tier-parity family: the SAME runner compiled to JavaScript,
    # reading the SAME fixture files. "It compiles under Fable" and "it behaves
    # the same under Fable" are different claims and only the second one matters,
    # which is why this is a run and not just a compile.
    #
    # --noCache is load-bearing: a cached Fable compile can serve a pass for
    # sources that no longer exist, which is the false-clean this leg exists to
    # rule out.
    if (-not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue)) {
        Write-Host "── parity leg (Fable): SKIPPED — node not found on PATH ──" -ForegroundColor Yellow
    }
    else {
        Write-Host "── parity leg (Fable/node) ──"
        dotnet fable tests/Fuaran.Program.Parity.Fable/FableParity.fsproj -o tests/Fuaran.Program.Parity.Fable/output --noCache
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        node tests/Fuaran.Program.Parity.Fable/output/Main.js tests/fixtures
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

if ($Pack) {
    # The shared workspace feed, resolved relative to this repo — the same path
    # nuget.config declares as `local`, so a fresh pack shadows a released
    # package at the same version for inner-loop iteration.
    $feed = Join-Path $PSScriptRoot '../../local-nuget-feed'
    if (-not (Test-Path $feed)) {
        throw "Local feed not found at '$feed'. Expected the shared workspace feed beside the estates."
    }

    $packable =
        Get-ChildItem -Path src -Recurse -Filter *.fsproj |
            Where-Object { (Get-Content $_.FullName -Raw) -match '<IsPackable>true</IsPackable>' } |
            Sort-Object Name
    foreach ($project in $packable) {
        Write-Host "── pack: $($project.BaseName) ──"
        dotnet pack $project.FullName -c Release -o $feed --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
