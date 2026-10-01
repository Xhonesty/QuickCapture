"""Decode all integration-test MP4s with an existing FFmpeg installation."""
import json
import pathlib
import re
import shutil
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
directory = root / 'dist' / 'Diagnostics'
ffmpeg = sys.argv[1] if len(sys.argv) > 1 else shutil.which('ffmpeg')
if not ffmpeg:
    raise SystemExit('FFmpeg is needed only for this additional verification, not for QuickCapture.')
results = []
for path in sorted(directory.glob('*.mp4')):
    if path.name.endswith('.partial.mp4'):
        continue
    process = subprocess.run([ffmpeg, '-hide_banner', '-i', str(path), '-f', 'null', '-'], capture_output=True, text=True, encoding='utf-8', errors='replace')
    log = process.stderr
    (directory / (path.name + '.decode.txt')).write_text(log, encoding='utf-8')
    source_log = log.split('Stream mapping:')[0]
    video = re.search(r'Video: h264.*?, (\d+)x(\d+)', source_log)
    audio = 'Audio: aac' in source_log
    duration = re.search(r'Duration: (\d+:\d+:\d+\.\d+)', source_log)
    frames = re.findall(r'frame=\s*(\d+)', log)
    ok = process.returncode == 0 and video is not None and bool(frames) and int(frames[-1]) > 50
    if 'audio' in path.name:
        ok = ok and audio
    results.append({'file': path.name, 'passed': ok, 'decodeExit': process.returncode, 'dimensions': [int(video[1]), int(video[2])] if video else None, 'audioAAC': audio, 'duration': duration[1] if duration else None, 'decodedFrames': int(frames[-1]) if frames else 0})
(directory / 'media-validation.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
print(json.dumps(results, indent=2))
if not results or not all(result['passed'] for result in results):
    raise SystemExit(1)
