# Phoenix Runtime Deployment

The packaged addon contains the retained x64 helper, but **that does not supply
the optional internal runtimes**. Installing only `Phoenix.addon32` is not enough
to run an x64 library inside an x86 game or to use the portable x86 renderer.

## What the installer does

- Downloads the optional `Portable-Runtimes` and `Experimental-WOW64` ZIPs when
  published alongside the selected addon release. Release digests and available
  checksums are verified. Packages are never borrowed from another release.
- Imports a sibling `PhoenixRuntime` directory with a local addon. Select a
  complete package, not a lone addon copied away from its companions.
- Normalizes SDK-style `win-x86/bin` and `win-x64/bin` layouts while retaining
  provider subdirectories and release licenses. Development headers, static
  libraries and examples are not installed into games.
- Publishes a bounded, fingerprinted runtime library associated with the addon
  pair. Verified unchanged packages are reused; repairs publish a new directory
  rather than replacing DLLs another process may have mapped.
- Installs `win-x86` companions for x86 games, `win-x64` for x64 games, and the
  optional `wow64` package for x86 games. Providers, shaders, kernels,
  configuration and notices travel together. Game-local files participate in
  preflight, backup, recovery and restore.
- Writes relative `NativeRuntime` and `PortableRuntime` paths into both ReShade
  configurations, including after a saved route profile is restored.
- Includes runtime DLLs and the bootstrap in component inspection, so a missing
  provider is visible rather than treating an addon alone as a complete install.

No NVIDIA model weights, system DLLs, game executables or logs are copied as
runtime companions. Shared model and DLL storage choices remain separate.

## Execution choices

In **Settings > Game installation defaults > Execution mechanism**, choose the
startup mechanism for future installs/reinstalls. Existing installations are not
changed simply by opening the app or pressing Play.

| Choice | Requirements |
| --- | --- |
| Automatic | Retains the helper; compatible native x64 DX12 can use the game's device |
| Internal native DX12 | Full x64 DX12 addon and Native RTX renderer |
| External x64 helper | The addon's retained compatibility helper |
| Internal portable | Architecture-matched C++ runtime, Vulkan provider and shaders; not NVIDIA DLSS5 comparison |
| x64 library inside x86 | Complete WOW64 runtime and matching Windows/NVIDIA driver profile |
| x86 host + native driver | Same profile checks, Phoenix DLSS5 Original, and an already large-address-aware x86 executable |

Explicit internal choices fail preflight if dependencies are unavailable, before
an existing installation is replaced. Automatic remains the safe default when
an optional archive is absent. Components lists the supplied runtime directories.
An x86 CPU-only package does not supply the Vulkan provider needed by portable
automatic device selection. It is not reported as a complete Vulkan runtime.

The experimental WOW64 bootstrap is tied to a verified Windows image and driver.
The installer checks fingerprints without loading the driver or running
inference. It does not remove the addon's checks, generate bootstrap offsets,
copy Windows DLLs, or patch an executable to make a profile appear compatible.
A mismatch requires a matching package or another execution mechanism. Runtime
availability is not proof of successful game presentation.

## Expected game layout

```text
game/
  <game executable>
  <ReShade proxy DLL>
  Phoenix.addon32                 # or Phoenix.addon64
  Phoenix.json
  ReShade.ini
  ReShadeVR.ini
  PhoenixRuntime/
    win-x86/                     # x86 game, when supplied
      phoenix_runtime_cpp.dll
      phoenix_cpu_cpp.dll
      phoenix_vulkan_cpp.dll
      phoenix_native_cpp.json
      vulkan/
        phoenix_vulkan.dll
        <shader files>
    win-x64/                     # x64 game instead of win-x86
    wow64/                       # optional x86 native mechanisms
      bootstrap.json
      wow64ext.dll
      phoenix_bootstrap64.dll
      phoenix_driver64.dll
      phoenix-nvngx.dll
      phoenix_native.dll
      native/
        native_manifest.json
        native_graph.json
        <kernel files>
      licenses/
```

Only directories supplied by the matching package are installed. Shader and
kernel subtrees must not be flattened. Use **Components > Update** or reimport a
complete local package, then reinstall to repair an older addon-only installation.
Removing a game installation leaves the central library intact.

Regression tests cover download/cache repair, checksums, paired imports,
architecture selection, dependency rejection, profile mismatch, installed-file
inspection and restore using temporary synthetic game directories. These are
CPU/file-operation tests, not GPU inference or retail-game compatibility tests.
