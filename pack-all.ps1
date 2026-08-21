<#
.SYNOPSIS
    Fuaran-Program estate — pack producer siblings into the shared root
    local-nuget-feed.
.DESCRIPTION
    The cross-repo inner-loop channel (workspace CLAUDE.md "NuGet feed model"):
    a repo that another consumes by PackageReference is packed here so the
    consumer restores it from the shared root ..\..\local-nuget-feed.

    Packs the Fuaran.Program.* tier — the domain package and the bounded
    interpreter (Fuaran.Program.Bounded: the bounded-Action fold, the binding
    re-resolution pass, and the server placement of the program loop).

    ORDERING: this step runs AFTER Fuaran-UI. The bounded tier consumes the UI
    tier's published packages by PackageReference (DECISIONS.md D4), and the
    dependency runs ONE WAY — no Fuaran.UI.* package references Fuaran.Program.*
    (D5) — so the order is a genuine dependency edge, not a convention. The
    reverse would be a cycle and was refused for exactly that reason.

    This script is invoked by the workspace-root pack-all.ps1, which owns the
    cross-estate ordering and the same-version repack guard. Running it directly
    packs correctly but BYPASSES that guard — prefer the root entry point.
.PARAMETER Configuration
    Build configuration to pack. Release by default, matching the other estate
    pack scripts.
.EXAMPLE
    ..\..\pack-all.ps1 -Only Fuaran-Program
    The preferred entry point (guarded).
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

# Named rather than packing the solution, which would also walk tests.
$producers = @(
    'src\Fuaran.Program\Fuaran.Program.fsproj'
    'src\Fuaran.Program.Bounded\Fuaran.Program.Bounded.fsproj'
    'src\Fuaran.Program.Runtime\Fuaran.Program.Runtime.fsproj'
    'src\Fuaran.Program.Server\Fuaran.Program.Server.fsproj'
)

foreach ($proj in $producers) {
    Write-Host "== pack: $proj" -ForegroundColor Cyan
    dotnet pack (Join-Path $PSScriptRoot $proj) -c $Configuration -o $feed --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Packed $($producers.Count) project(s) into $feed" -ForegroundColor Green
