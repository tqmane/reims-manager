"""Authored display descriptors; no macOS libraries or live display are loaded."""
import contextlib
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('display', Path(__file__).resolve().parents[1] / 'guest-display.py')
display = importlib.util.module_from_spec(spec)
spec.loader.exec_module(display)
SESSION = dict(gui_asid=101, boot_seconds=123, boot_microseconds=456)


def mode(identifier, scale=1):
    return dict(id=identifier, width=1600, height=900, pixel_width=1600 * scale,
                pixel_height=900 * scale, refresh_hz=60, flags=3, usable=True)


def screen(identifier, main=False):
    return dict(identity=dict(display_id=identifier, uuid=f'{identifier:08x}-0000-0000-0000-000000000000', vendor=1, model=2, serial=identifier),
                main=main, active=True, builtin=False, mirrored=False, mode=mode(1, 2))


class Graphics:
    def __init__(self):
        self.displays = [screen(10, True), screen(20)]
        self.options = {10: [mode(1, 2), mode(13)], 20: [mode(1, 2), mode(13)]}
        self.calls = []
        self.after_set = None

    def inventory(self):
        return copy.deepcopy(self.displays)

    def modes(self, identifier):
        return copy.deepcopy(self.options[identifier])

    @contextlib.contextmanager
    def retain_current(self, identifier):
        yield copy.deepcopy(next(d['mode'] for d in self.displays if d['identity']['display_id'] == identifier))

    def describe(self, original):
        return original

    def set_mode(self, identifier, value):
        self.calls.append(('set', identifier, copy.deepcopy(value)))
        next(d for d in self.displays if d['identity']['display_id'] == identifier)['mode'] = copy.deepcopy(value)
        if self.after_set:
            self.after_set(self)

    def set_raw(self, identifier, value):
        self.calls.append(('rollback', identifier, copy.deepcopy(value)))
        next(d for d in self.displays if d['identity']['display_id'] == identifier)['mode'] = copy.deepcopy(value)


class Optimization(unittest.TestCase):
    def setUp(self):
        root = Path(__file__).resolve().parents[2] / '.work/tmp'
        root.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=root)
        self.addCleanup(self.temp.cleanup)
        self.receipt = Path(self.temp.name) / 'receipt.json'
        self.graphics = Graphics()

    def apply(self, operation='lighten', token=SESSION):
        return display.optimize(operation, self.graphics, token, self.receipt)

    def test_lighten_restore_and_idempotent(self):
        main = self.graphics.inventory()[0]
        result = self.apply()
        self.assertTrue(result['applied'])
        self.assertEqual(self.graphics.calls[0][1], 20)
        self.assertEqual(self.graphics.inventory()[0], main)
        self.assertEqual(json.loads(self.receipt.read_bytes())['phase'], 'applied')
        self.assertTrue(self.apply()['already_applied'])
        self.assertEqual(len(self.graphics.calls), 1)
        self.assertFalse(self.apply('restore')['applied'])
        self.assertEqual(self.graphics.displays[1]['mode'], mode(1, 2))
        self.assertEqual(json.loads(self.receipt.read_bytes())['phase'], 'restored')
        self.apply()
        self.assertEqual(self.graphics.inventory()[0], main)

    def test_missing_or_multiple_eligible_display_refuses(self):
        for count in (0, 2):
            with self.subTest(count=count):
                self.graphics.displays = [screen(10, True)] + [screen(20 + i) for i in range(count)]
                self.graphics.options[21] = [mode(1, 2), mode(13)]
                with self.assertRaisesRegex(RuntimeError, 'Exactly one'):
                    self.apply()
        self.assertEqual(self.graphics.calls, [])
        self.assertFalse(self.receipt.exists())

    def test_main_builtin_mirrored_or_inactive_never_changes(self):
        for flag in ('main', 'builtin', 'mirrored', 'inactive'):
            with self.subTest(flag=flag):
                self.graphics = Graphics()
                if flag == 'inactive':
                    self.graphics.displays[1]['active'] = False
                else:
                    self.graphics.displays[1][flag] = True
                with self.assertRaises(RuntimeError):
                    self.apply()
                self.assertEqual(self.graphics.calls, [])

    def test_not_quarter_area_or_wrong_refresh_or_logical_size(self):
        for field, value in [('pixel_width', 3200), ('refresh_hz', 59.94), ('refresh_hz', 120), ('width', 1280), ('usable', False)]:
            with self.subTest(field=field, value=value):
                self.graphics = Graphics()
                self.graphics.options[20][1][field] = value
                with self.assertRaisesRegex(RuntimeError, 'Exactly one'):
                    self.apply()
                self.assertEqual(self.graphics.calls, [])

    def test_ambiguous_equal_size_modes_refuses(self):
        self.graphics.options[20].append(mode(14))
        with self.assertRaisesRegex(RuntimeError, 'More than one'):
            self.apply()
        self.assertEqual(self.graphics.calls, [])

    def test_foreign_mode_or_identity_change_refuses_restore(self):
        self.apply()
        for change in ('mode', 'identity'):
            before = copy.deepcopy(self.graphics.displays[1])
            if change == 'mode':
                self.graphics.displays[1]['mode'] = mode(99)
            else:
                self.graphics.displays[1]['identity']['uuid'] = 'ffffffff-0000-0000-0000-000000000000'
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                self.apply('restore')
            self.graphics.displays[1] = before
        self.assertEqual(len(self.graphics.calls), 1)

    def test_new_login_refuses_old_restore(self):
        self.apply()
        with self.assertRaisesRegex(RuntimeError, 'login session ended'):
            self.apply('restore', dict(SESSION, gui_asid=102))
        self.assertEqual(len(self.graphics.calls), 1)

    def test_saved_mode_unavailable_refuses_restore(self):
        self.apply()
        self.graphics.options[20] = [mode(13)]
        with self.assertRaisesRegex(RuntimeError, 'no longer offered'):
            self.apply('restore')
        self.assertEqual(len(self.graphics.calls), 1)

    def test_main_change_fails_without_setting_main(self):
        self.graphics.after_set = lambda graphics: graphics.displays[0].update(mode=mode(99))
        with self.assertRaisesRegex(RuntimeError, 'rollback unconfirmed'):
            self.apply()
        self.assertTrue(all(call[1] == 20 for call in self.graphics.calls))
        self.assertEqual(self.graphics.displays[0]['mode'], mode(99))

    def test_foreign_change_during_call_is_not_overwritten(self):
        self.graphics.after_set = lambda graphics: graphics.displays[1].update(mode=mode(99))
        with self.assertRaisesRegex(RuntimeError, 'rollback unconfirmed'):
            self.apply()
        self.assertEqual(self.graphics.displays[1]['mode'], mode(99))
        self.assertEqual(len(self.graphics.calls), 1)

    def test_receipt_commit_failure_rolls_back(self):
        original = display.write_record
        def fail_commit(path, value):
            if value['phase'] == 'applied':
                raise OSError('fixture disk failure')
            original(path, value)
        with patch.object(display, 'write_record', fail_commit), self.assertRaisesRegex(OSError, 'fixture disk'):
            self.apply()
        self.assertEqual(self.graphics.displays[1]['mode'], mode(1, 2))
        self.assertFalse(self.receipt.exists())
        self.assertEqual(self.graphics.calls[-1][0], 'rollback')

    def test_foreign_receipt_during_transaction_is_not_overwritten(self):
        self.graphics.after_set = lambda graphics: self.receipt.write_text('foreign replacement')
        with self.assertRaisesRegex(RuntimeError, 'Refusing to overwrite'):
            self.apply()
        self.assertEqual(self.receipt.read_text(), 'foreign replacement')

    def test_interrupted_before_apply_receipt_can_be_restored_without_setting_mode(self):
        self.apply()
        record = json.loads(self.receipt.read_bytes())
        record['phase'] = 'pending'
        display.write_record(self.receipt, record)
        self.graphics.displays[1]['mode'] = mode(1, 2)
        self.graphics.calls.clear()
        self.assertFalse(self.apply('restore')['applied'])
        self.assertEqual(self.graphics.calls, [])
        self.assertEqual(json.loads(self.receipt.read_bytes())['phase'], 'restored')

    def test_typed_ownership_receipt_refuses_malformed(self):
        self.apply()
        record = json.loads(self.receipt.read_bytes())
        for field, value in [('schema', True), ('owner', 'foreign'), ('phase', 'other')]:
            bad = dict(record)
            bad[field] = value
            self.receipt.write_text(json.dumps(bad))
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.apply('restore')
        self.assertEqual(len(self.graphics.calls), 1)

    def test_session_reads_only_actual_header_and_boottime(self):
        def run(args, **kwargs):
            text = 'gui/501 = {\n type = login\n session = Aqua\n asid = 100003\n' if args[0] == '/bin/launchctl' else '{ sec = 123, usec = 456 } fixture date'
            return subprocess.CompletedProcess(args, 0, text, '')
        with patch.object(display.os, 'getuid', return_value=501, create=True):
            self.assertEqual(display.session_token(run), dict(SESSION, gui_asid=100003))
            with self.assertRaisesRegex(RuntimeError, 'GUI login'):
                display.session_token(lambda args, **kw: subprocess.CompletedProcess(args, 0, 'session = Aqua\n', ''))


if __name__ == '__main__':
    unittest.main()
