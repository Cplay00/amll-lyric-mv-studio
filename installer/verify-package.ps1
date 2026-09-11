[CmdletBinding()]
param(
    [string]$PackageRoot = (Join-Path $PSScriptRoot 'staging'),
    [switch]$SkipGui,
    [switch]$SkipRender,
    # Lite verification omits Chromium and requires a system Edge/Chrome render.
    [switch]$SystemBrowser,
    [int]$GuiTimeoutSeconds = 25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$VerifyRoot = Join-Path $env:TEMP ("amll-package-verify-" + [Guid]::NewGuid().ToString('N'))
$WorkRoot = Join-Path $VerifyRoot 'package'
$OutputRoot = Join-Path $VerifyRoot 'output'

function Write-Check([string]$Message) {
    Write-Host "[verify] $Message" -ForegroundColor Cyan
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory,
        [hashtable]$Environment = @{}
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
    $start.Environment['PATH'] = ''
    foreach ($entry in $Environment.GetEnumerator()) { $start.Environment[$entry.Key] = [string]$entry.Value }
    $start.Arguments = [string]::Join(' ', @($ArgumentList | ForEach-Object {
        '"' + ([string]$_).Replace('"', '\"') + '"'
    }))
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "$FilePath failed ($($process.ExitCode))`n$stdout`n$stderr"
    }
    return ($stdout + $stderr).Trim()
}

try {
    Write-Check "Copying package away from the repository: $WorkRoot"
    New-Item -ItemType Directory -Force -Path $WorkRoot, $OutputRoot | Out-Null
    Copy-Item -Path (Join-Path $PackageRoot '*') -Destination $WorkRoot -Recurse -Force

    $required = @(
        'AmllMvStudio.exe',
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
    if ($manifest.contentRoot -ne 'runtime/content') { throw "Unexpected manifest contentRoot: $($manifest.contentRoot)" }
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

    $forbiddenRoot = [Regex]::Escape((Split-Path -Parent $PSScriptRoot))
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
        Write-Check 'Starting GUI from isolated package'
        $guiStart = [Diagnostics.ProcessStartInfo]::new()
        $guiStart.FileName = Join-Path $WorkRoot 'AmllMvStudio.exe'
        $guiStart.WorkingDirectory = $WorkRoot
        $guiStart.UseShellExecute = $false
        $guiStart.Environment['PATH'] = ''
        $guiStart.Environment['PLAYWRIGHT_BROWSERS_PATH'] = ''
        $guiStart.Environment['FFMPEG_PATH'] = ''
        $guiStart.Environment['NODE_PATH'] = ''
        $gui = [Diagnostics.Process]::Start($guiStart)
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds($GuiTimeoutSeconds)
            $windowReady = $false
            do {
                Start-Sleep -Milliseconds 500
                $gui.Refresh()
                if ($gui.HasExited) { throw "GUI exited during smoke test with code $($gui.ExitCode)." }
                if ($gui.MainWindowHandle -ne [IntPtr]::Zero -and $gui.MainWindowTitle) { $windowReady = $true; break }
            } while ([DateTime]::UtcNow -lt $deadline)
            if (-not $windowReady) { throw "GUI did not create a main window within $GuiTimeoutSeconds seconds." }
            Write-Host "  GUI window ready (PID $($gui.Id))"
        } finally {
            if ($gui -and -not $gui.HasExited) { $gui.Kill(); $gui.WaitForExit(10000) | Out-Null }
            if ($gui) { $gui.Dispose() }
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
                Where-Object { $_ -match 'Microsoft/Edge/Application/msedge\.exe|Google/Chrome/Application/chrome\.exe' } |
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
