#!/usr/bin/env python3
"""Build and check the authored macOS cursor helper with the selected Apple SDK."""

import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import zipfile


ROOT = Path(__file__).resolve().parents[1]
INPUTS = [
    "native/cursor-aid",
    "tools/build_cursor_aid.py",
    ".github/workflows/cursor-aid.yml",
    "docs/cursor-aid.md",
]


def output(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()


def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)


def clean_revision():
    revision = output("git", "rev-parse", "HEAD")
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise RuntimeError("Expected a committed source revision")
    if output("git", "status", "--porcelain", "--untracked-files=all", "--", *INPUTS):
        raise RuntimeError("Commit the cursor aid sources before packaging")
    return revision


def main():
    if sys.platform != "darwin":
        raise SystemExit("The cursor helper is built on macOS with the installed Apple SDK")
    revision = clean_revision()
    clang = output("xcrun", "--find", "clang")
    sdk = output("xcrun", "--sdk", "macosx", "--show-sdk-path")
    lipo = output("xcrun", "--find", "lipo")
    print(output(clang, "--version"), flush=True)
    print("SDK:", sdk, flush=True)
    work = ROOT / ".work" / "cursor-aid-build"
    work.mkdir(parents=True, exist_ok=True)
    package = work / "package"
    package.mkdir(exist_ok=True)
    source = ROOT / "native" / "cursor-aid" / "ReimsCursorAid.m"
    slices = []
    for architecture in ("arm64", "x86_64"):
        binary = work / ("ReimsCursorAid-" + architecture)
        run(clang, "-arch", architecture, "-isysroot", sdk,
            "-mmacosx-version-min=11.0", "-fobjc-arc", "-Wall", "-Wextra", "-O2",
            str(source), "-framework", "AppKit", "-framework", "CoreGraphics",
            "-framework", "ImageIO", "-o", str(binary))
        slices.append(str(binary))
    binary = package / "ReimsCursorAid"
    run(lipo, "-create", *slices, "-output", str(binary))
    binary.chmod(0o755)
    run("codesign", "--force", "--sign", "-", str(binary))
    run("codesign", "--verify", "--strict", str(binary))
    # Pure geometry, snapshot/pipe bounds and arguments; no window or current-cursor query.
    result = json.loads(output(str(binary), "--self-test"))
    if result.get("self_test") != "passed" or result.get("failures") != 0 or result.get("checks", 0) < 1:
        raise RuntimeError("Native self-test did not execute successfully")
    print(json.dumps(result), flush=True)
    digest = hashlib.sha256(binary.read_bytes()).hexdigest()
    manifest = {"schema": 1, "executable": "ReimsCursorAid", "sha256": digest, "revision": revision}
    (package / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (package / "ReimsCursorAid.sha256").write_text(digest + "  ReimsCursorAid\n", encoding="ascii")
    if clean_revision() != revision:
        raise RuntimeError("Cursor aid sources changed during the build")
    artifacts = ROOT / "artifacts" / "cursor-aid"
    artifacts.mkdir(parents=True, exist_ok=True)
    archive = artifacts / "ReimsCursorAid.zip"
    partial = artifacts / "ReimsCursorAid.zip.partial"
    with zipfile.ZipFile(partial, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for name in ("ReimsCursorAid", "manifest.json", "ReimsCursorAid.sha256"):
            bundle.write(package / name, name)
    os.replace(partial, archive)
    archive_hash = hashlib.sha256(archive.read_bytes()).hexdigest()
    (artifacts / "ReimsCursorAid.zip.sha256").write_text(archive_hash + "  ReimsCursorAid.zip\n", encoding="ascii")
    print("Built:", archive, flush=True)


if __name__ == "__main__":
    main()
