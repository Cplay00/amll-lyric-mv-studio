# AMLL MV Studio Windows distribution notices

This installer is a distribution of free/open-source software. It is not a
proprietary or closed-source grant. The AMLL application and AMLL rendering
components are distributed under **GNU Affero General Public License version
3 only (AGPL-3.0-only)**. Redistribution must comply with that license,
including the Corresponding Source obligations in AGPL section 6.

The complete AGPL-3.0 text shown by Setup and installed as `LICENSE.txt` is
copied from `@applemusic-like-lyrics/core@0.5.2`. The complete GPLv3 text for
the bundled FFmpeg build is installed as `COPYING.GPLv3.txt`. Distributors must
provide the Corresponding Source from the same place as the installer, or by
another method allowed by AGPL-3.0 section 6. This binary package alone is not
a substitute for that source-code offer.

## Components and pinned acquisition sources

| Component | Version/build | License / terms | Build acquisition source |
|---|---:|---|---|
| AMLL MV Studio, `scripts/render-mv.mjs`, `scripts/render-lib.mjs`, and `@applemusic-like-lyrics/{core,react,lyric}` | app 0.1.0; renderer scripts from this source release; packages 0.5.2/0.5.2/1.0.2 | AGPL-3.0-only | Project source and npm lockfile; exact installed script hashes are recorded in `package-manifest.json` |
| Node.js x64 | 24.16.0 LTS (Krypton) | MIT plus licenses in Node's bundled `LICENSE` | `https://nodejs.org/dist/v24.16.0/node-v24.16.0-win-x64.zip`; SHA256 `EDACA9BD58EC8E92037DAC4E877D52F6B8F430B81C18B57E264B4E2FB111CD56` from Node's official `SHASUMS256.txt` |
| FFmpeg Windows x64 | 8.1.2 essentials build | GPL version 3; built with `--enable-gpl --enable-version3 --enable-libx264` | Gyan official build CDN: `https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip`; SHA256 `DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC` from the adjacent official `.sha256` file |
| Playwright Core | 1.63.0 | Apache-2.0; its NOTICE credits Puppeteer | npm registry via `npm ci`, lockfile integrity recorded in `package-lock.json` |
| Chrome for Testing / Chromium | 153.0.8010.12, Playwright revision 1243 | Chromium BSD-style license and bundled third-party notices/licenses; Google Chrome terms may cover branded Chrome-for-Testing portions | Installed by pinned `playwright-core@1.63.0`: `https://cdn.playwright.dev/builds/cft/153.0.8010.12/win64/chrome-win64.zip`; Playwright verifies its revision download; installed browser contains `LICENSE`/`LICENSES` material where supplied upstream |
| Vite runtime server | 5.4.21 | MIT | npm registry via lockfile |
| Vite transitive runtime packages | esbuild 0.21.5 plus `@esbuild/win32-x64`, rollup 4.63.1 plus `@rollup/rollup-win32-x64-msvc`, postcss 8.5.28, nanoid 3.3.18, picocolors 1.1.1, source-map-js 1.2.1 | MIT / ISC / BSD-3-Clause as declared by each npm package | npm registry via lockfile |
| Microsoft .NET 8 self-contained runtime | .NET 8 runtime selected by the installed .NET SDK | MIT plus third-party notices included with .NET | Microsoft .NET SDK during `dotnet publish`; the exact published file hashes are recorded in `package-manifest.json` |
| Microsoft Windows App SDK | 1.8.260804001 and locked component packages | Microsoft Windows App SDK license and included third-party notices | NuGet during `dotnet publish`; self-contained files are distributable under the package terms |
| Microsoft WebView2 SDK/Loader | 1.0.4191.47 | SDK LICENSE/NOTICE installed in `licenses/`; Runtime is governed by Microsoft Edge WebView2 terms | NuGet during publish |
| Microsoft Edge WebView2 Evergreen Runtime Bootstrapper | current Evergreen bootstrapper pinned for this build | Microsoft WebView2/Edge Runtime license terms | Microsoft delivery URL `https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/474c9f1d-0fd5-49da-b3b0-df536d328170/MicrosoftEdgeWebview2Setup.exe`; SHA256 `17DEBF797A6C737959BC588236E897936FFAC1AF5F7E515E674AB32F9EDFE719`; Authenticode signer must be Microsoft Corporation |
| Inno Setup build tool (not installed with the app) | 6.7.3 | Inno Setup license | Preferred acquisition: official `winget` source package `JRSoftware.InnoSetup`; Chocolatey is used when already installed; otherwise the upstream release linked by `https://jrsoftware.org/isdl.php` is used: `https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe`, SHA256 `9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732`, valid Authenticode signer Pyrsys B.V. Used only with explicit `-InstallTooling`. |

## Installed full legal texts

Setup installs the following exact texts beneath `licenses/` when available:

- Node.js combined `LICENSE` (Node MIT terms and bundled dependency notices).
- Playwright `LICENSE` and `NOTICE`.
- Vite `LICENSE.md`.
- Microsoft Windows App SDK `license.txt` and `NOTICE.txt` from the exact NuGet package.
- Microsoft WebView2 SDK `LICENSE.txt` and `NOTICE.txt` from the exact NuGet package.
- The vendored Chromium BSD-style license at
  `licenses/browser/Chromium-LICENSE.txt`; additional third-party notices supplied
  by the browser build remain with the browser payload when available.

FFmpeg is a separate executable program invoked by the rendering script. The
included build identifies itself as GPLv3 and includes libx264. Its exact
Corresponding Source is the official FFmpeg 8.1.2 archive at
`https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz` (detached signature:
`https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz.asc`). A distributor must make
that source available as GPLv3 requires; relying on this upstream URL does not
remove the distributor's continuing obligation. FFmpeg license information:
`https://ffmpeg.org/legal.html`.

Chromium licensing information is available at
`https://www.chromium.org/chromium-os/licenses/` and in the browser payload's
own notices. The Lite package does not bundle Chromium: rendering uses the
system Microsoft Edge or Google Chrome installation, governed by the terms of
the user's own browser installation. The WebView2 Evergreen Bootstrapper is
retained in both variants because the GUI preview requires the WebView2 Runtime.
Microsoft license terms are not replaced or modified by this notice.

## No warranty

The AGPL and GPL components are supplied without warranty as stated in their
licenses. Other components are supplied under their respective terms. Product,
project, and company names are used only to identify their software and do not
imply endorsement.
