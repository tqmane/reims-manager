"""Collect a Windows engine without relying on the build host's DLL search path."""
import argparse
import bz2
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct


def pe_imports(path):
    data = path.read_bytes()
    if data[:2] != b'MZ':
        raise ValueError(f'Not a PE executable: {path.name}')
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[pe:pe+4] != b'PE\0\0':
        raise ValueError(f'Invalid PE header: {path.name}')
    machine, sections = struct.unpack_from('<HH', data, pe+4)
    if machine != 0x8664:
        raise ValueError(f'Expected x64 PE: {path.name}')
    optional = pe+24
    if struct.unpack_from('<H', data, optional)[0] != 0x20b:
        raise ValueError('Expected PE32+')
    table = optional + struct.unpack_from('<H', data, pe+20)[0]
    image_base = struct.unpack_from('<Q', data, optional+24)[0]

    def offset(rva):
        for i in range(sections):
            size, virtual, raw_size, raw = struct.unpack_from('<IIII', data, table+i*40+8)
            if virtual <= rva < virtual+max(size, raw_size):
                value = raw+rva-virtual
                if value >= len(data):
                    break
                return value
        raise ValueError(f'PE RVA outside file: {path.name}')

    def name(rva):
        start = offset(rva)
        value = data[start:data.index(b'\0', start)].decode('ascii').lower()
        if not value.endswith('.dll') or any(c in value for c in '/\\:'):
            raise ValueError(f'Invalid DLL name: {value}')
        return value

    imports = set()
    # Both ordinary and delay-load imports participate in the closure.
    for directory, stride in [(1, 20), (13, 32)]:
        rva, size = struct.unpack_from('<II', data, optional+112+directory*8)
        if not rva:
            continue
        start = offset(rva)
        for at in range(start, start+size, stride):
            descriptor = struct.unpack_from('<'+'I'*(stride//4), data, at)
            if not any(descriptor):
                break
            rva = descriptor[3] if directory == 1 else descriptor[1]
            if directory == 13 and not descriptor[0] & 1:
                rva -= image_base
            imports.add(name(rva))
    return imports


def collect_dlls(seeds, destination, search, system):
    candidates = {}
    for directory in reversed(search):
        candidates.update({p.name.lower(): p for p in directory.glob('*.dll')})
    windows = {p.name.lower() for p in system.glob('*.dll')}
    queue = list(seeds)
    seen = set()
    while queue:
        source = queue.pop()
        name = source.name.lower()
        if name in seen:
            continue
        seen.add(name)
        shutil.copy2(source, destination/source.name)
        for dependency in pe_imports(source):
            if dependency in candidates:
                queue.append(candidates[dependency])
            elif dependency not in windows and not dependency.startswith(('api-ms-win-', 'ext-ms-win-')):
                raise ValueError(f'Missing DLL dependency: {source.name} -> {dependency}')
    return sorted(seen)


def main():
    parser = argparse.ArgumentParser()
    for name in ('source', 'msys', 'downloads', 'sevenzip', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    lock = json.loads((Path(__file__).parent.parent/'backend/engine-lock.json').read_text())
    out = args.output
    out.mkdir(parents=True, exist_ok=False)
    for folder in ('bin', 'share/qemu', 'share/reims', 'downloads', 'licenses'):
        (out/folder).mkdir(parents=True, exist_ok=True)
    qemu = args.source/'build/qemu'
    runtime = args.msys/'ucrt64/bin'
    seeds = [qemu/n for n in ('qemu-system-x86_64.exe', 'qemu-img.exe', 'qemu-io.exe', 'reims_vgpu_c.dll')]
    # Vulkan is loaded dynamically, so the PE import table cannot discover it.
    seeds += [runtime/'vulkan-1.dll', args.sevenzip/'7z.exe', args.sevenzip/'7z.dll']
    collect_dlls(seeds, out/'bin', [qemu, args.sevenzip, runtime], Path(os.environ['SystemRoot'])/'System32')
    bios = args.source/'vendor/qemu/pc-bios'
    for name in ('bios.bin', 'bios-256k.bin', 'vgabios-stdvga.bin', 'kvmvapic.bin', 'efi-e1000.rom', 'efi-e1000e.rom', 'efi-virtio.rom', 'efi-vmxnet3.rom'):
        shutil.copy2(bios/name, out/'share/qemu'/name)
    for name, digest in lock['firmware']['files'].items():
        source = args.downloads/name
        if hashlib.sha256(source.read_bytes()).hexdigest() != digest:
            raise ValueError(f'Firmware download changed: {name}')
        target = out/'share/qemu'/name.removesuffix('.bz2')
        target.write_bytes(bz2.decompress(source.read_bytes()) if name.endswith('.bz2') else source.read_bytes())
    shutil.copy2(qemu/'ReimsVgpu.rom', out/'share/reims/ReimsVgpu.rom')
    shutil.copy2(args.downloads/lock['opencore']['file'], out/'downloads'/lock['opencore']['file'])
    for name in ('GPL-2.0-or-later.txt', 'GPL-3.0-or-later.txt', 'LGPL-3.0-or-later.txt'):
        # Reims uses SPDX-named LICENSES; fail rather than omit its licenses.
        shutil.copy2(args.source/'LICENSES'/name, out/'licenses'/name)
    shutil.copy2(args.source/'vendor/qemu/COPYING', out/'licenses/QEMU-COPYING')
    shutil.copytree(args.msys/'ucrt64/share/licenses', out/'licenses/native-dependencies')
    for name in ('License.txt', 'readme.txt'):
        shutil.copy2(args.sevenzip/name, out/'licenses'/('7zip-'+name))
    shutil.copy2(args.source/'.work/diagnostics/msys2-packages-current.txt', out/'licenses/msys2-packages.txt')
    shutil.copy2(Path(__file__).parent.parent/'docs/engine-setup.md', out/'README.md')
    paths = dict(qemu='bin/qemu-system-x86_64.exe', qemu_img='bin/qemu-img.exe', qemu_io='bin/qemu-io.exe',
                 seven_zip='bin/7z.exe', firmware='share/qemu', firmware_code='share/qemu/edk2-x86_64-code.fd',
                 firmware_vars='share/qemu/edk2-i386-vars.fd', rom='share/reims/ReimsVgpu.rom',
                 opencore_archive='downloads/'+lock['opencore']['file'])
    files = {p.relative_to(out).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
             for p in sorted(out.rglob('*')) if p.is_file()}
    manifest = dict(schema=1, id=lock['id'], sources=lock['sources'], paths=paths, files=files)
    (out/'reims-engine.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')


if __name__ == '__main__':
    main()
