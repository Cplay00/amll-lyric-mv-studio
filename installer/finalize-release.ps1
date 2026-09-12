[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.1',
    [switch]$Offline,
    # Windows PowerShell 5.1 cannot use $PSScriptRoot in a parameter default;
    # resolved against $PSScriptRoot in the body when empty.
    [string]$FfmpegSourceBundle = ''
)

if (-not $FfmpegSourceBundle) {
    $FfmpegSourceBundle = Join-Path $PSScriptRoot 'cache\gyan-build-source'
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$InstallerRoot = $PSScriptRoot
$ProjectRoot = Split-Path -Parent $InstallerRoot
$CacheRoot = Join-Path $InstallerRoot 'cache'
$OutputRoot = Join-Path $InstallerRoot 'output'
# The published release set lives at the repository root (D:\AMLL\release).
$ReleaseRoot = Join-Path $ProjectRoot 'release'
$TempRoot = Join-Path $InstallerRoot '.tmp\release'

$FfmpegVersion = '8.1.2'
$FfmpegSourceName = "ffmpeg-$FfmpegVersion.tar.xz"
$FfmpegSourceUrl = "https://ffmpeg.org/releases/$FfmpegSourceName"
$FfmpegSourceSha256 = '464BEB5E7BF0C311E68B45AE2F04E9CC2AF88851ABB4082231742A74D97B524C'
$FfmpegSignatureName = "$FfmpegSourceName.asc"
$FfmpegSignatureUrl = "$FfmpegSourceUrl.asc"
$FfmpegSignatureSha256 = '0A0963FCCD70597838073F3E31B20F4A4D8CC2B5E577472C9A5A1F22624246F8'
$FfmpegKeyName = 'ffmpeg-devel.asc'
$FfmpegKeyUrl = 'https://ffmpeg.org/ffmpeg-devel.asc'
$FfmpegKeySha256 = '397B3BECEDCD5A98769967FF1FF8501DDC89F8368B8F766E4701377D7DBAABE5'
$FfmpegKeyFingerprint = 'FCF986EA15E6E293A5644F10B4322F04D67658D8'
# Gyan "essentials" prebuilt static FFmpeg distributed with the installers.
# The release ships a corresponding-source OFFER bundle (gyan-build-source)
# that pins the exact binary archive plus every resolvable upstream source.
$FfmpegBinaryArchive = 'ffmpeg-8.1.2-essentials_build.zip'
$FfmpegBinarySha256 = 'DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required artifact missing: $Path" }
    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected.ToUpperInvariant()) { throw "SHA256 mismatch for $Path. Expected $Expected, got $actual" }
}

function Invoke-Download([string]$Uri, [string]$Destination, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Destination) -and (Get-Sha256 $Destination) -ne $Sha256) {
        Remove-Item -LiteralPath $Destination -Force
    }
    if (-not (Test-Path -LiteralPath $Destination)) {
        if ($Offline) { throw "Offline mode: required cached artifact missing: $Destination" }
        $partial = "$Destination.partial"
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        $request = @{ UseBasicParsing = $true; Uri = $Uri; OutFile = $partial }
        $proxyUri = if ($env:HTTPS_PROXY) { $env:HTTPS_PROXY } elseif ($env:HTTP_PROXY) { $env:HTTP_PROXY } else { $null }
        if ($proxyUri) { $request.Proxy = $proxyUri }
        Invoke-WebRequest @request
        Move-Item -LiteralPath $partial -Destination $Destination -Force
    }
    Assert-Hash $Destination $Sha256
}

function Get-CanonicalRelativePath([string]$Path) {
    return $Path.Substring($ProjectRoot.Length).TrimStart('\').Replace('\', '/')
}

function Test-SourceExcluded([string]$Relative) {
    $segments = $Relative.Split('/')
    $excludedNames = @('.git', 'node_modules', 'dist', 'bin', 'obj', 'artifacts', 'TestResults', 'coverage', 'out', 'frames', '.vs', '.mimosa', '.playwright-cli')
    if (@($segments | Where-Object { $_ -in $excludedNames }).Count) { return $true }
    if ($Relative -match '^installer/(cache|staging|output|\.tmp|release)(/|$)') { return $true }
    # Workspace-local helper scratch directories injected by the development
    # environment; they are not part of the product's source tree.
    if ($Relative -match '^(_agent_workspace|\.agent_workspace|\.tmp)(/|$)') { return $true }
    if ($Relative -match '(^|/)(\.env($|\.)|.*\.(log|user|suo|tsbuildinfo|partial)$)') { return $true }
    if ($Relative -match '(^|/)(secrets?|credentials?)(\.|/|$)') { return $true }
    return $false
}

function Test-FfmpegSourceOffer([string]$BundleRoot) {
    # The distributed FFmpeg binary is the Gyan "essentials" prebuilt. A static
    # link cannot be reproduced from this repository, so GPL compliance ships a
    # corresponding-source OFFER: a manifest pinning the exact binary archive,
    # the authenticated upstream FFmpeg archive, and every resolvable upstream
    # library source (url + sha256), with unresolvable components documented.
    $manifestPath = Join-Path $BundleRoot 'SOURCES-MANIFEST.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "FFmpeg source-offer manifest missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.ffmpegVersion -ne $FfmpegVersion) {
        throw "Source-offer bundle is for FFmpeg $($manifest.ffmpegVersion), not $FfmpegVersion"
    }
    foreach ($required in @('gyanReadmeSha256', 'binary', 'resolved')) {
        if (-not $manifest.PSObject.Properties[$required]) {
            throw "Source-offer manifest lacks required field: $required"
        }
    }
    if (-not $manifest.binary.file -or -not $manifest.binary.sha256) {
        throw 'Source-offer manifest does not pin the distributed FFmpeg binary archive.'
    }
    if ($manifest.binary.sha256 -ne $FfmpegBinarySha256) {
        throw 'Source-offer manifest does not pin the distributed FFmpeg binary archive hash.'
    }
    $resolvedBinary = [IO.Path]::GetFullPath((Join-Path $BundleRoot $manifest.binary.file))
    Assert-Hash $resolvedBinary $FfmpegBinarySha256

    # Tie the manifest to the exact binary: hash the Gyan build README carried
    # inside the distributed archive.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $readmeTemp = Join-Path $TempRoot 'ffmpeg-offer-readme'
    Remove-Item -LiteralPath $readmeTemp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $readmeTemp | Out-Null
    try {
        $binaryZip = Get-Item -LiteralPath $resolvedBinary
        $zip = [IO.Compression.ZipFile]::OpenRead($binaryZip.FullName)
        try {
            $readmeEntry = $zip.Entries | Where-Object { $_.FullName -match '(^|/)README\.txt$' } | Select-Object -First 1
            if (-not $readmeEntry) { throw 'Distributed FFmpeg archive does not carry a README.txt build description.' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($readmeEntry, (Join-Path $readmeTemp 'README.txt'), $true)
        } finally { $zip.Dispose() }
        Assert-Hash (Join-Path $readmeTemp 'README.txt') $manifest.gyanReadmeSha256
    } finally {
        Remove-Item -LiteralPath $readmeTemp -Recurse -Force -ErrorAction SilentlyContinue
    }

    foreach ($entry in @($manifest.resolved)) {
        if (-not $entry.PSObject.Properties['library'] -or -not $entry.PSObject.Properties['url'] -or -not $entry.PSObject.Properties['sha256']) {
            throw 'Source-offer resolved entry lacks library/url/sha256 provenance.'
        }
        if ($entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "Source-offer resolved entry has a malformed sha256: $($entry.library)"
        }
    }
    if (@($manifest.resolved).Count -lt 10) {
        throw 'Source-offer manifest resolves implausibly few upstream sources.'
    }
    foreach ($blocker in @($manifest.blockers)) {
        if (-not $blocker.PSObject.Properties['library'] -or -not $blocker.PSObject.Properties['reason']) {
            throw 'Source-offer blockers entry lacks library/reason documentation.'
        }
    }
    return $true
}

New-Item -ItemType Directory -Force -Path $CacheRoot, $OutputRoot | Out-Null
foreach ($name in @(
    "AMLL-Lyric-MV-Studio-$Version-x64-Setup.exe",
    "AMLL-Lyric-MV-Studio-$Version-x64-Lite-Setup.exe"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputRoot $name) -PathType Leaf)) {
        throw "Both verified installer variants are required before finalization; missing $name"
    }
}

$ffmpegSource = Join-Path $CacheRoot $FfmpegSourceName
$ffmpegSignature = Join-Path $CacheRoot $FfmpegSignatureName
$ffmpegKey = Join-Path $CacheRoot $FfmpegKeyName
Invoke-Download $FfmpegSourceUrl $ffmpegSource $FfmpegSourceSha256
Invoke-Download $FfmpegSignatureUrl $ffmpegSignature $FfmpegSignatureSha256
Invoke-Download $FfmpegKeyUrl $ffmpegKey $FfmpegKeySha256

$gpg = Get-Command gpg.exe -ErrorAction SilentlyContinue
if (-not $gpg) { $gpg = Get-Command gpg -ErrorAction SilentlyContinue }
if (-not $gpg) { throw 'GnuPG is required to authenticate the FFmpeg source archive.' }
$gpgHome = Join-Path $TempRoot 'gnupg'
Remove-Item -LiteralPath $gpgHome -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $gpgHome | Out-Null
# MSYS/Git-Bash gpg builds require a POSIX-style GNUPGHOME; native GnuPG wants the Windows path.
if ($gpg.Source -match 'GnuPG\\bin\\gpg\.exe$') { $gpgHomeForGpg = $gpgHome } else {
    $gpgHomeForGpg = if ($gpgHome -match '^([A-Za-z]):\\(.*)$') {
        '/' + $Matches[1].ToLowerInvariant() + '/' + ($Matches[2] -replace '\\', '/')
    } else { $gpgHome }
}
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$previousGnupgHome = $env:GNUPGHOME
$env:GNUPGHOME = $gpgHomeForGpg
try {
    & $gpg.Source --batch --import $ffmpegKey 2>$null
    $importExit = $LASTEXITCODE
    $status = & $gpg.Source --batch --status-fd 1 --verify $ffmpegSignature $ffmpegSource 2>$null
    $verifyExit = $LASTEXITCODE
} finally {
    if ($null -eq $previousGnupgHome) { Remove-Item Env:\GNUPGHOME -ErrorAction SilentlyContinue } else { $env:GNUPGHOME = $previousGnupgHome }
    $ErrorActionPreference = $previousEap
}
if ($importExit -ne 0) { throw 'FFmpeg release key import failed.' }
if ($verifyExit -ne 0 -or -not @($status | Where-Object { $_ -match "\[GNUPG:\] VALIDSIG $FfmpegKeyFingerprint" })) {
    throw 'FFmpeg source signature did not validate with the pinned release key fingerprint.'
}

if (-not (Test-FfmpegSourceOffer $FfmpegSourceBundle)) {
    throw 'FFmpeg source-offer bundle validation failed.'
}

Remove-Item -LiteralPath $ReleaseRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null

# The application source archive is produced with git archive from the local
# release commit: deterministic, complete (every tracked file), and free of
# build outputs and workspace scratch by construction of .gitignore.
$releaseCommit = & git -C $ProjectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or -not $releaseCommit) { throw 'A local release commit is required before finalization.' }
$releaseCommit = [string]$releaseCommit
$appSourceZip = Join-Path $ReleaseRoot "AMLL-Lyric-MV-Studio-$Version-source.zip"
& git -C $ProjectRoot archive --format=zip "--output=$appSourceZip" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Application source archive creation (git archive) failed.' }
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tar)) { throw 'Windows tar.exe is required to inspect the source zip.' }
$zipListing = & $tar -tf $appSourceZip
if ($LASTEXITCODE -ne 0) { throw 'Application source archive is not readable.' }
foreach ($required in @('package.json', 'package-lock.json', 'vite.config.ts', 'installer/build-installer.ps1', 'installer/finalize-release.ps1', 'src/App.tsx', 'scripts/render-mv.mjs')) {
    $entry = "./$required"
    if (@($zipListing | Where-Object { $_ -eq $entry -or $_ -eq $required }).Count -eq 0) {
        throw "Application source archive omits tracked file: $required"
    }
}

Copy-Item -LiteralPath $ffmpegSource, $ffmpegSignature, $ffmpegKey -Destination $ReleaseRoot -Force
$bundleDestination = Join-Path $ReleaseRoot 'ffmpeg-8.1.2-source-offer'
Remove-Item -LiteralPath $bundleDestination -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bundleDestination | Out-Null
Copy-Item -Path (Join-Path $FfmpegSourceBundle '*') -Destination $bundleDestination -Recurse -Force
Get-ChildItem -LiteralPath $OutputRoot -File | Where-Object {
    $_.Name -match "^AMLL-Lyric-MV-Studio-$([Regex]::Escape($Version))-x64-(Lite-)?Setup\.exe(\.json)?$"
} | Copy-Item -Destination $ReleaseRoot -Force

$releaseFiles = @(Get-ChildItem -LiteralPath $ReleaseRoot -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ file = $_.Name; sizeBytes = $_.Length; sha256 = Get-Sha256 $_.FullName }
})
$releaseManifest = [ordered]@{
    product = 'AMLL Lyric MV Studio'
    version = $Version
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    releaseCommit = $releaseCommit
    ffmpeg = [ordered]@{
        version = $FfmpegVersion
        binaryArchive = $FfmpegBinaryArchive
        binarySha256 = $FfmpegBinarySha256
        build = 'Gyan essentials 8.1.2 (prebuilt; corresponding-source offer in ffmpeg-8.1.2-source-offer)'
        sourceSignatureFingerprint = $FfmpegKeyFingerprint
    }
    files = $releaseFiles
}
$releaseManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') -Encoding UTF8
Write-Host "Final release set prepared: $ReleaseRoot"
