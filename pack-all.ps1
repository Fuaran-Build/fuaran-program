<#
.SYNOPSIS
    Pack the Fuaran.Program.* producers into a shared local folder feed.
.DESCRIPTION
    The inner-loop distribution channel for anyone developing this tier
    alongside a consumer: a folder feed at ..\..\local-nuget-feed, declared as
    the `local` source in nuget.config, which a consumer restores from ahead of
    the released source. Released distribution is a tag push, not this script.

    Packs the Fuaran.Program.* tier — the domain package and the bounded
    interpreter (Fuaran.Program.Bounded: the bounded-Action fold, the binding
    re-resolution pass, and the server placement of the program loop).

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
