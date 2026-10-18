"""Per-user installer for the authored Reims cursor aid. Request is JSON stdin."""
import base64
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import stat
import subprocess
import sys
import tempfile
import time

LABEL = 'io.github.tqmane.reims.cursor-aid'
EXECUTABLE = 'ReimsCursorAid'
MAX_BINARY = 20 * 1024 * 1024


def digest(data):
    return hashlib.sha256(data).hexdigest()


def manifest(value):
    if (not isinstance(value, dict) or type(value.get('schema')) is not int
            or value['schema'] != 1 or value.get('executable') != EXECUTABLE
            or not isinstance(value.get('sha256'), str)
            or not re.fullmatch('[0-9a-f]{64}', value['sha256'])
            or not isinstance(value.get('revision'), str)
            or not re.fullmatch('[0-9a-f]{40}', value['revision'])):
        raise ValueError('Invalid cursor aid manifest')
    return {key: value[key] for key in ('schema', 'executable', 'sha256', 'revision')}


def check_path(path, home, uid):
    # Reject symlinked ancestors as well as symlinked files before any mutation.
    if path != home:
        check_path(path.parent, home, uid)
    if path.is_symlink():
        raise ValueError('Cursor aid path is a symbolic link')
    if path.exists():
        info = path.stat()
        if os.name == 'posix' and info.st_uid != uid:
            raise ValueError('Cursor aid path belongs to another user')
        if os.name == 'posix' and info.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
            raise ValueError('Cursor aid path is writable by another user')


def read_file(path, limit):
    if not path.is_file() or path.stat().st_size > limit:
        raise ValueError('Invalid cursor aid file size or type')
    return path.read_bytes()


def write_file(path, data, mode):
    handle, name = tempfile.mkstemp(prefix='.reims-', dir=path.parent)
    try:
        with os.fdopen(handle, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(name, mode)
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


class LaunchAgent:
    def __init__(self, uid, agent, executable, run=subprocess.run):
        self.domain = f'gui/{uid}'
        self.service = f'{self.domain}/{LABEL}'
        self.agent = agent
        self.executable = executable
        self.run = run

    def call(self, *args):
        return self.run(['/bin/launchctl', *args], capture_output=True,
                        text=True, timeout=10, check=False)

    def require(self, *args):
        result = self.call(*args)
        if result.returncode:
            raise RuntimeError(f'launchctl {args[0]} failed: {result.stderr.strip()[:512]}')
        return result.stdout

    def loaded(self):
        result = self.call('print', self.service)
        if result.returncode:
            if result.returncode == 113:  # launchctl: service not found in this domain.
                return None
            raise RuntimeError('Unable to inspect the cursor aid LaunchAgent')
        # Refuse a different job using our label, even if files on disk are ours.
        for field, expected in (('path', self.agent), ('program', self.executable)):
            match = re.search(r'^\s*' + field + r' = (.+)$', result.stdout, re.M)
            if not match or match.group(1) != str(expected):
                raise ValueError('Loaded cursor aid service has an unowned path')
        return result.stdout

    def stop(self):
        if self.loaded() is not None:
            self.require('bootout', self.service)
            if self.loaded() is not None:
                raise RuntimeError('Cursor aid service is still loaded after bootout')

    def start(self):
        self.require('enable', self.service)
        self.require('bootstrap', self.domain, str(self.agent))
        self.require('kickstart', self.service)
        deadline = time.monotonic() + 5
        while True:
            output = self.loaded()
            match = re.search(r'^\s*pid = ([1-9][0-9]*)$', output or '', re.M)
            if match:
                return int(match.group(1))
            if time.monotonic() >= deadline:
                raise RuntimeError('Cursor aid did not confirm a running LaunchAgent')
            time.sleep(0.1)


def apply(request, home, uid, run=subprocess.run):
    if (not isinstance(request, dict) or type(request.get('schema')) is not int
            or request['schema'] != 1 or request.get('operation') not in ('enable', 'disable')):
        raise ValueError('Invalid cursor aid request')
    enable = request['operation'] == 'enable'
    incoming = manifest(request.get('manifest')) if enable else None
    payload = None
    if enable:
        encoded = request.get('payload')
        if not isinstance(encoded, str) or len(encoded) > (MAX_BINARY + 2) // 3 * 4:
            raise ValueError('Invalid cursor aid payload size')
        payload = base64.b64decode(encoded, validate=True)
        if not 0 < len(payload) <= MAX_BINARY or digest(payload) != incoming['sha256']:
            raise ValueError('Cursor aid payload does not match its manifest')

    directory = home / 'Library/Application Support/Reims/CursorAid'
    executable = directory / EXECUTABLE
    receipt_path = directory / 'receipt.json'
    agent = home / f'Library/LaunchAgents/{LABEL}.plist'
    for path in (executable, receipt_path, agent):
        check_path(path, home, uid)
    plist = plistlib.dumps(dict(Label=LABEL, ProgramArguments=[str(executable)],
                               RunAtLoad=True, KeepAlive=True,
                               LimitLoadToSessionType='Aqua', ProcessType='Interactive',
                               StandardOutPath='/dev/null', StandardErrorPath='/dev/null'),
                          sort_keys=True)
    previous = None
    if receipt_path.exists():
        previous = json.loads(read_file(receipt_path, 4096))
        if (not isinstance(previous, dict) or type(previous.get('schema')) is not int
                or previous['schema'] != 1 or previous.get('owner') != LABEL
                or type(previous.get('enabled')) is not bool
                or previous.get('plist_sha256') != digest(plist)):
            raise ValueError('Unrecognized cursor aid ownership receipt')
        old_manifest = manifest(previous.get('manifest'))
        if digest(read_file(executable, MAX_BINARY)) != old_manifest['sha256']:
            raise ValueError('Installed cursor aid was changed outside Manager')
        if previous['enabled'] != agent.exists():
            raise ValueError('Cursor aid agent does not match its ownership receipt')
        if agent.exists() and read_file(agent, 16384) != plist:
            raise ValueError('Cursor aid LaunchAgent was changed outside Manager')
        if set(item.name for item in directory.iterdir()) != {EXECUTABLE, 'receipt.json'}:
            raise ValueError('Cursor aid directory contains unowned files')
    elif directory.exists() or agent.exists():
        raise ValueError('Existing cursor aid files have no ownership receipt')

    control = LaunchAgent(uid, agent, executable, run)
    control.require('print', control.domain)  # Requires the paired user's GUI login.
    loaded_output = control.loaded()
    loaded = loaded_output is not None
    if loaded and (not previous or not previous['enabled']):
        raise ValueError('Loaded cursor aid has no enabled ownership receipt')
    if not enable and not previous:
        return dict(schema=1, enabled=False, pid=None)
    if enable and previous and previous['enabled'] and incoming == manifest(previous['manifest']):
        running = re.search(r'^\s*pid = ([1-9][0-9]*)$', loaded_output or '', re.M)
        if running:
            return dict(schema=1, enabled=True, pid=int(running.group(1)))
    if not enable and not previous['enabled']:
        return dict(schema=1, enabled=False, pid=None)

    paths = (executable, agent, receipt_path)
    before = {path: path.read_bytes() if path.exists() else None for path in paths}
    current = dict(before)
    created_directory = not directory.exists()
    attempted = False
    try:
        control.stop()
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        agent.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        record = dict(schema=1, owner=LABEL, enabled=enable,
                      manifest=incoming if enable else previous['manifest'],
                      plist_sha256=digest(plist))
        if enable:
            write_file(executable, payload, 0o755)
            current[executable] = payload
            write_file(agent, plist, 0o644)
            current[agent] = plist
        elif agent.exists():
            agent.unlink()
            current[agent] = None
        receipt_data = json.dumps(record).encode('utf-8')
        write_file(receipt_path, receipt_data, 0o600)
        current[receipt_path] = receipt_data
        attempted = enable
        pid = control.start() if enable else None
        return dict(schema=1, enabled=enable, pid=pid)
    except Exception as error:
        try:
            for path in paths:
                check_path(path, home, uid)
                actual = read_file(path, MAX_BINARY) if path.exists() else None
                if actual != current[path]:
                    raise ValueError('Cursor aid files changed during the operation')
            if attempted:
                control.stop()
            for path in paths:
                if before[path] is None:
                    if path.exists():
                        path.unlink()
                else:
                    mode = 0o755 if path == executable else 0o600 if path == receipt_path else 0o644
                    write_file(path, before[path], mode)
            if created_directory and directory.exists():
                directory.rmdir()
            if loaded:
                control.start()
        except Exception as rollback:
            raise RuntimeError(f'Cursor aid failed: {error}; rollback unconfirmed: {rollback}') from error
        raise


def main():
    raw = sys.stdin.buffer.read(32 * 1024 * 1024 + 1)
    if len(raw) > 32 * 1024 * 1024:
        raise ValueError('Cursor aid request is too large')
    request = json.loads(raw)
    print(json.dumps(apply(request, Path.home(), os.getuid())))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'Reims cursor aid: {error}', file=sys.stderr)
        sys.exit(1)
