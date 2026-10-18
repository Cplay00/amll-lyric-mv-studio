# AMLL Lyric MV Studio Windows distribution notices

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
| AMLL Lyric MV Studio, `scripts/render-mv.mjs`, `scripts/render-lib.mjs`, and `@applemusic-like-lyrics/{core,react,lyric}` | app 0.2.0; renderer scripts from this source release; packages 0.5.2/0.5.2/1.0.2 | AGPL-3.0-only | Project source and npm lockfile; AMLL npm packages come from [amll-dev/applemusic-like-lyrics](https://github.com/amll-dev/applemusic-like-lyrics); exact installed script hashes are recorded in `package-manifest.json` |
| Node.js x64 | 24.16.0 LTS (Krypton) | MIT plus licenses in Node's bundled `LICENSE` | `https://nodejs.org/dist/v24.16.0/node-v24.16.0-win-x64.zip`; SHA256 `EDACA9BD58EC8E92037DAC4E877D52F6B8F430B81C18B57E264B4E2FB111CD56` from Node's official `SHASUMS256.txt` |
| FFmpeg Windows x64 (Gyan "essentials" prebuilt build) | 8.1.2 | Conveyed under GPL version 3: FFmpeg GPL components plus libx264/libx265 (GPL-2.0-or-later), GPL-licensed external libraries, and MIT/BSD-style helper libraries | Prebuilt binary from Gyan D. (Felix Hänicke)'s codexffmpeg release: `https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip`, SHA-256 `DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC`; the archive URL and hash are pinned in the staged `package-manifest.json`, and corresponding sources of FFmpeg and its enabled libraries remain available through the Gyan build page, the upstream projects, and the contact channels published with the project |
| Playwright Core | 1.63.0 | Apache-2.0; its NOTICE credits Puppeteer | npm registry via `npm ci`; exact version and integrity recorded in `package-lock.json` |
| React, ReactDOM, scheduler | 18.3.1 / 18.3.1 / 0.23.2 | MIT | npm registry tarballs recorded below and in `package-lock.json` |
| PixiJS production packages | `@pixi/{app,color,constants,core,display,extensions,filter-blur,filter-color-matrix,math,runner,settings,sprite,ticker,utils}` 7.4.3; `@pixi/filter-bulge-pinch` 5.1.1; `@pixi/colord` 2.9.6 | MIT | npm registry tarballs recorded below and in `package-lock.json` |
| Chrome for Testing / Chromium | 153.0.8010.12, Playwright revision 1243 | Chromium BSD-style license and bundled third-party notices/licenses; Google Chrome terms may cover branded Chrome-for-Testing portions | Installed by pinned `playwright-core@1.63.0`: `https://cdn.playwright.dev/builds/cft/153.0.8010.12/win64/chrome-win64.zip`; Playwright verifies its revision download; installed browser contains `LICENSE`/`LICENSES` material where supplied upstream |
| Vite runtime server | 7.3.6 | MIT | npm registry: `https://registry.npmjs.org/vite/-/vite-7.3.6.tgz`; exact integrity in `package-lock.json` |
| Vite transitive runtime packages | esbuild 0.28.2 plus `@esbuild/win32-x64` 0.28.2; rollup 4.63.1 plus `@rollup/rollup-win32-x64-msvc` 4.63.1; postcss 8.5.28; nanoid 3.3.18; picocolors 1.1.1; source-map-js 1.2.1 | MIT (Vite, esbuild, rollup, postcss, nanoid) / ISC (picocolors) / BSD-3-Clause (source-map-js) | npm registry via exact lockfile records; original package legal texts installed under `licenses/` |
| Microsoft .NET 8 self-contained runtime | .NET 8 runtime selected by the installed .NET SDK | MIT plus third-party notices included with .NET | Microsoft .NET SDK during `dotnet publish`; the exact published file hashes are recorded in `package-manifest.json` |
| Microsoft Windows App SDK | 1.8.260804001 and locked component packages | Microsoft Windows App SDK license and included third-party notices | NuGet during `dotnet publish`; self-contained files are distributable under the package terms |
| Microsoft WebView2 SDK/Loader | 1.0.4191.47 | SDK LICENSE/NOTICE installed in `licenses/`; Runtime is governed by Microsoft Edge WebView2 terms | NuGet during publish |
| Microsoft Edge WebView2 Evergreen Runtime Bootstrapper | current Evergreen bootstrapper pinned for this build | Microsoft WebView2/Edge Runtime license terms | Microsoft delivery URL `https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/474c9f1d-0fd5-49da-b3b0-df536d328170/MicrosoftEdgeWebview2Setup.exe`; SHA256 `17DEBF797A6C737959BC588236E897936FFAC1AF5F7E515E674AB32F9EDFE719`; Authenticode signer must be Microsoft Corporation |
| Inno Setup build tool (not installed with the app) | 6.7.3 | Inno Setup license | Preferred acquisition: official `winget` source package `JRSoftware.InnoSetup`; Chocolatey is used when already installed; otherwise the upstream release linked by `https://jrsoftware.org/isdl.php` is used: `https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe`, SHA256 `9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732`, valid Authenticode signer Pyrsys B.V. Used only with explicit `-InstallTooling`. |

| Inno Setup Simplified Chinese messages (source build input, not installed separately) | Inno Setup 6.5.0+ translation, retrieved 2026-09-10 | Inno Setup License; the unmodified source retains its maintainer/origin notices, and this notice retains the license's source-redistribution conditions | Official `jrsoftware/issrc` file `Files/Languages/ChineseSimplified.isl`; included unchanged at `installer/languages/ChineseSimplified.isl`, SHA256 `E0B0B350E2245F3C5E65586DFE43D574F6E7F06F2261149ABA284954B3FC9A8D`; license: `https://github.com/jrsoftware/issrc/blob/main/license.txt` (SHA256 `2E5346868C2A18434489824E11D65C3031620F792FEFC415D05F19CD441ABF5C`) |

### Inno Setup translation redistribution conditions

The bundled `installer/languages/ChineseSimplified.isl` is an unmodified source
file from the official Inno Setup repository. Its existing maintainer, email,
GitHub, encoding, and origin comments are retained. The applicable Inno Setup
License copyright notices are:

- Copyright (C) 1997-2026 Jordan Russell. All rights reserved.
- Portions Copyright (C) 2000-2026 Martijn Laan. All rights reserved.

The license states:

> Permission is granted to anyone to use this software for any purpose, including commercial
> applications, and to alter and redistribute it, provided that the following conditions are met:
>
> 1. All redistributions of source code files must retain all copyright notices that are currently
>    in place, and this list of conditions without modification.
>
> 2. All redistributions in binary form must retain all occurrences of the above copyright notice
>    and web site addresses that are currently in place (for example, in the About boxes).
>
> 3. The origin of this software must not be misrepresented; you must not claim that you wrote
>    the original software. If you use this software to distribute a product, an acknowledgment
>    in the product documentation would be appreciated but is not required.
>
> 4. Modified versions in source or binary form must be plainly marked as such, and must not
>    be misrepresented as being the original software.

The authoritative full license is linked and pinned by hash in the table above.

No unchanged AMLL source files are vendored in this repository, and no submodule is used or represented as an AMLL submodule. AMLL functionality is consumed through the npm packages named above; package provenance and integrity are locked by `package-lock.json`.

The complete production dependency closure from lockfile entries where
`dev !== true` is recorded below. `build-installer.ps1` regenerates this closure
from `package-lock.json`, copies each package's original legal text to
`licenses/npm/`, and fails if a required text is unavailable. `source` is the
exact npm registry tarball locked for the build.

| Package | Version | Declared license | Source |
|---|---:|---|---|
| `@applemusic-like-lyrics/core` | 0.5.2 | AGPL-3.0-only | `https://registry.npmjs.org/@applemusic-like-lyrics/core/-/core-0.5.2.tgz` |
| `@applemusic-like-lyrics/lyric` | 1.0.2 | AGPL-3.0-only | `https://registry.npmjs.org/@applemusic-like-lyrics/lyric/-/lyric-1.0.2.tgz` |
| `@applemusic-like-lyrics/react` | 0.5.2 | AGPL-3.0-only | `https://registry.npmjs.org/@applemusic-like-lyrics/react/-/react-0.5.2.tgz` |
| `@applemusic-like-lyrics/ttml` | 1.0.1 | AGPL-3.0-only | `https://registry.npmjs.org/@applemusic-like-lyrics/ttml/-/ttml-1.0.1.tgz` |
| `@pixi/app`, `@pixi/color`, `@pixi/constants`, `@pixi/core`, `@pixi/display`, `@pixi/extensions`, `@pixi/filter-blur`, `@pixi/filter-color-matrix`, `@pixi/math`, `@pixi/runner`, `@pixi/settings`, `@pixi/sprite`, `@pixi/ticker`, `@pixi/utils` | 7.4.3 | MIT | package-specific `https://registry.npmjs.org/@pixi/<name>/-/<name>-7.4.3.tgz` |
| `@pixi/filter-bulge-pinch` | 5.1.1 | MIT | `https://registry.npmjs.org/@pixi/filter-bulge-pinch/-/filter-bulge-pinch-5.1.1.tgz` |
| `@pixi/colord` | 2.9.6 | MIT | `https://registry.npmjs.org/@pixi/colord/-/colord-2.9.6.tgz` |
| `@types/css-font-loading-module` / `@types/earcut` | 0.0.12 / 2.1.4 | MIT | exact npm tarballs in `package-lock.json` |
| `@ungap/structured-clone` | 1.4.0 | ISC | `https://registry.npmjs.org/@ungap/structured-clone/-/structured-clone-1.4.0.tgz` |
| `bezier-easing` / `gl-matrix` | 3.1.0 / 4.0.0-beta.2 | MIT | exact npm tarballs in `package-lock.json` |
| `react` / `react-dom` / `scheduler` | 18.3.1 / 18.3.1 / 0.23.2 | MIT | exact npm tarballs in `package-lock.json` |
| `pako` | 2.2.0 | MIT AND Zlib | `https://registry.npmjs.org/pako/-/pako-2.2.0.tgz` |
| `earcut` | 2.2.4 | ISC | `https://registry.npmjs.org/earcut/-/earcut-2.2.4.tgz` |
| `qs` | 6.16.0 | BSD-3-Clause | `https://registry.npmjs.org/qs/-/qs-6.16.0.tgz` |
| `url` / `punycode` | 0.11.4 / 1.4.1 | MIT | exact npm tarballs in `package-lock.json` |
| `call-bind-apply-helpers`, `call-bound`, `dunder-proto`, `es-define-property`, `es-errors`, `es-object-atoms`, `eventemitter3`, `function-bind`, `get-intrinsic`, `get-proto`, `gopd`, `has-symbols`, `hasown`, `ismobilejs`, `js-tokens`, `loose-envify`, `math-intrinsics`, `object-inspect`, `side-channel`, `side-channel-list`, `side-channel-map`, `side-channel-weakmap` | exact versions in `package-lock.json` | MIT | exact npm tarballs and versions in `package-lock.json` and `package-manifest.json` |
| `deep-freeze` | 0.0.1 | Public domain | `https://registry.npmjs.org/deep-freeze/-/deep-freeze-0.0.1.tgz` |

## Installed full legal texts

Setup installs the following exact texts beneath `licenses/`; absence is a
build-stopping error rather than a warning:

- Node.js combined `LICENSE` (Node MIT terms and bundled dependency notices).
- Playwright `LICENSE` and `NOTICE`.
- Vite, esbuild, Rollup, PostCSS, nanoid, picocolors and source-map-js legal texts.
- One original legal text for every `dev !== true` npm package, beneath `licenses/npm/`.
- Microsoft Windows App SDK `license.txt` and `NOTICE.txt` from the exact NuGet package.
- Microsoft WebView2 SDK `LICENSE.txt` and `NOTICE.txt` from the exact NuGet package.
- The vendored Chromium BSD-style license at
  `licenses/browser/Chromium-LICENSE.txt`; additional third-party notices supplied
  by the browser build remain with the browser payload when available.
- The FFmpeg build's own `LICENSE` (GPL v3 text shipped by the Gyan build) at
  `licenses/ffmpeg/`, taken verbatim from the distributed binary archive.

FFmpeg is a separate executable program invoked by the rendering script. The
installed build is Gyan's statically linked FFmpeg 8.1.2 "essentials" prebuilt
binary, conveyed in full under GPL version 3. It provides the hardware encoders
(`h264_nvenc`/`hevc_nvenc`, `h264_qsv`/`hevc_qsv`, `h264_amf`/`hevc_amf`) and the
software encoders `libx264`/`libx265`. The distributed binary archive is
verified against its pinned SHA-256 (recorded in the staged package manifest
together with its upstream URL). Releases ship the installers only; the
corresponding sources of FFmpeg and of every third-party library the build
enables remain available through the Gyan build page and the upstream projects
it documents. FFmpeg license information: `https://ffmpeg.org/legal.html`.
Written source offers under GPL section 6(b) remain valid for at least three
years; anyone may additionally request the corresponding source by the contact
channels published with the project.

The build workflow records the exact distributed FFmpeg archive (URL, version,
SHA-256) in the staged `package-manifest.json` carried inside each installer.
Earlier releases attached corresponding-source offer bundles to the release
page (v0.2.0: self-built minimal build `ffmpeg-8.1.2-amll-minimal-gpl.zip` with
its build closure; v0.2.1: `ffmpeg-8.1.2-source-offer/`); those bundles remain
available in the repository history and in the v0.2.0/v0.2.1 release materials.

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
