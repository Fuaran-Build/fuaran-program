#Requires -Version 7.0
# fuaran-program — "drop into the repo, run one command, the thing works".
# Stage-0 shape (library-only): tool restore -> format -> build -> test [-> pack].
[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests,
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
    $testProjects = Get-ChildItem -Path tests -Directory | Sort-Object Name
    foreach ($project in $testProjects) {
        Write-Host "── tests: $($project.Name) ──"
        dotnet run --project $project.FullName --no-build
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
