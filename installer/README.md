# AMLL Lyric MV Studio Windows installer and release build

The installer definition (`.iss`, legal texts, languages) lives here. Build
scripts, tooling, caches, and generated output live in the workspace-local
`build-tools\` directory: scripts under `build-tools\scripts`, verified
downloads under `build-tools\cache`, compiled installers under
`build-tools\output`, and the staging tree under `build-tools\.tmp\staging`.

## Build

From PowerShell on Windows:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build-tools\scripts\bootstrap-cache.ps1   # optional: pre-populate build-tools\cache
.\build-tools\scripts\build-installer.ps1
```

The default build performs a clean `npm ci`, `npm run build`, self-contained
`dotnet publish -c Release -r win-x64`, downloads and verifies pinned runtimes
(Node, Chromium, WebView2), verifies the Gyan "essentials" static FFmpeg 8.1.2
archive against its pinned SHA-256 (the prebuilt binary provides the
AMF/NVENC/QSV hardware encoders plus libx264/libx265), installs Playwright
Chromium into staging, runs package smoke tests, then calls Inno Setup 6. Use
`-Lite` to omit bundled Chromium and verify rendering through the system
Microsoft Edge (or Google Chrome fallback). Generated paths:

- `build-tools/.tmp/staging/` — unpacked standalone application tree for the latest build.
- `build-tools/output/AMLL-Lyric-MV-Studio-<version>-x64-Setup.exe` — Full installer.
- `build-tools/output/AMLL-Lyric-MV-Studio-<version>-x64-Lite-Setup.exe` — Lite installer.
- `build-tools/cache/` — reusable verified downloads.
- `build-tools/tools/Inno Setup 6/` — workspace-local Inno Setup toolchain.

Useful switches:

- `-Version 1.2.3` overrides `installer/VERSION` for assembly and installer.
- `-Lite` builds the Lite installer without `runtime/browsers`; package verification
  must find and render with the system Microsoft Edge or Google Chrome.
- `-Offline` refuses downloads and requires a complete cache.
- `-SkipVerify` or `-SkipInstaller` supports focused build troubleshooting.
- `-InstallTooling` explicitly authorizes installing Inno Setup 6 when ISCC is
  missing. Without this switch the script never installs tools silently.
- `-CertificateThumbprint` / `-PfxPath` optionally Authenticode-sign the
  produced installer (SHA-256 digest, RFC 3161 timestamp via
  `-TimestampServer`). PFX passwords are read from `AMLL_SETUP_PFX_PASSWORD`
  and never accepted as a command-line argument. Omit both to build unsigned.

`Invoke-WebRequest`, npm, NuGet, and Playwright use normal proxy settings.
Set `HTTP_PROXY` and `HTTPS_PROXY` before building where required. Node and
WebView2 cache files have pinned SHA256 values; Microsoft Authenticode signing
is also checked for WebView2. Playwright/npm/NuGet integrity mechanisms verify
their locked inputs.

## Runtime layout

The installation root contains the GUI executable and self-contained .NET /
Windows App SDK files plus:

```text
runtime/content/dist/
runtime/content/scripts/render-mv.mjs
runtime/content/scripts/render-lib.mjs
runtime/content/node_modules/         # Vite 7.3.6 + @vitejs/plugin-react 4.7.0 + playwright-core 1.63.0
                                      # closure (fdir, picomatch, tinyglobby, @esbuild/win32-x64, ...)
runtime/node/node.exe
runtime/ffmpeg/ffmpeg.exe
runtime/browsers/chromium-*/          # Full only
runtime/webview2/MicrosoftEdgeWebview2Setup.exe
licenses/npm/                         # production npm dependency license texts
tools/verification/                   # immutable package verification executables
package-manifest.json                 # hashes every other staged file
```

The Lite layout omits `runtime/browsers`. Its renderer discovers the installed
Microsoft Edge first and Google Chrome as a fallback; the WebView2 bootstrapper
is retained because the GUI preview itself requires the WebView2 Runtime.

The installer is x64 and per-user (`PrivilegesRequired=lowest`). Simplified
Chinese is the default language, the welcome page appears before the license,
and its lower-left selector can restart Setup safely in Simplified Chinese,
English, Japanese, Korean, French, German, Spanish, or Russian while preserving
all original Setup command-line options. The repository bundles the official
Inno Setup Simplified Chinese translation so compilation does not depend on a
machine-specific language file. Setup creates only selected Start Menu/desktop
shortcuts, launches `AmllMvStudio.exe` directly,
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
.\build-tools\scripts\verify-package.ps1 -PackageRoot .\build-tools\.tmp\staging

# Lite: requires no runtime/browsers directory and exercises system discovery
.\build-tools\scripts\verify-package.ps1 -PackageRoot .\build-tools\.tmp\staging -SystemBrowser
```

The verifier copies staging to a random `%TEMP%` directory, strictly recomputes
`package-manifest.json` for the complete file tree, checks the exact runtime npm
name/version closure, scans text files for the development path, executes the
prebuilt immutable GUI verification tools, checks Node/FFmpeg and the selected
browser, and makes a one-second 320x180 smoke render. It does not restore or
compile against the working tree. `-SkipGui` and `-SkipRender` may be used only
for targeted diagnosis; release builds use all checks.

After both variants pass, `finalize-release.ps1` assembles the final release
set under `<repo>\release`. Since after v0.2.1 the release policy is
**installers only**: the release set contains the Full and Lite Setup
executables, their `.json` SHA256/signature sidecars, and a slim
`release-manifest.json` with file hashes. Source archives and FFmpeg
corresponding-source offer bundles are no longer attached to releases; the
script refuses to finalize unless both verified installer variants exist in
`build-tools\output`.

## Code signing

When a certificate is supplied, each installer is signed after compilation with
signtool (`/fd SHA256`, RFC 3161 timestamp `/tr`). The current release set is
signed with a self-signed code-signing certificate (`CN=Cplay00`, SHA-1
thumbprint `B767B438E13579C59C2D5B3683DE2D51CD0D7080`, expires 2031-09-13).
Because the chain is self-signed, Windows SmartScreen still shows a first-run
warning and `Get-AuthenticodeSignature` reports an untrusted-root status; the
signature still proves file integrity and names the publisher. Replacing it
with a CA-issued certificate later only changes the thumbprint passed to the
build.

## Distribution obligations

Read `NOTICE.txt`, `LICENSE.txt`, `COPYING.GPLv3.txt`, and
`THIRD-PARTY-NOTICES.md`. AMLL is AGPL-3.0-only and the bundled FFmpeg is the
Gyan "essentials" statically linked 8.1.2 GPL build (libx264/libx265 plus
AMF/NVENC/QSV). Releases ship the installers only; the corresponding sources
of FFmpeg and its enabled libraries remain available through the Gyan build
page and the upstream projects, and by request through the contact channels
published with the project (see `THIRD-PARTY-NOTICES.md`). Do not describe the
package as closed-source or unrestricted proprietary software.
