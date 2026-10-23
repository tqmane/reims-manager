#!/usr/bin/env python3
"""Local fixtures for the Apple Silicon cache collector (no Apple files)."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "collect-arm64-sysroot.py"
spec = importlib.util.spec_from_file_location("collect_arm64_sysroot", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CollectorTests(unittest.TestCase):
    def test_collect_split_cache_and_hashes(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            source = root / "source"
            source.mkdir()
            (source / "dyld_shared_cache_arm64e").write_bytes(b"dyld_v1 arm64e")
            (source / "dyld_shared_cache_arm64e.01").write_bytes(b"part1")
            (source / "dyld_shared_cache_arm64e.02").write_bytes(b"part2")
            dyld = root / "dyld"
            dyld.write_bytes(b"loader")
            out = root / "out"
            result = module.collect(out, source=source, dyld=dyld,
                                    version="26.7", build="25G229")
            self.assertEqual(set(result["files"]), {
                "dyld", "dyld_shared_cache_arm64e", "dyld_shared_cache_arm64e.01",
                "dyld_shared_cache_arm64e.02",
            })
            self.assertIn("dyld_shared_cache_arm64e.02", (out / "SHA256SUMS").read_text())
            self.assertEqual((out / "dyld").read_bytes(), b"loader")

    def test_reject_missing_split_cache(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            source = root / "source"
            source.mkdir()
            (source / "dyld_shared_cache_arm64e").write_bytes(b"main")
            dyld = root / "dyld"
            dyld.write_bytes(b"loader")
            with self.assertRaisesRegex(RuntimeError, "split"):
                module.collect(root / "out", source=source, dyld=dyld,
                               version="26.7", build="25G229")


if __name__ == "__main__":
    unittest.main()
