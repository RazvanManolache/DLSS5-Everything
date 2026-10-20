"""Keep public addon/core/runners, replace only their embedded helper executable."""
from pathlib import Path
import ctypes as C, hashlib, json, os, shutil, subprocess, sys, tempfile, zipfile

APP=Path(__file__).resolve().parents[2];BASE=APP.parent;PROJECT=BASE/'dlss5-phoenix-addon';OUT=APP/'local-build/validation-current'
sys.path.insert(0,str(PROJECT/'tools'))
from validate_portable_bridge import resources
from embed_runtime import compress
from prepare_addon_release import decompress

def digest(data):return hashlib.sha256(data).hexdigest()
def replace_resources(binary, updates):
    k=C.WinDLL('kernel32',use_last_error=True)
    k.BeginUpdateResourceW.argtypes=[C.c_wchar_p,C.c_bool];k.BeginUpdateResourceW.restype=C.c_void_p
    k.UpdateResourceW.argtypes=[C.c_void_p,C.c_void_p,C.c_void_p,C.c_ushort,C.c_void_p,C.c_uint];k.UpdateResourceW.restype=C.c_bool
    k.EndUpdateResourceW.argtypes=[C.c_void_p,C.c_bool];k.EndUpdateResourceW.restype=C.c_bool
    k.LoadLibraryExW.argtypes=[C.c_wchar_p,C.c_void_p,C.c_uint];k.LoadLibraryExW.restype=C.c_void_p
    callback=C.WINFUNCTYPE(C.c_bool,C.c_void_p,C.c_void_p,C.c_void_p,C.c_ushort,C.c_ssize_t)
    k.EnumResourceLanguagesW.argtypes=[C.c_void_p,C.c_void_p,C.c_void_p,callback,C.c_ssize_t]
    k.FreeLibrary.argtypes=[C.c_void_p]
    module=k.LoadLibraryExW(str(binary),None,0x22);assert module
    languages=[]
    @callback
    def found(module,kind,name,language,parameter):languages.append(language);return True
    try:assert k.EnumResourceLanguagesW(module,10,1001,found,0)
    finally:k.FreeLibrary(module)
    handle=k.BeginUpdateResourceW(str(binary),False);assert handle
    try:
        for name,data in updates.items():
            buffer=C.create_string_buffer(data)
            for language in languages:
                if not k.UpdateResourceW(handle,10,name,language,buffer,len(data)):raise C.WinError(C.get_last_error())
    except BaseException:
        k.EndUpdateResourceW(handle,True);raise
    if not k.EndUpdateResourceW(handle,False):raise C.WinError(C.get_last_error())

archive=PROJECT/'artifacts/current/release-0.81/DLSS5-Phoenix-0.81-Windows.zip'
release=json.loads((OUT/'github-release.json').read_text(encoding='utf-8-sig'))
with archive.open('rb') as stream:assert 'sha256:'+hashlib.file_digest(stream,'sha256').hexdigest()==next(a['digest'] for a in release['assets'] if a['name']==archive.name)
destination=PROJECT/'artifacts/current/everything-fixes';destination.mkdir(exist_ok=True)
receipt={'candidate_kind':'public-0.81-helper-hotfix','public_baseline':'0.81','core_rebuilt':False,'runners_rebuilt':False,'kernels_rebuilt':False,'model_included':False}
with tempfile.TemporaryDirectory(prefix='release-hotfix-',dir=PROJECT/'build') as directory:
    root=Path(directory)
    with zipfile.ZipFile(archive) as z:z.extractall(root/'release')
    if '--base-candidate' in sys.argv:
        base=Path(sys.argv[sys.argv.index('--base-candidate')+1]).resolve()
        previous=json.loads((base/'validation.json').read_text())
        hashes=json.loads((base/'FILES_SHA256.json').read_text())
        assert previous['source_build_passed'] and previous['package_build_passed']
        for bits in (64,32):
            name=f'Phoenix.addon{bits}';assert digest((base/name).read_bytes())==hashes[name]
            shutil.copy2(base/name,root/'release'/name)
        receipt.update(previous)
        receipt['helper_refreshed']=True
    vswhere=Path(os.environ['ProgramFiles(x86)'])/'Microsoft Visual Studio/Installer/vswhere.exe'
    studio=subprocess.check_output([str(vswhere),'-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath'],text=True).strip()
    helper=root/'phoenix_graphics_host.exe'
    recipe=root/'build.cmd'
    recipe.write_text(f'@echo off\ncall "{studio}/VC/Auxiliary/Build/vcvars64.bat" > "{root}/toolchain.log" 2>&1\n'
        f'cl /nologo /std:c++20 /Zc:__cplusplus /O2 /EHsc /MT /DNOMINMAX /I"{PROJECT}/third_party" /I"{PROJECT}/third_party/reshade" '
        f'"{PROJECT}/src/integration/compatibility/host/dlss5-feed-host64.cpp" /Fo:"{root}/host.obj" /Fe:"{helper}" '
        '/link d3d12.lib dxgi.lib d3dcompiler.lib version.lib user32.lib gdi32.lib advapi32.lib ole32.lib windowscodecs.lib\nexit /b %errorlevel%\n')
    with (OUT/'release-hotfix-compile.log').open('w') as log:
        subprocess.run(['cmd','/d','/c',str(recipe)],cwd=root,stdout=log,stderr=subprocess.STDOUT,check=True)
    receipt['helper_sha256']=digest(helper.read_bytes())
    receipt['helper_source_sha256']=digest((PROJECT/'src/integration/compatibility/host/dlss5-feed-host64.cpp').read_bytes())
    for bits in (64,32):
        name=f'Phoenix.addon{bits}';binary=root/'release'/name;packed=root/f'resources-{bits}';packed.mkdir();resources(binary,packed)
        manifest=json.loads((packed/'1001.bin').read_text());updates={};original_hashes={entry['path']:entry['sha256'] for entry in manifest['files']}
        names=set(original_hashes)
        for entry in manifest['files']:
            data=None
            if entry['path']=='compatibility/phoenix_graphics_host.exe':data=helper.read_bytes()
            elif entry['path']=='runtime/phoenix_native_cpp.json':
                raw=(packed/f"{entry['resource']}.bin").read_bytes()
                config=json.loads(decompress(raw,entry['bytes']) if entry['algorithm']=='xpress_huff' else raw)
                icd=config.get('private_opencl_cpu')
                if icd and 'runtime/'+icd not in names:
                    config['devices']=[device for device in config.get('devices',[]) if device['id']!='sycl:cpu']
                    config['validated_devices']=[device for device in config.get('validated_devices',[]) if device!='sycl:cpu']
                    config['unavailable_devices']={'sycl:cpu':'Optional Intel CPU ICD is not included in this package'}
                    data=json.dumps(config,indent=2).encode()
                    receipt['sycl_unavailable_reason']=config['unavailable_devices']['sycl:cpu']
            if data is not None:
                entry.update(bytes=len(data),sha256=digest(data),algorithm='xpress_huff')
                updates[entry['resource']]=compress(data)
                entry['packed_bytes']=len(updates[entry['resource']])
        updates[1001]=json.dumps(manifest,indent=2).encode();replace_resources(binary,updates)
        checked=root/f'checked-{bits}';checked.mkdir();resources(binary,checked)
        verified=json.loads((checked/'1001.bin').read_text())
        for entry in verified['files']:
            raw=(checked/f"{entry['resource']}.bin").read_bytes();data=decompress(raw,entry['bytes']) if entry['algorithm']=='xpress_huff' else raw
            assert digest(data)==entry['sha256']
            if entry['path'] not in ('compatibility/phoenix_graphics_host.exe','runtime/phoenix_native_cpp.json'):
                assert entry['sha256']==original_hashes[entry['path']]
        shutil.copy2(binary,destination/name)
    shutil.copy2(helper,destination/helper.name)
    files={name:digest((destination/name).read_bytes()) for name in ('Phoenix.addon64','Phoenix.addon32','phoenix_graphics_host.exe')}
    receipt['addon_sha256']=files['Phoenix.addon64']
    receipt['addon32_sha256']=files['Phoenix.addon32']
    (destination/'FILES_SHA256.json').write_text(json.dumps(files,indent=2))
    receipt.update(source_build_passed=True,package_build_passed=True,payload_identity_verified=True)
    (destination/'validation.json').write_text(json.dumps(receipt,indent=2));(OUT/'release-hotfix-build.json').write_text(json.dumps(receipt,indent=2))
    if '--base-candidate' in sys.argv:(OUT/'release-source-fixes-build.json').write_text(json.dumps(receipt,indent=2))
print(json.dumps(receipt))
