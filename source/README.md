# Source Tree

This is the native bridge side of the Phoenix DLSS5 migration; the native .NET 9 / WinForms application is in [app/Dlss5CompatApp](../app/Dlss5CompatApp/).

| Source | Purpose |
| --- | --- |
| `src/dlss5-feed32.cpp` | Shared x86/x64 ReShade feeder, CPU/shared-resource transports, feeder controls |
| `src/feed_ipc.h` | Feeder/host IPC contract |
| `screen/screen-worker.cpp`, `screen/screen-contract.h` | Independent Windows Graphics Capture frontend, GPU presenter, shared-host transport and capture settings contracts |
| `host/dlss5-feed-host64.cpp` | x64 D3D12 host, Phoenix DLSS5 configuration/arming and direct texture input |
| `host/phoenix-feeder-api.h` | Versioned direct Phoenix DLSS5 frame contract; no NGX feature required |
| `host/phoenix-host-config.h` | CPU-testable Phoenix DLSS5 host configuration validation |
| `host/phoenix-vulkan-feed.cpp` | Experimental Vulkan SDR CPU bridge |
| `openvr-shim/openvr_api_proxy.cpp` | OpenVR forwarding/ReShade loader shim |
| `openxr-layer/openxr_layer.cpp` | D3D11 OpenXR API layer |
| `shaders/DLSS5_Feed.fx` | Feeder shader source; distributable shader lives under `runtime/shaders/` |
| `external/` | Included ReShade/ImGui headers and optional locally supplied SDKs |

## Build

Use Windows, Visual C++ Build Tools and the Windows SDK. From the repository root:

```bat
build-addon32.bat
build-addon64.bat
build-host64.bat
source\screen\build-screen.bat
build-openvr-shim64.bat
build-openxr-layer64.bat
```

The batch scripts currently name a local Visual Studio Build Tools location. Adapt it to your installed toolchain or use their compiler commands from the appropriate developer environment. The x86 and x64 feeder builds need matching compiler architectures.

The host requires `PHOENIX_SOURCE_DIR` pointing to a Phoenix DLSS5 checkout with `third_party/nlohmann/json.hpp`, but no NGX SDK or import library. Its build script also compiles the Vulkan bridge. The OpenXR layer requires `OPENXR_SDK_DIR` containing Khronos headers. The repository does not supply the external model or vendor runtimes.

The Screen Studio worker additionally uses Windows SDK C++/WinRT headers, C++20 and the pinned BSD Spout SDK. Run `scripts/Fetch-ScreenDependencies.ps1` before building, or set `SPOUT_SOURCE_DIR` to that revision's checkout. Its CPU contracts run during the worker build; GPU sharing and capture/inference tests require explicit opt-in. No NeuralScreen implementation is copied or linked. See [capture configuration, limits and validation](../docs/screen-studio.md).

The app publish includes the native files declared in its project; build them before publishing. Windows CI builds the direct host, first-party VR DLLs and Vulkan bridge, then builds/tests/publishes the .NET app. It uses the checked-in in-process feeder binaries and does not validate GPU execution.

## Consumer And Validation Boundary

### Legacy COM helper build

The process-local legacy bridge is built from `src/phoenix-legacy-com.cpp`.
Install Python with `pefile==2024.8.26`, the Visual C++ x86/x64 tools and the
Windows SDK, and prepare a local component folder containing matching dgVoodoo
`MS/x86/DDraw.dll` and `MS/x64/DDraw.dll` export contracts. Then run:

```powershell
python tools/build_legacy_com.py --payload C:\Components\Payload
```

The builder runs 20 CPU contracts per architecture, checks exact named/ordinal
export forwarding, and generates `runtime/legacy-com/PhoenixLegacyDDraw32.dll`
and `PhoenixLegacyDDraw64.dll`. The release packager requires both. A source-only
managed build can omit them; installation of a route requiring the proxies
fails preflight when the corresponding runtime is absent. No vendor renderer
or model is added to the source tree.

Phoenix DLSS5 is the managed rendering consumer. The native game-DLSS route uses an existing NGX SR/RR input path. Other implemented routes feed a separate x64 host using Phoenix DLSS5's versioned direct texture API, without an SR/DLAA evaluation. A separately supplied compatible model is still required. RenoDX is not an active runtime requirement, and Swapper is only a feature/metadata/locale reference with MIT attribution.

Configuration uses `Phoenix.json` and `[Phoenix]`, not the historical consumer's INI section. Hosts use `input_provider=feeder_d3d12`; actual game-DLSS interception uses `game_dlss`. Old runtime templates and curated public comparisons are historical material. Isolated host evidence does not establish gameplay compatibility, image quality or performance.

See [installation](../README.md), [route/configuration and CI details](../docs/phoenix-migration.md), and [licenses/notices](../THIRD_PARTY_NOTICES.md). Main project code remains MIT; external Phoenix DLSS5 code is Apache-2.0.
