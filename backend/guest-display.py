"""Explicit, session-only optimization of one extended display; public APIs only."""
import contextlib
import ctypes as C
import json
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile

OWNER = 'io.github.tqmane.reims.display-optimization'
MODE_KEYS = ('id', 'width', 'height', 'pixel_width', 'pixel_height', 'refresh_hz', 'flags', 'usable')


def function(lib, name, result, *args):
    value = getattr(lib, name)
    value.restype, value.argtypes = result, list(args)
    return value


class CoreGraphics:
    def __init__(self):
        self.cg = C.CDLL('/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics')
        self.cf = C.CDLL('/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation')
        self.cs = C.CDLL('/System/Library/Frameworks/ColorSync.framework/ColorSync')
        p, u, n = C.c_void_p, C.c_uint32, C.c_long
        for name in ('CGMainDisplayID',):
            function(self.cg, name, u)
        for name in ('CGDisplayIsActive', 'CGDisplayIsBuiltin', 'CGDisplayIsInMirrorSet',
                     'CGDisplayVendorNumber', 'CGDisplayModelNumber', 'CGDisplaySerialNumber'):
            function(self.cg, name, u, u)
        function(self.cg, 'CGGetActiveDisplayList', C.c_int, u, C.POINTER(u), C.POINTER(u))
        function(self.cg, 'CGDisplayCopyDisplayMode', p, u)
        function(self.cg, 'CGDisplayCopyAllDisplayModes', p, u, p)
        for name in ('Width', 'Height', 'PixelWidth', 'PixelHeight'):
            function(self.cg, 'CGDisplayModeGet' + name, C.c_size_t, p)
        function(self.cg, 'CGDisplayModeGetRefreshRate', C.c_double, p)
        function(self.cg, 'CGDisplayModeGetIOFlags', u, p)
        function(self.cg, 'CGDisplayModeGetIODisplayModeID', C.c_int32, p)
        function(self.cg, 'CGDisplayModeIsUsableForDesktopGUI', C.c_bool, p)
        function(self.cg, 'CGBeginDisplayConfiguration', C.c_int, C.POINTER(p))
        function(self.cg, 'CGConfigureDisplayWithDisplayMode', C.c_int, p, u, p, p)
        function(self.cg, 'CGCompleteDisplayConfiguration', C.c_int, p, u)
        function(self.cg, 'CGCancelDisplayConfiguration', C.c_int, p)
        function(self.cs, 'CGDisplayCreateUUIDFromDisplayID', p, u)
        function(self.cf, 'CFRelease', None, p)
        function(self.cf, 'CFUUIDCreateString', p, p, p)
        function(self.cf, 'CFStringGetCString', C.c_bool, p, p, n, u)
        function(self.cf, 'CFArrayGetCount', n, p)
        function(self.cf, 'CFArrayGetValueAtIndex', p, p, n)
        function(self.cf, 'CFDictionaryCreate', p, p, C.POINTER(p), C.POINTER(p), n, p, p)

    @contextlib.contextmanager
    def owned(self, pointer):
        if not pointer:
            raise RuntimeError('CoreGraphics returned no display information')
        try:
            yield pointer
        finally:
            self.cf.CFRelease(pointer)

    def describe(self, mode):
        return dict(id=self.cg.CGDisplayModeGetIODisplayModeID(mode),
                    width=self.cg.CGDisplayModeGetWidth(mode), height=self.cg.CGDisplayModeGetHeight(mode),
                    pixel_width=self.cg.CGDisplayModeGetPixelWidth(mode), pixel_height=self.cg.CGDisplayModeGetPixelHeight(mode),
                    refresh_hz=self.cg.CGDisplayModeGetRefreshRate(mode),
                    flags=self.cg.CGDisplayModeGetIOFlags(mode), usable=bool(self.cg.CGDisplayModeIsUsableForDesktopGUI(mode)))

    def inventory(self):
        ids, count = (C.c_uint32 * 64)(), C.c_uint32()
        if self.cg.CGGetActiveDisplayList(64, ids, C.byref(count)) or count.value >= 64:
            raise RuntimeError('Cannot enumerate active displays safely')
        main, displays = self.cg.CGMainDisplayID(), []
        for display in ids[:count.value]:
            with self.owned(self.cs.CGDisplayCreateUUIDFromDisplayID(display)) as uuid:
                with self.owned(self.cf.CFUUIDCreateString(None, uuid)) as text:
                    buffer = C.create_string_buffer(64)
                    if not self.cf.CFStringGetCString(text, buffer, len(buffer), 0x08000100):
                        raise RuntimeError('Cannot read public display UUID')
                    identity = dict(display_id=display, uuid=buffer.value.decode('ascii'),
                                    vendor=self.cg.CGDisplayVendorNumber(display), model=self.cg.CGDisplayModelNumber(display),
                                    serial=self.cg.CGDisplaySerialNumber(display))
            with self.owned(self.cg.CGDisplayCopyDisplayMode(display)) as current:
                displays.append(dict(identity=identity, main=display == main,
                                     active=bool(self.cg.CGDisplayIsActive(display)),
                                     builtin=bool(self.cg.CGDisplayIsBuiltin(display)),
                                     mirrored=bool(self.cg.CGDisplayIsInMirrorSet(display)), mode=self.describe(current)))
        return displays

    @contextlib.contextmanager
    def mode_arrays(self, display):
        key = C.c_void_p.in_dll(self.cg, 'kCGDisplayShowDuplicateLowResolutionModes')
        truth = C.c_void_p.in_dll(self.cf, 'kCFBooleanTrue')
        keys, values = (C.c_void_p * 1)(key.value), (C.c_void_p * 1)(truth.value)
        # The standard CFType callbacks are data symbols, passed by address.
        key_callbacks = C.addressof(C.c_byte.in_dll(self.cf, 'kCFTypeDictionaryKeyCallBacks'))
        value_callbacks = C.addressof(C.c_byte.in_dll(self.cf, 'kCFTypeDictionaryValueCallBacks'))
        with self.owned(self.cf.CFDictionaryCreate(None, keys, values, 1, key_callbacks, value_callbacks)) as options:
            with self.owned(self.cg.CGDisplayCopyAllDisplayModes(display, None)) as normal:
                with self.owned(self.cg.CGDisplayCopyAllDisplayModes(display, options)) as duplicates:
                    result = []
                    for array in (normal, duplicates):
                        count = self.cf.CFArrayGetCount(array)
                        if not 0 <= count <= 4096:
                            raise RuntimeError('Unexpected display mode count')
                        result.extend(self.cf.CFArrayGetValueAtIndex(array, i) for i in range(count))
                    yield result

    def modes(self, display):
        with self.mode_arrays(display) as modes:
            return list({json.dumps(self.describe(mode), sort_keys=True): self.describe(mode) for mode in modes}.values())

    def set_mode(self, display, descriptor):
        with self.mode_arrays(display) as modes:
            matches = [mode for mode in modes if self.describe(mode) == descriptor]
            if not matches:
                raise RuntimeError('The exact display mode is no longer offered')
            self.set_raw(display, matches[0])

    def set_raw(self, display, mode):
        config = C.c_void_p()
        result = self.cg.CGBeginDisplayConfiguration(C.byref(config))
        if result:
            raise RuntimeError(f'Cannot begin display configuration: {result}')
        completed = False
        try:
            result = self.cg.CGConfigureDisplayWithDisplayMode(config, display, mode, None)
            if result:
                raise RuntimeError(f'Cannot configure selected display: {result}')
            completed = True  # Complete consumes config, including on refusal.
            result = self.cg.CGCompleteDisplayConfiguration(config, 1)  # kCGConfigureForSession
            if result:
                raise RuntimeError(f'Cannot complete session display configuration: {result}')
        finally:
            if not completed:
                self.cg.CGCancelDisplayConfiguration(config)

    def retain_current(self, display):
        return self.owned(self.cg.CGDisplayCopyDisplayMode(display))


def session_token(run=subprocess.run):
    result = run(['/bin/launchctl', 'print', f'gui/{os.getuid()}'], capture_output=True, text=True, timeout=10, check=False)
    header = '\n'.join(result.stdout.splitlines()[:25])
    asids = re.findall(r'^\s*asid = ([1-9][0-9]*)\s*$', header, re.M)
    if result.returncode or len(asids) != 1 or not re.search(r'^\s*session = Aqua\s*$', header, re.M):
        raise RuntimeError('Cannot confirm the paired user GUI login session')
    boot = run(['/usr/sbin/sysctl', '-n', 'kern.boottime'], capture_output=True, text=True, timeout=5, check=False)
    match = re.search(r'\bsec\s*=\s*([0-9]+),\s*usec\s*=\s*([0-9]+)', boot.stdout)
    if boot.returncode or not match:
        raise RuntimeError('Cannot confirm the current boot identity')
    return dict(gui_asid=int(asids[0]), boot_seconds=int(match[1]), boot_microseconds=int(match[2]))


def eligible(display):
    return display['active'] and not any(display[key] for key in ('main', 'builtin', 'mirrored'))


def mode_valid(mode):
    return (isinstance(mode, dict) and set(mode) == set(MODE_KEYS)
            and all(type(mode.get(key)) is int and 0 < mode[key] <= 65535
                    for key in ('width', 'height', 'pixel_width', 'pixel_height'))
            and type(mode.get('id')) is int and -(2**31) <= mode['id'] < 2**31
            and type(mode.get('flags')) is int and 0 <= mode['flags'] < 2**32
            and type(mode.get('usable')) is bool
            and type(mode.get('refresh_hz')) in (int, float) and math.isfinite(mode['refresh_hz']))


def reduced(before, after):
    return (mode_valid(before) and mode_valid(after) and before['usable'] and after['usable']
            and before['refresh_hz'] == after['refresh_hz'] == 60
            and before['width'] == after['width'] and before['height'] == after['height']
            and after['pixel_width'] * after['pixel_height'] * 4 <= before['pixel_width'] * before['pixel_height'])


def valid_record(value):
    if (not isinstance(value, dict) or type(value.get('schema')) is not int or value['schema'] != 1
            or value.get('owner') != OWNER or value.get('phase') not in ('pending', 'applied', 'restored')
            or not reduced(value.get('before'), value.get('after'))
            or not isinstance(value.get('session'), dict)
            or set(value['session']) != {'gui_asid', 'boot_seconds', 'boot_microseconds'}
            or any(type(x) is not int or x < 0 for x in value['session'].values())):
        raise ValueError('Unrecognized display optimization receipt')
    identity = value.get('identity')
    if (not isinstance(identity, dict) or set(identity) != {'display_id', 'uuid', 'vendor', 'model', 'serial'}
            or any(type(identity[k]) is not int or not 0 <= identity[k] < 2**32 for k in ('display_id', 'vendor', 'model', 'serial'))
            or not isinstance(identity['uuid'], str) or not re.fullmatch('[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}', identity['uuid'])):
        raise ValueError('Invalid managed display identity')
    return value


def write_record(path, value):
    handle, temporary = tempfile.mkstemp(prefix='.reims-', dir=path.parent)
    try:
        with os.fdopen(handle, 'wb') as stream:
            stream.write(json.dumps(value).encode('utf-8'))
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def current(displays, identity):
    # shortcut: identical ID/UUID/mode reuse on reconnect is not observable.
    # A resident observer is needed only if automatic reconnect handling is added.
    matches = [d for d in displays if d['identity'] == identity and eligible(d)]
    if len(matches) != 1:
        raise RuntimeError('The managed extended display is disconnected or its identity/role changed')
    return matches[0]


def main_display(displays):
    matches = [d for d in displays if d['main'] and d['active']]
    if len(matches) != 1:
        raise RuntimeError('Cannot identify one active main display')
    return matches[0]


def optimize(operation, graphics, token, receipt):
    old = valid_record(json.loads(receipt.read_bytes())) if receipt.exists() else None
    inventory = graphics.inventory()
    primary = main_display(inventory)
    if operation == 'restore':
        if not old or old['phase'] == 'restored':
            raise RuntimeError('There is no managed display optimization to restore')
        if old['session'] != token:
            raise RuntimeError('The managed login session ended; no display was changed')
        target = current(inventory, old['identity'])
        if old['phase'] == 'pending' and target['mode'] == old['before']:
            old['phase'] = 'restored'
            write_record(receipt, old)
            return dict(schema=1, operation='restore', applied=False, already_applied=False,
                        main_unchanged=True, display_id=old['identity']['display_id'], before=target['mode'], after=target['mode'])
        if target['mode'] != old['after']:
            raise RuntimeError('The display changed outside Manager; refusing to overwrite it')
        desired, next_phase = old['before'], 'restored'
        record = dict(old)
    else:
        if old and old['phase'] != 'restored' and old['session'] == token:
            matching = [d for d in inventory if d['identity'] == old['identity']]
            if matching:
                managed = current(inventory, old['identity'])
                if managed['mode'] == old['after']:
                    return dict(schema=1, operation='lighten', applied=True, already_applied=True,
                                main_unchanged=True, display_id=old['identity']['display_id'], before=old['before'], after=old['after'])
                raise RuntimeError('The managed display changed outside Manager; refusing a new baseline')
        candidates = []
        for display in inventory:
            if eligible(display):
                options = [m for m in graphics.modes(display['identity']['display_id']) if reduced(display['mode'], m)]
                if options:
                    smallest = min(m['pixel_width'] * m['pixel_height'] for m in options)
                    options = [m for m in options if m['pixel_width'] * m['pixel_height'] == smallest]
                    if len(options) != 1:
                        raise RuntimeError('More than one lightweight mode matches; no display was changed')
                    candidates.append((display, options[0]))
        if len(candidates) != 1:
            raise RuntimeError('Exactly one eligible extended display is required; no display was changed')
        target, desired = candidates[0]
        record = dict(schema=1, owner=OWNER, phase='pending', session=token,
                      identity=target['identity'], before=target['mode'], after=desired)
        valid_record(record)
        next_phase = 'applied'

    display_id = target['identity']['display_id']
    if desired not in graphics.modes(display_id):
        raise RuntimeError('The exact saved display mode is no longer offered')
    if graphics.inventory() != inventory:
        raise RuntimeError('Displays changed before the transaction; no display was changed')
    previous_receipt = receipt.read_bytes() if receipt.exists() else None
    expected_receipt = previous_receipt
    attempted = False
    with graphics.retain_current(display_id) as original:
        if graphics.describe(original) != target['mode']:
            raise RuntimeError('The display changed while retaining its original mode')
        try:
            if operation == 'lighten':
                write_record(receipt, record)  # Retain recovery data even if the process ends mid-call.
                expected_receipt = receipt.read_bytes()
            attempted = True
            graphics.set_mode(display_id, desired)
            after = graphics.inventory()
            managed = current(after, target['identity'])
            if main_display(after) != primary or managed['mode'] != desired:
                raise RuntimeError('Display transaction verification failed')
            if receipt.is_symlink() or (receipt.read_bytes() if receipt.exists() else None) != expected_receipt:
                raise RuntimeError('Display receipt changed during the transaction')
            record['phase'] = next_phase
            write_record(receipt, record)
            return dict(schema=1, operation=operation, applied=operation == 'lighten', already_applied=False,
                        main_unchanged=True, display_id=display_id, before=target['mode'], after=managed['mode'])
        except Exception as error:
            try:
                if receipt.is_symlink() or (receipt.read_bytes() if receipt.exists() else None) != expected_receipt:
                    raise RuntimeError('Refusing to overwrite a changed display receipt')
                if attempted:
                    now = graphics.inventory()
                    managed = current(now, target['identity'])
                    if managed['mode'] not in (target['mode'], desired):
                        raise RuntimeError('Display changed outside Manager during rollback')
                    graphics.set_raw(display_id, original)
                    restored = graphics.inventory()
                    if current(restored, target['identity'])['mode'] != target['mode'] or main_display(restored) != primary:
                        raise RuntimeError('Original display configuration could not be verified')
                if previous_receipt is None:
                    receipt.unlink(missing_ok=True)
                else:
                    write_record(receipt, json.loads(previous_receipt))
            except Exception as rollback:
                raise RuntimeError(f'{error}; rollback unconfirmed: {rollback}') from error
            raise


def check_owned_path(path, home):
    if path != home:
        check_owned_path(path.parent, home)
    if path.is_symlink():
        raise ValueError('Display optimization path is a symbolic link')
    if path.exists() and os.name == 'posix':
        info = path.stat()
        if info.st_uid != os.getuid() or info.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
            raise ValueError('Display optimization path is not private to this user')


def main():
    raw = sys.stdin.buffer.read(4097)
    if len(raw) > 4096:
        raise ValueError('Display request is too large')
    request = json.loads(raw)
    if (not isinstance(request, dict) or set(request) != {'schema', 'operation'}
            or type(request['schema']) is not int or request['schema'] != 1
            or request['operation'] not in ('lighten', 'restore')):
        raise ValueError('Invalid fixed display operation')
    home = Path.home()
    directory = home / 'Library/Application Support/Reims/DisplayOptimization'
    receipt = directory / 'receipt.json'
    lock = directory / '.lock'
    check_owned_path(receipt, home)
    check_owned_path(lock, home)
    if directory.exists():
        if set(p.name for p in directory.iterdir()) - {'receipt.json', '.lock'} or (lock.exists() and (not lock.is_file() or lock.stat().st_size != 0)):
            raise ValueError('Existing display optimization files are not owned by Manager')
        if receipt.exists():
            if not receipt.is_file() or receipt.stat().st_size > 16384:
                raise ValueError('Invalid display receipt size or type')
            valid_record(json.loads(receipt.read_bytes()))
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    import fcntl
    handle = os.open(lock, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        print(json.dumps(optimize(request['operation'], CoreGraphics(), session_token(), receipt)))
    finally:
        os.close(handle)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'Reims display optimization: {error}', file=sys.stderr)
        sys.exit(1)
