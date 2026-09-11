# Windows installer and release build

This directory is self-contained release infrastructure; generated downloads,
staging files, and installers remain under `installer/`.

## Build

From PowerShell on Windows:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\installer\build-installer.ps1
```

The default build performs a clean `npm ci`, `npm run build`, self-contained
`dotnet publish -c Release -r win-x64`, downloads and verifies pinned runtimes,
installs Playwright Chromium into staging, runs package smoke tests, then calls
Inno Setup 6. Use `-Lite` to omit bundled Chromium and verify rendering through
the system Microsoft Edge (or Google Chrome fallback). Generated paths:

- `installer/staging/` — unpacked standalone application tree for the latest build.
- `installer/output/AMLL-MV-Studio-<version>-x64-Setup.exe` — Full installer.
- `installer/output/AMLL-MV-Studio-<version>-x64-Lite-Setup.exe` — Lite installer.
- `installer/cache/` — reusable verified downloads.

Useful switches:

- `-Version 1.2.3` overrides `installer/VERSION` for assembly and installer.
- `-Lite` builds the Lite installer without `runtime/browsers`; package verification
  must find and render with the system Microsoft Edge or Google Chrome.
- `-Offline` refuses downloads and requires a complete cache.
- `-SkipVerify` or `-SkipInstaller` supports focused build troubleshooting.
- `-InstallTooling` explicitly authorizes installing Inno Setup 6 when ISCC is
  missing. Without this switch the script never installs tools silently.

`Invoke-WebRequest`, npm, NuGet, and Playwright use normal proxy settings.
Set `HTTP_PROXY` and `HTTPS_PROXY` before building where required. Node and
WebView2 cache files have pinned SHA256 values; Microsoft Authenticode signing
is also checked for WebView2. Playwright/npm/NuGet integrity mechanisms verify
their locked inputs.

## Runtime layout

The installation root contains the GUI executable and self-contained .NET /
Windows App SDK files plus:

```text
dist/
scripts/render-mv.mjs
node_modules/                 # only render-time dependency closure
runtime/node/node.exe
runtime/ffmpeg/ffmpeg.exe
runtime/browsers/chromium-*/      # Full only
runtime/webview2/MicrosoftEdgeWebview2Setup.exe
licenses/
```

The Lite layout omits `runtime/browsers`. Its renderer discovers the installed
Microsoft Edge first and Google Chrome as a fallback; the WebView2 bootstrapper
is retained because the GUI preview itself requires the WebView2 Runtime.

The installer is x64 and per-user (`PrivilegesRequired=lowest`), creates only
selected Start Menu/desktop shortcuts, launches `AmllMvStudio.exe` directly,
and uninstalls program files. User media/output is never deleted. It sets the
current user's `PLAYWRIGHT_BROWSERS_PATH` and removes it only if it still points
to this installation. The browser also exists at Playwright's package-local
default discovery path. If WebView2 is absent, Setup silently runs Microsoft's
signed Evergreen Bootstrapper; .NET and Windows App SDK are self-contained and
no machine-wide framework is installed.

## Verification

Run an independent package check with:

```powershell
# Full: verifies the packaged Chromium path
.\installer\verify-package.ps1 -PackageRoot .\installer\staging

# Lite: requires no runtime/browsers directory and exercises system discovery
.\installer\verify-package.ps1 -PackageRoot .\installer\staging -SystemBrowser
```

The verifier copies staging to a random `%TEMP%` directory, scans text files
for the development path, starts the GUI, checks Node/FFmpeg and the selected
browser, and makes a one-second 320x180 smoke render. `-SkipGui` and
`-SkipRender` may be used only for targeted diagnosis; release builds use all
checks.

## Distribution obligations

Read `NOTICE.txt`, `LICENSE.txt`, `COPYING.GPLv3.txt`, and
`THIRD-PARTY-NOTICES.md`. AMLL is AGPL-3.0-only and the included FFmpeg/libx264 build is GPLv3. Do not distribute
the binary installer without fulfilling Corresponding Source requirements; do
not describe the package as closed-source or unrestricted proprietary software.
