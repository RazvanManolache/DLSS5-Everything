# Phoenix DLSS5 Migration Validation

For the later real-game installation workflow and its acceptance boundaries,
see [automatic game verification](automatic-game-verification.md). The dated
checkpoint below is historical and does not describe the later game tests.

Local validation checkpoint, 11-12 October 2026. Nothing was published and no game installation was modified or launched.

## Application Tests

The current Windows .NET test run passed **743 tests**, with zero failures and **two opt-in live capture tests skipped** (745 total). The suite covers release selection/checksums/cache reuse, archive traversal and link rejection, installer ownership and rollback, profiles and renderer overrides, discovery and exclusions, launch arguments, temporary restoration, anti-cheat acknowledgements, diagnostics, live/offline configuration, and UI layout/accessibility. Direct-feeder cases cover static export inspection, model-only installation without SR, host/provider compatibility and preserved game-DLSS configuration. Screen Studio adds settings, presets, staging cancellation, operation ownership, startup error reporting and screenshot recovery checks. The distribution-notice smoke script separately passed 37 checks.

Vulkan registration tests use fake registry state; the real-filesystem adapter also has exclusive-handle contention tests. No real Vulkan registration was changed. Native test programs separately exercise feeder IPC and host/shader contracts. Tests are not gameplay, image-quality or performance certificates.

UI screenshots were inspected at normal and minimum window sizes. Layout tests cover 1060x740, 1400x900 and 1920x1080. The preview library contains public example titles and synthetic paths, not personal game metadata.

## Screen Studio Capture

The independent Windows Graphics Capture worker passed **25 native CPU contract checks**. Screen Studio's focused managed suite passed **29 tests**, included in the full-suite total above. Offscreen captures covered all five tabs in both themes at three window sizes; no Computer Use was used.

The separate opt-in **controlled-window GPU test passed** against generated content: exact bypass within one channel value, reduced-scale screenshot cropping, scale/resize resource rebuilding, bypass without a wipe separator, actual neural output, static-window strength synchronization and owned-process shutdown. The final run recorded/submitted/completed **25 neural evaluations**, with no terminal error or ownership deferrals. Its sampled one-pass RGB mean absolute difference was **8.313/255**, which proves changed pixels for this input, not improved quality or gaming performance.

The whole-display test was interrupted by another window covering the pattern and did not pass. **Desktop feedback exclusion remains unverified** and requires a separate explicit opt-in on an idle, personal-content-free test desktop. No game or headset was tested by this capture workflow. Recording, Spout, frame generation and HDR remain unimplemented; see [the complete checklist and limits](screen-studio.md).

The tested and locally packaged screen worker SHA-256 is `f997b84c0763835a8e76b61977199eeef4ed0a29fdbdd96f2cd50102a7ac4f75`. Temporary runtime/capture/log evidence remains ignored and is not distributed.

## Native CPU Tests

- Direct host release build: **2,439 CPU configuration, format, shader and safety checks passed**, plus direct API loader/version/reply/exception checks and 51 stereo schedule checks.
- Native transport probe: **one PNG codec CPU roundtrip passed**.
- Local Phoenix DLSS5: **3,610 existing native CPU checks plus 32 direct-API contract checks passed**.
- Phoenix DLSS5 WARP resolution/descriptor test passed, including unchanged-settings timing/budget regressions. This uses software D3D12 and no model inference.

These CPU checks do not establish GPU inference. Lorentz's final read-only review confirmed the four host safety fixes were addressed, with no remaining findings: byte-format iteration barriers and failed command-list closure, shared-output timeout ownership, fence-wake/device-removal handling, and BGRA-to-RGBA bypass conversion.

## Review Feedback Applied

Independent code-safety, functionality, gamer and casual-user reviews led to these changes:

- Preserve outside edits during Vulkan setup and rollback; hold an exclusive verified file handle through the final write or deletion.
- Validate live-control destinations before writing, serialize configuration updates and restore exact prior bytes on failure.
- Require explicit anti-cheat risk acknowledgement in the UI, command-line installer and smoke runner.
- Preserve saved route/proxy choices and use the currently entered launch arguments.
- Carry edited launch arguments into generated launchers, preserve recent tuning across removal/reinstallation, and roll back component publication if provenance cannot be saved.
- Make missing prerequisites and the setup action visible, collapse advanced options, provide empty-search recovery, and retain operation failures in the footer.
- Keep fields and primary actions inside the minimum window, prevent clipped table headings, give icon buttons accessible names/tooltips, and disable mutable install inputs while busy.
- Make smoke tests require fresh Phoenix DLSS5 native-completion and output-routing evidence, not an NGX success message alone.

The [Swapper audit](swapper-feature-audit.md) records intentional differences, including hosted services that are not implemented. Non-English translations remain partial.

## Direct Feeder Runtime Evidence

The host uses `input_provider=feeder_d3d12`, without an NGX import, SR feature creation or DLAA evaluation. Isolated test folders contained ReShade, Phoenix DLSS5 and the model copy, but no `nvngx_dlss.dll`; loaded-module diagnostics reported SR absent. The actual returned frames were inspected and compared numerically, not inferred from API success alone.

CPU RGBA8 rows and synthetic shared-GPU BGRA8-to-RGBA8 textures/fences each passed exact bypass and one/two-pass enhancement. Each completed **3 recorded / 3 submitted / 3 completed** evaluations and **462 own-network launches**. The returned CPU/shared images were pixel-identical. One pass changed 2,728,941 RGB pixels (MAE 6.615/255, RMS 9.384/255); two passes changed 2,754,070 (MAE 9.658/255, RMS 13.767/255). Alpha was unchanged. A larger difference is not a quality improvement claim.

An additional Input-mode test kept Phoenix DLSS5 armed with uplift enabled, selected backend 0 and exercised the API's explicit bypass outcome. Its returned image matched the decoded input exactly, with zero recorded/completed neural frames and no terminal error. This is separate from bypassing processing with the feeder's enable flag.

A four-frame alternating-eye shared-GPU probe completed **4 evaluations / 616 own-network launches**, with per-stream records `[0,2,2]` and resets `[0,1,1]`. Each eye retained its history instead of resetting on every switch. This is synthetic transport/state evidence, not real headset gameplay acceptance. Stereo NVIDIA-reference comparison is explicitly unsupported until NR eye features are isolated.

The exact tested and packaged host SHA-256 is `8ac3311615eb1a704b16835668aac29067d5b3002da7f8631d6a848efc89fb7b`. The explicitly imported local Phoenix DLSS5 build is `d79a13c73dc8fb0dfbfdac2357dd5726a7633b46a1ab0a905516362e017ee1b9`; it is not included in the app package or represented as a published release. Local receipts remain ignored under `.local-validation/host-direct-*`.

Initial probes caught and corrected ReShade proxy-device identity handling and the host's previously implicit descriptor-heap binding state. Those failed probes were not counted as successful inference. Static review found no remaining P1/P2 issue in the default direct feeder path after the fixes.

## Earlier Hook-Route Evidence

The measurements in this section used the earlier SR/DLAA hook route, now replaced by the direct API. They are retained for provenance, not as proof of the current implementation.

A local Phoenix DLSS5 patch adds NGX SuperSampling feature ID 1 to its existing interception filter. The unmodified published `0.8-beta` does not accept that ID. The downloader continues to obtain actual upstream releases; use explicit local-build import until the compatibility fix is published upstream.

The isolated float-texture host test completed two native frames, recorded 308 own-network launches and confirmed output writeback. The exact bypass image matched the decoded input. Phoenix DLSS5 output changed 2,728,565 RGB pixels, with mean absolute difference 6.564/255 and RMS difference 9.302/255. This establishes processed output for that image and configuration only.

The 8-bit adapter passed isolated actual host IPC tests with CPU RGBA8 rows and synthetic D3D12 shared BGRA8 input to RGBA8 output. Each tested exact bypass followed by iteration counts 1 and 2, with **3 recorded, 3 submitted and 3 completed native evaluations**, **462 own-network launches**, explicit output writeback, zero deferrals and no terminal errors. Iteration 1 changed 2,729,270 RGB pixels (MAE 6.551/255, RMS 9.321/255); iteration 2 changed 2,692,024 RGB pixels (MAE 7.067/255, RMS 11.349/255). Alpha was unchanged. CPU and shared-transport output PNGs were byte-identical at both iteration counts.

The final shared BGRA-to-RGBA probe used the exact shipped host SHA-256 `33c41833621b380bd1864988b63c1ab05348db8a3418abd9ad7d6fbb7dc68df1`; it was not rebuilt afterward. The earlier `989b539f...` build differs in seven timestamp bytes only (PE timestamp fields and embedded `__TIME__`). The sibling Phoenix DLSS5 checkout's local `build/sr_compat_local_001/phoenix.addon64` has SHA-256 `a8db3e69f2333b43a548ffde20506108a3ecd7b0a28f5729ffc5a39c60c205d4`. It remains unpublished. Detailed local receipts and images are in ignored `.local-validation/host-format-adapter-final-evidence.json` and `.local-validation/host-shared-bgra-final-33c41833-001/`.

Native CPU checks also passed: 2,437 host contracts plus the PNG codec roundtrip, and 3,610 checks in the locally patched Phoenix DLSS5 project. The reviewed host fixes cover invalid byte-format UAV barriers, command-list close failure, stale fence events/device removal, timeout readiness and BGRA bypass conversion.

Synthetic shared transport is not an actual D3D11 game producer. No DX9/DX11/DX12, Vulkan, DOS or VR gameplay acceptance is claimed for this migration. Portable-provider performance, temporal stability, HDR, image-quality improvement and NVIDIA DLSS5 equivalence are not established.

## Packaging

The self-contained local app build was checked for external payload DLLs, model data, logs, user settings, test results, debug symbols and personal path strings. None were present. External Phoenix DLSS5/NVIDIA DLSS5/ReShade/wrapper/emulator components remain outside the application package and repository. Downloaded and local test artifacts remain ignored.
