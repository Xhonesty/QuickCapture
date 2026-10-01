import pathlib
import urllib.request
import json

root = pathlib.Path(__file__).resolve().parents[1]
base = 'https://raw.githubusercontent.com/sskodje/ScreenRecorderLib/master/'
for name, path in {'Options.h':'ScreenRecorderLib/Options.h', 'RecordingSources.h':'ScreenRecorderLib/RecordingSources.h', 'upstream.md':'README.md', 'upstream-license.txt':'LICENSE'}.items():
    try:
        (root / 'docs' / name).write_bytes(urllib.request.urlopen(base + path).read())
        print(name, flush=True)
    except Exception as e:
        print(e, flush=True)
versions = json.load(urllib.request.urlopen('https://api.nuget.org/v3-flatcontainer/screenrecorderlib/index.json'))['versions']
print('Recorder versions:', versions[-12:], flush=True)
