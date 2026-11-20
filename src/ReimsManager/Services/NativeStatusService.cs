using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ReimsManager.Services;

internal static partial class NativeStatusService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotNameRegex();

    [GeneratedRegex(@"^Reims TAP [A-Za-z0-9 -]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReimsTapNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRepoRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PinIdOrAssetRegex();

    [GeneratedRegex(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerSha256Regex();

    [GeneratedRegex(@"[. ]$|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidSegmentRegex();

    [LibraryImport("WinHvPlatform.dll")]
    private static partial int WHvGetCapability(uint code, out ulong value, uint size, out uint written);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    internal static async Task<string> GetStatusJsonAsync(string engineRoot, string vmDirectory, CancellationToken cancellation = default)
    {
        string configPath = Path.Combine(vmDirectory, "state", "config.json");
        bool prepared = File.Exists(configPath);
        JsonObject? config = prepared ? AtomicFileStore.ReadJsonNode(configPath).AsObject() : null;
        if (config is not null && config["schema"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Unknown VM configuration schema.");

        string runtimePath = Path.Combine(vmDirectory, "state", "runtime.json");
        JsonObject? runtime = File.Exists(runtimePath) ? AtomicFileStore.ReadJsonNode(runtimePath).AsObject() : null;
        bool runtimeAlive = false;
        if (runtime is not null) {
            int pid = runtime["pid"]?.GetValue<int>() ?? 0;
            long ticks = runtime["start_ticks"]?.GetValue<long>() ?? 0;
            string exe = runtime["executable"]?.GetValue<string>() ?? "";
            runtimeAlive = ProcessIdentity.IsAlive(pid, ticks, exe);
        }

        string state = prepared ? "stopped" : "unprepared";
        if (runtimeAlive && runtime is not null) {
            int qmpPort = runtime["qmp_port"]?.GetValue<int>() ?? 0;
            state = await QmpClient.QueryStatusAsync(qmpPort, cancellation).ConfigureAwait(false);
        }

        var snapshots = new JsonArray();
        string snapshotsDir = Path.Combine(vmDirectory, "snapshots");
        if (Directory.Exists(snapshotsDir)) {
            foreach (string dir in Directory.EnumerateDirectories(snapshotsDir)) {
                string snapFile = Path.Combine(dir, "snapshot.json");
                if (!File.Exists(snapFile)) continue;
                var snap = AtomicFileStore.ReadJsonNode(snapFile).AsObject();
                if (snap["name"]?.GetValue<string>() is { Length: > 0 } name) snapshots.Add(name);
            }
        }

        var adaptersList = NativeHostNetwork.GetAdapters();
        var adaptersJson = new JsonArray();
        var tapsJson = new JsonArray();
        foreach (var adapter in adaptersList) {
            adaptersJson.Add(new JsonObject {
                ["Name"] = adapter.Name,
                ["InterfaceDescription"] = adapter.Description,
                ["Status"] = adapter.Status,
                ["HardwareInterface"] = adapter.HardwareInterface
            });
            if (adapter.IsTap && !string.IsNullOrWhiteSpace(adapter.Name))
                tapsJson.Add(adapter.Name);
        }

        bool connectInstaller = config?["connect_installer"]?.GetValue<bool>() ?? false;
        bool installerAttached = connectInstaller;
        if (runtimeAlive && runtime is not null && runtime["run_directory"]?.GetValue<string>() is { Length: > 0 } runDir) {
            string cmdPath = Path.Combine(runDir, "command.json");
            if (File.Exists(cmdPath)) {
                var cmd = AtomicFileStore.ReadJsonNode(cmdPath).AsObject();
                if (cmd["arguments"] is JsonArray args) {
                    installerAttached = args.Any(a => a?.GetValue<string>() is string s && s.StartsWith("id=installer,", StringComparison.Ordinal));
                }
            }
        }

        bool hostCursorHidden = ReadHostCursorHidden(vmDirectory);
        bool usbdkInstalled = IsUsbDkInstalled();
        var networkNode = config?["network"]?.AsObject();
        string networkMode = networkNode?["mode"]?.GetValue<string>() ?? "nat";
        string networkAdapter = networkNode?["adapter"]?.GetValue<string>() ?? "";
        string guestHost = networkNode?["guest_host"]?.GetValue<string>() ?? "";
        string uplinkId = networkNode?["physical_adapter_id"]?.GetValue<string>() ?? "";

        var result = new JsonObject {
            ["state"] = state,
            ["prepared"] = prepared,
            ["snapshots"] = snapshots,
            ["taps"] = tapsJson,
            ["adapters"] = adaptersJson,
            ["runtime"] = runtimeAlive && runtime is not null ? runtime.DeepClone() : null,
            ["vm_directory"] = vmDirectory,
            ["host_cursor_hidden"] = hostCursorHidden,
            ["microphone"] = config?["microphone"]?.GetValue<bool>() ?? false,
            ["usb_backend"] = config?["usb_backend"]?.GetValue<string>() ?? "winusb",
            ["usb_backend_active"] = runtimeAlive ? runtime?["usb_backend"]?.DeepClone() : null,
            ["usb_usbdk_installed"] = usbdkInstalled,
            ["cpus"] = config?["cpus"]?.GetValue<int>() ?? 8,
            ["memory_mib"] = config?["memory_mib"]?.GetValue<int>() ?? 8192,
            ["disk_gib"] = config?["disk_gib"]?.GetValue<int>() ?? 128,
            ["disk"] = config?["active_disk"]?.GetValue<string>() ?? "",
            ["audio"] = config?["audio"]?.GetValue<string>() ?? "none",
            ["connect_installer"] = connectInstaller,
            ["with_installer"] = installerAttached,
            ["network_adapter"] = networkAdapter,
            ["guest_host"] = guestHost,
            ["network_uplink_id"] = uplinkId,
            ["resolution"] = config?["resolution"]?.GetValue<string>() ?? "1920x1080",
            ["network"] = networkMode,
            ["network_forwarder"] = GetWifiStatusNode(vmDirectory, config, runtimeAlive ? runtime : null, adaptersList),
            ["iso"] = config?["iso"]?.GetValue<string>() ?? ""
        };
        return result.ToJsonString();
    }

    internal static bool IsUsbDkInstalled()
    {
        using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UsbDk");
        return svc is not null && File.Exists(Path.Combine(Environment.SystemDirectory, "UsbDkHelper.dll"));
    }

    internal static bool ReadHostCursorHidden(string vmDirectory)
    {
        string path = Path.Combine(vmDirectory, "state", "host-cursor.json");
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Host cursor policy must be an ordinary VM-owned file.");
        if (info.Length > 65536)
            throw new InvalidDataException("Host cursor policy is too large.");
        var node = AtomicFileStore.ReadJsonNode(path, 65536).AsObject();
        if (node.Count != 2 || node["schema"]?.GetValue<int>() != 1 ||
            node["host_cursor_hidden"] is not JsonValue v || !v.TryGetValue<bool>(out bool hidden))
            throw new InvalidDataException("Invalid typed host cursor policy in state/host-cursor.json.");
        return hidden;
    }

    internal static JsonObject GetWifiStatusNode(string vmDirectory, JsonObject? config, JsonObject? aliveRuntime, IReadOnlyList<NativeHostNetwork.AdapterRow>? adapters = null)
    {
        string toolsRoot = Path.Combine(AppContext.BaseDirectory, "runtimes");
        string helperPath = Path.Combine(toolsRoot, "wifi-macnat", "reims-wifi-macnat.exe");
        bool helperAvailable = File.Exists(helperPath);
        string driverState = NativeHostNetwork.GetKernelDriverState("ndisrd");
        bool installed = helperAvailable && driverState == "running";

        var result = new JsonObject {
            ["schema"] = 1,
            ["kind"] = "none",
            ["installed"] = installed,
            ["configured"] = false,
            ["running"] = false,
            ["ready"] = false,
            ["pid"] = null,
            ["enabled"] = false,
            ["helper_available"] = helperAvailable,
            ["driver_state"] = driverState,
            ["forwarder_state"] = "stopped",
            ["error"] = "",
            ["tap_id"] = "",
            ["uplink_id"] = "",
            ["ipv4_only"] = true,
            ["ipv6_enabled"] = false
        };
        if (string.IsNullOrWhiteSpace(vmDirectory)) return result;

        try {
            string recordPath = Path.Combine(vmDirectory, "state", "wifi-forwarder.json");
            if (!File.Exists(recordPath)) return result;
            var record = AtomicFileStore.ReadJsonNode(recordPath, 65536).AsObject();
            if (record["schema"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported Wi-Fi state schema.");

            string recState = record["state"]?.GetValue<string>() ?? "";
            string tapId = record["tap_id"]?.GetValue<string>() ?? "";
            string uplinkId = record["uplink_id"]?.GetValue<string>() ?? "";
            result["enabled"] = recState is "enabled" or "prepared" or "error";
            result["tap_id"] = tapId;
            result["uplink_id"] = uplinkId;
            result["error"] = record["error"]?.GetValue<string>() ?? "";

            var net = config?["network"]?.AsObject();
            string? fwd = net?["forwarder"]?.GetValue<string>();
            if (fwd is "macnat" or "macnat-ipv4") {
                result["kind"] = fwd;
                bool configured = recState == "enabled" &&
                    net?["mode"]?.GetValue<string>() == "bridge" &&
                    net?["tap_id"]?.GetValue<string>() == tapId &&
                    net?["physical_adapter_id"]?.GetValue<string>() == uplinkId;
                result["configured"] = configured;
                if (fwd == "macnat-ipv4") {
                    result["configured"] = false;
                    result["error"] = "Reapply Wi-Fi preparation for IPv6";
                }
                if ((result["configured"]?.GetValue<bool>() ?? false) && fwd == "macnat") {
                    AssertWifiRouterDisabled(record, adapters ?? NativeHostNetwork.GetAdapters());
                    result["ipv6_enabled"] = true;
                    result["ipv4_only"] = false;
                }
            }

            string statusPath = Path.Combine(vmDirectory, "state", "wifi-forwarder", "status.json");
            string fwdRuntimePath = Path.Combine(vmDirectory, "state", "wifi-forwarder", "runtime.json");
            var nativeStatus = File.Exists(statusPath) ? AtomicFileStore.ReadJsonNode(statusPath, 65536).AsObject() : null;
            var fwdRuntime = File.Exists(fwdRuntimePath) ? AtomicFileStore.ReadJsonNode(fwdRuntimePath, 65536).AsObject() : null;

            if (fwdRuntime is not null) {
                int fwdPid = fwdRuntime["pid"]?.GetValue<int>() ?? 0;
                long fwdTicks = fwdRuntime["start_ticks"]?.GetValue<long>() ?? 0;
                string fwdExe = fwdRuntime["executable"]?.GetValue<string>() ?? "";
                string fwdSession = fwdRuntime["session"]?.GetValue<string>() ?? "";
                int fwdQemuPid = fwdRuntime["qemu_pid"]?.GetValue<int>() ?? 0;
                long fwdQemuTicks = fwdRuntime["qemu_start_ticks"]?.GetValue<long>() ?? 0;

                if (ProcessIdentity.IsAlive(fwdPid, fwdTicks, fwdExe)) {
                    bool sameQemu = aliveRuntime is not null &&
                        (aliveRuntime["pid"]?.GetValue<int>() ?? 0) == fwdQemuPid &&
                        (aliveRuntime["start_ticks"]?.GetValue<long>() ?? 0) == fwdQemuTicks;
                    result["running"] = sameQemu;
                    result["pid"] = fwdPid;
                    string nativeState = nativeStatus?["state"]?.GetValue<string>() ?? "";
                    if (sameQemu && nativeStatus is not null &&
                        nativeStatus["session"]?.GetValue<string>() == fwdSession &&
                        (nativeStatus["helper_pid"]?.GetValue<int>() ?? 0) == fwdPid &&
                        (nativeStatus["qemu_pid"]?.GetValue<int>() ?? 0) == fwdQemuPid &&
                        (nativeStatus["qemu_start_ticks"]?.GetValue<long>() ?? 0) == fwdQemuTicks &&
                        nativeState is "starting" or "ready" or "error") {
                        result["forwarder_state"] = nativeState;
                        string nativeErr = nativeStatus["error"]?.GetValue<string>() ?? "";
                        if (nativeErr.Length > 0) result["error"] = nativeErr;
                        bool ipv6 = nativeStatus["ipv6_enabled"]?.GetValue<bool>() ?? false;
                        bool ready = (result["configured"]?.GetValue<bool>() ?? false) && nativeState == "ready" && ipv6;
                        result["ready"] = ready;
                        if (nativeState == "ready" && !ready && result["kind"]?.GetValue<string>() == "macnat") {
                            result["forwarder_state"] = "unconfirmed";
                            result["error"] = "Installed helper has no confirmed IPv6 readiness receipt.";
                        }
                    } else {
                        result["forwarder_state"] = "unconfirmed";
                    }
                } else if (nativeStatus is not null && nativeStatus["session"]?.GetValue<string>() == fwdSession &&
                           nativeStatus["state"]?.GetValue<string>() == "error") {
                    result["forwarder_state"] = "error";
                    result["error"] = nativeStatus["error"]?.GetValue<string>() ?? "";
                } else if (nativeStatus is not null && nativeStatus["session"]?.GetValue<string>() == fwdSession &&
                           nativeStatus["state"]?.GetValue<string>() == "stopped") {
                    result["forwarder_state"] = "stopped";
                } else {
                    result["forwarder_state"] = "unconfirmed";
                    result["error"] = "Forwarder exited without confirmed cleanup.";
                }
            }
        } catch (Exception ex) {
            result["forwarder_state"] = "unconfirmed";
            result["error"] = ex.Message;
            result["configured"] = false;
            result["ipv6_enabled"] = false;
            result["ipv4_only"] = true;
            result["ready"] = false;
        }
        return result;
    }

    private static void AssertWifiRouterDisabled(JsonObject record, IReadOnlyList<NativeHostNetwork.AdapterRow> adapters)
    {
        if (record["ipv6_enabled"]?.GetValue<bool>() != true || record["router_discovery_before"] is not JsonObject)
            throw new InvalidOperationException("Reapply PrepareWifi to protect the dedicated TAP before enabling IPv6.");
        if (record["router_discovery_store"]?.GetValue<string>() != "ActiveStore")
            throw new InvalidOperationException("Older dual-store RouterDiscovery receipt requires inspection; unknown persistent policy was not changed.");
        string ownedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ReimsManager", "Drivers", "tap-windows-9.27.0", "tap-adapter.json");
        if (!File.Exists(ownedPath))
            throw new InvalidOperationException("Select the dedicated TAP created by Manager; other VPN adapters are preserved.");
        var owned = AtomicFileStore.ReadJsonNode(ownedPath, 65536).AsObject();
        string tapId = record["tap_id"]?.GetValue<string>() ?? "";
        string pnpId = record["pnp_instance_id"]?.GetValue<string>() ?? "";
        if (!Guid.TryParse(tapId, out var tapGuid) ||
            !Guid.TryParse(owned["adapter_id"]?.GetValue<string>(), out var ownedGuid) ||
            tapGuid != ownedGuid ||
            owned["pnp_instance_id"]?.GetValue<string>() != pnpId)
            throw new InvalidOperationException("Dedicated TAP identity changed; no bindings were modified.");
        var tapAdapter = adapters.SingleOrDefault(x => x.Guid == tapGuid && x.IsTap);
        if (tapAdapter is null || !ReimsTapNameRegex().IsMatch(tapAdapter.Name))
            throw new InvalidOperationException("Dedicated TAP identity/name changed; no bindings were modified.");
        string live = NativeHostNetwork.GetIpv6RouterDiscovery(tapGuid);
        if (live != "Disabled")
            throw new InvalidOperationException("Dedicated TAP RouterDiscovery changed outside Manager; inspect before reapplying or starting IPv6.");
    }

    internal static async Task<string> GetPrerequisitesJsonAsync(string engineRoot, string toolsRoot, int cpus, int memoryMiB, CancellationToken cancellation)
    {
        var checks = new JsonArray();
        bool restartRequired = false;
        int availableMemMiB = 0;
        int logicalCpus = Environment.ProcessorCount;

        try {
            bool featureEnabled = CheckHypervisorFeatureEnabled();
            bool whpxReady = false;
            try {
                whpxReady = WHvGetCapability(0, out ulong cap, 8, out uint written) >= 0 && written >= 4 && (cap & 0xFFFFFFFF) != 0;
            } catch { whpxReady = false; }

            var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref mem)) throw new InvalidOperationException("Cannot query host memory.");
            availableMemMiB = (int)(mem.AvailPhys / (1024 * 1024));

            bool pendingReboot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null;
            bool firmwareVirt = CheckFirmwareVirtualization(whpxReady);
            restartRequired = pendingReboot || (featureEnabled && !whpxReady && firmwareVirt);

            string whpxMsg = whpxReady ? "WHPX を利用できます。"
                : !firmwareVirt ? "UEFI/BIOS の CPU 仮想化を有効にしてください。"
                : !featureEnabled ? "Windows Hypervisor Platform を管理者権限で有効にし、Windows を再起動してください。"
                : "WHPX がまだ利用できません。Windows を再起動し、再確認してください。";
            var whpxObj = new JsonObject { ["id"] = "whpx", ["label"] = "Windows Hypervisor Platform", ["ready"] = whpxReady, ["message"] = whpxMsg };
            if (!featureEnabled) whpxObj["action"] = "EnableWhpx";
            checks.Add(whpxObj);

            bool hasVulkan = HasVulkanIcd();
            checks.Add(new JsonObject {
                ["id"] = "vulkan", ["label"] = "GPU Vulkan driver", ["ready"] = hasVulkan,
                ["message"] = hasVulkan ? "Vulkan ICD 登録を検出しました。GPU の実行・性能検証はしていません。" : "GPU メーカーの Vulkan 対応ドライバーをインストールしてください。"
            });

            bool hasOpenSsh = File.Exists(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe")) &&
                              File.Exists(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"));
            checks.Add(new JsonObject {
                ["id"] = "openssh", ["label"] = "OpenSSH Client", ["ready"] = hasOpenSsh,
                ["message"] = hasOpenSsh ? "SSH と鍵作成ツールを検出しました。" : "Windows のオプション機能で OpenSSH クライアントを追加してください。管理者権限が必要です。"
            });
        } catch (Exception ex) {
            return new JsonObject {
                ["ready"] = false, ["restart_required"] = false,
                ["checks"] = new JsonArray(new JsonObject { ["id"] = "host", ["label"] = "Windows", ["ready"] = false, ["message"] = $"Windows の前提条件を取得できません: {ex.Message}" }),
                ["plan"] = new JsonObject { ["cpus"] = cpus, ["memory_mib"] = memoryMiB, ["available_memory_mib"] = null, ["start_ready"] = false }
            }.ToJsonString();
        }

        try {
            VerifyEnginePackage(engineRoot);
            checks.Add(new JsonObject {
                ["id"] = "engine", ["label"] = "Reims engine / firmware", ["ready"] = true,
                ["message"] = "固定版 QEMU・DLL・OpenCore・OVMF・GPU ROM を検出しました。"
            });
        } catch (Exception ex) {
            checks.Add(new JsonObject { ["id"] = "engine", ["label"] = "Reims engine / firmware", ["ready"] = false, ["message"] = ex.Message });
        }

        bool hasPython = File.Exists(Path.Combine(toolsRoot, "python", "python.exe"));
        checks.Add(new JsonObject {
            ["id"] = "runtimes", ["label"] = "Manager runtimes", ["ready"] = hasPython,
            ["message"] = hasPython ? "同梱 Python を検出しました。" : "Manager の完全な配布パッケージを再展開してください。同梱 Python がありません。"
        });

        try {
            string pinId = await CheckGuestBridgeReleaseAsync(cancellation).ConfigureAwait(false);
            checks.Add(new JsonObject {
                ["id"] = "guest_addon", ["label"] = "Metal / WebGL guest addon", ["ready"] = true,
                ["message"] = $"資格確認済みの addon {pinId} を取得できます。フレームペーシングには制限があります。"
            });
        } catch (Exception ex) {
            checks.Add(new JsonObject { ["id"] = "guest_addon", ["label"] = "Metal / WebGL guest addon", ["ready"] = false, ["message"] = ex.Message });
        }

        bool usbdkReady = File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "UsbDk.sys"));
        checks.Add(new JsonObject {
            ["id"] = "usb", ["label"] = "USB passthrough (optional)", ["required"] = false, ["ready"] = usbdkReady,
            ["message"] = usbdkReady ? "UsbDk ドライバーを検出しました。選択した機器だけを転送します。" : "USB 転送を使う場合は UsbDk を管理者権限で導入してください。再起動が必要になる場合があります。",
            ["action"] = "InstallUsbDk"
        });

        bool planFits = availableMemMiB >= memoryMiB + 1024 && cpus <= logicalCpus;
        checks.Add(new JsonObject {
            ["id"] = "capacity", ["label"] = "VM memory / CPU plan", ["required"] = false, ["ready"] = planFits,
            ["message"] = planFits
                ? $"空き RAM {availableMemMiB} MiB。計画 {memoryMiB} MiB / {cpus} vCPU を起動できます。"
                : $"空き RAM {availableMemMiB} MiB / ホスト {logicalCpus} 論理 CPU。計画 {memoryMiB} MiB / {cpus} vCPU は保存できますが、自動起動の前に RAM を確保するか割り当てを下げてください。ホスト用に 1024 MiB を残します。"
        });

        bool ready = checks.All(node => {
            var obj = node!.AsObject();
            bool required = !obj.TryGetPropertyValue("required", out var req) || req!.GetValue<bool>();
            return !required || (obj["ready"]?.GetValue<bool>() ?? false);
        });

        return new JsonObject {
            ["ready"] = ready,
            ["restart_required"] = restartRequired,
            ["checks"] = checks,
            ["plan"] = new JsonObject {
                ["cpus"] = cpus,
                ["memory_mib"] = memoryMiB,
                ["available_memory_mib"] = availableMemMiB,
                ["start_ready"] = ready && !restartRequired && planFits
            }
        }.ToJsonString();
    }

    private static bool CheckHypervisorFeatureEnabled()
    {
        // Fast check via DISM / CBS registry or WinHvPlatform.dll availability.
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "WinHvPlatform.dll"))) {
            try {
                if (WHvGetCapability(0, out ulong cap, 8, out uint written) >= 0 && written >= 4 && (cap & 0xFFFFFFFF) != 0)
                    return true;
            } catch { }
        }
        using var hvService = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\hvhost");
        if (hvService is not null && hvService.GetValue("Start") is int start && start <= 3) return true;
        return false;
    }

    private static bool CheckFirmwareVirtualization(bool whpxReady)
    {
        if (whpxReady) return true;
        try { return IsProcessorFeaturePresent(21 /* PF_VIRT_FIRMWARE_ENABLED */); }
        catch { return true; }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessorFeaturePresent(uint processorFeature);

    private static bool HasVulkanIcd()
    {
        var icds = new List<string>();
        try {
            using var drivers = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\Drivers", writable: false);
            if (drivers is not null) {
                foreach (string name in drivers.GetValueNames()) {
                    if (drivers.GetValue(name) is int v && v == 0) icds.Add(name);
                }
            }
        } catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        try {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}", writable: false);
            if (display is not null) {
                foreach (string sub in display.GetSubKeyNames()) {
                    try {
                        using var key = display.OpenSubKey(sub, writable: false);
                        object? val = key?.GetValue("VulkanDriverName");
                        if (val is string s) icds.Add(s);
                        else if (val is string[] arr) icds.AddRange(arr);
                    } catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        } catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        return icds.Any(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    internal static string JoinPackagePath(string directory, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.StartsWith('/') ||
            relative.Any(c => c == ':' || c < 0x20) ||
            relative.Split('/').Any(seg => seg is "" or "." or ".." || InvalidSegmentRegex().IsMatch(seg)))
            throw new InvalidDataException($"Invalid package path: {relative}");
        string root = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package path escapes its directory.");
        return full;
    }

    internal static string ResolveScriptAssetPath(string fileName)
    {
        string inScripts = Path.Combine(AppContext.BaseDirectory, "scripts", fileName);
        if (File.Exists(inScripts)) return inScripts;
        string devBackend = Path.Combine(AppContext.BaseDirectory, "backend", fileName);
        if (File.Exists(devBackend)) return devBackend;
        return inScripts;
    }

    internal static string ResolveEngineToolPath(string engineRoot, string key, out string libusbRelPath)
    {
        string manifestPath = Path.Combine(engineRoot, "reims-engine.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("先に対応エンジンをセットアップしてください。");
        var doc = AtomicFileStore.ReadJsonNode(manifestPath, 4 * 1024 * 1024).AsObject();
        var pin = AtomicFileStore.ReadJsonNode(ResolveScriptAssetPath("engine-lock.json"), 65536).AsObject();
        if (doc["schema"]?.GetValue<int>() != 1 || doc["id"]?.GetValue<string>() != pin["id"]?.GetValue<string>())
            throw new InvalidDataException("Unsupported engine package. Use the engine version paired with this manager.");
        var paths = doc["paths"]?.AsObject() ?? throw new InvalidDataException("Missing engine paths.");
        var files = doc["files"]?.AsObject() ?? throw new InvalidDataException("Missing engine files.");
        string rel = paths[key]?.GetValue<string>() ?? throw new InvalidDataException($"Engine component is missing: {key}");
        string full = JoinPackagePath(engineRoot, rel);
        if (!files.ContainsKey(rel) || !File.Exists(full))
            throw new FileNotFoundException($"Engine component is missing: {key}");
        libusbRelPath = files.Select(kv => kv.Key).SingleOrDefault(k => string.Equals(Path.GetFileName(k), "libusb-1.0.dll", StringComparison.Ordinal)) ?? "";
        return full;
    }

    internal static void VerifyEnginePackage(string engineRoot)
    {
        string manifestPath = Path.Combine(engineRoot, "reims-engine.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("先に対応エンジンをセットアップしてください。");
        var doc = AtomicFileStore.ReadJsonNode(manifestPath, 4 * 1024 * 1024).AsObject();
        var pin = AtomicFileStore.ReadJsonNode(ResolveScriptAssetPath("engine-lock.json"), 65536).AsObject();
        if (doc["schema"]?.GetValue<int>() != 1 || doc["id"]?.GetValue<string>() != pin["id"]?.GetValue<string>())
            throw new InvalidDataException("Unsupported engine package. Use the engine version paired with this manager.");

        if (pin["sources"]?.AsObject() is JsonObject pinSources && doc["sources"]?.AsObject() is JsonObject docSources) {
            foreach (var kv in pinSources) {
                var exp = kv.Value?.AsObject();
                var act = docSources[kv.Key]?.AsObject();
                if (act?["commit"]?.GetValue<string>() != exp?["commit"]?.GetValue<string>() ||
                    act?["repository"]?.GetValue<string>() != exp?["repository"]?.GetValue<string>())
                    throw new InvalidDataException($"Unexpected source revision: {kv.Key}");
            }
        } else {
            throw new InvalidDataException("Unexpected engine source metadata.");
        }

        var paths = doc["paths"]?.AsObject() ?? throw new InvalidDataException("Missing engine paths.");
        var files = doc["files"]?.AsObject() ?? throw new InvalidDataException("Missing engine files.");
        if (!files.ContainsKey("bin/vulkan-1.dll"))
            throw new InvalidDataException("エンジンに Vulkan loader がありません。");

        foreach (string key in new[] { "qemu", "qemu_img", "qemu_io", "seven_zip", "firmware_code", "firmware_vars", "rom", "opencore_archive" }) {
            string rel = paths[key]?.GetValue<string>() ?? throw new InvalidDataException($"Engine component is missing: {key}");
            string full = JoinPackagePath(engineRoot, rel);
            if (!files.ContainsKey(rel) || !File.Exists(full))
                throw new FileNotFoundException($"Engine component is missing: {key}");
        }
        _ = JoinPackagePath(engineRoot, paths["firmware"]?.GetValue<string>() ?? "");

        string? opencoreRel = paths["opencore_archive"]?.GetValue<string>();
        string? expectedOcSha = pin["opencore"]?["sha256"]?.GetValue<string>();
        if (opencoreRel is null || files[opencoreRel]?.GetValue<string>() != expectedOcSha)
            throw new InvalidDataException("OpenCore archive does not match the official pinned release.");

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in files) {
            listed.Add(kv.Key);
            string full = JoinPackagePath(engineRoot, kv.Key);
            string expectedHash = kv.Value?.GetValue<string>() ?? "";
            if (!LowerSha256Regex().IsMatch(expectedHash))
                throw new InvalidDataException($"Invalid SHA256: {kv.Key}");
            if (!File.Exists(full))
                throw new FileNotFoundException($"エンジンの依存ファイルがありません: {kv.Key}");
            using var stream = File.OpenRead(full);
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Engine file missing or changed: {kv.Key}");
        }

        foreach (string file in Directory.EnumerateFiles(engineRoot, "*", SearchOption.AllDirectories)) {
            string rel = Path.GetRelativePath(engineRoot, file).Replace('\\', '/');
            if (!string.Equals(rel, "reims-engine.json", StringComparison.OrdinalIgnoreCase) && !listed.Contains(rel))
                throw new InvalidDataException($"Unlisted package file: {rel}");
        }
    }

    internal static (string Url, string Id, string Asset, long Bytes) ResolveReleaseAssetInfo(JsonObject pin)
    {
        string repo = pin["release_repository"]?.GetValue<string>() ?? "";
        string id = pin["id"]?.GetValue<string>() ?? "";
        string asset = pin["asset"]?.GetValue<string>() ?? "";
        if (!GitHubRepoRegex().IsMatch(repo) || !PinIdOrAssetRegex().IsMatch(id) || !PinIdOrAssetRegex().IsMatch(asset))
            throw new InvalidDataException("Invalid immutable release pin.");
        long bytes = 0;
        if (pin.TryGetPropertyValue("bytes", out var bytesNode) && bytesNode is not null) {
            bytes = bytesNode.GetValue<long>();
            if (bytes <= 0) throw new InvalidDataException("Invalid pinned release size.");
        }
        return ($"https://github.com/{repo}/releases/download/{id}/{asset}", id, asset, bytes);
    }

    internal static async Task<JsonObject> GetQualifiedGuestBridgePinAsync(bool checkAvailability, CancellationToken cancellation)
    {
        var pin = AtomicFileStore.ReadJsonNode(ResolveScriptAssetPath("guest-bridge-lock.json"), 65536).AsObject();
        bool releaseReady = pin["release_ready"]?.GetValue<bool>() ?? false;
        string? scope = pin["scope"]?.GetValue<string>();
        string? runtimeValidation = pin["runtime_validation"]?.GetValue<string>();
        int kernelCount = pin["qualified_kernels"]?.AsArray().Count ?? 0;
        if (!releaseReady || scope != "generic-metal" || runtimeValidation != "abi-checked" || kernelCount == 0)
            throw new InvalidOperationException("Guest bridge is still a candidate; setup cannot be completed with an unqualified addon.");

        if (checkAvailability) {
            var (url, id, asset, _) = ResolveReleaseAssetInfo(pin);
            try {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var res = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested) {
                throw new InvalidOperationException($"Pinned asset {id}/{asset} is not available. Check the network or wait for publication; setup is not ready.");
            }
        }
        return pin;
    }

    private static async Task<string> CheckGuestBridgeReleaseAsync(CancellationToken cancellation)
    {
        var pin = await GetQualifiedGuestBridgePinAsync(checkAvailability: true, cancellation).ConfigureAwait(false);
        return pin["id"]?.GetValue<string>() ?? "";
    }
}
