# DLSS5 x86/x64 Compatibility Installer

Native Windows installer and compatibility kit for testing RenoDX DLSS 5 Neural Rendering with older DirectX games, including 32-bit DirectX 9 games and x64 DXGI/DirectX 11 games that do not call DLSS themselves.

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
- Checks `.\Payload` on startup and downloads or updates supported external payload files from known upstream sources.
- Shows payload update progress in the app and logs which files are current, updated, extracted, or still manual.
- Validates the payload folder for RenoDX DLSS5, NVIDIA DLSS/DLSSNR DLLs, dgVoodoo2 D3D9, and extra ReShade add-ons.
- Installs the x86 DirectX 9 route through native D3D9 ReShade, then the 32-bit feeder add-on, then the hidden 64-bit DLSS host.
- Installs the x86 DXGI/DirectX 11 route through x86 ReShade, the 32-bit feeder add-on, and the hidden 64-bit DLSS host.
- Installs the x64 DXGI/DirectX 11 route through x64 ReShade, the 64-bit feeder add-on, and the hidden 64-bit DLSS host.
- Installs the native x64 DirectX 12 route as direct ReShade plus RenoDX DLSS5 in the game folder.
- Copies the feeder shader and ReShade include needed by the feeder routes.
- Writes the ReShade preset/INI entries needed for the feeder shader.
- Forces installed files and managed config values on every install, so reinstalling over an older test folder refreshes the target DLLs and settings.
- Keeps a dgVoodoo2 fallback configuration for DX9 cases where native D3D9 ReShade is not usable.
- Marks the ReShade first-run tutorial as completed in generated `ReShade.ini` files.
- Disables ReShade's own screenshot hotkey so the feeder can own PrintScreen.
- Backs up replaced files into `_DLSS5_Compat_Backup/`.
- Writes `_DLSS5_Compat_Backup/manifest.json` for restore.
- Restores files from that manifest.
- Runs the selected game executable directly from the app.

For supported x86 games, the important part is that the app does not copy a 64-bit add-on into a 32-bit game process. It installs a 32-bit ReShade feeder in the game process and puts RenoDX DLSS5 plus NVIDIA DLSS/DLSSNR into a separate `host64/` helper folder.

For supported x64 DXGI/DirectX 11 games that do not already make DLSS calls, the app uses the same feeder-host idea with a 64-bit in-game feeder. The game folder gets ReShade plus `dlss5-feed.addon64`; `renodx-dlss5.addon64` and the NVIDIA DLLs stay in `host64/`. This avoids the dead path where RenoDX is loaded directly in a non-DLSS game and waits forever for NGX/DLSS activity that never arrives.

## Confirmed Results

This is still a compatibility experiment, but the current x86 path is no longer just a DLL-loading test. In the games below, the feeder returned processed output back to the game frame, and the comparison modes made that visible.

- Call of Duty 4: x86 path produced visible DLSS output in paired normal/DLSS captures.
- Call of Duty 4 with two host evaluation passes: output changed again, proving the iteration setting is being applied.
- Spider-Man 3: DX9 through the x86 feeder path produced visible processed output.
- x64 DX11/DXGI feeder path: log-validated with a non-DLSS x64 DX11 game. The feeder spawned `host64`, connected to NGX, created DLSSNR feature 18, and delivered frames back to the game. More visual testing is still in progress.

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
- `source/src/dlss5-feed32.cpp` - ReShade feeder add-on source; builds as x86 `.addon32` and x64 `.addon64`.
- `source/src/feed_ipc.h` - shared IPC and texture contract for the add-on and host.
- `source/host/dlss5-feed-host64.cpp` - 64-bit helper process that hosts the DLSS stack.
- `source/shaders/DLSS5_Feed.fx` - ReShade feed shader.
- `runtime/x86-dx9-dx11/dlss5-feed.addon32` - built 32-bit feeder add-on.
- `runtime/x64-dx9-dx11/dlss5-feed.addon64` - built 64-bit feeder add-on.
- `runtime/host64/dlss5-feed-host64.exe` - built 64-bit helper.
- `runtime/shaders/DLSS5_Feed.fx` - runtime copy of the feed shader.
- `runtime/shaders/ReShade.fxh` - ReShade shader include needed by the feeder shader.
- `configs/dlss5-feed-32.cfg` - default x86 feeder config.
- `configs/dlss5-feed-64.cfg` - default x64 feeder config.
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

## Startup Payload Bootstrap

The app is designed to go from a clean release folder to a usable local payload without bundling third-party runtime files in this repository.

On startup, and when you press `Update payload`, the app checks the selected payload folder. By default, that folder is:

```text
Dlss5CompatApp.exe
Payload/
```

The updater creates `Payload/`, downloads files from known sources when a direct public source exists, extracts the files the installer needs, and writes `Payload/dlss5-payload-manifest.json` so later starts can skip files that are already current.

Automatically handled sources:

| Package | Source | What the app downloads or extracts |
| --- | --- | --- |
| ReShade with full add-on support | [reshade.me](https://reshade.me/) | Latest `ReShade_Setup_*_Addon.exe`. The installer runs it in headless mode for x86 and x64 ReShade setup. |
| RenoDX DLSS5 add-on | [yumlevi/renodx-dlss-installer](https://github.com/yumlevi/renodx-dlss-installer/releases) | `renodx-dlss5.addon64` only when the latest release contains the verified 1 MB+ add-on build; otherwise the app keeps a known-good local copy if already present and reports that a manual source is still required. |
| NVIDIA DLSS/DLSSNR 310.8 payload | [zhubaohi/FF7R-DLSS5](https://github.com/zhubaohi/FF7R-DLSS5/releases) | `nvidia.zip`, then extracts the verified `nvngx_dlss.dll`, `nvngx_dlssg.dll`, `nvngx_dlssnr.dll`, and `sl.*.dll` files when present. |
| dgVoodoo2 | [dege-diosg/dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2/releases) | Latest normal dgVoodoo2 ZIP, then extracts `MS/x86/D3D9.dll` and `dgVoodooCpl.exe`. |

Manual fallback sources:

| Package | Link | When you need it |
| --- | --- | --- |
| NVIDIA DLSS developer page | [developer.nvidia.com/rtx/dlss](https://developer.nvidia.com/rtx/dlss) | Use this if the automatic source does not provide the DLSS/DLSSNR file you need. Do not mix a normal public DLSS SDK `nvngx_dlss.dll` with a newer DLSSNR package unless the versions match. |
| NVIDIA Streamline SDK | [developer.nvidia.com/rtx/streamline/get-started](https://developer.nvidia.com/rtx/streamline/get-started) | Use this if a RenoDX package expects extra Streamline files not found by the updater. |
| RenoDX project | [github.com/clshortfuse/renodx](https://github.com/clshortfuse/renodx) | Source/reference for RenoDX itself. |
| RenoDX community | [discord.gg/renodx](https://discord.com/invite/renodx) | Current compatibility notes and add-on builds if the public release package changes. |
| NIGos DLSS5 bridge | [github.com/NIGos/dlss5-bridge](https://github.com/NIGos/dlss5-bridge) | Reference/alternate bridge work; not required by this installer. |
| DLSS5 Swapper | [github.com/rakanki911/DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper) | Reference for a swapper-style UX; this project keeps the x86 feeder route baked into a native .NET app. |

Expected payload shape after the automatic bootstrap succeeds:

```text
Payload/
  ReShade_Setup_6.x.x_Addon.exe
  renodx-dlss5.addon64
  nvngx_dlss.dll                  <- extracted from the same Streamline package as DLSSNR
  nvngx_dlssg.dll                 <- extracted from the same Streamline package as DLSSNR
  nvngx_dlssnr.dll                <- extracted from the same Streamline package as DLSS
  sl.interposer.dll                <- optional, package-dependent
  sl.common.dll                    <- optional, package-dependent
  MS/
    x86/
      D3D9.dll                     <- dgVoodoo2, only needed for x86 DX9 games
  dgVoodooCpl.exe                  <- optional
  dlss5-payload-manifest.json
  README.md
  .download-cache/
    streamline.zip
    dgVoodoo2.zip
```

Security notes:

- The repository release does not include these third-party binaries.
- The app downloads from explicit upstream URLs rather than random mirrors.
- Keep `renodx-dlss5.addon64`, `nvngx_dlss.dll`, `nvngx_dlssnr.dll`, and optional Streamline files from compatible package generations.
- A mismatched set can load but fail at feature creation or evaluation.

## Final Installed Folder Shapes

### x86 DirectX 9 Game

The app installs this route:

```text
D3D9 game -> x86 ReShade D3D9 -> dlss5-feed.addon32 -> host64 helper
```

Expected game folder after install:

```text
GameFolder/
  Game.exe
  D3D9.dll                         <- 32-bit ReShade with add-on support
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

### x64 DXGI / DirectX 11 Game

The app installs this route for x64 DXGI/DirectX 11 games that do not already call DLSS:

```text
GameFolder/
  Game.exe
  dxgi.dll                         <- 64-bit ReShade with add-on support
  dlss5-feed.addon64
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

### x64 DirectX 12 Game

The app keeps x64 DirectX 12 on the direct RenoDX/ReShade route:

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

## Feeder Runtime Controls

The x86 and x64 feeder configs support F9 display cycling by default:

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
| `compare_mode` | Startup display mode for feeder paths. |
| `iterations` | Runs the same delivered frame through the host pipeline 1-10 times before presenting the final output. |
| `hotkey_compare` | Virtual-key code for display cycling. `120` is F9. |
| `hotkey_screenshot` | Virtual-key code for paired normal/DLSS capture. `44` is PrintScreen. |
| `host_window` | `0` hides the helper window, `1` shows it. |
| `mv_scale_x`, `mv_scale_y` | Extra motion-vector scale multipliers. |

PrintScreen is handled by the feeder while the game is focused. This avoids ReShade or Windows stealing the screenshot path and saves paired `normal` and `dlss` BMP captures when the feed path is active.

The ReShade Add-ons page for `DLSS 5 Feed` exposes the controls that matter:

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

For a working feeder session, expect:

- Game `ReShade.log`: ReShade loaded and `dlss5-feed.addon32` or `dlss5-feed.addon64` registered.
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
build-addon64.bat
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
TutorialProgress=4

[INPUT]
KeyOverlay=36,0,0,0
KeyScreenshot=0,0,0,0

[RenoDX.DLSS5]
NeuralUplift=1
NREnableUpscaling=1
NRPreset=3
NRStyle=1
NRIntensity=2.000000
NRLocalStructure=2.000000
NRLocalTone=2.000000
NRSkinStructure=2.000000
NRAutoMask=1
NRUICorrection=1
NRDepthMode=2
NRMVecScaleX=4
NRMVecScaleY=4
NRPaperWhiteScale=16.000000
NRTransferStrength=2.000000
NRColorStrength=2.000000
EnableHooks=2
```

### Manual x86 DirectX 9 Setup

1. Back up the game folder.
2. Put 32-bit ReShade full-add-on `D3D9.dll` in the game folder.
3. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` into the game folder.
4. Copy `configs/dlss5-feed-32.cfg` into the game folder as `dlss5-feed.cfg`.
5. Copy `runtime/shaders/DLSS5_Feed.fx` and `runtime/shaders/ReShade.fxh` into `reshade-shaders/Shaders/`.
6. Create or edit `ReShadePreset.ini`:

```ini
[GENERAL]
Techniques=DLSS5_Feed@DLSS5_Feed.fx
TechniqueSorting=DLSS5_Feed@DLSS5_Feed.fx,DLSS5_Feed_Debug@DLSS5_Feed.fx
```

7. Create or edit `ReShade.ini`:

```ini
[GENERAL]
EffectSearchPaths=.\reshade-shaders\Shaders\**
TextureSearchPaths=.\reshade-shaders\Textures\**
PresetPath=.\ReShadePreset.ini
TutorialProgress=4
PreprocessorDefinitions=RESHADE_DEPTH_INPUT_IS_REVERSED=1,DLSS5_MV_PROVIDER=0

[INPUT]
KeyScreenshot=0,0,0,0
```

8. Complete the shared `host64/` setup.
9. Launch the game and check that `ReShade.log` loads the feeder add-on and that `dlss5-feed.log` reports delivered frames.

### Manual x86 DirectX 9 dgVoodoo2 Fallback

Use this only when native D3D9 ReShade cannot hook the game correctly.

1. Back up the game folder.
2. Copy dgVoodoo2 `MS/x86/D3D9.dll` into the game folder as `D3D9.dll`.
3. Copy or create `dgVoodoo.conf`. Use Direct3D 11 output, native/unforced resolution unless the game needs an override, and disable the dgVoodoo watermark.
4. Put 32-bit ReShade full-add-on `dxgi.dll` in the game folder. Do not name ReShade `d3d9.dll`; dgVoodoo2 owns that filename.
5. Follow the remaining x86 DirectX 9 setup steps, but create the game `ReShade.ini` for `dxgi.dll`.
6. Launch the game and check that `ReShade.log` shows D3D11/DXGI. If it logs native `IDirect3DDevice9`, dgVoodoo2 and ReShade are not chained correctly.

### Manual x86 DXGI / DirectX 11 Setup

1. Back up the game folder.
2. Put 32-bit ReShade full-add-on `dxgi.dll` in the game folder.
3. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` into the game folder.
4. Copy `configs/dlss5-feed-32.cfg` into the game folder as `dlss5-feed.cfg`.
5. Copy `runtime/shaders/DLSS5_Feed.fx` and `runtime/shaders/ReShade.fxh` into `reshade-shaders/Shaders/`.
6. Create the same `ReShade.ini` and `ReShadePreset.ini` shown in the x86 DirectX 9 setup.
7. Complete the shared `host64/` setup.

### Manual x64 DXGI / DirectX 11 Setup

1. Back up the game folder.
2. Put 64-bit ReShade full-add-on `dxgi.dll` in the game folder.
3. Copy `runtime/x64-dx9-dx11/dlss5-feed.addon64` into the game folder.
4. Copy `configs/dlss5-feed-64.cfg` into the game folder as `dlss5-feed.cfg`.
5. Copy `runtime/shaders/DLSS5_Feed.fx` and `runtime/shaders/ReShade.fxh` into `reshade-shaders/Shaders/`.
6. Create the same `ReShade.ini` and `ReShadePreset.ini` shown in the x86 DirectX 9 setup.
7. Complete the shared `host64/` setup.
8. Do not also put `renodx-dlss5.addon64` in the game root for this route. It belongs in `host64/`.

### Manual x64 DirectX 12 Setup

Use this direct route for x64 DirectX 12 games, especially games that already create DLSS/NGX work that RenoDX can intercept.

1. Back up the game folder.
2. Put 64-bit ReShade full-add-on `dxgi.dll` in the game folder.
3. Copy `renodx-dlss5.addon64` into the game folder.
4. Copy `nvngx_dlss.dll`, `nvngx_dlssnr.dll`, and optional required `sl.*.dll` files into the game folder.
5. Copy extra `.addon64` files only if you know they are compatible with the game.
6. Create or edit `ReShade.ini` so add-ons are enabled and the first-run tutorial is skipped:

```ini
[GENERAL]
TutorialProgress=4

[ADDON]
DisabledAddons=
```

7. Start the game, open ReShade, and confirm the RenoDX DLSS5 add-on page appears.

## License

Project code is released under the MIT License. See `LICENSE`.

Bundled third-party source/header files remain under their own licenses:

- Dear ImGui: MIT License, see `source/external/imgui/LICENSE.txt`.
- ReShade headers: BSD-3-Clause OR MIT, see SPDX headers in `source/external/reshade/include/`.
- `runtime/shaders/ReShade.fxh`: CC0-1.0, see the SPDX header in that file.

Third-party runtime binaries such as NVIDIA DLSS/NGX, RenoDX DLSS5, and dgVoodoo2 are not redistributed by this repository.
