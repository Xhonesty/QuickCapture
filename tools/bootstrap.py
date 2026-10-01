"""Download a project-local SDK from Microsoft's release metadata."""
import hashlib
import json
import pathlib
import urllib.request
import zipfile
import time

root = pathlib.Path(__file__).resolve().parents[1]
meta = json.load(urllib.request.urlopen('https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'))
sdk = next(r['sdk'] for r in meta['releases'] if '-' not in r['sdk']['version'])
asset = next(f for f in sdk['files'] if f['rid'] == 'win-x64' and f['name'].endswith('.zip'))
print('SDK', sdk['version'], flush=True)
archive = root / '.tools' / 'sdk.zip'
archive.parent.mkdir(exist_ok=True)
for attempt in range(12):
    offset = archive.stat().st_size if archive.exists() else 0
    request = urllib.request.Request(asset['url'], headers={'Range': f'bytes={offset}-'} if offset else {})
    try:
        with urllib.request.urlopen(request, timeout=90) as response:
            append = offset > 0 and response.status == 206
            if not append:
                offset = 0
            expected = int(response.headers['Content-Length']) + offset
            with archive.open('ab' if append else 'wb') as dest:
                while chunk := response.read(1024 * 1024):
                    dest.write(chunk)
        if archive.stat().st_size == expected:
            break
    except Exception as exc:
        print('Retrying SDK download:', exc, flush=True)
    print('SDK downloaded bytes:', archive.stat().st_size, flush=True)
    time.sleep(1)
else:
    raise RuntimeError('SDK download incomplete')
assert hashlib.sha512(archive.read_bytes()).hexdigest().lower() == asset['hash'].lower(), 'SDK checksum mismatch'
print('Verified SDK archive; extracting', flush=True)
with zipfile.ZipFile(archive) as z:
    z.extractall(root / '.tools' / 'dotnet')
archive.unlink()
print('SDK ready', flush=True)
