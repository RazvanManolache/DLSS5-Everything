# Optional software presentation hook

Stock ReShade 6.8.0 deliberately skips Direct3D 10/11 software devices. For an
application that actually renders through WARP, Phoenix consequently receives
no presentation events. Changing the application to hardware rendering would
hide that compatibility gap.

`python scripts/Build-SoftwareReShade.py` builds both architectures from pinned
upstream source. Visual Studio 2022 C++ tools, a Windows SDK, Python and Git are
required. The script downloads the pinned ReShade source and dependencies,
applies the maintained patch, compiles in a temporary directory and retains only
the two current runtime DLLs, the upstream license and build metadata.

The patch adds `APP/IncludeSoftwareDevices=1`, off by default, and exports
`CnrDLSS5SoftwareDevices` so the installer can verify the variant. The installer
requires matching architecture and capability, enables it after a log shows
that this executable was rejected as a software renderer, or after explicit
per-game opt-in. Guided verification offers this as a replacement for approval;
it does not silently replace a working hardware hook.

Phoenix uses a hardware inference helper for a software render device, including
Direct3D 12. Software rendering and CPU frame transfer can remain very slow;
successful presentation is not evidence of playable performance.

This optional build is modified ReShade, not an official upstream release.
Preserve `Runtime/software-reshade/LICENSE.md`, `DEPENDENCIES.txt` and `BUILD.json`
in distributions. The dependency bundle retains the pinned upstream license texts.
