"""Measure current helper delivery with real controls and returned-pixel checks."""
from pathlib import Path
import json, os, shutil, subprocess, sys, tempfile
APP=Path(__file__).resolve().parents[2];BASE=APP.parent;TESTING=BASE/'dlss5-everything-testing';OUT=APP/'local-build/validation-current'
sys.path.insert(0,str(TESTING/'tools'))
from run_live_regressions import ApplicationControl,FixtureControl,command,wait_for_bridge_endpoint,observe,pixel_delta
from live_assertions import progress
from run_backends import read,close_fixture,digest
from asset_paths import point_assets,find_assets
from run_execution_sweep import configure
from payload import stage

application=APP/'app/Dlss5CompatApp/bin/Debug/net9.0-windows/Dlss5CompatApp.exe'
visible='--visible' in sys.argv
identities=sys.argv[sys.argv.index('--only')+1:] if '--only' in sys.argv else ['vulkan-x86','dx11-x64','dx12-x64']
result={'passed':False,'scope':'1080p synthetic application delivery, not kernel throughput or retail-game acceptance','cases':[]}
receipt=OUT/('fixed-transport-visible.json' if visible else 'fixed-transport.json')
result['visible_fixture']=visible
def save():receipt.write_text(json.dumps(result,indent=2))
with tempfile.TemporaryDirectory(prefix='fixed-transport-',dir=APP/'local-build') as directory:
    root=Path(directory);package=root/'package';package.mkdir();payload=root/'payload';payload.mkdir()
    result.update(stage(package,APP));save()
    for bits,source in [(64,TESTING/'games/dx11-x64/dxgi.dll'),(32,TESTING/'games/dx9-x86/d3d9.dll')]:shutil.copy2(source,payload/f'ReShade{bits}.dll')
    command([application,'--components-import','--payload',payload,'--phoenix',package/'Phoenix.addon64','--model',BASE/'_dlls/nvngx_dlssnr.dll'])
    matrix={game['id']:game for game in read(TESTING/'matrix.json')['games']}
    for identity in identities:
        game=matrix[identity];folder=root/identity;folder.mkdir();work=folder/'measurement';work.mkdir()
        original=TESTING/'clean'/game['executable'];exe=folder/original.name;shutil.copy2(original,exe);point_assets(folder,find_assets(original.parent))
        for file in original.parent.glob('*.dll'):shutil.copy2(file,folder/file.name)
        process=None;control=None;installed=False;row={'id':identity,'passed':False}
        try:
            command([application,'--install',exe,'--payload',payload,'--offline']);installed=True;configure(folder,'phoenix','auto')
            env=os.environ.copy();manifest=read(folder/'_DLSS5_Compat_Backup/manifest.json');env.update(manifest.get('VulkanLaunchEnvironment') or {})
            arguments=[exe,'--windowed','--live-control','--fixed-time','--no-capture','--width','1920','--height','1080','--mode','showcase','--seconds','0','--output',work]
            if not visible:arguments.append('--background')
            process=subprocess.Popen(list(map(str,arguments)),cwd=folder,env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            control=ApplicationControl(application,exe,process,work);wait_for_bridge_endpoint(exe,process);control.wait({'Backend':3},timeout=65,request_timeout=20)
            FixtureControl(work,process).set({'frozen':True,'reset_view':True})
            control.set({'OverrideStrengths':True,'Tone':0.,'Structure':0.});low=control.screenshot()
            control.set({'Tone':2.,'Structure':2.});high=control.screenshot();row['appearance']=pixel_delta(low,high)
            assert row['appearance']['central_mae']>.25,'No measurable control effect on presented output'
            control.set({'OverrideStrengths':False,'Tone':1.,'Structure':1.})
            samples=observe(control,12,pair_ids=False);row.update(progress(samples));row['samples']=samples
            row['last_snapshot']=control.call()['snapshot'];row['addon_sha256']=digest(folder/('Phoenix.addon32' if game['architecture']=='x86' else 'Phoenix.addon64'));row['passed']=True
        except Exception as error:row['error']=str(error)
        finally:
            if process:
                if control:control.remember_children()
                close_fixture(process)
                if control:control.cleanup_helpers()
                row['exit_code']=process.returncode
                if process.returncode:row['passed']=False
            if installed:row['restore_exit_code']=command([application,'--restore',folder],accept_failure=True).returncode
            result['cases'].append(row);save();print(json.dumps({k:row.get(k) for k in ('id','passed','fresh_output_fps','error')}),flush=True)
result['passed']=bool(result['cases']) and all(row['passed'] for row in result['cases']);save()
raise SystemExit(0 if result['passed'] else 1)
