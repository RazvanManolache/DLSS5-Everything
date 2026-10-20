# Third-Party Notices

The main DLSS5 Everything project remains under the [MIT License](LICENSE). Dependencies retain their own licenses. Downloading a component does not relicense it under MIT, and these notices do not grant rights to NVIDIA models, game content or vendor runtimes.

## Distributed License Texts

Self-contained packages include a [license index](Licenses/README.md) and [notice inventory](Licenses/inventory.json). The packaging helper `scripts/Copy-DistributionNotices.ps1 -PackageRoot <published-app-folder> -JsonHeaderPath <host-build-json.hpp>` copies actual vendored texts and the exact runtime-pack licenses selected by the published app's runtimeconfig/deps metadata and restored NuGet package folders. `-ProjectAssetsPath` selects non-default build/restore output. No newest-version lookup or download is performed, and the inventory contains package identities and hashes, not private source paths.

Retained package files include [Dear ImGui MIT](Licenses/DearImGui/LICENSE.txt), [ReShade SDK MIT](Licenses/ReShade-SDK/LICENSE.txt) with [original header declarations](Licenses/ReShade-SDK/HEADER-NOTICES.txt), [nlohmann JSON MIT and component copyrights](Licenses/nlohmann-json/LICENSE.txt) with the [complete unchanged build header](Licenses/nlohmann-json/json.hpp.txt), [.NET Core license](Licenses/dotnet/Microsoft.NETCore.App/LICENSE.txt) and [third-party notices](Licenses/dotnet/Microsoft.NETCore.App/THIRD-PARTY-NOTICES.TXT), and [Windows Desktop runtime license](Licenses/dotnet/Microsoft.WindowsDesktop.App/LICENSE.txt). Any additional notice files present in the selected runtime packs are retained too. These package-relative links are generated during packaging, not model/runtime payload downloads.

## Phoenix (Apache-2.0)

[DLSS5 Phoenix](https://github.com/RazvanManolache/dlss5-phoenix-addon) is the external managed rendering add-on replacing RenoDX. Its original project code in [0.8-beta](https://github.com/RazvanManolache/dlss5-phoenix-addon/releases/tag/0.8-beta) is Apache-2.0. See the upstream [LICENSE](https://github.com/RazvanManolache/dlss5-phoenix-addon/blob/0.8-beta/LICENSE), [NOTICE](https://github.com/RazvanManolache/dlss5-phoenix-addon/blob/0.8-beta/NOTICE) and [third-party inventory](https://github.com/RazvanManolache/dlss5-phoenix-addon/blob/0.8-beta/THIRD_PARTY.md).

Phoenix binaries, embedded runners and model data are not bundled in this project's repository/package. The downloader retains available release license/notices in the local component folder. Preserve the actual upstream license, NOTICE and applicable dependency notices whenever redistributing Phoenix or adapted source; identify modifications as required. Phoenix's embedded vendor components retain their vendor terms.

The host build reads nlohmann JSON from an external Phoenix checkout. That header is MIT licensed with Niels Lohmann and component attributions retained in the header; preserve those notices with derived distributions.

## Windows LAN Codec

The optional Windows LAN codec worker compiles [Pyrowave](https://github.com/Themaister/pyrowave), [Granite](https://github.com/Themaister/Granite) and [Volk](https://github.com/zeux/volk), under their MIT licenses, with [Vulkan-Headers](https://github.com/KhronosGroup/Vulkan-Headers) declarations and applicable Apache-2.0/MIT texts. Pinned revisions and unmodified license texts are retained in [source/codec/licenses](source/codec/licenses/NOTICE.txt) and PC package notices. The wrapper's coefficient-envelope validation follows the upstream bitstream specification and validation algorithm. No Steam client code is copied and Steam is not required.

The Android companion, its Gradle wrapper, JNI/mobile shaders and APKs are maintained separately and are not distributed by this repository or release.

## NeuralScreen (Feature Reference Only)

Screen Studio's general capture feature reference is [NeuralScreen](https://github.com/perseval-BLR/NeuralScreen), whose implementation is under [PolyForm Strict 1.0.0](https://github.com/perseval-BLR/NeuralScreen/blob/4a9ee16d486668099a12296eabaa9ffe34704744/LICENSE). That implementation is not copied, linked, downloaded or redistributed here. Screen Studio uses independently written Windows SDK capture/presentation code and this project's existing Phoenix host. No NeuralScreen license is being represented as MIT, and its vendor DLLs are not part of this distribution.

## DLSS5 Swapper (MIT)

Selected native application features and emulator renderer metadata are adapted from or informed by [DLSS5 Swapper](https://github.com/rakanki911/DLSS5-Swapper). Common-action translations in `app/Dlss5CompatApp/UiLocalization.cs` are adapted from its `src/renderer/i18n.js` and `i18n-extra.js`. Emulator metadata in `EmulatorProfiles.cs` records source revision `9fb0b7c3563d350aa46eaa01d3e5ca4cbab4a7cb`; upstream also credits DLSS5-Autopilot's MIT detection table.

These reused features/locales do not imply inclusion of Swapper's hosted community/chat services, full feature parity, or its alternate rendering consumers. No Swapper binary payload is downloaded. The applicable retained MIT notice is:

> MIT License
>
> Copyright (c) 2026 Rakan Alkhaldi
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## Included Headers And Shaders

| Component | License / retained notice |
| --- | --- |
| ReShade SDK headers | BSD-3-Clause OR MIT; packages select MIT and retain Patrick Mours copyright/SPDX declarations from `source/external/reshade/include/` together with the full MIT permission/disclaimer |
| Dear ImGui | MIT; [license](source/external/imgui/LICENSE.txt), Omar Cornut and contributors |
| Spout2 SDK, compiled into Screen Studio worker | BSD-2-Clause; [license](source/external/spout/LICENSE), Lynn Jarvis; pinned revision `6b68a5b4fdeca6130b851e5f9366467a844860e5` |
| `runtime/shaders/ReShade.fxh` | CC0-1.0; SPDX header in the file |

## Separately Obtained Components

| Component | Source and terms |
| --- | --- |
| ReShade full-add-on runtime/setup | [ReShade](https://reshade.me/), upstream licenses; SDK header terms alone do not describe every runtime dependency |
| NVIDIA NGX/DLSS/DLSSNR, models and optional Streamline runtimes | Separately supplied NVIDIA/vendor terms; no model or vendor DLL redistribution by this repository |
| d3d8to9 | [crosire/d3d8to9](https://github.com/crosire/d3d8to9), upstream BSD-2-Clause terms |
| dgVoodoo2 | [dgVoodoo2](https://dege.freeweb.hu/dgVoodoo2/), upstream distribution terms |
| DOSBox Staging | [DOSBox Staging](https://github.com/dosbox-staging/dosbox-staging), GPL-2.0-or-later and dependency notices |
| NIGos legacy bridge | [dlss5-bridge](https://github.com/NIGos/dlss5-bridge), historical reference only; no longer downloaded or installed |
| 7-Zip helper | `7zip-bin@5.2.0` via unpkg for extraction only; retain the binary's applicable 7-Zip/component notices, distinct from wrapper package licensing |
| Optional XeSS, CUDA, HIP, SYCL and other provider runtimes | Supplied separately or within the external Phoenix release; consult that release's vendor/dependency notices |
| .NET runtime | [dotnet/runtime](https://github.com/dotnet/runtime), MIT and third-party notices; preserve runtime notices in self-contained distributions |
| FFmpeg / FFprobe (optional, user supplied) | [FFmpeg](https://ffmpeg.org/legal.html); applicable LGPL/GPL and codec terms depend on the user's build; not downloaded or redistributed |
| Khronos OpenXR SDK headers (CI/build only) | [OpenXR-SDK](https://github.com/KhronosGroup/OpenXR-SDK), Apache-2.0 or MIT declarations and upstream notices |

RenoDX and alternate GPL-3.0 neural-rendering consumers are outside this migration and are not installed or redistributed by it. Historical curated screenshots depict a previous consumer and are not Phoenix validation artifacts. Project licensing does not change the rights in depicted game content.
## DOSBox Enhanced and DOSBox Pure

DOS support uses [DOSBox Enhanced](https://github.com/RazvanManolache/dosbox-enhanced),
pinned as a separate Git submodule at `external/dosbox-enhanced` and launched as
a separate application. Its corresponding source, GPL license, DOSBox Pure
engine notices and dependency pins are retained in that repository. Its build
and component staging keep its license and third-party notices alongside the
runtime. NVIDIA graphics runtimes retain their separate vendor terms; the
neural-rendering model is supplied externally. The former DOSBox Staging /
ReShade feeder integration is no longer selected for DOS launches.

