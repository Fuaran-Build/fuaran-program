<#
.SYNOPSIS
    Pack the Fuaran.Program.* producers into a shared local folder feed.
.DESCRIPTION
    The inner-loop distribution channel for anyone developing this tier
    alongside a consumer: a folder feed at ..\..\local-nuget-feed, declared as
    the `local` source in nuget.config, which a consumer restores from ahead of
    the released source. Released distribution is a tag push, not this script.

    Packs every packable project under src: the Fuaran.Program.* core (the
    domain package, the bounded fold, the runtime and the server placement)
    and the two UI adapter packages, Fuaran.Program.UI and
    Fuaran.Program.Server.UI, which release at the core's one version.

    ORDERING: the UI tier packs BEFORE this one. The bounded tier consumes the
    UI tier's published packages by PackageReference (DECISIONS.md D4), and the
    dependency runs ONE WAY — no Fuaran.UI.* package references Fuaran.Program.*
    (D5) — so the order is a genuine dependency edge, not a convention. The
    reverse would be a cycle and was refused for exactly that reason.

    `pwsh ./run.ps1 -Pack` packs the same set as part of the ordinary gate.
.PARAMETER Configuration
    Build configuration to pack. Release by default.
.EXAMPLE
    pwsh ./pack-all.ps1
#>

#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$feed = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'local-nuget-feed'
New-Item -ItemType Directory -Force -Path $feed | Out-Null

# Every project under src that declares itself packable: the SAME discovery
# `run.ps1 -Pack` uses, so the two cannot pack different sets. Not the solution,
# which would also walk tests. A hand-kept list here packed the four core
# packages and silently omitted the two UI adapter packages once Phase 1897
# made them packable, so a consumer restoring `Fuaran.Program.Server.UI` off
# the local feed found no such package.
$producers =
    Get-ChildItem -Path (Join-Path $PSScriptRoot 'src') -Recurse -Filter *.fsproj |
        Where-Object { (Get-Content $_.FullName -Raw) -match '<IsPackable>true</IsPackable>' } |
        Sort-Object Name

foreach ($proj in $producers) {
    Write-Host "== pack: $($proj.BaseName)" -ForegroundColor Cyan
    dotnet pack $proj.FullName -c $Configuration -o $feed --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Packed $($producers.Count) project(s) into $feed" -ForegroundColor Green
