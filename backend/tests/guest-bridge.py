"""Authored fixtures only; no guest binaries or live VM are used."""
import copy
import importlib.util
import json
from pathlib import Path
import plistlib
import tempfile
import unittest

BACKEND = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('bridge', BACKEND/'opencore/guest-bridge.py')
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)
PIN = json.loads((BACKEND/'guest-bridge-lock.json').read_text(encoding='utf-8-sig'))
READY = dict(copy.deepcopy(PIN), id='fixture-qualified-generic', min_kernel='25.0.0', max_kernel='25.99.99',
             scope='generic-metal', runtime_validation='abi-checked', release_ready=True, qualified_kernels=['25.4.0'])


def config():
    return {'Kernel': {'Add': [{'BundlePath': 'Keep.kext', 'Enabled': True, 'MinKernel': '25.1.0', 'MaxKernel': '25.2.0'}], 'Quirks': {}},
            'PlatformInfo': {'Generic': {'SystemUUID': 'fixture-uuid', 'MLB': 'fixture-board', 'ROM': b'123456'}},
            'NVRAM': {'Add': {bridge.APPLE: {'boot-args': '-v keepsyms=1', 'csr-active-config': bytes(4)}}},
            'Misc': {'Security': {'ScanPolicy': 0x301, 'SecureBootModel': 'x86legacy'}}}


def package(root, pin=READY):
    source = root/'source'; source.mkdir(parents=True)
    (source/'fixture.cpp').write_text('int authored_fixture;\n', encoding='utf-8')
    snapshot = dict(schema=1, revision=pin['sources']['reims']['commit'], files=bridge.hashes(source))
    (source/'source-manifest.json').write_text(json.dumps(snapshot), encoding='utf-8')
    for name, identifier, version in ((bridge.BRIDGE, 'io.reims.MetalCompat', '0.1.0'),
                                      (bridge.LILU, 'as.vit9696.Lilu', '1.7.2')):
        contents = root/'Kexts'/name/'Contents'
        (contents/'MacOS').mkdir(parents=True)
        executable = name.removesuffix('.kext')
        (contents/'MacOS'/executable).write_bytes(b'authored fixture, not executable')
        (contents/'Info.plist').write_bytes(plistlib.dumps(dict(CFBundleIdentifier=identifier,
            CFBundleVersion=version, CFBundleExecutable=executable)))
    signed = root/'Kexts'/bridge.BRIDGE/'Contents/Resources'
    signed.mkdir()
    (signed/'source-manifest.json').write_text(json.dumps(snapshot), encoding='utf-8')
    manifest = {key: pin[key] for key in bridge.PACKAGE_FIELDS}
    manifest['files'] = bridge.hashes(root)
    (root/'bridge-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')


class GuestBridgeTests(unittest.TestCase):
    def test_candidate_cannot_be_published_or_applied(self):
        candidate = dict(PIN, release_ready=False, qualified_kernels=[])
        with self.assertRaisesRegex(ValueError, 'still a candidate'):
            bridge.require_qualified(candidate)
        with self.assertRaisesRegex(ValueError, 'still a candidate'):
            bridge.patch_config(config(), candidate, True)
        for field, value in [('scope', 'browser-scoped'), ('runtime_validation', 'pending'), ('qualified_kernels', [])]:
            bad = dict(READY); bad[field] = value
            with self.assertRaises(ValueError):
                bridge.require_qualified(bad)

    def test_enable_disable_preserves_identity_security_and_bounds(self):
        original = config(); before = copy.deepcopy(original)
        enabled = bridge.patch_config(original, READY, True)
        self.assertEqual(original, before)
        self.assertEqual(enabled['PlatformInfo'], original['PlatformInfo'])
        self.assertEqual(enabled['Misc'], original['Misc'])
        self.assertEqual(enabled['NVRAM']['Add'][bridge.APPLE]['csr-active-config'], bytes(4))
        entries = enabled['Kernel']['Add']
        self.assertEqual([x['BundlePath'] for x in entries], [bridge.LILU, bridge.BRIDGE, 'Keep.kext'])
        self.assertEqual((entries[1]['MinKernel'], entries[1]['MaxKernel']), ('25.0.0', '25.99.99'))
        self.assertEqual(bridge.patch_config(enabled, READY, True), enabled)
        disabled = bridge.patch_config(enabled, READY, False)
        self.assertFalse(disabled['Kernel']['Add'][1]['Enabled'])
        self.assertEqual(disabled['Kernel']['Add'][1]['MaxKernel'], '25.99.99')
        self.assertEqual(disabled['NVRAM']['Add'][bridge.APPLE]['boot-args'], '-v keepsyms=1')

    def test_qualified_reapply_migrates_old_bridge_bounds_only_within_family(self):
        enabled = bridge.patch_config(config(), READY, True)
        enabled['Kernel']['Add'][0].update(MinKernel='25.0.0', MaxKernel='25.99.99')
        self.assertEqual(bridge.patch_config(enabled, READY, True)['Kernel']['Add'][0], enabled['Kernel']['Add'][0])
        enabled['Kernel']['Add'][1].update(MinKernel='25.4.0', MaxKernel='25.4.0')
        before = copy.deepcopy(enabled)
        reapplied = bridge.patch_config(enabled, READY, True)
        self.assertEqual((reapplied['Kernel']['Add'][1]['MinKernel'], reapplied['Kernel']['Add'][1]['MaxKernel']), ('25.0.0', '25.99.99'))
        self.assertEqual(reapplied['Kernel']['Add'][2], enabled['Kernel']['Add'][2])
        self.assertEqual(enabled, before)
        enabled['Kernel']['Add'][1]['MaxKernel'] = '26.0.0'
        before = copy.deepcopy(enabled)
        with self.assertRaisesRegex(ValueError, 'kernel bounds differ'):
            bridge.patch_config(enabled, READY, True)
        self.assertEqual(enabled, before)
        self.assertEqual(bridge.patch_config(enabled, READY, False)['Kernel']['Add'][1]['MaxKernel'], '26.0.0')

    def test_only_owned_lilu_entry_migrates_with_explicit_qualified_enable(self):
        old = bridge.patch_config(config(), READY, True)
        for entry in old['Kernel']['Add'][:2]:
            entry.update(MinKernel='25.4.0', MaxKernel='25.4.0')
        updated = bridge.patch_config(old, READY, True)
        self.assertTrue(all((e['MinKernel'], e['MaxKernel']) == ('25.0.0', '25.99.99') for e in updated['Kernel']['Add'][:2]))
        custom = copy.deepcopy(old); custom['Kernel']['Add'][0]['Comment'] = 'Custom Lilu policy'
        with self.assertRaisesRegex(ValueError, 'Existing Lilu excludes'):
            bridge.patch_config(custom, READY, True)
        self.assertEqual(custom['Kernel']['Add'][0]['MaxKernel'], '25.4.0')
        with self.assertRaisesRegex(ValueError, 'still a candidate'):
            bridge.patch_config(old, dict(PIN, release_ready=False), True)

    def test_release_records_measured_kernels_and_frame_pacing_limit(self):
        bridge.require_qualified(PIN)
        self.assertEqual(PIN['qualified_kernels'], ['25.4.0', '25.6.0'])
        self.assertTrue(PIN['limitations'])
        self.assertEqual(PIN['qualification']['26.4']['webgl_stages'], 22)
        self.assertEqual(PIN['qualification']['26.7.1']['kernel'], '25.6.0')

    def test_package_hashes_source_and_kernel_pin_are_checked(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); package(root); bridge.verify(root, READY)
            manifest = json.loads((root/'bridge-manifest.json').read_text())
            manifest['max_kernel'] = '25.6.0'
            (root/'bridge-manifest.json').write_text(json.dumps(manifest))
            with self.assertRaisesRegex(ValueError, 'max_kernel'):
                bridge.verify(root, READY)
            manifest['max_kernel'] = READY['max_kernel']
            (root/'bridge-manifest.json').write_text(json.dumps(manifest))
            (root/'source/fixture.cpp').write_text('changed')
            with self.assertRaisesRegex(ValueError, 'files changed'):
                bridge.verify(root, READY)

    def test_application_keeps_other_kexts_and_rejects_unqualified_lilu(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp); addon = base/'addon'; package(addon)
            root = base/'efi'; oc = root/'EFI/OC'; oc.mkdir(parents=True)
            path = oc/'config.plist'; path.write_bytes(plistlib.dumps(config()))
            bridge.apply(root, READY, True, addon)
            self.assertTrue((oc/'Kexts'/bridge.BRIDGE/'Contents/MacOS/ReimsMetalCompat').is_file())
            bridge.apply(root, READY, False)
            self.assertEqual(plistlib.loads(path.read_bytes())['PlatformInfo'], config()['PlatformInfo'])
            (oc/'Kexts'/bridge.LILU/'Contents/MacOS/Lilu').write_bytes(b'other build')
            before = path.read_bytes()
            with self.assertRaisesRegex(ValueError, 'Existing Lilu differs'):
                bridge.apply(root, READY, True, addon)
            self.assertEqual(path.read_bytes(), before)


if __name__ == '__main__':
    unittest.main()
