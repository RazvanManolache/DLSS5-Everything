"""Check advertised execution devices through the real application control path."""
from pathlib import Path
import hashlib,json,os,shutil,subprocess,sys,tempfile,time,zipfile
import numpy as np
APP=Path(__file__).resolve().parents[2];OUT=APP/'local-build/validation-current';BASE=APP.parent;TESTING=BASE/'dlss5-everything-testing'
sys.path.insert(0,str(TESTING/'tools'))
from run_live_regressions import ApplicationControl,FixtureControl,command,wait_for_bridge_endpoint
from run_backends import read,digest,close_fixture
from asset_paths import point_assets,find_assets
from live_assertions import matches
from run_execution_sweep import configure
application=APP/'app/Dlss5CompatApp/bin/Debug/net9.0-windows/Dlss5CompatApp.exe'
only_device=next((sys.argv[i+1] for i,v in enumerate(sys.argv[:-1]) if v=='--only-device'),None)
receipt_name='feature-device-recheck.json' if only_device else 'feature-devices.json'
if '--candidate' in sys.argv:receipt_name=receipt_name.replace('.json','-fixed.json')
result={'scope':'Advertised release 0.81 devices via Everything controls, 640x480 D3D11 helper fixture',
        'addon_rebuilt':False,'cases':[],'pending':True}
def save():(OUT/receipt_name).write_text(json.dumps(result,indent=2))
save()
with tempfile.TemporaryDirectory(prefix='feature-devices-',dir=APP/'local-build') as directory:
    root=Path(directory);package=root/'package';package.mkdir();payload=root/'payload';payload.mkdir();folder=root/'fixture';folder.mkdir()
    from payload import stage
    result.update(stage(package,APP))
    result['scope']='Execution devices through Everything controls; see explicit baseline identity'
    shutil.copy2(TESTING/'games/dx11-x64/dxgi.dll',payload/'ReShade64.dll')
    env=os.environ.copy();env['LOCALAPPDATA']=str(root/'user');Path(env['LOCALAPPDATA']).mkdir()
    imported=subprocess.run([str(application),'--components-import','--payload',str(payload),'--phoenix',str(package/'Phoenix.addon64'),
        '--model',str(BASE/'_dlls/nvngx_dlssnr.dll')],env=env,capture_output=True,text=True)
    if imported.returncode:raise RuntimeError(f'Payload import failed: {imported.stdout} {imported.stderr}')
    matrix={g['id']:g for g in read(TESTING/'matrix.json')['games']};game=matrix['dx11-x64'];original=TESTING/'clean'/game['executable']
    exe=folder/original.name;shutil.copy2(original,exe);point_assets(folder,find_assets(original.parent))
    for file in original.parent.glob('*.dll'):shutil.copy2(file,folder/file.name)
    installed=False;process=None;control=None;work=folder/'measurement';work.mkdir()
    try:
        command([application,'--install',exe,'--payload',payload,'--offline']);installed=True;configure(folder,'phoenix','auto')
        process_log=(OUT/'device-fixture.log').open('w')
        process=subprocess.Popen(list(map(str,[exe,'--background','--windowed','--live-control','--fixed-time','--no-capture',
            '--width','640','--height','480','--mode','showcase','--seconds','0','--output',work])),cwd=folder,env=env,
            stdout=process_log,stderr=process_log)
        control=ApplicationControl(application,exe,process,work);wait_for_bridge_endpoint(exe,process)
        initial=control.wait({'Backend':3});FixtureControl(work,process).set({'frozen':True,'reset_view':True})
        devices=(initial.get('helper') or initial.get('in_process') or {}).get('execution_devices',[])
        result['advertised_devices']=devices;result['addon_sha256']=digest(folder/'Phoenix.addon64')
        targets=[('native',0),('cpu',1),('cpu',os.cpu_count()),('cpu',0)]
        targets += [(d['id'],0) for d in devices if d['id'] not in ('native','cpu')]
        if only_device:targets=[target for target in targets if target[0].startswith(only_device)]
        for device,threads in targets:
            row={'device':device,'cpu_threads':threads,'passed':False};start=time.monotonic()
            try:
                request={'Backend':3,'ExecutionDevice':device,'CpuThreads':threads,'Experimental':False,'AutomaticBudget':False,
                    'WorkerEngine':'auto','ResolutionMode':0}
                control.call('set',request,timeout_seconds=20)
                deadline=time.monotonic()+(200 if device=='cpu' else 65);first=None;last={}
                while process.poll() is None and time.monotonic()<deadline:
                    snapshot=control.call(timeout_seconds=20)['snapshot'];last=snapshot
                    if snapshot.get('phase') in (5,6):raise AssertionError(snapshot.get('status','Terminal processing failure'))
                    if matches(snapshot.get('applied_settings') or {},request) and snapshot.get('selection_revision')==snapshot.get('presented_revision'):
                        info=snapshot.get('helper') or snapshot.get('in_process') or {}
                        execution=info.get('execution') or {}
                        if info.get('error'):raise AssertionError(info['error'])
                        if first is None:first=(snapshot.get('delivered',0),snapshot.get('presented',0))
                        elif snapshot.get('delivered',0)>=first[0]+2 and snapshot.get('presented',0)>first[1]:break
                    time.sleep(.25)
                else:raise AssertionError('No fresh presented result for requested device')
                row['execution_mode']=snapshot.get('execution_mode');row['execution']=execution
                if device=='native':assert execution.get('kind')=='native','Native selection silently changed engines'
                else:
                    assert execution.get('kind')!='native','Requested alternate device silently fell back to native'
                    assert execution.get('requested_device')==device,'Runtime device identity differs from requested device'
                row['helper_status']=info;row['portable_device']=snapshot.get('portable_device')
                image=control.screenshot();assert image.size==(640,480);row['output_sha256']=hashlib.sha256(np.asarray(image).tobytes()).hexdigest()
                row['passed']=True
            except Exception as error:
                row['error']=str(error)[-1600:]
                try:row['last_snapshot']=control.call(timeout_seconds=5).get('snapshot')
                except Exception as diagnostic:row['diagnostic_error']=str(diagnostic)
            row['seconds']=round(time.monotonic()-start,2);result['cases'].append(row);save()
            print(json.dumps({k:row.get(k) for k in ('device','cpu_threads','passed','seconds','error')}),flush=True)
    except Exception as error:
        result['error']=str(error)
        if process:result['fixture_exit_code']=process.poll()
        for file in folder.glob('ReShade.log*'):
            (OUT/'device-reshade.log').write_text(file.read_text(errors='replace')[-12000:])
    finally:
        if process:
            if control:control.remember_children()
            close_fixture(process)
            if control:control.cleanup_helpers()
            process_log.close()
        if installed:result['restore_exit_code']=command([application,'--restore',folder],accept_failure=True).returncode
        result['pending']=False;result['passed']=bool(result['cases']) and all(c['passed'] for c in result['cases']);save()
print(json.dumps({'passed':result['passed'],'cases':len(result['cases']),'error':result.get('error')}))
sys.exit(0 if result['passed'] else 1)
