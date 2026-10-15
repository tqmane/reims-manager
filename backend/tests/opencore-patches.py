"""Authored fixtures for the shipped OpenCore configuration patches."""
import copy
from pathlib import Path
import plistlib
import runpy
import sys
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]/'opencore'
APPLE = '7C436110-AB2A-4BBB-A880-FE41995C9F82'


class OpenCorePatches(unittest.TestCase):
    def test_base_and_reversible_version_scoped_account_patch(self):
        original = {'ACPI': {}, 'Booter': {'Quirks': {}}, 'DeviceProperties': {},
                    'Kernel': {'Emulate': {}, 'Quirks': {}},
                    'Misc': {'Boot': {}, 'Debug': {}, 'Serial': {}, 'Security': {}},
                    'NVRAM': {'Add': {}, 'Delete': {}}, 'PlatformInfo': {'Generic': {}},
                    'UEFI': {'Drivers': [{'Path': 'OpenRuntime.efi', 'Enabled': True, 'Arguments': '', 'Comment': ''}], 'Output': {}}}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source, base, patched, removed = [root/n for n in ('sample.plist', 'base.plist', 'account.plist', 'removed.plist')]
            source.write_bytes(plistlib.dumps(original))
            def run(name, *args):
                previous = sys.argv
                try:
                    sys.argv = [name, *map(str, args)]
                    runpy.run_path(str(SCRIPTS/name), run_name='__main__')
                finally:
                    sys.argv = previous
            run('base.py', source, base)
            result = plistlib.loads(base.read_bytes())
            self.assertEqual(result['NVRAM']['Add'][APPLE]['csr-active-config'], bytes(4))
            self.assertEqual(result['PlatformInfo']['Generic']['SystemProductName'], 'iMac20,1')
            self.assertEqual(result['Misc']['Security']['SecureBootModel'], 'x86legacy')
            self.assertEqual(plistlib.loads(source.read_bytes()), original)
            result['Kernel']['Patch'].append({'Comment': 'Keep unrelated patch'})
            base.write_bytes(plistlib.dumps(result))
            run('apple-account.py', base, patched, 'Enable')
            enabled = plistlib.loads(patched.read_bytes())
            additions = enabled['Kernel']['Patch'][1:]
            self.assertEqual(len(additions), 2)
            for patch in additions:
                self.assertEqual(patch['MinKernel'], '25.4.0')
                self.assertEqual(patch['MaxKernel'], '25.4.0')
                self.assertEqual(len(patch['Find']), len(patch['Replace']))
            self.assertEqual(enabled['NVRAM'], result['NVRAM'])
            run('apple-account.py', patched, removed, 'Enable')
            self.assertEqual(plistlib.loads(removed.read_bytes()), enabled)
            run('apple-account.py', patched, removed, 'Disable')
            self.assertEqual(plistlib.loads(removed.read_bytes()), result)


if __name__ == '__main__':
    unittest.main()
