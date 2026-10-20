"""Additional app-level feature acceptance against unchanged release binaries."""
from pathlib import Path
import hashlib, json, os, shutil, subprocess, sys, tempfile, time, zipfile
import numpy as np

APP = Path(__file__).resolve().parents[2]
OUT = APP / 'local-build/validation-current'
BASE = APP.parent
TESTING = BASE/'dlss5-everything-testing'
sys.path.insert(0, str(TESTING/'tools'))
from run_live_regressions import ApplicationControl, FixtureControl, command, wait_for_bridge_endpoint, pixel_delta
from run_backends import close_fixture, read, digest
from asset_paths import point_assets, find_assets
from run_execution_sweep import configure

result = {'scope': 'Current Everything control client and public Phoenix 0.81 addon; synthetic D3D12 input',
    'addon_rebuilt': False, 'cases': [], 'passed': False, 'pending': True}
application = APP/'app/Dlss5CompatApp/bin/Debug/net9.0-windows/Dlss5CompatApp.exe'
model_only = '--model-only' in sys.argv
focused = '--focused' in sys.argv or model_only
result_name = 'feature-model-validation.json' if model_only else 'feature-controls-recheck.json' if focused else 'feature-controls.json'
if '--candidate' in sys.argv:result_name=result_name.replace('.json','-fixed.json')
def save(): (OUT/result_name).write_text(json.dumps(result, indent=2))
def brief(snapshot):
    info = snapshot.get('in_process') or snapshot.get('helper') or {}
    return {key:snapshot.get(key) for key in ('selection_revision','presented_revision','delivered','presented','width','height','execution_mode','error')} | {
        'resolution': info.get('resolution'), 'upscaler_status': info.get('upscaler_status'),
        'transformer_blocks':info.get('transformer_blocks'), 'budget_estimate_ms':info.get('budget_estimate_ms'),
        'history_resets':info.get('history_resets'), 'model_validation': info.get('model_validation'),
        'execution':info.get('execution'), 'info_keys':list(info)}

save()
with tempfile.TemporaryDirectory(prefix='feature-controls-', dir=APP/'local-build') as directory:
    root = Path(directory); package = root/'package'; package.mkdir()
    from payload import stage
    result.update(stage(package,APP))
    result['scope']='Feature controls through Everything; see explicit baseline identity'
    payload = root/'payload'; payload.mkdir()
    shutil.copy2(TESTING/'games/dx11-x64/dxgi.dll', payload/'ReShade64.dll')
    env = os.environ.copy(); env['LOCALAPPDATA']=str(root/'user'); Path(env['LOCALAPPDATA']).mkdir()
    subprocess.run([str(application),'--components-import','--payload',str(payload),'--phoenix',str(package/'Phoenix.addon64'),
        '--model',str(BASE/'_dlls/nvngx_dlssnr.dll')], env=env, check=True, capture_output=True)
    matrix={g['id']:g for g in read(TESTING/'matrix.json')['games']}
    game=matrix['dx12-x64']; original=TESTING/'clean'/game['executable']
    folder=root/'fixture'; folder.mkdir(); exe=folder/original.name
    shutil.copy2(original,exe); point_assets(folder,find_assets(original.parent))
    for file in original.parent.glob('*.dll'): shutil.copy2(file,folder/file.name)
    process=None;control=None;installed=False
    work=folder/'measurement';work.mkdir()
    baseline={'Backend':3,'ExecutionDevice':'native','Experimental':False,'AutomaticBudget':False,
        'ResolutionMode':0,'Upscaler':0,'ResolutionQuality':0,'ResolutionAspect':0,
        'OverrideStrengths':True,'Tone':1.,'Structure':1.,'Advanced':True,'AutoMask':True,'Skin':1.,
        'TransformerBlocks':8,'MinimumBlocks':1,'RefinementPasses':1,
        **{f'Block{i}Repeats':1 for i in range(1,9)}}
    try:
        command([application,'--install',exe,'--payload',payload,'--offline']);installed=True
        configure(folder,'phoenix','auto')
        process=subprocess.Popen(list(map(str,[exe,'--background','--windowed','--live-control','--fixed-time','--no-capture',
            '--width','1920' if focused else '640','--height','1080' if focused else '480','--mode','showcase','--seconds','0','--output',work])), cwd=folder, env=env,
            stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        control=ApplicationControl(application,exe,process,work)
        wait_for_bridge_endpoint(exe,process)
        control.wait({'Backend':3},route='in_process')
        FixtureControl(work,process).set({'frozen':True,'reset_view':True})
        control.set(baseline)
        snapshot=control.call()['snapshot']
        (OUT/('feature-controls-recheck-baseline.json' if focused else 'feature-controls-baseline.json')).write_text(json.dumps(snapshot,indent=2))
        result['addon_sha256']=digest(folder/'Phoenix.addon64')
        result['input_extent']=[1920,1080] if focused else [640,480]
        baseline_image=control.screenshot()
        def check(name, settings, capture=False, extra=None):
            if model_only and name != 'nvidia-dll-model-selection': return
            if focused and name not in ('comparison-mode-0','automatic-presentation-fps-budget',
                'automatic-budget-disabled-full-depth','nvidia-dll-model-selection'): return
            row={'name':name,'settings':settings,'passed':False};start=time.monotonic()
            try:
                unsupported = settings.get('Comparison') == 0 or settings.get('BudgetMode') == 1 or settings.get('Upscaler') in (1,2)
                if unsupported:
                    previous=control.call()['snapshot'].get('settings')
                    completed,reply=control.call('set',settings,accept_failure=True)
                    assert completed.returncode != 0 and not reply.get('ok'),'Unavailable route choice was accepted'
                    reason=reply.get('error') or completed.stderr
                    assert reason and ('Direct capture' in reason),'Unavailable choice lacks its route explanation'
                    current=control.call()['snapshot']
                    assert current.get('settings')==previous,'Rejected choice modified the working settings'
                    row.update(passed=True,explicitly_rejected=True,rejection_reason=reason.strip())
                    return row
                snapshot=control.set(settings)
                row['snapshot']=brief(snapshot)
                if capture:
                    image=control.screenshot()
                    assert list(image.size)==result['input_extent'],'Presented extent changed unexpectedly'
                    row['pixel_difference_from_normal']=pixel_delta(baseline_image,image)
                if extra: extra(row,snapshot)
                row['passed']=True
            except Exception as error:
                row['error']=str(error)[-1600:]
                try:
                    diagnostic=control.call()['snapshot']; row['snapshot']=brief(diagnostic)
                    row['applied_settings']=diagnostic.get('applied_settings')
                    row['full_info']=diagnostic.get('in_process')
                except Exception as diagnostic_error: row['diagnostic_error']=str(diagnostic_error)
            finally:
                row['seconds']=round(time.monotonic()-start,2);result['cases'].append(row);save()
                print(json.dumps({k:row[k] for k in ('name','passed','seconds')}),flush=True)
            return row
        for quality in range(3):
            for aspect in range(6):
                check(f'spatial-quality-{quality}-aspect-{aspect}',{'ResolutionMode':1,'Upscaler':0,
                    'ResolutionQuality':quality,'ResolutionAspect':aspect,'ResolutionRatioWidth':3,'ResolutionRatioHeight':2},True)
        for vendor in (1,2):
            for supplied in (False,True):
                if supplied:
                    name='nvngx_dlss.dll' if vendor==1 else 'libxess.dll'
                    shutil.copy2(BASE/'_dlls'/name,folder/name)
                check(f'vendor-upscaler-{vendor}-runtime-{supplied}',{'ResolutionMode':0,'Upscaler':0})
                check(f'vendor-upscaler-{vendor}-runtime-{supplied}-active',{'ResolutionMode':1,'Upscaler':vendor,
                    'ResolutionQuality':0,'ResolutionAspect':0},True)
        control.set(baseline)
        for mode in range(6):
            check(f'comparison-mode-{mode}',{'Backend':2,'SourceA':0,'SourceB':2,'Comparison':mode,
                'ComparisonLabels':False,'ComparisonLines':False,'Divider':.4,'DifferenceGain':4.},True)
        control.set(baseline)
        for blocks in (1,2,4,6,8):
            check(f'manual-depth-{blocks}',{'Experimental':True,'TransformerBlocks':blocks,'MinimumBlocks':1},True)
        check('refinement-two-passes',{'TransformerBlocks':8,'RefinementPasses':2},True)
        check('repeat-block-1-twice',{'RefinementPasses':1,'Block1Repeats':2},True)
        check('skip-block-1',{'Block1Repeats':0},True)
        control.set(baseline)
        def observe_budget(row,snapshot):
            samples=[]; start=time.monotonic()
            for index in range(14):
                time.sleep(.5);snap=control.call()['snapshot'];samples.append({'seconds':round(time.monotonic()-start,2),**brief(snap)})
            row['samples']=samples
            assert samples[-1]['delivered']>samples[0]['delivered'],'Budget test stopped producing output'
        check('automatic-gpu-tight-budget',{'Experimental':True,'AutomaticBudget':True,'BudgetMode':0,'BudgetMs':.1,
            'TransformerBlocks':8,'MinimumBlocks':2},extra=observe_budget)
        check('automatic-gpu-generous-budget',{'BudgetMs':100.},extra=observe_budget)
        check('automatic-presentation-fps-budget',{'Experimental':True,'AutomaticBudget':True,'BudgetMode':1,
            'DesiredFPS':360.,'MinimumBlocks':2,'TransformerBlocks':8},extra=observe_budget)
        check('automatic-budget-disabled-full-depth',baseline,extra=observe_budget)
        control.call('reset_history')
        check('post-history-reset-still-renders',{'ForceReset':False,'ResetEvery':10},True)
        control.set(baseline|{'ResetEvery':0})
        check('nvidia-dll-model-selection',{'ModelSource':'nvidia_dll','ModelDll':str(folder/'PhoenixModel.dll')})
        def validate_model(name, expected_passed):
            reply=control.call('validate_model')
            result[name+'_queued']=(reply.get('snapshot') or {}).get('model_validation')
            deadline=time.monotonic()+45
            while time.monotonic()<deadline:
                validation=control.call()['snapshot'].get('model_validation') or {}
                if validation.get('key') and validation.get('message') and not validation.get('busy'):
                    result[name]=validation
                    assert validation.get('passed') is expected_passed, str(validation)
                    return
                time.sleep(.25)
            raise AssertionError('Model validation never published a completed keyed result')
        validate_model('valid_dll_validation',True)
        if model_only:
            control.call('set',{'ModelSource':'model_folder','ModelFolder':str(root/'missing-model-folder')})
            validate_model('missing_model_folder_validation',False)
        result['passed']=all(c['passed'] for c in result['cases'])
    except Exception as error:
        result['error']=str(error)
    finally:
        if process:
            if control: control.remember_children()
            close_fixture(process)
            if control:control.cleanup_helpers()
        if installed:
            restored=command([application,'--restore',folder],accept_failure=True)
            result['restore_exit_code']=restored.returncode
        result['pending']=False;save()
print(json.dumps({'passed':result['passed'],'cases':len(result['cases']),'error':result.get('error')}))
sys.exit(0 if result['passed'] else 1)
