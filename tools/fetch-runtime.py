import concurrent.futures
import pathlib
import urllib.request

root = pathlib.Path(__file__).resolve().parents[1]
feed = root / '.tools' / 'feed'
feed.mkdir(parents=True, exist_ok=True)
runtime = next((root / '.tools' / 'dotnet' / 'shared' / 'Microsoft.NETCore.App').iterdir()).name

def download(package):
    name = package.lower()
    path = feed / f'{name}.{runtime}.nupkg'
    url = f'https://api.nuget.org/v3-flatcontainer/{name}/{runtime}/{name}.{runtime}.nupkg'
    for attempt in range(10):
        offset = path.stat().st_size if path.exists() else 0
        req = urllib.request.Request(url, headers={'Range': f'bytes={offset}-'} if offset else {})
        try:
            with urllib.request.urlopen(req, timeout=90) as r:
                append = offset > 0 and r.status == 206
                expected = int(r.headers['Content-Length']) + (offset if append else 0)
                with path.open('ab' if append else 'wb') as f:
                    while chunk := r.read(1024 * 1024):
                        f.write(chunk)
            if path.stat().st_size == expected:
                print('Downloaded', package, runtime, flush=True)
                return
        except Exception as exc:
            print('Retry', package, str(exc), flush=True)
    raise RuntimeError(f'Incomplete package: {package}')

with concurrent.futures.ThreadPoolExecutor(max_workers=3) as executor:
    list(executor.map(download, ['Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64', 'Microsoft.AspNetCore.App.Runtime.win-x64']))
