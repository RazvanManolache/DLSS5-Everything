# Phoenix Runtime Deployment

## Selecting a compatible model

The selected neural model is checked **before** recovering or replacing a game
installation. Original and Lightweight remain separate choices; the installer
does not silently change one into the other.

- Phoenix DLSS5 prefers the matching shared native export, with its manifest and
  weight checksum verified. NVIDIA DLSS5 and comparisons involving NVIDIA use a DLL.
- Automatic DLL selection checks the actual named model resource in an x64 PE
  DLL. An older local DLL can remain the preferred source for Original, while a
  compatible managed download is selected for Lightweight.
- All cached NR DLL candidates are retained for this check, even when the normal
  payload list prefers a local import with the same filename. A filename or file
  version alone does not establish that a model is present.
- An explicitly selected incompatible DLL produces a clear error before any
  existing installation is changed. Importing only an SR/FG DLL does not pin an
  unrelated NR model selection.
- Model resource inspection reads only bounded PE directory entries, without
  executing the DLL or reading its full weight payload. These checks happen at
  installation time and add no per-frame work.

The shared export library keeps its existing containment and no-folder-links
policy. Test payloads should use a real model directory or an explicitly
configured real library path; a rejected linked library must not be worked
around by disabling its path checks.

The packaged addon contains the retained x64 helper, but **that does not supply
the optional internal runtimes**. Installing only `Phoenix.addon32` is not enough
to run an x64 library inside an x86 game or to use the portable x86 renderer.

## What the installer does

Automatic prefers compatible same-process execution; finding a helper in the
package does not mean it must run. Current graphics-capable Phoenix builds
advertise `CnrPHOENIXInProcessGraphicsV2`: x64 DX11 uses same-adapter GPU texture
sharing, DX12 uses the game queue, and DX9/10/OpenGL/Vulkan use native inference
with fenced CPU pixel transfers inside the game. Older DX12-only packages are
not advertised as supporting the new internal routes. Runtime initialization
can still reject an adapter, layout or setting and select a qualified fallback.

For x86, use Automatic or an explicitly supported native/portable mechanism.
The matching optional runtime must actually be installed and pass its model,
Windows/driver, TLS and address-space checks. No helper-only default is forced
when the add-on can select its own internal route.

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
- Uses relative `NativeRuntime` and `PortableRuntime` defaults for a new install.
  Existing absolute or addon-relative locations are checked and retained in both
  ReShade configurations, including after a saved route profile is restored.
  External runtimes are inspected in place and remain user-owned on removal.
  An explicitly configured missing folder never silently selects the default.
- Includes runtime DLLs and the bootstrap in component inspection, so a missing
  provider is visible rather than treating an addon alone as a complete install.

No NVIDIA model weights, system DLLs, game executables or logs are copied as
runtime companions. Shared model and DLL storage choices remain separate.

Legacy DirectDraw/DirectPlay COM compatibility is a separate first-party bridge.
Both preflight and installation check `Payload/legacy-com` first, then the app's
bundled `Runtime/legacy-com` when the payload file is absent. The selected DLL
must match the game's architecture and required exports. An explicitly supplied
invalid DLL still blocks installation; the fallback does not hide it. No COM
bridge is added unless the executable and selected legacy translation need it.

## Execution choices

In **Settings > Game installation defaults > Execution mechanism**, choose the
startup mechanism for future installs/reinstalls. Existing installations are not
changed simply by opening the app or pressing Play.

| Choice | Requirements |
| --- | --- |
| Automatic | Retains the helper; compatible native x64 DX12 can use the game's device |
| Internal native DX12 | Full x64 DX12 addon and Native RTX renderer |
| External x64 helper | The addon's retained compatibility helper |
| Internal portable | Architecture-matched C++ runtime and selected device provider; validated device or explicit experimental opt-in; not NVIDIA DLSS5 comparison |
| x64 library inside x86 | Complete WOW64 runtime, matching Windows image, and runtime-qualified NVIDIA driver |
| x86 host + native driver | Same profile checks, Phoenix DLSS5 Original, and an already large-address-aware x86 executable |

Explicit internal choices fail preflight if dependencies are unavailable, before
an existing installation is replaced. Automatic remains the safe default when
an optional archive is absent. Components lists the supplied runtime directories.
An x86 CPU-only package does not supply the Vulkan provider needed by portable
automatic device selection. It is not reported as a complete Vulkan runtime.

The experimental WOW64 bootstrap checks the Windows image fingerprint without
loading code during installation. Legacy packages also pin a driver directory
and hash. Coordinated v3 packages with `dynamic_driver_validation=1` instead
discover the installed driver, validate it in an isolated runtime probe, and
cache compatibility per driver. The installer verifies the packaged probe's
checksum; it does not require the removed legacy driver fields or claim that
driver qualification has already passed. These checks never copy Windows DLLs,
generate bootstrap offsets, or patch game executables. A failed qualification
uses another available execution mechanism. Runtime availability alone is not
proof of successful game presentation.

Imported, architecture-matched SDL libraries now expose their dynamically loaded
renderer capabilities as installation choices. When an executable uses SDL's
renderer-selection API, the app marks that choice as runtime-dependent; an
embedded OpenGL string alone does not prove OpenGL is active. This does not
rewrite the game's SDL configuration or force a different rendering backend.

## Expected game layout

An executable can import both DX9 and newer Direct3D entry points, then unload
its DXGI probe before choosing the actual renderer. Automatic installation uses
the ordinary startup-imported `d3d9.dll` frontend in that case, keeping one ReShade
instance alive to observe the selected graphics API. This changes the injection
entry point, not the game's renderer or settings. Explicit proxy choices and
translator chains retain their existing behavior. Capability strings alone do
not activate this rule. Neither does a **delayed** D3D9 import: optional fallback
DLLs may load after the modern renderer is already running. The scanner keeps
all imported capabilities separate from ordinary startup imports, and the
installer uses the latter to qualify this bootstrap. Modern renderers retain
their DXGI frontend when D3D9 is only delayed. The runtime status remains the authority for the API
actually used; a static capability list cannot prove it.

Explicitly selecting a small, valid packed executable keeps it visible as
**Unknown** so its API can be chosen. The bulk scanner still filters unknown
small helpers; an unknown executable is never silently declared DX9-compatible.

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

Regression tests cover alternate absolute/relative locations, preservation across
reinstall/removal, missing or wrong-bitness external runtimes, modified native
profile members, download/cache repair, checksums, paired imports,
architecture selection, dependency rejection, profile mismatch, installed-file
inspection and restore using temporary synthetic game directories. These are
CPU/file-operation tests, not GPU inference or retail-game compatibility tests.

### Enabled state and legacy profiles

Reinstallation also preserves validated translation dependencies. A DX8 game
with d3d8to9 installed is detected as producing DX9, but it still needs both
`D3D8.dll` (the translator) and `D3D9.dll` (ReShade). The installer carries that
validated chain into its new plan before restoring the outgoing manifest.
Selecting an unrelated API explicitly does not retain this DX8 chain. A file
regression covers the initial install, two reinstalls, and restoration; live
game results are recorded separately in the coverage campaign.

Reinstalling preserves tuning, but the selected renderer and enabled state belong
to the new installation request. Both `Backend` and the legacy `NeuralUplift`
master switch are reapplied after profile restoration. Otherwise an old profile
with `NeuralUplift=0` can disable the compatibility renderer at startup even when
the newly selected backend is Phoenix DLSS5. The on/off regression cases also
verify that unrelated tuning survives. This is distinct from processing-scale
controls and does not force a user's deliberately disabled installation on.

### Exported Godot renderer selection

Installer route labels describe automatic execution rather than asserting a
`host64` process for every capture backend. Eligible in-process inference is
preferred; the runtime can still choose a helper when its requirements demand
one. The live execution mode and inference PID identify the actual selection.
CPU capture/return alone does not imply an external process.

Godot engines contain several renderer entry-point strings. The scanner now
reads the actual exported Godot 4 project choice from an unencrypted PCK v2
`project.binary`, including a same-stem external PCK or an embedded footer pack.
An explicit `gl_compatibility` project setting selects the OpenGL proxy while
retaining the engine's other API choices and explicit user overrides. Windows
settings take priority over the generic setting; mobile settings do not select
the Windows renderer. No game-name list or game assets are extracted.

Directory/configuration reads have fixed size/count limits. Encrypted, malformed
and unknown pack formats leave ordinary detection in place. This is a default
project choice, not live proof of a renderer selected by launch arguments or a
separate runtime override. Format references: Godot's
[PCK loader](https://github.com/godotengine/godot/blob/4.2/core/io/file_access_pack.cpp)
and [project settings loader](https://github.com/godotengine/godot/blob/4.2/core/config/project_settings.cpp).

### Preserve existing and mixed legacy rendering chains

Renderer choices also include graphics imports from a bounded set of directly
imported, local engine DLLs with the executable's architecture. An executable's
own concrete graphics imports keep their priority. General-purpose DLL capability
strings are scanned only when the executable has no concrete renderer; they do
not establish which backend is active. Stray, unimported and mismatched DLLs do
not supply these choices.

SDL applications can select their renderer at runtime. A matching imported SDL
library supplies the available choices, with an explicit runtime-selection
warning for the SDL renderer API. A successful install still requires a live
API and frame-delivery check; the presence of OpenGL or Direct3D entry points
alone is not that check.

A recognized installed translator remains the automatic default even when the
executable also imports a natively supported API. Removing it can change adapter
identifiers, startup behavior and saved graphics settings. Native APIs remain
available as explicit choices. A leftover configuration file alone does not force
a fresh native DX9 game through a translator.

Translated routes can load ReShade early through an ordinary `dinput8.dll` or
legacy `dinput.dll` import. The import must belong to the executable or one
directly imported, local engine DLL of the same architecture. Inspection stops
at that one level and excludes graphics proxies. This keeps the selected graphics translator in place and
lets ReShade install its graphics hooks before an engine or overlay caches the
system DXGI module. Automatic selection requires that import and an absent or
compatible ReShade input DLL. An existing input mod is preserved.

The same loading options are available explicitly in the proxy selector and with
`--proxy dinput8.dll` or `--proxy dinput.dll` for supported desktop graphics routes. Delayed imports
and capability strings do not qualify as startup imports. The source ReShade
must export `DirectInput8Create` for DirectInput8, or the required
`DirectInputCreateA`/`DirectInputCreateW`/`DirectInputCreateEx` entry points for
legacy DirectInput. Missing source requirements are checked before
opening or recovering an installation transaction. Install, reinstall, restore
and saved renderer-profile checks cover the option. Its loading point does not
select the Phoenix DLSS5 execution backend or require an external helper.

Some older engines import DirectDraw for menus and dynamically load Glide for
gameplay. The scanner retains case-insensitive, named Glide DLL choices found in
that executable, alongside its DirectDraw import. The installation plan deploys
both required interfaces and protects both during route cleanup and reinstall.
Those same detections populate the UI's renderer choices. General-purpose sibling
DLL capability strings alone do not add a required companion translator.

Routing and reinstall/restore regression tests cover these rules. They do not
prove that every launch-time renderer selection is statically detectable. When an
older managed deployment hides the original translator behind its installed
ReShade proxy, use **Restore**, then scan and install again to inspect the original
chain. This is distinct from a validated in-place upgrade of that older manifest.

Packed executables may provide no reliable API evidence. Select their known API
explicitly rather than treating a guessed renderer as an automatic detection
success. At runtime, report the actual API, inference process and fallback reason;
an x86 allocation or device failure can legitimately require the helper even when
the same-process library is present.
