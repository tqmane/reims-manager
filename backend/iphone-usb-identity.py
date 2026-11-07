"""Read an exact iPhone's ECID without changing its USB configuration or driver."""
import ctypes as C
import json
from pathlib import Path
import plistlib
import re
import socket
import struct
import sys

LIMIT = 1024 * 1024


class Descriptor(C.Structure):
    _fields_ = [(name, kind) for name, kind in (
        ("length", C.c_uint8), ("kind", C.c_uint8), ("usb", C.c_uint16),
        ("cls", C.c_uint8), ("subcls", C.c_uint8), ("protocol", C.c_uint8),
        ("packet", C.c_uint8), ("vendor", C.c_uint16), ("product", C.c_uint16),
        ("device", C.c_uint16), ("manufacturer", C.c_uint8), ("name", C.c_uint8),
        ("serial", C.c_uint8), ("configs", C.c_uint8))]


def serial_ecid(serial):
    tokens = re.findall(r"(?<!\S)ECID:([^\s]*)", serial)
    if len(tokens) != 1 or not re.fullmatch(r"[0-9a-fA-F]{1,16}", tokens[0]) or int(tokens[0], 16) == 0:
        raise ValueError("Missing, invalid or ambiguous ECID in the USB serial descriptor")
    return f"{int(tokens[0], 16):016x}"


def identify_mode(instance_id, product, native_library):
    if not isinstance(native_library, str) or not Path(native_library).is_absolute():
        raise ValueError("Recovery/DFU identity requires an absolute verified native_library path")
    try:
        library = C.CDLL(native_library, winmode=0x900)

        def bind(name, result, *args):
            function = getattr(library, name)
            function.restype, function.argtypes = result, list(args)
            return function

        ptr = C.c_void_p
        init = bind("libusb_init", C.c_int, C.POINTER(ptr))
        exit_ = bind("libusb_exit", None, ptr)
        list_ = bind("libusb_get_device_list", C.c_ssize_t, ptr, C.POINTER(C.POINTER(ptr)))
        free_list = bind("libusb_free_device_list", None, C.POINTER(ptr), C.c_int)
        descriptor = bind("libusb_get_device_descriptor", C.c_int, ptr, C.POINTER(Descriptor))
        instance = bind("reims_libusb_get_windows_instance_id", C.c_int, ptr, ptr, C.c_int)
        open_ = bind("libusb_open", C.c_int, ptr, C.POINTER(ptr))
        close = bind("libusb_close", None, ptr)
        control = bind("libusb_control_transfer", C.c_int, ptr, C.c_uint8, C.c_uint8,
                       C.c_uint16, C.c_uint16, ptr, C.c_uint16, C.c_uint)

        def check(result, label):
            if result < 0:
                raise ValueError(f"{label}: libusb error {result}")
            return result

        if C.sizeof(Descriptor) != 18:
            raise ValueError("Unsupported USB descriptor layout")
        ctx, devices, handle = ptr(), C.POINTER(ptr)(), ptr()
        check(init(C.byref(ctx)), "initialize")
        try:
            count = check(list_(ctx, C.byref(devices)), "enumerate")
            if count > 1024 or (count and not devices):
                raise ValueError("Invalid USB device list")
            matches = []
            for index in range(count):
                if not devices[index]:
                    raise ValueError("Invalid USB device entry")
                name = C.create_string_buffer(2048)
                if instance(devices[index], name, len(name)) != 0:
                    continue
                if b"\0" not in name.raw:
                    raise ValueError("Invalid native USB instance identifier")
                if name.value.decode("utf-8").upper() != instance_id.upper():
                    continue
                desc = Descriptor()
                check(descriptor(devices[index], C.byref(desc)), "descriptor")
                if (desc.length, desc.kind, desc.vendor, desc.product) != (18, 1, 0x05AC, product) or not desc.serial:
                    raise ValueError("Selected recovery/DFU device descriptor does not match")
                matches.append((devices[index], desc.serial))
            if len(matches) != 1:
                raise ValueError("Selected recovery/DFU instance is absent or ambiguous")
            device, serial_index = matches[0]
            check(open_(device, C.byref(handle)), "open exact selected device")
            if not handle:
                raise ValueError("Native USB open returned no handle")

            def string_descriptor(index, language):
                data = (C.c_uint8 * 254)()
                size = check(control(handle, 0x80, 6, 0x0300 | index, language, data, len(data), 2000),
                             "read standard USB string descriptor")
                if not 2 <= size <= len(data) or size % 2 or data[0] != size or data[1] != 3:
                    raise ValueError("Invalid or incomplete USB string descriptor")
                return bytes(data[2:size])

            languages = string_descriptor(0, 0)
            if not languages or not int.from_bytes(languages[:2], "little"):
                raise ValueError("USB serial language is unavailable")
            serial = string_descriptor(serial_index, int.from_bytes(languages[:2], "little")).decode("utf-16-le")
            if not serial or any(ord(character) < 32 or ord(character) > 126 for character in serial):
                raise ValueError("Invalid USB serial descriptor text")
            return {"schema": 1, "instance_id": instance_id, "ecid": serial_ecid(serial),
                    "mode": "recovery" if 0x1280 <= product <= 0x1283 else "dfu"}
        finally:
            if handle:
                close(handle)
            if devices:
                free_list(devices, 1)
            exit_(ctx)
    except (OSError, AttributeError, ValueError, UnicodeError) as error:
        raise ValueError(f"Recovery/DFU identity unsupported with the original driver: {error}") from error


def read_exact(stream, size):
    chunks = bytearray()
    while len(chunks) < size:
        chunk = stream.recv(size - len(chunks))
        if not chunk:
            raise ValueError("Apple service closed an incomplete reply")
        chunks.extend(chunk)
    return bytes(chunks)


def mux(stream, request, tag):
    payload = plistlib.dumps(request)
    stream.sendall(struct.pack("<IIII", 16 + len(payload), 1, 8, tag) + payload)
    size, version, kind, reply_tag = struct.unpack("<IIII", read_exact(stream, 16))
    if not 16 <= size <= LIMIT or (version, kind, reply_tag) != (1, 8, tag):
        raise ValueError("Invalid Apple USB service reply")
    result = plistlib.loads(read_exact(stream, size - 16))
    if not isinstance(result, dict):
        raise ValueError("Apple USB service reply is not a dictionary")
    return result


def lockdown(stream, key):
    payload = plistlib.dumps({"Label": "ReimsManager", "Request": "GetValue", "Key": key})
    stream.sendall(struct.pack(">I", len(payload)) + payload)
    size, = struct.unpack(">I", read_exact(stream, 4))
    if not 1 <= size <= LIMIT:
        raise ValueError("Invalid iPhone identity reply size")
    result = plistlib.loads(read_exact(stream, size))
    if not isinstance(result, dict) or result.get("Request") != "GetValue" or "Error" in result:
        raise ValueError("iPhone identity is unavailable; unlock and trust this Windows computer")
    return result.get("Value")


def normalized_udid(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9A-Fa-f-]{16,64}", value):
        raise ValueError("Invalid iPhone USB identity")
    return value.replace("-", "").upper()


def identify(instance_id, connect=socket.create_connection, native_library=None):
    mode = re.fullmatch(r"USB\\VID_05AC&PID_(128[0-3]|122[0-7])(?:&REV_[0-9A-F]{4})?\\[^\\/\x00-\x20*?]{1,200}", instance_id, re.I)
    if mode and instance_id.isascii():
        return identify_mode(instance_id, int(mode[1], 16), native_library)
    match = re.fullmatch(r"USB\\VID_05AC&PID_(?P<product>12A8|12AB)(?:&REV_[0-9A-F]{4})?\\(?P<serial>[0-9A-F-]{16,64})", instance_id, re.I)
    if not match:
        raise ValueError("Select exactly one normal iPhone or iPad parent instance")
    expected = normalized_udid(match['serial'])
    product = int(match['product'], 16)
    endpoint = ("127.0.0.1", 27015)
    with connect(endpoint, timeout=5) as stream:
        result = mux(stream, {"MessageType": "ListDevices", "ProgName": "ReimsManager", "kLibUSBMuxVersion": 3}, 1)
    devices = result.get("DeviceList")
    if not isinstance(devices, list) or len(devices) > 128:
        raise ValueError("Invalid Apple USB device list")
    matches = []
    for row in devices:
        if not isinstance(row, dict) or not isinstance(row.get("Properties"), dict):
            raise ValueError("Invalid Apple USB device entry")
        properties = row["Properties"]
        if properties.get("ConnectionType") != "USB" or properties.get("ProductID") != product:
            continue
        if normalized_udid(properties.get("SerialNumber")) == expected:
            matches.append(row)
    if len(matches) != 1:
        raise ValueError("Selected iPhone is absent or ambiguous in the Apple service")
    device_id = matches[0].get("DeviceID")
    if type(device_id) is not int or not 0 < device_id <= 0xFFFFFFFF:
        raise ValueError("Invalid Apple USB device number")
    with connect(endpoint, timeout=5) as stream:
        reply = mux(stream, {"MessageType": "Connect", "DeviceID": device_id,
                             "PortNumber": socket.htons(62078), "ProgName": "ReimsManager"}, 2)
        if reply.get("MessageType") != "Result" or type(reply.get("Number")) is not int or reply["Number"] != 0:
            raise ValueError("Could not open the selected iPhone identity service")
        actual = lockdown(stream, "UniqueDeviceID")
        if normalized_udid(actual) != expected:
            raise ValueError("iPhone changed before identity confirmation")
        ecid = lockdown(stream, "UniqueChipID")
    if type(ecid) is not int or not 0 < ecid < 2**64:
        raise ValueError("Invalid iPhone ECID")
    return {"schema": 1, "instance_id": instance_id, "udid": actual, "ecid": f"{ecid:016x}"}


if __name__ == "__main__":
    request = sys.stdin.buffer.read(4097)
    if len(request) > 4096:
        raise SystemExit("Identity request exceeds its size limit")
    value = json.loads(request)
    print(json.dumps(identify(value["instance_id"], native_library=value.get("native_library"))))
