[CmdletBinding()]
param(
    [string]$PackageRoot = '',
    [switch]$SkipGui,
    [switch]$SkipRender,
    # Lite verification omits Chromium and requires a system Edge/Chrome render.
    [switch]$SystemBrowser,
    [int]$GuiTimeoutSeconds = 25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Windows PowerShell 5.1 cannot use $PSScriptRoot in a parameter default;
# resolve the workspace staging location here instead. This script lives in
# build-tools\scripts, so staging is build-tools\.tmp\staging.
if (-not $PackageRoot) {
    $PackageRoot = Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) '.tmp') 'staging'
}
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$VerifyRoot = Join-Path $env:TEMP ("amll-package-verify-" + [Guid]::NewGuid().ToString('N'))
$WorkRoot = Join-Path $VerifyRoot 'package'
$OutputRoot = Join-Path $VerifyRoot 'output'

function Write-Check([string]$Message) {
    Write-Host "[verify] $Message" -ForegroundColor Cyan
}

function ConvertTo-WindowsCommandLineArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * ($slashes * 2 + 1)))
            [void]$builder.Append('"')
        } else {
            if ($slashes) { [void]$builder.Append(('\' * $slashes)) }
            [void]$builder.Append($character)
        }
        $slashes = 0
    }
    if ($slashes) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [int]$TimeoutSeconds = 300
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables['PATH'] = ''
    foreach ($entry in $Environment.GetEnumerator()) { $start.EnvironmentVariables[$entry.Key] = [string]$entry.Value }
    if ($start.PSObject.Properties['ArgumentList']) {
        foreach ($argument in $ArgumentList) { [void]$start.ArgumentList.Add([string]$argument) }
    } else {
        $start.Arguments = [string]::Join(' ', @($ArgumentList | ForEach-Object {
            ConvertTo-WindowsCommandLineArgument ([string]$_)
        }))
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Failed to start $FilePath" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit([Math]::Max(1, $TimeoutSeconds) * 1000)) {
            & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F 2>&1 | Out-Null
            throw "$FilePath timed out after $TimeoutSeconds seconds."
        }
        $process.WaitForExit()
        [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask))
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        if ($process.ExitCode -ne 0) {
            throw "$FilePath failed ($($process.ExitCode))`n$stdout`n$stderr"
        }
        return ($stdout + $stderr).Trim()
    } finally {
        $process.Dispose()
    }
}

try {
    Write-Check "Copying package away from the repository: $WorkRoot"
    New-Item -ItemType Directory -Force -Path $WorkRoot, $OutputRoot | Out-Null
    Copy-Item -Path (Join-Path $PackageRoot '*') -Destination $WorkRoot -Recurse -Force

    $required = @(
        'AmllMvStudio.exe',
        'Assets\app.ico',
        'runtime\content\dist\index.html',
        'runtime\content\scripts\render-mv.mjs',
        'runtime\content\scripts\render-lib.mjs',
        'runtime\node\node.exe',
        'runtime\ffmpeg\ffmpeg.exe',
        'runtime\content\node_modules\vite\package.json',
        'runtime\content\node_modules\playwright-core\package.json',
        'LICENSE.txt',
        'COPYING.GPLv3.txt',
        'NOTICE.txt',
        'THIRD-PARTY-NOTICES.md',
        'VERSION',
        'tools\verification\GuiSmokeTest.exe',
        'tools\verification\OutputActionTest.exe',
        'package-manifest.json'
    )
    if (-not $SystemBrowser) { $required += 'runtime\browsers' }
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $WorkRoot $relative))) {
            throw "Required package entry missing: $relative"
        }
    }
    foreach ($forbidden in @('dist', 'scripts', 'node_modules', 'content')) {
        if (Test-Path -LiteralPath (Join-Path $WorkRoot $forbidden)) {
            throw "Duplicate content root entry is forbidden: $forbidden"
        }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $WorkRoot 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.product -ne 'AMLL Lyric MV Studio') { throw "Unexpected manifest product: $($manifest.product)" }
    if ($manifest.variant -notin @('full', 'lite')) { throw "Unexpected manifest variant: $($manifest.variant)" }
    $expectsSystemBrowser = ($manifest.variant -eq 'lite')
    if ([bool]$SystemBrowser -ne $expectsSystemBrowser) {
        throw "Verification mode mismatch: manifest variant is '$($manifest.variant)', SystemBrowser=$([bool]$SystemBrowser)."
    }
    if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid manifest version: $($manifest.version)" }
    $packagedVersion = (Get-Content -LiteralPath (Join-Path $WorkRoot 'VERSION') -Raw).Trim()
    if ($packagedVersion -ne [string]$manifest.version) { throw "VERSION/manifest mismatch: $packagedVersion / $($manifest.version)" }
    $iconPath = Join-Path $WorkRoot 'Assets\app.ico'
    if ((Get-Item -LiteralPath $iconPath).Length -lt 256) { throw "Application icon is unexpectedly small: $iconPath" }
    if ($manifest.contentRoot -ne 'runtime/content') { throw "Unexpected manifest contentRoot: $($manifest.contentRoot)" }

    $manifestFiles = @($manifest.stagedFiles)
    if ($manifestFiles.Count -eq 0) { throw 'Manifest stagedFiles is missing or empty.' }
    $expectedPaths = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $manifestPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $previousPath = $null
    foreach ($entry in $manifestFiles) {
        $relative = [string]$entry.path
        if (-not $relative -or $relative -match '\\' -or [IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') {
            throw "Manifest contains non-canonical path: $relative"
        }
        if ($relative -eq 'package-manifest.json') { throw 'Manifest must not hash itself.' }
        if ($null -ne $previousPath -and [StringComparer]::Ordinal.Compare($previousPath, $relative) -ge 0) {
            throw "Manifest stagedFiles is not strictly sorted: $previousPath / $relative"
        }
        if ($entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or [long]$entry.sizeBytes -lt 0) {
            throw "Manifest has invalid hash/size metadata: $relative"
        }
        $expectedPaths.Add($relative, $entry)
        [void]$manifestPathSet.Add($relative)
        $previousPath = $relative
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $WorkRoot -Recurse -File | Where-Object {
        $_.FullName -ne (Join-Path $WorkRoot 'package-manifest.json')
    } | ForEach-Object {
        $relative = $_.FullName.Substring($WorkRoot.Length).TrimStart('\').Replace('\', '/')
        # npm package tarballs never contain dot-named entries; dot-named files
        # under node_modules are foreign tooling state written while this
        # verifier runs (e.g. security-scan hook caches), not package content.
        if ($relative.StartsWith('runtime/content/node_modules/')) {
            $isForeign = $false
            foreach ($segment in $relative.Substring('runtime/content/node_modules/'.Length).Split('/')) {
                if ($segment.StartsWith('.')) { $isForeign = $true; break }
            }
            if ($isForeign) { return }
        }
        [ordered]@{ path = $relative; file = $_ }
    } | Sort-Object path)
    if ($actualFiles.Count -ne $manifestFiles.Count) {
        throw "Staging file count mismatch: manifest=$($manifestFiles.Count), actual=$($actualFiles.Count)"
    }
    foreach ($actual in $actualFiles) {
        if (-not $expectedPaths.ContainsKey($actual.path)) { throw "Unexpected staging file: $($actual.path)" }
        $expected = $expectedPaths[$actual.path]
        if ($actual.file.Length -ne [long]$expected.sizeBytes) { throw "Staging file size mismatch: $($actual.path)" }
        $actualHash = (Get-FileHash -LiteralPath $actual.file.FullName -Algorithm SHA256).Hash
        if ($actualHash -ne [string]$expected.sha256) { throw "Staging file hash mismatch: $($actual.path)" }
        [void]$expectedPaths.Remove($actual.path)
    }
    if ($expectedPaths.Count) { throw "Missing staging files: $([string]::Join(', ', $expectedPaths.Keys))" }

    $productionPackages = @($manifest.productionNpmPackages)
    if ($productionPackages.Count -eq 0) { throw 'Manifest productionNpmPackages is missing or empty.' }
    $previousProductionName = $null
    foreach ($package in $productionPackages) {
        if (-not $package.name -or -not $package.version -or -not $package.license -or -not $package.source -or -not $package.licenseFile) {
            throw 'Incomplete production npm package legal metadata in manifest.'
        }
        if ($null -ne $previousProductionName -and [StringComparer]::Ordinal.Compare($previousProductionName, [string]$package.name) -ge 0) {
            throw "productionNpmPackages is not strictly sorted: $previousProductionName / $($package.name)"
        }
        if (-not $manifestPathSet.Contains([string]$package.licenseFile)) {
            throw "Production npm legal text is absent from stagedFiles: $($package.name) -> $($package.licenseFile)"
        }
        $previousProductionName = [string]$package.name
    }

    $manifestRuntimePackages = @($manifest.runtimeNpmPackages)
    $actualRuntimePackages = @(Get-ChildItem -LiteralPath (Join-Path $WorkRoot 'runtime\content\node_modules') -Directory |
        Where-Object { -not $_.Name.StartsWith('.') } |
        ForEach-Object {
        if ($_.Name.StartsWith('@')) { Get-ChildItem -LiteralPath $_.FullName -Directory | Where-Object { -not $_.Name.StartsWith('.') } } else { $_ }
    } | ForEach-Object {
        $metadata = Get-Content -LiteralPath (Join-Path $_.FullName 'package.json') -Raw | ConvertFrom-Json
        [ordered]@{ name = [string]$metadata.name; version = [string]$metadata.version }
    } | Sort-Object name)
    $expectedRuntime = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($package in $manifestRuntimePackages) {
        if (-not $package -or -not $package.name -or -not $package.version) {
            throw 'Manifest runtimeNpmPackages entries must each contain name and version.'
        }
        $expectedRuntime.Add([string]$package.name, [string]$package.version)
    }
    if ($actualRuntimePackages.Count -ne $manifestRuntimePackages.Count) {
        throw "Runtime npm package count mismatch: manifest=$($manifestRuntimePackages.Count), actual=$($actualRuntimePackages.Count)"
    }
    foreach ($actual in $actualRuntimePackages) {
        if (-not $expectedRuntime.ContainsKey([string]$actual.name)) {
            throw "Runtime npm closure contains unexpected package: $($actual.name)"
        }
        if ($expectedRuntime[[string]$actual.name] -ne [string]$actual.version) {
            throw "Runtime npm package version mismatch: $($actual.name) (expected $($expectedRuntime[[string]$actual.name]), got $($actual.version))"
        }
    }

    $hashedFiles = [ordered]@{
        renderMv = 'runtime\content\scripts\render-mv.mjs'
        renderLib = 'runtime\content\scripts\render-lib.mjs'
        nodeExe = 'runtime\node\node.exe'
        ffmpegExe = 'runtime\ffmpeg\ffmpeg.exe'
    }
    foreach ($entry in $hashedFiles.GetEnumerator()) {
        $expected = $manifest.fileSha256.($entry.Key)
        if (-not $expected) { throw "Manifest hash missing: $($entry.Key)" }
        $actual = (Get-FileHash -LiteralPath (Join-Path $WorkRoot $entry.Value) -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Manifest hash mismatch: $($entry.Value)" }
    }
    $chromiumHashProperty = $manifest.fileSha256.PSObject.Properties['chromiumExe']
    if ($SystemBrowser) {
        if ($manifest.playwright.bundled -ne $false) { throw 'Lite manifest must record bundled=false.' }
        if ($chromiumHashProperty -and $chromiumHashProperty.Value) { throw 'Lite manifest must not hash a bundled Chromium.' }
    } elseif (-not $chromiumHashProperty -or -not $chromiumHashProperty.Value) {
        throw 'Manifest hash missing: chromiumExe'
    }

    # The repository root is two levels above build-tools\scripts. No packaged
    # text file may reference this development tree.
    $forbiddenRoot = [Regex]::Escape((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
    $textFiles = Get-ChildItem -LiteralPath $WorkRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.json', '.js', '.mjs', '.html', '.txt', '.md', '.xml', '.config') }
    foreach ($file in $textFiles) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
        if ($text -and $text -match $forbiddenRoot) {
            throw "Development path leaked into package: $($file.FullName)"
        }
    }

    Write-Check 'Running embedded Node.js'
    $node = Join-Path $WorkRoot 'runtime\node\node.exe'
    $nodeVersion = Invoke-Checked $node @('--version') $WorkRoot
    if ($nodeVersion -notmatch '^v24\.16\.0') { throw "Unexpected Node version: $nodeVersion" }
    Write-Host "  Node $nodeVersion"

    Write-Check 'Running embedded GPLv3/libx264 FFmpeg'
    $ffmpeg = Join-Path $WorkRoot 'runtime\ffmpeg\ffmpeg.exe'
    $ffmpegVersion = Invoke-Checked $ffmpeg @('-version') $WorkRoot
    if ($ffmpegVersion -notmatch '^ffmpeg version 8\.1\.2(?:[-\s]|$)' -or
        $ffmpegVersion -notmatch '--enable-gpl' -or $ffmpegVersion -notmatch '--enable-libx264') {
        throw 'FFmpeg is not the required 8.1.2 GPL/libx264 runtime.'
    }
    Write-Host ('  ' + (($ffmpegVersion -split "`r?`n")[0]))

    Write-Check 'Running embedded Chromium'
    if ($SystemBrowser) {
        if (Test-Path -LiteralPath (Join-Path $WorkRoot 'runtime\browsers')) {
            throw 'Lite package must not contain runtime/browsers.'
        }
        Write-Host '  Lite variant: no bundled browser expected.'
    } else {
    $chromium = Get-ChildItem -LiteralPath (Join-Path $WorkRoot 'runtime\browsers') `
        -Filter chrome.exe -File -Recurse | Select-Object -First 1
    if (-not $chromium) { throw 'chrome.exe was not found under runtime/browsers.' }
    $chromiumHash = (Get-FileHash -LiteralPath $chromium.FullName -Algorithm SHA256).Hash
    if ($chromiumHash -ne $chromiumHashProperty.Value) { throw 'Manifest hash mismatch: Chromium executable' }
    $chromiumVersion = (Get-Item -LiteralPath $chromium.FullName).VersionInfo.ProductVersion
    if ($chromiumVersion -notmatch '^153\.0\.8010\.12') {
        throw "Unexpected Chromium version metadata: $chromiumVersion"
    }
    Write-Host "  Chromium $chromiumVersion"
    # Windows Chrome for Testing does not reliably implement a terminating
    # `--version` command; it may open a browser process and leave children after
    # forced termination. The exact hash/version checks above plus the real
    # Playwright render below validate both identity and executability.
    }

    if (-not $SkipGui) {
        $guiToolsRoot = Join-Path $WorkRoot 'tools\verification'

        Write-Check 'Creating demo audio for staged GUI functional tests'
        $guiAudio = Join-Path $OutputRoot 'gui-smoke.wav'
        $null = Invoke-Checked $ffmpeg @(
            '-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi',
            '-i', 'sine=frequency=440:sample_rate=44100:duration=0.5', '-c:a', 'pcm_s16le', $guiAudio
        ) $WorkRoot
        if (-not (Test-Path -LiteralPath $guiAudio) -or (Get-Item -LiteralPath $guiAudio).Length -le 0) {
            throw 'GUI smoke audio was not created.'
        }

        $guiTimeout = [Math]::Max(60, $GuiTimeoutSeconds)
        $guiSmokeOutput = Join-Path $OutputRoot 'gui-smoke.mp4'
        Write-Check 'Running staged GUI render smoke test'
        $guiSmokeLog = Invoke-Checked (Join-Path $guiToolsRoot 'GuiSmokeTest.exe') @(
            '--exe', (Join-Path $WorkRoot 'AmllMvStudio.exe'),
            '--audio', $guiAudio,
            '--output', $guiSmokeOutput,
            '--timeout-seconds', [string]$guiTimeout
        ) $WorkRoot
        Write-Host $guiSmokeLog

        Write-Check 'Running staged GUI output-action security test'
        $outputActionLog = Invoke-Checked (Join-Path $guiToolsRoot 'OutputActionTest.exe') @(
            '--exe', (Join-Path $WorkRoot 'AmllMvStudio.exe'),
            '--audio', $guiAudio,
            '--output-dir', $OutputRoot,
            '--timeout-seconds', [string]$guiTimeout
        ) $WorkRoot
        Write-Host $outputActionLog
        if (Test-Path -LiteralPath (Join-Path $OutputRoot '成片 & calc & 结束 ^ (v1) ! 安全测试.mp4')) {
            throw 'OutputActionTest did not clean its hostile-name render output.'
        }
    }

    if (-not $SkipRender) {
        Write-Check 'Checking renderer encoder discovery without PATH or runtime environment hints'
        $renderScript = Join-Path $WorkRoot 'runtime\content\scripts\render-mv.mjs'
        $renderEnvironment = @{
            'PLAYWRIGHT_BROWSERS_PATH' = ''
            'FFMPEG_PATH' = ''
            'NODE_PATH' = ''
        }
        $encoderOutput = Invoke-Checked $node @(
            $renderScript, '--list-encoders'
        ) $WorkRoot $renderEnvironment
        $encoderJsonLine = @($encoderOutput -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('{') })[0]
        if (-not $encoderJsonLine) { throw "Renderer did not emit encoder JSON: $encoderOutput" }
        $encoderReport = $encoderJsonLine | ConvertFrom-Json
        if ($encoderReport.ffmpegVersion -notmatch '^ffmpeg version 8\.1\.2(?:[-\s]|$)') {
            throw "Renderer used unexpected FFmpeg version: $($encoderReport.ffmpegVersion)"
        }
        if ([IO.Path]::GetFullPath([string]$encoderReport.ffmpegPath) -ne [IO.Path]::GetFullPath($ffmpeg)) {
            throw "Renderer FFmpeg path is not runtime/ffmpeg: $($encoderReport.ffmpegPath)"
        }
        if ($encoderReport.ffmpegSource -notmatch 'runtime/ffmpeg') {
            throw "Renderer FFmpeg source is not packaged runtime: $($encoderReport.ffmpegSource)"
        }
        $libx264 = @($encoderReport.encoders | Where-Object { $_.name -eq 'libx264' })[0]
        if (-not $libx264 -or -not $libx264.compiled -or -not $libx264.available) {
            throw "Packaged libx264 did not pass real encode probe: $encoderJsonLine"
        }

        Write-Check 'Creating a one-second demo audio input'
        $audio = Join-Path $OutputRoot 'smoke.wav'
        $makeAudioOutput = Invoke-Checked $ffmpeg @(
            '-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi',
            '-i', 'sine=frequency=440:sample_rate=44100:duration=1', '-c:a', 'pcm_s16le', $audio
        ) $WorkRoot
        if (-not (Test-Path -LiteralPath $audio)) { throw 'Smoke audio was not created.' }

        Write-Check 'Rendering a short video with isolated runtime closure'
        $rendered = Join-Path $OutputRoot 'smoke.mp4'
        $renderArgs = @(
            $renderScript,
            '--audio', $audio,
            '--out', $rendered,
            '--width', '320', '--height', '180', '--fps', '10',
            '--duration', '1000', '--tail', '0', '--crf', '30',
            '--ffmpeg', $ffmpeg, '--progress-json'
        )
        if (-not $SystemBrowser) {
            # Full explicitly supplies the embedded Chromium executable.
            $renderArgs += @('--browser', $chromium.FullName)
        }
        # Lite intentionally omits --browser to exercise real system discovery.
        $renderOutput = Invoke-Checked $node $renderArgs $WorkRoot $renderEnvironment
        if (-not (Test-Path -LiteralPath $rendered)) {
            throw "Smoke render output was not created. Renderer output:`n$renderOutput"
        }
        $size = (Get-Item -LiteralPath $rendered).Length
        if ($size -lt 1024) { throw "Smoke render is unexpectedly small: $size bytes" }
        if ($SystemBrowser) {
            $browserLine = $renderOutput -split "`r?`n" |
                Where-Object { $_ -match 'Microsoft[\\/]Edge[\\/]Application[\\/]msedge\.exe|Google[\\/]Chrome[\\/]Application[\\/]chrome\.exe' } |
                Select-Object -First 1
            if (-not $browserLine) { throw "Renderer did not log a system Edge/Chrome path: $renderOutput" }
            if ($browserLine -notmatch 'system browser') {
                # The path match above proves an installed Edge/Chrome executable;
                # verify the source marker through its UTF-8 byte sequence so
                # Windows PowerShell 5.1 cannot misdecode this source file.
                $systemBrowserMarker = [Text.Encoding]::UTF8.GetString([byte[]](0xE7,0xB3,0xBB,0xE7,0xBB,0x9F,0xE6,0xB5,0x8F,0xE8,0xA7,0x88,0xE5,0x99,0xA8))
                if ($browserLine -notmatch [Regex]::Escape($systemBrowserMarker)) {
                    throw "Lite render did not identify the system browser source: $browserLine"
                }
            }
            Write-Host "  System browser render: $browserLine"
        }
        Write-Host "  Render OK: $rendered ($size bytes)"
    }

    Write-Host "`nPackage verification PASSED: $PackageRoot" -ForegroundColor Green
    exit 0
} finally {
    # A terminated GUI/browser may briefly keep files locked. Cleanup failure
    # must not turn a successful verification into a failing exit code.
    $cleanupDone = $false
    foreach ($attempt in 1..5) {
        if (-not (Test-Path -LiteralPath $VerifyRoot)) { $cleanupDone = $true; break }
        try {
            Remove-Item -LiteralPath $VerifyRoot -Recurse -Force -ErrorAction Stop
            $cleanupDone = $true
            break
        } catch {
            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }
    if (-not $cleanupDone -and (Test-Path -LiteralPath $VerifyRoot)) {
        Start-Process -FilePath "$env:ComSpec" -ArgumentList @('/c', "ping 127.0.0.1 -n 3 > nul & rmdir /s /q `"$VerifyRoot`"") -WindowStyle Hidden | Out-Null
    }
}
