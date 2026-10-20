"""Select a verified public baseline or an explicitly identified fixed candidate."""
from pathlib import Path
import hashlib, json, shutil, sys, zipfile


def stage(package, app):
    base=app.parent
    index=next((i for i,v in enumerate(sys.argv) if v=='--candidate'),None)
    if index is not None:
        candidate=Path(sys.argv[index+1]).resolve()
        receipt=json.loads((candidate/'validation.json').read_text())
        assert receipt['source_build_passed'] and receipt['package_build_passed']
        files=json.loads((candidate/'FILES_SHA256.json').read_text())
        for name,expected in files.items():
            source=(candidate/name).resolve();assert source.is_relative_to(candidate)
            with source.open('rb') as stream:assert hashlib.file_digest(stream,'sha256').hexdigest()==expected
            target=package/name;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,target)
        return {'addon_rebuilt':True,'baseline':'explicit fixed integration candidate','candidate':str(candidate),
                'candidate_validation':receipt}
    archive=base/'dlss5-phoenix-addon/artifacts/current/release-0.81/DLSS5-Phoenix-0.81-Windows.zip'
    metadata=json.loads((app/'local-build/validation-current/github-release.json').read_text(encoding='utf-8-sig'))
    expected=next(a['digest'] for a in metadata['assets'] if a['name']==archive.name)
    with archive.open('rb') as stream:assert 'sha256:'+hashlib.file_digest(stream,'sha256').hexdigest()==expected
    with zipfile.ZipFile(archive) as z:z.extractall(package)
    return {'addon_rebuilt':False,'baseline':'unchanged public Phoenix 0.81'}
