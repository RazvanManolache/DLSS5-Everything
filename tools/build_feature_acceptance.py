"""Summarize current acceptance receipts by product area, without launching tests.

Unit-test counts describe exercised contracts, not feature completeness. Areas may
reference the same suite; their counts must not be added into a coverage score.
Live acceptance remains separate from delivery, fallbacks and missing hardware.
"""
from collections import Counter
from datetime import datetime, timezone
from html import escape
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'local-build/validation-current'
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def read(name):
    path = OUT / name
    return json.loads(path.read_text(encoding='utf-8-sig')) if path.is_file() else {}


def unit_counts():
    tree = ET.parse(OUT / ('fixes-full.trx' if (OUT/'fixes-full.trx').is_file() else 'managed-recheck.trx'))
    counts = Counter()
    for item in tree.findall('.//t:UnitTestResult', NS):
        if item.get('outcome') == 'Passed':
            counts[item.get('testName').split('(')[0].split('.')[-2]] += 1
    return counts


def build_rows():
    rows = []

    def row(area, suites, checked, remaining, receipts):
        counts = unit_counts()
        rows.append(dict(area=area, unit_suites={name: counts[name] for name in suites},
                         checked=checked, remaining=remaining, receipts=receipts))

    row('Discovery / library', ['LibraryServicesTests', 'RendererDetectionTests', 'ScannerExecutableRoleTests',
        'EngineBootstrapTests', 'EmulatorProfilesTests', 'GameArtworkTests', 'MyGamesTests', 'ConvenienceTests',
        'VerifiedRendererProfileTests'],
        'Store-format parsers, scanning/detection, saved library state, covers, favorites and profiles passed their managed checks. '
        'All 82 synthetic Windows fixtures were inspected through the actual CLI with matching architecture.',
        'Five store formats are synthetic contracts, not a live check of every store client. Nineteen emulator profiles are metadata-tested; '
        'their actual emulator/ROM combinations remain unqualified. Four x64 DirectDraw routes are intentionally unsupported.',
        ['managed-recheck.trx', 'fixture-inspections.json'])
    row('Installation / recovery', ['PhoenixInstallerTests', 'WorkflowSafetyTests', 'CliSafetyTests',
        'PackagedPhoenixTests', 'BackendProfileStoreTests', 'InstallPreflightTests', 'VulkanLayerInstallerTests'],
        'Five released-addon fixtures completed install/update/remove/reinstall and saved-settings restart checks. '
        'An owned installer was actually terminated during its model copy; recovery preserved original bytes and removed the active addon.',
        'The forced termination exposed an orphaned 165,840,496-byte atomic-copy temporary file after recovery. '
        'Actual power loss, filesystem corruption, antivirus intervention and broad interrupted-update timing remain unqualified.',
        ['managed-recheck.trx', 'release-live.json', 'interrupted-install.json'])
    row('Components', ['PhoenixDownloadTests', 'ComponentCliTests', 'ComponentInspectionTests', 'PeAndIniTests'],
        'Real online bootstrap into an empty payload completed successfully, downloading public Phoenix 0.81 and matching x86 companion, '
        'ReShade, wrappers and DOS dependency. Exact asset digest and installed addon hashes were recorded. '
        'Mocked corruption/cache/cancellation/ABI checks also passed. The valid model DLL passed asynchronous validation; a missing model folder was rejected.',
        'Fresh component bootstrap is not a complete first-user GUI installation. Real network interruption, authenticated proxies '
        'and remote corruption were not injected; optional NVIDIA models are supplied separately.',
        ['managed-recheck.trx', 'online-setup.json', 'release-checks.json', 'feature-model-validation.json'])
    row('Native graphics', ['PhoenixBridgeEvidenceTests', 'PhoenixTransitionEvidenceTests', 'SmokeTestTests'],
        '52 released-addon graphics checks passed: seven APIs in both architectures, plus motion, controls, resize and multi-window cases. '
        'Five Everything lifecycle fixtures passed observable checks.',
        'Release 0.81 omits frame-ID lineage diagnostics. Vulkan x86 delivered only 0.24 fresh outputs/s in the earlier 1080p stability check. '
        'Synthetic fixture results do not establish retail-game, fullscreen/device-loss or temporal-quality acceptance.',
        ['release-graphics.json', 'release-live.json'])
    row('Live controls', ['PhoenixControlTests', 'PhoenixControlCliTests'],
        'All 67 app control names were checked against running release binaries. Strength/skin pixel effects, save/restart, screenshots, '
        'pair/difference comparison modes and current-selection presentation were exercised.',
        'The release normalizes three-way comparison to pair comparison on direct feeds. The app still exposes the unavailable choice. '
        'A setting acknowledgement or matching name is not proof of every intended effect; all combinations and timing-history persistence remain open.',
        ['release-runtime-contract.json', 'feature-controls.json', 'feature-controls-recheck.json'])
    row('Upscaling', ['PhoenixControlTests', 'PhoenixInstallerTests'],
        '18 spatial combinations were presented at 640x480: three quality levels by six aspect choices, including custom 3:2. '
        'Runtime processing geometry and changed displayed pixels were recorded. The released standalone DLL also passed a spatial host-buffer test.',
        'NVIDIA SR and XeSS selections, with and without their runtime DLLs, reported spatial fallback because this capture path has no observed '
        'depth/motion guides. These are fallback checks, not successful temporal-upscaler execution. Guided-engine quality, HUD and aspect fidelity remain open.',
        ['feature-controls.json', 'native-dll.json'])
    row('Adaptive execution', ['PhoenixControlTests'],
        'Manual depths 1/2/4/6/8, two refinement passes, repeating/skipping a block, and continued output were exercised. '
        'GPU-time budgeting reduced execution to two blocks under a tight target and restored eight under a generous target.',
        'Presentation-FPS budgeting is normalized to GPU milliseconds on this direct-feed route; the app exposes a choice that does not apply. '
        'Long-run oscillation, quality impact, paired performance savings and workload transitions remain unqualified.',
        ['feature-controls.json', 'feature-controls-recheck.json'])
    devices = read('feature-devices.json')
    qualified = sum(c.get('passed', False) for c in devices.get('cases', []))
    row('Alternate runners / devices', ['PhoenixInstallerTests', 'PhoenixControlTests'],
        f'Configuration/device-ID contracts passed. Additional release-backed device run: {qualified} of '
        f'{len(devices.get("cases", []))} requested selections delivered output. Native RTX passed; CPU at 1/32/automatic, CUDA, both advertised Vulkan devices, '
        'and SYCL did not deliver accepted outputs. A fresh-fixture CUDA recheck also failed. CPU/CUDA/Vulkan telemetry showed discarded captures with '
        'zero delivered/presented worker results. SYCL reported “Private CPU ICD missing”.',
        'These are tiny synthetic delivery checks, not throughput/quality parity or a thread-scaling benchmark. '
        'Dedicated AMD/Intel cards, HIP, physical GPU removal and a game/inference split across separate GPUs remain unqualified.',
        ['managed-recheck.trx', 'feature-devices.json', 'feature-device-recheck.json', 'device-output-summaries.json'])
    row('DOS', ['DosEnhancedDiscoveryTests', 'PhoenixInstallerTests'],
        'Published DOSBox Enhanced v0.5 passed native, NVIDIA NR and Phoenix VGA tests after supplying its documented external vendor DLLs.',
        'One VGA fixture does not qualify broader gameplay, input, audio or save handling. Guest-clock and enhanced host-clock frame rates '
        'must not be compared as a speed ratio. The bundled DOS Phoenix library is separate from the Windows 0.81 acceptance baseline.',
        ['dos-configured.json', 'dos-results.json'])
    row('VR', ['PhoenixInstallerTests', 'GameLauncherTests'],
        '16 OpenXR profile/IPC contracts and 51 stereo schedule/counter contracts passed. Installation/launcher contracts were also exercised.',
        'No physical headset/compositor run was performed. Eye order, stereo history correctness, pose, end-to-end latency and comfort remain unqualified.',
        ['openxr-contracts.log', 'host-contracts.log', 'managed-recheck.trx'])
    row('Screen Studio', ['ScreenStudioTests'],
        'Six of seven unique live scenarios passed against the released addon, including actual returned-pixel effects, target switching, '
        'scaling, resize, controls, paired screenshots and synthetic float transport.',
        'Whole-display feedback exclusion has not passed. Initial target-switch failure was a fixture-placement problem and passed after its test fix. '
        'Exclusive fullscreen, protected applications and physical HDR color accuracy remain unqualified.',
        ['screen-live.trx', 'screen-recheck.trx', 'monitor-diagnostic.json'])
    row('Media / sharing — recording removed', ['ScreenMediaTests'],
        'Image/video file conversion passed the short live jobs; Spout sender/receiver recovered matching pixels. '
        'Recording and system-audio capture are absent from the app/worker interfaces; legacy recording preferences are ignored.',
        'Long conversions, AV sync, VFR, uncommon codecs, external OBS receivers and physical HDR accuracy remain unqualified. '
        'File conversion is retained; live recording is not a supported feature.',
        ['screen-live.trx', 'spout-gpu.log', 'managed-recheck.trx'])
    row('LAN / Android', ['LanFrameTests', 'StreamerIntegrationTests'],
        'TLS loopback, pairing/protocol and cleanup contracts passed. Emulator raw round trips, follow mode, controls, overlays and '
        'consent UI were exercised; a real released-addon neural round trip passed.',
        'Compressed codec could not qualify: software Vulkan initialization failed, then host-backed Vulkan froze the owned emulator. '
        'Physical follow-mode/touches, real Wi-Fi loss/throughput, permission lifecycle and thermals remain unqualified.',
        ['managed-recheck.trx', 'android-emulator.json', 'android-emulator-recheck.json', 'android-extended.json', 'android-codec-host-timeout.json'])
    row('UI / distribution', ['LayoutTests', 'DarkThemeTests', 'ButtonBehaviorTests', 'ArtworkLayoutTests',
        'ConvenienceTests', 'AppUpdateServiceTests', 'SupportReportTests'],
        'The complete managed rerun passed 1,023 tests. UI layout/theme/navigation/accessibility-name contracts passed. '
        'Self-contained publish succeeded; packaged CLI exported 67 controls and contained none of the forbidden external model/addon payloads. '
        '39 distribution-notice checks passed.',
        'One intermittent test-server publication failure occurred in the initial full run and is retained. Mixed-DPI/200%, screen-reader/RTL '
        'acceptance, an actual distribution release and current remote CI execution were not performed.',
        ['managed.trx', 'managed-recheck.trx', 'publish.json', 'audit.json'])
    # Retain initial release findings as history; promote only independently
    # completed fix receipts, never a source change or an acknowledgement alone.
    def updated(area,checked,remaining,receipts):
        item=next(item for item in rows if item['area']==area)
        item.update(checked=checked,remaining=remaining,receipts=item['receipts']+receipts)
    if read('interrupted-install.json').get('passed'):
        updated('Installation / recovery',
            'The atomic-copy temporary path is now journaled before copying. Forced installer termination and actual recovery passed: originals preserved, addon removed and no untracked temporary files remained. Five original released-addon lifecycle fixtures also passed.',
            'Actual power loss, filesystem corruption, antivirus intervention and a broad interruption timing sweep remain unqualified.',
            ['installer-fixes.trx'])
    fixed_controls=read('feature-controls-fixed.json')
    if fixed_controls.get('passed'):
        updated('Live controls',
            '46 live control cases passed, including explicit rejection of unavailable three-way comparison, temporal upscalers and presentation-FPS budgeting on color-only capture. Rejected requests preserved working settings; saved unavailable choices remain visibly inactive. The helper device catalogue is used by the UI and request validator.',
            'Acknowledgements and matching names do not prove every individual control effect; all combinations and timing-history persistence remain open.',
            ['feature-controls-fixed.json','control-availability-fixes.trx'])
        updated('Upscaling',
            'All 18 spatial quality/aspect combinations passed with returned-pixel checks. Color-only capture explicitly refuses NVIDIA SR and XeSS requests, with and without vendor DLLs, instead of accepting spatial fallback as vendor execution.',
            'Guided-engine temporal upscalers, HUD fidelity and perceptual aspect/quality acceptance remain unqualified.',
            ['feature-controls-fixed.json'])
        updated('Adaptive execution',
            'Manual depth, block repeats/skips, refinement and GPU-time adaptation passed. Tight/generous budgets changed depth as expected. Presentation-FPS budgeting is explicitly unavailable on the hidden-companion capture route.',
            'Long-run convergence, oscillation, quality impact and measured savings remain unqualified.',
            ['feature-controls-fixed.json'])
    fixed_devices=read('feature-devices-fixed.json')
    if fixed_devices.get('passed'):
        entries=fixed_devices['cases']
        updated('Alternate runners / devices',
            f'{len(entries)} execution selections delivered fresh, presented outputs through the fixed helper, with runtime engine/device identity and nonblank screenshot checks. CPU used 1, all available and automatic thread choices. The missing optional private Intel CPU ICD is no longer advertised as an available SYCL runner.',
            'CPU is a slow reference route. Synthetic delivery does not establish throughput or visual-quality parity. Dedicated AMD/Intel cards, HIP, device removal and separate game/inference GPUs remain unqualified.',
            ['feature-devices-fixed.json','release-source-fixes-build.json'])
    transport=read('fixed-transport-visible.json')
    vulkan=next((entry for entry in transport.get('cases',[]) if entry['id']=='vulkan-x86'),{})
    if vulkan.get('passed'):
        updated('Native graphics',
            'The original public release passed 52 graphics checks. A release-compatible source hotfix eliminates read-modify-write of GPU-mapped upload memory. '+
            f'Its visible Vulkan x86 1080p check delivered {vulkan["fresh_output_fps"]:.2f} fresh outputs/s with returned-pixel control effects.',
            'The unchanged public 0.81 binary remains slow; the fix requires the new build. Original release lacks frame-ID diagnostics. Retail games, fullscreen/device-loss and perceptual temporal quality remain unqualified.',
            ['fixed-transport-visible.json','release-source-fixes-build.json'])
    if read('monitor-fixes.json').get('passed'):
        updated('Screen Studio',
            'The whole-display feedback test now passes: a visibly different output overlay is displayed while two successive captured inputs match the generated source across the monitor. Output exclusion is verified by the worker. Six prior live window/media/float scenarios also passed.',
            'Transient driver notifications must settle before the exact whole-monitor oracle can pass. Exclusive fullscreen, protected applications and physical HDR color accuracy remain unqualified.',
            ['monitor-fixes.json','monitor-fixes.trx'])
    if (OUT/'fixes-full.trx').is_file():
        counters=ET.parse(OUT/'fixes-full.trx').find('.//t:Counters',NS)
        if counters is not None and int(counters.get('failed','1'))==0:
            item=next(item for item in rows if item['area']=='UI / distribution')
            item['checked']=f'The current full managed suite passed {counters.get("passed")} tests; opt-in live scenarios are recorded separately. UI layout'+item['checked'].split('UI layout',1)[-1]
            item['receipts'].append('fixes-full.trx')
    graphics=read('fixed-graphics.json')
    if graphics.get('passed'):
        count=sum(len(graphics.get(key,[])) for key in ('cases','motion_cases','control_cases','resize_cases','multiwindow_cases'))
        item=next(item for item in rows if item['area']=='Native graphics')
        item['checked']+=f' The final candidate also passed all {count} graphics API checks across both bitnesses: returned pixels, motion pairing, live controls, resize and window isolation.'
        item['receipts'].append('fixed-graphics.json')
    return rows


def main():
    rows = build_rows()
    data = {'generated_utc': datetime.now(timezone.utc).isoformat(), 'scope': 'All requested product areas; evidence levels kept separate',
            'tests_launched_by_generator': False, 'rows': rows}
    for row in rows:
        for receipt in row['receipts']:
            if not (OUT / receipt).is_file():
                raise FileNotFoundError(receipt)
    html_rows = []
    for row in rows:
        suites = ', '.join(f'{name}: {count}' for name, count in row['unit_suites'].items())
        links = ' · '.join(f'<a href="{escape(name, quote=True)}">{escape(name)}</a>' for name in row['receipts'])
        html_rows.append('<tr><td><strong>'+escape(row['area'])+'</strong></td><td>'+escape(row['checked'])+
            '<details><summary>Managed suite evidence</summary>'+escape(suites)+'</details><p>'+links+'</p></td><td>'+escape(row['remaining'])+'</td></tr>')
    (OUT/'feature-matrix.json').write_text(json.dumps(data, indent=2), encoding='utf-8')
    (OUT/'feature-matrix.html').write_text('''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>DLSS5 Everything — full feature acceptance</title><style>
body{font:16px system-ui;background:#101620;color:#e8eef8;margin:30px auto;max-width:1500px;padding:0 24px}p,td{line-height:1.55}a,summary{color:#91c8ff}table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;vertical-align:top;padding:18px;border:1px solid #38465c}th{background:#25344b}td:first-child{width:15%}td:nth-child(2){width:45%}details{margin-top:12px;font-size:12px}summary{cursor:pointer}section{padding:20px;background:#1c2534;border-radius:12px;margin:20px 0}
</style><h1>DLSS5 Everything: all functionality areas</h1><p>'''+escape(data['generated_utc'])+''' · Initial public Phoenix 0.81 baseline and explicitly identified local fixes. Fixed binaries are a separate acceptance baseline and have not been published.</p>
<section><strong>Implemented, contract-tested, delivered and accepted are different claims.</strong><p>Every requested area is mapped below. Shared suite counts overlap and must not be added into a feature-coverage percentage. No physical-device, retail-game, temporal-upscaler or quality claims follow from a synthetic pass.</p><p><a href="REPORT.html">Full validation report</a> · <a href="feature-matrix.json">Machine-readable matrix</a> · <a href="../functionality-inventory/index.html">Detailed source inventory</a></p></section>
<table><thead><tr><th>Area</th><th>What was actually checked</th><th>Failures, limitations and remaining work</th></tr></thead><tbody>'''+''.join(html_rows)+'</tbody></table></html>', encoding='utf-8')
    print(json.dumps({'areas': len(rows), 'output': str(OUT/'feature-matrix.html')}))


if __name__ == '__main__':
    main()
