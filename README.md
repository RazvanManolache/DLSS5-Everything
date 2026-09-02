# DLSS5 x86/x64 Compatibility Installer

**Enhance the visuals of games from the last 25 years.**

Native Windows installer and compatibility kit for bringing RenoDX DLSS 5 Neural Rendering to games that were never built to load it: Glide, DirectDraw-era DirectX, DirectX 8, DirectX 9, DirectX 11/DXGI, DirectX 12, Vulkan, and experimental OpenGL.

The release package intentionally does not ship NVIDIA DLSS/NGX DLLs, RenoDX DLSS5 binaries, DLSS5 Bridge binaries, ReShade installers, d3d8to9, or dgVoodoo2. On first run, the app creates a relative `.\Payload` folder and retrieves the runtime payload from known upstream release/download locations. That keeps this repository and its releases clean while still making the app usable from a fresh unzip.

The goal is simple: pick a game executable, let the app detect the renderer, and install the route that can actually get processed output back on screen. For 32-bit games, that means an x86 in-game feeder and a hidden 64-bit DLSS host. For modern x64 games, it means choosing between direct RenoDX, the feeder-host route, a DirectX 12 hybrid fallback, or the Vulkan bridge path.

DLSS 5 Neural Rendering is controversial, and that is fair. It can be too aggressive, it can expose weak inputs, and it is not a magic remaster button. But if you have a favorite game that still feels great and only needs a visual lift, this kind of model-driven pass can be worth experimenting with. This tool exists to make that experiment practical across more games, especially older 32-bit titles that cannot load modern 64-bit DLSS/RenoDX components directly.

I am convinced the interesting future is not one fixed model for everything. There will likely be more neural rendering models, better tuning paths, and maybe even per-game or per-engine models. Before that future is useful, though, the boring compatibility layer has to work: the right DLL in the right process, the right renderer route, the right payload version, and proof that the processed frame really gets back on screen. That is what this project focuses on.

The hard compatibility work is x86 support. 32-bit DirectX 8 games can be converted to DirectX 9 with crosire's d3d8to9 wrapper, then use the same x86 feeder route as native DirectX 9. 32-bit Glide games are wrapped through dgVoodoo2 into a modern Direct3D output, then chained through the same feeder and hidden 64-bit DLSS host. 32-bit DirectX 9 and 32-bit DirectX 11/DXGI games cannot load 64-bit DLSS/RenoDX add-ons directly, so this project installs a 32-bit in-game feeder and a hidden 64-bit DLSS host. x64 DirectX 11/DXGI games use the same feeder-host route when they do not already make DLSS calls. x64 DirectX 12 games install native RenoDX DLSS5 first, then use the feeder-host route automatically if the game does not emit DLSS/NGX work for RenoDX to intercept. x64 Vulkan games use the ReShade Vulkan layer plus NIGos DLSS5 Bridge, configured by this app, so Vulkan mirror/substitute handling comes from the upstream bridge instead of this project's feeder. Experimental OpenGL support uses a CPU readback/upload bridge into the same host64 path.

The project is MIT licensed and includes the full source for the .NET installer app and feeder bridge. It is built around the workflow we validated locally: scan a game folder, detect architecture/API, download the external payload, install the correct ReShade/DLSS route, keep a restore manifest, and provide capture/comparison controls to prove whether the output is actually reaching the game.

## At a Glance

- One installer for classic and modern renderers, from late-1990s APIs through current x64 titles.
- First-run payload bootstrap: downloads and updates the needed external runtime files into `.\Payload`.
- x86 compatibility: supports 32-bit DirectX 8, DirectX 9, DirectX 11/DXGI, and Glide games through the feeder plus hidden 64-bit host route.
- API coverage: DirectX 8 via d3d8to9, Glide via dgVoodoo2, native DirectX 9, DirectX 11/DXGI, DirectX 12, x64 Vulkan, and experimental OpenGL install paths are implemented.
- Clean release model: GitHub source and release ZIPs contain this app, feeder, host, shaders, configs, docs, and license, not third-party proprietary payloads.
- Native app: self-contained WinForms/.NET release.

![DLSS5 x86/x64 Compatibility Installer](docs/images/app-main.png)

## What It Automates

The app in `app/Dlss5CompatApp/` automates the repetitive and error-prone setup work:

- Scans a game library folder for likely game executables.
- Filters out common false positives such as launchers, uninstallers, setup tools, ReShade tools, Steam helpers, and dgVoodoo control utilities.
- Detects executable architecture from PE headers: x86 or x64.
- Detects likely graphics API from PE imports and embedded markers: DirectX 9, DirectX 11, DirectX 12, DXGI, DirectX 8, DirectDraw-era APIs, Glide, Vulkan, and OpenGL.
- For Unity games, probes sibling renderer modules such as `UnityPlayer.dll` and prefers DirectX creation markers over generic OpenGL compatibility imports.
- Supports detected x86 DirectX 8 games by installing crosire d3d8to9 first, then applying the native x86 DirectX 9 feeder route.
- Supports detected x86 Glide games by installing the matching dgVoodoo2 Glide wrapper first, then applying the feeder-host route.
- Detects multiple possible renderers for the same executable and lets you choose the route before installing.
- Supports x86 DirectDraw-era routes experimentally through dgVoodoo2, and marks x64 DirectX 8 or unsupported legacy edge cases as unavailable.
- Installs x64 Vulkan games through the ReShade Vulkan layer, NIGos DLSS5 Bridge, RenoDX DLSS5, and matching NGX/DLSS files.
- Searches installed Steam, GOG, and Epic metadata when available so the grid can show better game names.
- Lets you search and sort the detected game grid by any column.
- Can hide incompatible entries while still allowing them to be shown for diagnosis.
- Remembers the last game scan folder.
- Uses a relative `.\Payload` folder by default, while still allowing an external payload folder to be selected.
- Checks `.\Payload` on startup and downloads or updates supported external payload files from known upstream sources.
- Shows payload update progress in the app and logs which files are current, updated, extracted, or still manual.
- Validates the payload folder for RenoDX DLSS5, NVIDIA DLSS/DLSSNR DLLs, d3d8to9, dgVoodoo2 DirectX/Glide files, and extra ReShade add-ons.
- Installs the x86 DirectX 8 route through d3d8to9, native D3D9 ReShade, the 32-bit feeder add-on, and the hidden 64-bit DLSS host.
- Installs x86 Glide 2.11, Glide 2.45, Glide 3.1, and Glide 3.1 Napalm routes through dgVoodoo2, the 32-bit feeder add-on, and the hidden 64-bit DLSS host.
- Installs the x86 DirectX 9 route through native D3D9 ReShade, then the 32-bit feeder add-on, then the hidden 64-bit DLSS host.
- Installs the x86 DXGI/DirectX 11 route through x86 ReShade, the 32-bit feeder add-on, and the hidden 64-bit DLSS host.
- Installs experimental x86/x64 OpenGL routes through `opengl32.dll` ReShade, the feeder add-on, and the hidden 64-bit DLSS host.
- Installs the x64 DXGI/DirectX 11 route through x64 ReShade, the 64-bit feeder add-on, and the hidden 64-bit DLSS host.
- Installs the x64 DirectX 12 route as native ReShade plus RenoDX DLSS5, with an automatic feeder-host fallback if no native RenoDX NGX activity appears.
- Installs the x64 Vulkan route as ReShade's Vulkan layer plus `dlss5-bridge.addon64`, with `vk_mirror=1`, `source=auto`, and the synthetic fallback enabled in `dlss5-bridge.cfg`.
- Copies the feeder shader and ReShade include needed by the feeder routes.
- Writes the ReShade preset/INI entries needed for the feeder shader.
- Forces installed files and managed config values on every install, so reinstalling over an older test folder refreshes the target DLLs and settings.
- Keeps a dgVoodoo2 configuration for Glide and older DirectX routes, plus DX9 cases where native D3D9 ReShade is not usable.
- Marks the ReShade first-run tutorial as completed in generated `ReShade.ini` files and disables ReShade's own small status popups.
- Disables ReShade's own screenshot hotkey so the feeder can own PrintScreen.
- Attempts to disable NVIDIA's DLSS on-screen indicator for both 64-bit and 32-bit NGX when the app is run with administrator rights.
- Backs up replaced files into `_DLSS5_Compat_Backup/`.
- Writes `_DLSS5_Compat_Backup/manifest.json` for restore.
- Restores files from that manifest.
- Runs the selected game executable directly from the app.

For supported x86 games, the important part is that the app does not copy a 64-bit add-on into a 32-bit game process. It installs a 32-bit ReShade feeder in the game process and puts RenoDX DLSS5 plus NVIDIA DLSS/DLSSNR into a separate `host64/` helper folder.

For supported x64 DXGI/DirectX 11 and DirectX 12 games that do not already make DLSS calls, the app uses the same feeder-host idea with a 64-bit in-game feeder. DXGI/DirectX 11 uses shared GPU textures; DirectX 12 uses a CPU fallback bridge after the native RenoDX probe times out. This avoids the dead path where RenoDX is loaded directly in a non-DLSS game and waits forever for NGX/DLSS activity that never arrives.

## Confirmed Results

This is no longer just a DLL-loading experiment. The current feeder path has returned processed frames back into real games across old and modern renderers, including x86 DirectX 9, legacy DirectDraw/Glide routes, OpenGL, x64 DXGI/DirectX 11, and DirectX 12 fallback paths.

- Call of Duty 4: x86 path produced visible DLSS output in paired normal/DLSS captures.
- Call of Duty 4 with two host evaluation passes: output changed again, proving the iteration setting is being applied.
- Spider-Man 3: DX9 through the x86 feeder path produced visible processed output.
- FlatOut 2: x86 DirectX 9 route produced paired normal/DLSS captures through the feeder-host pipeline.
- Batman Arkham Knight: x64 DirectX 11/DXGI route produced paired normal/DLSS captures.
- HITMAN 3: x64 DirectX 12 route created DLSSNR feature 18 and produced a live capture during the smoke run.
- Warcraft III, Homeworld Classic, Diablo II, Dangerous Curves, and Luna: older renderer routes produced smoke-run captures across OpenGL, Glide, and legacy DirectX handling.
- x64 Vulkan route: implemented through ReShade's Vulkan layer and NIGos DLSS5 Bridge. Screenshot coverage is still pending; DOOM Eternal currently needs the documented manual ReShade Vulkan disable-key step after install.

The strongest visible wins so far are on faces, close-up character detail, foliage, high-contrast edges, and some surface structure. Older scenes remain sensitive to depth, motion vectors, validation masks, and bias masks. When those inputs are weak or absent, the model can still process frames, but the result can be subtle instead of a dramatic remaster.

The comparison captures below are generated by the feeder itself. The left image is the normal frame capture; the right image is the DLSS output capture from the same moment.

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

### FlatOut 2

| Normal capture | DLSS output |
| --- | --- |
| ![FlatOut 2 normal capture](docs/images/compat-flatout-normal.jpg) | ![FlatOut 2 DLSS output](docs/images/compat-flatout-dlss.jpg) |

### Batman Arkham Knight

| Normal capture | DLSS output |
| --- | --- |
| ![Batman Arkham Knight normal capture](docs/images/compat-batman-normal.jpg) | ![Batman Arkham Knight DLSS output](docs/images/compat-batman-dlss.jpg) |

### Warcraft III

| Normal capture | DLSS output |
| --- | --- |
| ![Warcraft III normal capture](docs/images/compat-warcraft3-normal.jpg) | ![Warcraft III DLSS output](docs/images/compat-warcraft3-dlss.jpg) |

### StarCraft

| Normal capture | DLSS output |
| --- | --- |
| ![StarCraft normal capture](docs/images/compat-starcraft-normal.jpg) | ![StarCraft DLSS output](docs/images/compat-starcraft-dlss.jpg) |

This capture came from the smoke-test window after the launcher run exited early, so the visual capture exists but the runner entry still needs cleaner process tracking.

### Homeworld Classic

| Normal capture | DLSS output |
| --- | --- |
| ![Homeworld Classic normal capture](docs/images/compat-homeworld-normal.jpg) | ![Homeworld Classic DLSS output](docs/images/compat-homeworld-dlss.jpg) |

### Diablo II

| Normal capture | DLSS output |
| --- | --- |
| ![Diablo II normal capture](docs/images/compat-diablo2-normal.jpg) | ![Diablo II DLSS output](docs/images/compat-diablo2-dlss.jpg) |

### Dangerous Curves

| Normal capture | DLSS output |
| --- | --- |
| ![Dangerous Curves normal capture](docs/images/compat-dangerous-curves-normal.jpg) | ![Dangerous Curves DLSS output](docs/images/compat-dangerous-curves-dlss.jpg) |

### Luna

| Normal capture | DLSS output |
| --- | --- |
| ![Luna normal capture](docs/images/compat-luna-normal.jpg) | ![Luna DLSS output](docs/images/compat-luna-dlss.jpg) |

### Live Smoke-Run Captures

These full-screen captures were taken during the same smoke-test window and are useful as quick visual proof that the installed route was visible in a live game session.

| FlatOut 2 | HITMAN 3 | Luna |
| --- | --- | --- |
| ![FlatOut 2 live capture](docs/images/compat-live-flatout.jpg) | ![HITMAN 3 live capture](docs/images/compat-live-hitman3.jpg) | ![Luna live capture](docs/images/compat-live-luna.jpg) |

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
- `configs/dgVoodoo-dx9.conf` - dgVoodoo2 config used for DX9 fallback, Glide, and legacy DirectX wrapper routes.
- `setup-dx9-dgvoodoo.ps1` - manual staging helper for people not using the app.
- `docs/images/` - README screenshots and local comparison captures.

## Not Included

The public repository does not include proprietary or redistributable-third-party runtime payloads unless their license allows it and they are explicitly listed above.

Not included:

- NVIDIA NGX/DLSS/DLSSNR DLLs such as `nvngx_dlss.dll` and `nvngx_dlssnr.dll`.
- RenoDX DLSS5 add-on binaries.
- DLSS5 Bridge binaries.
- d3d8to9 binaries.
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
| RenoDX DLSS5 add-on | [rakanki911/DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper/releases) | Latest portable release, then extracts only `resources/payload/renodx-dlss5.addon64` and verifies it matches the known working 1.7 MB add-on hash. |
| DLSS5 Bridge | [NIGos/dlss5-bridge](https://github.com/NIGos/dlss5-bridge/releases) | Latest `dlss5-bridge.addon64`, used for the x64 Vulkan route. |
| 7za extractor | [7zip-bin on unpkg](https://unpkg.com/7zip-bin@5.2.0/win/x64/7za.exe) | Downloaded into `.download-cache/` only so the app can extract the DLSS5-Swapper portable package without requiring 7-Zip to be installed. |
| NVIDIA DLSS/DLSSNR 310.8 payload | [zhubaohi/FF7R-DLSS5](https://github.com/zhubaohi/FF7R-DLSS5/releases) | `nvidia.zip`, then extracts the verified `nvngx_dlss.dll`, `nvngx_dlssg.dll`, `nvngx_dlssnr.dll`, and `sl.*.dll` files when present. |
| d3d8to9 | [crosire/d3d8to9](https://github.com/crosire/d3d8to9/releases) | Latest release `d3d8.dll`, stored under `Payload/d3d8to9/` for x86 DirectX 8 games. |
| dgVoodoo2 | [dege-diosg/dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2/releases) | Latest normal dgVoodoo2 ZIP, then extracts `MS/x86/D3D8.dll`, `MS/x86/D3D9.dll`, `MS/x86/DDraw.dll`, `MS/x86/D3DImm.dll`, `3Dfx/x86/Glide.dll`, `3Dfx/x86/Glide2x.dll`, `3Dfx/x86/Glide3x.dll`, the Napalm `Glide3x.dll`, and `dgVoodooCpl.exe`. |

Manual fallback sources:

| Package | Link | When you need it |
| --- | --- | --- |
| NVIDIA DLSS developer page | [developer.nvidia.com/rtx/dlss](https://developer.nvidia.com/rtx/dlss) | Use this if the automatic source does not provide the DLSS/DLSSNR file you need. Do not mix a normal public DLSS SDK `nvngx_dlss.dll` with a newer DLSSNR package unless the versions match. |
| NVIDIA Streamline SDK | [developer.nvidia.com/rtx/streamline/get-started](https://developer.nvidia.com/rtx/streamline/get-started) | Use this if a RenoDX package expects extra Streamline files not found by the updater. |
| RenoDX project | [github.com/clshortfuse/renodx](https://github.com/clshortfuse/renodx) | Source/reference for RenoDX itself. |
| RenoDX community | [discord.gg/renodx](https://discord.com/invite/renodx) | Current compatibility notes and add-on builds if the public release package changes. |
| NIGos DLSS5 bridge | [github.com/NIGos/dlss5-bridge](https://github.com/NIGos/dlss5-bridge) | Source and releases for the x64 Vulkan bridge route used by this installer. |
| yumlevi RenoDX DLSS installer | [github.com/yumlevi/renodx-dlss-installer](https://github.com/yumlevi/renodx-dlss-installer/releases) | Historical source checked during testing. Its current standalone `renodx-dlss5.addon64` release is the smaller build that did not match our verified working add-on hash. |

Expected payload shape after the automatic bootstrap succeeds:

```text
Payload/
  ReShade_Setup_6.x.x_Addon.exe
  renodx-dlss5.addon64
  dlss5-bridge.addon64             <- NIGos DLSS5 Bridge, only needed for x64 Vulkan games
  nvngx_dlss.dll                  <- extracted from the same Streamline package as DLSSNR
  nvngx_dlssg.dll                 <- extracted from the same Streamline package as DLSSNR
  nvngx_dlssnr.dll                <- extracted from the same Streamline package as DLSS
  sl.interposer.dll                <- optional, package-dependent
  sl.common.dll                    <- optional, package-dependent
  d3d8to9/
    d3d8.dll                       <- crosire d3d8to9, only needed for x86 DX8 games
  MS/
    x86/
      D3D8.dll                     <- dgVoodoo2 DirectX wrapper, optional fallback for x86 DX8 games
      D3D9.dll                     <- dgVoodoo2, only needed for x86 DX9 games
      DDraw.dll                    <- dgVoodoo2, only needed for DirectDraw-era games
      D3DImm.dll                   <- dgVoodoo2, only needed for Direct3D 1-7 games
  3Dfx/
    x86/
      Glide.dll                    <- dgVoodoo2 Glide 2.11 wrapper
      Glide2x.dll                  <- dgVoodoo2 Glide 2.45 wrapper
      Glide3x.dll                  <- dgVoodoo2 Glide 3.1 wrapper
      Napalm/
        Glide3x.dll                <- dgVoodoo2 Glide 3.1 Napalm wrapper
  dgVoodooCpl.exe                  <- optional
  dlss5-payload-manifest.json
  README.md
  .download-cache/
    7za.exe
    DLSS5-Swapper-portable.exe
    nvidia-3108.zip
    dgVoodoo2.zip
```

Security notes:

- The repository release does not include these third-party binaries.
- The app downloads from explicit upstream URLs rather than random mirrors.
- Keep `renodx-dlss5.addon64`, `nvngx_dlss.dll`, `nvngx_dlssnr.dll`, and optional Streamline files from compatible package generations.
- A mismatched set can load but fail at feature creation or evaluation.

## Final Installed Folder Shapes

### x86 DirectX 8 Game

The app installs this route:

```text
D3D8 game -> d3d8to9 d3d8.dll -> x86 ReShade D3D9 -> dlss5-feed.addon32 -> host64 helper
```

Expected game folder after install:

```text
GameFolder/
  Game.exe
  d3d8.dll                         <- crosire d3d8to9 wrapper
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

### x86 Glide Game

The app installs this route for detected Glide 2.11, Glide 2.45, Glide 3.1, and Glide 3.1 Napalm executables:

```text
Glide game -> dgVoodoo2 Glide wrapper -> x86 ReShade DXGI/D3D11 -> dlss5-feed.addon32 -> host64 helper
```

Expected game folder after install:

```text
GameFolder/
  Game.exe
  Glide.dll or Glide2x.dll or Glide3x.dll
  dgVoodoo.conf
  dgVoodooCpl.exe
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

The installer chooses the Glide wrapper by selected API:

| Selected API | Game-folder wrapper |
| --- | --- |
| Glide 2.11 | `Glide.dll` |
| Glide 2.45 | `Glide2x.dll` |
| Glide 3.1 | `Glide3x.dll` |
| Glide 3.1 Napalm | `Glide3x.dll` from dgVoodoo2's Napalm folder |

Some older games need a launch switch to select the Glide renderer. When the scanner finds strong local evidence for a `-3dfx` style switch, the app stores that as suggested arguments for the Glide route and uses it when launching from the app or from the generated `*-dlss5-glide*.bat` launcher.

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

### OpenGL Game

The app installs this route for x86 and x64 OpenGL executables:

```text
OpenGL game -> ReShade opengl32.dll -> feeder CPU bridge -> host64 helper
```

Expected game folder after install is the same as the matching x86 or x64 feeder route, except the ReShade proxy in the game folder is:

```text
GameFolder/
  Game.exe
  opengl32.dll                     <- ReShade with add-on support
  dlss5-feed.addon32 or .addon64
  dlss5-feed.cfg
  ReShade.ini
  ReShadePreset.ini
  reshade-shaders/
    Shaders/
      DLSS5_Feed.fx
      ReShade.fxh
  host64/
    dlss5-feed-host64.exe
    dxgi.dll
    renodx-dlss5.addon64
    nvngx_dlss.dll
    nvngx_dlssnr.dll
```

This path is experimental and uses CPU readback/upload. It is expected to be slower than D3D11 shared-texture feeding and currently uses zero motion/depth guides.

### x64 DirectX 12 Game

The app installs the native RenoDX/ReShade route and a feeder fallback in the same pass. At runtime, the feeder waits briefly for root RenoDX to show real NGX/DLSS activity. If that signal appears, the feeder stays out of the way. If the game has no native DLSS signal, the feeder starts the hidden host and returns processed output to the game frame.

```text
GameFolder/
  Game.exe
  dxgi.dll                         <- 64-bit ReShade with add-on support
  renodx-dlss5.addon64
  nvngx_dlss.dll
  nvngx_dlssnr.dll
  sl.*.dll                         <- optional, if present in payload
  dlss5-feed.addon64
  dlss5-feed.cfg                    <- native_probe_seconds=12 for this route
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
| `native_probe_seconds` | DX12 hybrid route delay before feeder fallback starts. `12` means native RenoDX gets 12 seconds to prove it is intercepting NGX/DLSS before the fallback starts. |

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

## Smoke Test Runner

The app also has a headless smoke-test mode for repeatable compatibility passes across a small set of known local games. It uses the same installer engine as the UI.

For each configured target, the runner:

- Updates the selected payload folder unless disabled.
- Restores the previous app-managed install from `_DLSS5_Compat_Backup/manifest.json`, if one exists.
- Clears the app-managed logs before launch.
- Installs the route detected for that executable.
- Launches the game.
- Watches game and host logs for evidence such as delivered frames, NGX initialization, or backend-specific bridge readiness.
- By default, closes the game window and force-kills the process tree after the timeout if needed.
- When `waitForExit` is enabled, leaves the game running until you close it yourself. This is useful for collecting screenshots during a manual visual pass.
- Writes a JSON report with detected route, process start/end timestamps, pass/fail state, matched evidence, and log tail summaries. During long runs, the report is refreshed after each completed target.

Example config:

```json
{
  "payloadRoot": ".\\Payload",
  "updatePayload": true,
  "restoreBeforeInstall": true,
  "restoreAfterRun": false,
  "clearLogsBeforeRun": true,
  "runSeconds": 60,
  "minRunSeconds": 12,
  "closeSeconds": 8,
  "waitForExit": false,
  "reportPath": ".\\smoke-report.json",
  "tests": [
    {
      "name": "Example DX9 game",
      "exe": "E:\\Games\\ExampleGame\\Game.exe"
    },
    {
      "name": "Example Steam-launched Vulkan game",
      "exe": "E:\\Games\\ExampleVulkanGame\\Gamex64vk.exe",
      "api": "Vulkan",
      "launchUri": "steam://rungameid/000000",
      "processName": "Gamex64vk",
      "waitForExit": true
    }
  ]
}
```

Run it with:

```cmd
tools\run-smoke-tests.cmd tools\smoke-tests.example.json
```

The wrapper waits for the WinForms executable to finish headless mode. The report defaults to `smoke-report.json` next to the config file unless `reportPath` is changed.

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

### Manual x86 DirectX 8 Setup

Use this only for x86 DirectX 8 games. It depends on crosire d3d8to9 converting D3D8 calls into D3D9, then ReShade handles the D3D9 side.

1. Back up the game folder.
2. Copy crosire d3d8to9 `d3d8.dll` into the game folder.
3. Put 32-bit ReShade full-add-on `D3D9.dll` in the game folder.
4. Follow the remaining manual x86 DirectX 9 setup steps.
5. Launch the game and check that `ReShade.log` reports D3D9. If ReShade does not appear, the game may not be using DirectX 8 from that executable, or another local wrapper may be taking priority.

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

### Manual x86 Glide Setup

Use this for 32-bit games that have a Glide renderer. If the same executable can run OpenGL too, try the OpenGL route first because it avoids dgVoodoo2. Use Glide when the game's Glide renderer is the stable or higher-quality path.

1. Back up the game folder.
2. Copy the matching dgVoodoo2 Glide wrapper into the game folder:

| Game renderer | Copy from payload | Copy as |
| --- | --- | --- |
| Glide 2.11 | `Payload/3Dfx/x86/Glide.dll` | `Glide.dll` |
| Glide 2.45 | `Payload/3Dfx/x86/Glide2x.dll` | `Glide2x.dll` |
| Glide 3.1 | `Payload/3Dfx/x86/Glide3x.dll` | `Glide3x.dll` |
| Glide 3.1 Napalm | `Payload/3Dfx/x86/Napalm/Glide3x.dll` | `Glide3x.dll` |

3. Copy or create `dgVoodoo.conf`. Use Direct3D 11 output, native/unforced resolution unless the game needs an override, and disable the dgVoodoo watermark.
4. Put 32-bit ReShade full-add-on `dxgi.dll` in the game folder. Do not name ReShade after the Glide wrapper; dgVoodoo2 owns the Glide DLL name.
5. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` into the game folder.
6. Copy `configs/dlss5-feed-32.cfg` into the game folder as `dlss5-feed.cfg`.
7. Copy `runtime/shaders/DLSS5_Feed.fx` and `runtime/shaders/ReShade.fxh` into `reshade-shaders/Shaders/`.
8. Create the same `ReShade.ini` and `ReShadePreset.ini` shown in the x86 DirectX 9 setup, but keep ReShade named `dxgi.dll`.
9. Complete the shared `host64/` setup.
10. If the game needs a renderer switch, launch with that switch, for example `-3dfx`.
11. Check `ReShade.log` for D3D11/DXGI and `dlss5-feed.log` for delivered frames. If ReShade does not appear, the game is probably not using the Glide renderer or the wrong Glide wrapper version was chosen.

### Manual x86 DXGI / DirectX 11 Setup

1. Back up the game folder.
2. Put 32-bit ReShade full-add-on `dxgi.dll` in the game folder.
3. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` into the game folder.
4. Copy `configs/dlss5-feed-32.cfg` into the game folder as `dlss5-feed.cfg`.
5. Copy `runtime/shaders/DLSS5_Feed.fx` and `runtime/shaders/ReShade.fxh` into `reshade-shaders/Shaders/`.
6. Create the same `ReShade.ini` and `ReShadePreset.ini` shown in the x86 DirectX 9 setup.
7. Complete the shared `host64/` setup.

### Manual OpenGL Setup

Use this only for OpenGL games. Vulkan games do not use `opengl32.dll` and need a separate Vulkan-layer path.

1. Back up the game folder.
2. Put matching-architecture ReShade full-add-on `opengl32.dll` in the game folder.
3. Copy `runtime/x86-dx9-dx11/dlss5-feed.addon32` or `runtime/x64-dx9-dx11/dlss5-feed.addon64` into the game folder.
4. Copy the matching `dlss5-feed.cfg`, `DLSS5_Feed.fx`, and `ReShade.fxh` files exactly like the matching x86 or x64 feeder route.
5. Complete the shared `host64/` setup.
6. Launch the game and check `dlss5-feed.log` for `native OpenGL CPU bridge ready` and delivered frames.

### Manual x64 Vulkan Setup

Use this only for x64 Vulkan games. This route uses ReShade's Vulkan layer and `dlss5-bridge.addon64`; it does not use this project's `dlss5-feed.addon64` or `host64/` helper.

1. Back up the game folder.
2. Run `ReShade_Setup_*_Addon.exe` for the game executable and choose Vulkan.
3. Copy `dlss5-bridge.addon64` into the game folder.
4. Copy `renodx-dlss5.addon64` into the game folder.
5. Copy `nvngx_dlss.dll`, `nvngx_dlssnr.dll`, and optional required `sl.*.dll` files into the game folder.
6. Create or edit `ReShade.ini` so add-ons are enabled and the ReShade tutorial/status noise is disabled:

```ini
[GENERAL]
EffectSearchPaths=
TextureSearchPaths=
PresetPath=.\ReShadePreset.ini
TutorialProgress=4

[ADDON]
AddonPath=.\
DisabledAddons=

[OVERLAY]
TutorialProgress=4
ShowOverlay=0
ShowClock=0
ShowFPS=0
ShowFrameTime=0
ShowPresetName=0
ShowPresetTransitionMessage=0
ShowScreenshotMessage=0

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
EnableHooks=2
```

7. Create `ReShadePreset.ini` with no active effects:

```ini
[GENERAL]
Techniques=
TechniqueSorting=
```

8. Create `dlss5-bridge.cfg`:

```ini
mode=2
stage=3
skip_game=1
vk_mirror=1
source=auto
synth=1
synth_after=10
flags=-1
subrects=1
reset_every=0
pixels=0
dred=1
skip_exe=1
unwrap=1
probe=0
hash_out=0
mv_sign_x=0
mv_sign_y=0
ofa_grid=2
ofa_perf=20
```

9. If a Vulkan game blocks ReShade by setting `DISABLE_VK_LAYER_reshade_1`, rename that disable key in ReShade's Vulkan layer JSON. The app attempts this automatically for `C:\ProgramData\ReShade\ReShade64.json`; if Windows blocks the write, run the app as administrator and reinstall.
10. DOOM Eternal specific final step: open `C:\ProgramData\ReShade\ReShade64.json` as administrator and rename the `disable_environment` entry from `DISABLE_VK_LAYER_reshade_1` to `DISABLE_VK_LAYER_reshade_2`. Then launch the game through Steam, not by directly starting `DOOMEternalx64vk.exe`.
11. Start the game and check `ReShade.log` and `dlss5-bridge.log`. ReShade should load both `dlss5-bridge.addon64` and `renodx-dlss5.addon64`.

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

Use this hybrid route for x64 DirectX 12 games. It supports both cases we saw in testing: games that already create DLSS/NGX work that RenoDX can intercept, and games that expose D3D12 but do not emit a native DLSS signal.

1. Back up the game folder.
2. Put 64-bit ReShade full-add-on `dxgi.dll` in the game folder.
3. Copy `renodx-dlss5.addon64` into the game folder.
4. Copy `nvngx_dlss.dll`, `nvngx_dlssnr.dll`, and optional required `sl.*.dll` files into the game folder.
5. Copy `runtime/x64-dx9-dx11/dlss5-feed.addon64` into the game folder.
6. Copy `configs/dlss5-feed-64.cfg` into the game folder as `dlss5-feed.cfg`, then set `native_probe_seconds=12`.
7. Copy `runtime/shaders/DLSS5_Feed.fx` and `runtime/shaders/ReShade.fxh` into `reshade-shaders/Shaders/`.
8. Create the same `ReShadePreset.ini` shown in the feeder routes.
9. Complete the shared `host64/` setup.
10. Copy extra `.addon64` files only if you know they are compatible with the game.
11. Create or edit `ReShade.ini` so add-ons are enabled, the first-run tutorial is skipped, and the feeder shader is available:

```ini
[GENERAL]
EffectSearchPaths=.\reshade-shaders\Shaders\**
TextureSearchPaths=.\reshade-shaders\Textures\**
PresetPath=.\ReShadePreset.ini
TutorialProgress=4
PreprocessorDefinitions=RESHADE_DEPTH_INPUT_IS_REVERSED=1,DLSS5_MV_PROVIDER=0,DLSS5_GEOM_FIT=1

[ADDON]
DisabledAddons=
```

12. Start the game. If root RenoDX sees native NGX/DLSS calls, the feeder disables itself. If not, check `dlss5-feed.log` and `host64/dlss5-feed-host.log` for host connection and delivered frames.

## Resources And Thanks

This project stands on a lot of prior work. The app tries to make the pieces easier to combine, but the important graphics/runtime components come from these projects and vendors:

- [ReShade](https://reshade.me/) for the injector, shader runtime, add-on support, and Vulkan layer.
- [RenoDX](https://github.com/clshortfuse/renodx) and the [RenoDX community](https://discord.com/invite/renodx) for the DLSS/RenoDX add-on ecosystem this project builds around.
- [DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper) for the portable RenoDX DLSS5 payload source used by the automatic downloader.
- [DLSS5 Bridge](https://github.com/NIGos/dlss5-bridge) for the x64 Vulkan bridge route.
- [NVIDIA DLSS](https://developer.nvidia.com/rtx/dlss) and [NVIDIA Streamline](https://developer.nvidia.com/rtx/streamline/get-started) for the DLSS/NGX/Streamline runtime technology.
- [FF7R-DLSS5](https://github.com/zhubaohi/FF7R-DLSS5) for the currently verified public DLSS/DLSSNR 310.8 runtime package source used by the downloader.
- [d3d8to9](https://github.com/crosire/d3d8to9) for the DirectX 8 to DirectX 9 wrapper used by the preferred x86 DX8 route.
- [dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2) for DirectDraw, legacy Direct3D, DirectX 9 fallback, and Glide wrapper routes.
- [7zip-bin](https://www.npmjs.com/package/7zip-bin) and [unpkg](https://unpkg.com/) for the small 7-Zip helper used to extract portable packages without requiring a local 7-Zip install.
- [Dear ImGui](https://github.com/ocornut/imgui) for bundled headers used by native tooling.
- [.NET](https://dotnet.microsoft.com/) for the native Windows installer app.

Thank you to the ReShade, RenoDX, DLSS5 Bridge, dgVoodoo2, d3d8to9, NVIDIA Streamline/DLSS, and broader graphics-modding communities. This tool is mostly glue, testing, configuration, and compatibility work; the hard enabling technology comes from those projects.

## License

Project code is released under the MIT License. See `LICENSE`.

Bundled third-party source/header files remain under their own licenses:

- Dear ImGui: MIT License, see `source/external/imgui/LICENSE.txt`.
- ReShade headers: BSD-3-Clause OR MIT, see SPDX headers in `source/external/reshade/include/`.
- `runtime/shaders/ReShade.fxh`: CC0-1.0, see the SPDX header in that file.

Third-party runtime binaries such as NVIDIA DLSS/NGX, RenoDX DLSS5, d3d8to9, and dgVoodoo2 are not redistributed by this repository.
