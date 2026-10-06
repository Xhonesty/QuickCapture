"""Publish a verified portable release. Git credentials stay in process memory."""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
REPO = "/repos/Xhonesty/QuickCapture"


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("inspect", "publish"))
    parser.add_argument("--version", required=True, help="Release version, for example 0.7.0")
    options = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", options.version):
        raise SystemExit("Use a three-part numeric version.")
    tag = "v" + options.version

    env = dict(os.environ, GIT_TERMINAL_PROMPT="0", GCM_INTERACTIVE="Never")
    credential = subprocess.run(
        ["git", "-c", "credential.interactive=never", "credential", "fill"],
        input="protocol=https\nhost=github.com\nusername=Xhonesty\n\n",
        text=True, capture_output=True, env=env, cwd=ROOT,
    )
    if credential.returncode:
        raise SystemExit("GitHub authentication unavailable.")
    fields = dict(line.split("=", 1) for line in credential.stdout.splitlines() if "=" in line)
    token = fields.get("password")
    del credential, fields
    if not token:
        raise SystemExit("No GitHub credential returned.")

    def api(path, data=None, method=None, allow_missing=False):
        request = urllib.request.Request(
            "https://api.github.com" + path,
            data=json.dumps(data).encode("utf-8") if data is not None else None,
            headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
                     "Content-Type": "application/json", "User-Agent": "QuickCapture-Release"},
            method=method,
        )
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if allow_missing and error.code == 404:
                return None
            raise SystemExit("GitHub API returned HTTP " + str(error.code)) from None
        except urllib.error.URLError:
            raise SystemExit("GitHub API connection failed.") from None

    def release():
        return next((item for item in api(REPO + "/releases?per_page=100") if item["tag_name"] == tag), None)

    def upload(item, path):
        expected = sha256(path)
        existing = next((asset for asset in item["assets"] if asset["name"] == path.name), None)
        if existing:
            if existing["size"] != path.stat().st_size or existing.get("digest") != "sha256:" + expected:
                raise SystemExit("Existing asset differs; no asset was replaced: " + path.name)
            print("Already verified: " + path.name, flush=True)
            return
        if not item["draft"]:
            raise SystemExit("Published releases cannot receive new assets through this script.")
        url = item["upload_url"].split("{", 1)[0] + "?name=" + urllib.parse.quote(path.name)
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme != "https" or parsed.hostname != "uploads.github.com":
            raise SystemExit("Unexpected upload endpoint.")
        connection = http.client.HTTPSConnection(parsed.hostname, timeout=300)
        connection.putrequest("POST", parsed.path + "?" + parsed.query)
        connection.putheader("Authorization", "Bearer " + token)
        connection.putheader("Accept", "application/vnd.github+json")
        connection.putheader("User-Agent", "QuickCapture-Release")
        connection.putheader("Content-Type", "application/zip" if path.suffix == ".zip" else "text/plain")
        connection.putheader("Content-Length", str(path.stat().st_size))
        connection.endheaders()
        sent, last = 0, 0
        print("Uploading " + path.name, flush=True)
        with path.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                connection.send(block)
                sent += len(block)
                if sent - last >= 32 * 1024 * 1024:
                    print("Uploaded " + str(sent // (1024 * 1024)) + " MiB", flush=True)
                    last = sent
        response = connection.getresponse()
        content = response.read()
        connection.close()
        if response.status != 201:
            raise SystemExit("GitHub upload returned HTTP " + str(response.status))
        asset = json.loads(content)
        if asset["size"] != path.stat().st_size or asset.get("digest") != "sha256:" + expected:
            raise SystemExit("Uploaded asset verification failed: " + path.name)
        print("Verified upload: " + path.name, flush=True)

    profile = api("/user")
    if profile["login"].lower() != "xhonesty":
        raise SystemExit("Authenticated account mismatch.")
    repo = api(REPO)
    if not repo.get("permissions", {}).get("push"):
        raise SystemExit("Repository write permission unavailable.")
    commit = git("rev-parse", "HEAD")
    remote = api(REPO + "/git/ref/heads/main")["object"]["sha"]
    if options.mode == "inspect":
        print(json.dumps({"repository": repo["html_url"], "default_branch": repo["default_branch"],
                          "remote_head": remote, "local_head": commit,
                          "tags": [item["name"] for item in api(REPO + "/tags?per_page=100")],
                          "releases": [{"tag": item["tag_name"], "draft": item["draft"], "url": item["html_url"]}
                                       for item in api(REPO + "/releases?per_page=100")]}, ensure_ascii=False, indent=2))
        return

    # Local research notes are not build/package inputs. Preserve them without
    # adding unrelated user work to the release commit or portable archive.
    untracked = git("ls-files", "--others", "--exclude-standard").splitlines()
    if (git("branch", "--show-current") != "main"
            or git("status", "--porcelain", "--untracked-files=no")
            or any(not path.startswith("docs/research/") for path in untracked)):
        raise SystemExit("Publish from a clean main checkout.")
    if remote != commit:
        raise SystemExit("Remote main differs from the local commit.")
    project_version = ET.parse(ROOT / "src/QuickCapture.csproj").findtext("PropertyGroup/Version")
    if project_version != options.version:
        raise SystemExit("Release and project versions differ.")
    package = ROOT / "artifacts/QuickCapture-win-x64.zip"
    checksum = package.with_suffix(".zip.sha256")
    checksum.write_text(sha256(package) + "  " + package.name + "\n", encoding="ascii")
    notes = (ROOT / "docs/releases" / (tag + ".md")).read_text(encoding="utf-8-sig")
    item = release()
    tag_ref = api(REPO + "/git/ref/tags/" + tag, allow_missing=True)
    if item is None:
        if tag_ref is not None:
            raise SystemExit("Tag already exists without a release; refusing to replace it.")
        item = api(REPO + "/releases", {"tag_name": tag, "target_commitish": commit,
                   "name": "轻截 QuickCapture " + tag, "body": notes, "draft": True, "prerelease": False})
        print("Created draft " + tag, flush=True)
    elif item["target_commitish"] != commit or (tag_ref is not None and tag_ref["object"]["sha"] != commit):
        raise SystemExit("Existing release or tag targets another commit; refusing to replace it.")
    upload(item, package)
    upload(release(), checksum)
    item = release()
    if item["draft"]:
        item = api(REPO + "/releases/" + str(item["id"]), {"draft": False, "make_latest": "true"}, method="PATCH")
    if api(REPO + "/git/ref/tags/" + tag)["object"]["sha"] != commit:
        raise SystemExit("Published tag does not match the verified commit.")
    result = {"url": item["html_url"], "tag": item["tag_name"], "commit": commit, "draft": item["draft"],
              "published_at": item["published_at"],
              "assets": [{"name": asset["name"], "size": asset["size"], "digest": asset.get("digest"),
                          "url": asset["browser_download_url"]} for asset in item["assets"]]}
    output = json.dumps(result, ensure_ascii=False, indent=2)
    (ROOT / "artifacts" / ("release-" + tag + "-result.json")).write_text(output + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
