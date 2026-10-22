# DLSS5 Everything

**Give your games a neural makeover. Then let the scope creep win.**

Games are the main course: bring DLSS5 neural rendering to more of your library,
from old favourites to modern titles. The screen, webcam and Android tricks?
Side dishes that got out of hand.

I do mean *Everything*: your games, your screen, your phone even—through a PC
proxy. Be afraid of what I'm going to think of next. Your printer is safe. For now.

**New in 3.1:** better legacy and software-rendered game installation, safer old-file
cleanup and component downloads that survive GitHub's API quota sulks. The installer
has been to compatibility school. It still gets homework.

- **Find your games:** store discovery, folder scans, favourites and a library ready to Play.
- **Install, verify, undo:** matching x86/x64 addons, rendering checks, route choices and tracked backups.
- **Old games welcome:** DirectX, OpenGL, Vulkan, legacy translators and DOSBox Enhanced have routes. Compatibility varies.
- **Make it yours:** live strengths, skin, comparisons, presets and supported network/FPS budgets.
- **Choose the model:** Original or Lightweight, compatible imports and optional shared downloads. One shelf, fewer giant DLL copies.

Powered by the open-source [Phoenix DLSS5 addon and library](https://github.com/RazvanManolache/dlss5-phoenix-addon).
**Native RTX is the main event.** Portability is our increasingly elaborate side quest.

[Get started](#getting-started) · [Game routes](#routes-and-limits) · [The extras](#and-then-the-side-dishes-escaped) · [Build](#distribution-and-build)

[**Download the Windows preview**](https://github.com/RazvanManolache/DLSS5-Everything/releases/tag/v3.1.0-preview.3)
· [What's new and what's still experimental](docs/releases/v3.1.0-preview.3.md).
The Android companion is a separate project; its APK and source are not included here.

## Games first. The clue is in your GPU bill.

![DLSS5 Everything game library with example titles, per-game controls and installation actions](docs/images/demos/games-library-demo.png)

Example library, simulated installation states—not a scan of someone's games or
a compatibility certificate. **Discover → Install → Verify & fix → Play.**
Tune each game without treating every session as a fresh science experiment.

## And then the side dishes escaped

- **Screen:** a window or the whole desktop. The browser video isn't safe either.
- **Camera: make yourself more real.** Start/Stop, live tuning, preview, Spout and optional virtual webcam.
- **Android:** send frames to your PC; get enhanced pixels back. Your phone takes all the credit.

Fictional scenes, **actual DLSS5 processing**, zero personal data.
[How these demos were made](docs/readme-demos.md).

### Your desktop: the video stays in its browser

![A fictional desktop with a YouTube-style browser and windowed travel video, before and after actual DLSS5 processing](docs/images/demos/desktop-dlss5-demo.jpg)

One made-up desktop, one normal video page, zero accidentally published browser history.

### Your phone: the PC does the heavy lifting

![A fictional Android travel app before and after PC-side DLSS5 processing](docs/images/demos/android-dlss5-demo.jpg)

The PC does the heavy lifting. The phone acts like this was its idea all along.

### Your webcam: make yourself more real

![Make yourself more real: a fictional webcam portrait before and after actual DLSS5 processing](docs/images/demos/webcam-dlss5-demo.jpg)

Apparently being made of actual atoms was only the starting preset.
Your GPU would like a second opinion.

## What is behind the pictures?

The source photos were AI-generated; Phoenix produced the enhanced halves.
Desktop and camera examples use the shared Screen Studio/Camera processing
pipeline. The staged Android example uses the PC-side phone backend directly,
not a physical phone or Wi-Fi session. These stills show processing, not live
latency or a promise that every scene looks better.
[Inputs, settings, hashes and provenance](docs/readme-demos.md).

## Powered by Phoenix

Compatible current Phoenix builds support:

- **Original and Lightweight FP8 models**, plus compatible external weights and
  shared native model folders. New architectures still need an implementation;
  dropping in an arbitrary model does not teach the runner a new language.
- **Tone, structure and skin tuning**, masks, appearance controls and comparison
  views. Engine-fed surface editing needs actual surface inputs from the game.
- **Network control:** block selection, repeats, passes and adaptive FPS/time
  budgets on supported routes. Spend less work when the frame budget gets tight.
- **Resolution and aspect controls**, spatial resizing and optional vendor
  upscalers where the integration and required runtimes support them.
- **Backend and device choices**, including native RTX and portable runners,
  with CPU thread/GPU selection where available.
- **A library as well as an addon**, so Screen Studio, Camera and the Android
  receiver can use Phoenix without installing it into a game.

Controls depend on the model, backend, input and Phoenix version. Everything
exposes the controls applicable to each workflow; camera color frames do not
magically acquire a game's depth, motion vectors or material IDs.
See Phoenix's [controls](https://github.com/RazvanManolache/dlss5-phoenix-addon/blob/main/docs/NEURAL_CONTROLS.md)
and [support matrix](https://github.com/RazvanManolache/dlss5-phoenix-addon/blob/main/docs/SUPPORT.md).

## Current integration notes

Current checkout: [optional shared native models](docs/optional-model-library.md) adds
opt-in Original and Lightweight export downloads. Weights are converted without
Python, installed once, and discovered by the matching current Phoenix build.

Current checkout: [internal runtime deployment](docs/phoenix-runtime-deployment.md)
retains matching optional runtime archives, installs the right x86/x64 companions,
and checks dependencies before accepting an explicitly selected internal mechanism.
Local addon imports include their sibling `PhoenixRuntime` directory.

Use a matching Phoenix package with the compatibility bridge, architecture-specific
addon and required companions. Feeder frames go directly to Phoenix at native
resolution by default, without an extra DLSS Super Resolution or DLAA pass.
Older direct-feeder builds retain their legacy host route. A newly published
Phoenix release is not automatically a qualified Everything release; Components
checks compatibility before replacing an imported build.
See [validation and remaining limits](docs/phoenix-migration.md).

Use Windows x64, compatible graphics drivers and the Microsoft Visual C++ x64 runtime required by Phoenix DLSS5. Self-contained app builds include .NET; the .NET 9 SDK is needed only for development.

## Getting Started

1. Extract an app build into a writable folder and run `Dlss5CompatApp.exe`.
2. Open **Components** and check for updates. **Latest (includes previews)** also includes prereleases; **Stable releases only** excludes them. Check the [Phoenix releases](https://github.com/RazvanManolache/dlss5-phoenix-addon/releases) for package requirements. You can explicitly import a compatible local build, including its companion runtime directory.
3. Choose a compatible model in **Components**, import a supported NVIDIA 
vngx_dlssnr.dll`, or opt into the [shared native model downloads](docs/optional-model-library.md) with a matching Phoenix build. Original and Lightweight offer different looks and costs. Optional native exports are off by default. The feeder does not require 
vngx_dlss.dll` for enhancement; optional vendor upscalers have separate runtime requirements.
4. Open **Discover**, find games, add an executable, or scan a folder. Choose **Install** to add Phoenix without launching the game. If renderer detection is uncertain, choose the installation route in the prompt; Cancel leaves game files unchanged. Then use **Verify & fix** to launch the installed game, check processed and presented frames through the add-on bridge, and close it. If the check fails, choose an alternative route to install and test. A successful check keeps the working installation; replacements always require your selection.
5. **My Games** is the startup library for installed titles, with its own search, favorites, sorting, and List/Previews views. Use **Play** and **Diagnostics** to inspect the result. **Controls** opens Phoenix DLSS5's live/offline settings; **Apply & save** sends and persists changes. **Compare** opens comparison settings. An installed addon or successful NGX call alone does not prove Phoenix inference. Close the game before installing or removing; **Remove** restores tracked original files from its validated backup manifest.

Updates cache components locally and record provenance; verified unchanged payloads
are reused. Downloads/imports check the direct feeder API and required embedded
resources before replacing the selected addon. Explicit local builds stay selected
until **Download Phoenix DLSS5** is confirmed. Optional internal/portable execution
mechanisms also need their matching runtime package; an addon alone does not
supply every optional mechanism. See [runtime deployment](docs/phoenix-runtime-deployment.md).
Python and Torch are not runtime requirements.

If an individual file is blocked, installation continues with the remaining
files and shows an **Installation incomplete** warning naming anything it could
not install. Check antivirus history or file permissions, then install again.
Partial installs are not reported as ready. See [blocked files and safe recovery](docs/partial-install-recovery.md).

## The App

<details>
<summary>Camera controls: the knobs behind the makeover</summary>

![Camera tab with Start/Stop, input selection, live controls and output choices; example device, camera stopped](docs/images/camera-studio.png)

Stopped preview with a fictional device. [Camera setup and output options](docs/camera-studio.md).

</details>

![Phoenix DLSS5 library preview using example game entries](docs/images/phoenix-library.jpg)

The screenshot uses a preview library, not a scan of a personal installation, and predates the My Games/Discover split.

The app opens on **My Games**, a separate list of installations with a valid installer manifest and the recorded files still present. Discovery filters do not affect it. **Refresh My Games** rechecks files, including installations removed outside the app. Missing or incomplete installations are not listed; installation evidence is not a claim of successful rendering. **Discover** is the scanning and installation workspace.

Use **List** or **Previews** beside the library filters to switch between the table and cover grid. Selection, filters and sorting are retained, and the view preference is remembered. The sort and artwork menus are next to these buttons. Actions requiring a selection or installed Phoenix DLSS5 configuration are disabled until available.

Automatic covers use Steam's local library artwork first, then its public store search and artwork CDN, following [Swapper's artwork approach](https://github.com/rakanki911/DLSS5-Swapper/blob/667d2a6f9108f2754ba96aac11071c6f32ec638d/src/steamart.js). Only game titles are sent for searches, never executable paths. Downloads are cached locally; custom covers take priority and weak matches keep a placeholder. Automatic downloads can be turned off in the artwork menu. Artwork remains publisher-owned and is not bundled in app builds.

| Page | Available workflow |
| --- | --- |
| My Games | Startup installed-game library, independent search/favorites/sorting, list or covers, refresh, play, Phoenix DLSS5 controls/comparison, removal and diagnostics |
| Discover | Store discovery, folder scans, search/filtering, favorites, hidden games, artwork, renderer/route/proxy overrides, profiles, launch arguments, component version inspection and installation |
| Screen Studio | Desktop/window enhancement without game installation: GPU capture, live controls, processing scale, 1..10 iterations, comparisons/effects, presets, screenshots, Spout and file conversion |
| [Camera](docs/camera-studio.md) | Camera selection, native Phoenix enhancement, live strengths/comparison, Start/Stop, separate preview, Spout output and an optional OBS Virtual Camera feed for calling apps |
| [Streamer / Android](docs/lan-frame-service.md) | Paired TLS LAN service, raw/Pyrowave transport, PC-side enhancement, Android app/follow capture, floating result overlay and live controls; preview with device/network limits |
| Components | External component updates, release channel, local Phoenix DLSS5/model/NGX imports, download cancellation and explicit per-user Vulkan layer setup/removal |
| Add-ons | Import compatible custom add-ons, enable/disable them and remove them from the local library |
| History | Installation activity and results, with copy actions |
| Settings | DLL storage policy, game startup renderer/model defaults, appearance, saved scan folders, hidden games and app update checks |
| Resources | Upstream project and issue links |

Selected Swapper features and common-action translations are reused under MIT. This is a native .NET UI, with partial translations falling back to English. Hosted chat/community services and alternate GPL-3.0 or RenoDX consumers are outside this migration; full Swapper parity is not claimed. See [migration details](docs/phoenix-migration.md) and [third-party notices](THIRD_PARTY_NOTICES.md).

See the [feature-by-feature Swapper audit](docs/swapper-feature-audit.md) for what was implemented, adapted or deliberately left out.

**Game defaults:** in Settings, choose **Phoenix DLSS5** or **NVIDIA DLSS5** and the
**Original** or **Lightweight FP8** neural model. **Copy NVIDIA DLL into each game**
keeps self-contained game copies; uncheck it to reference the downloaded/imported
component-library DLL instead. These choices are saved into Windows game startup
configuration on install/reinstall, including the packaged x86/x64 bridge and legacy
helpers. Existing per-game tuning is not reset merely by opening the launcher.
Lightweight requires a compatible addon and matching weights, from a supported
DLL or native export. Selecting it does not download a model; Components offers
separate opt-in downloads. [Storage, startup defaults and safe removal](docs/game-installation-defaults.md).

**Screen Studio** is for live viewing: choose **Entire display** or **Window**, select the target, and enable Phoenix. Basic tuning, comparison and **Apply & save** are visible on the page; advanced settings contain effects and output options. Screenshots, Spout, experimental HDR and image/video file conversion are available. Native-resolution processing is the default. Color-only sources use flat depth/zero motion, so this is neural image enhancement, not game-integrated temporal Super Resolution. No NeuralScreen code or binaries are reused. See the [Screen Studio guide](docs/screen-studio.md) for processing limits and separate FFmpeg requirements. Frame generation and optical-flow motion are intentionally omitted.

**LAN frame service and Android client (previews)** let another device send frames
to the Windows PC for Phoenix processing and receive the result with live
parameter updates. The separately maintained Phoenix Link Android client adds single-app
or follow-active capture and a floating result/control overlay. Raw RGBA and
Pyrowave transports use TLS certificate pinning and pairing. Real phone-to-PC
processing and emulator checks have passed in documented scenarios; sustained
Wi-Fi latency, thermals and broad device compatibility remain open. There is
also a dependency-free Python raw reference client. The Android APK and client
source are not distributed by this repository or release. See the [LAN protocol](docs/lan-frame-service.md)
for evidence and system restrictions. Protected apps can block capture.

<details>
<summary>Component management preview</summary>

![Phoenix DLSS5 component management at the minimum window size](docs/images/phoenix-components.jpg)

</details>

The [validation checkpoint](docs/phoenix-validation.md) separates automated tests, reviewer feedback and isolated inference evidence from untested gameplay routes.

## Routes And Limits

| Input | Current installation route |
| --- | --- |
| x64 D3D12 with a detected native NGX/DLSS signal | ReShade + Phoenix DLSS5 beside the executable; uses the game's existing NGX input path |
| x64 D3D12 without that signal | Packaged Phoenix DLSS5 backbuffer input; no separate installed feeder folder |
| x86/x64 D3D11 / DXGI | Matching packaged add-on; shared-texture inference with CPU transfer fallback |
| x86/x64 D3D9 and OpenGL | Matching packaged add-on; readback/upload compatibility bridge |
| x86 D3D8, DirectDraw / older DirectX, Glide | Separately supplied d3d8to9 or dgVoodoo2 -> matching packaged add-on |
| x86/x64 Vulkan | Architecture-matched ReShade Vulkan layer -> packaged Phoenix DLSS5 compatibility bridge |
| DOS | DOSBox Enhanced submodule -> direct native Phoenix DLSS5 RTX library; no ReShade |
| Supported VR routes | OpenVR/OpenXR bridge -> host; experimental |

The initial native-input choice comes from imports, files and markers, not observed inference. Runtime verification can select backbuffer input if the game never supplies usable native input. A verified route is saved against the executable path, content hash and architecture. Changing the executable invalidates that result. Game names are not routing rules. A 32-bit game receives the matching 32-bit add-on; its bundled 64-bit inference helper is materialized privately at runtime.

**Advanced** includes explicit Native/Feeder and DXGI/D3D11 proxy choices. Unsupported combinations are rejected. Vulkan installation selects the matching 32- or 64-bit layer and supplies its per-game launch environment through both **Play** and the generated launcher. Layer references have recovery state; **Remove Vulkan reference** preserves other games. Unknown third-party hooks remain explicit conflicts; recognized obsolete project hooks are backed up before retirement.

The baseline Phoenix native bundle targets **`sm_120`**. The local experimental addon/player packages also include **`sm_75`, `sm_86` and `sm_89`** for RTX 20/30/40, alongside RTX 50. Actual execution was checked only on RTX 5080; compilation does not certify the older physical cards. The additional NODIX/OpenNR DLL supplies compatibility device images for older RTX cards through the **NVIDIA DLSS5 backend**; it does not make Phoenix's own native kernels portable. Older-card runtime/quality/performance validation is still required. Portable CUDA/Vulkan/HIP/CPU/SYCL paths depend on their providers and are substantially slower, often unsuitable for real-time use. Upstream synthetic benchmarks are not measurements of this kit. CPU bridges add transfer cost, and estimated or missing motion/depth guides limit quality. This is not a promise of native game-integrated Super Resolution or Ray Reconstruction for older games.

See [route requirements and validation boundaries](docs/phoenix-migration.md), including packaging caveats for experimental Vulkan/VR routes.

## Distribution And Build

Repository packages contain this app's feeder, host, shaders, configs and documentation. **Do not include `Payload/` or external runtime binaries**: Phoenix DLSS5, NVIDIA DLSS5 model/NGX DLLs, ReShade, wrappers, DOSBox and vendor upscalers are obtained separately under their own terms. Vulkan uses this project's first-party bridge. See [runtime sources and versions](docs/nvidia-runtime-downloads.md).

Build on Windows. Useful downloads:

- [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) for the app.
- [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/)
  with **Desktop development with C++** and the
  [Windows SDK](https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/)
  for native helpers.
- [ReShade with full addon support](https://reshade.me/) and a compatible
  [Phoenix package](https://github.com/RazvanManolache/dlss5-phoenix-addon/releases)
  for running the workflows; model sources are configured separately.
- [Spout2 SDK](https://github.com/leadedge/Spout2) for building screen/camera sharing;
  [OBS Studio](https://obsproject.com/download) supplies the optional virtual-camera
  component for calling apps. Neither is needed just to browse your game library.
- [FFmpeg and FFprobe](https://ffmpeg.org/download.html) for video **file conversion**;
  they are not needed for live desktop or camera processing.

The native prerequisite guide below describes SDK locations and build order.

```powershell
dotnet --version
```

Then build, test and package the app:

```powershell
dotnet build Dlss5DxCompat.sln -c Release
dotnet test Dlss5DxCompat.sln -c Release
dotnet publish app/Dlss5CompatApp/Dlss5CompatApp.csproj -c Release -r win-x64 --self-contained true -o build/app
```

Native runtime files referenced by the app must exist before building. See [native build prerequisites and CI scope](docs/phoenix-migration.md#build-and-ci). Main project code remains [MIT](LICENSE); external Phoenix DLSS5 code is Apache-2.0. Their licenses do not grant rights to NVIDIA DLSS5 models or vendor binaries.

Complete packages also require `python scripts/Build-SoftwareReShade.py` for the
opt-in software-renderer hooks. This builds modified ReShade from pinned public
source; [the build and opt-in behavior are documented here](docs/software-renderer-hook.md).

For a complete distributable, build the native helpers, run
`scripts/Build-Pyrowave.ps1`, then
`scripts/Publish-Release.ps1`. It prepares the self-contained Windows ZIP,
and checksums under `local-release/`; it does not upload
anything. SDK/provider/model prerequisites remain separate.

## Credits And Sources

Thanks to the maintainers whose code, models and research this toolkit builds
on. Direct adaptations are credited separately from feature references.

| Upstream | Contribution and reuse |
| --- | --- |
| [ApolloF/DLSS5-Updater](https://github.com/ApolloF/DLSS5-Updater) | **Adapted source:** NVAPI DRS interop in `app/Dlss5CompatApp/NvDrs.cs`. The settings journal, rollback/conflict handling and UI are local additions. MIT [license and notice](licenses/ApolloF-NvDrs/NOTICE). |
| [rakanki911/DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper) | **Adapted source/data:** common-action translations and emulator renderer metadata in `UiLocalization.cs` and `EmulatorProfiles.cs`; also a workflow reference. Source revision and MIT copyrights are retained in the code and [notices](THIRD_PARTY_NOTICES.md#dlss5-swapper-mit). |
| [Kizzuwatnaa/DLSS5-Autopilot](https://github.com/Kizzuwatnaa/DLSS5-Autopilot) | **Inherited metadata attribution:** Swapper's emulator table credits Autopilot's MIT detection table. Its runtime catalogue was also studied. |
| [Benjamin-Wegener/dlss5-distill](https://github.com/Benjamin-Wegener/dlss5-distill) | **Published architecture and external checkpoint:** the optional tiny 2x model downloaded centrally, with a new native C++/HLSL port in Phoenix. The downloader/converter is local code; [Apache-2.0 attribution](licenses/tiny-distill/NOTICE) is retained. |
| [2600th/dlss5-video-player](https://github.com/2600th/dlss5-video-player) | **Adapted source in the separate Phoenix Video Player project:** native player/media/cache, comparisons, subtitles and UI foundation. Everything launches that companion; it does not incorporate the upstream application into its own MIT source tree. The player retains its MIT origin record and dependency notices. |

Additional components and source providers:

- [Phoenix DLSS5](https://github.com/RazvanManolache/dlss5-phoenix-addon/releases) for the rendering add-on and compiled runners.
- [ReShade](https://reshade.me/) and its [SDK](https://github.com/crosire/reshade) for injection, capture and add-on APIs.
- [bdsqlsz/DLSSNR3.19.16.0](https://huggingface.co/bdsqlsz/DLSSNR3.19.16.0) for optional Original/Lightweight native model exports. These are externally supplied NVIDIA-derived model data, not new models trained by this project.
- [NODIX-TECH/DLSS-5-MANAGER](https://github.com/NODIX-TECH/DLSS-5-MANAGER) for distributing the inspected [OpenNR](https://github.com/clshortfuse/openNR) compatibility runtime; NVIDIA created the underlying DLSS technology and models. The manager is not installed or executed.
- [kibblerz/DLSS5-Reshade-AIO](https://github.com/kibblerz/DLSS5-Reshade-AIO), [NVIDIA Image Scaling](https://github.com/NVIDIAGameWorks/NVIDIAImageScaling), [mauri870/dlssnr-pytorch](https://github.com/mauri870/dlssnr-pytorch), [DLSS-NR on Intel Xe2](https://github.com/Uzbekunknown/dlss-nr-on-intel) and [DLSSNR-AMD](https://github.com/mochizuki0323/DLSSNR-AMD) for components adapted in Phoenix's renderer; their source credits and notices are maintained by Phoenix.
- [d3d8to9](https://github.com/crosire/d3d8to9/releases), [dgVoodoo2](https://dege.freeweb.hu/dgVoodoo2/) and [DOSBox Enhanced](https://github.com/RazvanManolache/dosbox-enhanced) for older rendering APIs and DOS execution.
- [Pyrowave](https://github.com/Themaister/pyrowave), [Granite](https://github.com/Themaister/Granite) and [Volk](https://github.com/zeux/volk) for the optional native LAN codec stack; [Spout2](https://github.com/leadedge/Spout2) for shared-texture media interoperability. Their component notices are retained separately.
- [NVIDIA Streamline](https://github.com/NVIDIAGameWorks/Streamline), [Khronos Vulkan headers](https://github.com/KhronosGroup/Vulkan-Headers), [OpenXR SDK](https://github.com/KhronosGroup/OpenXR-SDK), [OpenVR](https://github.com/ValveSoftware/openvr), [Dear ImGui](https://github.com/ocornut/imgui), [nlohmann JSON](https://github.com/nlohmann/json) and [.NET](https://github.com/dotnet/runtime) for their APIs and development foundations.

Feature/design references include [NeuralScreen](https://github.com/perseval-BLR/NeuralScreen)
for Screen Studio, [OBS Studio](https://github.com/obsproject/obs-studio) for the
shared-memory camera protocol, [Lunarc-DLSS5](https://github.com/Yukikaze1945/Lunarc-DLSS5)
for native media/scaling options, and
[dlss5-vulkan-fg-relay](https://github.com/Yukikaze20170315/dlss5-vulkan-fg-relay)
for pipeline ordering and recovery. Those references do not mean their application
implementations are copied here; NeuralScreen's PolyForm Strict implementation
is not reused. Deferred features are not claimed as completed integrations.

These links are sources, not grants to redistribute vendor DLLs or models. See [third-party notices](THIRD_PARTY_NOTICES.md) for licensing boundaries.

## Historical Gallery

These preserved public captures show the **previous consumer**, before the Phoenix DLSS5 migration. The original/output comparisons and live-session images are historical material, not validated Phoenix DLSS5 results or current UI screenshots. They make no claim about Phoenix DLSS5 quality or performance.

<details>
<summary>Previous UI and consumer captures</summary>

![Historical installer UI, before the native Phoenix DLSS5 interface](docs/images/app-main.png)

| Historical original | Historical consumer output |
| --- | --- |
| ![Historical original capture](docs/images/cod4-1iter-normal.jpg) | ![Historical consumer output](docs/images/cod4-1iter-dlss.jpg) |
| ![Historical original capture](docs/images/cod4-2iter-normal.jpg) | ![Historical consumer output](docs/images/cod4-2iter-dlss.jpg) |
| ![Historical original capture](docs/images/spiderman-normal.jpg) | ![Historical consumer output](docs/images/spiderman-dlss.jpg) |
| ![Historical original capture](docs/images/compat-dos-doom2-normal.jpg) | ![Historical consumer output](docs/images/compat-dos-doom2-dlss.jpg) |
| ![Historical original capture](docs/images/compat-dos-skyroads-3d-normal.jpg) | ![Historical consumer output](docs/images/compat-dos-skyroads-3d-dlss.jpg) |
| ![Historical original capture](docs/images/compat-dos-indiana-jones-fate-of-atlantis-normal.jpg) | ![Historical consumer output](docs/images/compat-dos-indiana-jones-fate-of-atlantis-dlss.jpg) |
| ![Historical original capture](docs/images/compat-flatout-normal.jpg) | ![Historical consumer output](docs/images/compat-flatout-dlss.jpg) |
| ![Historical original capture](docs/images/compat-batman-normal.jpg) | ![Historical consumer output](docs/images/compat-batman-dlss.jpg) |
| ![Historical original capture](docs/images/compat-warcraft3-normal.jpg) | ![Historical consumer output](docs/images/compat-warcraft3-dlss.jpg) |
| ![Historical original capture](docs/images/compat-starcraft-normal.jpg) | ![Historical consumer output](docs/images/compat-starcraft-dlss.jpg) |
| ![Historical original capture](docs/images/compat-homeworld-normal.jpg) | ![Historical consumer output](docs/images/compat-homeworld-dlss.jpg) |
| ![Historical original capture](docs/images/compat-diablo2-normal.jpg) | ![Historical consumer output](docs/images/compat-diablo2-dlss.jpg) |
| ![Historical original capture](docs/images/compat-dangerous-curves-normal.jpg) | ![Historical consumer output](docs/images/compat-dangerous-curves-dlss.jpg) |
| ![Historical original capture](docs/images/compat-luna-normal.jpg) | ![Historical consumer output](docs/images/compat-luna-dlss.jpg) |

Historical live-session captures:

| Capture | Capture | Capture |
| --- | --- | --- |
| ![Historical consumer live-session capture](docs/images/compat-live-flatout.jpg) | ![Historical consumer live-session capture](docs/images/compat-live-hitman3.jpg) | ![Historical consumer live-session capture](docs/images/compat-live-luna.jpg) |
</details>

## Manual Installation

Prefer the app: it generates target-specific configuration and records backups. Manual changes are not covered by its restore manifest. Back up existing files and close the game first.

### Shared processing recipes and recovery

The [Phoenix Video Player](https://github.com/RazvanManolache/dlss5-phoenix-video-player)
page authors the native processing order, stage
switches, color treatment, multiscale branches, whole-network passes, central
block repeats, neural scale and optional FPS/time budgets. Models remain in
the central component library; the player receives an immutable recipe with
the selected model/runtime identity. Tiny 2x appears only when installed and
requests a 2x output. Adaptive processing requires monitoring; native offline
exports keep quality fixed and process every frame.
Target FPS, maximum processing time and minimum central blocks remain available
for both models with monitoring off. Those neural budgets are independent of the
optional pipeline governor and preserve their selected limits when monitoring
is toggled.

The game library's **Phoenix runtime recovery** action queues restoration of
the last working settings, disables processing, or chooses a verified installed
model while the game is closed. Phoenix consumes the request before provider
initialization. A requested model must establish sustained successful frames
before it replaces the last working configuration. This runtime journal is
separate from installation backups and driver-setting restoration.
The player page also exposes recovery for the companion application. Native
playback restores interrupted trials or initialization failures and promotes
working settings after 30 successful frames; ordinary appearance adjustments
do not start a structural trial.

The native chain and fusion have small-fixture execution checks for Original
and Lightweight on an RTX 5080. The bundled model-free player now includes its
matching native SDK, relative kernel paths and pinned YouTube/media helpers.
Native live segments, cache reuse/eviction and source-to-Present telemetry have
fixture checks. The processing budget excludes intentional prefetch and pauses;
physical display latency is not measured. The media processing-device selector
discovers native CPU/CUDA/Vulkan providers from the pinned model-free package.
Device discovery follows the component root and runtime-manifest identity.
Changed packages invalidate the inventory, obsolete asynchronous replies are
discarded, and failed discovery can be retried. **Refresh processing devices**
also queries the current drivers on demand. Launch is blocked while discovery is pending.
The player can switch them transactionally in its processing dialog. Both models
share the native D3D12 stage graph, including geometry changes, NIS/tiny scaling,
motion/scene services, fusion and latency policy. Portable inference uses native
workers and retains host transfer costs. CPU/CUDA and NVIDIA/AMD Vulkan have
fixture checks with D3D12 presentation on RTX 5080. AMD-only presentation,
unavailable HIP/SYCL/Metal devices and broader quality/performance remain
unverified. Addon insertion routes retain explicit capability limits.

The linked Phoenix pipeline report now includes 1080p timing/memory samples and
native-pixel face crops for both models and the optional stages. Fusion can
soften detail, and the current tiny scaler has visible artifacts and a high cost;
they remain experimental choices. Its host-transfer timings are separate from
the GPU-resident Phoenix/NVIDIA benchmark figures.

### Shared Phoenix DLSS5 Configuration

Obtain ReShade **with full add-on support**, the published Phoenix DLSS5 add-on and a compatible model separately. Keep Phoenix DLSS5's upstream license/notices. In the directory where Phoenix DLSS5 loads (the game directory for direct D3D12, or `host64/` for feeder routes):

1. Copy a compatible `phoenix.addon64` as `Phoenix.addon64` and the model DLL as `PhoenixModel.dll`. Feeder hosts require the direct feeder API, not the older published build.
2. Create `Phoenix.json` using the [configuration example](docs/phoenix-migration.md#configuration-example). Replace every placeholder with the exact absolute directory/model path and model SHA-256.
3. Add the following to `ReShade.ini` (and `ReShadeVR.ini` if used):

```ini
[ADDON]
AddonPath=.\
DisabledAddons=

[INPUT]
KeyOverlay=36,0,0,0
KeyScreenshot=0,0,0,0

[Phoenix]
Backend=3
ExecutionDevice=native
ModelSource=configured
NeuralUplift=1
ExternalReload=0
```

`ExecutionDevice=native` requires compatible native hardware. Portable routes need a concrete supported device selection; see the migration guide.

### Direct x64 D3D12 With Existing NGX

Install x64 ReShade as `dxgi.dll` beside the executable and complete the shared Phoenix DLSS5 configuration there. Preserve the game's existing 
vngx_*.dll` and `sl.*` integration; `PhoenixModel.dll` is a separate model copy. This route needs real D3D12 NGX SR/RR calls while playing. Copying Phoenix DLSS5 into an arbitrary D3D11 or non-DLSS game does not create that input path.

### Feeder And 64-bit Host

1. Put full-add-on ReShade matching the executable's architecture beside it: `d3d9.dll` for native D3D9, `opengl32.dll` for OpenGL, or `dxgi.dll` for D3D11/D3D12.
2. Copy the matching `runtime/x86-dx9-dx11/dlss5-feed.addon32` or `runtime/x64-dx9-dx11/dlss5-feed.addon64` beside the executable. Copy the matching `configs/dlss5-feed-32.cfg` or `dlss5-feed-64.cfg` as `dlss5-feed.cfg`; set 
ative_probe_seconds=0`, `compare_mode=0`, `render_scale=1.000`, `output_scale=1.000` and `iterations=1`.
3. Copy `runtime/shaders/DLSS5_Feed.fx` and `ReShade.fxh` into `reshade-shaders/Shaders/`. In the game's `ReShade.ini`, set `[GENERAL]` `EffectSearchPaths=.\reshade-shaders\Shaders\**`, `PresetPath=.\ReShadePreset.ini` and `PreprocessorDefinitions=RESHADE_DEPTH_INPUT_IS_REVERSED=1,DLSS5_MV_PROVIDER=0,DLSS5_GEOM_FIT=1` (use `DLSS5_GEOM_FIT=0` for OpenGL/native D3D9). Use the add-on/input settings above. Create `ReShadePreset.ini` with `[GENERAL]` `Techniques=DLSS5_Feed@DLSS5_Feed.fx`.
4. Create `host64/` beside the executable. Copy the rebuilt `runtime/host64/dlss5-feed-host64.exe` and x64 ReShade as `host64/dxgi.dll`.
5. Complete the shared Phoenix DLSS5 configuration **inside `host64/`**, with `input_provider=feeder_d3d12` and the model copy `PhoenixModel.dll`. No 
vngx_dlss.dll` is needed in the host for enhancement. The host requires Phoenix DLSS5's `CnrPHOENIXFeederVersion` and `CnrPHOENIXFeederEvaluate` exports and refuses an incompatible add-on; it does not fall back to DLAA. Do not reuse the old RenoDX INI section as Phoenix DLSS5 settings.
6. For x86 D3D8, add x86 d3d8to9 `d3d8.dll` and use the D3D9 route. For older DirectX/Glide, use the appropriate x86 dgVoodoo2 wrapper and the D3D11/DXGI feeder route.

Vulkan requires ReShade's registered Vulkan layer and this project's `phoenix-vulkan-feed.addon64` beside the executable, plus the configured host; it uses no feeder shader effects. DOS and VR need additional launcher/layer setup described in the migration guide. Keep these experimental routes distinct from a verified working installation.

### Thanks For The Pixels

Thanks to NVIDIA for the neural-rendering technology, [OpenNR](https://github.com/clshortfuse/openNR) for its compatibility work, and [NODIX](https://github.com/NODIX-TECH/DLSS-5-MANAGER) for distributing the compatibility edition. Upstream terms and [third-party notices](THIRD_PARTY_NOTICES.md) still apply.
