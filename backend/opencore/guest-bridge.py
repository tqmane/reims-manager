"""Validate the standalone bridge package and modify only its OpenCore settings."""
import argparse
import copy
import hashlib
import json
from pathlib import Path, PurePosixPath
import plistlib
import shutil

APPLE = '7C436110-AB2A-4BBB-A880-FE41995C9F82'
BRIDGE = 'ReimsMetalCompat.kext'
LILU = 'Lilu.kext'
PACKAGE_FIELDS = ('schema', 'id', 'required_engine_id', 'min_kernel', 'max_kernel', 'sources', 'lilu_release',
                  'abi_family', 'scope', 'runtime_validation', 'release_ready', 'qualified_kernels')


def require_qualified(pin):
    if (not pin['release_ready'] or pin['scope'] != 'generic-metal'
            or pin['runtime_validation'] != 'abi-checked' or not pin['qualified_kernels']):
        raise ValueError('Guest bridge is still a candidate: generic Metal routing and runtime ABI validation must be qualified before release or application')
    bounds = [tuple(map(int, pin[field].split('.'))) for field in ('min_kernel', 'max_kernel')]
    if pin['abi_family'] != 'Darwin25' or any(len(v) != 3 or v[0] != 25 for v in bounds) or bounds[0] > bounds[1]:
        raise ValueError('Expected ordered Darwin 25 ABI bounds')
    for kernel in pin['qualified_kernels']:
        value = tuple(map(int, kernel.split('.')))
        if len(value) != 3 or not bounds[0] <= value <= bounds[1]:
            raise ValueError('Qualified kernel falls outside the package ABI bounds')


def hashes(root):
    result = {}
    for path in sorted(root.rglob('*')):
        if path.is_symlink():
            raise ValueError('Package and kext directories cannot contain links')
        if path.is_file():
            result[path.relative_to(root).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def verify(root, pin):
    manifest = json.loads((root/'bridge-manifest.json').read_text(encoding='utf-8'))
    for field in PACKAGE_FIELDS:
        if manifest.get(field) != pin[field]:
            raise ValueError('Bridge package pin mismatch: '+field)
    expected = manifest['files']
    if not expected or any('\\' in name or ':' in name or PurePosixPath(name).is_absolute()
                           or '..' in PurePosixPath(name).parts for name in expected):
        raise ValueError('Invalid bridge file manifest')
    actual = hashes(root)
    actual.pop('bridge-manifest.json')
    if actual != expected:
        raise ValueError('Bridge package files changed or are incomplete')
    source = json.loads((root/'source/source-manifest.json').read_text(encoding='utf-8'))
    if source['schema'] != 1 or source['revision'] != pin['sources']['reims']['commit']:
        raise ValueError('Unexpected bridge source snapshot')
    source_files = hashes(root/'source'); source_files.pop('source-manifest.json')
    if source_files != source['files']:
        raise ValueError('Authored source snapshot is incomplete')
    signed_source = root/'Kexts'/BRIDGE/'Contents/Resources/source-manifest.json'
    if json.loads(signed_source.read_text(encoding='utf-8')) != source:
        raise ValueError('Built bridge provenance differs from delivered source')
    for name, identifier, version in ((BRIDGE, 'io.reims.MetalCompat', '0.1.0'),
                                       (LILU, 'as.vit9696.Lilu', pin['lilu_release']['version'])):
        bundle = root/'Kexts'/name
        info = plistlib.loads((bundle/'Contents/Info.plist').read_bytes())
        executable = name.removesuffix('.kext')
        if (info['CFBundleIdentifier'], info['CFBundleVersion'], info['CFBundleExecutable']) != (identifier, version, executable):
            raise ValueError('Unexpected kext identity: '+name)
        if not (bundle/'Contents/MacOS'/executable).is_file():
            raise ValueError('Missing kext executable: '+name)
    return manifest


def patch_config(config, pin, enable):
    if enable:
        require_qualified(pin)
    result = copy.deepcopy(config)
    entries = result['Kernel']['Add']
    bridge = [x for x in entries if x['BundlePath'] == BRIDGE]
    lilu = [x for x in entries if x['BundlePath'] == LILU]
    if len(bridge) > 1 or len(lilu) > 1:
        raise ValueError('Duplicate bridge/Lilu entries; preserve and resolve the existing configuration first')
    if enable and bridge:
        low, high = bridge[0].get('MinKernel', ''), bridge[0].get('MaxKernel', '')
        version = lambda s: tuple(map(int, s.split('.')))
        if (not low or not high or version(low) < version(pin['min_kernel'])
                or version(high) > version(pin['max_kernel']) or version(low) > version(high)):
            raise ValueError('Existing bridge kernel bounds differ from this ABI family; they were not changed')
    nv = result['NVRAM']['Add'][APPLE]
    args = [arg for arg in nv.get('boot-args', '').split() if arg not in ('-reimsmetal', '-reimsmetaloff')]
    if not enable:
        if bridge:
            bridge[0]['Enabled'] = False
        nv['boot-args'] = ' '.join(args)
        result['NVRAM'].setdefault('Delete', {}).setdefault(APPLE, [])
        if 'boot-args' not in result['NVRAM']['Delete'][APPLE]:
            result['NVRAM']['Delete'][APPLE].append('boot-args')
        return result
    if lilu and (not lilu[0]['Enabled'] or lilu[0].get('Arch', 'Any') not in ('Any', 'x86_64')):
        raise ValueError('Existing Lilu is disabled or has a different architecture')
    if bridge and bridge[0].get('Arch', 'Any') not in ('Any', 'x86_64'):
        raise ValueError('Existing bridge has a different architecture')
    if lilu:
        version = lambda s: tuple(map(int, s.split('.')))
        low, high = lilu[0].get('MinKernel', ''), lilu[0].get('MaxKernel', '')
        if lilu[0].get('Comment') == 'Reims Manager WebGL' and low and high:
            if not version(pin['min_kernel']) <= version(low) <= version(high) <= version(pin['max_kernel']):
                raise ValueError('Owned Lilu bounds are outside this package ABI family')
            lilu[0].update(MinKernel=pin['min_kernel'], MaxKernel=pin['max_kernel'])
            low, high = pin['min_kernel'], pin['max_kernel']
        if (low and version(low) > version(pin['min_kernel'])) or (high and version(high) < version(pin['max_kernel'])):
            raise ValueError('Existing Lilu excludes this kernel; its bounds were not changed')
    def entry(name):
        return dict(Arch='x86_64', BundlePath=name, Comment='Reims Manager WebGL', Enabled=True,
                    ExecutablePath='Contents/MacOS/'+name.removesuffix('.kext'),
                    MinKernel=pin['min_kernel'], MaxKernel=pin['max_kernel'], PlistPath='Contents/Info.plist')
    if not lilu:
        lilu = [entry(LILU)]
        entries.insert(0, lilu[0])
    if bridge:
        bridge[0]['Enabled'] = True
        bridge[0].update(MinKernel=pin['min_kernel'], MaxKernel=pin['max_kernel'])
        entries.remove(bridge[0])
    else:
        bridge = [entry(BRIDGE)]
    entries.insert(entries.index(lilu[0])+1, bridge[0])
    result['Kernel']['Quirks']['DisableLinkeditJettison'] = True
    nv['boot-args'] = ' '.join(args+['-reimsmetal'])
    deleted = result['NVRAM'].setdefault('Delete', {}).setdefault(APPLE, [])
    if 'boot-args' not in deleted:
        deleted.append('boot-args')
    return result


def apply(root, pin, enable, package=None):
    # The caller supplies a new, private EFI copy; the active image is never edited here.
    config_path = root/'EFI/OC/config.plist'
    config = plistlib.loads(config_path.read_bytes())
    candidate = patch_config(config, pin, enable)
    if enable:
        verify(package, pin)
        kexts = root/'EFI/OC/Kexts'; kexts.mkdir(exist_ok=True)
        for name in (LILU, BRIDGE):
            destination = kexts/name
            if destination.exists():
                if name == LILU:
                    if hashes(destination) != hashes(package/'Kexts'/name):
                        raise ValueError('Existing Lilu differs from the qualified package; it was not replaced')
                    continue
                identity = plistlib.loads((destination/'Contents/Info.plist').read_bytes())
                if identity['CFBundleIdentifier'] != 'io.reims.MetalCompat':
                    raise ValueError('Refusing to replace an unrelated kext')
                shutil.rmtree(destination)
            shutil.copytree(package/'Kexts'/name, destination)
    config_path.write_bytes(plistlib.dumps(candidate, sort_keys=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['verify', 'enable', 'disable', 'check-release'])
    parser.add_argument('root', type=Path)
    parser.add_argument('pin', type=Path)
    parser.add_argument('--package', type=Path)
    args = parser.parse_args()
    pin = json.loads(args.pin.read_text(encoding='utf-8-sig'))
    if args.action == 'check-release':
        require_qualified(pin)
    elif args.action == 'verify':
        verify(args.root, pin)
    else:
        if args.action == 'enable' and args.package is None:
            parser.error('enable requires --package')
        apply(args.root, pin, args.action == 'enable', args.package)
