import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('package_engine', Path(__file__).with_name('package_engine.py'))
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


def pe(import_name=None, delayed=False):
    data = bytearray(2048)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3c, 128)
    data[128:132] = b'PE\0\0'
    struct.pack_into('<HH', data, 132, 0x8664, 1)
    struct.pack_into('<H', data, 148, 240)
    struct.pack_into('<H', data, 152, 0x20b)
    struct.pack_into('<IIII', data, 400, 1024, 4096, 1024, 512)
    if import_name:
        directory = 13 if delayed else 1
        struct.pack_into('<II', data, 264+directory*8, 4096, 64 if delayed else 40)
        if delayed:
            struct.pack_into('<II', data, 512, 1, 4352)
        else:
            struct.pack_into('<I', data, 524, 4352)
        value = import_name.encode()+b'\0'
        data[768:768+len(value)] = value
    return data


class PackagingTests(unittest.TestCase):
    def test_recursive_dll_closure_and_missing_dependency(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for folder in ('input', 'output', 'system'):
                (root/folder).mkdir()
            source = root/'input/qemu.exe'
            source.write_bytes(pe('first.dll'))
            (root/'input/first.dll').write_bytes(pe('SECOND.dll', delayed=True))
            (root/'input/second.dll').write_bytes(pe('kernel32.dll'))
            (root/'system/kernel32.dll').write_bytes(b'Windows fixture')
            copied = package.collect_dlls([source], root/'output', [root/'input'], root/'system')
            self.assertEqual(copied, ['first.dll', 'qemu.exe', 'second.dll'])
            (root/'input/second.dll').unlink()
            with self.assertRaisesRegex(ValueError, 'Missing DLL dependency'):
                package.collect_dlls([source], root/'output', [root/'input'], root/'system')

    def test_reject_non_pe_and_wrong_architecture(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)/'bad.exe'
            path.write_bytes(b'not PE')
            with self.assertRaises(ValueError):
                package.pe_imports(path)
            data = pe()
            struct.pack_into('<H', data, 132, 0x14c)
            path.write_bytes(data)
            with self.assertRaisesRegex(ValueError, 'Expected x64'):
                package.pe_imports(path)


if __name__ == '__main__':
    unittest.main()
