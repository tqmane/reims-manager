#!/usr/bin/env python3
"""Collect the installed Apple Silicon dyld cache, without downloading any IPSW.

Only call through an explicit workflow_dispatch. Do not put proprietary
Apple binaries in git or attach them to a public Release.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess

CANDIDATES = [
    Path("/System/Volumes/Preboot/Cryptexes/OS/System/Library/dyld"),
    Path("/System/Cryptexes/OS/System/Library/dyld"),
    Path("/System/Library/dyld"),
]
PREFIX = "dyld_shared_cache_arm64e"


def choose_source(candidates=CANDIDATES):
    for folder in candidates:
        if (folder / PREFIX).is_file():
            return folder
    raise RuntimeError("Apple Silicon arm64e shared cache not present on this runner; do not use a partial sysroot")


def collect(output: Path, *, source: Path, dyld: Path, version: str, build: str):
    if output.exists() and any(output.iterdir()):
        raise RuntimeError(f"Output directory must be empty: {output}")
    if not dyld.is_file() or not (source / PREFIX).is_file():
        raise RuntimeError("Expected dyld and primary arm64e cache are required")
    subcaches = sorted(p for p in source.iterdir() if p.is_file() and p.name.startswith(PREFIX + "."))
    # The base plus *all* subcaches is required by dyld; never silently truncate.
    files = [source / PREFIX, *subcaches]
    if not subcaches:
        raise RuntimeError("No split arm64e cache parts found; refuse an incomplete export")
    output.mkdir(parents=True, exist_ok=True)
    manifest = {"schema": 1, "origin": "GitHub Actions macos-26 Apple Silicon runner",
                "macos_version": version, "macos_build": build, "architecture": "arm64",
                "files": {}}
    for src in [dyld, *files]:
        name = "dyld" if src == dyld else src.name
        digest = hashlib.sha256()
        with src.open("rb") as inp, (output / name).open("xb") as out:
            while chunk := inp.read(8 * 1024 * 1024):
                out.write(chunk)
                digest.update(chunk)
        manifest["files"][name] = {"sha256": digest.hexdigest(), "size": src.stat().st_size}
        print(f"Copied {name}: {src.stat().st_size} bytes")
    (output / "sysroot-manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    (output / "SHA256SUMS").write_text(
        "".join(f"{item['sha256']}  {name}\n" for name, item in manifest["files"].items()), encoding="utf-8"
    )
    return manifest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path)
    parser.add_argument("--probe", action="store_true", help="Check available Apple Silicon cache files without copying them")
    args = parser.parse_args()
    if platform.machine() != "arm64":
        raise RuntimeError("The sysroot export must run on a native Apple Silicon runner")
    src = choose_source()
    dyld = Path("/usr/lib/dyld")
    if not dyld.is_file():
        raise RuntimeError("Apple Silicon /usr/lib/dyld was not accessible")
    parts = sorted(p for p in src.iterdir() if p.is_file() and p.name.startswith(PREFIX + "."))
    if not parts:
        raise RuntimeError("No dyld_shared_cache_arm64e subcaches on this runner")
    if args.probe:
        print(f"Available cache: {src}, parts={len(parts)}, total_bytes=" +
              str(sum(p.stat().st_size for p in [src / PREFIX, *parts])))
        return
    if args.output is None:
        parser.error("--output is required unless --probe is set")
    version = subprocess.check_output(["sw_vers", "-productVersion"], text=True).strip()
    build = subprocess.check_output(["sw_vers", "-buildVersion"], text=True).strip()
    collect(args.output, source=src, dyld=dyld, version=version, build=build)


if __name__ == "__main__":
    main()
