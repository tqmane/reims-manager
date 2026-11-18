using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ReimsManager.Services;

internal static partial class NativeHostNetwork
{
    [GeneratedRegex(@"TAP-Windows|TAP-Win32|TAP.*Adapter", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TapDescriptionRegex();

    internal sealed record AdapterRow(Guid Guid, uint InterfaceIndex, string Name, string Description, string Status, uint Type, bool HardwareInterface)
    {
        internal string Kind => Type switch { 71 => "wifi", 6 => "ethernet", _ => "other" };
        internal bool Connected => string.Equals(Status, "Up", StringComparison.Ordinal);
        internal bool IsTap => TapDescriptionRegex().IsMatch(Description);
    }

    internal static IReadOnlyList<AdapterRow> GetAdapters()
    {
        uint err = GetIfTable2(out nint table);
        if (err != 0) throw new InvalidOperationException($"GetIfTable2 failed ({err}).");
        try {
            uint count = (uint)Marshal.ReadInt32(table);
            int rowSize = Marshal.SizeOf<MibIfRow2>();
            nint current = IntPtr.Add(table, 8);
            var rows = new List<AdapterRow>((int)Math.Min(count, 256));
            for (int i = 0; i < count; i++) {
                var row = Marshal.PtrToStructure<MibIfRow2>(IntPtr.Add(current, i * rowSize));
                bool isFilter = (row.InterfaceAndOperStatusFlags & 0x02) != 0;
                if (isFilter || row.OperStatus == 6 /* IfOperStatusNotPresent */) continue;
                bool isHw = (row.InterfaceAndOperStatusFlags & 0x01) != 0;
                string status = row.OperStatus == 1 ? "Up" : "Disconnected";
                rows.Add(new AdapterRow(row.InterfaceGuid, row.InterfaceIndex, row.Alias ?? "", row.Description ?? "", status, row.Type, isHw));
            }
            return rows;
        } finally {
            FreeMibTable(table);
        }
    }

    internal static NetworkInventory GetNetworkInventory()
    {
        var adapters = GetAdapters();
        var uplinks = adapters
            .Where(x => x.HardwareInterface && !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new NetworkUplink(x.Guid.ToString("D"), x.Name, x.Description, x.Kind, x.Status, x.Connected))
            .ToArray();
        var taps = adapters
            .Where(x => x.IsTap && !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x.Name)
            .ToArray();
        return new NetworkInventory(uplinks, taps);
    }

    internal static string GetNetworkInventoryJson()
    {
        var inv = GetNetworkInventory();
        return System.Text.Json.JsonSerializer.Serialize(new {
            schema = 1,
            uplinks = inv.Uplinks.Select(u => new {
                id = u.Id,
                name = u.Name,
                description = u.Description,
                kind = u.Kind,
                status = u.Status,
                connected = u.Connected
            }),
            tap_adapters = inv.Taps.Select(name => new { name })
        });
    }

    internal static string GetIpv6RouterDiscovery(Guid interfaceGuid)
    {
        var adapter = GetAdapters().FirstOrDefault(x => x.Guid == interfaceGuid)
            ?? throw new InvalidOperationException("Dedicated TAP interface was not found.");
        var row = new MibIpInterfaceRow();
        InitializeIpInterfaceEntry(ref row);
        row.Family = 23; // AF_INET6
        row.InterfaceIndex = adapter.InterfaceIndex;
        uint err = GetIpInterfaceEntry(ref row);
        if (err != 0) throw new InvalidOperationException($"Cannot read TAP IPv6 interface state ({err}).");
        return row.RouterDiscoveryBehavior switch {
            0 => "Disabled",
            1 => "Enabled",
            2 => "ControlledByDHCP",
            _ => throw new InvalidOperationException($"Unknown TAP IPv6 RouterDiscovery value: {row.RouterDiscoveryBehavior}")
        };
    }

    internal static string GetKernelDriverState(string serviceName)
    {
        nint scm = OpenSCManagerW(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm == 0) return "unknown";
        try {
            nint svc = OpenServiceW(scm, serviceName, 0x0004 /* SERVICE_QUERY_STATUS */);
            if (svc == 0) {
                int err = Marshal.GetLastPInvokeError();
                return err == 1060 /* ERROR_SERVICE_DOES_NOT_EXIST */ ? "not_installed" : "unknown";
            }
            try {
                if (!QueryServiceStatus(svc, out var status)) return "unknown";
                return status.CurrentState == 4 /* SERVICE_RUNNING */ ? "running" : "unknown";
            } finally {
                CloseServiceHandle(svc);
            }
        } finally {
            CloseServiceHandle(scm);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos;
        public ulong InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors;
        public ulong OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpInterfaceRow
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval, MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled, ForwardingEnabled, WeakHostSend, WeakHostReceive;
        public byte UseAutomaticMetric, UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported, OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public uint RouterDiscoveryBehavior, DadTransmits, BaseReachableTime, RetransmitTime, PathMtuDiscoveryTimeout;
        public uint LinkLocalAddressBehavior, LinkLocalAddressTimeout;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public uint[] ZoneIndices;
        public uint SitePrefixLength, Metric, NlMtu;
        public byte Connected, SupportsWakeUpPatterns, SupportsNeighborDiscovery, SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload, ReceiveOffload, DisableDefaultRoutes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable2(out nint table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(nint memory);
    [DllImport("iphlpapi.dll")] private static extern void InitializeIpInterfaceEntry(ref MibIpInterfaceRow row);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpInterfaceEntry(ref MibIpInterfaceRow row);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenServiceW(nint scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
}
