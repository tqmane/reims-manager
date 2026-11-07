"""Authored sockets and native DLL mocks only: no device or service is contacted."""
import importlib.util
import io
from contextlib import redirect_stdout
import json
from pathlib import Path
import plistlib
import socket
import struct
import runpy
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("identity", Path(__file__).parents[1] / "iphone-usb-identity.py")
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)
UDID = "00008120-000000000000BEEF"
INSTANCE = "USB\\VID_05AC&PID_12A8\\" + UDID.replace("-", "")
MODE_INSTANCE = "USB\\VID_05AC&PID_1281\\AUTHORED-RECOVERY"
LIBRARY = str(Path(__file__).resolve().with_name("authored-libusb.dll"))


class NativeFunction:
    def __init__(self, owner, name, callback):
        self.owner, self.name, self.callback = owner, name, callback

    def __call__(self, *args):
        self.owner.calls.append(self.name)
        return self.callback(*args)


class Native:
    def __init__(self, serial="CPID:8015 ECID:000000000000BEEF IBFL:3C", instance=MODE_INSTANCE):
        self.rows = ["USB\\VID_05AC&PID_1281\\OTHER", instance]
        self.calls, self.controls, self.opened = [], [], []
        self.product, self.vendor, self.serial_index = int(instance.upper().split("PID_")[1][:4], 16), 0x05AC, 3
        self.serial = serial
        self.language_data = b"\x04\x03\x09\x04"
        self.serial_data = None
        self.init_error = self.list_error = self.descriptor_error = self.open_error = self.control_error = 0
        self.count_override = None
        for name, callback in (
                ("libusb_init", self.init), ("libusb_exit", lambda ctx: None),
                ("libusb_get_device_list", self.list_), ("libusb_free_device_list", self.free_list),
                ("libusb_get_device_descriptor", self.descriptor),
                ("reims_libusb_get_windows_instance_id", self.instance),
                ("libusb_open", self.open_), ("libusb_close", lambda handle: None),
                ("libusb_control_transfer", self.control)):
            setattr(self, name, NativeFunction(self, name, callback))

    def init(self, output):
        if not self.init_error:
            identity.C.cast(output, identity.C.POINTER(identity.C.c_void_p))[0] = 7
        return self.init_error

    def list_(self, ctx, output):
        if self.list_error:
            return self.list_error
        self.devices = (identity.C.c_void_p * len(self.rows))(*range(100, 100 + len(self.rows)))
        identity.C.cast(output, identity.C.POINTER(identity.C.POINTER(identity.C.c_void_p)))[0] = self.devices
        return len(self.rows) if self.count_override is None else self.count_override

    def free_list(self, devices, unref):
        if unref != 1:
            raise AssertionError("Device references were not released")

    def instance(self, device, output, length):
        value = self.rows[device - 100].encode("utf-8") + b"\0"
        if len(value) > length:
            raise AssertionError("Authored instance exceeds native buffer")
        identity.C.memmove(output, value, len(value))
        return 0

    def descriptor(self, device, output):
        desc = identity.C.cast(output, identity.C.POINTER(identity.Descriptor)).contents
        desc.length, desc.kind, desc.vendor, desc.product, desc.serial = 18, 1, self.vendor, self.product, self.serial_index
        return self.descriptor_error

    def open_(self, device, output):
        self.opened.append(device)
        if not self.open_error:
            identity.C.cast(output, identity.C.POINTER(identity.C.c_void_p))[0] = 42
        return self.open_error

    def control(self, handle, direction, request, value, language, output, length, timeout):
        self.controls.append((direction, request, value, language, length, timeout))
        if (direction, request, length, timeout) != (0x80, 6, 254, 2000):
            raise AssertionError("Only bounded standard descriptor reads are allowed")
        if self.control_error:
            return self.control_error
        if (value, language) == (0x0300, 0):
            data = self.language_data
        elif (value, language) == (0x0303, 0x0409):
            text = self.serial.encode("utf-16-le")
            data = self.serial_data if self.serial_data is not None else bytes((len(text) + 2, 3)) + text
        else:
            raise AssertionError("Unexpected USB descriptor request")
        identity.C.memmove(output, data, min(length, len(data)))
        return len(data)


def mux_reply(value, tag):
    data = plistlib.dumps(value)
    return struct.pack("<IIII", len(data) + 16, 1, 8, tag) + data


def phone_reply(value=None, error=False):
    data = plistlib.dumps({"Request": "GetValue", **({"Error": "NotTrusted"} if error else {"Value": value})})
    return struct.pack(">I", len(data)) + data


def row(serial=UDID, product=0x12A8, device=42):
    return {"DeviceID": device, "Properties": {"ConnectionType": "USB", "ProductID": product, "SerialNumber": serial}}


class Peer:
    def __init__(self, data):
        self.data, self.writes, self.closed = bytearray(data), [], False

    def recv(self, size):
        size = min(size, 7, len(self.data))
        result = bytes(self.data[:size])
        del self.data[:size]
        return result

    def sendall(self, value):
        self.writes.append(value)

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.closed = True


class IdentityTests(unittest.TestCase):
    def run_case(self, devices=None, actual=UDID, ecid=0xBEEF, error=False, instance=INSTANCE):
        peers = [Peer(mux_reply({"DeviceList": [row()] if devices is None else devices}, 1)),
                 Peer(mux_reply({"MessageType": "Result", "Number": 0}, 2) +
                      phone_reply(actual) + phone_reply(ecid, error))]
        pending = iter(peers)

        def connect(endpoint, timeout):
            self.assertEqual(endpoint, ("127.0.0.1", 27015))
            self.assertEqual(timeout, 5)
            return next(pending)
        return peers, lambda: identity.identify(instance, connect)

    def test_ipad_uses_exact_product_and_serial_without_opening_phone(self):
        ipad = INSTANCE.replace('12A8', '12AB')
        devices = [row('00008120-000000000000CAFE', device=41), row(product=0x12AB)]
        peers, run = self.run_case(devices=devices, instance=ipad)
        with patch.object(identity.C, 'CDLL', side_effect=AssertionError('Normal iPad loaded native DLL')):
            self.assertEqual(run()['ecid'], '000000000000beef')
        self.assertEqual(plistlib.loads(peers[1].writes[0][16:])['DeviceID'], 42)
        self.assertTrue(all(peer.closed for peer in peers))

    def test_ipad_rejects_phone_product_even_with_matching_serial(self):
        peers, run = self.run_case(instance=INSTANCE.replace('12A8', '12AB'))
        with self.assertRaisesRegex(ValueError, 'absent or ambiguous'):
            run()
        self.assertFalse(peers[1].writes)

    def test_selected_identity_and_ecid(self):
        peers, run = self.run_case()
        self.assertEqual(run()["ecid"], "000000000000beef")
        self.assertTrue(all(p.closed for p in peers))
        request = plistlib.loads(peers[1].writes[0][16:])
        self.assertEqual(request["DeviceID"], 42)
        self.assertEqual(request["PortNumber"], socket.htons(62078))
        self.assertEqual([plistlib.loads(v[4:])["Key"] for v in peers[1].writes[1:]], ["UniqueDeviceID", "UniqueChipID"])

    def test_absent_and_ambiguous(self):
        for devices in ([], [row(), row()]):
            peers, run = self.run_case(devices=devices)
            with self.assertRaisesRegex(ValueError, "absent or ambiguous"):
                run()
            self.assertFalse(peers[1].writes)

    def test_reused_device_number_refuses_before_ecid(self):
        peers, run = self.run_case(actual="00008120-000000000000CAFE")
        with self.assertRaisesRegex(ValueError, "changed"):
            run()
        self.assertEqual(len(peers[1].writes), 2)
        self.assertTrue(peers[1].closed)

    def test_untrusted_phone(self):
        peers, run = self.run_case(error=True)
        with self.assertRaisesRegex(ValueError, "unlock and trust"):
            run()
        self.assertTrue(peers[1].closed)

    def test_invalid_ecids(self):
        for ecid in (0, -1, True, "beef"):
            _, run = self.run_case(ecid=ecid)
            with self.assertRaisesRegex(ValueError, "ECID"):
                run()
        _, run = self.run_case()
        with patch.object(identity, "lockdown", side_effect=[UDID, 2**64]):
            with self.assertRaisesRegex(ValueError, "ECID"):
                run()

    def test_bad_instance_never_connects(self):
        def unexpected(*args, **kwargs):
            self.fail("invalid selection contacted the Apple service")
        for value in ("USB\\VID_05AC&PID_12A8\\*", INSTANCE.replace("12A8", "1228"),
                      INSTANCE.replace("12A8", "12A8&MI_01")):
            with self.assertRaisesRegex(ValueError, "exactly one"):
                identity.identify(value, unexpected)

    def test_normal_mode_never_loads_native_library(self):
        _, run = self.run_case()
        with patch.object(identity.C, "CDLL", side_effect=AssertionError("Normal mode loaded a native DLL")):
            self.assertEqual(run()["ecid"], "000000000000beef")

    def test_oversized_and_truncated_packets(self):
        for data in (struct.pack("<IIII", identity.LIMIT + 1, 1, 8, 1), b"abc"):
            with self.assertRaises(ValueError):
                identity.mux(Peer(data), {}, 1)


class ModeIdentityTests(unittest.TestCase):
    def identify(self, native, instance=MODE_INSTANCE):
        with patch.object(identity.C, "CDLL", return_value=native) as load:
            result = identity.identify(instance, native_library=LIBRARY)
        load.assert_called_once_with(LIBRARY, winmode=0x900)
        return result

    def test_all_supported_modes_only_open_the_exact_instance(self):
        for product in (*range(0x1280, 0x1284), *range(0x1220, 0x1228)):
            with self.subTest(product=product):
                instance = MODE_INSTANCE.replace("1281", f"{product:04X}")
                native = Native(instance=instance)
                result = self.identify(native, instance)
                self.assertEqual(result, {"schema": 1, "instance_id": instance, "ecid": "000000000000beef",
                                          "mode": "recovery" if product >= 0x1280 else "dfu"})
                self.assertEqual(native.opened, [101])
                self.assertEqual(native.controls, [(0x80, 6, 0x0300, 0, 254, 2000),
                                                   (0x80, 6, 0x0303, 0x0409, 254, 2000)])
                self.assertEqual(native.calls[-3:], ["libusb_close", "libusb_free_device_list", "libusb_exit"])

    def test_case_insensitive_exact_instance_with_revision(self):
        instance = MODE_INSTANCE.replace("1281", "1281&REV_0001")
        native = Native(instance=instance.lower())
        self.assertEqual(self.identify(native, instance)["ecid"], "000000000000beef")

    def test_cli_native_library_field_reaches_only_the_mock(self):
        native = Native()
        request = io.TextIOWrapper(io.BytesIO(json.dumps({"instance_id": MODE_INSTANCE,
                                                        "native_library": LIBRARY}).encode()))
        output = io.StringIO()
        with patch("sys.stdin", request), patch.object(identity.C, "CDLL", return_value=native), redirect_stdout(output):
            runpy.run_path(str(Path(identity.__file__)), run_name="__main__")
        self.assertEqual(json.loads(output.getvalue())["ecid"], "000000000000beef")
        self.assertEqual(native.opened, [101])

    def test_missing_relative_library_and_invalid_instances_do_not_load(self):
        with patch.object(identity.C, "CDLL", side_effect=AssertionError("Invalid request loaded a native DLL")):
            for library in (None, "relative.dll", 1):
                with self.assertRaisesRegex(ValueError, "absolute verified"):
                    identity.identify(MODE_INSTANCE, native_library=library)
            for instance in (MODE_INSTANCE + "\\OTHER", MODE_INSTANCE.replace("1281", "1281&MI_00"),
                             MODE_INSTANCE.replace("RECOVERY", "*"), MODE_INSTANCE.replace("1281", "1284"),
                             MODE_INSTANCE.replace("05AC", "FFFF"), MODE_INSTANCE + "\n"):
                with self.assertRaisesRegex(ValueError, "exactly one"):
                    identity.identify(instance, native_library=LIBRARY)

    def test_absent_or_duplicate_instance_refuses_before_open(self):
        for rows in ([], ["USB\\VID_05AC&PID_1281\\OTHER"], [MODE_INSTANCE, MODE_INSTANCE]):
            native = Native()
            native.rows = rows
            with self.assertRaisesRegex(ValueError, "absent or ambiguous"):
                self.identify(native)
            self.assertFalse(native.opened)
            self.assertEqual(native.calls[-2:], ["libusb_free_device_list", "libusb_exit"])

    def test_wrong_descriptor_or_missing_serial_refuses_before_open(self):
        for name, value in (("vendor", 0xFFFF), ("product", 0x1227), ("serial_index", 0)):
            native = Native()
            setattr(native, name, value)
            with self.assertRaisesRegex(ValueError, "descriptor does not match"):
                self.identify(native)
            self.assertFalse(native.opened)
            self.assertEqual(native.calls[-2:], ["libusb_free_device_list", "libusb_exit"])

    def test_native_failures_are_unsupported_and_cleanup_owned_resources(self):
        for name in ("init_error", "list_error", "descriptor_error", "open_error", "control_error"):
            native = Native()
            setattr(native, name, -12)
            with self.assertRaisesRegex(ValueError, "identity unsupported.*libusb error -12"):
                self.identify(native)
            self.assertEqual(native.calls.count("libusb_exit"), 0 if name == "init_error" else 1)
            self.assertEqual(native.calls.count("libusb_close"), 1 if name == "control_error" else 0)
        with patch.object(identity.C, "CDLL", side_effect=OSError("authored missing DLL")):
            with self.assertRaisesRegex(ValueError, "identity unsupported"):
                identity.identify(MODE_INSTANCE, native_library=LIBRARY)
        native = Native()
        del native.reims_libusb_get_windows_instance_id
        with self.assertRaisesRegex(ValueError, "identity unsupported"):
            self.identify(native)
        self.assertFalse(native.calls)

    def test_device_list_bound(self):
        native = Native()
        native.count_override = 1025
        with self.assertRaisesRegex(ValueError, "Invalid USB device list"):
            self.identify(native)
        self.assertFalse(native.opened)
        self.assertEqual(native.calls[-2:], ["libusb_free_device_list", "libusb_exit"])

    def test_malformed_string_descriptors_and_text_cleanup(self):
        for name, data in (("language_data", b"\x02\x03"), ("language_data", b"\x04\x03\0\0"),
                           ("serial_data", b"\x04\x01xx"), ("serial_data", b"\x06\x03xx"),
                           ("serial_data", b"\x03\x03x"), ("serial_data", b"\x04\x03\0\xd8"),
                           ("serial_data", b"\x04\x03\0\0")):
            native = Native()
            setattr(native, name, data)
            with self.assertRaisesRegex(ValueError, "identity unsupported"):
                self.identify(native)
            self.assertEqual(native.calls[-3:], ["libusb_close", "libusb_free_device_list", "libusb_exit"])
        with self.assertRaisesRegex(ValueError, "Invalid USB serial descriptor text"):
            self.identify(Native(serial="CPID:\u03a9 ECID:BEEF"))

    def test_ecid_must_be_one_complete_nonzero_hex_token(self):
        for serial in ("CPID:8015", "XECID:BEEF", "ECID:", "ECID:0", "ECID:-1", "ECID:0xBEEF",
                       "ECID:BEEFG", "ECID:10000000000000000", "ECID:BEEF ECID:BEEF", "ECID:BEEF ECID:BAD!"):
            native = Native(serial=serial)
            with self.assertRaisesRegex(ValueError, "ECID"):
                self.identify(native)
            self.assertEqual(native.calls[-3:], ["libusb_close", "libusb_free_device_list", "libusb_exit"])
        self.assertEqual(identity.serial_ecid("CPID:8015 ECID:FFFFFFFFFFFFFFFF IBFL:3C"), "ffffffffffffffff")

if __name__ == "__main__":
    unittest.main()
