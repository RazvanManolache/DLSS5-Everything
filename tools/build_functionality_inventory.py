"""Render the source-reviewed functionality inventory without launching tests.

Descriptions and gaps are reviewed statements, not inferred coverage percentages.
Receipts, source pointers, control keys and input hashes are refreshed on each run.
Output replaces two small files in local-build/functionality-inventory.
"""
from collections import Counter
from datetime import datetime, timezone
from html import escape
from pathlib import Path
import hashlib
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
TESTING = ROOT.parent / 'dlss5-everything-testing'
APP = 'app/Dlss5CompatApp/'
TESTS = 'tests/Dlss5CompatApp.Tests/'
FEATURES = []


def feature(area, title, behavior, coverage, gaps, sources, tests=(), priority='P2'):
    FEATURES.append(dict(id=f'F{len(FEATURES)+1:02}', area=area, title=title,
        behavior=behavior, coverage=coverage, gaps=gaps, priority=priority,
        sources=list(sources), tests=list(tests)))


def app(name):
    return APP + name + '.cs'


def test(name):
    return TESTS + name + 'Tests.cs'


def catalog():
    feature('Library', 'Find games and executables',
        'Manual executable, drag/drop and bounded folder scans; saved scan roots, cancellation and exclusions.',
        'Unit/UI', 'Real-world false positives, missed renderers, protected executables and unusual layouts are not exhaustively covered.',
        [app('GameScanner'), app('MainForm.Accessibility')], [test('LibraryServices'), test('ScannerExecutableRole')])
    feature('Library', 'Store discovery',
        'Reads Steam, Epic, GOG, Ubisoft and Xbox installation metadata; merges manual entries without conflating separate installs.',
        'Unit/UI', 'Five synthetic store formats are tested. Current live store clients, restricted Xbox installs and every metadata revision are not certified.',
        [app('LibraryDiscovery')], [test('LibraryServices')])
    feature('Library', 'Renderer, architecture and engine detection',
        'PE imports/strings, engine modules, bootstraps, wrapper evidence and native-DLSS signals drive route choices; titles are not compatibility rules.',
        'Unit + frozen fixtures', 'Detection remains a hypothesis until runtime verification. Optional renderer modules and protected launchers can be ambiguous.',
        [app('GameScanner'), app('EngineBootstrap'), app('Models')], [test('RendererDetection'), test('ScannerExecutableRole'), test('EngineBootstrap')])
    feature('Library', 'Emulator profiles',
        'Nineteen metadata profiles supply aliases, renderer choices and setup hints while retaining observed evidence.',
        'Unit/UI', 'Metadata tests do not prove that all nineteen emulators, ROMs or advertised renderers work with Phoenix.',
        [app('EmulatorProfiles')], [test('EmulatorProfiles')])
    feature('Library', 'Installed My Games library',
        'Separate startup library based on valid manifests and present files; independent filters, favorites, refresh and list/cover views.',
        'Unit/UI', 'Install-file evidence is not successful enhancement. No exhaustive live-library acceptance run on the present build.',
        [app('MainForm.MyGames'), app('RuntimeDiagnostics')], [test('MyGames'), test('ArtworkLayout')], 'P3')
    feature('Library', 'Search, sorting, favorites, hide and recents',
        'Persistent per-game state, hidden-game recovery, list/preview selection, recently viewed entries and command search.',
        'Unit/UI', 'Very large real libraries, prolonged UI use and all filter combinations remain acceptance gaps.',
        [app('MainForm.Library'), app('MainForm.Convenience'), app('LibraryState')], [test('Convenience'), test('ButtonBehavior'), test('LibraryServices')], 'P3')
    feature('Library', 'Artwork and custom covers',
        'Local Steam artwork, optional title-only remote search, validated downloads/cache, placeholders and custom-cover priority.',
        'Unit/UI', 'Mocked network and synthetic local-art tests do not establish current external-store service availability or universal title matching.',
        [app('GameArtwork'), app('MainForm.Artwork')], [test('GameArtwork'), test('ArtworkLayout')], 'P3')
    feature('Library', 'Route, proxy and launch-argument overrides',
        'Saves validated renderer, Native/Feeder, DXGI/D3D11 proxy and launch-argument choices per executable.',
        'Unit + installer fixtures', 'Not every override is valid on every API; real launcher/store combinations still need acceptance tests.',
        [app('MainForm.RouteOptions'), app('GameProfile'), app('GameLauncher')], [test('GameRouteOverride'), test('WorkflowSafety'), test('GameLauncher')])
    feature('Library', 'Learned renderer profiles',
        'Remembers verified renderer/hook/input route against executable path, hash and architecture, preserving translator requirements.',
        'Unit + older game evidence', 'Profiles do not fingerprint every renderer DLL, driver or game configuration; those changes can stale a learned result.',
        [app('VerifiedRendererProfiles'), app('SmokeTestRunner.Resolve')], [test('VerifiedRendererProfile'), test('StartupDiagnostic')])
    feature('Library', 'Play, store launch and owned-process cleanup',
        'Direct and store-URI launch, engine arguments, generated launchers, bootstrap/child adoption and bounded cleanup.',
        'Unit + older game evidence', 'Account prompts, elevation, protected/short-lived processes and every launcher are not covered by the recent synthetic gate.',
        [app('GameLauncher'), app('GameLaunchLifetime'), app('StartupDiagnostics')], [test('GameLauncher'), test('GameLaunchLifetime'), test('ProcessLifetime')])

    feature('Installation', 'Install, update, remove and reinstall',
        'Uses matching x86/x64 packaged addons, selected model and ReShade route; tracks writes and restores originals.',
        'Recent live fixtures', 'Recent complete lifecycles passed D3D9 x86 and D3D11/D3D12/OpenGL x64. Vulkan restored cleanly but its behavior case failed later.',
        [app('InstallerEngine'), app('PackagedPhoenixPlan')], [test('PhoenixInstaller'), test('PackagedPhoenix')], 'P1')
    feature('Installation', 'Backups, recovery and relocated journals',
        'Durable pending journals, whole-plan restore validation, exact original bytes and bounded drive-relocation adoption.',
        'Unit + normal live restore', 'Injected failures are covered; real power-loss/process-kill recovery, security-software intervention and disk exhaustion are not comprehensively exercised live.',
        [app('InstallerEngine')], [test('PhoenixInstaller'), test('WorkflowSafety')], 'P1')
    feature('Installation', 'Settings across route changes and reinstall',
        'Separate backend profiles retain allowlisted configuration bytes without replacing original-file backups.',
        'Unit + recent live restart', 'Recent save/restart and same-route reinstall passed four APIs; full cross-renderer roundtrips and interrupted switches need broader live coverage.',
        [app('BackendProfileStore'), app('InstallerEngine')], [test('BackendProfileStore'), test('GameRouteOverride'), test('PhoenixInstaller')])
    feature('Installation', 'Preflight, anti-cheat and mod conflicts',
        'Checks running processes, locks, permissions, unsafe paths, known anti-cheat indicators and unowned hooks; preserves unknown mods.',
        'Unit/UI', 'Indicators and acknowledgement are not anti-cheat safety guarantees. Real protected titles and arbitrary mod managers remain outside certification.',
        [app('InstallPreflight'), app('CliInstaller')], [test('InstallPreflight'), test('CliSafety'), test('WorkflowSafety')])
    feature('Installation', 'Custom add-on library',
        'Imports architecture-checked custom addons, enables/disables by rename, and removes them from the local library.',
        'Unit/UI', 'Synthetic toggling is tested; compatibility and combined runtime behavior of arbitrary third-party addons are untested.',
        [app('MainForm.Commands'), app('PayloadScanner')], [test('ButtonBehavior'), test('PhoenixInstaller')])
    feature('Components', 'Component import, download and release selection',
        'Latest/preview or stable channel, exact asset selection, digests/notices, cache reuse, cancellation and preservation of explicit development imports.',
        'Unit + local import evidence', 'HTTP/archives are heavily simulated. An installed local development candidate does not qualify a current public release or fresh-user online installation.',
        [app('PhoenixReleaseDownloader'), app('PhoenixReleaseCatalog'), app('PayloadBootstrapper')], [test('PhoenixDownload'), test('ComponentCli')], 'P1')
    feature('Components', 'ABI, embedded runtime and x86 companion validation',
        'Validates PE exports, embedded resource completeness, architecture and matching 32-bit companion before replacing a working addon.',
        'Unit + recent live fixtures', 'Static compatibility is not GPU readiness. Optional native x86 runtimes are supplemental in the testing sweep, not normal installer deployment proof.',
        [app('PayloadScanner'), app('PhoenixCompanion32'), app('PackagedPhoenixPlan')], [test('PackagedPhoenix'), test('PhoenixDownload')])
    feature('Components', 'Model and NGX imports',
        'Explicit external DLL import with hash/provenance; distinguishes model-only enhancement from optional NVIDIA reference/upscaler runtimes.',
        'Unit + recent DLL inference', 'One selected model hash is exercised. Model-folder loading, incompatible versions, active model replacement and corruption need a dedicated live matrix.',
        [app('MainForm.Components'), app('PhoenixConfiguration')], [test('PhoenixDownload'), test('PhoenixInstaller')])
    feature('Components', 'Installed component inspection',
        'Bounded file/version/hash comparison with current/different/missing/unreadable states and installed/payload architecture separation.',
        'Unit/UI', 'File equality does not prove that the game loaded that file; every inspection state has not been checked against a live loader.',
        [app('ComponentInspection'), app('MainForm.Inspection')], [test('ComponentInspection')], 'P3')
    feature('Diagnostics', 'History, runtime diagnostics and support export',
        'Install activity, bounded log tails, missing-import diagnostics, copy actions and user-reviewed redacted support export.',
        'Unit/UI', 'Redaction is best effort, not a privacy proof for every log format. Actual clipboard/system-dialog and all crash-report variants are not fully live-tested.',
        [app('SupportReport'), app('RuntimeDiagnostics'), app('InstallHistory'), app('RendererDiagnostics')], [test('SupportReport'), test('LibraryServices'), test('MissingDependencyDiagnostics')])
    feature('Diagnostics', 'Automatic install-and-verify and CLI',
        'Inspection, component import/update, install/restore, serial library verification and authenticated live controls; bounded renderer retries.',
        'Unit + recent live fixtures', 'Launch/frame evidence is not gameplay acceptance. The latest 287 tests are focused; no fresh complete application-suite receipt was produced by this audit.',
        [app('Program'), app('LibraryVerification'), app('SmokeTestRunner'), app('PhoenixControlCli')], [test('LibraryVerification'), test('SmokeTest'), test('PhoenixControlCli')], 'P1')

    feature('Graphics routes', 'D3D12 native game-DLSS input',
        'Can use real x64 game NGX SR/RR input; verification can retry backbuffer input when native input never arrives.',
        'Unit + older runtime evidence', 'The new synthetic D3D12 test uses backbuffer input, not a retail engine native NGX call sequence. Current native-game temporal/parity acceptance remains open.',
        [app('Models'), app('SmokeTestRunner.Bridge'), app('VerifiedRendererProfiles')], [test('PhoenixInstaller'), test('PhoenixBridgeEvidence')], 'P1')
    feature('Graphics routes', 'D3D12 same-device backbuffer processing',
        'Packaged Phoenix processes the game backbuffer with local native RTX execution when supported.',
        'Recent live fixtures', 'D3D12 x64 passed controls, motion, resize and lifecycle tests. This is not all-device, native-guide, HDR, real-game or speed certification.',
        [app('PackagedPhoenixPlan'), app('PhoenixBridgeEvidence')], [test('PackagedPhoenix'), test('PhoenixTransitionEvidence')])
    feature('Graphics routes', 'D3D11 / DXGI',
        'Matching addon with shared textures/fences and CPU-transfer fallback; full network can execute in its private x64 helper.',
        'Recent live + frozen fixtures', 'Shared/fallback routes each need explicit dynamic stress and transport latency tests; x86 live lifecycle is less complete than x64.',
        [app('Models'), app('PackagedPhoenixPlan')], [test('PackagedPhoenix'), test('PhoenixInstaller')])
    feature('Graphics routes', 'D3D10 and D3D10.1',
        'Distinguishes concrete D3D10 entry points and installs the DXGI hook with the packaged bridge.',
        'Frozen fixtures', 'Frozen hardware fixtures passed; the recent moving/control/resize lifecycle gate does not exercise these APIs. WARP variants failed endpoint loading.',
        [app('Models'), app('GameScanner')], [test('RendererDetection'), test('ScannerExecutableRole')])
    feature('Graphics routes', 'D3D9 and MSAA',
        'Native x86/x64 D3D9 input, compatible upload/composition, and optional wrapper route.',
        'Recent live + older MSAA evidence', 'D3D9 x86 behavior passed; MSAA has older synthetic evidence. x64 dynamic lifecycle, all sample formats and real-game HUD/MSAA combinations remain gaps.',
        [app('Models'), app('PackagedPhoenixPlan')], [test('PhoenixInstaller'), test('GameRouteOverride')])
    feature('Graphics routes', 'DirectDraw / D3D1-8 / Glide translation',
        'x86 legacy APIs use separately supplied dgVoodoo2 or d3d8to9 before the packaged addon.',
        'Unit + frozen fixtures', 'Frozen x86 coverage exists. x64 legacy DirectDraw/D3D8 is unsupported; menus, fullscreen switching and moving real content are not broadly accepted.',
        [app('Models'), app('PackagedPhoenixPlan')], [test('RendererDetection'), test('PackagedPhoenix')])
    feature('Graphics routes', 'OpenGL',
        'x86/x64 OpenGL proxy and readback/upload path; startup may create the Phoenix endpoint on first swap.',
        'Recent live + frozen fixtures', 'OpenGL x64 lifecycle passed. Core/compatibility context requirements, x86 live lifecycle and exact-version applications need additional coverage.',
        [app('Models'), app('PackagedPhoenixPlan')], [test('ScannerExecutableRole'), test('PackagedPhoenix')])
    feature('Graphics routes', 'Vulkan and per-user layer ownership',
        'x86/x64 architecture-matched ReShade layer, launch environment, reference accounting, restoration and packaged readback path.',
        'Known failure', 'Latest Vulkan x86 run timed out during live controls after visible appearance changes; an earlier complete pass makes this intermittent, not resolved. x64 dynamic behavior also needs coverage.',
        [app('VulkanLayerInstaller'), app('InstallerEngine'), app('MainForm.Vulkan')], [test('VulkanLayerInstaller'), test('GameLauncher')], 'P1')
    feature('Graphics routes', 'Existing wrappers: ANGLE, DXVK, D3D11-on-12',
        'Detects wrapper outputs without discarding original translator/input requirements; uses applicable DXGI/Vulkan hooks.',
        'Unit + frozen fixtures', 'Frozen wrapper coverage is broader than the dynamic gate. Unrecognized wrapper chains and software D3D11-on-12 failures remain unqualified.',
        [app('GameScanner'), app('PackagedPhoenixPlan'), app('VerifiedRendererProfiles')], [test('ScannerExecutableRole'), test('PackagedPhoenix')])
    feature('Execution', 'Default x86 addon with private x64 helper',
        '32-bit frontend delegates fast native inference to an owned 64-bit helper; game architecture and inference backend are separate.',
        'Recent live + frozen fixtures', 'D3D9 x86 passed; Vulkan x86 controls are intermittent. Helper startup/cancellation, memory pressure and all x86 renderers need longer stress.',
        [app('PhoenixCompanion32'), app('PackagedPhoenixPlan')], [test('PackagedPhoenix'), test('PhoenixBridgeEvidence')])
    feature('Execution', 'Experimental x64 library inside x86',
        'Testing sweep explicitly deploys optional runtime files and requires the actual in-process execution identity.',
        'Known failure', 'Two non-WARP combinations failed with bad allocation: Phoenix/dgVoodoo D3D8 and NVIDIA/dgVoodoo D3D9. Dynamic controls, address-space pressure and normal packaging remain gaps.',
        ['../dlss5-everything-testing/tools/run_execution_sweep.py', app('PhoenixBridgeEvidence')], [test('PhoenixTransitionEvidence')], 'P1')
    feature('Execution', 'Experimental x86 native driver route',
        'Phoenix-only execution alternative with explicit no-fallback identity checks in the sweep.',
        'Frozen fixtures', '44/49 Phoenix frozen cases passed; software cases failed. NVIDIA has no x86 graph host: its 49 entries are intentional N/A. No recent dynamic route-specific gate.',
        ['../dlss5-everything-testing/tools/run_execution_sweep.py', app('PhoenixBridgeEvidence')], [test('PhoenixTransitionEvidence')])
    feature('Execution', 'NVIDIA DLL reference comparison',
        'NVIDIA NR selection is independent of Phoenix execution-device selection; comparison requires the separate runtime/model inputs.',
        'Recent live + frozen fixtures', 'Switching and delivery work on four recent APIs. This is not quality equivalence, full parameter ablation, stereo NVIDIA reference support or visible-game performance parity.',
        [app('PhoenixConfiguration'), app('PhoenixControlClient')], [test('PhoenixInstaller'), test('PhoenixControl')])
    feature('Execution', 'Portable CUDA, Vulkan, HIP, CPU and SYCL runners',
        'App configuration accepts supported concrete provider/device IDs and forwards worker-engine selections to Phoenix.',
        'Unit/configuration', 'No equivalent current Everything live matrix for these inference runners. Graphics Vulkan tests used native RTX inference, not the portable Vulkan inference runner. AMD/Intel hardware remains unqualified.',
        [app('PhoenixConfiguration'), app('PhoenixControlClient')], [test('PhoenixInstaller'), test('PhoenixControl')])
    feature('Execution', 'GPU selection and CPU thread count',
        'Live device list and stable device IDs; CPU thread count supports 0=automatic and explicit values.',
        'Unit/configuration', 'No physical multi-GPU selection/transfer test, GPU removal/switch test, or CPU-thread scaling/affinity and memory benchmark through this app.',
        [app('PhoenixLiveForm'), app('PhoenixControlClient')], [test('PhoenixControl')])

    feature('Controls', 'Authenticated live and safe offline settings',
        '67 typed core controls, staged Apply, observed process/session checks, bounded serialized mailbox and atomic offline conflict handling.',
        'Recent live + unit/UI', 'Key-name synchronization is checked; it does not prove every control has the intended effect. Many GUI tests intercept system boundaries rather than real dialogs.',
        [app('PhoenixControlClient'), app('PhoenixLiveForm'), app('PhoenixControlCli')], [test('PhoenixControl'), test('PhoenixControlCli')])
    feature('Controls', 'Tone, structure, skin and mask controls',
        'Overrides basic strengths and advanced skin/mask settings; tests compare actual displayed central pixels.',
        'Recent live; Vulkan intermittent', 'Four APIs passed low/high and isolated-skin checks. Segmentation accuracy, flicker, human quality judgment and face/content diversity remain untested.',
        [app('PhoenixControlClient'), app('PhoenixLiveForm')], [test('PhoenixControl'), '../dlss5-everything-testing/tools/run_live_regressions.py'], 'P1')
    feature('Controls', 'Comparison modes and sources',
        'Three-way, pair wipe, absolute/signed RGB, heatmap and luminance views, source selection, gain, divider, labels and lines.',
        'Partial live + unit/UI', 'Recent source/backend switching and pair-wipe delivery are covered; no pixel oracle for every mode, source order, divider, gain and label combination.',
        [app('PhoenixControlClient'), app('PhoenixLiveForm')], [test('Layout'), test('PhoenixControl')])
    feature('Controls', 'Model folder selection and validation',
        'Supports configured source, external NVIDIA DLL or compatible model folder, plus a Validate model command.',
        'Unit + DLL-only recent live', 'No representative valid custom-model folder, invalid-tensor matrix or active reload test. Path editors in Everything Controls are text boxes; the separate Components DLL import has browsing.',
        [app('PhoenixConfiguration'), app('PhoenixLiveForm')], [test('PhoenixInstaller'), test('PhoenixControl')])
    feature('Controls', 'Super resolution, aspect and upscaler selection',
        'Exposes normal/SR, quality scales, game/preset/custom aspect and spatial/NVIDIA DLSS SR/Intel XeSS choices to the addon.',
        'Unit/configuration', 'Recent live gate uses normal 1:1. No full end-to-end spatial/DLSS SR/XeSS quality, aspect, HUD or missing-runtime matrix on the current app.',
        [app('PhoenixControlClient'), app('PhoenixConfiguration')], [test('PhoenixInstaller'), test('PhoenixControl')], 'P1')
    feature('Controls', 'Adaptive network budget and per-layer workload',
        'Experimental block count, minimum blocks, FPS or GPU-time budget, refinement passes and per-block repeats.',
        'Partial live + unit/configuration', 'Recent gate recovers from reduced depth to full depth. Automatic budget convergence, repeat combinations, FPS oscillation, image quality and savings are not validated.',
        [app('PhoenixControlClient')], [test('PhoenixControl'), '../dlss5-everything-testing/tools/run_live_regressions.py'])
    feature('Controls', 'History, guides and NVIDIA-specific tuning',
        'History reset/interval controls, motion/depth interpretation, unjitter and NVIDIA-only exposure/style/HDR/intensity options.',
        'Partial live + unit/configuration', 'Reset command is exercised; periodic history behavior, real engine motion/depth, all NVIDIA options and game HDR color correctness remain gaps.',
        [app('PhoenixControlClient'), app('PhoenixLiveForm')], [test('PhoenixControl')])
    feature('Controls', 'Screenshots, capture, timings and diagnostic controls',
        'Presented screenshots, bounded input capture, model/runtime snapshots, log level and reset actions.',
        'Recent screenshots + unit', 'Presented PNGs passed. Capture bursts, log/reload effects, timing-history persistence and isolated GPU/transport/VRAM accounting need dedicated tests.',
        [app('PhoenixLiveForm'), app('PhoenixControlClient')], [test('PhoenixControl'), test('PhoenixBridgeEvidence')])
    feature('Controls', 'Moving-frame composition and recovery',
        'Compositor resource-lineage telemetry, current settings revisions, minimize/restore and actual D3D11/D3D12 resizing.',
        'Recent live fixtures', 'Resource matching is not a perceptual temporal oracle. Dynamic resize is missing from the other fixture APIs; fullscreen/Alt-Tab/device-loss and hour-long stress remain open.',
        ['../dlss5-everything-testing/tools/live_assertions.py', '../dlss5-everything-testing/tools/run_live_regressions.py'], [test('PhoenixTransitionEvidence')], 'P1')

    feature('DOS and VR', 'DOSBox Enhanced integration',
        'Pinned submodule, isolated profiles, direct native Phoenix library, mapped strengths/comparison/passes, no ReShade route.',
        'Unit + recent VGA fixture', 'Three DOS rows passed, including positive renderer counters and changed output. One 320x200 fixture is not broad DOS gameplay/input/audio/save acceptance; quick launch requires ASCII paths and DOS 8.3 names.',
        [app('DosGameLauncher'), 'scripts/Build-DosboxEnhanced.ps1', 'docs/dosbox-enhanced-integration.md'], [test('DosEnhancedDiscovery'), test('PhoenixInstaller')])
    feature('DOS and VR', 'OpenVR integration',
        'x64 forwarding shim preserves the existing ABI library and routes frames through the Phoenix host.',
        'Unit/build + older probes', 'No current physical-headset gameplay qualification. Headset runtime/ABI combinations, eye order, pose, latency and overlay comfort remain untested.',
        ['source/openvr-shim/openvr_api_proxy.cpp', app('InstallerEngine')], [test('PhoenixInstaller'), test('GameLauncher')], 'P1')
    feature('DOS and VR', 'OpenXR and stereo histories',
        'x64 D3D11 OpenXR layer, eye submissions and separate histories; synthetic host stereo scheduling has older evidence.',
        'Unit/build + older synthetic stereo', 'No PSVR2/other-headset acceptance, no all-runtime/device coverage, and no stereo NVIDIA-reference path qualification. Other graphics bindings and x86 VR are not general supported routes.',
        ['source/openxr-layer/openxr_layer.cpp', 'tests/openxr-profile-tests.cpp', 'docs/phoenix-validation.md'], [test('PhoenixInstaller'), test('GameLauncher')], 'P1')

    feature('Screen Studio', 'Window capture and live output',
        'Independent Windows Graphics Capture, GPU shared-texture processing and presenter; enable/disable, switch target and foreground/minimize handling.',
        'Historical synthetic GPU pass', 'Controlled-window tests passed previously, including actual view clicks. Re-run against the current app/worker/addon; exclusive fullscreen, DRM and arbitrary application behavior are not certified.',
        [app('ScreenStudioSession'), app('ScreenCaptureSources'), 'source/screen/screen-worker.cpp'], [test('ScreenStudioLive'), test('ScreenStudio')])
    feature('Screen Studio', 'Entire-display capture and feedback exclusion',
        'Selectable single-monitor capture with output-window capture exclusion; capture starts explicitly.',
        'Not accepted', 'Whole-display test was interrupted and did not pass. Overlay recapture/feedback exclusion needs an idle synthetic desktop test before this route is trusted.',
        ['source/screen/screen-worker.cpp', 'docs/screen-studio.md'], [test('ScreenStudioLive')], 'P1')
    feature('Screen Studio', 'Scale, controls, camera treatment and presets',
        'Native 1:1 or explicit reduced scale/residual detail, 1..10 evaluations, wipe/difference, lens/FOV/vignette/aberration/noise, presets and hotkeys.',
        'Historical synthetic GPU + unit/UI', 'Strength/resize evidence exists; not every camera effect, hotkey conflict, preset combination or temporal/performance cost is live-tested.',
        [app('ScreenStudioSettings'), app('ScreenStudioView')], [test('ScreenStudio'), test('ScreenStudioLive')])
    feature('Screen Studio', 'Paired screenshots and recovery',
        'Normal/enhanced/displayed-view PNG or JPEG with explicit folders and recovery of completed unexported captures.',
        'Historical synthetic GPU + unit', 'No exhaustive disk-full, inaccessible export/recovery, color-profile, JPEG or concurrent screenshot acceptance matrix.',
        [app('ScreenStudioSession'), app('ScreenStudioSettings')], [test('ScreenStudio'), test('ScreenMedia')])
    feature('Screen Studio', 'Spout sharing',
        'GPU sender for SDR or float composition with a compiled SDK; independent of screenshot capture.',
        'Historical synthetic GPU pass', 'Synthetic receiver recovered matching pixels; real OBS/receiver integration, device changes and different-adapter behavior are not qualified.',
        ['source/screen/screen-worker.cpp'], ['tests/screen-media-gpu-tests.cpp', test('ScreenMedia')])
    feature('Screen Studio', 'HDR transport',
        'Experimental RGBA16F scRGB capture/presentation with SDR-conditioned model residual and configurable paper white.',
        'Historical synthetic float pass', 'Not native HDR inference. No physical HDR-display color accuracy/peak/black-level acceptance. Screenshots and H.264 outputs remain SDR.',
        ['source/screen/screen-worker.cpp', app('ScreenStudioSettings')], [test('ScreenStudioLive'), 'tests/screen-contract-tests.cpp'], 'P1')
    feature('Screen Studio', 'Image and video file conversion',
        'Input-preserving SDR image export and bounded FFmpeg video jobs, CPU H.264 or NVENC, optional first audio stream and cancellation.',
        'Historical small synthetic GPU pass', 'Odd-size/native-size image and three-frame video evidence exists. Long jobs, VFR quality, every decoder/encoder, AV sync and cancellation under GPU stalls remain gaps; HDR video is rejected.',
        [app('ScreenMediaConverter'), app('ScreenMediaConversionForm')], [test('ScreenStudioLive'), test('ScreenMedia')])

    feature('Streamer and Android', 'Authenticated LAN frame server',
        'Explicit private IPv4 listener, TLS pin/token pairing, one client/session and sequential raw/v2 compressed frames with bounded settings and cleanup.',
        'Unit/TLS + documented device tests', 'No broad security certification, hostile upstream-codec fuzz campaign, IPv6/public service, adaptive congestion control or long network-fault/throughput acceptance.',
        [app('LanFrameServer'), app('LanFrameProtocol'), app('LanPhoenixBackend')], [test('LanFrame')])
    feature('Streamer and Android', 'Streamer page and pairing',
        'Select address/port, start/stop, export private pairing details, expose active session status. Android client distribution is separate.',
        'Unit/UI', 'No current full click-through pairing/export acceptance across firewalls/multiple NICs; README page name and validation text lag implementation.',
        [app('LanFrameView'), app('StreamerPairing'), app('MainForm.Shell')], [test('StreamerIntegration')])
    feature('Streamer and Android', 'Raw and Windows Pyrowave codecs',
        'Raw v1/v2 RGBA and bidirectional GPU Pyrowave with CPU color conversion/upload/readback, explicit codec negotiation and no silent fallback.',
        'Unit + documented device tests', 'Neither route is zero-copy. Sustained native-resolution bandwidth/latency and compression/quality on real moving content are not established.',
        [app('PyrowaveFrameCodec'), app('LanFrameProtocol'), 'docs/lan-frame-service.md'], [test('LanFrame')])
    feature('App experience', 'Themes, navigation, layouts and accessibility',
        'Light/dark/system themes, offscreen-rendered pages and dialogs, keyboard names/tooltips, busy gates, command palette, recents and optional tray lifetime.',
        'Unit/UI renders', 'Mixed-DPI monitors, 200% scaling, high contrast, screen readers, DWM titlebars, every native dialog and long interactive usage remain gaps.',
        [app('MainForm.Shell'), app('UiTheme'), app('MainForm.Accessibility'), app('MainForm.Convenience')], [test('DarkTheme'), test('Layout'), test('ButtonBehavior'), test('Convenience')], 'P3')
    feature('App experience', 'Localization',
        '38-language common-action catalog with explicit English fallback and RTL metadata.',
        'Unit/UI', 'Translations are partial; Screen Studio labels are English. No complete native-speaker, RTL, clipping or every-page localization acceptance.',
        [app('UiLocalization')], [test('Convenience')], 'P3')
    feature('App experience', 'App update checks',
        'Explicit bounded release check against this application repository and a release link; no automatic self-replacement.',
        'Unit/mocked HTTP', 'No current online release/fresh-install verification in this audit. Self-updating installation is not implemented.',
        [app('AppUpdateService')], [test('LibraryServices')], 'P3')
    feature('Distribution', 'Build, native contracts and packaging',
        'Windows .NET/native CI, host/IPC/VR/screen contract tests, self-contained publish and distribution-notice checks.',
        'Historical full suite + current focused gate', 'Most GPU/capture/hardware tests are opt-in and not ordinary CI. Latest retained full receipt is 955 passed/7 skipped (Oct 16); Oct 18 ran 287 focused app tests, not a new full suite. Qualify the exact release package separately.',
        ['.github/workflows/tests.yml', 'app/Dlss5CompatApp/Dlss5CompatApp.csproj', 'source/README.md'], ['tests/Copy-DistributionNotices.Smoke.ps1', 'tests/phoenix-host-config-tests.cpp', 'tests/phoenix-host-feeder-tests.cpp'], 'P1')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def refresh_acceptance_descriptions():
    """Replace earlier source-review coverage captions with current receipt scope."""
    if not (ROOT/'local-build/validation-current/managed-recheck.trx').is_file():
        return
    updates = {
        'Install, update, remove and reinstall': ('Current released-addon fixtures',
            'Five observable lifecycles passed, including Vulkan x86. Vulkan throughput remains very poor; actual process termination exposed an orphaned atomic-copy model temporary file after otherwise successful recovery.'),
        'Backups, recovery and relocated journals': ('Unit + actual interrupted installer',
            'Killing an owned installer during model copying preserved originals after recovery, but left a 165,840,496-byte .tmp file. Actual power-loss, security software and disk-full recovery remain unqualified.'),
        'Settings across route changes and reinstall': ('Unit + five released-addon lifecycles',
            'Save/restart and same-route reinstall passed five APIs. Full cross-renderer roundtrips and interrupted switches remain incomplete.'),
        'Component import, download and release selection': ('Unit + real fresh online bootstrap',
            'Empty-payload online setup downloaded the published 0.81 addon, matching companion, notices and dependencies successfully. Full first-user GUI flow and real interrupted/corrupted network transfers remain unqualified.'),
        'Automatic install-and-verify and CLI': ('Full managed suite + current release fixtures',
            '1,023 managed tests passed on the full rerun; one initial intermittent test-server publication failure is retained. Launch/frame evidence is not broad gameplay acceptance.'),
        'D3D10 and D3D10.1': ('Current released-addon graphics matrix',
            'Both bitnesses passed base, motion and controls checks. This is not a full installer/lifecycle, WARP, fullscreen or device-loss qualification for these APIs.'),
        'OpenGL': ('Current release matrix + x64 lifecycle',
            'Both bitnesses passed base/motion/control checks; multi-window checks passed. x64 lifecycle passed. Exact context/version applications and broad x86 lifecycle remain open.'),
        'Vulkan and per-user layer ownership': ('Current release matrix + x86 lifecycle; poor throughput',
            'The current x86 lifecycle passed observable controls/restart checks, but delivered about 0.24 fresh outputs/s at 1080p. Frame-ID lineage is absent in release 0.81. Broader x64 lifecycle and latency qualification remain open.'),
        'Default x86 addon with private x64 helper': ('Current released-addon matrix and lifecycles',
            'All seven graphics APIs passed bounded x86 checks. D3D9/Vulkan x86 completed observable lifecycle checks; Vulkan throughput is unusably low in this test. Longer memory/latency stress remains open.'),
        'Portable CUDA, Vulkan, HIP, CPU and SYCL runners': ('Configuration + current device-selection attempts',
            'See the full acceptance matrix for actual release-backed device results. CPU one-thread and 32-thread requests hit helper processing timeouts. Graphics Vulkan passes do not establish Vulkan neural-runner acceptance.'),
        'GPU selection and CPU thread count': ('Current advertised-device attempts + unit contracts',
            'Actual CPU/native/CUDA/Vulkan/SYCL selections are recorded in the acceptance matrix. This is not a scaling or multi-GPU game/inference benchmark; dedicated AMD/Intel cards and device removal remain unqualified.'),
        'Tone, structure, skin and mask controls': ('Five current released-addon lifecycle fixtures',
            'Low/high and skin pixel checks passed the five selected routes. Segmentation accuracy, flicker, human quality judgment and diverse faces/content remain untested.'),
        'Super resolution, aspect and upscaler selection': ('18 current spatial combinations + vendor fallback checks',
            'Three qualities by six aspect choices rendered at 640x480. NVIDIA SR/XeSS both reported spatial fallback without observed depth/motion, even with runtime DLLs supplied; actual temporal execution is unqualified.'),
        'Adaptive network budget and per-layer workload': ('Current manual-depth and automatic GPU-budget checks',
            'Manual depth/repeats/refinement rendered; tight GPU budget selected two blocks and generous budget restored eight. Presentation-FPS mode is normalized to GPU-time mode on direct feeds. Long-run convergence, quality and savings remain open.'),
        'Moving-frame composition and recovery': ('Current released-addon observable behavior + API pixel tests',
            'Release 0.81 exposes no frame-ID lineage. Completed output, controls and screenshot extent advanced, with separate bounded API motion-pixel checks; full temporal and internal-resize lineage remain unverified.'),
        'Window capture and live output': ('Current released-addon window tests',
            'Controlled-window tests passed, including target switching after fixing test-window positioning. Exclusive fullscreen, protected applications and arbitrary app behavior remain unqualified.'),
        'Entire-display capture and feedback exclusion': ('Current failure / not accepted',
            'Whole-display input contained unexpected pixels. Occlusion versus output recapture was not established. Feedback exclusion still needs a controlled desktop acceptance pass.'),
        'Scale, controls, camera treatment and presets': ('Current synthetic GPU + unit/UI',
            'Strength/scale/resize cases passed. Not every camera effect, hotkey, preset combination or temporal/performance cost has a live oracle.'),
        'Paired screenshots and recovery': ('Current synthetic GPU + unit',
            'Paired screenshots passed. Disk-full, inaccessible export/recovery, color-profile, JPEG and concurrent screenshot matrices remain incomplete.'),
        'Spout sharing': ('Current GPU pixel roundtrip',
            'Synthetic sender/receiver recovered matching pixels. Real OBS, device changes and different-adapter behavior remain unqualified.'),
        'HDR transport': ('Current synthetic float transport',
            'Float transport passed; physical HDR color/peak/black-level accuracy and native HDR inference are not established. Screenshots/H.264 remain SDR.'),
        'Image and video file conversion': ('Current short synthetic GPU jobs + unit',
            'Image and short video conversion passed. Long/VFR jobs, every codec, AV sync and cancellation under GPU stalls remain open. Live recording/system-audio capture are removed.'),
        'Android physical codec / PC inference roundtrip': ('Current emulator neural pass; codec unqualified',
            'The released-addon neural roundtrip passed. Software codec Vulkan creation failed, and a host-backed retry froze the owned emulator. Historical one-device evidence is not a fresh physical-phone or sustained-capture qualification.'),
        'Build, native contracts and packaging': ('Current full suite and self-contained publish',
            '1,023 managed tests passed on rerun, native contracts passed, and a self-contained publish exported 67 controls without forbidden external payloads. Initial intermittent failure is retained. Remote CI and a final distribution release were not performed.'),
    }
    for item in FEATURES:
        if item['title'] in updates:
            item['coverage'], item['gaps'] = updates[item['title']]
    fixes_path=ROOT/'local-build/validation-current/FIXES.json'
    if fixes_path.is_file():
        fixes=load(fixes_path)
        states={item['name']:item['passed'] for item in fixes['items']}
        revised={}
        if states.get('Interrupted installation'):
            revised['Backups, recovery and relocated journals']=('Unit + forced-termination recovery passed',
                'Atomic-copy temporary files are journaled before install/restore copies. Actual killed-installer recovery preserved originals and left no orphaned temporary files. Power loss, antivirus and broad timing sweeps remain unqualified.')
            revised['Install, update, remove and reinstall']=('Live lifecycles + interruption recovery',
                'Five original release-backed observable lifecycles passed. The local atomic-copy journal fix passed forced-termination recovery without orphaned files. See FIXES.html for the separate current Vulkan transport result. Broad power-loss and security-software intervention remain unqualified.')
        if states.get('Alternate runner delivery'):
            text='Seven runner/thread selections delivered current presented outputs: native, CPU one/all/auto threads, CUDA and both Vulkan devices. CPU is a slow reference path. Missing Intel CPU ICD is no longer advertised as working SYCL. Dedicated AMD/Intel cards, HIP, removal and separate game/inference GPUs remain unqualified.'
            for title in ('Portable CUDA, Vulkan, HIP, CPU and SYCL runners','GPU selection and CPU thread count'):revised[title]=('Fixed package: live delivery accepted',text)
        if states.get('Vulkan x86 transport'):
            for title in ('Vulkan and per-user layer ownership','Default x86 addon with private x64 helper'):
                revised[title]=('Fixed package: 1080p transport and pixel checks',
                    'GPU-upload read-modify-write was removed. The current measured rate is in FIXES.html; the original public 0.81 remains slow. Synthetic acceptance does not qualify every retail title, latency scenario or long session.')
        if states.get('Truthful controls'):
            revised['Super resolution, aspect and upscaler selection']=('18 spatial combinations + explicit availability checks',
                'Spatial quality/aspect combinations passed. Color-only capture now explicitly refuses vendor temporal upscalers instead of presenting a spatial fallback as DLSS SR/XeSS. Real engine-guided temporal upscaling and perceptual fidelity remain unqualified.')
            revised['Adaptive network budget and per-layer workload']=('Live manual-depth and GPU-budget checks',
                'Depth, repeats, skips and refinement passed. GPU budgeting responds to tight/generous targets. Hidden-helper capture explicitly disallows presentation-FPS budgeting. Long-run convergence, quality and savings remain open.')
        if states.get('Whole-display feedback'):
            revised['Entire-display capture and feedback exclusion']=('Whole-monitor generated-source test passed',
                'The worker verifies output exclusion. Two full monitored input samples match generated pixels while a distinct output overlay is displayed. Transient startup notifications must settle; exclusive fullscreen, protected content and physical HDR remain unqualified.')
        for item in FEATURES:
            if item['title'] in revised:item['coverage'],item['gaps']=revised[item['title']]
        managed=fixes.get('managed_tests') or {}
        if managed.get('failed')=='0':
            for item in FEATURES:
                if item['title'] in ('Automatic install-and-verify and CLI','Build, native contracts and packaging'):
                    item['coverage']='Current full managed suite and explicit live receipts'
                    item['gaps']=f'{managed.get("passed")} current managed tests passed. Native contracts and local integration builds passed; opt-in cases are documented separately in FIXES.html. Remote CI, a final published distribution and broad retail-game acceptance remain unqualified.'


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def controls():
    path = ROOT / app('PhoenixControlClient')
    text = path.read_text(encoding='utf-8-sig')
    rows = []
    pattern = r'\b([BNCS])\(PhoenixSetting\.(\w+),\s*"([^"]+)",\s*"([^"]+)"([^;]*?)\);'
    # Only labels/groups are extracted. This intentionally does not infer runtime semantics.
    for match in re.finditer(pattern, text):
        kind, key, label, group, arguments = match.groups()
        rows.append(dict(key=key, label=label, group=group, kind=kind,
                         definition=arguments.lstrip(', ').strip(),
                         source_line=text.count('\n', 0, match.start())+1))
    rows.append(dict(key='NrFeature', label='NR feature', group='NVIDIA DLSS5', kind='C',
        definition='13 (legacy RR), 18 (NR)', source_line=text[:text.index('list.Add(new(PhoenixSetting.NrFeature')].count('\n')+1))
    line=text[:text.index('N((PhoenixSetting)((int)PhoenixSetting.Block1Repeats')].count('\n')+1
    for index in range(1,9):
        rows.append(dict(key=f'Block{index}Repeats', label=f'Block {index} repeats', group='History / Budget',
                         kind='N', definition='integer 0..4', source_line=line))
    enum = text.split('internal enum PhoenixSetting',1)[1].split('}',1)[0]
    expected = set(re.findall(r'\b[A-Z][A-Za-z0-9]+\b', enum))
    if {r['key'] for r in rows} != expected or len(rows) != len(expected):
        raise ValueError('Control parser no longer matches the enum; review the inventory extractor.')
    live_keys = {'Backend','OverrideStrengths','Tone','Structure','Advanced','AutoMask','Skin',
                 'Comparison','SourceA','SourceB','Experimental','TransformerBlocks','MinimumBlocks'}
    for row in rows:
        row['live_check'] = ('Explicitly changed/read back by the recent gate; only tone/structure/skin have specific pixel-effect checks.'
                            if row['key'] in live_keys else
                            'Name contract checked; not individually changed and effect-verified by the recent gate.')
    return sorted(rows, key=lambda x:(x['group'], x['source_line'], x['key']))


def evidence():
    recent=load(TESTING/'runs/latest-live-regressions/report.json')
    broad=load(TESTING/'runs/latest-x64-backends/execution-comparison.json')
    sweep=load(TESTING/'runs/latest-x64-backends/execution-sweep.json')
    receipts=[]
    paths=[ROOT/'.local-validation/games-20261015/test-results/full-dosbox-enhanced-integration.trx',
           ROOT/'.local-validation/screen-test-results/screen-switch-presentation-final.trx',
           ROOT/'.local-validation/screen-test-results/compatibility-dynamic-live.trx',
           ROOT/'.local-validation/screen-test-results/screen-media-reviewed-live.trx',
           ROOT/'.local-validation/screen-test-results/screen-media-hdr-final.trx']
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    for path in paths:
        tree=ET.parse(path);counts=tree.find('.//t:Counters',ns);times=tree.find('t:Times',ns)
        results=tree.findall('.//t:UnitTestResult',ns)
        receipts.append(dict(path=path.relative_to(ROOT).as_posix(), sha256=digest(path),
            counts=counts.attrib, finished=times.attrib['finish'],
            skipped=[r.attrib['testName'] for r in results if r.attrib.get('outcome')=='NotExecuted']))
    failed=[r for r in broad['failures'] if r['outcome']=='failed']
    groups=Counter('Unsupported x64 DirectDraw' if r['id'].startswith('ddraw') else
        'Software WARP routes' if r['id'].startswith('warp_') else 'Cross-bitness allocation failure' for r in failed)
    return dict(recent=dict(path='../dlss5-everything-testing/runs/latest-live-regressions/report.json',
        sha256=digest(TESTING/'runs/latest-live-regressions/report.json'),
        cases=[{k:c.get(k) for k in ('id','passed','execution_mode','error')} for c in recent['cases']],
        hardware=recent['hardware'], control_contract=recent['control_contract'],
        addon_sha256=recent['addon_sha256'], application_assembly_sha256=recent['application_assembly_sha256'],
        scope=('Unchanged public Phoenix 0.81 addon; observable checks passed with explicit missing frame-lineage diagnostics.'
               if recent.get('prebuilt_release') else 'Development/source baseline; not a release-package qualification.')),
        broader=dict(path='../dlss5-everything-testing/runs/latest-x64-backends/execution-comparison.json',
            sha256=digest(TESTING/'runs/latest-x64-backends/execution-comparison.json'),
            completed=sweep['updated'], counts=broad['counts'], hardware=broad['hardware'],
            totals=broad['totals'], failures=failed, failure_groups=dict(groups),
            dos=[{k:r.get(k) for k in ('backend','passed','execution_mode','width','height','capture_source')} for r in broad['dos_results']],
            scope='82 Windows unenhanced baselines are included in the 348 passes; three DOS cases are separate. Frozen synthetic scenes, not all-game acceptance.'),
        historical_receipts=receipts,
        latest_focused_run=dict(application_passed=287,runner_passed=18,addon_cpu_suites=20,addon_software_d3d12_suites=2,
            origin='Observed in the immediately preceding chat task on 2026-10-18; not rerun or counted as a fresh full-suite run for this inventory.'))


def ref(path):
    target=(ROOT/path).resolve()
    if not target.is_file():
        raise ValueError('Inventory reference missing: '+path)
    # Relative HTML links avoid embedding personal absolute paths in the report.
    relative=Path('../../')/path
    return f'<a href="{escape(relative.as_posix(),quote=True)}">{escape(path)}</a>'


def render(data, output):
    rows=[]
    for row in data['features']:
        references='<details><summary>Source and tests</summary><p>'+ '<br>'.join(ref(p) for p in row['sources'])+'</p><p>'+ '<br>'.join(ref(p) for p in row['tests'])+'</p></details>'
        search=' '.join(str(row[k]) for k in ('area','title','behavior','coverage','gaps','priority')).lower()
        rows.append(f'<tr data-area="{escape(row["area"],quote=True)}" data-search="{escape(search,quote=True)}" data-priority="{row["priority"]}">'
            f'<td><b>{row["id"]} · {escape(row["title"])}</b><small>{escape(row["area"])}</small><p>{escape(row["behavior"])}</p>{references}</td>'
            f'<td>{escape(row["coverage"])}</td><td><span class="priority">{row["priority"]}</span> {escape(row["gaps"])}</td></tr>')
    recent=data['evidence']['recent']; broad=data['evidence']['broader']
    current_passes=sum(c['passed'] is True for c in recent['cases'])
    broad_counts=broad['counts']
    full_receipt=data['evidence']['historical_receipts'][0]
    full_skipped=int(full_receipt['counts']['total'])-int(full_receipt['counts']['executed'])
    current_receipt = ROOT/'local-build/validation-current/managed-recheck.trx'
    fresh_full = None
    if current_receipt.is_file():
        fresh_full = ET.parse(current_receipt).find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters').attrib
    full_card = (f'{fresh_full["passed"]} passed + {fresh_full["notExecuted"]} opt-in skipped</span>Current complete managed rerun; opt-ins were exercised separately.'
                 if fresh_full else f'{full_receipt["counts"]["passed"]} + {full_skipped} skipped</span>Historical complete managed receipt; see acceptance matrix for current execution.')
    current=''.join(f'<li><b>{escape(c["id"])}</b>: {"Pass" if c["passed"] else "FAILED — "+escape(c["error"] or "unqualified")}</li>' for c in recent['cases'])
    broad_rows=''.join('<tr>'+''.join('<td>'+escape(str(r.get(k,'')))+'</td>' for k in
        ('architecture','backend','variant','passed','failed','not_applicable'))+'</tr>' for r in broad['totals'])
    control_rows=''.join(f'<tr><td>{escape(r["key"])}</td><td>{escape(r["group"])}</td><td>{escape(r["label"])}</td><td>{escape(r["live_check"])}</td></tr>' for r in data['controls'])
    priorities=''.join(f'<li><b>{escape(p["title"])}</b> — {escape(p["action"])}</li>' for p in data['priorities'])
    missing=''.join(f'<li>{escape(s)}</li>' for s in data['not_offered'])
    drift=''.join(f'<li>{escape(s)}</li>' for s in data['documentation_gaps'])
    historical=''.join(f'<li>{ref(r["path"])}: {r["counts"]["passed"]} passed / {r["counts"]["total"]} total; completed {escape(r["finished"])}. Historical, without a current source-hash qualification.</li>' for r in data['evidence']['historical_receipts'])
    options=''.join(f'<option>{escape(area)}</option>' for area in sorted({r['area'] for r in data['features']}))
    html=f'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>DLSS5 Everything — functionality and coverage inventory</title><style>
:root{{color-scheme:dark}}body{{background:#111722;color:#eaf0f9;font:16px system-ui;margin:36px auto;max-width:1450px;padding:0 24px}}h1{{font-size:32px}}h2{{margin-top:36px}}p,li{{line-height:1.55}}p,small{{color:#bdcadd}}a{{color:#84c5ff}}.cards{{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:14px}}.card,section{{background:#1c2636;border:1px solid #35455c;border-radius:10px;padding:20px;margin:15px 0}}.number{{display:block;font-size:29px;font-weight:700}}table{{border-collapse:collapse;width:100%;font-size:14px}}th,td{{text-align:left;vertical-align:top;padding:13px;border-bottom:1px solid #35455c}}th{{background:#243248}}td:first-child{{width:46%}}small{{display:block;margin:5px 0}}summary{{color:#84c5ff;cursor:pointer}}details p{{font-size:12px;overflow-wrap:anywhere}}input,select{{font:inherit;background:#172334;color:#edf4ff;border:1px solid #52647f;border-radius:6px;padding:9px;margin:5px}}input{{min-width:280px}}.priority{{color:#ffcc8e;font-weight:bold}}.warning{{border-left:4px solid #e7a76b;padding-left:15px}}.tablewrap{{overflow-x:auto}}nav a{{margin-right:18px}}[hidden]{{display:none!important}}
</style><h1>DLSS5 Everything: functionality, tests and gaps</h1>
<p>Source-reviewed snapshot: {escape(data['generated_utc'])}. {len(data['features'])} functional areas/capabilities, plus all {len(data['controls'])} typed core controls.
This generator reads source and receipts; it does not execute tests. Current test execution is recorded in the linked acceptance matrix.</p>
<section><b>Current results for all 14 requested product areas:</b> <a href="../validation-current/feature-matrix.html">Full feature acceptance matrix</a> · <a href="../validation-current/REPORT.html">Detailed current validation report</a>. These current receipts supersede historical coverage captions below; missing diagnostics and unavailable hardware are kept explicit.</section>
<nav><a href="#inventory">Feature inventory</a><a href="#priorities">Priorities</a><a href="#evidence">Evidence</a><a href="#controls">{len(data['controls'])} controls</a><a href="#limits">Not offered</a><a href="inventory.json">Machine-readable inventory</a></nav>
<div class="cards"><div class="card"><span class="number">{current_passes} / {len(recent['cases'])}</span>Observable live behavior cases passed. Missing release diagnostics and poor Vulkan throughput remain gaps.</div><div class="card"><span class="number">{broad_counts['passed']} / {broad_counts['failed']} / {broad_counts['not_applicable']}</span>Historical frozen sweep: pass / failed / intentional N/A. Includes 82 unenhanced baselines.</div><div class="card"><span class="number">{full_card}</div><div class="card"><span class="number">{len(data['controls'])} controls</span>Names synchronized; only a subset is effect-verified. Not a code-coverage percentage.</div></div>
<p class="warning">Implemented does not mean accepted on every device. Unit/UI means synthetic files, mocked boundaries or offscreen rendering; frozen fixtures prove bounded delivery; older GPU/emulator/phone evidence is historical. Known failures and intentional exclusions are kept separate.</p>
<section id="priorities"><h2>What to address first</h2><ol>{priorities}</ol></section>
<h2 id="inventory">Functionality inventory</h2><p>Each row includes what exists, evidence strength and remaining work. P1 = high-priority reliability or acceptance work; P2 = feature/hardware coverage; P3 = broader UX/polish coverage. These are review priorities, not measured defect rates.</p>
<label>Find <input id="query" type="search" placeholder="model, audio, Vulkan, Android…"></label><label>Area <select id="area"><option value="">All areas</option>{options}</select></label><label>Priority <select id="priority"><option value="">All</option><option>P1</option><option>P2</option><option>P3</option></select></label><p id="shown"></p>
<div class="tablewrap"><table id="features"><thead><tr><th>Functionality and implementation</th><th>Evidence / coverage</th><th>Untested behavior and gaps</th></tr></thead><tbody>{''.join(rows)}</tbody></table></div>
<section id="evidence"><h2>Existing evidence, with limits</h2><p>Recent fixture hardware: {escape(recent['hardware']['gpu_name_driver_memory'])}; {recent['hardware']['logical_cpu_threads']} logical CPU threads. {escape(recent['scope'])}</p><ul>{current}</ul>
<p>{ref('../dlss5-everything-testing/runs/latest-live-regressions/REPORT.html')} · {ref(broad['path'])}</p>
<div class="tablewrap"><table><thead><tr><th>Architecture</th><th>Consumer</th><th>Execution route</th><th>Pass</th><th>Fail</th><th>N/A</th></tr></thead><tbody>{broad_rows}</tbody></table></div>
<p>Frozen sweep completed {escape(broad['completed'])}. Failure groups: {escape(str(broad['failure_groups']))}. There are three separate DOS fixture passes. Software-renderer failures do not establish failure of hardware inference, and unsupported x64 DirectDraw is a product boundary.</p>
<ul>{historical}</ul><p>The 287 application / 18 runner passes and 20 CPU / 2 software-D3D12 addon suites were observed in the preceding task. They were not re-executed for this audit. Historical totals are not summed and do not replace a fresh full-suite run on the final release artifact.</p></section>
<h2 id="controls">Core control inventory</h2><p>The recent gate checks all wire names against Phoenix. The table below deliberately distinguishes that from changing a control and proving its intended visual/runtime effect. Defaults appearing in status are not individual semantic tests.</p>
<div class="tablewrap"><table><thead><tr><th>Wire key</th><th>Group</th><th>Label</th><th>Recent live scope</th></tr></thead><tbody>{control_rows}</tbody></table></div>
<section id="limits"><h2>Not offered, rather than merely untested</h2><ul>{missing}</ul><h2>Documentation gaps</h2><ul>{drift}</ul></section>
<p>Inventory descriptions are a source review; input SHA-256 values and local receipt references are in inventory.json. This report is local, contains no model data or copied screenshots, and does not modify application/runtime behavior.</p>
<script>
const rows=[...document.querySelectorAll('#features tbody tr')];
function filter(){{let q=document.querySelector('#query').value.toLowerCase(),a=document.querySelector('#area').value,p=document.querySelector('#priority').value,n=0;for(const r of rows){{r.hidden=!!((a&&r.dataset.area!==a)||(p&&r.dataset.priority!==p)||!r.dataset.search.includes(q));if(!r.hidden)n++}}document.querySelector('#shown').textContent=n+' of '+rows.length+' capabilities shown';}}
for(const id of ['query','area','priority'])document.getElementById(id).addEventListener('input',filter);filter();
</script></html>'''
    (output/'index.html').write_text(html,encoding='utf-8')


def main():
    catalog()
    refresh_acceptance_descriptions()
    source_paths=sorted({p for row in FEATURES for p in row['sources']+row['tests']})
    for path in source_paths:
        if not (ROOT/path).is_file():
            raise ValueError('Missing source/test reference: '+path)
    data=dict(schema='dlss5-functionality-inventory/1',generated_utc=datetime.now(timezone.utc).isoformat(),
        scope='Source review plus existing evidence. No tests executed by this generator.',
        features=FEATURES,controls=controls(),evidence=evidence(),
        source_sha256={p:digest(ROOT/p) for p in source_paths},
        priorities=[
            dict(title='Vulkan responsiveness',action='Reproduce the intermittent x86 live-control timeout with request timing; establish whether startup, render-thread stalls or mailbox service is responsible. Do not erase it with retries.'),
            dict(title='Experimental cross-bitness memory failures',action='Reproduce the two non-WARP bad-allocation cases and bound address-space/resource growth; keep them separate from the working default helper route.'),
            dict(title='Whole-display Screen Studio',action='Pass the existing monitor-feedback exclusion test on a controlled desktop; separately validate physical HDR.'),
            dict(title='Interrupted installer cleanup',action='Actual process termination exposed an orphaned model-copy .tmp file after recovery. Full managed rerun, public runtime checks and empty-payload online bootstrap have now passed; retain their exact scope rather than treating them as all-device acceptance.'),
            dict(title='Remaining live controls and quality',action='Exercise SR/spatial/DLSS SR/XeSS, aspect changes, adaptive budgets, custom-model validation and comparison modes with pixel/runtime oracles, then motion/fullscreen/device-loss/long-run tests.'),
            dict(title='Physical targets and broader gameplay',action='Validate Android follow/touches/lifecycle on a phone, actual stereo VR on a headset, alternate AMD/Intel devices, multi-GPU selection and representative visible games with latency/memory measurements.')],
        not_offered=[
            'x64 DirectDraw/D3D1-8 and general x86 VR installation routes are unsupported; existing translated outputs are a different case.',
            'The experimental x86 native-driver route has no NVIDIA graph host; its NVIDIA rows are intentionally N/A.',
            'No universal arbitrary-model architecture/training pipeline; loading requires a compatible model layout. Models/vendor DLLs are supplied separately.',
            'DLSS5 Everything is a Windows application. Android is a remote capture/client path, not on-phone Phoenix inference; a portable Phoenix library does not make this installer a Linux/macOS app.',
            'Screen Studio omits recording and system-audio capture by design. It also omits frame generation and optical-flow motion; it does not provide engine motion/depth or native HDR model inference.',
            'Android whole-display overlay enhancement, LAN audio/input forwarding, unattended reconnect and automatic discovery are not implemented.',
            'Hosted community reports/chat/votes and full 38-language translation parity are not implemented.',
            'App update checks do not install a self-update. Privacy/anti-cheat safety and all-game compatibility are not guaranteed.'
        ],documentation_gaps=[
            'Main README still says no phone/network/GPU tests ran for LAN/Android. Detailed Android/LAN validation records emulator and limited physical-device tests.',
            'The older migration checkpoint predates current Spout/HDR support. Recording and system-audio capture have been removed by request.',
            'The older Swapper audit says Vulkan setup lacks x86 support; current code/tests and current fixtures have x86 Vulkan support.',
            'Several route captions still describe the historical loose-feeder chain, while a compatible payload uses the packaged addon. The old fallback message about x86 Vulkan also survives.',
            'Several documents quote older test totals. Current managed rerun: 1,023 passed/7 opt-in skipped, with opt-ins exercised separately; one initial intermittent failure remains documented. See current acceptance receipts.'
        ])
    fixes_path=ROOT/'local-build/validation-current/FIXES.json'
    if fixes_path.is_file() and load(fixes_path).get('passed'):
        revised_priorities={
            'Vulkan responsiveness':('Broader Vulkan latency and gameplay','The mapped-upload readback fault is fixed and 1080p throughput is measured in FIXES.html. Test more hardware, resolutions, long sessions and visible retail-game latency.'),
            'Whole-display Screen Studio':('Capture edge cases and physical HDR','The controlled whole-monitor feedback test passed. Qualify protected/exclusive-fullscreen sources, real HDR color and display/device changes.'),
            'Interrupted installer cleanup':('Broader interruption timing','The forced-termination orphaned-copy bug is fixed. Expand interruption timing, update/restore cancellation, disk exhaustion and security-software scenarios.'),
            'Remaining live controls and quality':('Guided temporal upscalers and quality','Color-only capture explicitly rejects temporal upscalers and unavailable targets. Qualify real engine depth/motion routes, perceptual quality, fullscreen/device loss and long-run budgeting.')}
        for item in data['priorities']:
            if item['title'] in revised_priorities:item['title'],item['action']=revised_priorities[item['title']]
        count=load(fixes_path).get('managed_tests',{}).get('passed','unknown')
        data['documentation_gaps'][-1]=f'Older checkpoints quote historical test totals. Current fix verification records {count} managed passes and separate live receipts in FIXES.html; historical failures and corrected test assertions remain documented.'
    output=ROOT/'local-build/functionality-inventory';output.mkdir(parents=True,exist_ok=True)
    (output/'inventory.json').write_text(json.dumps(data,indent=2,ensure_ascii=False),encoding='utf-8')
    render(data,output)
    print(json.dumps(dict(features=len(FEATURES),controls=len(data['controls']),source_references=len(source_paths),
        output=str(output/'index.html'),tests_launched=False,gpu_used=False)))


if __name__=='__main__':
    main()
