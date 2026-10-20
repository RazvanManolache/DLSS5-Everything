"""Qualify the fixed package across the 7 graphics APIs and both bitnesses."""
from pathlib import Path
import json,os,subprocess,sys,tempfile,time
from payload import stage
APP=Path(__file__).resolve().parents[2];BASE=APP.parent;OUT=APP/'local-build/validation-current'
ADDON=BASE/'dlss5-phoenix-addon';TESTING=BASE/'dlss5-everything-testing'
with tempfile.TemporaryDirectory(prefix='fixed-api-',dir=ADDON/'build') as directory:
    work=Path(directory);package=work/'package';package.mkdir();identity=stage(package,APP);target=work/'results'
    env=os.environ.copy();env['LOCALAPPDATA']=str(work/'user');Path(env['LOCALAPPDATA']).mkdir()
    args=[sys.executable,ADDON/'tools/test_graphics_apis.py','--package',package,'--weight-source',ADDON/'artifacts/current/weight-source.json',
          '--reshade32',TESTING/'games/dx9-x86/d3d9.dll','--reshade64',TESTING/'games/dx11-x64/dxgi.dll','--output',target,
          '--apis','d3d9','d3d10','d3d10_1','d3d11','d3d12','opengl','vulkan','--vulkan-headers',BASE/'external/OptiScaler/external/vulkan/include',
          '--control-checks','--resize-checks','--motion-checks','--multiwindow-check']
    start=time.monotonic()
    with (OUT/'fixed-graphics.log').open('w',encoding='utf-8') as log:
        completed=subprocess.run(list(map(str,args)),cwd=ADDON,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=1500)
    result={'passed':completed.returncode==0,'exit_code':completed.returncode,'seconds':time.monotonic()-start,**identity}
    if (target/'report.json').is_file():result.update(json.loads((target/'report.json').read_text()))
    errors=[]
    for file in target.glob('*.build.log'):
        text=file.read_text(errors='replace')
        if 'error ' in text:errors.append({'file':file.name,'error':text[-2400:]})
    result['build_errors']=errors
    (OUT/'fixed-graphics.json').write_text(json.dumps(result,indent=2))
print(json.dumps({'passed':result['passed'],'seconds':result['seconds'],'error':result.get('error')}))
raise SystemExit(0 if result['passed'] else 1)
