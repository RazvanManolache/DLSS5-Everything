"""Build the optional, clearly marked ReShade software-device variant.

Normal ReShade is still downloaded from upstream. This variant only enables
software hooks when APP/IncludeSoftwareDevices is explicitly set by the installer.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
CHECKOUT = ROOT/'.build-deps/reshade-software'
REVISION = '18deaa52de0c425a78b329e9cb3c497281cd00ec'


def run(args, **kwargs):
    subprocess.run(list(map(str,args)),check=True,**kwargs)


def write_notices(destination):
    texts = []
    for folder in sorted((CHECKOUT/'deps').iterdir()):
        if not folder.is_dir(): continue
        for path in sorted(folder.iterdir()):
            if path.is_file() and path.name.upper().startswith(('LICENSE','COPYING')):
                texts.append(str(path.relative_to(CHECKOUT))+'\n\n'+path.read_text(errors='strict'))
    fpng = (CHECKOUT/'deps/fpng/src/fpng.cpp').read_text()
    marker = 'This is free and unencumbered software released into the public domain.'
    if marker not in fpng: raise ValueError('Pinned fpng notice changed')
    texts.append('deps/fpng/src/fpng.cpp (verbatim license tail)\n\n'+fpng[fpng.index(marker):])
    (destination/'DEPENDENCIES.txt').write_text('\n\n'.join(texts),encoding='utf-8')
    shutil.copy2(CHECKOUT/'LICENSE.md',destination/'LICENSE.md')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--notices-only',action='store_true')
    args = parser.parse_args()
    if not CHECKOUT.exists():
        run(['git','clone','--depth','1','--branch','v6.8.0','--recurse-submodules',
            '--shallow-submodules','https://github.com/crosire/reshade.git',CHECKOUT])
    revision = subprocess.check_output(['git','-C',str(CHECKOUT),'rev-parse','HEAD'],text=True).strip()
    if revision != REVISION:
        raise ValueError('Unexpected upstream revision; do not overwrite another checkout')
    if args.notices_only:
        write_notices(ROOT/'runtime/software-reshade')
        return
    sys.path.insert(0,str(ROOT/'tools'))
    from patch_software_reshade import patch
    patch(CHECKOUT)
    vswhere = Path(os.environ['ProgramFiles(x86)'])/'Microsoft Visual Studio/Installer/vswhere.exe'
    installation = Path(subprocess.check_output([str(vswhere),'-latest','-products','*',
        '-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath'],text=True).strip())
    msbuild = installation/'MSBuild/Current/Bin/MSBuild.exe'
    destination = ROOT/'runtime/software-reshade'
    destination.mkdir(parents=True,exist_ok=True)
    staging = ROOT/'local-build/software-reshade'
    staging.mkdir(parents=True,exist_ok=True)
    receipts = {'upstream':'https://github.com/crosire/reshade','revision':revision,
        'feature':'Opt-in APP/IncludeSoftwareDevices, preserves actual render adapter and API',
        'patch_sha256':hashlib.sha256((ROOT/'tools/patch_software_reshade.py').read_bytes()).hexdigest(),
        'files':{}}
    with tempfile.TemporaryDirectory(prefix='reshade-software-',dir=staging) as directory:
        work = Path(directory)
        for platform, suffix in [('x64','64'),('Win32','32')]:
            build = work/platform
            (build/'res/shaders').mkdir(parents=True)
            command = [msbuild,CHECKOUT/'ReShade.sln','/t:ReShade','/m:4','/v:minimal',
                '/p:Configuration=Release',f'/p:Platform={"64-bit" if platform=="x64" else "32-bit"}',
                '/p:PlatformToolset=v143',f'/p:PhoenixBuildRoot={build}']
            with (staging/f'reshade-software-{suffix}.log').open('w') as log:
                run(command,cwd=CHECKOUT,stdout=log,stderr=subprocess.STDOUT)
            binary = build/f'bin/{platform}/Release/ReShade{suffix}.dll'
            target = destination/f'ReShadeSoftware{suffix}.dll'
            shutil.copy2(binary,target)
            receipts['files'][target.name]=hashlib.sha256(target.read_bytes()).hexdigest()
        write_notices(destination)
        (destination/'BUILD.json').write_text(json.dumps(receipts,indent=2)+'\n')
    print(json.dumps(receipts),flush=True)


if __name__=='__main__':
    main()
