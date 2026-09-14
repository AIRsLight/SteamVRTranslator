"""Fetch the pinned Windows native ASR trial assets; does not modify app settings."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import zipfile


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def within(root, relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root) or path == root:
        raise ValueError(f"Path outside evaluation directory: {relative}")
    return path


def prepare(root, install_dependencies, family="all"):
    manifest = json.loads(Path(__file__).with_name("sources.json").read_text(encoding="utf-8"))
    for folder in ("downloads", "runtimes", "models", "samples", "results", "python-libs"):
        (root / folder).mkdir(parents=True, exist_ok=True)
    for entry in manifest["files"]:
        if family != "all" and entry.get("family") != family:
            continue
        path = within(root, entry["path"])
        path.parent.mkdir(parents=True, exist_ok=True)
        if not path.exists():
            print(f"Downloading {entry['path']} ({entry['size']} bytes)", flush=True)
            part = path.with_name(path.name + ".part")
            subprocess.run(["curl.exe", "--fail", "--location", "--silent", "--show-error",
                            "--retry", "2", "--max-time", "600", "--output", str(part), entry["url"]],
                           check=True)
            if part.stat().st_size != entry["size"] or digest(part) != entry["sha256"]:
                raise RuntimeError(f"Downloaded file failed size/SHA-256 verification: {part}")
            part.replace(path)
        if path.stat().st_size != entry["size"] or digest(path) != entry["sha256"]:
            raise RuntimeError(f"Existing file failed size/SHA-256 verification: {path}")
        if "extract_to" in entry:
            target = within(root, entry["extract_to"])
            target.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(path) as archive:
                for member in archive.infolist():
                    within(target, member.filename)
                archive.extractall(target)
        print(f"Verified {entry['path']}", flush=True)
    if install_dependencies:
        subprocess.run([sys.executable, "-m", "pip", "install", "--disable-pip-version-check",
                        "--upgrade", "--target", str(root / "python-libs"),
                        "psutil==7.2.2", "websocket-client==1.9.0", "google-crc32c==1.7.1"], check=True)
    print(f"Ready: {root}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default="artifacts/asr-evaluation-20260914")
    parser.add_argument("--skip-dependencies", action="store_true")
    parser.add_argument("--family", choices=["all", "sensevoice"], default="all")
    args = parser.parse_args()
    if sys.platform != "win32":
        parser.error("This trial requires Windows x64.")
    prepare(Path(args.root).resolve(), not args.skip_dependencies, args.family)
