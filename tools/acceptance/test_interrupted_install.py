"""Kill only an owned installer during its atomic model copy; verify recovery."""
from pathlib import Path
import hashlib, json, os, shutil, subprocess, sys, tempfile, time, zipfile
APP=Path(__file__).resolve().parents[2];OUT=APP/'local-build/validation-current';BASE=APP.parent;TESTING=BASE/'dlss5-everything-testing'
application=APP/'app/Dlss5CompatApp/bin/Debug/net9.0-windows/Dlss5CompatApp.exe'
result={'scope':'Actual owned installer process termination, temporary synthetic game and released addon', 'passed':False}
def digest(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
try:
    with tempfile.TemporaryDirectory(prefix='feature-recovery-',dir=APP/'local-build') as directory:
        root=Path(directory);package=root/'package';package.mkdir();payload=root/'payload';payload.mkdir();folder=root/'game';folder.mkdir()
        archive=BASE/'dlss5-phoenix-addon/artifacts/current/release-0.81/DLSS5-Phoenix-0.81-Windows.zip'
        expected=next(a['digest'] for a in json.loads((OUT/'github-release.json').read_text(encoding='utf-8-sig'))['assets'] if a['name']==archive.name)
        assert 'sha256:'+digest(archive)==expected
        with zipfile.ZipFile(archive) as z:z.extractall(package)
        shutil.copy2(TESTING/'games/dx11-x64/dxgi.dll',payload/'ReShade64.dll')
        env=os.environ.copy();env['LOCALAPPDATA']=str(root/'user');Path(env['LOCALAPPDATA']).mkdir()
        imported=subprocess.run([str(application),'--components-import','--payload',str(payload),'--phoenix',str(package/'Phoenix.addon64'),
            '--model',str(BASE/'_dlls/nvngx_dlssnr.dll')],env=env,capture_output=True,timeout=60)
        assert imported.returncode==0,imported.stderr.decode(errors='replace')
        matrix=json.loads((TESTING/'matrix.json').read_text());game=next(g for g in matrix['games'] if g['id']=='dx12-x64')
        original=TESTING/'clean'/game['executable'];exe=folder/original.name;shutil.copy2(original,exe)
        (folder/'notes.txt').write_text('Preserve unrelated files byte-for-byte.\n')
        (folder/'ReShade.ini').write_text('[GENERAL]\nMyOriginalSetting=preserve\n')
        originals={p.name:digest(p) for p in folder.iterdir() if p.is_file()}
        with (OUT/'interrupted-install.log').open('w') as log:
            process=subprocess.Popen([str(application),'--install',str(exe),'--payload',str(payload),'--offline'],env=env,
                stdout=log,stderr=log)
            try:
                deadline=time.monotonic()+30;observed=None
                while process.poll() is None and time.monotonic()<deadline:
                    partial=list(folder.glob('PhoenixModel.dll.phoenix-*.tmp'))
                    if partial:
                        observed=partial[0].name;process.kill();process.wait(timeout=10);break
                    time.sleep(.001)
                result['killed_mid_copy']=observed is not None
                result['observed_temporary_file']=observed
                if observed is None:
                    process.wait(timeout=30)
                    raise AssertionError('Installer finished before model-copy interruption was observed')
            finally:
                if process.poll() is None:process.kill();process.wait(timeout=10)
        result['pending_journal_present']=(folder/'_DLSS5_Compat_Backup/pending.json').is_file()
        restored=subprocess.run([str(application),'--restore',str(folder)],env=env,capture_output=True,text=True,timeout=60)
        (OUT/'interrupted-restore.log').write_text(restored.stdout+'\n'+restored.stderr)
        result['restore_exit_code']=restored.returncode
        result['originals_preserved']=all((folder/name).is_file() and digest(folder/name)==sha for name,sha in originals.items())
        result['active_addons_removed']=not list(folder.glob('*.addon64'))
        result['untracked_temporary_files']=[{'name':p.name,'bytes':p.stat().st_size} for p in folder.glob('*.phoenix-*.tmp')]
        result['passed']=bool(result['pending_journal_present'] and restored.returncode==0 and result['originals_preserved']
            and result['active_addons_removed'] and not result['untracked_temporary_files'])
except Exception as error:result['error']=str(error)
(OUT/'interrupted-install.json').write_text(json.dumps(result,indent=2));print(json.dumps(result))
sys.exit(0 if result['passed'] else 1)
