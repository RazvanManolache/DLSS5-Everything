# dlss5-dx9-dx11-x86compat

Experimental compatibility kit for running RenoDX DLSS 5 Neural Rendering with older 32-bit games.

This repository is intentionally narrow. It contains the 32-bit ReShade feeder add-on, the 64-bit helper process it talks to, the ReShade feed shader, small install/config helpers for 32-bit D3D11 and D3D9-through-dgVoodoo2 testing, and selected result captures from local tests.

For the tested 32-bit paths, the bridge is not just loading: logs show the host creates/evaluates DLSS work, and paired captures show the processed output is being blitted back into the game. New games still need per-game log validation, especially for depth, motion vectors, and masks.

## What We Actually Tested

Observed locally:

- 32-bit D3D11 path: game loads 32-bit ReShade, `dlss5-feed.addon32` attaches, the feed shader compiles, the 64-bit helper starts, shared textures/fences connect, and the helper logs DLSSNR feature creation/evaluation.
- D3D9 path through dgVoodoo2: D3D9 game is translated to D3D11 first, then the same 32-bit feeder/64-bit helper path runs.
- F9 comparison cycling works on the 32-bit path: original, DLSS output, split, amplified difference, and difference/DLSS.
- PrintScreen capture support is present for saving original and processed frames when the feed path is active. The expected setup is `hotkey_screenshot=44` in `dlss5-feed.cfg` and `KeyScreenshot=0,0,0,0` in the game's `ReShade.ini`, so the feeder owns PrintScreen instead of ReShade. The x86 feeder suppresses Windows' PrintScreen/Snipping Tool path while the game is focused.
- Captured output in Call of Duty 4 and Spider-Man 3 shows visible processed-frame output from the feeder path. The strongest improvement is on character faces and close-up surface detail; older DX9 scenes still depend heavily on the quality of generated depth, motion vectors, and validation/bias masks.

## Captured Results

These are local captures from the x86 feeder path. The images are included as evidence that the bridge can return visible DLSS output to real 32-bit games, not just initialize the helper process.

### Call of Duty 4: One Iteration

This capture used one host evaluation pass. The left image is the feeder's paired normal capture; the right image is the feeder's DLSS output capture from the same frame.

| Feeder normal capture | Feeder DLSS output |
| --- | --- |
| ![Call of Duty 4 one-iteration normal capture](docs/images/cod4-1iter-normal.jpg) | ![Call of Duty 4 one-iteration DLSS output](docs/images/cod4-1iter-dlss.jpg) |

### Call of Duty 4: Two Iterations

This capture used two host evaluation passes on the same delivered frame.

| Feeder normal capture | Feeder DLSS output, two iterations |
| --- | --- |
| ![Call of Duty 4 two-iteration normal capture](docs/images/cod4-2iter-normal.jpg) | ![Call of Duty 4 two-iteration DLSS output](docs/images/cod4-2iter-dlss.jpg) |

### Spider-Man 3

This capture uses the DX9-to-D3D11 path through dgVoodoo2, then the same x86 feeder and 64-bit host bridge.

| Feeder normal capture | Feeder DLSS output |
| --- | --- |
| ![Spider-Man 3 normal capture](docs/images/spiderman-normal.jpg) | ![Spider-Man 3 DLSS output](docs/images/spiderman-dlss.jpg) |

## Included Files

- `source/src/dlss5-feed32.cpp` - 32-bit ReShade add-on.
- `source/src/feed_ipc.h` - shared IPC/texture contract used by the 32-bit add-on and 64-bit helper.
- `source/host/dlss5-feed-host64.cpp` - 64-bit helper process that runs NGX/DLSS work for 32-bit games.
- `source/shaders/DLSS5_Feed.fx` - ReShade shader that exposes color/depth/motion-vector/mask inputs to the feeder.
- `runtime/x86-dx9-dx11/dlss5-feed.addon32` - built 32-bit feeder add-on.
- `runtime/host64/dlss5-feed-host64.exe` - built 64-bit helper.
- `runtime/shaders/DLSS5_Feed.fx` - runtime copy of the feed shader.
- `configs/dlss5-feed-32.cfg` - default 32-bit feeder config.
- `configs/dgVoodoo-dx9.conf` - minimal dgVoodoo2 config for DX9-to-D3D11 wrapping.
- `setup-dx9-dgvoodoo.ps1` - helper script for staging dgVoodoo2 wrapper files when you already have dgVoodoo2 locally.
- `docs/images/` - selected local result captures used by this README.

## Not Included

The repository does not redistribute third-party runtime binaries:

- ReShade DLLs.
- NVIDIA NGX/DLSS DLLs such as `nvngx_dlss.dll` and `nvngx_dlssnr.dll`.
- RenoDX DLSS5 add-on binaries.
- dgVoodoo2 binaries.
- Game files, logs, cache files, or local machine paths.

Only the selected comparison images in `docs/images/` are intentionally included.

The local working folder may contain some of those files for testing, but `.gitignore` keeps them out of the public repository.

## Required Downloads

Fetch third-party pieces from their original projects or community release channels. This repository does not mirror or redistribute them.

| Package | Link | Files Needed |
| --- | --- | --- |
| ReShade with full add-on support | [reshade.me](https://reshade.me/) | 32-bit ReShade `dxgi.dll` for the game folder, and 64-bit ReShade `dxgi.dll` for `host64/`. Use the unsigned full-add-on build. |
| dgVoodoo2 | [official site](https://dege.freeweb.hu/dgVoodoo2/) / [GitHub releases](https://github.com/dege-diosg/dgVoodoo2/releases) | For DX9 games: `MS/x86/D3D9.dll`, `dgVoodoo.conf`, and optionally `dgVoodooCpl.exe`. |
| NVIDIA DLSS SDK / runtime | [NVIDIA/DLSS releases](https://github.com/NVIDIA/DLSS/releases) / [NVIDIA Streamline SDK](https://developer.nvidia.com/rtx/streamline/get-started) | `nvngx_dlss.dll` in `host64/`. NVIDIA's public SDK/Streamline packages are the clean source for this file. |
| NVIDIA DLSS Neural Rendering runtime | [NVIDIA DLSS developer page](https://developer.nvidia.com/rtx/dlss) when officially available; otherwise use a legitimate runtime source you have rights to use | `nvngx_dlssnr.dll` in `host64/`. At the time this README was written, this file was not available as a normal public NVIDIA SDK download, so verify provenance and signature yourself. |
| RenoDX DLSS5 add-on | [RenoDX Discord](https://discord.com/invite/renodx), [RenoDX project](https://github.com/clshortfuse/renodx), or the community [renodx-dlss-installer release](https://github.com/yumlevi/renodx-dlss-installer/releases/tag/latest) | `renodx-dlss5.addon64` in `host64/`. Use the version that matches the DLSSNR runtime you are testing. |
| NIGos DLSS5 bridge | [NIGos/dlss5-bridge](https://github.com/NIGos/dlss5-bridge) | Reference/alternate bridge for native 64-bit DX11 DLSS-call forwarding. Not required by this x86 compatibility package. |

Security/provenance notes:

- Prefer official project pages and signed binaries.
- Do not mix random DLLs from old packages. A mismatched `renodx-dlss5.addon64`, `nvngx_dlss.dll`, and `nvngx_dlssnr.dll` can load but fail during feature creation.
- This project was built for x86 games. Native 64-bit DX11/DX12 games should use the normal RenoDX/DLSS5 path or an upstream 64-bit feeder/bridge setup instead of this x86 helper.

## Shared Host Setup

Both supported install paths below use the same `host64/` helper folder.

1. Create `host64/` next to the game executable.
2. Copy `runtime/host64/dlss5-feed-host64.exe` into `host64/`.
3. Install 64-bit ReShade with full add-on support into `host64/` by targeting `host64/dlss5-feed-host64.exe` and choosing DirectX 10/11/12. The result should be `host64/dxgi.dll`.
4. Copy `renodx-dlss5.addon64` into `host64/`.
5. Copy `nvngx_dlss.dll` and `nvngx_dlssnr.dll` into `host64/`.
6. Create or edit `host64/ReShade.ini` so screenshots do not get captured by the hidden helper window:

```ini
[GENERAL]
EffectSearchPaths=.\reshade-shaders\Shaders\**
TextureSearchPaths=.\reshade-shaders\Textures\**
PresetPath=.\ReShadePreset.ini

[INPUT]
KeyOverlay=36,0,0,0
KeyScreenshot=0,0,0,0
```

7. Optional: put host-side RenoDX defaults in `host64/ReShade.ini`. The in-game `DLSS 5 Feed` add-on page can also write these and restart the helper:

```ini
[RenoDX.DLSS5]
NeuralUplift=1
NREnableUpscaling=1
NRPreset=2
NRStyle=1
NRIntensity=2.000000
NRLocalStructure=2.000000
NRLocalTone=2.000000
NRSkinStructure=2.000000
NRAutoMask=1
NRUICorrection=1
NRDepthMode=1
NRMVecScaleX=2
NRMVecScaleY=2
```

## Install Shape: 32-bit D3D11

Use this for a 32-bit game that already renders through D3D11/DXGI.

1. Back up the game folder files you are about to replace.
2. Install 32-bit ReShade with full add-on support into the game folder. Target the game `.exe` and choose DirectX 10/11/12. The result should be a 32-bit ReShade `dxgi.dll` next to the game executable.
3. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` next to the game executable.
4. Copy `configs/dlss5-feed-32.cfg` next to the game executable as `dlss5-feed.cfg`.
5. Copy `runtime/shaders/DLSS5_Feed.fx` into `reshade-shaders/Shaders/`.
6. Install and enable a ReShade motion-vector/depth provider above `DLSS5_Feed`. In our tested setups, `DLSS5_MV_PROVIDER=1` used Marty McFly's Launchpad path.
7. In the game `ReShade.ini`, merge the feeder preprocessor definition with any existing ReShade definitions, and disable ReShade's own screenshot key:

```ini
[GENERAL]
PreprocessorDefinitions=RESHADE_DEPTH_INPUT_IS_REVERSED=1,DLSS5_MV_PROVIDER=1

[INPUT]
KeyScreenshot=0,0,0,0
```

8. Complete the shared `host64/` setup above.
9. Start the game and open ReShade. Enable the motion-vector provider technique first, then enable `DLSS5_Feed`.
10. Use F9 to cycle display modes. Use PrintScreen for paired `normal` and `dlss` BMP captures from the feeder.
11. Verify `dlss5-feed.log` and `host64/dlss5-feed-host.log` before judging image quality.

Expected game folder shape:

```text
GameFolder/
  Game.exe
  dxgi.dll                         <- 32-bit ReShade with add-on support
  dlss5-feed.addon32               <- from runtime/x86-dx9-dx11/
  dlss5-feed.cfg                   <- copied/adapted from configs/dlss5-feed-32.cfg
  reshade-shaders/
    Shaders/
      DLSS5_Feed.fx                <- from runtime/shaders/
      ... motion-vector/depth shader files, if used
  host64/
    dlss5-feed-host64.exe          <- from runtime/host64/
    dxgi.dll                       <- 64-bit ReShade with add-on support
    renodx-dlss5.addon64           <- external RenoDX DLSS5 add-on
    nvngx_dlss.dll                 <- external NVIDIA runtime DLL
    nvngx_dlssnr.dll               <- external NVIDIA runtime DLL
    ReShade.ini                    <- created/managed by ReShade
```

## Install Shape: DirectX 9

DirectX 9 is not handled directly by the feeder. The tested path is:

```text
D3D9 game -> dgVoodoo2 -> D3D11 -> 32-bit ReShade -> dlss5-feed.addon32 -> host64 helper
```

Use this for a 32-bit DirectX 9 game.

1. Back up the game folder files you are about to replace.
2. Download and extract dgVoodoo2.
3. Copy `MS/x86/D3D9.dll` from dgVoodoo2 into the game folder. It must be the x86 wrapper for a 32-bit game.
4. Copy `dgVoodoo.conf` into the game folder. If you use `dgVoodooCpl.exe`, set:
   - Output API: Direct3D 11.
   - Adapter: your GPU, or the default adapter if that is the only stable option.
   - Resolution: unforced/native unless the game needs a fixed override.
   - dgVoodoo watermark: off.
5. Install 32-bit ReShade with full add-on support into the game folder as DirectX 10/11/12. Do not install ReShade as `d3d9.dll`, because dgVoodoo2 owns that filename.
6. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` next to the game executable.
7. Copy `configs/dlss5-feed-32.cfg` next to the game executable as `dlss5-feed.cfg`.
8. Copy `runtime/shaders/DLSS5_Feed.fx` into `reshade-shaders/Shaders/`.
9. Install and enable a ReShade motion-vector/depth provider above `DLSS5_Feed`.
10. In the game `ReShade.ini`, merge `DLSS5_MV_PROVIDER` with any existing ReShade preprocessor definitions and set `KeyScreenshot=0,0,0,0`.
11. Complete the shared `host64/` setup above.
12. Start the game and check `ReShade.log`. It should show D3D11/DXGI hooks. If it logs native `IDirect3DDevice9`, dgVoodoo2/ReShade is not chained correctly.
13. Enable the provider technique and `DLSS5_Feed`, then use F9/PrintScreen as in the D3D11 path.

If ReShade logs native `IDirect3DDevice9` instead of D3D11, this path is not set up correctly.

Expected DX9 game folder shape:

```text
GameFolder/
  Game.exe
  D3D9.dll                         <- 32-bit dgVoodoo2 D3D9 wrapper
  dgVoodoo.conf                    <- copied/adapted from configs/dgVoodoo-dx9.conf
  dxgi.dll                         <- 32-bit ReShade with add-on support
  dlss5-feed.addon32               <- from runtime/x86-dx9-dx11/
  dlss5-feed.cfg                   <- copied/adapted from configs/dlss5-feed-32.cfg
  reshade-shaders/
    Shaders/
      DLSS5_Feed.fx                <- from runtime/shaders/
      ... motion-vector/depth shader files, if used
  host64/
    dlss5-feed-host64.exe          <- from runtime/host64/
    dxgi.dll                       <- 64-bit ReShade with add-on support
    renodx-dlss5.addon64           <- external RenoDX DLSS5 add-on
    nvngx_dlss.dll                 <- external NVIDIA runtime DLL
    nvngx_dlssnr.dll               <- external NVIDIA runtime DLL
    ReShade.ini                    <- created/managed by ReShade
```

## Install Shape: Native 64-bit D3D11/D3D12

Use this for a normal 64-bit DX11 or DX12 game. This does not need the x86 feeder bridge; it is the direct RenoDX/ReShade add-on route.

1. Back up the game folder files you are about to replace.
2. Install 64-bit ReShade with full add-on support into the game folder. Target the game `.exe` and choose DirectX 10/11/12. The result should be a 64-bit ReShade `dxgi.dll` next to the game executable.
3. Copy `renodx-dlss5.addon64` next to the game executable.
4. Copy `nvngx_dlss.dll` and `nvngx_dlssnr.dll` next to the game executable, unless the RenoDX/DLSS5 package you are using documents a different subfolder layout.
5. If the RenoDX/DLSS5 package includes Streamline support files, keep those files together exactly as that package ships them.
6. Start the game, open ReShade, and confirm the RenoDX DLSS5 add-on page appears.
7. Enable DLSS5/Neural Rendering from the RenoDX add-on page and tune the RenoDX settings there.
8. Verify ReShade logs before judging the image. The log should show the 64-bit RenoDX DLSS5 add-on loading in the game process.

Expected native 64-bit game folder shape:

```text
GameFolder/
  Game.exe
  dxgi.dll                         <- 64-bit ReShade with add-on support
  renodx-dlss5.addon64             <- external RenoDX DLSS5 add-on
  nvngx_dlss.dll                   <- external NVIDIA runtime DLL
  nvngx_dlssnr.dll                 <- external NVIDIA runtime DLL
  ... optional Streamline files, if included by the package
  ReShade.ini                      <- created/managed by ReShade
```

## Controls

The 32-bit feeder config supports F9 display cycling by default:

| Mode | View |
| --- | --- |
| 0 | Original |
| 1 | DLSS output |
| 2 | Original / DLSS split |
| 3 | Original / amplified difference |
| 4 | Amplified difference / DLSS |

Relevant config keys in `dlss5-feed.cfg`:

| Key | Meaning |
| --- | --- |
| `enabled` | Enables/disables the feeder. |
| `mode` | `0` inert, `1` transport test, `2` full DLSS path. |
| `render_scale` | Input scale. `1.000` is native. |
| `compare_mode` | Startup display mode for the 32-bit path. |
| `iterations` | Runs the same delivered frame through the host pipeline 1-10 times before presenting the final output; cost scales roughly with the value. |
| `hotkey_compare` | Virtual-key code for display cycling. `120` is F9. |
| `hotkey_screenshot` | Virtual-key code for paired normal/DLSS BMP capture. `44` is PrintScreen; with the x86 feeder this key is intercepted while the game is focused. |
| `host_window` | `0` hides the helper window, `1` shows it. |
| `mv_scale_x`, `mv_scale_y` | Extra motion-vector scale multipliers. |

The in-game ReShade Add-ons page for `DLSS 5 Feed` also exposes the controls that matter on the 32-bit path:

- Feed shader controls: motion-vector validation, static/luma/depth/vector consistency tests, bias-current mask strength, geometry-vector experiment controls, motion-vector sign/scale, and debug views.
- Host neural-rendering controls: neural uplift, NR upscaling, preset, style, intensity, local structure, local tone, skin structure, automatic mask, UI correction, paper-white scale, HDR transfer strength, color strength, depth convention, and NR motion-vector scale.

Changing host neural-rendering controls requires pressing `Apply to the DLSS 5 host`. That restarts the hidden 64-bit helper so it reloads `host64/ReShade.ini`.

## Verification

Copied files are not proof. Check logs.

For a working 32-bit session, expect:

- Game `ReShade.log`: ReShade loaded and `dlss5-feed.addon32` registered.
- Game `dlss5-feed.log`: effects found, host connected, shared set ready, frames delivered.
- `host64/dlss5-feed-host.log`: NGX initialized, feature ready, frames evaluated.
- `host64/ReShade.log`: RenoDX DLSS5 loaded and reported feature creation/evaluation.

If depth, motion vectors, or mask are missing, the output may be weak or unchanged even if the helper is running.

## Building

Build scripts assume Visual Studio Build Tools and the needed SDK files are available locally.

- `build-addon32.bat` builds `runtime/x86-dx9-dx11/dlss5-feed.addon32`.
- `build-host64.bat` builds `runtime/host64/dlss5-feed-host64.exe`.

For the host build, place NGX headers/libs under `source/external/ngx`, or set `DLSS_SDK_DIR` to an NGX SDK checkout.

## License

Project code is released under the MIT License. See `LICENSE`.

Bundled third-party source/header files remain under their own licenses:

- Dear ImGui: MIT License, see `source/external/imgui/LICENSE.txt`.
- ReShade headers: BSD-3-Clause OR MIT, see the SPDX headers in `source/external/reshade/include/`.

Third-party runtime binaries such as ReShade, NVIDIA DLSS/NGX, RenoDX DLSS5, and dgVoodoo2 are not redistributed by this repository.
