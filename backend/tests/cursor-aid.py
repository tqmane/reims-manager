"""Synthetic payloads and launchctl replies only; never starts a guest process."""
import base64
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('cursor_aid', Path(__file__).resolve().parents[1] / 'guest-cursor-aid.py')
aid = importlib.util.module_from_spec(spec)
spec.loader.exec_module(aid)


def request(payload=b'authored fixture', revision='a' * 40):
    return dict(schema=1, operation='enable', payload=base64.b64encode(payload).decode(),
                manifest=dict(schema=1, executable=aid.EXECUTABLE,
                              sha256=aid.digest(payload), revision=revision))


class Launchctl:
    def __init__(self, home):
        self.agent = home / f'Library/LaunchAgents/{aid.LABEL}.plist'
        self.executable = home / f'Library/Application Support/Reims/CursorAid/{aid.EXECUTABLE}'
        self.live = False
        self.gui = True
        self.foreign = False
        self.fail_bootstrap = 0
        self.calls = []

    def __call__(self, args, **kwargs):
        self.calls.append(args)
        assert args[0] == '/bin/launchctl'
        assert kwargs['timeout'] == 10
        verb = args[1]
        code, output = 0, ''
        if verb == 'print':
            if len(args[2].split('/')) == 2:
                code = 0 if self.gui else 1
            elif self.live:
                path = '/foreign.plist' if self.foreign else str(self.agent)
                output = f'\tpath = {path}\n\tprogram = {self.executable}\n\tpid = 123\n'
            else:
                code = 113
        elif verb == 'bootout':
            self.live = False
        elif verb == 'bootstrap':
            if self.fail_bootstrap:
                self.fail_bootstrap -= 1
                code = 5
            else:
                assert self.agent.exists() and self.executable.exists()
                self.live = True
        elif verb not in ('enable', 'kickstart'):
            raise AssertionError(args)
        return subprocess.CompletedProcess(args, code, output, 'synthetic failure' if code else '')


class CursorAid(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name) / 'user 日本語 $ shell'
        self.home.mkdir()
        self.control = Launchctl(self.home)
        self.directory = self.control.executable.parent
        self.receipt = self.directory / 'receipt.json'

    def apply(self, value):
        return aid.apply(value, self.home, os.getuid() if hasattr(os, 'getuid') else 0, self.control)

    def test_enable_disable_reenable(self):
        result = self.apply(request())
        self.assertEqual(result, dict(schema=1, enabled=True, pid=123))
        plist = plistlib.loads(self.control.agent.read_bytes())
        self.assertEqual(plist['ProgramArguments'], [str(self.control.executable)])
        self.assertEqual(plist['LimitLoadToSessionType'], 'Aqua')
        self.assertTrue(plist['KeepAlive'])
        self.assertTrue(json.loads(self.receipt.read_bytes())['enabled'])
        self.assertEqual(self.apply(dict(schema=1, operation='disable')), dict(schema=1, enabled=False, pid=None))
        self.assertFalse(self.control.agent.exists())
        self.assertTrue(self.control.executable.exists())
        self.assertFalse(json.loads(self.receipt.read_bytes())['enabled'])
        self.apply(request(b'new authored fixture', 'b' * 40))
        self.assertTrue(self.control.live)
        self.assertEqual(self.control.executable.read_bytes(), b'new authored fixture')
        self.assertTrue(all('/bin/launchctl' == args[0] for args in self.control.calls))

    def test_disable_without_install_is_noop(self):
        self.apply(dict(schema=1, operation='disable'))
        self.assertFalse(self.directory.exists())
        self.assertFalse(any(args[1] == 'bootout' for args in self.control.calls))

    def test_repeated_enable_reuses_verified_running_service(self):
        self.apply(request())
        before = self.receipt.read_bytes()
        self.control.calls.clear()
        self.assertEqual(self.apply(request())['pid'], 123)
        self.assertFalse(any(args[1] in ('bootout', 'bootstrap', 'kickstart') for args in self.control.calls))
        self.assertEqual(self.receipt.read_bytes(), before)

    def test_failed_inspection_is_not_missing_service(self):
        def denied(args, **kwargs):
            return subprocess.CompletedProcess(args, 5, '', 'inspection failed')
        with self.assertRaisesRegex(RuntimeError, 'inspect'):
            aid.LaunchAgent(0, self.control.agent, self.control.executable, denied).loaded()

    def test_no_gui_changes_nothing(self):
        self.control.gui = False
        with self.assertRaisesRegex(RuntimeError, 'print failed'):
            self.apply(request())
        self.assertFalse(self.directory.exists())

    def test_malformed_requests_never_call_launchctl(self):
        values = [None, {}, dict(schema=True, operation='enable'), dict(schema=1, operation='other')]
        for field, value in [('schema', True), ('executable', '../foreign'), ('sha256', 'F'*64), ('revision', 'not-a-revision')]:
            incoming = request()
            incoming['manifest'][field] = value
            values.append(incoming)
        for payload in ('@@@', '', base64.b64encode(b'changed').decode()):
            incoming = request()
            incoming['payload'] = payload
            values.append(incoming)
        for value in values:
            with self.subTest(value=value), self.assertRaises((ValueError, TypeError)):
                self.apply(value)
        self.assertEqual(self.control.calls, [])

    def test_foreign_files_refused(self):
        self.directory.mkdir(parents=True)
        self.control.executable.write_bytes(b'foreign')
        with self.assertRaisesRegex(ValueError, 'no ownership'):
            self.apply(request())
        self.assertEqual(self.control.executable.read_bytes(), b'foreign')
        self.assertEqual(self.control.calls, [])

    def test_changed_payload_and_plist_refused_before_bootout(self):
        self.apply(request())
        original = self.control.executable.read_bytes()
        self.control.executable.write_bytes(b'tampered')
        self.control.calls.clear()
        with self.assertRaisesRegex(ValueError, 'changed outside'):
            self.apply(dict(schema=1, operation='disable'))
        self.assertEqual(self.control.calls, [])
        self.control.executable.write_bytes(original)
        self.control.agent.write_bytes(b'foreign plist')
        with self.assertRaisesRegex(ValueError, 'changed outside'):
            self.apply(request())
        self.assertEqual(self.control.agent.read_bytes(), b'foreign plist')

    def test_foreign_service_refused(self):
        self.apply(request())
        self.control.foreign = True
        self.control.calls.clear()
        with self.assertRaisesRegex(ValueError, 'unowned path'):
            self.apply(dict(schema=1, operation='disable'))
        self.assertFalse(any(args[1] == 'bootout' for args in self.control.calls))

    def test_changed_receipt_types_and_extra_files_refused(self):
        self.apply(request())
        before = self.receipt.read_bytes()
        record = json.loads(before)
        record['enabled'] = 'true'
        self.receipt.write_text(json.dumps(record))
        with self.assertRaisesRegex(ValueError, 'ownership receipt'):
            self.apply(request())
        self.receipt.write_bytes(before)
        (self.directory / 'foreign').write_text('keep')
        with self.assertRaisesRegex(ValueError, 'unowned files'):
            self.apply(request())

    def test_failed_first_start_removes_own_install(self):
        self.control.fail_bootstrap = 1
        with self.assertRaisesRegex(RuntimeError, 'bootstrap failed'):
            self.apply(request())
        self.assertFalse(self.directory.exists())
        self.assertFalse(self.control.agent.exists())
        self.assertFalse(self.control.live)
        self.apply(request())

    def test_failed_upgrade_restores_previous_running_install(self):
        self.apply(request())
        before = {path: path.read_bytes() for path in (self.receipt, self.control.agent, self.control.executable)}
        self.control.fail_bootstrap = 1
        with self.assertRaisesRegex(RuntimeError, 'bootstrap failed'):
            self.apply(request(b'new', 'c'*40))
        self.assertTrue(self.control.live)
        for path, data in before.items():
            self.assertEqual(path.read_bytes(), data)

    def test_unconfirmed_rollback_is_error(self):
        self.apply(request())
        self.control.fail_bootstrap = 2
        with self.assertRaisesRegex(RuntimeError, 'rollback unconfirmed'):
            self.apply(request(b'new', 'c'*40))

    def test_symlinked_parent_refused(self):
        target = self.home / 'elsewhere'
        target.mkdir()
        try:
            (self.home / 'Library').symlink_to(target, target_is_directory=True)
        except OSError:
            self.skipTest('Symlink creation not available for this Windows account')
        with self.assertRaisesRegex(ValueError, 'symbolic link'):
            self.apply(request())
        self.assertEqual(list(target.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
