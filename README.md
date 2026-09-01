# DLSS5 x86/x64 Compatibility Installer

Native Windows installer and compatibility kit for testing RenoDX DLSS 5 Neural Rendering with older DirectX games, including 32-bit DirectX 9 games that need a dgVoodoo2-to-D3D11 bridge before ReShade can host the feeder.

The project is MIT licensed, includes the full source for the .NET installer app and feeder bridge, and avoids Electron. It is built around the workflow we validated locally: scan a game folder, detect architecture/API, install the correct ReShade/DLSS route, keep a restore manifest, and provide enough capture/comparison controls to prove whether the output is actually reaching the game.

![DLSS5 x86/x64 Compatibility Installer](docs/images/app-main.png)

## What It Automates

The app in `app/Dlss5CompatApp/` automates the repetitive and error-prone setup work:

- Scans a game library folder for likely game executables.
- Filters out common false positives such as launchers, uninstallers, setup tools, ReShade tools, Steam helpers, and dgVoodoo control utilities.
- Detects executable architecture from PE headers: x86 or x64.
- Detects likely graphics API from PE imports and embedded markers: DirectX 9, DirectX 11, DirectX 12, DXGI, DirectX 8, DirectDraw-era APIs, Vulkan, and OpenGL.
- Marks DirectX 8 and DirectDraw-era games as unsupported by this installer path.
- Searches installed Steam, GOG, and Epic metadata when available so the grid can show better game names.
- Lets you search and sort the detected game grid by any column.
- Can hide incompatible entries while still allowing them to be shown for diagnosis.
- Remembers the last game scan folder.
- Uses a relative `.\Payload` folder by default, while still allowing an external payload folder to be selected.
- Validates the payload folder for RenoDX DLSS5, NVIDIA DLSS/DLSSNR DLLs, dgVoodoo2 D3D9, and extra ReShade add-ons.
- Installs the x86 DirectX 9 route through dgVoodoo2, then x86 ReShade, then the 32-bit feeder add-on, then the hidden 64-bit DLSS host.
- Installs the x86 DXGI/DirectX 11 route through x86 ReShade, the 32-bit feeder add-on, and the hidden 64-bit DLSS host.
- Installs the native x64 DirectX 11/DirectX 12 route as direct ReShade plus RenoDX DLSS5 in the game folder.
- Copies the feeder shader and ReShade include needed by the x86 route.
- Writes the ReShade preset/INI entries needed for the feeder shader.
- Disables ReShade's own screenshot hotkey so the feeder can own PrintScreen.
- Backs up replaced files into `_DLSS5_Compat_Backup/`.
- Writes `_DLSS5_Compat_Backup/manifest.json` for restore.
- Restores files from that manifest.
- Runs the selected game executable directly from the app.

For supported x86 games, the important part is that the app does not copy a 64-bit add-on into a 32-bit game process. It installs a 32-bit ReShade feeder in the game process and puts RenoDX DLSS5 plus NVIDIA DLSS/DLSSNR into a separate `host64/` helper folder.

## Confirmed Results

This is still a compatibility experiment, but the current x86 path is no longer just a DLL-loading test. In the games below, the feeder returned processed output back to the game frame, and the comparison modes made that visible.

- Call of Duty 4: x86 path produced visible DLSS output in paired normal/DLSS captures.
- Call of Duty 4 with two host evaluation passes: output changed again, proving the iteration setting is being applied.
- Spider-Man 3: DX9 through dgVoodoo2 into the x86 feeder path produced visible processed output.

The strongest visible wins so far are on faces, close-up character detail, and some high-contrast surface structure. Older DX9 scenes remain sensitive to depth, motion vectors, validation masks, and bias masks. When those inputs are weak or absent, the model can still process frames, but the result may be subtle.

### Call of Duty 4: One Iteration

| Normal capture | DLSS output |
| --- | --- |
| ![Call of Duty 4 one-iteration normal capture](docs/images/cod4-1iter-normal.jpg) | ![Call of Duty 4 one-iteration DLSS output](docs/images/cod4-1iter-dlss.jpg) |

### Call of Duty 4: Two Iterations

| Normal capture | DLSS output, two iterations |
| --- | --- |
| ![Call of Duty 4 two-iteration normal capture](docs/images/cod4-2iter-normal.jpg) | ![Call of Duty 4 two-iteration DLSS output](docs/images/cod4-2iter-dlss.jpg) |

### Spider-Man 3

| Normal capture | DLSS output |
| --- | --- |
| ![Spider-Man 3 normal capture](docs/images/spiderman-normal.jpg) | ![Spider-Man 3 DLSS output](docs/images/spiderman-dlss.jpg) |

## Repository Contents

- `app/Dlss5CompatApp/` - source code for the native .NET WinForms installer.
- `source/src/dlss5-feed32.cpp` - 32-bit ReShade add-on used inside x86 games.
- `source/src/feed_ipc.h` - shared IPC and texture contract for the add-on and host.
- `source/host/dlss5-feed-host64.cpp` - 64-bit helper process that hosts the DLSS stack.
- `source/shaders/DLSS5_Feed.fx` - ReShade feed shader.
- `runtime/x86-dx9-dx11/dlss5-feed.addon32` - built 32-bit feeder add-on.
- `runtime/host64/dlss5-feed-host64.exe` - built 64-bit helper.
- `runtime/shaders/DLSS5_Feed.fx` - runtime copy of the feed shader.
- `runtime/shaders/ReShade.fxh` - ReShade shader include needed by the feeder shader.
- `configs/dlss5-feed-32.cfg` - default x86 feeder config.
- `configs/dgVoodoo-dx9.conf` - dgVoodoo2 config used for the DX9 route.
- `setup-dx9-dgvoodoo.ps1` - manual staging helper for people not using the app.
- `docs/images/` - README screenshots and local comparison captures.

## Not Included

The public repository does not include proprietary or redistributable-third-party runtime payloads unless their license allows it and they are explicitly listed above.

Not included:

- NVIDIA NGX/DLSS/DLSSNR DLLs such as `nvngx_dlss.dll` and `nvngx_dlssnr.dll`.
- RenoDX DLSS5 add-on binaries.
- dgVoodoo2 binaries.
- Game files.
- Local release ZIPs.
- Game screenshots outside the curated README images.
- Logs, ReShade caches, local backup folders, or machine-specific install manifests.

## Required Payload Downloads

The installer expects a payload folder. By default, that is:

```text
Dlss5CompatApp.exe
Payload/
```

You can also browse to another payload folder in the app.

Use legitimate sources and keep the package layout intact:

| Package | Link | Files the app looks for |
| --- | --- | --- |
| ReShade with full add-on support | [reshade.me](https://reshade.me/) | This repo's local app build can bundle `ReShade32.dll` and `ReShade64.dll`, but fresh source users must provide equivalent full-add-on ReShade DLLs before packaging/running installs. |
| NVIDIA DLSS runtime | [NVIDIA/DLSS releases](https://github.com/NVIDIA/DLSS/releases) | `nvngx_dlss.dll` |
| NVIDIA Streamline SDK | [NVIDIA Streamline](https://developer.nvidia.com/rtx/streamline/get-started) | Optional `sl.*.dll` files if your RenoDX/DLSS5 package needs them |
| NVIDIA DLSS developer page | [developer.nvidia.com/rtx/dlss](https://developer.nvidia.com/rtx/dlss) | `nvngx_dlssnr.dll` when available through an official or otherwise legitimate source |
| RenoDX project | [github.com/clshortfuse/renodx](https://github.com/clshortfuse/renodx) | Project/source reference for RenoDX |
| RenoDX DLSS installer releases | [github.com/yumlevi/renodx-dlss-installer](https://github.com/yumlevi/renodx-dlss-installer/releases) | `renodx-dlss5.addon64` if provided by the package you choose |
| RenoDX community | [discord.gg/renodx](https://discord.com/invite/renodx) | Current RenoDX DLSS5 add-on packages and compatibility notes |
| dgVoodoo2 | [dege.freeweb.hu/dgVoodoo2](https://dege.freeweb.hu/dgVoodoo2/) and [GitHub releases](https://github.com/dege-diosg/dgVoodoo2/releases) | `MS/x86/D3D9.dll` for the x86 DX9 route, plus optional `dgVoodooCpl.exe` |
| NIGos DLSS5 bridge | [github.com/NIGos/dlss5-bridge](https://github.com/NIGos/dlss5-bridge) | Reference/alternate bridge work; not required by this installer |
| DLSS5 Swapper | [github.com/rakanki911/DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper) | Reference for a swapper-style UX; this project keeps the x86 feeder route baked into a native .NET app |

Recommended payload shape:

```text
Payload/
  renodx-dlss5.addon64
  nvngx_dlss.dll
  nvngx_dlssnr.dll
  sl.interposer.dll                <- optional, package-dependent
  sl.common.dll                    <- optional, package-dependent
  MS/
    x86/
      D3D9.dll                     <- dgVoodoo2, only needed for x86 DX9 games
  dgVoodooCpl.exe                  <- optional
  other.addon64                    <- optional extra ReShade add-ons for x64 direct route
```

Security notes:

- Prefer official pages and signed binaries.
- Keep `renodx-dlss5.addon64`, `nvngx_dlss.dll`, `nvngx_dlssnr.dll`, and optional Streamline files from compatible package generations.
- A mismatched set can load but fail at feature creation or evaluation.

## Final Installed Folder Shapes

### x86 DirectX 9 Game

The app installs this route:

```text
D3D9 game -> dgVoodoo2 -> D3D11/DXGI -> x86 ReShade -> dlss5-feed.addon32 -> host64 helper
```

Expected game folder after install:

```text
GameFolder/
  Game.exe
  D3D9.dll                         <- dgVoodoo2 x86 D3D9 wrapper
  dgVoodoo.conf
  dgVoodooCpl.exe                  <- optional, if present in payload
  dxgi.dll                         <- 32-bit ReShade with add-on support
  dlss5-feed.addon32
  dlss5-feed.cfg
  ReShade.ini
  ReShadePreset.ini
  reshade-shaders/
    Shaders/
      DLSS5_Feed.fx
      ReShade.fxh
  host64/
    dlss5-feed-host64.exe
    dxgi.dll                       <- 64-bit ReShade with add-on support
    renodx-dlss5.addon64
    nvngx_dlss.dll
    nvngx_dlssnr.dll
    sl.*.dll                       <- optional, if present in payload
    ReShade.ini
  _DLSS5_Compat_Backup/
    manifest.json
    ... backed-up replaced files
```

### x86 DXGI / DirectX 11 Game

Expected game folder after install:

```text
GameFolder/
  Game.exe
  dxgi.dll                         <- 32-bit ReShade with add-on support
  dlss5-feed.addon32
  dlss5-feed.cfg
  ReShade.ini
  ReShadePreset.ini
  reshade-shaders/
    Shaders/
      DLSS5_Feed.fx
      ReShade.fxh
  host64/
    dlss5-feed-host64.exe
    dxgi.dll                       <- 64-bit ReShade with add-on support
    renodx-dlss5.addon64
    nvngx_dlss.dll
    nvngx_dlssnr.dll
    sl.*.dll                       <- optional, if present in payload
    ReShade.ini
  _DLSS5_Compat_Backup/
    manifest.json
    ... backed-up replaced files
```

### x64 DirectX 11 / DirectX 12 / DXGI Game

This does not use the x86 feeder. The app installs the direct RenoDX/ReShade route:

```text
GameFolder/
  Game.exe
  dxgi.dll                         <- 64-bit ReShade with add-on support
  renodx-dlss5.addon64
  nvngx_dlss.dll
  nvngx_dlssnr.dll
  sl.*.dll                         <- optional, if present in payload
  other.addon64                    <- optional extra add-ons from payload
  ReShade.ini
  _DLSS5_Compat_Backup/
    manifest.json
    ... backed-up replaced files
```

## x86 Runtime Controls

The 32-bit feeder config supports F9 display cycling by default:

| Mode | View |
| --- | --- |
| 0 | Original game frame |
| 1 | DLSS output |
| 2 | Original / DLSS split |
| 3 | Original / amplified difference |
| 4 | Amplified difference / DLSS |

Relevant `dlss5-feed.cfg` keys:

| Key | Meaning |
| --- | --- |
| `enabled` | Enables or disables the feeder. |
| `mode` | `0` inert, `1` transport test, `2` full DLSS path. |
| `render_scale` | Input scale. `1.000` is native. |
| `compare_mode` | Startup display mode for the 32-bit path. |
| `iterations` | Runs the same delivered frame through the host pipeline 1-10 times before presenting the final output. |
| `hotkey_compare` | Virtual-key code for display cycling. `120` is F9. |
| `hotkey_screenshot` | Virtual-key code for paired normal/DLSS capture. `44` is PrintScreen. |
| `host_window` | `0` hides the helper window, `1` shows it. |
| `mv_scale_x`, `mv_scale_y` | Extra motion-vector scale multipliers. |

PrintScreen is handled by the x86 feeder while the game is focused. This avoids ReShade or Windows stealing the screenshot path and saves paired `normal` and `dlss` BMP captures when the feed path is active.

The ReShade Add-ons page for `DLSS 5 Feed` exposes the x86 controls that matter:

- Motion-vector validation.
- Static, luma, depth, and vector consistency tests.
- Bias-current mask strength.
- Geometry-vector experiment controls.
- Motion-vector sign and scale.
- Debug views.
- Host neural-rendering settings such as neural uplift, NR upscaling, preset, style, intensity, local structure, local tone, skin structure, automatic mask, UI correction, paper-white scale, HDR transfer strength, color strength, depth convention, and NR motion-vector scale.

Host neural-rendering changes require pressing `Apply to the DLSS 5 host`. That restarts the hidden 64-bit helper so it reloads `host64/ReShade.ini`.

## Verification

Do not judge by copied files alone. Check the logs after launching a game.

For a working x86 session, expect:

- Game `ReShade.log`: ReShade loaded and `dlss5-feed.addon32` registered.
- Game `dlss5-feed.log`: effects found, host connected, shared set ready, frames delivered.
- `host64/dlss5-feed-host.log`: NGX initialized, feature ready, frames evaluated.
- `host64/ReShade.log`: RenoDX DLSS5 loaded and reported feature creation/evaluation.

If depth, motion vectors, or masks are missing, the output can be weak even while the host is evaluating frames. The fallback path feeds color with zero motion vectors and flat depth so the pipeline can still prove presentation, but better providers should improve non-face scene detail.

## Build From Source

Requirements:

- Windows.
- .NET 9 SDK.
- Visual Studio 2022 C++ toolchain if rebuilding the native ReShade add-on or host.
- ReShade add-on headers, already included under `source/external/reshade/include/`.
- NGX/DLSS SDK headers/libs if rebuilding the host.

Build the app:

```powershell
dotnet build Dlss5DxCompat.sln -c Release
dotnet publish app\Dlss5CompatApp\Dlss5CompatApp.csproj -c Release -r win-x64 --self-contained true
```

Build the feeder binaries:

```powershell
build-addon32.bat
build-host64.bat
```

The host build expects NGX headers/libs under `source/external/ngx`, or `DLSS_SDK_DIR` pointing at an NGX SDK checkout.

## Manual Install Notes

Use the app when possible. These notes are for manual recovery, inspection, or reproducing the installed shape without the UI.

### Manual Shared `host64/` Setup

1. Create `host64/` next to the game executable.
2. Copy `runtime/host64/dlss5-feed-host64.exe` into `host64/`.
3. Put 64-bit ReShade full-add-on `dxgi.dll` in `host64/`.
4. Copy `renodx-dlss5.addon64` into `host64/`.
5. Copy `nvngx_dlss.dll`, `nvngx_dlssnr.dll`, and optional required `sl.*.dll` files into `host64/`.
6. Create or edit `host64/ReShade.ini`:

```ini
[GENERAL]
EffectSearchPaths=.\reshade-shaders\Shaders\**
TextureSearchPaths=.\reshade-shaders\Textures\**
PresetPath=.\ReShadePreset.ini

[INPUT]
KeyOverlay=36,0,0,0
KeyScreenshot=0,0,0,0

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

### Manual x86 DirectX 9 Setup

1. Back up the game folder.
2. Copy dgVoodoo2 `MS/x86/D3D9.dll` into the game folder as `D3D9.dll`.
3. Copy or create `dgVoodoo.conf`. Use Direct3D 11 output, native/unforced resolution unless the game needs an override, and disable the dgVoodoo watermark.
4. Put 32-bit ReShade full-add-on `dxgi.dll` in the game folder. Do not name ReShade `d3d9.dll`; dgVoodoo2 owns that filename.
5. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` into the game folder.
6. Copy `configs/dlss5-feed-32.cfg` into the game folder as `dlss5-feed.cfg`.
7. Copy `runtime/shaders/DLSS5_Feed.fx` and `runtime/shaders/ReShade.fxh` into `reshade-shaders/Shaders/`.
8. Create or edit `ReShadePreset.ini`:

```ini
[GENERAL]
Techniques=DLSS5_Feed@DLSS5_Feed.fx
TechniqueSorting=DLSS5_Feed@DLSS5_Feed.fx,DLSS5_Feed_Debug@DLSS5_Feed.fx
```

9. Create or edit `ReShade.ini`:

```ini
[GENERAL]
EffectSearchPaths=.\reshade-shaders\Shaders\**
TextureSearchPaths=.\reshade-shaders\Textures\**
PresetPath=.\ReShadePreset.ini
PreprocessorDefinitions=RESHADE_DEPTH_INPUT_IS_REVERSED=1,DLSS5_MV_PROVIDER=0

[INPUT]
KeyScreenshot=0,0,0,0
```

10. Complete the shared `host64/` setup.
11. Launch the game and check that `ReShade.log` shows D3D11/DXGI. If it logs native `IDirect3DDevice9`, dgVoodoo2 and ReShade are not chained correctly.

### Manual x86 DXGI / DirectX 11 Setup

1. Back up the game folder.
2. Put 32-bit ReShade full-add-on `dxgi.dll` in the game folder.
3. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` into the game folder.
4. Copy `configs/dlss5-feed-32.cfg` into the game folder as `dlss5-feed.cfg`.
5. Copy `runtime/shaders/DLSS5_Feed.fx` and `runtime/shaders/ReShade.fxh` into `reshade-shaders/Shaders/`.
6. Create the same `ReShade.ini` and `ReShadePreset.ini` shown in the x86 DirectX 9 setup.
7. Complete the shared `host64/` setup.

### Manual x64 DirectX 11 / DirectX 12 Setup

1. Back up the game folder.
2. Put 64-bit ReShade full-add-on `dxgi.dll` in the game folder.
3. Copy `renodx-dlss5.addon64` into the game folder.
4. Copy `nvngx_dlss.dll`, `nvngx_dlssnr.dll`, and optional required `sl.*.dll` files into the game folder.
5. Copy extra `.addon64` files only if you know they are compatible with the game.
6. Start the game, open ReShade, and confirm the RenoDX DLSS5 add-on page appears.

## License

Project code is released under the MIT License. See `LICENSE`.

Bundled third-party source/header files remain under their own licenses:

- Dear ImGui: MIT License, see `source/external/imgui/LICENSE.txt`.
- ReShade headers: BSD-3-Clause OR MIT, see SPDX headers in `source/external/reshade/include/`.
- `runtime/shaders/ReShade.fxh`: CC0-1.0, see the SPDX header in that file.

Third-party runtime binaries such as NVIDIA DLSS/NGX, RenoDX DLSS5, and dgVoodoo2 are not redistributed by this repository.
