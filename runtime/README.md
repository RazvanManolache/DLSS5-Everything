# Runtime Layout

Tracked runtime files in this repository:

- `x86-dx9-dx11/dlss5-feed.addon32`
- `host64/dlss5-feed-host64.exe`
- `shaders/DLSS5_Feed.fx`

Files you must provide yourself for a local game install:

- 32-bit ReShade with add-on support in the game folder: https://reshade.me/
- 64-bit ReShade with add-on support in the `host64/` helper folder: https://reshade.me/
- RenoDX DLSS5 add-on in the `host64/` helper folder. This repository does not redistribute it.
- NVIDIA `nvngx_dlss.dll` and `nvngx_dlssnr.dll` in the `host64/` helper folder. Use files from a legitimate local install/package.
- dgVoodoo2 D3D9 wrapper files for DX9 games: https://dege.freeweb.hu/dgVoodoo2/

Those third-party binaries are intentionally not tracked or redistributed here.
