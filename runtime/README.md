# Runtime Layout

Tracked runtime files in this repository:

- `x86-dx9-dx11/dlss5-feed.addon32`
- `host64/dlss5-feed-host64.exe`
- `shaders/DLSS5_Feed.fx`
- `shaders/ReShade.fxh`

Files you must provide yourself for a local game install or payload:

- ReShade full-add-on DLLs: https://reshade.me/
- RenoDX DLSS5 add-on: https://github.com/clshortfuse/renodx and https://github.com/yumlevi/renodx-dlss-installer/releases
- NVIDIA `nvngx_dlss.dll`: https://github.com/NVIDIA/DLSS/releases
- NVIDIA Streamline files, if needed by the RenoDX package: https://developer.nvidia.com/rtx/streamline/get-started
- NVIDIA `nvngx_dlssnr.dll`: use an official or otherwise legitimate DLSS Neural Rendering runtime source.
- dgVoodoo2 D3D9 wrapper files for DX9 games: https://dege.freeweb.hu/dgVoodoo2/ and https://github.com/dege-diosg/dgVoodoo2/releases

Those third-party binaries are intentionally not tracked or redistributed here.
