[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.0',
    [switch]$Offline,
    # Windows PowerShell 5.1 cannot use $PSScriptRoot in a parameter default;
    # resolved against $PSScriptRoot in the body when empty.
    [string]$FfmpegSourceBundle = ''
)

if (-not $FfmpegSourceBundle) {
    $FfmpegSourceBundle = Join-Path $PSScriptRoot 'cache\amll-ffmpeg-build-source'
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
# Self-built minimal static FFmpeg distributed with the installers.
$FfmpegBinaryArchive = 'ffmpeg-8.1.2-amll-minimal-gpl.zip'
$FfmpegBinarySha256 = '03DC06C3C02CE318EDAE209D4FD8EE7603AFB554BFE89B60245540A868704C88'
$X264Commit = '0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee'
$X264SourceName = "x264-$X264Commit.tar.gz"
$X264SourceUrl = "https://code.videolan.org/videolan/x264/-/archive/$X264Commit/$X264SourceName"
$X264SourceSha256 = 'D0967A1348C85DFDE363BB52610403BE898171493100561EFA0DD05D5FD1AE50'

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

function Test-FfmpegSourceBundle([string]$BundleRoot) {
    $manifestPath = Join-Path $BundleRoot 'SOURCES-MANIFEST.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "FFmpeg corresponding-source bundle manifest missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.ffmpegVersion -ne $FfmpegVersion) {
        throw "Corresponding-source bundle is for FFmpeg $($manifest.ffmpegVersion), not $FfmpegVersion"
    }
    if (-not $manifest.binary -or -not $manifest.binary.file -or -not $manifest.binary.sha256) {
        throw 'Corresponding-source manifest does not pin the distributed FFmpeg binary archive.'
    }
    foreach ($requiredName in @($manifest.buildScript, 'BUILD-INFORMATION.txt', 'sources.sha256')) {
        if (-not $requiredName -or -not (Test-Path -LiteralPath (Join-Path $BundleRoot $requiredName) -PathType Leaf)) {
            throw "Corresponding-source bundle lacks required entry: $requiredName"
        }
    }
    # Recompute sources.sha256 into a pending set, then verify and consume every entry.
    $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in @(Get-Content -LiteralPath (Join-Path $BundleRoot 'sources.sha256') | Where-Object { $_.Trim() })) {
        if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') { throw "Unparseable sources.sha256 line: $line" }
        $resolvedPath = [IO.Path]::GetFullPath((Join-Path $BundleRoot $Matches[2].Trim()))
        if ($expected.ContainsKey($resolvedPath)) { throw "sources.sha256 lists a duplicate file: $($Matches[2])" }
        $expected.Add($resolvedPath, $Matches[1])
    }
    $resolvedBinary = [IO.Path]::GetFullPath((Join-Path $BundleRoot $manifest.binary.file))
    if ($manifest.binary.sha256 -ne $FfmpegBinarySha256) {
        throw 'Corresponding-source manifest does not pin the distributed FFmpeg binary archive.'
    }
    Assert-Hash $resolvedBinary $FfmpegBinarySha256
    [void]$expected.Remove($resolvedBinary)
    foreach ($requiredName in @($manifest.buildScript, 'BUILD-INFORMATION.txt')) {
        $resolvedPath = [IO.Path]::GetFullPath((Join-Path $BundleRoot $requiredName))
        if (-not $expected.ContainsKey($resolvedPath)) {
            throw "sources.sha256 does not cover required bundle entry: $requiredName"
        }
        Assert-Hash $resolvedPath $expected[$resolvedPath]
        [void]$expected.Remove($resolvedPath)
    }
    foreach ($entry in @($manifest.sources)) {
        if (-not $entry.file -or -not $entry.sha256 -or -not $entry.origin) {
            throw 'Corresponding-source manifest sources entry lacks file/sha256/origin provenance.'
        }
        $resolvedPath = [IO.Path]::GetFullPath((Join-Path $BundleRoot $entry.file))
        if (-not $expected.ContainsKey($resolvedPath)) {
            throw "Manifest source is absent from sources.sha256: $($entry.file)"
        }
        if ($expected[$resolvedPath] -ne $entry.sha256) {
            throw "sources.sha256 and SOURCES-MANIFEST.json disagree for $($entry.file)"
        }
        Assert-Hash $resolvedPath $entry.sha256
        [void]$expected.Remove($resolvedPath)
    }
    if ($expected.Count) {
        throw "sources.sha256 lists files not covered by SOURCES-MANIFEST.json: $([string]::Join(', ', $expected.Keys))"
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
$x264Source = Join-Path $CacheRoot $X264SourceName
Invoke-Download $FfmpegSourceUrl $ffmpegSource $FfmpegSourceSha256
Invoke-Download $FfmpegSignatureUrl $ffmpegSignature $FfmpegSignatureSha256
Invoke-Download $FfmpegKeyUrl $ffmpegKey $FfmpegKeySha256
Invoke-Download $X264SourceUrl $x264Source $X264SourceSha256

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

if (-not (Test-FfmpegSourceBundle $FfmpegSourceBundle)) {
    throw 'Corresponding-source bundle validation failed.'
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

Copy-Item -LiteralPath $ffmpegSource, $ffmpegSignature, $ffmpegKey, $x264Source -Destination $ReleaseRoot -Force
$bundleDestination = Join-Path $ReleaseRoot 'ffmpeg-8.1.2-corresponding-source'
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
        build = 'amll-minimal-gpl (self-built; see ffmpeg-8.1.2-corresponding-source)'
        sourceSignatureFingerprint = $FfmpegKeyFingerprint
        x264Version = '0.165.3223'
        x264Commit = $X264Commit
    }
    files = $releaseFiles
}
$releaseManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') -Encoding UTF8
Write-Host "Final release set prepared: $ReleaseRoot"
