"""Build the authored x86 kext on macOS, separately from the Windows engine."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import urllib.request
import zipfile

REPO = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('bridge', REPO/'backend/opencore/guest-bridge.py')
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


def run(*args, **kwargs):
    subprocess.run([str(x) for x in args], check=True, **kwargs)


def checkout(source, path):
    run('git', 'init', path)
    run('git', '-C', path, 'config', 'core.autocrlf', 'false')
    run('git', '-C', path, 'remote', 'add', 'origin', source['repository'])
    run('git', '-C', path, 'fetch', '--depth=1', 'origin', source['commit'])
    run('git', '-C', path, 'checkout', '--detach', 'FETCH_HEAD')
    actual = subprocess.check_output(['git', '-C', str(path), 'rev-parse', 'HEAD'], text=True).strip()
    if actual != source['commit']:
        raise ValueError('Source checkout mismatch')


def main():
    if platform.system() != 'Darwin':
        raise SystemExit('Build the guest bridge on a macOS runner with Xcode command-line tools.')
    pin = json.loads((REPO/'backend/guest-bridge-lock.json').read_text())
    work = REPO/'.work/guest-bridge-build'
    work.mkdir(parents=True, exist_ok=False)
    os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
    os.environ['TMPDIR'] = str(work/'tmp'); (work/'tmp').mkdir()
    for name, source in pin['sources'].items():
        checkout(source, work/name)
    source = work/'reims/guest/metal-compat'
    snapshot = work/'package/source'
    run(sys.executable, source/'test_source_snapshot.py')
    run(sys.executable, source/'source_snapshot.py', 'export', snapshot)
    run('xcrun', 'clang++', '-std=c++17', '-Wall', '-Wextra', '-Werror', snapshot/'abi-test.cpp', '-o', work/'abi-test')
    run(work/'abi-test')
    run('bash', snapshot/'build.sh', work/'lilu', work/'kernel_sdk', work/'build')
    bundle = work/'build/ReimsMetalCompat.kext'
    run('codesign', '--verify', '--strict', bundle)
    run('xcrun', 'lipo', bundle/'Contents/MacOS/ReimsMetalCompat', '-verify_arch', 'x86_64')
    package = work/'package'; (package/'Kexts').mkdir()
    shutil.copytree(bundle, package/'Kexts/ReimsMetalCompat.kext')
    archive = work/'Lilu.zip'
    with urllib.request.urlopen(pin['lilu_release']['url'], timeout=120) as response, archive.open('wb') as target:
        shutil.copyfileobj(response, target)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != pin['lilu_release']['sha256']:
        raise ValueError('Official Lilu archive digest mismatch')
    with zipfile.ZipFile(archive) as entries:
        for entry in entries.infolist():
            if not entry.filename.startswith('Lilu.kext/'):
                continue
            parts = Path(entry.filename).parts
            if '..' in parts or '\\' in entry.filename or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError('Invalid Lilu archive entry')
            target = package/'Kexts'/entry.filename
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(entries.read(entry))
    run('xcrun', 'lipo', package/'Kexts/Lilu.kext/Contents/MacOS/Lilu', '-verify_arch', 'x86_64')
    licenses = package/'licenses'; licenses.mkdir()
    for name in ('lilu', 'kernel_sdk'):
        shutil.copy2(work/name/'LICENSE.txt', licenses/(name+'.txt'))
    shutil.copytree(work/'reims/LICENSES', licenses/'reims')
    shutil.copy2(REPO/'backend/opencore/guest-bridge.py', package/'apply-config.py')
    shutil.copy2(REPO/'backend/guest-bridge-lock.json', package/'guest-bridge-lock.json')
    shutil.copy2(REPO/'docs/guest-bridge.md', package/'README.md')
    for name, source_pin in pin['sources'].items():
        changes = subprocess.check_output(['git', '-C', str(work/name), 'status', '--porcelain', '--untracked-files=normal'])
        if changes:
            raise ValueError('Build source changed: '+name)
    manifest = {key: pin[key] for key in bridge.PACKAGE_FIELDS}
    manifest['build'] = {
        'manager_revision': subprocess.check_output(['git', '-C', str(REPO), 'rev-parse', 'HEAD'], text=True).strip(),
        'xcode': subprocess.check_output(['xcodebuild', '-version'], text=True).strip(),
        'bridge_uuid': subprocess.check_output(['xcrun', 'dwarfdump', '--uuid', str(bundle/'Contents/MacOS/ReimsMetalCompat')], text=True).split()[1]
    }
    manifest['files'] = {path.relative_to(package).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                         for path in sorted(package.rglob('*')) if path.is_file()}
    (package/'bridge-manifest.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
    run(sys.executable, REPO/'backend/opencore/guest-bridge.py', 'verify', package, REPO/'backend/guest-bridge-lock.json')
    output = REPO/'artifacts/guest-bridge'; output.mkdir(parents=True, exist_ok=True)
    archive = output/pin['asset']
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as target:
        for path in sorted(package.rglob('*')):
            if path.is_file():
                target.write(path, path.relative_to(package).as_posix())
    archive.with_suffix('.zip.sha256').write_text(hashlib.sha256(archive.read_bytes()).hexdigest()+'  '+archive.name+'\n', encoding='ascii')
    shutil.copy2(package/'bridge-manifest.json', output)
    print('Guest bridge package:', archive)


if __name__ == '__main__':
    main()
