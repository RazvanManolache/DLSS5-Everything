# dlss5-dx9-dx11-x86compat

Experimental compatibility kit for running RenoDX DLSS 5 Neural Rendering with older 32-bit games.

This repository is intentionally narrow. It contains the 32-bit ReShade feeder add-on, the 64-bit helper process it talks to, the ReShade feed shader, and small install/config helpers for 32-bit D3D11 and D3D9-through-dgVoodoo2 testing.

It does not claim general DLSS5 support for every API or game. It should be treated as a local experiment that needs per-game log validation.

## What We Actually Tested

Observed locally:

- 32-bit D3D11 path: game loads 32-bit ReShade, `dlss5-feed.addon32` attaches, the feed shader compiles, the 64-bit helper starts, shared textures/fences connect, and the helper logs DLSSNR feature creation/evaluation.
- D3D9 path through dgVoodoo2: D3D9 game is translated to D3D11 first, then the same 32-bit feeder/64-bit helper path runs.
- F9 comparison cycling works on the 32-bit path: original, DLSS output, split, amplified difference, and difference/DLSS.
- PrintScreen capture support is present for saving original and processed frames when the feed path is active. The paired feeder capture is queued after ReShade finishes its own screenshot and then delayed several frames, because some wrapped games do not tolerate competing readbacks on the same frame.
- Visual impact varies a lot. Some old DX9 games showed little useful improvement outside faces, even when the logs proved the path was active.

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

## Not Included

The repository does not redistribute third-party runtime binaries:

- ReShade DLLs.
- NVIDIA NGX/DLSS DLLs such as `nvngx_dlss.dll` and `nvngx_dlssnr.dll`.
- RenoDX DLSS5 add-on binaries.
- dgVoodoo2 binaries.
- Game files, logs, screenshots, cache files, or local machine paths.

The local working folder may contain some of those files for testing, but `.gitignore` keeps them out of the public repository.

## Required Downloads

Fetch third-party pieces from their original projects:

| Package | Link | Used For |
| --- | --- | --- |
| ReShade with full add-on support | https://reshade.me/ | 32-bit game injection and 64-bit helper injection. |
| dgVoodoo2 | https://dege.freeweb.hu/dgVoodoo2/ | Optional DX9-to-D3D11 wrapper path. |
| NVIDIA DLSS / NGX SDK files | https://github.com/NVIDIA/DLSS | Headers/libs for building the helper; runtime DLSS DLLs must come from a legitimate local install/package. |
| NIGos DLSS5 bridge | https://github.com/NIGos/dlss5-bridge | Reference project we compared against; not required by this x86 compatibility package. |
| RenoDX project | https://github.com/clshortfuse/renodx | RenoDX framework/source context. The DLSS5 neural-rendering add-on binary is not redistributed here. |

## Install Shape: 32-bit D3D11

1. Install 32-bit ReShade with add-on support for the game.
2. Put `dlss5-feed.addon32` next to the game executable.
3. Put `DLSS5_Feed.fx` in `reshade-shaders/Shaders/`.
4. Create `host64/` next to the game executable.
5. Put `dlss5-feed-host64.exe` in `host64/`.
6. Add the required third-party 64-bit runtime files to `host64/`: 64-bit ReShade, RenoDX DLSS5, `nvngx_dlss.dll`, and `nvngx_dlssnr.dll`.
7. Enable a motion-vector provider above `DLSS5_Feed` in ReShade.
8. Verify using logs before judging the image.

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

DirectX 9 is not handled directly. The tested path is:

```text
D3D9 game -> dgVoodoo2 -> D3D11 -> 32-bit ReShade -> dlss5-feed.addon32 -> host64 helper
```

Basic steps:

1. Use dgVoodoo2's 32-bit D3D9 wrapper in the game executable folder.
2. Configure dgVoodoo2 to output D3D11.
3. Install ReShade as the D3D11/DXGI hook, not as `d3d9.dll`.
4. Then use the 32-bit D3D11 install shape above.

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

See `LICENSE`.
