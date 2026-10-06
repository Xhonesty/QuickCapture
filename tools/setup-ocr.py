"""Fetch and verify pinned local OCR models inside QuickCapture."""
import hashlib
import json
from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def main():
    manifest = json.loads((ROOT / "vendor/ocr/manifest.json").read_text(encoding="utf-8"))
    destination = ROOT / "vendor/ocr/tessdata"
    destination.mkdir(parents=True, exist_ok=True)
    for entry in manifest["models"]:
        path = destination / entry["name"]
        if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
            temporary = path.with_suffix(".partial")
            try:
                urllib.request.urlretrieve(entry["url"], temporary)
                if hashlib.sha256(temporary.read_bytes()).hexdigest() != entry["sha256"]:
                    raise RuntimeError("OCR model hash mismatch: " + path.name)
                temporary.replace(path)
            finally:
                temporary.unlink(missing_ok=True)
        print("Verified: " + path.name)


if __name__ == "__main__":
    main()
