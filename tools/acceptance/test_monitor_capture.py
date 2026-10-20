"""Controlled whole-monitor feedback test; never inspect personal screenshots."""
from pathlib import Path
import json, os, shutil, subprocess, sys, tempfile
import xml.etree.ElementTree as ET
from payload import stage

APP=Path(__file__).resolve().parents[2];OUT=APP/'local-build/validation-current';BASE=APP.parent
roots=Path(tempfile.gettempdir())/'DLSS5-ScreenValidation'
before=set(roots.iterdir()) if roots.exists() else set()
result={'passed':False,'phone_touched':False,'scope':'Generated fullscreen source, visible-pixel precondition and whole-monitor output-exclusion test'}
all_cases='--all' in sys.argv
strength_only='--strength-only' in sys.argv
log_name='screen-strength-fixes' if strength_only else 'screen-fixes' if all_cases else 'monitor-fixes'
try:
    with tempfile.TemporaryDirectory(prefix='monitor-fixes-',dir=APP/'local-build') as directory:
        package=Path(directory)/'package';package.mkdir();result.update(stage(package,APP))
        payload=Path(directory)/'payload';payload.mkdir()
        shutil.copy2(package/'Phoenix.addon64',payload/'phoenix.addon64')
        shutil.copy2(BASE/'dlss5-everything-testing/games/dx11-x64/dxgi.dll',payload/'ReShade64.dll')
        shutil.copy2(BASE/'_dlls/nvngx_dlssnr.dll',payload/'nvngx_dlssnr.dll')
        env=os.environ.copy();env.update(DLSS5_SCREEN_LIVE_PAYLOAD=str(payload),DLSS5_SCREEN_LIVE_NEURAL='1',DLSS5_SCREEN_LIVE_MONITOR='1')
        trx=OUT/(log_name+'.trx')
        trx.unlink(missing_ok=True) # A build failure must not reuse an old pass.
        with (OUT/(log_name+'.log')).open('w') as log:
            process=subprocess.run(['dotnet','test','tests/Dlss5CompatApp.Tests','--filter',
                'FullyQualifiedName~StrengthControlsChangeReturnedPixels' if strength_only else
                'FullyQualifiedName~ScreenStudioLiveTests' if all_cases else 'FullyQualifiedName~SyntheticMonitorDoesNotCaptureItsEnhancedOverlay',
                '--logger','trx;LogFileName='+log_name+'.trx',
                '--results-directory',str(OUT),'--verbosity','minimal'],cwd=APP,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=600 if all_cases else 180)
        result['exit_code']=process.returncode;result['passed']=process.returncode==0
        if trx.is_file():
            result['cases']={item.get('testName'):item.get('outcome') for item in
                ET.parse(trx).findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')}
finally:
    # Only new roots created by this invocation are ours. In particular, never
    # revisit the two older folders whose deletion was blocked in an earlier run.
    for path in (set(roots.iterdir()) if roots.exists() else set())-before:
        if path.resolve().parent!=roots.resolve() or path.is_symlink() or path.is_junction():raise RuntimeError('Unsafe screen-test cleanup')
        if any(p.is_symlink() or p.is_junction() for p in path.rglob('*')):raise RuntimeError('Linked screen-test output')
        shutil.rmtree(path)
    (OUT/(log_name+'.json')).write_text(json.dumps(result,indent=2))
    if all_cases:
        monitor=next((outcome for name,outcome in result.get('cases',{}).items() if name.endswith('SyntheticMonitorDoesNotCaptureItsEnhancedOverlay')),None)
        (OUT/'monitor-fixes.json').write_text(json.dumps(result|{'passed':monitor=='Passed','scope':'Whole-monitor result within full Screen Studio run'},indent=2))
print(json.dumps(result))
raise SystemExit(0 if result['passed'] else 1)
