"""Prepare or collect the pinned, project-local Windows shared FFmpeg build."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / '.tools/media-native'
DOWNLOADS = ROOT / '.tools/media-download'
VENDOR = ROOT / 'vendor/media'


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def extract(archive, destination, source=False):
    destination.mkdir(parents=True, exist_ok=True)
    tar = str(Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/tar.exe')
    names = subprocess.check_output([tar, '-tf', str(archive)], text=True).splitlines()
    for name in names:
        normalized = name.replace('\\', '/')
        if normalized.startswith('/') or ':' in normalized or '..' in normalized.split('/'):
            raise ValueError('Unsafe archive path: ' + name)
    arguments = [tar, '-xf', str(archive), '-C', str(destination)]
    if source:
        arguments += ['--strip-components=1']
    subprocess.run(arguments, check=True)


def prepare(offline):
    inputs = json.loads((VENDOR / 'build-inputs.json').read_text())
    DOWNLOADS.mkdir(parents=True, exist_ok=True)
    for item in inputs['archives']:
        archive = DOWNLOADS / item.get('archiveName', item['url'].rsplit('/', 1)[1])
        if not archive.exists():
            if offline:
                raise FileNotFoundError('Offline source archive missing: ' + str(archive))
            temporary = archive.with_suffix(archive.suffix + '.download')
            urllib.request.urlretrieve(item['url'], temporary)
            if sha256(temporary) != item['sha256']:
                temporary.unlink()
                raise ValueError('Download SHA-256 mismatch: ' + item['name'])
            temporary.replace(archive)
        if sha256(archive) != item['sha256']:
            raise ValueError('Source SHA-256 mismatch: ' + item['name'])
        extract(archive, NATIVE / ('source' if item['name'] == 'ffmpeg' else 'deps'), item['name'] == 'ffmpeg')
        print('Verified build input:', item['name'], flush=True)


def collect():
    output = NATIVE / 'output/bin'
    deps = NATIVE / 'deps/ucrt64/bin'
    gcc = shutil.which('gcc')
    if not gcc:
        raise FileNotFoundError('MinGW-w64 gcc is required')
    objdump = Path(gcc).with_name('objdump.exe')
    candidates = {p.name.lower(): p for directory in [output, deps, Path(gcc).parent]
                  for p in directory.glob('*') if p.is_file()}
    selected = {}
    pending = ['ffmpeg.exe', 'ffprobe.exe']
    while pending:
        name = pending.pop().lower()
        if name in selected:
            continue
        path = candidates[name]
        selected[name] = path
        imports = re.findall(r'DLL Name: (\S+)', subprocess.check_output([str(objdump), '-p', str(path)], text=True))
        for imported in imports:
            key = imported.lower()
            if key in candidates:
                pending.append(key)
            elif not (Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32' / imported).exists() and not key.startswith('api-ms-'):
                raise FileNotFoundError('Unresolved media runtime dependency: ' + imported)
    files = []
    for name, source in sorted(selected.items()):
        target = VENDOR / source.name
        shutil.copy2(source, target)
        files.append({'name': target.name, 'size': target.stat().st_size, 'sha256': sha256(target)})
    # Only remove earlier native media outputs, never material or user data.
    for path in VENDOR.iterdir():
        if path.suffix.lower() in ('.exe', '.dll') and path.name.lower() not in selected:
            if path.is_symlink() or path.resolve().parent != VENDOR.resolve():
                raise ValueError('Unexpected media output path')
            path.unlink()
    inputs = json.loads((VENDOR / 'build-inputs.json').read_text())
    manifest = {'version': '9.0.2-quickcapture-shared', 'layout': 'shared',
                'license': 'GPL-3.0-or-later', 'buildSource': 'tools/build-media.ps1',
                'ffmpegSource': inputs['ffmpegSource'], 'buildInputs': 'build-inputs.json',
                'files': files}
    archive = ROOT / 'artifacts/media/QuickCapture-media-9.0.2-shared-x64.zip'
    archive.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
        for item in files:
            bundle.write(VENDOR / item['name'], item['name'])
    manifest['archiveName'] = archive.name
    manifest['archiveSha256'] = sha256(archive)
    (VENDOR / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    licenses = ROOT / 'docs/licenses/FFmpeg-dependencies'
    licenses.mkdir(exist_ok=True)
    for source in (NATIVE / 'deps/ucrt64/share/licenses').rglob('*'):
        if source.relative_to(NATIVE / 'deps/ucrt64/share/licenses').parts[0] not in ('dav1d', 'opus', 'zlib', 'libwinpthread'):
            continue
        if source.is_file():
            target = licenses / source.relative_to(NATIVE / 'deps/ucrt64/share/licenses')
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
    shutil.copy2(NATIVE / 'source/COPYING.GPLv3', ROOT / 'docs/licenses/FFmpeg.txt')
    configuration = (NATIVE / 'build/config.h').read_text()
    build_notice = ('QuickCapture FFmpeg shared build, 9.0.2\n'
                    'Source: ' + inputs['ffmpegSource'] + '\n'
                    'Recipe: tools/build-media.ps1, tools/build-media.sh, tools/build-media.py\n'
                    'Pinned inputs and corresponding sources: Tools/media/build-inputs.json\n'
                    'Compiler: ' + subprocess.check_output([gcc, '--version'], text=True).splitlines()[0] + '\n\n'
                    + next(line for line in configuration.splitlines() if line.startswith('#define FFMPEG_CONFIGURATION')) + '\n')
    (ROOT / 'docs/licenses/FFmpeg-build.txt').write_text(build_notice)
    subprocess.run([str(VENDOR / 'ffmpeg.exe'), '-version'], check=True, stdout=subprocess.DEVNULL)
    subprocess.run([str(VENDOR / 'ffprobe.exe'), '-version'], check=True, stdout=subprocess.DEVNULL)
    print(json.dumps({'runtimeMiB': sum(item['size'] for item in files) / 1024**2,
                      'archiveMiB': archive.stat().st_size / 1024**2, 'files': [item['name'] for item in files]}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['prepare', 'collect'])
    parser.add_argument('--offline', action='store_true')
    args = parser.parse_args()
    prepare(args.offline) if args.mode == 'prepare' else collect()
