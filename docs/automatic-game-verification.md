# Automatic game installation and verification

The default runner is Phoenix DLSS5's optimized native RTX executor. CUDA, Vulkan,
CPU and other inference runners remain optional experiments. A game using the
Vulkan graphics API can still use the native RTX executor.

Use **Install** in the library to add files without launching the game. Unknown,
runtime-selected or multiple unverified renderers open an installation-choice
prompt before game files change. A concrete manual renderer or a matching live
verified profile resolves that ambiguity. The prompt lists the renderer, route,
component readiness and launch arguments; Cancel leaves game files unchanged.

Use the separate **Verify & fix** button after installation to launch, check the addon
bridge and close the owned game and helper. It checks the route and launch
arguments recorded in the installation journal, even if Advanced options have
since changed. The initial check keeps installed files and settings and does not
clear existing logs. If it fails, a prompt shows the failure and untried routes,
including compatible alternate capture/proxy routes for the same renderer. Choose
**Install and verify** to replace the route and run another check; Cancel keeps
the last installation. The workflow stops on success and never silently replaces
a working route. It tests at most 12 routes per session and does not repeat the
same renderer/route/proxy combination. Incomplete installations require recovery
first. The result distinguishes completed
Phoenix DLSS5 output from a screenshot, gameplay, image quality and performance.
A loaded addon, successful NGX call or NVIDIA DLSS5 reference output is not sufficient
to qualify Phoenix DLSS5.

During the check, **Stop check** cancels the check and closes only its owned test
game; it keeps the installed route and does not close this app. The game pane
shows elapsed time and the current phase. An interrupted installation is offered
as **Undo interrupted**, with a recovery-specific confirmation.

The library distinguishes **Installed · unchecked**, **Output verified**, and
**Needs attention**. Verification details include the date, scope and report
folder. Results are retained across app restarts and bound to the executable's
content and installation journal; reinstalling or changing those invalidates the
result. Stopping a check does not overwrite the previous completed result.

For a serial library pass (this explicit command still installs and tries
alternatives automatically, unlike the UI's guided **Verify & fix** button):

```powershell
Dlss5CompatApp.exe --verify-library C:\Games --payload C:\PhoenixPayload --output C:\Verification\new-run
```

The output directory must be new. It contains the detected inventory, individual
reports and an incremental results index. One blocked game does not prevent
checking the rest. Ctrl+C cancels between checks or closes the current owned
processes through the normal verification cleanup.

## What automatic selection uses

- PE architecture and renderer imports, engine modules and bootstrap resources.
- Existing graphics wrappers, selected renderer arguments and installed store
  metadata. Optional engine XR strings do not automatically select a headset.
- Matching 32-bit or 64-bit packaged Phoenix DLSS5 frontends. Legacy DirectDraw,
  D3D8 and Glide inputs retain their required translator.
- A learned profile bound to the executable path, content hash and architecture.
  The profile remembers both a proven renderer and its original translated API.
  It does not key compatibility decisions on a game title.
- Bounded retries of detected alternatives and the supported D3D8 translators.
  Each attempt closes its owned processes before another installation begins.
- Source-engine content arguments are derived from engine architecture and
  installed `gameinfo.txt` metadata. A Vulkan alternative is offered only when
  matching engine modules advertise that renderer and its launch switch.

Verification requires a PID/session-bound reply, Phoenix-owned writeback,
multiple delivered/presented frames and `execution.kind=native` with the native
device selected. A retained launch handle can authenticate a game which later
restricts new process queries; creation time and exact executable checks remain
mandatory. Active or unidentifiable mailbox requests are preserved.

Store-URI checks also watch for newly started bootstraps inside the selected
installation. Adopted processes and their descendants share the check's cleanup
lifetime, including when the expected renderer never appears. Pre-existing
processes and the external store client are excluded. Processes which exit before
observation, or deny identity/lifetime queries, cannot be safely adopted.

Known old project addons and recognized graphics components are backed up before
replacement. Unknown mods are preserved and can block an incompatible plan.
Legacy backup journals moved to another drive can be adopted only after a
complete local path, backup and lock audit; the old journal is retained. A
transient file-sharing lock after an owned game exits is waited on briefly,
without bypassing other preflight checks.

## Current validation limits

Local October 2026 testing includes real D3D9, D3D11, D3D12, OpenGL and Vulkan
game routes and an earlier DOSBox session through the packaged native bridge.
DOS now uses the [DOSBox Enhanced direct library integration](dosbox-enhanced-integration.md);
that earlier DOS receipt is historical and does not qualify the replacement. D3D9 8x
MSAA fixtures passed in both bitnesses with byte-identical reference output and
matching bridge screenshots. This does not establish HDR, stereo VR, every
renderer configuration or every game.

Startup menus and attract sequences are reported as such. Missing media/data,
dependencies, account prompts, protected launchers and unavailable headset
hardware remain separate blockers. No title is declared playable merely from
installation or frame counters. Current profiles do not fingerprint every
renderer dependency or game configuration file; re-verify after changing those.
