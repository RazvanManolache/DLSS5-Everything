# Runtime Layout

These are this project's native components, separate from downloaded external payloads:

| File | Role |
| --- | --- |
| `x86-dx9-dx11/dlss5-feed.addon32` | In-process x86 feeder |
| `x64-dx9-dx11/dlss5-feed.addon64` | In-process x64 feeder, including D3D12 fallback and experimental OpenGL |
| `host64/dlss5-feed-host64.exe` | Separate x64 D3D12 host feeding textures directly to Phoenix DLSS5 |
| `screen/dlss5-screen.exe` | First-party Windows Graphics Capture worker and click-through GPU presenter; experimental Screen Studio mode |
| `host64/phoenix-vulkan-feed.addon64` | Experimental Vulkan SDR CPU bridge to the host |
| `openvr/dlss5-openvr-shim64.dll` | First-party OpenVR forwarding/ReShade loader shim |
| `openxr/XR_APILAYER_DLSS5_everything.dll` and `.json` | First-party D3D11 OpenXR API layer and manifest |
| `shaders/DLSS5_Feed.fx`, `shaders/ReShade.fxh` | Feeder shader and ReShade include |

Some native components are generated locally; source presence does not mean every binary is tracked or up to date. Build the matching components before app publish. See [source/build notes](../source/README.md).

## External Components

Use **Components** in the native app to choose **Latest (includes previews)** or **Stable releases only**. The Phoenix DLSS5 source is [RazvanManolache/dlss5-phoenix-addon](https://github.com/RazvanManolache/dlss5-phoenix-addon); the latest published release checked on 11 October 2026 is [0.8-beta](https://github.com/RazvanManolache/dlss5-phoenix-addon/releases/tag/0.8-beta), a prerelease. Release provenance and verified Phoenix DLSS5 cache metadata are local to the selected component folder.

Provide a compatible model separately (`nvngx_dlssnr.dll`, installed as `PhoenixModel.dll`). Feeder enhancement does not need an `nvngx_dlss.dll` SR runtime. It requires a Phoenix DLSS5 build with direct feeder API v1; import that build explicitly until it is available in a published release. NVIDIA DLSS5 files are not downloaded automatically. The native game-DLSS route preserves the game's NGX/Streamline DLLs.

ReShade **with full add-on support** comes from [ReShade](https://reshade.me/). Routes for older APIs additionally use [d3d8to9](https://github.com/crosire/d3d8to9), [dgVoodoo2](https://dege.freeweb.hu/dgVoodoo2/) or [DOSBox Staging](https://www.dosbox-staging.org/). The Phoenix DLSS5 Vulkan route uses this project's bridge, not a legacy consumer download.

Do not place external payloads in the repository or app package. Keep them in the local `Payload/` component folder under their own terms. No RenoDX download/install or Swapper payload is required.

## Configuration And Evidence

The old host INI/preset templates are historical legacy defaults, not a Phoenix DLSS5 configuration. The migrated installer generates `Phoenix.json` and `[Phoenix]` controls in the directory loading Phoenix DLSS5. For manual setup use the [current installation guide](../README.md#manual-installation) and [configuration example](../docs/phoenix-migration.md#configuration-example).

Native Phoenix DLSS5 kernels currently target `sm_120`; portable runners have provider-specific requirements and substantial performance limits. Isolated host evidence is separate from gameplay acceptance. Curated public screenshots show only the historical consumer. See [migration boundaries](../docs/phoenix-migration.md) and [third-party notices](../THIRD_PARTY_NOTICES.md).
