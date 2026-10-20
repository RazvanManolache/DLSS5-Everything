# Feature acceptance

The current local matrix is `local-build/validation-current/feature-matrix.html`.
It maps all fourteen product areas to measured behavior, contract tests, failures,
fallbacks and unavailable hardware. Shared unit-suite counts overlap; do not add
them into a feature-completeness percentage.

## Local fixes after the release audit

`local-build/validation-current/FIXES.html` records the five requested repairs.
The candidate is separate from public 0.81: it builds compatible integration
sources around unchanged published kernels and runner libraries. This local
build has not been published.

- Worker retries reset once per captured input. An additive feeder entry point
  identifies immutable inputs held across retries; ordinary moving engine
  frames retain their age limits. Output acceptance requires delivery/composition
  counters and a retired GPU submission.
- Vulkan swizzling writes GPU upload memory once per pixel, without reading
  that memory back to swap channels.
- Color-only capture explicitly rejects unavailable temporal upscalers,
  three-way comparison and presentation-FPS targets. Saved unavailable values
  remain visibly inactive; working settings are not silently rewritten.
- Install and restore temporary files enter the recovery journal before copying.
  Actual forced-termination recovery leaves no orphaned copy file.
- The screen worker verifies capture exclusion. The monitor test requires two
  whole-monitor input matches while a distinct output overlay is visible.

Build and qualify the candidate with separately supplied external models:

```powershell
python tools/acceptance/build_release_source_candidate.py
$candidate = '..\dlss5-phoenix-addon\artifacts\current\everything-fixes'
python tools/acceptance/test_device_choices.py --candidate $candidate
python tools/acceptance/test_fixed_transport.py --candidate $candidate --visible --only vulkan-x86 dx11-x64 dx12-x64
python tools/acceptance/test_feature_controls.py --candidate $candidate
python tools/acceptance/test_monitor_capture.py --candidate $candidate --all
python tools/acceptance/test_fixed_graphics.py --candidate $candidate
python tools/acceptance/render_fixes.py
```

CPU checks permit a longer bounded observation period; a single thread can take
tens of seconds per output. This is functional acceptance, not a real-time CPU
performance claim. Optional SYCL CPU execution requires its private Intel CPU
ICD and is omitted from discovery when that dependency is missing.

## Published runtime baseline

The October 18 checks use the unchanged public Phoenix **0.81** addon and DLL.
Only Everything and synthetic test fixtures are built locally. Public archive
digests, installed addon hashes and the external model checksum are retained in
the current receipts. A development Phoenix build is a different test baseline.

The extra runners under `tools/acceptance` require the sibling test bench's clean
fixtures and Python dependencies, the existing Debug Everything build, the public
0.81 archive under the Phoenix checkout's `artifacts/current/release-0.81`, and
`local-build/validation-current/github-release.json` containing its GitHub release
metadata. Model-based tests use the separately supplied sibling
`_dlls/nvngx_dlssnr.dll`. Vendor fallback checks also use `_dlls/nvngx_dlss.dll`
and `_dlls/libxess.dll`; no model or vendor DLL is added to this repository.

Run only when GPU/device testing is intended:

```powershell
python tools/acceptance/test_online_setup.py
python tools/acceptance/test_interrupted_install.py
python tools/acceptance/test_feature_controls.py
python tools/acceptance/test_feature_controls.py --focused
python tools/acceptance/test_feature_controls.py --model-only
python tools/acceptance/test_device_choices.py
python tools/acceptance/test_device_choices.py --only-device cuda
```

The online check downloads real public prerequisites into an empty temporary
payload. The interruption check kills only its own installer in a temporary
synthetic game folder. The other checks use authenticated addon controls and
require fresh output, not merely successful setting replies. Temporary staging
is cleaned on exit; compact current receipts replace their own files.
Failures produce a nonzero exit code. Unsupported capture choices must be
explicitly rejected with a route explanation and unchanged working settings.
A displayed device is not proof of a working inference engine.

Regenerate the summaries without launching more tests:

```powershell
python tools/build_feature_acceptance.py
python tools/build_functionality_inventory.py
```

Recording and system-audio capture are removed. Image/video file conversion,
screenshots and Spout are separate retained features. Synthetic HDR transport
does not establish physical-display accuracy. Emulator passes do not qualify
physical-phone capture, Wi-Fi behavior or a VR headset.
