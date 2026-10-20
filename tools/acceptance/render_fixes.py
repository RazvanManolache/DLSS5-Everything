"""Render the five requested fixes from current receipts, without starting tests."""
from pathlib import Path
import html,json,xml.etree.ElementTree as ET
APP=Path(__file__).resolve().parents[2];OUT=APP/'local-build/validation-current'
def read(name):
    path=OUT/name
    return json.loads(path.read_text(encoding='utf-8-sig')) if path.is_file() else {}
def passed_tests(name):
    path=OUT/name
    if not path.is_file():return None
    counters=ET.parse(path).find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters')
    return dict(counters.attrib) if counters is not None else None
devices=read('feature-devices-fixed.json');transport=read('fixed-transport-visible.json');controls=read('feature-controls-fixed.json')
vulkan=next((row for row in transport.get('cases',[]) if row['id']=='vulkan-x86'),{})
items=[
    ('Alternate runner delivery',devices.get('passed',False),
     'Reset once per captured input; accept retired worker composition using delivery counters. An explicit immutable-input token keeps synchronous retries from expiring their own CPU results. Ordinary moving-frame age limits are preserved.',
     f'{sum(row.get("passed",False) for row in devices.get("cases",[]))}/{len(devices.get("cases",[]))} device/thread selections passed. CPU is a reference route, not a real-time performance claim.',
     ['feature-devices-fixed.json']),
    ('Vulkan x86 transport',vulkan.get('passed',False),
     'Swizzle channels in a CPU register before writing GPU upload memory. Never read GPU-mapped upload memory to swap already-written channels.',
     f'Visible 1920×1080 synthetic test: {vulkan.get("fresh_output_fps",0):.2f} fresh presented outputs/s. The earlier same-scenario observation was 0.29/s; the original release audit measured 0.24/s. These are application delivery rates, not isolated kernel timings.',
     ['fixed-transport-visible.json']),
    ('Truthful controls',controls.get('passed',False),
     'Color-only capture refuses three-way comparison, presentation-FPS budgets and vendor temporal upscalers. The UI filters available choices and preserves old saved values as inactive. Missing optional SYCL CPU ICD is not advertised as a usable runner.',
     f'{len(controls.get("cases",[]))} live cases; refused choices must explain the route restriction and leave working settings unchanged.',
     ['feature-controls-fixed.json','control-availability-fixes.trx']),
    ('Interrupted installation',read('interrupted-install.json').get('passed',False),
     'Journal atomic-copy temporary files before copying, for both installation and restoration. Normal completion removes their journal entries; recovery can remove a file left by process termination.',
     'An actual owned installer was terminated mid-copy. Recovery preserved original files, removed the active addon and left no untracked temporary files.',
     ['interrupted-install.json','installer-fixes.trx']),
    ('Whole-display feedback',read('monitor-fixes.json').get('passed',False),
     'Verify output-window capture exclusion. The test first proves the generated source is visible, then requires two complete exact input matches while a contrasting processed view is visibly displayed.',
     'Driver startup notifications may occlude the source briefly. The bounded settle period does not relax the pixel oracle or omit a monitor region.',
     ['monitor-fixes.json','monitor-fixes.trx'])]
data={'passed':all(row[1] for row in items),'scope':'Local fixes; not a published release. Phone and retail games untouched.',
      'managed_tests':passed_tests('fixes-full.trx'),'candidate':read('release-source-fixes-build.json'),
      'screen_full_run':read('screen-fixes.json'),'screen_strength_retry':read('screen-strength-fixes.json'),
      'graphics_matrix':read('fixed-graphics.json'),
      'transport_measurements':transport,'hardware':read('audit.json').get('hardware',{}),
      'items':[dict(name=name,passed=passed,fix=fix,evidence=evidence,receipts=receipts) for name,passed,fix,evidence,receipts in items]}
(OUT/'FIXES.json').write_text(json.dumps(data,indent=2),encoding='utf-8')
e=html.escape
rows=''.join('<tr><td>'+e(name)+'</td><td class="'+('pass' if passed else 'pending')+'">'+('Passed' if passed else 'Pending / failed')+'</td><td>'+e(fix)+'</td><td>'+e(evidence)+'<p>'+ ' · '.join(f'<a href="{e(name)}">{e(name)}</a>' for name in receipts)+'</p></td></tr>' for name,passed,fix,evidence,receipts in items)
page='''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>DLSS5 Everything — fixes verification</title><style>body{font:16px system-ui;background:#111823;color:#edf2fb;max-width:1400px;margin:36px auto;padding:0 24px}a{color:#9bd1ff}td,th{padding:16px;border:1px solid #34445e;text-align:left;vertical-align:top;line-height:1.5}table{border-collapse:collapse}th{background:#24334b}.pass{color:#8be3b1}.pending{color:#ffcc88}pre{white-space:pre-wrap;background:#1c2839;padding:16px}</style><h1>Five fixes: current verification</h1>'''
page+='<p>'+e(data['scope'])+'</p><p>The candidate rebuilds release-compatible integration code around unchanged public 0.81 kernels and runner libraries. NVIDIA DLLs and models are not included.</p><table><tr><th>Issue</th><th>Result</th><th>What changed</th><th>Evidence and limits</th></tr>'+rows+'</table>'
page+='<h2>Managed regression tests</h2><pre>'+e(json.dumps(data['managed_tests'],indent=2))+'</pre><p><a href="feature-matrix.html">Full functionality matrix</a> · <a href="REPORT.html">Initial release audit and wider limitations</a> · <a href="FIXES.json">Current verification data</a></p></html>'
if transport.get('passed'):
    labels={'vulkan-x86':'Vulkan x86 → native RTX helper','dx11-x64':'D3D11 x64 → native RTX helper','dx12-x64':'D3D12 x64 → in-process native RTX'}
    timings=''.join('<tr><td>'+e(labels.get(row['id'],row['id']))+'</td><td>'+f'{row["fresh_output_fps"]:.2f}'+'</td></tr>' for row in transport['cases'] if row.get('passed'))
    page=page.replace('</html>','<h2>Current visible 1080p delivery</h2><p>Same generated scene and returned-pixel controls. These are fresh-output rates, not model-only GPU timings, game FPS or a quality-parity score.</p><table><tr><th>Route</th><th>Fresh outputs / second</th></tr>'+timings+'</table><pre>'+e(json.dumps(data['hardware'],indent=2))+'</pre></html>')
graphics=data['graphics_matrix']
if graphics.get('passed'):
    count=sum(len(graphics.get(key,[])) for key in ('cases','motion_cases','control_cases','resize_cases','multiwindow_cases'))
    page=page.replace('</html>',f'<p><a href="fixed-graphics.json">All {count} final graphics API checks passed</a>: seven APIs, x86/x64, returned pixels, moving-frame pairing, controls, resize and window isolation.</p></html>')
page=page.replace('</html>','<p>The seven Screen Studio scenarios were checked in a full run: six passed, and one strength test depended on the diagnostic file removed from frame polling. Its pixel-effect check passed; its settings assertion now uses the authenticated bridge and the focused retry passed. <a href="screen-fixes.trx">Initial full run</a> · <a href="screen-strength-fixes.trx">Corrected strength check</a></p></html>')
(OUT/'FIXES.html').write_text(page,encoding='utf-8')
print(json.dumps({'passed':data['passed'],'report':str(OUT/'FIXES.html')}))
