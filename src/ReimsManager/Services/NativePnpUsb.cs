using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ReimsManager.Services;

internal static partial class NativePnpUsb
{
    internal sealed record PnpUsbDevice(string InstanceId, string Class, string FriendlyName, string BusReportedDesc);

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex();

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&|\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbIdPrefixRegex();

    [GeneratedRegex(@"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&REV_[0-9A-F]{4})?&(?!REV_[0-9A-F]{4}(?:\\|$))[^\\]+\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceSegmentRegex();

    [GeneratedRegex(@"\s+_SN:\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SerialSuffixRegex();

    [GeneratedRegex(@"^(?:Android(?: Device| Phone)?|MTP(?: USB (?:Device|デバイス))?|(?:.*\s)?USB (?:Composite Device|複合デバイス)|USB (?:Device|デバイス)|(?:.*\s)?ADB (?:Interface|インターフェ[イー]ス))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericNameRegex();

    [GeneratedRegex(@"^USB\\VID_05AC&PID_(12A8|12AB|128[0-3]|122[0-7])(?:&REV_[0-9A-F]{4})?\\[^\\/\x00-\x20*?]{1,200}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppleCandidateRegex();

    private static readonly DevPropKey DevpkeyFriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DevPropKey DevpkeyDeviceDesc = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly DevPropKey DevpkeyClass = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 9);
    private static readonly DevPropKey DevpkeyBusDesc = new(new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);

    private static readonly string[] ProtectedClasses = [
        "{4d36e96b-e325-11ce-bfc1-08002be10318}", // Keyboard
        "{4d36e96f-e325-11ce-bfc1-08002be10318}", // Mouse
        "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}"  // Bluetooth
    ];

    private static readonly Guid GuidDevInterfaceDisk = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    private const uint CmGetIdListFilterPresent = 0x00000100;
    private const uint CmGetIdListFilterClass = 0x00000200;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    internal static bool IsAppleCandidateInstance(string instanceId) => AppleCandidateRegex().IsMatch(instanceId);

    internal static IReadOnlyList<PnpUsbDevice> GetPresentUsbDevices()
    {
        var results = new List<PnpUsbDevice>();
        foreach (string id in ListPresentDeviceIds(null)) {
            if (!id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNodeW(out uint devInst, id, 0) != 0) continue;
            string cls = GetDevNodeStringProperty(devInst, DevpkeyClass) ?? "";
            string friendly = GetDevNodeStringProperty(devInst, DevpkeyFriendlyName)
                              ?? GetDevNodeStringProperty(devInst, DevpkeyDeviceDesc) ?? "";
            string busDesc = GetDevNodeStringProperty(devInst, DevpkeyBusDesc) ?? "";
            results.Add(new PnpUsbDevice(id, cls, friendly, busDesc));
        }
        return results;
    }

    internal static IReadOnlyList<string> GetPresentAppleCandidates()
    {
        var list = new List<string>();
        foreach (string id in ListPresentDeviceIds(null)) {
            if (AppleCandidateRegex().IsMatch(id)) {
                list.Add(id);
                if (list.Count > 64) throw new InvalidOperationException("iPhone USB の候補数が上限を超えました。");
            }
        }
        return list;
    }

    internal static HashSet<string> GetProtectedUsbIds(string projectRoot, string vmRoot)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string classGuid in ProtectedClasses) {
            foreach (string id in ListPresentDeviceIds(classGuid)) {
                if (CM_Locate_DevNodeW(out uint dev, id, 0) == 0)
                    WalkAncestors(dev, blocked);
            }
        }

        // Identify storage devices backing Engine, VM and Windows directories.
        var diskNumbers = new HashSet<uint>();
        var usbStorageDiskNumbers = new HashSet<uint>();
        var paths = new[] { projectRoot, vmRoot, Environment.SystemDirectory };
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths) {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
        }

        foreach (string root in roots) {
            if (root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':') {
                string volumePath = @"\\.\" + root[..2];
                using var handle = OpenDeviceReadOnly(volumePath);
                if (handle.IsInvalid) continue;
                if (TryGetDiskNumber(handle, out uint diskNumber)) {
                    diskNumbers.Add(diskNumber);
                    if (TryGetStorageBusType(handle, out uint busType) && busType == 7 /* BusTypeUsb */)
                        usbStorageDiskNumbers.Add(diskNumber);
                }
            }
        }

        if (diskNumbers.Count > 0) {
            var diskDevNodes = MapDiskNumbersToDevNodes();
            foreach (uint num in diskNumbers) {
                if (!diskDevNodes.TryGetValue(num, out uint devInst))
                    throw new InvalidOperationException("Cannot identify the host storage device; USB forwarding was not changed.");
                bool foundUsbVidPid = WalkAncestors(devInst, blocked);
                if (usbStorageDiskNumbers.Contains(num) && !foundUsbVidPid)
                    throw new InvalidOperationException(UiText.T("USB 保存先の識別情報を取得できません。転送は開始しません。"));
            }
        }

        return blocked;
    }

    internal static Dictionary<string, string> BuildProductNames(IReadOnlyList<PnpUsbDevice> pnp)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, List<PnpUsbDevice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in pnp) {
            var m = UsbIdPrefixRegex().Match(device.InstanceId);
            if (!m.Success) continue;
            string key = $"{m.Groups[1].Value}:{m.Groups[2].Value}".ToLowerInvariant();
            if (!groups.TryGetValue(key, out var list)) { list = []; groups[key] = list; }
            list.Add(device);
        }

        foreach (var (key, group) in groups) {
            var roots = group.Where(x => !InterfaceSegmentRegex().IsMatch(x.InstanceId)).ToArray();
            if (roots.Length > 1) {
                var rootNames = new List<string>(roots.Length);
                foreach (var root in roots) {
                    string? candidate = CleanProductName(root.BusReportedDesc) ?? CleanProductName(root.FriendlyName);
                    if (candidate is not null) rootNames.Add(candidate);
                }
                var unique = rootNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (rootNames.Count == roots.Length && unique.Length == 1) names[key] = unique[0];
                continue;
            }
            var interfaces = group.Where(x => InterfaceSegmentRegex().IsMatch(x.InstanceId)).ToArray();
            var wpd = interfaces.Where(x => string.Equals(x.Class, "WPD", StringComparison.OrdinalIgnoreCase)).ToArray();
            var tiers = new (PnpUsbDevice[] Devices, bool UseBusDesc)[] {
                (roots, true), (roots, false),
                (wpd, false), (wpd, true),
                (interfaces, true), (interfaces, false)
            };
            foreach (var (devices, useBusDesc) in tiers) {
                var candidates = devices
                    .Select(d => CleanProductName(useBusDesc ? d.BusReportedDesc : d.FriendlyName))
                    .Where(n => n is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (candidates.Length == 1) { names[key] = candidates[0]; break; }
                if (candidates.Length > 1) break;
            }
        }
        return names;
    }

    private static string? CleanProductName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string name = SerialSuffixRegex().Replace(raw, "").Trim();
        if (name.Length == 0 || GenericNameRegex().IsMatch(name)) return null;
        return name;
    }

    private static bool WalkAncestors(uint devInst, HashSet<string> blocked)
    {
        bool foundAny = false;
        uint cur = devInst;
        var buf = new char[512];
        for (int depth = 0; depth < 32; depth++) {
            if (CM_Get_Device_IDW(cur, buf, (uint)buf.Length, 0) != 0) break;
            int nul = Array.IndexOf(buf, '\0');
            string cid = nul >= 0 ? new string(buf, 0, nul) : new string(buf);
            var m = VidPidRegex().Match(cid);
            if (m.Success) {
                blocked.Add($"{m.Groups[1].Value}:{m.Groups[2].Value}");
                foundAny = true;
            }
            if (CM_Get_Parent(out uint parent, cur, 0) != 0 || parent == 0) break;
            cur = parent;
        }
        return foundAny;
    }

    private static string[] ListPresentDeviceIds(string? classGuid)
    {
        uint flags = CmGetIdListFilterPresent | (classGuid is not null ? CmGetIdListFilterClass : 0);
        if (CM_Get_Device_ID_List_SizeW(out uint len, classGuid, flags) != 0 || len == 0) return [];
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(classGuid, buf, len, flags) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? GetDevNodeStringProperty(uint devInst, DevPropKey key)
    {
        uint size = 512;
        var buf = new byte[size];
        uint cr = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buf, ref size, 0);
        if (cr == 0x1A /* CR_BUFFER_SMALL */ && size is > 0 and <= 65536) {
            buf = new byte[size];
            cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0);
        }
        if (cr != 0 || type != 18 /* DEVPROP_TYPE_STRING */ || size < 2) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }

    private static Dictionary<uint, uint> MapDiskNumbersToDevNodes()
    {
        var map = new Dictionary<uint, uint>();
        Guid diskGuid = GuidDevInterfaceDisk;
        nint set = SetupDiGetClassDevsW(ref diskGuid, null, 0, 0x02 /* DIGCF_PRESENT */ | 0x10 /* DIGCF_DEVICEINTERFACE */);
        if (set == new nint(-1)) return map;
        try {
            var iface = new SpDeviceInterfaceData { cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, 0, ref diskGuid, index, ref iface); index++) {
                SetupDiGetDeviceInterfaceDetailW(set, ref iface, 0, 0, out uint required, 0);
                if (required < 8 || required > 65536) continue;
                nint detailBuf = Marshal.AllocHGlobal((int)required);
                try {
                    Marshal.WriteInt32(detailBuf, 8); // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) on x64
                    var devInfo = new SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detailBuf, required, out _, ref devInfo)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4)) ?? "";
                    if (path.Length == 0) continue;
                    using var handle = OpenDeviceReadOnly(path);
                    if (!handle.IsInvalid && TryGetDiskNumber(handle, out uint diskNum))
                        map[diskNum] = devInfo.DevInst;
                } finally {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        } finally {
            SetupDiDestroyDeviceInfoList(set);
        }
        return map;
    }

    private static SafeFileHandle OpenDeviceReadOnly(string path) =>
        CreateFileW(path, 0, 0x03 /* FILE_SHARE_READ | FILE_SHARE_WRITE */, 0, 3 /* OPEN_EXISTING */, 0, 0);

    private static bool TryGetDiskNumber(SafeFileHandle handle, out uint diskNumber)
    {
        var sdn = new StorageDeviceNumber();
        int size = Marshal.SizeOf<StorageDeviceNumber>();
        nint buf = Marshal.AllocHGlobal(size);
        try {
            if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, 0, 0, buf, (uint)size, out uint ret, 0) || ret < size) {
                diskNumber = 0;
                return false;
            }
            sdn = Marshal.PtrToStructure<StorageDeviceNumber>(buf);
            diskNumber = sdn.DeviceNumber;
            return sdn.DeviceType == 7 /* FILE_DEVICE_DISK */;
        } finally {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static bool TryGetStorageBusType(SafeFileHandle handle, out uint busType)
    {
        var query = new StoragePropertyQuery { PropertyId = 0, QueryType = 0 };
        int qSize = Marshal.SizeOf<StoragePropertyQuery>();
        const int outSize = 256;
        nint qBuf = Marshal.AllocHGlobal(qSize);
        nint oBuf = Marshal.AllocHGlobal(outSize);
        try {
            Marshal.StructureToPtr(query, qBuf, false);
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, qBuf, (uint)qSize, oBuf, outSize, out uint ret, 0) || ret < 32) {
                busType = 0;
                return false;
            }
            // Offset 28 in STORAGE_DEVICE_DESCRIPTOR is BusType (4 bytes).
            busType = (uint)Marshal.ReadInt32(oBuf, 28);
            return true;
        } finally {
            Marshal.FreeHGlobal(qBuf);
            Marshal.FreeHGlobal(oBuf);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid fmtid, uint pid) { public Guid fmtid = fmtid; public uint pid = pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public nuint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData { public uint cbSize; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber { public uint DeviceType; public uint DeviceNumber; public uint PartitionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery { public uint PropertyId; public uint QueryType; public uint AdditionalParameters; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint bufferLen, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, uint bufferLen, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData interfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref SpDeviceInterfaceData interfaceData, nint detailData, uint detailDataSize, out uint requiredSize, ref SpDevInfoData devInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref SpDeviceInterfaceData interfaceData, nint detailData, uint detailDataSize, out uint requiredSize, nint unused);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint sec, uint disp, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint inBuf, uint inSize, nint outBuf, uint outSize, out uint bytesReturned, nint overlapped);
}
