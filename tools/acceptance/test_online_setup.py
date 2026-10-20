"""Exercise the real public component bootstrap in an empty, temporary user area."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, tempfile, time

APP = Path(__file__).resolve().parents[2]
OUT = APP / 'local-build/validation-current'
result = {'scope': 'Real online first-run component update; no local Phoenix import or source build', 'passed': False}
start = time.monotonic()
try:
    with tempfile.TemporaryDirectory(prefix='feature-online-', dir=APP/'local-build') as directory:
        root = Path(directory)
        payload = root/'payload'
        env = os.environ.copy()
        env['LOCALAPPDATA'] = str(root/'user')
        Path(env['LOCALAPPDATA']).mkdir()
        with (OUT/'online-setup.log').open('w', encoding='utf-8') as log:
            process = subprocess.run([str(APP/'app/Dlss5CompatApp/bin/Debug/net9.0-windows/Dlss5CompatApp.exe'),
                '--components-update', '--payload', str(payload)], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=360)
        result['exit_code'] = process.returncode
        result['files'] = [{ 'path': str(p.relative_to(payload)), 'bytes': p.stat().st_size,
            'sha256': hashlib.file_digest(p.open('rb'), 'sha256').hexdigest() }
            for p in payload.rglob('*') if p.is_file() and '.download-cache' not in p.parts]
        receipt = payload/'phoenix-release.json'
        if receipt.is_file(): result['phoenix_release'] = json.loads(receipt.read_text(encoding='utf-8-sig'))
        result['passed'] = process.returncode == 0
except Exception as error:
    result['error'] = str(error)
result['seconds'] = round(time.monotonic()-start, 2)
(OUT/'online-setup.json').write_text(json.dumps(result, indent=2))
print(json.dumps({k:v for k,v in result.items() if k not in ('files','phoenix_release')}))
sys.exit(0 if result['passed'] else 1)
