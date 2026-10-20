# Phoenix DLSS5 Migration

This describes the current source during the migration, checked on 12 October 2026. The host now hands native-resolution textures directly to Phoenix DLSS5 instead of evaluating SR/DLAA first. No game has been launched for this migration. Build success, mock tests, hook installation and historical screenshots do not establish gameplay compatibility or visual quality.

## Published Build Compatibility

The checked `0.8-beta` release does not expose `CnrPHOENIXFeederVersion` / `CnrPHOENIXFeederEvaluate`. The direct feeder route requires those exports and runtime ABI version 1. Downloads and local imports now require both the direct feeder exports and the embedded C++ runtime before replacing the selected addon; incompatible cached builds are not considered ready. A rejected update leaves the current addon and its provenance intact. Other independent prerequisite downloads can still complete, but the bootstrap reports the incompatible release rather than success. The runtime host separately checks ABI 1; inspecting exports does not execute the addon.

The downloader retrieves genuine upstream releases and does not silently label a development build as a release. Until the direct-API build is published, use **Components > Import Phoenix DLSS5 build**. An older add-on produces an actionable compatibility error, not a silent SR/DLAA fallback. No Phoenix DLSS5 publication was made by this migration. Local builds can be repaired without opening the UI:

```powershell
.\Dlss5CompatApp.exe --components-import --payload .\Payload --phoenix C:\Components\phoenix.addon64 --model C:\Components\nvngx_dlssnr.dll
```

Both component paths are explicitly supplied. Either `--phoenix` or `--model` can be omitted; imports use the same validation/provenance/rollback path as the UI, and return a nonzero exit code on failure. Each component is committed independently, so a later model-import failure does not undo a completed addon import. Model imports remain separately licensed and runtime-validated; there is no automatic model download. Normal startup updates preserve explicitly imported development addons unless the user explicitly selects replacement.

Earlier isolated tests used the now-replaced SR/DLAA hook route. The initial float-texture test completed two native frames, recorded 308 own-network launches, and confirmed output writeback. Compared with an exact bypass capture, 2,728,565 RGB pixels changed (RGB mean absolute difference 6.564/255); bypass matched the decoded input exactly. Those measurements are historical, not direct-API validation.

Later synthetic IPC tests exercised the actual host server with CPU RGBA8 rows and shared GPU BGRA-to-RGBA textures/fences. Bypass was exact, iterations 1/2 completed native processing, and the two transports returned byte-identical images. The exact final host binary was also tested. These measurements establish returned processed output for the isolated cases, not NVIDIA DLSS5 equivalence, improved quality, frame rate or actual game compatibility. See the [validation checkpoint](phoenix-validation.md).

## Phoenix DLSS5 And Model Sources

The managed rendering add-on comes from [RazvanManolache/dlss5-phoenix-addon](https://github.com/RazvanManolache/dlss5-phoenix-addon). The default `LatestPublished` policy lists published releases, excludes drafts and selects by publication time, including prereleases. At this check, the latest publication is [`0.8-beta`](https://github.com/RazvanManolache/dlss5-phoenix-addon/releases/tag/0.8-beta), with `DLSS5-Phoenix-0.8-beta.zip` and `SHA256SUMS.txt`. GitHub's `/releases/latest` is not a reliable prerelease-inclusive selector. `StableOnly` excludes previews.

The downloader accepts a direct `phoenix.addon64` asset or a suitable uploaded ZIP containing it; source/debug/other-platform archives are excluded. It verifies available release digests/checksums, validates the x64 PE DLL and Phoenix DLSS5 exports, rejects Python imports, and records provenance in `phoenix-release.json`. Release notices are retained under `phoenix-notices/` in the local component folder. Rate-limited API lookup can use public release metadata; an unavailable release reports an error. Local development import is explicit and recorded as development provenance.

Components exposes **Latest (includes previews)** and **Stable releases only**. Downloads and their provenance/cache metadata stay in the selected local component folder, with `dlss5-payload-manifest.json` tracking bootstrap sources. Phoenix DLSS5 cache reuse verifies release asset identity, the add-on hash/PE and retained notice hashes before skipping a binary download. An explicitly imported local development build is preserved unless explicitly replaced; it is not silently exchanged for a published build. Other bootstrap components currently use source/version/size checks rather than Phoenix DLSS5's full hash verification; cache presence is not inference evidence.

NVIDIA DLSS5 model data is separate. Model/NGX import accepts supported x64 NVIDIA DLSS5 DLL names, including `nvngx_dlssnr.dll`; multiple files can be selected. Phoenix DLSS5 must still validate its model architecture/layout. PE validity or a known hash is not proof of compatible inference. No NVIDIA DLSS5 model/runtime DLL is automatically downloaded, and neither Swapper nor RenoDX is a payload source.

Feeder enhancement does not require an `nvngx_dlss.dll` SR runtime. The host seeds a native-resolution output texture with the original frame and calls Phoenix DLSS5's direct API with that texture, motion guides and optional depth. The separately supplied model is installed as `PhoenixModel.dll`. Optional DLSS SR/XeSS upscalers and the NVIDIA DLSS5 comparison backend have their own separately supplied runtimes and guide requirements; these are not an implicit preprocessing pass for Phoenix DLSS5 enhancement.

The bootstrap checks ReShade, Phoenix DLSS5, d3d8to9, dgVoodoo2 and DOSBox Enhanced. It no longer downloads the old NIGos consumer bridge; Vulkan uses this project's first-party feeder bridge.

## Native UI And Intentional Boundaries

The native .NET 9 / WinForms shell contains Library, Components, Add-ons, History, Settings and Resources. The library supports store discovery and folder scans, artwork, search/filtering, favorites/hiding, renderer overrides, launch arguments, profiles and install/play/restore. Components manages download status, release channels, explicit imports and cancellation. Add-ons manages local custom components; managed rendering add-ons cannot be imported through that path. History records actions/results, and Settings manages appearance and scan roots.

Swapper-derived emulator renderer metadata and common-action locale strings retain MIT attribution. Non-English translations are partial with English fallback; locale availability is not evidence that every native control has been translated. Emulator recognition and suggested renderer choices are metadata, not tested emulator compatibility.

Selected local workflows are implemented, with explicit boundaries:

- Hosted chat, cloud/community backends, accounts and upstream community-service parity are not included. Resource/issue links are ordinary external links.
- Alternate GPL-3.0 consumers, RenoDX installation, RenoDX-specific rendering controls and the old NIGos consumer route are outside the Phoenix DLSS5 migration.
- This is not a complete port of Swapper, and matching navigation or a passing service test is not full feature/runtime parity.
- The main project remains MIT. Phoenix DLSS5's Apache-2.0 terms and dependency/vendor licenses remain separate.

## Rendering Routes

### Direct D3D12 With Existing NGX

`GameCandidate.Route` selects `X64DirectPhoenix` only for x64 DirectX 12 with `HasNativeDlssSignal`. The scanner infers that signal from imports, neighboring NGX/Streamline files and executable markers. It does not observe the game making NGX calls.

The installer adds full-add-on ReShade and `Phoenix.addon64` beside the executable, copies the selected model to `PhoenixModel.dll`, and writes `Phoenix.json` and `[Phoenix]` controls. It preserves the game's existing NGX/Streamline libraries rather than replacing its `sl.*` or `nvngx_*` integration. Phoenix DLSS5 needs the game's actual D3D12 NGX SR/RR input calls to run.

### Feeder Fallback

x64 D3D12 without the detected native signal uses the x64 feeder directly. The retained `X64NativeThenFeeder` installer entry also delegates to that feeder route. There is no timed root-Phoenix DLSS5 probe or automatic transition from the direct route in this installer: generated feeder configs set `native_probe_seconds=0`.

x86/x64 D3D11 and DXGI also use the feeder. The in-process add-on captures frames and sends them to `host64/dlss5-feed-host64.exe`. D3D11 uses shared GPU resources when possible; D3D9, D3D12 and OpenGL use CPU readback/upload paths. D3D8 can use d3d8to9; older DirectX/DirectDraw and Glide can use dgVoodoo2 before feeding D3D11.

The host loads x64 ReShade and Phoenix DLSS5, reads its own `Phoenix.json`/`[Phoenix]` configuration and calls the versioned direct texture API. It never initializes NGX, creates an SR feature or evaluates DLAA. Queue identity, device identity and fresh resource states are validated before processing. The host waits for actual submission/completion evidence before returning enhanced output. A 32-bit game gets only an x86 feeder; the x64 consumer belongs in the host.

The direct API accepts native-size RGBA16F color and supported motion textures. An adapter handles RGBA8/BGRA8 transport without changing the SDR code-value domain. Bypass preserves the seeded input. Mono/left/right streams retain separate executors, timing and temporal history; switching is allowed only after the previous stream's observed fence has retired. Stereo currently supports Input/Phoenix DLSS5 sources only: NVIDIA DLSS5 comparison is refused because its NR eye features are not isolated. This state separation still needs real stereo-game validation.

Feeder-generated depth/motion may be estimated, zero or incomplete. Same-resolution frame processing does not imply genuine engine-integrated Super Resolution/Ray Reconstruction. CPU transfer, synchronization, stale output and missing guides affect both latency and quality.

### Experimental Routes

- **Vulkan x64:** ReShade's Vulkan layer plus this project's `phoenix-vulkan-feed.addon64` captures SDR color through a CPU bridge into the host. It replaces the old bridge consumer route and uses an empty effects preset. The general feeder's Vulkan attachment/probe code is not this transport. Vulkan x86 is unsupported.
- **DOS:** the launcher uses the DOSBox Enhanced submodule and its direct native Phoenix DLSS5 RTX library, with isolated profiles and no ReShade feeder. See [DOS integration](dosbox-enhanced-integration.md). Older Staging captures do not validate this path.
- **OpenVR:** a first-party loader shim forwards the original OpenVR API and loads ReShade; the matching feeder sends frames to the host.
- **OpenXR:** the first-party API layer intercepts D3D11 headset swapchains and feeds eyes to a host under `_DLSS5_OpenXR/`. This is not universal VR API support.

Check packaged native files before using these routes. The current app project explicitly publishes the first-party OpenVR/OpenXR DLLs and Vulkan bridge, which must exist before publishing. The host script builds the Vulkan bridge; separate scripts build the VR DLLs. First-party build outputs are distinct from external payloads. No experimental route has Phoenix DLSS5 gameplay validation here.

## Hardware And Performance

The [Phoenix DLSS5 upstream documentation](https://github.com/RazvanManolache/dlss5-phoenix-addon/blob/0.8-beta/README.md) reports native tests on RTX 5080 and a native bundle targeting `sm_120`. Do not generalize that to all RTX hardware.

Portable CUDA/Vulkan/HIP/CPU/SYCL runners depend on their compiled providers and supported devices. Upstream reports substantially slower complete-frame times, with CPU and some other routes taking seconds rather than real-time frame budgets. Its synthetic measurements have different timing boundaries from this feeder kit. No game-FPS gain, numerical parity, discrete AMD/Intel compatibility or dual-GPU performance is established here.

Selecting `native` or a portable device is configuration, not a capability test. Portable GPU execution requires a concrete device ID discovered by Phoenix DLSS5; a family name alone is insufficient. The `nvidia` runner selects Phoenix DLSS5's NVIDIA DLSS5 backend, not RenoDX. Compiled production runners do not need Python/Torch.

Feeder `iterations` (1..10 repeated evaluations) and Phoenix DLSS5 `RefinementPasses` (1..4) are separate controls. Increasing either increases work and does not promise better quality.

## Configuration Example

Use this template in the directory that actually loads Phoenix DLSS5. Replace all three uppercase placeholders; `INSTALL_DIRECTORY` must be that exact absolute directory, repeated identically. For feeder installs it is the host directory, not the game directory. The model path must be absolute. Obtain its digest with `Get-FileHash -LiteralPath '<model DLL>' -Algorithm SHA256` and use lowercase hexadecimal.

```json
{
  "schema": "phoenix-live/1",
  "armed": true,
  "input_provider": "feeder_d3d12",
  "direct_transfer": "srgb",
  "sandbox": "INSTALL_DIRECTORY",
  "in_place_game_directory": "INSTALL_DIRECTORY",
  "in_place_approval": "I approve driving this installed game in place",
  "weight_source": {
    "kind": "nvidia_dll",
    "path": "ABSOLUTE_MODEL_DLL_PATH",
    "sha256": "LOWERCASE_MODEL_SHA256"
  },
  "frame_limit": 4000000,
  "color_domain": "game_nr_sdr_proxy",
  "require_gpu_ready_file": false
}
```

This template is for a feeder host. Use `input_provider=game_dlss` for Phoenix DLSS5 beside a game that actually emits native NGX calls. This is an SDR proxy configuration, not an HDR validation claim. A supported model folder instead uses `"kind": "model_folder"` and an absolute directory containing `model.json`, without the DLL digest entry. Embedded production releases supply their compiled runtime internally. Development builds need matching `bundle` and `portable_cpp.runtime_root` paths; the latter must contain `phoenix_runtime_cpp.dll` and `phoenix_native_cpp.json`.

The app writes `[Phoenix]` in `ReShade.ini` and `ReShadeVR.ini`; its defaults enable backend 3 with `ExecutionDevice=native`, `ModelSource=configured` and `NeuralUplift=1`. Portable device identifiers must match the selected provider. A separate model copy lets the direct route keep the game's vendor DLLs intact.

## Build And CI

Windows and the .NET 9 SDK are required for the app/tests. Native rebuilding additionally needs the Visual C++ toolchain and Windows SDK:

- `build-addon32.bat` / `build-addon64.bat`: matching compiler architecture plus bundled ReShade/ImGui headers.
- `build-host64.bat`: a Phoenix DLSS5 source checkout through `PHOENIX_SOURCE_DIR` for its JSON header; no NGX SDK/import library is required. It also builds the Vulkan bridge.
- `build-openvr-shim64.bat` and `build-openxr-layer64.bat`: x64 compiler; the OpenXR layer needs Khronos OpenXR headers through `OPENXR_SDK_DIR`.

Current batch scripts contain a local Visual Studio Build Tools location. Adjust that location for your toolchain, or initialize the compiler environment and invoke their compiler commands. No model DLL is needed merely to compile the app or run mocked/unit tests.

The Windows CI workflow installs .NET 9, compiles the host, first-party VR DLLs and Vulkan bridge from source using pinned public headers where needed, builds the solution, runs tests, and checks a self-contained app publish. It also compiles/runs native host configuration, direct-API version/export/exception, format-adapter, synchronization-policy and transport-codec CPU contracts. It rejects external payloads in tracked files and published output. It uses the checked-in in-process feeder binaries; it does not rebuild those feeders, download a Phoenix DLSS5 add-on/model, launch games, execute neural inference or certify GPU behavior.

Distribute app publish output with README, migration guide, LICENSE and these notices. Never zip a populated working folder: keep `Payload/`, downloaded Phoenix/vendor binaries, private games, captures outside the curated gallery, caches, local install manifests and diagnostic reports out of the repository/package.

## Validation Still Required

A future Phoenix DLSS5 gameplay claim needs the exact add-on/model/runtime identities, actual inference evidence, returned processed frames, numerical/visual comparisons and clearly bounded latency measurements for each route/hardware combination. Until that exists, the preserved gallery remains historical consumer material and upstream Phoenix DLSS5 results remain upstream results.
