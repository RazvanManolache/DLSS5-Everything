"""Build process-local legacy COM compatibility; CPU-only contracts on x86/x64."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import pefile

ROOT=Path(__file__).resolve().parents[1]

def toolchain(bits):
    finder=Path(os.environ['ProgramFiles(x86)'])/'Microsoft Visual Studio/Installer/vswhere.exe'
    studio=subprocess.check_output([str(finder),'-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath'],text=True).strip()
    setup=Path(studio)/f'VC/Auxiliary/Build/vcvars{bits}.bat'
    values=subprocess.check_output(f'cmd.exe /d /s /c ""{setup}" >nul && set"',text=True)
    env={k.upper():v for k,v in os.environ.items()}
    for line in values.splitlines():
        key,sep,value=line.partition('=')
        if sep and key:env[key.upper()]=value
    return shutil.which('cl.exe',path=env['PATH']),env

def invoke(command,cwd,env):
    result=subprocess.run([str(x) for x in command],cwd=cwd,env=env,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+'\n'+result.stderr)
    return result.stdout

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--payload',type=Path);args=parser.parse_args()
    output=ROOT/'runtime/legacy-com';output.mkdir(exist_ok=True,parents=True)
    (ROOT/'build').mkdir(exist_ok=True)
    report={'passed':False,'cpu_only':True,'architectures':{}}
    with tempfile.TemporaryDirectory(prefix='legacy_com_',dir=ROOT/'build') as temporary:
        for bits in (32,64):
            work=Path(temporary)/str(bits);work.mkdir();compiler,env=toolchain(bits)
            common=[compiler,'/nologo','/std:c++20','/EHsc','/MT','/O2']
            test=work/'contracts.exe'
            invoke(common+[ROOT/'tests/legacy-com-tests.cpp',f'/Fo:{work/"contracts.obj"}',f'/Fe:{test}','ole32.lib','advapi32.lib','uuid.lib'],work,env)
            contracts=json.loads(invoke([test],work,env));assert contracts['passed']
            if not args.payload:raise RuntimeError('--payload supplies architecture-matched dgVoodoo export contracts')
            wrapped=args.payload.resolve()/'MS'/('x86' if bits==32 else 'x64')/'DDraw.dll'
            wrapped_pe=pefile.PE(str(wrapped));assert wrapped_pe.FILE_HEADER.Machine==(0x14c if bits==32 else 0x8664)
            exports=[];target_exports=[];expected_exports=set()
            for symbol in wrapped_pe.DIRECTORY_ENTRY_EXPORT.symbols:
                if not symbol.name:raise RuntimeError('Unnamed dgVoodoo export requires explicit forwarding support')
                name=symbol.name.decode('ascii');exports.append(f' {name}=PhoenixDgVoodooDDraw.{name} @{symbol.ordinal}')
                target_exports.append(f' {name} @{symbol.ordinal}');expected_exports.add((symbol.name,symbol.ordinal))
            wrapped_pe.close()
            definition=work/'forward.def';definition.write_text('LIBRARY DDraw\nEXPORTS\n'+'\n'.join(exports)+'\n')
            target_definition=work/'target.def';target_definition.write_text('LIBRARY PhoenixDgVoodooDDraw\nEXPORTS\n'+'\n'.join(target_exports)+'\n')
            target_library=work/'target.lib'
            invoke([shutil.which('lib.exe',path=env['PATH']),'/nologo',f'/DEF:{target_definition}',f'/MACHINE:{"X86" if bits==32 else "X64"}',f'/OUT:{target_library}'],work,env)
            dll=work/'DDraw.dll';name=f'PhoenixLegacyDDraw{bits}.dll'
            invoke(common+['/LD',ROOT/'source/src/phoenix-legacy-com.cpp',f'/Fo:{work/"legacy.obj"}',f'/Fe:{dll}',target_library,'/link','/INCREMENTAL:NO',f'/DEF:{definition}','ole32.lib','advapi32.lib','uuid.lib','user32.lib'],work,env)
            actual_pe=pefile.PE(str(dll));actual=actual_pe.DIRECTORY_ENTRY_EXPORT.symbols
            assert {(x.name,x.ordinal) for x in actual}==expected_exports
            assert all(x.forwarder and x.forwarder.startswith(b'PhoenixDgVoodooDDraw.') for x in actual)
            actual_pe.close();shutil.copy2(dll,output/name)
            report['architectures'][str(bits)]={'contracts':contracts,'sha256':hashlib.sha256(dll.read_bytes()).hexdigest()}
    report['passed']=True;(output/'validation.json').write_text(json.dumps(report,indent=2)+'\n')
    if args.payload:
        target=args.payload.resolve()/'legacy-com';target.mkdir(parents=True,exist_ok=True)
        for name in ('PhoenixLegacyDDraw32.dll','PhoenixLegacyDDraw64.dll','validation.json'):shutil.copy2(output/name,target/name)
    print(json.dumps(report))

if __name__=='__main__':main()
