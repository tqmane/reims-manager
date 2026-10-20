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
from unittest.mock import patch

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
        self.move_reply = None
        self.move_code = 0
        self.move_calls = []
        self.calls = []
        self.pid = 123
        self.stop_delay = 0
        self.stop_pending = None
        self.never_stop = False
        self.foreign_on_stop = False
        self.replacement_on_stop = False

    def __call__(self, args, **kwargs):
        self.calls.append(args)
        if args[0] == str(self.executable):
            self.move_calls.append(args)
            assert args[1] == '--move-to-display' and args[2] in ('main', 'extended')
            assert len(args) == 3 and kwargs['timeout'] == 10
            value = dict(schema=1, action='move-to-display', target=args[2], ok=True,
                         cgError=0, displayID=22, boundsCG=dict(x=1600, y=-10, width=1000, height=800),
                         positionCG=dict(x=2100, y=390), candidateCount=1, error=None)
            return subprocess.CompletedProcess(args, self.move_code,
                                               self.move_reply if self.move_reply is not None else json.dumps(value), '')
        assert args[0] == '/bin/launchctl'
        assert kwargs['timeout'] == 10
        verb = args[1]
        code, output = 0, ''
        if verb == 'print':
            if len(args[2].split('/')) == 2:
                code = 0 if self.gui else 1
            else:
                if self.stop_pending is not None and not self.never_stop:
                    if self.stop_pending == 0:
                        self.live = False
                        self.stop_pending = None
                    else:
                        self.stop_pending -= 1
                if self.live:
                    path = '/foreign.plist' if self.foreign else str(self.agent)
                    output = f'\tpath = {path}\n\tprogram = {self.executable}\n\tpid = {self.pid}\n'
                else:
                    code = 113
        elif verb == 'bootout':
            self.stop_pending = self.stop_delay
            if self.foreign_on_stop:
                self.foreign = True
                self.never_stop = True
            if self.replacement_on_stop:
                self.pid += 1
                self.never_stop = True
        elif verb == 'bootstrap':
            if self.fail_bootstrap:
                self.fail_bootstrap -= 1
                code = 5
            else:
                assert self.agent.exists() and self.executable.exists()
                self.live = True
                self.stop_pending = None
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

    def test_async_bootout_waits_before_upgrading(self):
        self.apply(request())
        self.control.stop_delay = 3
        self.control.calls.clear()
        with patch.object(aid.time, 'sleep') as sleep:
            self.apply(request(b'new authored fixture', 'b' * 40))
        self.assertEqual(sleep.call_count, 3)
        verbs = [args[1] for args in self.control.calls]
        self.assertEqual(verbs.count('bootout'), 1)
        self.assertGreaterEqual(verbs.index('bootstrap') - verbs.index('bootout'), 5)
        self.assertEqual(self.control.executable.read_bytes(), b'new authored fixture')
        self.assertTrue(self.control.live)

    def test_nonexit_does_not_change_files_or_bootstrap(self):
        self.apply(request())
        before = {path: path.read_bytes() for path in (self.receipt, self.control.agent, self.control.executable)}
        self.control.never_stop = True
        self.control.calls.clear()
        clock = [0.0]
        def advance(seconds):
            clock[0] += seconds
        with patch.object(aid.time, 'monotonic', side_effect=lambda: clock[0]), patch.object(aid.time, 'sleep', side_effect=advance):
            with self.assertRaisesRegex(RuntimeError, 'not confirmed within 5 seconds'):
                self.apply(request(b'new', 'b' * 40))
        self.assertLess(clock[0], 5.2)
        self.assertFalse(any(args[1] == 'bootstrap' for args in self.control.calls))
        for path, data in before.items():
            self.assertEqual(path.read_bytes(), data)

    def test_foreign_transition_during_teardown_preserves_files(self):
        self.apply(request())
        before = {path: path.read_bytes() for path in (self.receipt, self.control.agent, self.control.executable)}
        for kind, message in (('foreign_on_stop', 'unowned path'), ('replacement_on_stop', 'PID changed')):
            with self.subTest(kind=kind):
                self.control.foreign = False
                self.control.never_stop = False
                self.control.foreign_on_stop = False
                self.control.replacement_on_stop = False
                self.control.stop_pending = None
                setattr(self.control, kind, True)
                self.control.calls.clear()
                with self.assertRaisesRegex((ValueError, RuntimeError), message):
                    self.apply(request(b'new', 'b' * 40))
                self.assertFalse(any(args[1] == 'bootstrap' for args in self.control.calls))
                for path, data in before.items():
                    self.assertEqual(path.read_bytes(), data)

    def test_rollback_stops_new_service_before_restoring_old_files(self):
        self.apply(request())
        before = {path: path.read_bytes() for path in (self.receipt, self.control.agent, self.control.executable)}
        self.control.stop_delay = 2
        original_run = self.control
        def run(args, **kwargs):
            if args[1] == 'kickstart' and self.control.executable.read_bytes() == b'new':
                return subprocess.CompletedProcess(args, 5, '', 'synthetic kickstart failure')
            return original_run(args, **kwargs)
        with patch.object(aid.time, 'sleep'), self.assertRaisesRegex(RuntimeError, 'kickstart failed'):
            aid.apply(request(b'new', 'b' * 40), self.home, os.getuid() if hasattr(os, 'getuid') else 0, run)
        self.assertTrue(self.control.live)
        for path, data in before.items():
            self.assertEqual(path.read_bytes(), data)

    def test_move_verifies_enables_then_runs_only_fixed_target(self):
        for target in ('main', 'extended'):
            result = self.apply(dict(request(), operation='move-' + target))
            self.assertTrue(result['enabled'])
            self.assertTrue(result['move']['ok'])
            self.assertEqual(result['move']['target'], target)
            self.assertEqual(self.control.move_calls[-1], [str(self.control.executable), '--move-to-display', target])
            self.assertTrue(json.loads(self.receipt.read_bytes())['enabled'])
        self.assertEqual(sum(args[1] == 'bootstrap' for args in self.control.calls), 1)

    def test_failed_move_keeps_enabled_installation(self):
        refused = dict(schema=1, action='move-to-display', target='extended', ok=False,
                       cgError=None, displayID=None, boundsCG=None, positionCG=None,
                       candidateCount=2, error='synthetic-refusal')
        self.control.move_reply, self.control.move_code = json.dumps(refused), 1
        with self.assertRaisesRegex(RuntimeError, 'remains enabled.*movement was refused'):
            self.apply(dict(request(), operation='move-extended'))
        self.assertTrue(self.control.live)
        self.assertTrue(json.loads(self.receipt.read_bytes())['enabled'])
        self.assertTrue(self.control.agent.exists())
        self.assertEqual(sum(args[1] == 'bootout' for args in self.control.calls), 0)

    def test_old_native_cli_and_malformed_result_keep_enabled(self):
        for code, output in ((2, ''), (0, 'not JSON')):
            self.control.move_code, self.control.move_reply = code, output
            with self.assertRaisesRegex(RuntimeError, 'remains enabled'):
                self.apply(dict(request(), operation='move-main'))
            self.assertTrue(self.control.live)
            self.assertTrue(json.loads(self.receipt.read_bytes())['enabled'])

    def test_move_protocol_rejects_bad_success_fields(self):
        value = dict(schema=1, action='move-to-display', target='extended', ok=True,
                     cgError=0, displayID=22, boundsCG=dict(x=1600, y=-10, width=1000, height=800),
                     positionCG=dict(x=2100, y=390), candidateCount=1, error=None)
        for field, bad in [('schema', True), ('ok', 'true'), ('cgError', True), ('cgError', 1),
                           ('displayID', -1), ('candidateCount', 2), ('candidateCount', True),
                           ('boundsCG', dict(x=0, y=0, width=0, height=1)),
                           ('positionCG', dict(x=float('nan'), y=0)), ('error', 'unexpected')]:
            with self.subTest(field=field, bad=bad), self.assertRaises((ValueError, RuntimeError)):
                aid.move_result(subprocess.CompletedProcess([], 0, json.dumps(dict(value, **{field: bad})), ''), 'extended')

    def test_repeated_enable_reuses_verified_running_service(self):
        self.apply(request())
        before = self.receipt.read_bytes()
        self.control.calls.clear()
        self.assertEqual(self.apply(request())['pid'], 123)
        self.assertFalse(any(args[1] in ('bootout', 'bootstrap', 'kickstart') for args in self.control.calls))
        self.assertEqual(self.receipt.read_bytes(), before)

    def test_failed_inspection_is_not_missing_service(self):
        for code in (5, 125):
            def denied(args, **kwargs):
                return subprocess.CompletedProcess(args, code, '', 'inspection failed')
            with self.subTest(code=code), self.assertRaisesRegex(RuntimeError, 'inspect'):
                aid.LaunchAgent(0, self.control.agent, self.control.executable, denied).loaded()

    def test_loginwindow_domain_125_has_actionable_message_without_mutation(self):
        calls = []
        uid = os.getuid() if hasattr(os, 'getuid') else 0
        def loginwindow(args, **kwargs):
            calls.append(args)
            self.assertEqual(args, ['/bin/launchctl', 'print', f'gui/{uid}'])
            return subprocess.CompletedProcess(args, 125, '',
                'Could not print domain: 125: Domain does not support specified action')
        with patch.object(aid, 'write_file') as write:
            with self.assertRaisesRegex(RuntimeError, 'macOSにログインしてから'):
                aid.apply(request(), self.home, uid, loginwindow)
            write.assert_not_called()
        self.assertEqual(len(calls), 1)
        self.assertEqual(list(self.home.rglob('*')), [])

    def test_other_domain_permission_failure_keeps_inspection_error(self):
        calls = []
        def denied(args, **kwargs):
            calls.append(args)
            return subprocess.CompletedProcess(args, 5, '', 'Operation not permitted')
        with patch.object(aid, 'write_file') as write:
            with self.assertRaisesRegex(RuntimeError, 'launchctl print failed: Operation not permitted'):
                aid.apply(request(), self.home, os.getuid() if hasattr(os, 'getuid') else 0, denied)
            write.assert_not_called()
        self.assertEqual(len(calls), 1)
        self.assertEqual(list(self.home.rglob('*')), [])

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
