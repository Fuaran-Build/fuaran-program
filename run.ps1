#Requires -Version 7.0
# fuaran-program — "drop into the repo, run one command, the thing works".
# Stage-0 shape (library-only): tool restore -> format -> build -> test.
[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests
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
    dotnet run --project tests/Fuaran.Program.Tests --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
