# DOS games through DOSBox Enhanced

DOS launches use [DOSBox Enhanced](https://github.com/RazvanManolache/dosbox-enhanced),
pinned as the real Git submodule `external/dosbox-enhanced`. Its embedded DOSBox
Pure engine sends original framebuffers to the native Phoenix DLSS5 RTX library.
ReShade, DOSBox Staging and the old feeder/visible-host chain are not used.

## Build and stage the component

```powershell
git submodule update --init --recursive external/dosbox-enhanced
.\scripts\Build-DosboxEnhanced.ps1 -PhoenixSdk C:\SDKs\Phoenix-native -PayloadRoot C:\PhoenixPayload -Model C:\Models\nvngx_dlssnr.dll
```

Use a native Phoenix DLSS5 SDK containing its compiled, model-free kernel bundle.
DOSBox Enhanced's own `BUILDING.md` lists its pinned dependencies and compiler
requirements. Its setup script obtains and verifies its NVIDIA graphics runtimes;
the model DLL is supplied separately. An optional `-CoreLibrary` selects a locally
built DOS engine and `-NvidiaSdkArchive` supplies the pinned NVIDIA SDK archive.

Component update obtains DOSBox Enhanced's Windows x64 portable release, verifies
the published asset SHA-256, and uses bounded archive extraction. An explicitly
staged source build is preserved. Existing Staging files are ignored rather than
selected or silently migrated into the new engine. Unknown user files are not
deleted.

## Launch behavior

- Each launch creates an isolated profile, graphics configuration and writable
  data directory under `Payload/.dos-sessions/<session-id>`.
- Immutable app/runtime files use hard links on supported volumes, otherwise
  copies. Compiled kernels and vendor graphics runtimes are referenced from the
  installed component. The selected model is read in place and is not extracted
  into the source repository or copied into the session.
- The process receives a `--profile` JSON argument, with no shell-command
  interpolation. Native Phoenix DLSS5 is selected; comparison, strengths and pass
  count are mapped to the Enhanced controls. NVIDIA SR/FG start disabled and
  remain available in the Enhanced UI. Native GPU inference does not imply
  zero-copy DOS framebuffer transport.
- Quick launch currently accepts DOS 8.3 program names and ASCII host paths.
  Other naming/mapping requirements use Enhanced's environment browser.
- Original game folders are writable so games can save normally. Existing
  Enhanced personal environments and settings are not edited by the installer.

Changes to the DOS implementation belong in the submodule repository; changes
to detection, staging and launch selection belong in DLSS5 Everything. Commit
submodule work there first, then update the parent's pinned Git link. Runtime
builds, models, games, profiles and captures remain outside Git.

CPU/build checks cannot prove game rendering or native inference. Actual DOS
validation must observe Enhanced's native Phoenix DLSS5 counters and processed output;
old ReShade bridge receipts are not evidence for this new path.
