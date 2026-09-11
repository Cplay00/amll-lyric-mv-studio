[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,
    [switch]$InstallTooling,
    [switch]$SkipInstaller,
    [switch]$SkipVerify,
    [switch]$Offline,
    # Lite omits Chromium and renders through an installed Edge/Chrome browser.
    [switch]$Lite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$InstallerRoot = $PSScriptRoot
$ProjectRoot = Split-Path -Parent $InstallerRoot
$StagingRoot = Join-Path $InstallerRoot 'staging'
$ContentRoot = Join-Path $StagingRoot 'runtime\content'
$CacheRoot = Join-Path $InstallerRoot 'cache'
$OutputRoot = Join-Path $InstallerRoot 'output'
$TempRoot = Join-Path $InstallerRoot '.tmp'
$VersionFile = Join-Path $InstallerRoot 'VERSION'

# Pin every downloaded build input. Update THIRD-PARTY-NOTICES.md with these values.
$NodeVersion = '24.16.0'
$NodeArchive = "node-v$NodeVersion-win-x64.zip"
$NodeUrl = "https://nodejs.org/dist/v$NodeVersion/$NodeArchive"
$NodeSha256 = 'EDACA9BD58EC8E92037DAC4E877D52F6B8F430B81C18B57E264B4E2FB111CD56'
$WebView2Url = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/474c9f1d-0fd5-49da-b3b0-df536d328170/MicrosoftEdgeWebview2Setup.exe'
$WebView2Sha256 = '17DEBF797A6C737959BC588236E897936FFAC1AF5F7E515E674AB32F9EDFE719'
$FfmpegVersion = '8.1.2'
$FfmpegArchive = "ffmpeg-$FfmpegVersion-essentials_build.zip"
$FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/packages/$FfmpegArchive"
$FfmpegSha256 = 'DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC'
$ChromiumRevision = '1243'
$ChromiumVersion = '153.0.8010.12'
$ChromiumCacheArchive = "playwright-chromium-$ChromiumRevision-win64.zip"
$InnoVersion = '6.7.3'
$InnoUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$InnoSha256 = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Assert-Command([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) { throw "Required command not found: $Name" }
    return $command.Source
}

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
        } else {
            Write-Warning "Discarding cached file with wrong SHA256: $Destination"
            Remove-Item -LiteralPath $Destination -Force
        }
    }

    if (-not (Test-Path -LiteralPath $Destination)) {
        if ($Offline) { throw "Offline mode: missing cached download $Destination" }
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
    }

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

function Copy-Tree([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Missing source: $Source" }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
}

function Copy-NpmPackage([string]$Name) {
    $source = Join-Path (Join-Path $ProjectRoot 'node_modules') $Name
    $destination = Join-Path (Join-Path $ContentRoot 'node_modules') $Name
    Copy-Tree $source $destination
}

function Get-RequiredNpmPackages([string[]]$Roots) {
    $lockPath = Join-Path $ProjectRoot 'package-lock.json'
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $nodeCommand) { throw 'node.exe is required to resolve the package-lock runtime closure.' }
    $resolver = @'
const lock = require(process.argv[1]);
const result = new Set(), queue = process.argv.slice(2);
while (queue.length) {
  const name = queue.shift();
  if (result.has(name)) continue;
  result.add(name);
  const entry = lock.packages[`node_modules/${name}`];
  if (!entry) throw new Error(`Package ${name} is absent from package-lock.json`);
  queue.push(...Object.keys(entry.dependencies || {}));
}
result.add('@esbuild/win32-x64');
result.add('@rollup/rollup-win32-x64-msvc');
process.stdout.write([...result].sort().join('\n'));
'@
    $resolved = & $nodeCommand.Source -e $resolver $lockPath @Roots
    if ($LASTEXITCODE -ne 0) { throw "Runtime npm closure resolution failed with exit code $LASTEXITCODE" }
    return @($resolved | Where-Object { $_ })
}

function Find-Iscc {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:ISCC_PATH) { $candidates.Add($env:ISCC_PATH) }
    if (${env:ProgramFiles(x86)}) { $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')) }
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) }
    if ($env:ChocolateyInstall) { $candidates.Add((Join-Path $env:ChocolateyInstall 'bin\ISCC.exe')) }
    $found = @($candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($found.Count -gt 0) { return $found[0] }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Install-InnoSetup {
    Write-Step "Installing Inno Setup $InnoVersion (explicit -InstallTooling request)"
    Write-Host 'This build installs Inno Setup for the current user only; it does not silently request elevation.'

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($winget) {
        & $winget.Source install --exact --id JRSoftware.InnoSetup --source winget --scope user --silent `
            --disable-interactivity --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -eq 0 -and (Find-Iscc)) { return }
        Write-Warning "winget user-scope installation did not make ISCC available (exit $LASTEXITCODE); trying Chocolatey/upstream."
    }

    $choco = Get-Command choco.exe -ErrorAction SilentlyContinue
    if ($choco) {
        & $choco.Source install innosetup --yes --no-progress --limit-output
        if ($LASTEXITCODE -eq 0 -and (Find-Iscc)) { return }
        Write-Warning "Chocolatey did not make ISCC available (exit $LASTEXITCODE); trying the signed upstream installer."
    }

    $installer = Join-Path $CacheRoot "innosetup-$InnoVersion.exe"
    Invoke-Download -Uri $InnoUrl -Destination $installer -Sha256 $InnoSha256
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
        throw "Inno Setup Authenticode verification failed: $($signature.Status)"
    }
    & $installer /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /NORESTART
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup current-user installation failed with exit code $LASTEXITCODE. Run '$installer /CURRENTUSER' interactively; no system-wide install was attempted."
    }
    if (-not (Find-Iscc)) { throw 'Inno Setup reported success, but ISCC.exe is still unavailable.' }
}

if (-not $Version) {
    if (-not (Test-Path -LiteralPath $VersionFile)) { throw "Missing VERSION file: $VersionFile" }
    $Version = (Get-Content -LiteralPath $VersionFile -Raw).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid version: $Version" }
# A four-part input would become an invalid five-part FileVersion after appending .0.
if ($Version.Split('.').Count -gt 3) { throw "Version must be three segments (x.y.z); got: $Version" }

New-Item -ItemType Directory -Force -Path $CacheRoot, $OutputRoot, $TempRoot | Out-Null

Write-Step 'Clean npm install and web build'
# npm depends on the caller CWD, so always execute it from the repository root.
Push-Location $ProjectRoot
try {
    $npm = Assert-Command 'npm.cmd'
    & $npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE" }
    & $npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }

Write-Step 'Publishing x64 self-contained GUI'
$dotnet = Assert-Command 'dotnet.exe'
$publishDir = Join-Path $TempRoot 'gui-publish'
Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    $publishArgs = @(
        'publish', (Join-Path $ProjectRoot 'gui\AmllMvStudio.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:Platform=x64', '-p:WindowsAppSDKSelfContained=true',
        "-p:Version=$Version", "-p:FileVersion=$Version.0", "-p:InformationalVersion=$Version",
        '-o', $publishDir
    )
    $publishSucceeded = $false
    for ($attempt = 1; $attempt -le 3 -and -not $publishSucceeded; $attempt++) {
        & $dotnet @publishArgs
        $publishSucceeded = ($LASTEXITCODE -eq 0)
        if (-not $publishSucceeded -and $attempt -lt 3) {
            Write-Warning "dotnet publish failed; retrying NuGet restore in $($attempt * 5) seconds..."
            Start-Sleep -Seconds ($attempt * 5)
        }
    }
    if (-not $publishSucceeded) { throw "dotnet publish failed after 3 attempts" }

Write-Step 'Downloading and verifying pinned runtimes'
$nodeZip = Join-Path $CacheRoot $NodeArchive
$ffmpegZip = Join-Path $CacheRoot $FfmpegArchive
$webViewInstaller = Join-Path $CacheRoot 'MicrosoftEdgeWebview2Setup.exe'
Invoke-Download -Uri $NodeUrl -Destination $nodeZip -Sha256 $NodeSha256
Invoke-Download -Uri $FfmpegUrl -Destination $ffmpegZip -Sha256 $FfmpegSha256
Invoke-Download -Uri $WebView2Url -Destination $webViewInstaller `
    -Sha256 $WebView2Sha256 -RequireMicrosoftSignature

Write-Step 'Assembling clean staging tree'
if (Test-Path -LiteralPath $StagingRoot) {
    Remove-Item -LiteralPath $StagingRoot -Recurse -Force -ErrorAction Stop
}
New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null
Copy-Tree $publishDir $StagingRoot
Remove-Item -LiteralPath (Join-Path $StagingRoot 'runtime\content') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $ContentRoot 'scripts') | Out-Null
Copy-Tree (Join-Path $ProjectRoot 'dist') (Join-Path $ContentRoot 'dist')
foreach ($scriptName in @('render-mv.mjs', 'render-lib.mjs')) {
    Copy-Item -LiteralPath (Join-Path $ProjectRoot "scripts\$scriptName") `
        -Destination (Join-Path $ContentRoot "scripts\$scriptName") -Force
}
# The repository renderer serves its source root during development. The packaged
# entry keeps the same real implementation but serves the canonical built dist tree.
$stagedRender = Join-Path $ContentRoot 'scripts\render-mv.mjs'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$renderText = [IO.File]::ReadAllText($stagedRender, $utf8NoBom)
$renderSourceRoot = 'const PROJECT_ROOT = resolve(dirname(SCRIPT_PATH), "..");'
$renderDistRoot = 'const PROJECT_ROOT = resolve(dirname(SCRIPT_PATH), "..", "dist");'
if (-not $renderText.Contains($renderSourceRoot)) { throw 'Packaged renderer root marker was not found.' }
$renderText = $renderText.Replace($renderSourceRoot, $renderDistRoot)
$spaMarker = 'root: PROJECT_ROOT,'
if (-not $renderText.Contains($spaMarker)) { throw 'Packaged renderer vite root marker was not found.' }
$renderText = $renderText.Replace($spaMarker, "root: PROJECT_ROOT,`n      appType: `"spa`",")
[IO.File]::WriteAllText($stagedRender, $renderText, $utf8NoBom)

$nodeTemp = Join-Path $TempRoot 'node'
Remove-Item -LiteralPath $nodeTemp -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath $nodeZip -DestinationPath $nodeTemp -Force
$nodeExtracted = Get-ChildItem -LiteralPath $nodeTemp -Directory | Select-Object -First 1
Copy-Tree $nodeExtracted.FullName (Join-Path $StagingRoot 'runtime\node')

$runtimePackages = Get-RequiredNpmPackages @('vite', 'playwright-core')
foreach ($package in $runtimePackages) { Copy-NpmPackage $package }
Write-Host "Packaged $($runtimePackages.Count) runtime npm packages: $($runtimePackages -join ', ')"

$ffmpegTemp = Join-Path $TempRoot 'ffmpeg'
Remove-Item -LiteralPath $ffmpegTemp -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath $ffmpegZip -DestinationPath $ffmpegTemp -Force
$ffmpegSource = Get-ChildItem -LiteralPath $ffmpegTemp -Filter ffmpeg.exe -File -Recurse | Select-Object -First 1
if (-not $ffmpegSource) { throw "ffmpeg.exe was not found in $FfmpegArchive" }
$ffmpegDestination = Join-Path $StagingRoot 'runtime\ffmpeg\ffmpeg.exe'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ffmpegDestination) | Out-Null
Copy-Item -LiteralPath $ffmpegSource.FullName -Destination $ffmpegDestination -Force

# Keep the npm package layout used by render-mv.mjs and also expose a stable runtime path.
Copy-Item -LiteralPath (Join-Path $InstallerRoot 'THIRD-PARTY-NOTICES.md') `
    -Destination (Join-Path $StagingRoot 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $InstallerRoot 'LICENSE.txt') `
    -Destination (Join-Path $StagingRoot 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $InstallerRoot 'NOTICE.txt') `
    -Destination (Join-Path $StagingRoot 'NOTICE.txt') -Force
Copy-Item -LiteralPath (Join-Path $InstallerRoot 'COPYING.GPLv3.txt') `
    -Destination (Join-Path $StagingRoot 'COPYING.GPLv3.txt') -Force
Copy-Item -LiteralPath $VersionFile -Destination (Join-Path $StagingRoot 'VERSION') -Force
$webViewDestination = Join-Path $StagingRoot 'runtime\webview2\MicrosoftEdgeWebview2Setup.exe'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $webViewDestination) | Out-Null
Copy-Item -LiteralPath $webViewInstaller -Destination $webViewDestination -Force

# Preserve exact third-party legal texts alongside the aggregate notice.
$legalRoot = Join-Path $StagingRoot 'licenses'
New-Item -ItemType Directory -Force -Path $legalRoot | Out-Null
$legalFiles = @{
    'Node-LICENSE.txt' = Join-Path $StagingRoot 'runtime\node\LICENSE'
    'Playwright-LICENSE.txt' = Join-Path $ProjectRoot 'node_modules\playwright-core\LICENSE'
    'Playwright-NOTICE.txt' = Join-Path $ProjectRoot 'node_modules\playwright-core\NOTICE'
    'Playwright-ThirdPartyNotices.txt' = Join-Path $ProjectRoot 'node_modules\playwright-core\ThirdPartyNotices.txt'
    'Vite-LICENSE.txt' = Join-Path $ProjectRoot 'node_modules\vite\LICENSE.md'
    'esbuild-LICENSE.txt' = Join-Path $ProjectRoot 'node_modules\esbuild\LICENSE.md'
    'rollup-LICENSE.txt' = Join-Path $ProjectRoot 'node_modules\rollup\LICENSE.md'
    'postcss-LICENSE.txt' = Join-Path $ProjectRoot 'node_modules\postcss\LICENSE'
    'nanoid-LICENSE.txt' = Join-Path $ProjectRoot 'node_modules\nanoid\LICENSE'
    'picocolors-LICENSE.txt' = Join-Path $ProjectRoot 'node_modules\picocolors\LICENSE'
    'source-map-js-LICENSE.txt' = Join-Path $ProjectRoot 'node_modules\source-map-js\LICENSE'
    'WindowsAppSDK-LICENSE.txt' = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk\1.8.260804001\license.txt'
    'WindowsAppSDK-NOTICE.txt' = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk\1.8.260804001\NOTICE.txt'
    'WebView2-SDK-LICENSE.txt' = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2\1.0.4191.47\LICENSE.txt'
    'WebView2-SDK-NOTICE.txt' = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2\1.0.4191.47\NOTICE.txt'
}
foreach ($entry in $legalFiles.GetEnumerator()) {
    if (Test-Path -LiteralPath $entry.Value) {
        Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $legalRoot $entry.Key) -Force
    } else {
        Write-Warning "Legal file not found: $($entry.Value)"
    }
}

Write-Step 'Installing pinned Playwright Chromium into staging'
$previousPlaywrightBrowsersPath = $env:PLAYWRIGHT_BROWSERS_PATH
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $StagingRoot 'runtime\browsers'
$env:PLAYWRIGHT_DOWNLOAD_CONNECTION_TIMEOUT = '180000'
$playwrightCli = Join-Path $ContentRoot 'node_modules\playwright-core\cli.js'
$stagedNode = Join-Path $StagingRoot 'runtime\node\node.exe'
$chromiumCache = Join-Path $CacheRoot $ChromiumCacheArchive
$chromiumExecutable = $null
if ($Lite) {
    Write-Host 'Lite variant: skipping bundled Chromium; renderer falls back to system Edge/Chrome.'
} elseif (Test-Path -LiteralPath $chromiumCache) {
    Write-Host "Using cached $chromiumCache"
    New-Item -ItemType Directory -Force -Path $env:PLAYWRIGHT_BROWSERS_PATH | Out-Null
    $tarPath = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tarPath)) { throw 'Windows tar.exe is required to restore the cached Chromium archive.' }
    & $tarPath -xf $chromiumCache -C $env:PLAYWRIGHT_BROWSERS_PATH
    if ($LASTEXITCODE -ne 0) { throw "Chromium cache extraction failed with exit code $LASTEXITCODE" }
} elseif ($Offline) {
    throw "Offline mode: missing cached Chromium archive $chromiumCache. Run once without -Offline to populate installer/cache."
} else {
    & $stagedNode $playwrightCli install chromium --no-shell
    if ($LASTEXITCODE -ne 0) { throw "Playwright Chromium install failed with exit code $LASTEXITCODE" }
    Compress-Archive -Path (Join-Path $env:PLAYWRIGHT_BROWSERS_PATH 'chromium-*') -DestinationPath $chromiumCache -CompressionLevel Optimal -Force
    Write-Host "Cached pinned Chromium runtime: $chromiumCache"
}
if (-not $Lite) {
    # Prune Playwright helper binaries that are unused by the app; encoding uses runtime/ffmpeg.
    # This also avoids shipping LGPL binaries without their complete license material.
    foreach ($stray in @('ffmpeg-*', 'winldd-*')) {
        Get-ChildItem -LiteralPath $env:PLAYWRIGHT_BROWSERS_PATH -Directory -Filter $stray -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force
    }
    $chromiumExecutable = Get-ChildItem -LiteralPath $env:PLAYWRIGHT_BROWSERS_PATH -Filter chrome.exe -File -Recurse | Select-Object -First 1
    if (-not $chromiumExecutable) { throw "Cached browser archive does not contain chrome.exe: $chromiumCache" }
    if ($chromiumExecutable.VersionInfo.ProductVersion -notmatch "^$([Regex]::Escape($ChromiumVersion))") {
        throw "Cached Chromium version mismatch: $($chromiumExecutable.VersionInfo.ProductVersion)"
    }
}

$browserLicenseRoot = Join-Path $legalRoot 'browser'
New-Item -ItemType Directory -Force -Path $browserLicenseRoot | Out-Null
# Chrome for Testing provides only ABOUT -> chrome://credits, not a license text.
# Ship the vendored official Chromium license from installer/legal.
$chromiumLicenseSource = Join-Path $InstallerRoot 'legal\Chromium-LICENSE.txt'
if (Test-Path -LiteralPath $chromiumLicenseSource) {
    Copy-Item -LiteralPath $chromiumLicenseSource -Destination (Join-Path $browserLicenseRoot 'Chromium-LICENSE.txt') -Force
    Copy-Item -LiteralPath $chromiumLicenseSource -Destination (Join-Path $StagingRoot 'licenses\Chromium-LICENSE.txt') -Force
} else {
    Write-Warning "Chromium license text missing: $chromiumLicenseSource"
}
if (-not $Lite) {
    $browserLegalFiles = Get-ChildItem -LiteralPath $env:PLAYWRIGHT_BROWSERS_PATH -Recurse -File |
        Where-Object { $_.Name -match '^(LICENSE|LICENSES|NOTICE|ThirdPartyNotices|COPYING)(\..*)?$' }
    foreach ($file in $browserLegalFiles) {
        $relative = $file.FullName.Substring($env:PLAYWRIGHT_BROWSERS_PATH.Length).TrimStart('\')
        $destination = Join-Path $browserLicenseRoot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}

$runtimeFileHashes = [ordered]@{
    renderMv = Get-Sha256 (Join-Path $ContentRoot 'scripts\render-mv.mjs')
    renderLib = Get-Sha256 (Join-Path $ContentRoot 'scripts\render-lib.mjs')
    nodeExe = Get-Sha256 (Join-Path $StagingRoot 'runtime\node\node.exe')
    ffmpegExe = Get-Sha256 $ffmpegDestination
}
if (-not $Lite) {
    $runtimeFileHashes['chromiumExe'] = Get-Sha256 ((Get-ChildItem -LiteralPath $env:PLAYWRIGHT_BROWSERS_PATH -Filter chrome.exe -File -Recurse | Select-Object -First 1).FullName)
}
$nodeActualVersion = [string](& (Join-Path $StagingRoot 'runtime\node\node.exe') --version)
$nodeActualVersion = $nodeActualVersion.Trim().TrimStart('v')
$ffmpegActualVersion = ((& $ffmpegDestination -version 2>&1 | Select-Object -First 1) -replace '^ffmpeg version\s+', '').Split(' ')[0]
if (-not $Lite) {
    $chromiumExecutable = (Get-ChildItem -LiteralPath $env:PLAYWRIGHT_BROWSERS_PATH -Filter chrome.exe -File -Recurse | Select-Object -First 1).FullName
    # Chrome for Testing may open a persistent process for --version on Windows.
    # Read signed executable metadata instead to avoid leaking staging locks.
    $chromiumActualVersion = (Get-Item -LiteralPath $chromiumExecutable).VersionInfo.ProductVersion
}
$manifest = [ordered]@{
    product = 'AMLL MV Studio'
    variant = if ($Lite) { 'lite' } else { 'full' }
    version = $Version
    architecture = 'x64'
    contentRoot = 'runtime/content'
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    node = [ordered]@{ version = $NodeVersion; actualVersion = $nodeActualVersion; archive = $NodeArchive; sha256 = $NodeSha256; url = $NodeUrl }
    webView2Bootstrapper = [ordered]@{ sha256 = $WebView2Sha256; url = $WebView2Url }
    playwright = [ordered]@{
        version = '1.63.0'
        chromium = $ChromiumVersion
        actualChromiumVersion = if ($Lite) { $null } else { $chromiumActualVersion }
        revision = $ChromiumRevision
        bundled = (-not $Lite)
        cacheArchive = $ChromiumCacheArchive
        cacheSha256 = Get-Sha256 $chromiumCache
    }
    ffmpeg = [ordered]@{ version = $FfmpegVersion; actualVersion = $ffmpegActualVersion; archive = $FfmpegArchive; sha256 = $FfmpegSha256; url = $FfmpegUrl; license = 'GPLv3'; libx264 = $true }
    renderScripts = [ordered]@{ entry = 'runtime/content/scripts/render-mv.mjs'; library = 'runtime/content/scripts/render-lib.mjs' }
    fileSha256 = $runtimeFileHashes
    runtimeNpmPackages = $runtimePackages
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $StagingRoot 'package-manifest.json') -Encoding UTF8

if (-not $SkipVerify) {
    Write-Step 'Verifying standalone staging package'
    $verifyScript = Join-Path $InstallerRoot 'verify-package.ps1'
    $verifyArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $verifyScript, '-PackageRoot', $StagingRoot)
    if ($Lite) { $verifyArgs += '-SystemBrowser' }
    & powershell @verifyArgs
    if ($LASTEXITCODE -ne 0) { throw "Package verification failed with exit code $LASTEXITCODE" }
}

if (-not $SkipInstaller) {
    Write-Step 'Compiling Inno Setup installer'
    $iscc = Find-Iscc
    if (-not $iscc -and $InstallTooling) {
        Install-InnoSetup
        $iscc = Find-Iscc
    }
    if (-not $iscc) {
        $official = "winget install --exact --id JRSoftware.InnoSetup --source winget --scope user"
        throw "ISCC.exe was not found. Rerun this script with -InstallTooling, or run '$official' and retry. Staging is complete at $StagingRoot."
    }
    # Restore the caller's environment after staging and verification.
    if ($null -eq $previousPlaywrightBrowsersPath) {
        Remove-Item Env:\PLAYWRIGHT_BROWSERS_PATH -ErrorAction SilentlyContinue
    } else {
        $env:PLAYWRIGHT_BROWSERS_PATH = $previousPlaywrightBrowsersPath
    }
    $isccArgs = @("/DMyAppVersion=$Version", (Join-Path $InstallerRoot 'AMLL-MV-Studio.iss'))
    if ($Lite) { $isccArgs = @("/DMyAppFlavor=Lite") + $isccArgs }
    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
    $setupName = if ($Lite) { "AMLL-MV-Studio-$Version-x64-Lite-Setup.exe" } else { "AMLL-MV-Studio-$Version-x64-Setup.exe" }
    $setupPath = Join-Path $OutputRoot $setupName
    if (-not (Test-Path -LiteralPath $setupPath)) { throw "ISCC succeeded, but installer is missing: $setupPath" }
    $setupItem = Get-Item -LiteralPath $setupPath
    if ($setupItem.Length -lt 1MB) { throw "Installer is unexpectedly small: $($setupItem.Length) bytes" }
    $setupSignature = Get-AuthenticodeSignature -LiteralPath $setupPath
    $setupMetadata = [ordered]@{
        file = $setupItem.Name
        variant = if ($Lite) { 'lite' } else { 'full' }
        bundledChromium = (-not $Lite)
        sizeBytes = $setupItem.Length
        sha256 = Get-Sha256 $setupPath
        signatureStatus = [string]$setupSignature.Status
        signer = if ($setupSignature.SignerCertificate) { $setupSignature.SignerCertificate.Subject } else { $null }
    }
    $setupMetadata | ConvertTo-Json | Set-Content -LiteralPath "$setupPath.json" -Encoding UTF8
    Write-Host "Installer SHA256: $($setupMetadata.sha256)"
    Write-Host "Installer signature: $($setupMetadata.signatureStatus)"
}

Write-Step 'Build completed'
Write-Host "Staging: $StagingRoot"
    if (-not $SkipInstaller) {
        Write-Host "Installer: $(Join-Path $OutputRoot $setupName)"
    }
