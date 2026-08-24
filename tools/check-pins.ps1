#Requires -Version 7.0
<#
.SYNOPSIS
    Preflight: every `Fuaran.*` version this repository pins must be servable
    EXACTLY by a restore path, and by a restore path that is not this machine's
    alone.

.DESCRIPTION
    A pin that names a version no source can serve is not an error. NuGet's
    answer to it is NU1603 — a WARNING — after which it silently substitutes the
    nearest higher version and carries on. The build then compiles against a
    package nobody chose, and the substitution is reported once, in a line
    nothing fails on.

    That is not a hypothetical failure mode here. This repository pinned the UI
    tier at a version that had never been published, restored green on the one
    machine whose local feed happened to hold a locally-packed copy, and floated
    a minor version ahead everywhere else. Where it floated it did not merely
    build something different: the NU1603 output aborts the Fable project crack,
    so the parity leg failed with four assembly-reference errors that named the
    float nowhere. A warning had become a build failure two removes away from
    its cause.

    This says it in one line, before the build, and it distinguishes the two
    ways a pin can be wrong — because they have different remedies:

      MISSING     No source serves it. Restore floats HERE, now. Correct the pin,
                  or pack/publish the version it names.
      LOCAL-ONLY  Only this machine's local feed serves it. Restore is exact here
                  and floats in every fresh clone and every CI job. A version that
                  only one machine can resolve has not been released; it has been
                  cached. Publish it, or pin one that is published.

    Both are failures. The second is the one that hides, which is why it is not
    merely a warning: the machine that can still build is exactly the machine
    that will not notice.

    A network fault is NOT treated as absence. An outage would otherwise read as
    an accusation against the producer, and the accusation would be indelible in
    the log. Transport is separated from a real answer by HTTP status, and a pin
    the local feed can serve while the registry is unreachable is reported as
    unverified rather than judged.

.PARAMETER Props
    The central package-version file. Defaults to the repository's own.

.PARAMETER Feed
    The local folder feed. Defaults to the `local` source declared in nuget.config.

.PARAMETER Offline
    Skip the registry entirely: check only that a local source serves each pin
    exactly. Honest and weaker — it cannot see the LOCAL-ONLY class at all, and
    says so.
#>
[CmdletBinding()]
param(
    [string] $Props,
    [string] $Feed,
    [switch] $Offline
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Props) { $Props = Join-Path $repoRoot 'Directory.Packages.props' }
if (-not (Test-Path $Props)) { throw "No package-version file at '$Props'." }

# The feed is READ FROM nuget.config rather than assumed, so this check and the
# restore it is preflighting can never be looking at two different folders.
if (-not $Feed) {
    $config = Join-Path $repoRoot 'nuget.config'

    if (Test-Path $config) {
        $declared = ([xml](Get-Content $config -Raw)).configuration.packageSources.add |
            Where-Object { $_.key -eq 'local' } |
            Select-Object -First 1

        if ($declared) { $Feed = Join-Path $repoRoot $declared.value }
    }
}

$feedResolved = if ($Feed -and (Test-Path $Feed)) { (Resolve-Path $Feed).Path } else { $null }

$pins =
    Select-String -Path $Props -Pattern '<PackageVersion\s+Include="(Fuaran\.[A-Za-z.]*)"\s+Version="([^"]+)"' -AllMatches |
        ForEach-Object { $_.Matches } |
        ForEach-Object { [pscustomobject]@{ Id = $_.Groups[1].Value; Version = $_.Groups[2].Value } } |
        Sort-Object Id -Unique

if (-not $pins) {
    Write-Host "check-pins: no Fuaran.* pins declared in $Props — nothing to check."
    exit 0
}

Write-Host "── preflight: pinned Fuaran.* versions ──"
if ($feedResolved) { Write-Host "   local source: $feedResolved" }
else { Write-Host "   local source: none resolved" -ForegroundColor Yellow }

$failures = [System.Collections.Generic.List[string]]::new()
$unverified = 0

foreach ($pin in $pins) {
    $inFeed =
        $null -ne $feedResolved -and
        (Test-Path (Join-Path $feedResolved "$($pin.Id).$($pin.Version).nupkg"))

    if ($Offline) {
        if ($inFeed) {
            Write-Host "   ok(local) $($pin.Id) $($pin.Version)"
        }
        else {
            Write-Host "   MISSING   $($pin.Id) $($pin.Version)   (no local source serves it)" -ForegroundColor Red
            $failures.Add("MISSING $($pin.Id) $($pin.Version)")
        }

        continue
    }

    # 404 is a real answer — this id has never been published. Anything else
    # non-200 is transport, and must not be read as one.
    $status = $null
    $body = $null

    try {
        $lower = $pin.Id.ToLowerInvariant()
        $response =
            Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$lower/index.json" `
                -TimeoutSec 30 -SkipHttpErrorCheck -ErrorAction Stop
        $status = [int] $response.StatusCode
        $body = $response.Content
    }
    catch {
        $status = $null
    }

    $published =
        if ($status -eq 200) { ($body | ConvertFrom-Json).versions -contains $pin.Version }
        elseif ($status -eq 404) { $false }
        else { $null }

    if ($published -eq $true) {
        Write-Host "   ok        $($pin.Id) $($pin.Version)"
    }
    elseif ($null -eq $published) {
        # Transport. The local feed still gives a real answer about THIS machine.
        $unverified++

        if ($inFeed) {
            Write-Host "   ?         $($pin.Id) $($pin.Version)   (registry unreachable; a local source serves it here)" -ForegroundColor Yellow
        }
        else {
            Write-Host "   MISSING   $($pin.Id) $($pin.Version)   (registry unreachable AND no local source serves it — restore floats here)" -ForegroundColor Red
            $failures.Add("MISSING $($pin.Id) $($pin.Version)")
        }
    }
    elseif ($inFeed) {
        Write-Host "   LOCAL-ONLY $($pin.Id) $($pin.Version)   (served only by this machine's local feed; unpublished)" -ForegroundColor Red
        $failures.Add("LOCAL-ONLY $($pin.Id) $($pin.Version)")
    }
    else {
        Write-Host "   MISSING   $($pin.Id) $($pin.Version)   (no source serves it — restore floats here)" -ForegroundColor Red
        $failures.Add("MISSING $($pin.Id) $($pin.Version)")
    }
}

if ($failures.Count -gt 0) {
    $detail = ($failures | ForEach-Object { "    $_" }) -join "`n"

    # Written to stderr and followed by an explicit `exit 1`, NOT raised as a
    # PowerShell error: under `$ErrorActionPreference = 'Stop'` a `Write-Error`
    # terminates the script BEFORE the exit line, and the host then reports
    # success. A gate that prints its own failure and returns 0 is worse than no
    # gate — caught here by running the perturbed case and reading the exit code
    # rather than the output.
    [Console]::Error.WriteLine(@"

FAIL: $($failures.Count) pinned version(s) cannot be served exactly by a restore
path this repository can rely on:

$detail

NuGet does not fail on this. It emits NU1603, substitutes the nearest higher
version, and builds — so the pin you read is not the package you compiled
against, and the substitution surfaces later as something else entirely.

  MISSING     nothing serves this version. Either the pin names a version that
              was never produced, or the producing repository has not packed or
              published it. Correct the pin.
  LOCAL-ONLY  a local folder feed serves it and the registry does not. The local
              feed is machine-local and merely accumulates, so this builds here
              and floats in every fresh clone. Publication is triggered by the
              producing repository's release tag, not by its version bump — and a
              tag is not a publish: check the workflow run and the registry
              listing, since indexing lags the push.
"@)
    exit 1
}

if ($unverified -gt 0) {
    Write-Host "   $($pins.Count) pin(s) checked; $unverified unverified (registry unreachable)." -ForegroundColor Yellow
}
else {
    Write-Host "   all $($pins.Count) pinned version(s) are published and resolve exactly."
}
