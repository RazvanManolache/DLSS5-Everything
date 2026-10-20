"""Rebuild integration code around the verified release's model-free payload.

The resulting candidate is explicitly different from the public 0.81 baseline.
No model or vendor DLL is copied into the candidate package.
"""
from pathlib import Path
import hashlib, json, shutil, sys, tempfile, zipfile

APP = Path(__file__).resolve().parents[2]
OUT = APP/'local-build/validation-current'
BASE = APP.parent
PROJECT = BASE/'dlss5-phoenix-addon'
sys.path.insert(0, str(BASE/'dlss5-everything-testing/tools'))
from live_candidate import candidate_with_telemetry

archive = PROJECT/'artifacts/current/release-0.81/DLSS5-Phoenix-0.81-Windows.zip'
metadata = json.loads((OUT/'github-release.json').read_text(encoding='utf-8-sig'))
expected = next(a['digest'] for a in metadata['assets'] if a['name']==archive.name)
with archive.open('rb') as stream:
    assert 'sha256:'+hashlib.file_digest(stream,'sha256').hexdigest()==expected
destination = PROJECT/'artifacts/current/everything-fixes'
destination.mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(prefix='integration-fixes-',dir=PROJECT/'build') as directory:
    scratch=Path(directory); baseline=scratch/'release';baseline.mkdir()
    with zipfile.ZipFile(archive) as z:z.extractall(baseline)
    # Other tasks may edit this shared checkout. Compile/test an immutable copy,
    # retaining source hashes rather than weakening the build identity guard.
    snapshot=scratch/'source';snapshot.mkdir()
    identities={}
    for name in ('src','native_runtime','third_party','tools','tests'):
        for source in (PROJECT/name).rglob('*'):
            if not source.is_file() or '__pycache__' in source.parts:continue
            if source.is_symlink() or source.is_junction():raise RuntimeError('Linked source is not a snapshot input')
            relative=source.relative_to(PROJECT);target=snapshot/relative;target.parent.mkdir(parents=True,exist_ok=True)
            content=source.read_bytes();target.write_bytes(content);identities[relative.as_posix()]=hashlib.sha256(content).hexdigest()
    for source in PROJECT.iterdir():
        if source.is_file() and source.suffix in ('.json','.txt','.md','.h'):
            (snapshot/source.name).write_bytes(source.read_bytes())
    (snapshot/'build').mkdir()
    (OUT/'fixed-candidate-source.json').write_text(json.dumps(identities,indent=2))
    with candidate_with_telemetry(snapshot,baseline,BASE/'dlss5-everything-testing/games/dx11-x64/dxgi.dll',
        BASE/'external/Streamline/external/ngx-sdk/include',scratch) as (package,receipt):
        for name in ('Phoenix.addon64','Phoenix.addon32','phoenix_graphics_host.exe','FILES_SHA256.json'):
            temporary=destination/(name+'.incoming')
            shutil.copy2(package/name,temporary);temporary.replace(destination/name)
        receipt.update(public_baseline='0.81',native_kernels_rebuilt=False,model_included=False,
            source_changes='Current integration fixes and preserved checkout edits; not the unchanged public release')
        (destination/'validation.json').write_text(json.dumps(receipt,indent=2))
        (OUT/'fixed-candidate-build.json').write_text(json.dumps(receipt,indent=2))
print(destination)
