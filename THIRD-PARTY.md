# Third-party components

## Scope of the Rewind license

The root [LICENSE](LICENSE) applies to original Rewind source code and original scripts/documentation: Copyright (c) 2026 enddless8. It does **not** replace third-party licenses or automatically license fonts, imported SVGs, supplied artwork, adapted external code, or bundled executables. Approval of an asset's visual design does not itself establish redistribution rights.

The inventory below was checked against `Replay.csproj`, restored NuGet metadata, build scripts, font name tables, and the locally installed FFmpeg on 2026-10-10. There are no tracked third-party DLL/EXE files in the source repository. Local tools and generated distributions remain excluded from Git.

## .NET / WPF / Windows Forms

The application targets `net10.0-windows`; `bootstrap.ps1` downloads SDK 10.0.401. Microsoft/.NET Foundation and contributors license the original .NET runtime, WPF and Windows Forms code under MIT, with additional third-party components and notices. See [.NET runtime license](https://github.com/dotnet/runtime/blob/v10.0.0/LICENSE.TXT), [runtime notices](https://github.com/dotnet/runtime/blob/v10.0.0/THIRD-PARTY-NOTICES.TXT), [WPF](https://github.com/dotnet/wpf) and [Windows Forms](https://github.com/dotnet/winforms).

The existing build publishes a **self-contained** Windows application, so the distribution includes runtime binaries even though `.tools/` is not committed. Preserve the licenses and notices for the exact runtime/Windows Desktop versions shipped. The current build script copies README, this document and the FFmpeg LICENSE; it does not explicitly collect all runtime notices. This document alone is not a complete replacement for those notices. Packaging remains to be checked before distributing a binary release.

## NAudio

`NAudio.Wasapi` 2.3.0 is the direct NuGet dependency; `NAudio.Core` 2.3.0 is its only restored transitive package. Both are MIT, Mark Heath and contributors. The restored package metadata points to repository commit [`c89fee940ee6f8d7374d18714a6b85d8b7a18ab0`](https://github.com/naudio/NAudio/tree/c89fee940ee6f8d7374d18714a6b85d8b7a18ab0). Keep the upstream copyright and full MIT text below with distributions. Package metadata reports © Mark Heath 2026; the upstream license itself uses Copyright 2020 Mark Heath, retained below.

## FFmpeg and its bundled libraries — GPLv3, not Rewind MIT

FFmpeg: https://ffmpeg.org/ — Windows essentials build supplied by [Gyan](https://www.gyan.dev/ffmpeg/builds/). The installed build is `9.0.2-essentials_build-www.gyan.dev`; its configuration includes `--enable-gpl --enable-version3 --enable-static` and no `--enable-nonfree`. Its bundled LICENSE is the GNU GPL version 3. It includes external libraries such as x264, x265, libaom and others; their source, build information and applicable notices also need to be accounted for.

The Gyan README identifies FFmpeg source revision [`946fcce07b`](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b) and contains build configuration/library versions. Rewind invokes `ffmpeg.exe` and `ffprobe.exe` as separate processes; it does not link their libraries into the managed application. This does not remove the obligations for redistributing those binaries and does not relicense them under MIT.

Before binary redistribution:

- Preserve the GPL text, upstream notices, exact build identity and build information.
- Arrange GPLv3-compliant access to the complete Corresponding Source for the exact binaries, including required library sources and build scripts/patches. A generic upstream repository link or copying the GPL text is not, by itself, a complete compliance solution. Select the applicable GPLv3 section 6 distribution mechanism.
- Verify the notices and licenses of the bundled libraries, and retain applicable runtime/other component notices separately.

Sources: [FFmpeg legal information](https://ffmpeg.org/legal.html), [GPLv3](https://www.gnu.org/licenses/gpl-3.0.html), and the exact downloaded package's LICENSE/README. The FFmpeg legal page also contains an LGPL checklist; it must not be presented as the license of this GPLv3 build.

**Unresolved:** the repository does not contain a complete corresponding-source bundle or a documented distribution arrangement for the shipped build. `build.ps1` currently copies only FFmpeg/ffprobe and files matching `*LICENSE*`, not Gyan's build README or a complete source/notices package. `bootstrap.ps1` downloads the current release archive from a mutable URL without a version/checksum lock; the current local version is not a reproducible dependency lock. No binaries or release packaging were changed in this preparation pass.

## Fonts

All three font binaries are embedded as WPF resources. They are not covered by Rewind's MIT license.

| Asset | Metadata / license | Status |
| --- | --- | --- |
| `assets/fonts/Inter_28pt-Regular.ttf` | Inter 28pt Regular, version 4.001 / `66647c0bb`; Copyright 2016 The Inter Project Authors; SIL Open Font License 1.1 | Full upstream copyright/OFL text added as [LICENSE-Inter.txt](assets/fonts/LICENSE-Inter.txt), fetched from the corresponding source revision. Preserve it with font redistribution/embedding. |
| `assets/fonts/coolvetica.otf` | Coolvetica Regular, version 5.001; Copyright 1999–2022 Typodermic Fonts Inc.; Ray Larabie | Exact supplied EULA and evidence of software embedding/public font-file redistribution rights are absent. OS/2 `fsType=4` is preview/print metadata, not a substitute for permission. |
| `assets/fonts/CoolveticaLight.ttf` | Coolvetica Lt Regular, version 5.000; © 1999–2018 Typodermic Fonts Inc.; Ray Larabie | Exact supplied EULA and rights are absent. `fsType=0` does not establish copyright permission or extend a purchase license. |

Typodermic distinguishes fixed artwork from working font embedding and says the agreement supplied with the **exact style/copy** controls; a current agreement does not replace an earlier perpetual agreement. Do not assume a free desktop download covers embedding in a WPF application or publishing the OTF/TTF in a public repository. See [Typodermic licensing](https://typodermicfonts.com/license/) and [Coolvetica](https://typodermicfonts.com/coolvetica/). Obtain the original agreements and confirm both intended uses before public redistribution. No fonts were removed, converted, substituted or relicensed here.

## SVGs, application/tray artwork and adapted code

- `assets/figma/*.svg` are exports from the user-supplied Rewind Figma design. The repository has no author/license manifest identifying the origin and redistribution terms of each vector. Figma access alone does not grant rights to imported third-party icon libraries. Confirm provenance and preserve any required attribution; do not label all exports MIT without evidence.
- `assets/app/Rewind.png`, `Rewind.ico` and `assets/tray/RewindTray.png`, `RewindTray.ico` derive from user-approved raster artwork. The preparation scripts preserve that artwork. Its author/ownership and any separate redistribution license are not documented in the repository; the root MIT license does not automatically settle those rights.
- `CompressionProfile.cs` identifies a port of `MP4 COMPRESSOR/mp4compressor/ffmpeg_tools.py`; README also records imported preset logic. The originating project's license/ownership evidence is not present. Confirm the author/license and required attribution for copied/adapted portions before redistribution; do not automatically relicense another project's code.

These are provenance questions, not a claim that any asset is infringing. Required assets remain trackable for review and the current application is unchanged.

## Local tools and release readiness

Downloaded .NET SDK/runtime tools, FFmpeg, cached NAudio packages, local research/sample files and experimental FFmpeg builds live under ignored `.tools/` or `artifacts/`; they do not enter the first commit. The optional icon-preparation scripts require Python, Pillow and NumPy. Those external development dependencies are not vendored by this repository and retain their own licenses.

Source Git hygiene and permission to redistribute a full application package are separate checks. Public source publication still needs the Coolvetica/provenance decisions above; binary publication additionally needs the FFmpeg Corresponding Source arrangement and exact runtime/font/library notices. The existing publish script does not copy the new root LICENSE or standalone Inter license either; address the complete notice packaging in a separate release-preparation change.

NAudio MIT license:

Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
