# Assemble the final release set under <repo>\release.
#
# Release policy (since after v0.2.1): releases ship the installer executables
# only, together with the per-installer .json sidecar metadata produced by
# build-installer.ps1 and a slim release-manifest.json recording hashes. Source
# archives and FFmpeg corresponding-source offer bundles are no longer attached
# to releases; upstream sources stay available through the Gyan build page and
# the contact channels published with the project (see
# installer\THIRD-PARTY-NOTICES.md).

[CmdletBinding()]
param(
    # Defaults to installer\VERSION when omitted. Windows PowerShell 5.1 cannot
    # use $PSScriptRoot in a parameter default, so it is resolved in the body.
    [string]$Version = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$BuildToolsRoot = Split-Path -Parent $PSScriptRoot
$ProjectRoot = Split-Path -Parent $BuildToolsRoot
$OutputRoot = Join-Path $BuildToolsRoot 'output'
# The published release set lives at the repository root (D:\AMLL\release).
$ReleaseRoot = Join-Path $ProjectRoot 'release'

if (-not $Version) {
    $VersionFile = Join-Path $ProjectRoot 'installer\VERSION'
    if (-not (Test-Path -LiteralPath $VersionFile)) { throw "Missing VERSION file: $VersionFile" }
    $Version = (Get-Content -LiteralPath $VersionFile -Raw).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $Version" }

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

$setupNames = @(
    "AMLL-Lyric-MV-Studio-$Version-x64-Setup.exe",
    "AMLL-Lyric-MV-Studio-$Version-x64-Lite-Setup.exe"
)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
foreach ($name in $setupNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputRoot $name) -PathType Leaf)) {
        throw "Both verified installer variants are required before finalization; missing $name. Build them with build-tools\scripts\build-installer.ps1."
    }
}

$releaseCommit = & git -C $ProjectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or -not $releaseCommit) { throw 'A local release commit is required before finalization.' }
$releaseCommit = [string]$releaseCommit

Remove-Item -LiteralPath $ReleaseRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null

foreach ($name in $setupNames) {
    Copy-Item -LiteralPath (Join-Path $OutputRoot $name) -Destination $ReleaseRoot -Force
    $sidecar = Join-Path $OutputRoot "$name.json"
    if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
        Copy-Item -LiteralPath $sidecar -Destination $ReleaseRoot -Force
    }
}

$releaseFiles = @(Get-ChildItem -LiteralPath $ReleaseRoot -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ file = $_.Name; sizeBytes = $_.Length; sha256 = Get-Sha256 $_.FullName }
})
$releaseManifest = [ordered]@{
    product = 'AMLL Lyric MV Studio'
    version = $Version
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    releaseCommit = $releaseCommit
    releasePolicy = 'installers-only'
    files = $releaseFiles
}
$releaseManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') -Encoding UTF8
Write-Host "Final release set prepared (installers only): $ReleaseRoot"
