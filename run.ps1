#Requires -Version 7.0
# fuaran-program — "drop into the repo, run one command, the thing works".
# Stage-0 shape (library-only): tool restore -> format -> pins -> build -> test [-> pack].
[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    # Skip the Fable parity leg (needs node + the Fable tool). The .NET legs
    # still run — a partial gate is honest; a silently-skipped one is not.
    [switch] $SkipFable,
    # Skip the pin preflight. It reaches the package registry; an unreachable
    # one is already reported as unverified rather than failed, so this is for
    # deliberately working against an unpublished local pack, not for going
    # offline.
    [switch] $SkipPins,
    # Pack the shipping packages into the shared local feed for downstream
    # consumption. Off by default: packing is an inner-loop publication step,
    # not part of the verify gate.
    [switch] $Pack,
    # Phase 1985 - the gate LANE. A lane SELECTS which checks run after the build; it never
    # drops tool restore, format, the pin preflight or the build, and `-Skip*` switches compose
    # with it unchanged.
    #
    #   full - every Expecto runner and the Fable parity leg: exactly the gate as it stood before
    #          lanes existed. The default, so `pwsh ./run.ps1` is unchanged, and the only lane a
    #          release or a recorded gate run may cite.
    #   fast - every Expecto runner; the Fable parity leg is skipped, by name. The pre-merge lane.
    #   pure - only the runners named in $pureRunners below: suites that read neither the
    #          filesystem nor the conformance corpus. Runs without the corpus present.
    #
    # The split is chosen from a measurement, not a guess (2026-10-02, warm tree, full lane):
    # tool restore 1.0s, format 6.2s, pins 4.5s, build 104.7s; the runners, each wall-clock
    # including `dotnet run`'s per-project up-to-date check - Bench 23.9s, Bounded.Tests 24.0s,
    # Parity.Tests 29.7s, Runtime.Tests 26.1s, Server.Tests 28.9s, Tests 20.9s; the Fable parity
    # leg 258.1s (compile 247.8s, node 10.3s). Every runner's own test execution is 1-6s and the
    # rest of its wall-clock is the up-to-date check every runner pays alike, so no test project
    # is slow - including the two that host the proof oracle's differential - and dropping one
    # would buy ~25s at the price of a conformance or parity leg. The Fable leg is about half of
    # the whole gate, and it is the one thing `fast` drops.
    [ValidateSet('pure', 'fast', 'full')]
    [string] $Lane = 'full',
    # fuaran#2011 - the conformance corpus's SCENARIO FAMILIES this run certifies, by name
    # (the manifest's `scenarioFamilies`). Omitted, every declared family runs - the gate as
    # it stood. `-Families driver-semantics-toy` runs the toy witness's family alone: every
    # suite that reads a family consults the selection and SKIPS a deselected one by name,
    # and a name the manifest does not declare fails the run rather than matching nothing.
    # Passed to the suites as FUARAN_PROGRAM_FAMILIES, which a caller may also set directly.
    # Which suites certify which family, and the one suite that reads its family on every run
    # regardless, is recorded by a test (tests/Fuaran.Program.Tests/ToyFamilyTests.fs).
    [string[]] $Families
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Seeded deliberately. `$LASTEXITCODE` is $null in a fresh session until a NATIVE
# command runs, and every stage below guards on `-ne 0` — so with a stage skipped
# the first such guard compared $null, took the branch, and `exit $null` returned
# 0. `-SkipFormat` therefore ran the pin preflight and then exited GREEN, having
# built nothing and tested nothing. A gate that reports success for work it did
# not do is the failure this file's other comments exist to rule out.
# The `global:` scope is load-bearing: a plain assignment creates a SCRIPT-scope copy,
# and when this file is invoked with `&` that copy shadows the real exit code, so
# every guard below reads 0 whatever the native command returned.
$global:LASTEXITCODE = 0

# The runners the `pure` lane admits. Named rather than inferred: whether a suite touches the
# filesystem or the corpus is a property of its code, not of its name or its project file. A name
# here that matches no runner is REFUSED below rather than ignored, so a rename cannot quietly
# empty the lane.
$pureRunners = @('Fuaran.Program.Bounded.Tests', 'Fuaran.Program.Runtime.Tests')

function Write-Skip([string] $what, [string] $why) {
    Write-Host "── $($what): SKIPPED — $why ──" -ForegroundColor Yellow
}

if ($Lane -ne 'full') {
    Write-Host "── lane '$Lane' (the citable gate is the full lane: pwsh ./run.ps1) ──" -ForegroundColor Yellow
}

if ($Families) {
    $env:FUARAN_PROGRAM_FAMILIES = ($Families -join ',')
}
if ($env:FUARAN_PROGRAM_FAMILIES) {
    Write-Host "── scenario families selected: $($env:FUARAN_PROGRAM_FAMILIES) ──" -ForegroundColor Yellow
}

if (-not $SkipFormat) {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet fantomas src tests
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else { Write-Skip 'tool restore + format' '-SkipFormat' }

# Re-seeded before every stage, not only once: a stage that runs no native command
# would otherwise read whatever the previous stage left behind.
$global:LASTEXITCODE = 0
if (-not $SkipPins) {
    # BEFORE the build, deliberately. A pin naming a version no source serves is
    # not an error to NuGet — it is NU1603, a warning, after which the nearest
    # higher version is substituted and the build proceeds against a package
    # nobody chose. Downstream that float has already presented as four
    # assembly-reference errors in the Fable leg, naming its cause nowhere. Run
    # first, so the one-line diagnosis arrives before the misdirection does.
    & (Join-Path $PSScriptRoot 'tools/check-pins.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else { Write-Skip 'pin preflight' '-SkipPins' }

$global:LASTEXITCODE = 0
if (-not $SkipBuild) {
    dotnet build Fuaran.Program.slnx --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else { Write-Skip 'build' '-SkipBuild' }

$global:LASTEXITCODE = 0
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
    if ($Lane -eq 'pure') {
        $unknown = @($pureRunners | Where-Object { $_ -notin @($testProjects | ForEach-Object Name) })
        if ($unknown.Count -gt 0) {
            # A stale name would shrink the lane silently; a lane must never get quietly weaker.
            Write-Error "-Lane pure names runner(s) that do not exist under tests/: $($unknown -join ', ')." -ErrorAction Continue
            $global:LASTEXITCODE = 1
            exit 1
        }
    }
    foreach ($project in $testProjects) {
        if ($Lane -eq 'pure' -and $project.Name -notin $pureRunners) {
            Write-Skip "tests: $($project.Name)" "not in lane 'pure'"
            continue
        }
        Write-Host "── tests: $($project.Name) ──"
        dotnet run --project $project.FullName
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
else { Write-Skip 'tests' '-SkipTests' }

$global:LASTEXITCODE = 0
if ($SkipTests -or $SkipFable) {
    Write-Skip 'parity leg (Fable)' $(if ($SkipTests) { '-SkipTests' } else { '-SkipFable' })
}
elseif ($Lane -ne 'full') {
    # The one stage a lane drops: about half the gate's wall-clock (see the -Lane comment).
    Write-Skip 'parity leg (Fable)' "lane '$Lane' (the full lane runs it)"
}
else {
    # Leg (c) of the tier-parity family: the SAME runner compiled to JavaScript,
    # reading the SAME scenario files — the conformance corpus's driver-semantics
    # family. "It compiles under Fable" and "it behaves the same under Fable" are
    # different claims and only the second one matters, which is why this is a run
    # and not just a compile.
    #
    # --noCache is load-bearing: a cached Fable compile can serve a pass for
    # sources that no longer exist, which is the false-clean this leg exists to
    # rule out.
    #
    # The corpus is a sibling clone and a BUILD INPUT, resolved the same way the
    # .NET legs resolve it: FUARAN_PROGRAM_SPEC, else the sibling path. Its
    # absence fails this leg rather than skipping it.
    if (-not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue)) {
        Write-Host "── parity leg (Fable): SKIPPED — node not found on PATH ──" -ForegroundColor Yellow
    }
    else {
        $spec =
            if ($env:FUARAN_PROGRAM_SPEC) { $env:FUARAN_PROGRAM_SPEC }
            else { Join-Path $PSScriptRoot '../Fuaran-UI/fuaran-program-spec' }
        $corpus = Join-Path $spec 'wire-fixtures'
        if (-not (Test-Path (Join-Path $corpus 'manifest.json'))) {
            throw "The conformance corpus is not present at '$corpus'. It is a sibling clone and a BUILD INPUT to this gate — clone it beside this repository, or point FUARAN_PROGRAM_SPEC at it."
        }

        Write-Host "── parity leg (Fable/node) ──"
        dotnet fable tests/Fuaran.Program.Parity.Fable/FableParity.fsproj -o tests/Fuaran.Program.Parity.Fable/output --noCache
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        node tests/Fuaran.Program.Parity.Fable/output/Main.js (Resolve-Path $corpus).Path
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

if ($Pack) {
    # The shared local folder feed, resolved relative to this repo — the same
    # path nuget.config declares as `local`, so a fresh pack shadows a released
    # package at the same version for inner-loop iteration.
    $feed = Join-Path $PSScriptRoot '../../local-nuget-feed'
    if (-not (Test-Path $feed)) {
        throw "Local folder feed not found at '$feed'. Packing is for developing this tier alongside a consumer that restores from it; create the folder, or drop -Pack."
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
