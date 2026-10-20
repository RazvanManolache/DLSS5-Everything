# DLL Storage and Game Startup Defaults

Open **Settings** and choose:

- **Copy NVIDIA DLL into each game**: checked keeps the existing deployment policy.
  Unchecked references the selected DLL in the component library instead of copying
  `PhoenixModel.dll` or a helper's optional `nvngx_dlssnr.dll` into every game.
- **Default renderer**: Phoenix DLSS5 or NVIDIA DLSS5.
- **Default neural model**: Original or Lightweight FP8.
- **Execution mechanism**: Automatic, internal native DX12, external helper,
  internal portable, x64 library inside x86, or x86 host/native driver.
  Explicit internal selections must pass dependency/profile checks before
  installation changes any game files.

The app remembers these choices in its local library settings. Existing saved
libraries keep the previous defaults: local copies, Phoenix DLSS5, Original,
Automatic execution. [Runtime packages and final layout](phoenix-runtime-deployment.md).

The settings seed native-input installations, packaged x86/x64 bridges and legacy
helpers, including temporary Windows installations and user-selected verification
replacements. They are written on install/reinstall, so games start with those choices
whether launched through the app or through their generated launcher. Merely opening
the app or pressing Play does not reset an installed game's subsequent per-game tuning.
Reinstall to apply changed defaults to an existing installation. The separate DOSBox
Enhanced profile and Screen Studio/Streamer settings are not changed by these defaults.

## Shared Library Mode

1. Download/import the compatible library through the existing component workflow.
   This option does not add a new NVIDIA download source or redistribution permission.
2. Uncheck **Copy NVIDIA DLL into each game**.
3. Choose the default renderer and model, then install/reinstall the game.

`Phoenix.json` contains the absolute shared path and its SHA-256 in `weight_source`,
including the selected model ID. `ReShade.ini` and `ReShadeVR.ini` record `ModelId`,
`Backend`, matching NVIDIA preset selection and `NvidiaDllFolder`; the JSON also supplies
`nvidia_dll_folder` for compatible addons. Helpers receive the same external model source.

No shared DLL is listed as game-owned or deleted by game uninstall/restore. An old
installer-owned local copy is removed through the normal restore transaction when
switching to shared storage. Unowned local files are not deleted to enforce a preference.

Keep the component-library location available. Moving, deleting or replacing the
shared DLL can affect all installations referring to it. The checksum remains pinned:
after a library update, reinstall affected games to authenticate the new file. There is
no silent substitution of an incompatible or modified model. Shared mode does not mean
all processes share one set of live GPU model allocations.

## Model and Renderer Selection

The selected model ID is written to both the weight source and Phoenix panel. NVIDIA
model selection follows that same ID, rather than accidentally retaining a different
preset from an earlier route profile. Other saved strength controls remain independent.

Lightweight FP8 requires a supporting addon and an NVIDIA container with
`WEIGHTS_CC_SMALL_FP8`, such as the compatible 310.9.16 edition. Original-only files
cannot become Lightweight by changing a setting. Selecting a model is configuration,
not proof of successful inference; the addon performs its normal model authentication
and capability checks when loaded.

Tests use synthetic PE files and preview UI, not retail games or GPU inference. They
check copy/shared installation, profile migration, model/renderer serialization, UI
binding, preflight ownership and restoring without changing the shared payload.
