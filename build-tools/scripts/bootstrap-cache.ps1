# Populate build-tools\cache with every pinned download a build needs, so
# build-installer.ps1 can run again (including with -Offline) without
# re-downloading. Idempotent: hash-verified files already in the cache are
# kept, anything missing or corrupted is re-downloaded.
#
# Keep the pinned constants in sync with build-installer.ps1. They are
# duplicated on purpose so each script stays independently runnable.

[CmdletBinding()]
param(
    # Skip materializing the Chromium runtime archive (needed only by Full builds).
    [switch]$SkipChromium
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$BuildToolsRoot = Split-Path -Parent $PSScriptRoot
$ProjectRoot = Split-Path -Parent $BuildToolsRoot
$CacheRoot = Join-Path $BuildToolsRoot 'cache'

$NodeVersion = '24.16.0'
$NodeArchive = "node-v$NodeVersion-win-x64.zip"
$NodeUrl = "https://nodejs.org/dist/v$NodeVersion/$NodeArchive"
$NodeSha256 = 'EDACA9BD58EC8E92037DAC4E877D52F6B8F430B81C18B57E264B4E2FB111CD56'
$WebView2Url = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/474c9f1d-0fd5-49da-b3b0-df536d328170/MicrosoftEdgeWebview2Setup.exe'
$WebView2Sha256 = '17DEBF797A6C737959BC588236E897936FFAC1AF5F7E515E674AB32F9EDFE719'
$FfmpegVersion = '8.1.2'
$FfmpegArchive = 'ffmpeg-8.1.2-essentials_build.zip'
$FfmpegUrl = 'https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip'
$FfmpegSha256 = 'DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC'
$ChromiumRevision = '1243'
$ChromiumVersion = '153.0.8010.12'
$ChromiumCacheArchive = "playwright-chromium-$ChromiumRevision-win64.zip"
$InnoVersion = '6.7.3'
$InnoUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$InnoSha256 = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
$PixiColordLicenseUrl = 'https://raw.githubusercontent.com/omgovich/colord/5344fbf77b736f81cd33c21050021bc09bc9dd1d/LICENSE.md'
$PixiColordLicenseSha256 = '7613D4594EE8B6163926AF3435DAE61C9E3D5A27CD137BD76A543BA40002D8FC'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Invoke-Download {
    param(
        [Parameter(Mandatory)] [string]$Uri,
        [Parameter(Mandatory)] [string]$Destination,
        [string]$Sha256,
        [switch]$RequireMicrosoftSignature
    )

    if (Test-Path -LiteralPath $Destination) {
        if (-not $Sha256 -or (Get-Sha256 $Destination) -eq $Sha256.ToUpperInvariant()) {
            Write-Host "Using cached $Destination"
            return
        }
        Write-Warning "Discarding cached file with wrong SHA256: $Destination"
        Remove-Item -LiteralPath $Destination -Force
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    $partial = "$Destination.partial"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    Write-Host "Downloading $Uri"
    # Windows PowerShell 5.1 does not consistently consume HTTP_PROXY itself.
    # Explicitly map HTTP_PROXY/HTTPS_PROXY while retaining the system proxy default.
    $proxyUri = if ($env:HTTPS_PROXY) { $env:HTTPS_PROXY } elseif ($env:HTTP_PROXY) { $env:HTTP_PROXY } else { $null }
    $request = @{
        UseBasicParsing = $true
        Uri = $Uri
        OutFile = $partial
    }
    if ($proxyUri) { $request.Proxy = $proxyUri }
    Invoke-WebRequest @request
    Move-Item -LiteralPath $partial -Destination $Destination -Force

    if ($Sha256) {
        $actual = Get-Sha256 $Destination
        if ($actual -ne $Sha256.ToUpperInvariant()) {
            throw "SHA256 mismatch for $Destination. Expected $Sha256, got $actual"
        }
    }

    if ($RequireMicrosoftSignature) {
        $signature = Get-AuthenticodeSignature -LiteralPath $Destination
        if ($signature.Status -ne 'Valid' -or
            $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw "Microsoft Authenticode verification failed for $Destination ($($signature.Status))"
        }
    }
}

Write-Host "Build cache root: $CacheRoot"
New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null

Invoke-Download -Uri $NodeUrl -Destination (Join-Path $CacheRoot $NodeArchive) -Sha256 $NodeSha256
Invoke-Download -Uri $FfmpegUrl -Destination (Join-Path $CacheRoot $FfmpegArchive) -Sha256 $FfmpegSha256
Invoke-Download -Uri $WebView2Url -Destination (Join-Path $CacheRoot 'MicrosoftEdgeWebview2Setup.exe') `
    -Sha256 $WebView2Sha256 -RequireMicrosoftSignature
Invoke-Download -Uri $InnoUrl -Destination (Join-Path $CacheRoot "innosetup-$InnoVersion.exe") -Sha256 $InnoSha256
Invoke-Download -Uri $PixiColordLicenseUrl -Destination (Join-Path $CacheRoot 'pixi-colord-2.9.6-LICENSE.md') `
    -Sha256 $PixiColordLicenseSha256

if (-not $SkipChromium) {
    # Materialize the pinned Chromium runtime archive used by Full builds from
    # the repository's locked playwright-core, exactly as build-installer.ps1
    # would on a cold cache.
    $chromiumCache = Join-Path $CacheRoot $ChromiumCacheArchive
    if (Test-Path -LiteralPath $chromiumCache) {
        Write-Host "Using cached $chromiumCache"
    } else {
        $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
        if (-not $nodeCommand) { throw 'node.exe is required to download the pinned Playwright Chromium runtime.' }
        $playwrightCli = Join-Path $ProjectRoot 'node_modules\playwright-core\cli.js'
        if (-not (Test-Path -LiteralPath $playwrightCli -PathType Leaf)) {
            throw "playwright-core is not installed; run 'npm ci' in $ProjectRoot first."
        }
        $pwRoot = Join-Path $BuildToolsRoot '.tmp\pw-browsers'
        Remove-Item -LiteralPath $pwRoot -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $pwRoot | Out-Null
        $previousPlaywrightBrowsersPath = $env:PLAYWRIGHT_BROWSERS_PATH
        $env:PLAYWRIGHT_BROWSERS_PATH = $pwRoot
        $env:PLAYWRIGHT_DOWNLOAD_CONNECTION_TIMEOUT = '180000'
        try {
            & $nodeCommand.Source $playwrightCli install chromium --no-shell
            if ($LASTEXITCODE -ne 0) { throw "Playwright Chromium install failed with exit code $LASTEXITCODE" }
            # Prune helper binaries unused by the app (encoding uses cache-external ffmpeg).
            foreach ($stray in @('ffmpeg-*', 'winldd-*')) {
                Get-ChildItem -LiteralPath $pwRoot -Directory -Filter $stray -ErrorAction SilentlyContinue |
                    Remove-Item -Recurse -Force
            }
            $chromiumExecutable = Get-ChildItem -LiteralPath $pwRoot -Filter chrome.exe -File -Recurse | Select-Object -First 1
            if (-not $chromiumExecutable) { throw "Playwright Chromium install did not produce chrome.exe under $pwRoot" }
            if ($chromiumExecutable.VersionInfo.ProductVersion -notmatch "^$([Regex]::Escape($ChromiumVersion))") {
                throw "Chromium version mismatch: $($chromiumExecutable.VersionInfo.ProductVersion)"
            }
            Compress-Archive -Path (Join-Path $pwRoot 'chromium-*') -DestinationPath $chromiumCache -CompressionLevel Optimal -Force
            Write-Host "Cached pinned Chromium runtime: $chromiumCache"
        } finally {
            if ($null -eq $previousPlaywrightBrowsersPath) {
                Remove-Item Env:\PLAYWRIGHT_BROWSERS_PATH -ErrorAction SilentlyContinue
            } else {
                $env:PLAYWRIGHT_BROWSERS_PATH = $previousPlaywrightBrowsersPath
            }
            Remove-Item -LiteralPath $pwRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ''
Write-Host 'Cache contents:'
Get-ChildItem -LiteralPath $CacheRoot -File | ForEach-Object {
    Write-Host ("  {0}  ({1:N0} bytes)" -f $_.Name, $_.Length)
}
