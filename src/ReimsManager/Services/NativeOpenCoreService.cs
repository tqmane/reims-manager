using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReimsManager.Services;

internal static partial class NativeOpenCoreService
{
    [GeneratedRegex(@"^([A-Z0-9]{12})\s*\|\s*([A-Z0-9]{17})$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MacSerialPairRegex();

    [GeneratedRegex(@"^\s*Model:\s*(\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MacSerialModelRegex();

    private static readonly (string Name, string Version, string Sha256)[] UpdatePackages = [
        ("Lilu", "1.7.2", "53967d7dcfaab01023a33df2e969a89522f13d6654a6a56ac4711b62dabf3ab8"),
        ("RestrictEvents", "1.1.6", "98170dfae195ddd28b5d95e3f040125a13ca783bcb9bd1e5b8c588e217b14ee6")
    ];

    internal static async Task<string> PrepareOpenCoreAsync(
        string engineRoot, string toolsRoot, string vmDirectory,
        bool generateIdentity = false, string? identityJson = null, bool applyBootProfile = false,
        bool rebuild = false, string firmwareDirectory = "", CancellationToken cancellation = default)
    {
        string resultMessage = "";
        await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => {
                resultMessage = PrepareOpenCoreCoreAsync(
                    engineRoot, toolsRoot, vmDirectory, generateIdentity, identityJson,
                    applyBootProfile, rebuild, firmwareDirectory, cancellation).GetAwaiter().GetResult();
            }), cancellation).ConfigureAwait(false);
        return resultMessage;
    }

    internal static async Task<string> SetIdentityAsync(string engineRoot, string toolsRoot, string vmDirectory, string identityJson, CancellationToken cancellation = default)
    {
        if (identityJson.Length > 4096) throw new InvalidDataException("Identity data is too large.");
        _ = await PrepareOpenCoreAsync(engineRoot, toolsRoot, vmDirectory, identityJson: identityJson, rebuild: true, cancellation: cancellation).ConfigureAwait(false);
        return "識別情報を保存しました。次回起動時に適用します。";
    }

    internal static async Task<string> GenerateIdentityAsync(string engineRoot, string toolsRoot, string vmDirectory, CancellationToken cancellation = default) =>
        await PrepareOpenCoreAsync(engineRoot, toolsRoot, vmDirectory, generateIdentity: true, rebuild: true, cancellation: cancellation).ConfigureAwait(false);

    internal static async Task<string> ApplyBootProfileAsync(string engineRoot, string toolsRoot, string vmDirectory, CancellationToken cancellation = default) =>
        await PrepareOpenCoreAsync(engineRoot, toolsRoot, vmDirectory, applyBootProfile: true, rebuild: true, cancellation: cancellation).ConfigureAwait(false);

    internal static async Task<string> ApplyGuestBridgeAsync(
        string engineRoot, string toolsRoot, string vmDirectory, bool enable,
        string? archivePath = null, string? explicitSha256 = null, CancellationToken cancellation = default)
    {
        string mode = enable ? "Enable" : "Disable";
        return await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => ApplyGuestBridgeCoreAsync(engineRoot, toolsRoot, vmDirectory, mode, archivePath, explicitSha256, cancellation).GetAwaiter().GetResult()), cancellation).ConfigureAwait(false);
    }

    internal static async Task<string> PrepareUpdatesAsync(string engineRoot, string toolsRoot, string vmDirectory, CancellationToken cancellation = default)
    {
        string supportRoot = Path.Combine(vmDirectory, "support");
        string downloads = Path.Combine(supportRoot, "downloads");
        Directory.CreateDirectory(downloads);
        string backupPath = "";

        await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => {
                AtomicFileStore.AssertVmStopped(vmDirectory);
                var oc = AtomicFileStore.ReadJsonNode(Path.Combine(vmDirectory, "state", "opencore.json")).AsObject();
                string plist = AtomicFileStore.AssertArtifactPath(oc["config"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                string ocvalidate = AtomicFileStore.AssertArtifactPath(oc["ocvalidate"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                string ocDir = Path.GetDirectoryName(plist)!;
                string kextsDest = Path.Combine(ocDir, "Kexts");
                Directory.CreateDirectory(kextsDest);

                foreach (var (name, version, sha) in UpdatePackages) {
                    string archive = Path.Combine(downloads, $"{name}-{version}-RELEASE.zip");
                    string url = $"https://github.com/acidanthera/{name}/releases/download/{version}/{name}-{version}-RELEASE.zip";
                    NativeToolRunner.DownloadVerifiedAsync(url, sha, 0, archive, null, cancellation).GetAwaiter().GetResult();
                    string expanded = Path.Combine(supportRoot, "kexts", $"{name}-{version}");
                    if (Directory.Exists(expanded)) Directory.Delete(expanded, true);
                    NativeToolRunner.ExpandZipSafely(archive, expanded, cancellation);
                    string? kextDir = Directory.EnumerateDirectories(expanded, $"{name}.kext", SearchOption.AllDirectories).FirstOrDefault();
                    if (kextDir is null) throw new FileNotFoundException($"Missing kext: {name}");
                    string dst = Path.Combine(kextsDest, Path.GetFileName(kextDir));
                    if (Directory.Exists(dst)) Directory.Delete(dst, true);
                    CopyDirectoryRecursive(kextDir, dst);
                }

                backupPath = BackupOpenCorePlist(vmDirectory, plist, "updates");
                string candidate = backupPath + ".candidate";
                string python = NativeToolRunner.ResolvePythonExecutable(engineRoot, toolsRoot);
                const string patchPy = """
                    import pathlib, plistlib, sys
                    source, destination = map(pathlib.Path, sys.argv[1:])
                    c = plistlib.loads(source.read_bytes())
                    names = ['Lilu', 'RestrictEvents']
                    entries = [dict(Arch='x86_64', BundlePath=n+'.kext', Comment=n+' update compatibility', Enabled=True,
                        ExecutablePath='Contents/MacOS/'+n, MinKernel='25.0.0', MaxKernel='25.99.99', PlistPath='Contents/Info.plist') for n in names]
                    c['Kernel']['Add'] = entries + [p for p in c['Kernel']['Add'] if p['BundlePath'] not in [n+'.kext' for n in names]]
                    c['Kernel']['Quirks']['DisableLinkeditJettison'] = True
                    nv = c['NVRAM']['Add']['7C436110-AB2A-4BBB-A880-FE41995C9F82']
                    args = [a for a in nv.get('boot-args', '').split() if not a.startswith(('revpatch=', 'revblock='))]
                    nv['boot-args'] = ' '.join(args + ['revpatch=sbvmm', 'revblock=none'])
                    destination.write_bytes(plistlib.dumps(c, sort_keys=False))
                    """;
                NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", "-", plist, candidate], stdinText: patchPy, cancellation: cancellation).GetAwaiter().GetResult();
                NativeToolRunner.RunCheckedAsync(ocvalidate, [candidate], cancellation: cancellation).GetAwaiter().GetResult();
                File.Move(candidate, plist, overwrite: true);
                _ = PrepareOpenCoreCoreAsync(engineRoot, toolsRoot, vmDirectory, false, null, false, true, "", cancellation).GetAwaiter().GetResult();
            }), cancellation).ConfigureAwait(false);

        return $"更新検出の互換設定を保存しました。次回起動後に macOS の更新を確認してください。バックアップ: {backupPath}";
    }

    internal static async Task<string> SetupVmAsync(
        string engineRoot, string toolsRoot, string vmDirectory, string isoPath, int diskGiB, int cpus, int memoryMiB, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
            throw new InvalidOperationException("macOS インストーラーを選択してください。");

        var pin = await NativeStatusService.GetQualifiedGuestBridgePinAsync(checkAvailability: false, cancellation).ConfigureAwait(false);
        string engineManifest = Path.Combine(engineRoot, "reims-engine.json");
        if (!File.Exists(engineManifest)) throw new InvalidOperationException("Install and select the matching portable engine before setup.");
        string engineId = AtomicFileStore.ReadJsonNode(engineManifest, 4 * 1024 * 1024)["id"]?.GetValue<string>() ?? "";
        if (!IsGuestBridgeCompatible(pin, engineId))
            throw new InvalidOperationException("Install and select the matching portable engine before setup.");

        _ = await NativeStatusService.GetQualifiedGuestBridgePinAsync(checkAvailability: true, cancellation).ConfigureAwait(false);

        return await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => SetupVmCore(engineRoot, toolsRoot, vmDirectory, isoPath, diskGiB, cpus, memoryMiB, pin, engineId, cancellation)), cancellation).ConfigureAwait(false);
    }

    internal static async Task<string> ImportDiskAsync(
        string engineRoot, string toolsRoot, string vmDirectory, string diskPath, CancellationToken cancellation = default)
    {
        return await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => ImportDiskCore(engineRoot, toolsRoot, vmDirectory, diskPath, cancellation)), cancellation).ConfigureAwait(false);
    }

    internal static async Task<string> SetHardwareWithIsoOrBridgeAsync(
        string engineRoot, string toolsRoot, string vmDirectory,
        int cpus, int memoryMiB, string resolution, string iso, string audio, bool microphone, string usbBackend,
        bool withInstaller, bool preserveNetwork, string network, string adapter, string guestHost, string physicalAdapter,
        CancellationToken cancellation = default)
    {
        if (withInstaller && string.IsNullOrEmpty(iso))
            throw new InvalidOperationException("インストーラーを接続する場合は ISO を選択してください。");

        return await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => {
                AtomicFileStore.AssertVmStopped(vmDirectory);
                if (memoryMiB % 4 != 0) throw new InvalidDataException("メモリは 4 MiB 単位で指定してください。");
                if (!NativeVmControlService.CheckHostHardwareCapacity(cpus, memoryMiB))
                    throw new InvalidOperationException("ホストの CPU / メモリ容量以内で設定してください。");

                var savedConfig = AtomicFileStore.ReadVmConfig(vmDirectory);
                string curUsb = savedConfig["usb_backend"]?.GetValue<string>() ?? "winusb";
                if (usbBackend != curUsb && savedConfig["usb_devices"] is JsonArray usbArr && usbArr.Count > 0)
                    throw new InvalidOperationException("USB の転送方式を変更する前に、登録済み USB の自動接続を解除してください。変更を保存して VM を起動した後、新しい方式の一覧から選び直してください。");

                JsonObject? selectedNet = null;
                if (!preserveNetwork)
                    selectedNet = ResolveSavedNetworkObject(vmDirectory, savedConfig, network, adapter, guestHost, physicalAdapter);

                if (!string.IsNullOrWhiteSpace(iso))
                    PrepareVmMediaAndDisksAsync(engineRoot, toolsRoot, vmDirectory, iso, diskGiB: 128, qmpPort: 4444, sshPort: 2222, cancellation).GetAwaiter().GetResult();

                var config = AtomicFileStore.ReadVmConfig(vmDirectory);
                config["iso"] = iso;
                config["cpus"] = cpus;
                config["memory_mib"] = memoryMiB;
                config["resolution"] = resolution;
                config["audio"] = audio;
                config["microphone"] = microphone;
                config["usb_backend"] = usbBackend;
                config["connect_installer"] = withInstaller;
                if (selectedNet is not null) config["network"] = selectedNet;

                AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, engineRoot, vmDirectory);
                return "仮想マシン設定を保存しました。次回起動に適用します。";
            }), cancellation).ConfigureAwait(false);
    }

    internal static bool CanHandleNetworkSwitchNatively(string vmDirectory, string network, string physicalAdapter)
    {
        if (network != "bridge") return true;
        if (!string.IsNullOrEmpty(physicalAdapter)) {
            var uplink = NativeHostNetwork.GetNetworkInventory().Uplinks.FirstOrDefault(u => u.Id == physicalAdapter || u.Name == physicalAdapter);
            return uplink?.Kind == "wifi";
        }
        var config = File.Exists(Path.Combine(vmDirectory, "state", "config.json"))
            ? AtomicFileStore.ReadJsonNode(Path.Combine(vmDirectory, "state", "config.json")).AsObject()
            : null;
        return config?["network"]?["forwarder"]?.GetValue<string>() is "macnat" or "macnat-ipv4";
    }

    private static JsonObject ResolveSavedNetworkObject(
        string vmDirectory, JsonObject config, string mode, string tapName, string guestHost, string physicalSelection)
    {
        if (!string.IsNullOrEmpty(guestHost) && !Regex.IsMatch(guestHost, @"^[A-Za-z0-9][A-Za-z0-9.:-]*$"))
            throw new InvalidDataException("Invalid guest address.");
        var adapters = NativeHostNetwork.GetNetworkInventory();
        if (mode == "bridge" && !adapters.Taps.Contains(tapName, StringComparer.Ordinal))
            throw new InvalidOperationException("TAP adapter not found.");

        string? existingFwd = config["network"]?["forwarder"]?.GetValue<string>();
        bool isWifi = existingFwd is "macnat" or "macnat-ipv4";

        NetworkUplink? uplink = null;
        if (!string.IsNullOrEmpty(physicalSelection)) {
            uplink = adapters.Uplinks.FirstOrDefault(u => u.Id == physicalSelection || u.Name == physicalSelection)
                ?? throw new InvalidOperationException("Selected uplink adapter was not found.");
        }

        if ((mode == "bridge" && uplink?.Kind == "wifi") || isWifi) {
            if (isWifi && mode != "bridge")
                throw new InvalidOperationException("Disable Wi-Fi compatibility before switching network mode, to restore its owned TAP settings.");
            string targetUplink = uplink?.Id ?? config["network"]?["physical_adapter_id"]?.GetValue<string>() ?? "";
            string wifiPath = Path.Combine(vmDirectory, "state", "wifi-forwarder.json");
            if (!File.Exists(wifiPath))
                throw new InvalidOperationException("Enable this dedicated TAP/Wi-Fi pair before saving Wi-Fi mode.");
            var rec = AtomicFileStore.ReadJsonNode(wifiPath).AsObject();
            string recState = rec["state"]?.GetValue<string>() ?? "";
            string recUplink = rec["uplink_id"]?.GetValue<string>() ?? "";
            string recTapId = rec["tap_id"]?.GetValue<string>() ?? "";
            string recTapName = rec["tap_name"]?.GetValue<string>() ?? "";
            if (recState != "enabled" ||
                !Guid.TryParse(targetUplink, out var uGuid) || !string.Equals(recUplink, uGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                (tapName != recTapName && tapName.Trim('{', '}') != recTapId))
                throw new InvalidOperationException("Enable this dedicated TAP/Wi-Fi pair before saving Wi-Fi mode.");

            bool ipv6 = rec["ipv6_enabled"]?.GetValue<bool>() ?? false;
            string kind = ipv6 ? "macnat" : "macnat-ipv4";
            if (ipv6) NativeStatusService.GetWifiStatusNode(vmDirectory, new JsonObject { ["network"] = new JsonObject { ["mode"] = "bridge", ["forwarder"] = "macnat", ["tap_id"] = recTapId, ["physical_adapter_id"] = recUplink } }, null);

            return new JsonObject {
                ["mode"] = "bridge",
                ["forwarder"] = kind,
                ["adapter"] = recTapName,
                ["tap_id"] = recTapId,
                ["physical_adapter_id"] = recUplink,
                ["guest_host"] = guestHost
            };
        }

        var net = new JsonObject { ["mode"] = mode, ["adapter"] = tapName, ["guest_host"] = guestHost };
        if (uplink is not null) {
            net["physical_adapter_id"] = uplink.Id;
            net["physical_adapter_name"] = uplink.Name;
        } else if (config["network"] is JsonObject prevNet &&
                   prevNet["physical_adapter_id"]?.GetValue<string>() is { Length: > 0 } prevId &&
                   prevNet["adapter"]?.GetValue<string>() == tapName) {
            net["physical_adapter_id"] = prevId;
            net["physical_adapter_name"] = prevNet["physical_adapter_name"]?.GetValue<string>() ?? "";
        }
        return net;
    }

    private static string SetupVmCore(
        string engineRoot, string toolsRoot, string vmDirectory, string isoPath, int diskGiB, int cpus, int memoryMiB,
        JsonObject pin, string engineId, CancellationToken cancellation)
    {
        AtomicFileStore.AssertVmStopped(vmDirectory);
        string configPath = Path.Combine(vmDirectory, "state", "config.json");
        string ocPath = Path.Combine(vmDirectory, "state", "opencore.json");
        string prepPath = Path.Combine(vmDirectory, "state", "setup-preparation.json");
        bool existing = File.Exists(configPath);
        if (existing && !File.Exists(ocPath))
            throw new InvalidOperationException("Existing VM is missing its OpenCore manifest. Restore its matched EFI/identity metadata before setup; it was not regenerated.");

        string backupDir = Path.Combine(vmDirectory, "backups", $"setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDir);
        var saved = new Dictionary<string, string>();
        foreach (string name in new[] { "config.json", "opencore.json", "setup-preparation.json" }) {
            string p = Path.Combine(vmDirectory, "state", name);
            if (File.Exists(p)) {
                File.Copy(p, Path.Combine(backupDir, name));
                saved[name] = p;
            }
        }

        try {
            if (!File.Exists(ocPath))
                _ = PrepareOpenCoreCoreAsync(engineRoot, toolsRoot, vmDirectory, false, null, false, true, "", cancellation).GetAwaiter().GetResult();

            var (qmpPort, sshPort) = AllocateNewVmPorts(engineRoot, vmDirectory);
            PrepareVmMediaAndDisksAsync(engineRoot, toolsRoot, vmDirectory, isoPath, diskGiB, qmpPort, sshPort, cancellation).GetAwaiter().GetResult();

            string identityFile = Path.Combine(vmDirectory, "state", "opencore-identity.json");
            if (!existing && !File.Exists(identityFile))
                _ = PrepareOpenCoreCoreAsync(engineRoot, toolsRoot, vmDirectory, true, null, false, true, "", cancellation).GetAwaiter().GetResult();

            var config = AtomicFileStore.ReadVmConfig(vmDirectory);
            config["cpus"] = cpus;
            config["memory_mib"] = memoryMiB;
            if (!existing) config["connect_installer"] = true;
            AtomicFileStore.WriteJsonAtomic(configPath, config, engineRoot, vmDirectory);

            PrepareToolsCoreAsync(engineRoot, toolsRoot, vmDirectory, cancellation).GetAwaiter().GetResult();
            _ = ApplyGuestBridgeCoreAsync(engineRoot, toolsRoot, vmDirectory, "Enable", null, null, cancellation).GetAwaiter().GetResult();

            var prep = new JsonObject {
                ["schema"] = 1,
                ["complete"] = true,
                ["engine_id"] = engineId,
                ["guest_addon_id"] = pin["id"]?.GetValue<string>(),
                ["macos_installation"] = "user",
                ["prepared_utc"] = DateTime.UtcNow.ToString("O")
            };
            AtomicFileStore.WriteJsonAtomic(prepPath, prep, engineRoot, vmDirectory);

            var sb = new StringBuilder();
            sb.AppendLine("準備完了。インストーラー付きで起動し、macOS のインストールと初回設定を行ってください。");
            sb.AppendLine("macOS 設定後、ReimsTools の setup.command を実行して SSH・共有機能をペアリングしてください。");
            if (pin["limitations"] is JsonArray lims) {
                foreach (var l in lims) if (l?.GetValue<string>() is { } s) sb.AppendLine(s);
            }
            return sb.ToString().TrimEnd();
        } catch {
            foreach (var kv in saved) {
                string src = Path.Combine(backupDir, kv.Key);
                if (!File.Exists(kv.Value) || !string.Equals(NativeToolRunner.ComputeSha256Lower(kv.Value), NativeToolRunner.ComputeSha256Lower(src), StringComparison.Ordinal))
                    File.Copy(src, kv.Value, overwrite: true);
            }
            foreach (string name in new[] { "config.json", "setup-preparation.json" }) {
                if (!saved.ContainsKey(name)) {
                    string p = Path.Combine(vmDirectory, "state", name);
                    if (File.Exists(p)) try { File.Delete(p); } catch { }
                }
            }
            throw;
        }
    }

    private static string ImportDiskCore(string engineRoot, string toolsRoot, string vmDirectory, string diskPath, CancellationToken cancellation)
    {
        AtomicFileStore.AssertVmStopped(vmDirectory);
        string stateDir = Path.Combine(vmDirectory, "state");
        string configPath = Path.Combine(stateDir, "config.json");
        if (File.Exists(configPath)) throw new InvalidOperationException("既存の VM に上書きせず、新しい VM を作成してください。");
        if (Directory.Exists(diskPath) || !File.Exists(diskPath)) throw new InvalidOperationException("仮想ディスクのファイルを選択してください。");
        string fullSource = Path.GetFullPath(diskPath);

        string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");
        string infoJson = NativeToolRunner.RunCheckedAsync(qemuImg, ["info", "--output=json", fullSource], cancellation: cancellation).GetAwaiter().GetResult();
        using var infoDoc = JsonDocument.Parse(infoJson);
        string format = infoDoc.RootElement.GetProperty("format").GetString() ?? "";
        long virtualSize = infoDoc.RootElement.GetProperty("virtual-size").GetInt64();
        if (format is not ("qcow2" or "vmdk" or "raw") || virtualSize <= 0)
            throw new InvalidOperationException("QCOW2、VMDK、RAW の仮想ディスクを選択してください。");

        string disksDir = Path.Combine(vmDirectory, "disks");
        Directory.CreateDirectory(disksDir);
        string baseDisk = Path.Combine(disksDir, "base.qcow2");
        string activeDisk = Path.Combine(disksDir, "current.qcow2");
        string partial = baseDisk + ".partial";
        if (File.Exists(baseDisk) || File.Exists(partial) || File.Exists(activeDisk))
            throw new IOException("取り込み先には既にディスクがあります。新しい保存先を選択してください。");

        using (new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            NativeToolRunner.RunCheckedAsync(qemuImg, ["convert", "-f", format, "-O", "qcow2", fullSource, partial], timeoutMs: 600_000, cancellation: cancellation).GetAwaiter().GetResult();
        }
        NativeToolRunner.RunCheckedAsync(qemuImg, ["check", "-q", partial], timeoutMs: 300_000, cancellation: cancellation).GetAwaiter().GetResult();
        File.Move(partial, baseDisk, overwrite: false);
        NativeToolRunner.RunCheckedAsync(qemuImg, ["create", "-f", "qcow2", "-F", "qcow2", "-b", baseDisk, activeDisk], cancellation: cancellation).GetAwaiter().GetResult();

        _ = PrepareOpenCoreCoreAsync(engineRoot, toolsRoot, vmDirectory, false, null, false, false, "", cancellation).GetAwaiter().GetResult();
        var oc = AtomicFileStore.ReadJsonNode(Path.Combine(stateDir, "opencore.json")).AsObject();
        string vars = Path.Combine(vmDirectory, "firmware", "OVMF_VARS.fd");
        Directory.CreateDirectory(Path.GetDirectoryName(vars)!);
        File.Copy(oc["firmware_vars_template"]!.GetValue<string>(), vars, overwrite: true);

        var (qmpPort, sshPort) = AllocateNewVmPorts(engineRoot, vmDirectory);
        string rom = ResolveEngineFirmwareOrRomPath(engineRoot, "rom", ".work/target/dist-vulkan/share/reims-vgpu/ReimsVgpu.rom");
        var cfg = new JsonObject {
            ["schema"] = 1,
            ["cpus"] = 4,
            ["memory_mib"] = 8192,
            ["resolution"] = "1920x1080",
            ["disk_gib"] = (int)Math.Ceiling(virtualSize / (1024.0 * 1024.0 * 1024.0)),
            ["cpu"] = "Haswell-noTSX,stepping=3,vendor=GenuineIntel,+ssse3,+sse4.2,+popcnt,+avx,+avx2,+aes,+xsave,+xsaveopt,-arat,enforce",
            ["mac"] = GenerateRandomMac(),
            ["qmp_port"] = qmpPort,
            ["ssh_port"] = sshPort,
            ["iso"] = "",
            ["media_kind"] = "cdrom",
            ["connect_installer"] = false,
            ["base_disk"] = baseDisk,
            ["active_disk"] = activeDisk,
            ["opencore"] = oc["image"]!.GetValue<string>(),
            ["firmware_code"] = oc["firmware_code"]!.GetValue<string>(),
            ["firmware_vars"] = vars,
            ["audio"] = "dsound",
            ["microphone"] = false,
            ["rom"] = rom
        };
        AtomicFileStore.WriteJsonAtomic(configPath, cfg, engineRoot, vmDirectory);
        _ = PrepareOpenCoreCoreAsync(engineRoot, toolsRoot, vmDirectory, true, null, false, true, "", cancellation).GetAwaiter().GetResult();
        PrepareToolsCoreAsync(engineRoot, toolsRoot, vmDirectory, cancellation).GetAwaiter().GetResult();
        return "ディスクのコピーを新しい VM に取り込みました。元のファイルは変更していません。";
    }

    private static async Task<string> PrepareOpenCoreCoreAsync(
        string engineRoot, string toolsRoot, string vmDirectory,
        bool generateIdentity, string? identityJson, bool applyBootProfile, bool rebuild, string firmwareDirectory,
        CancellationToken cancellation)
    {
        AtomicFileStore.AssertVmStopped(vmDirectory);
        if (generateIdentity && !rebuild) throw new InvalidOperationException("Use -GenerateIdentity with -Rebuild.");
        if (identityJson is not null && (!rebuild || generateIdentity)) throw new InvalidOperationException("Use -IdentityFile with -Rebuild.");
        if (applyBootProfile && !rebuild) throw new InvalidOperationException("Use -ApplyBootProfile with -Rebuild.");

        string supportRoot = Path.Combine(vmDirectory, "support");
        string downloads = Path.Combine(supportRoot, "downloads");
        Directory.CreateDirectory(downloads);

        var lockDoc = AtomicFileStore.ReadJsonNode(NativeStatusService.ResolveScriptAssetPath("engine-lock.json"), 65536).AsObject();
        var ocPin = lockDoc["opencore"]?.AsObject() ?? throw new InvalidDataException("Missing OpenCore pin.");
        string version = ocPin["version"]?.GetValue<string>() ?? "1.0.8";
        string expectedHash = ocPin["sha256"]?.GetValue<string>() ?? "";
        string url = ocPin["url"]?.GetValue<string>() ?? "";
        string archive = File.Exists(Path.Combine(engineRoot, "reims-engine.json"))
            ? ResolveEngineFirmwareOrRomPath(engineRoot, "opencore_archive", "")
            : Path.Combine(downloads, ocPin["file"]!.GetValue<string>());

        await NativeToolRunner.DownloadVerifiedAsync(url, expectedHash, 0, archive, null, cancellation).ConfigureAwait(false);
        string package = Path.Combine(supportRoot, "opencore", $"OpenCore-{version}-{expectedHash[..12]}");
        string marker = package + ".sha256";
        if (!File.Exists(marker)) {
            if (Directory.Exists(package)) throw new IOException("Unverified OpenCore extraction already exists; use a new engine folder.");
            NativeToolRunner.ExpandZipSafely(archive, package, cancellation);
            File.WriteAllText(marker, expectedHash, Encoding.ASCII);
        }
        if (!string.Equals(File.ReadAllText(marker).Trim(), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OpenCore extraction belongs to a different release.");

        string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");
        string python = NativeToolRunner.ResolvePythonExecutable(engineRoot, toolsRoot);
        string destination = Path.Combine(vmDirectory, "firmware", $"opencore-{version}");
        string efiRoot = Path.Combine(destination, "root");
        string image = Path.Combine(destination, "OpenCore.img");
        string manifestPath = Path.Combine(vmDirectory, "state", "opencore.json");

        JsonObject? active = null;
        if (File.Exists(manifestPath)) {
            active = AtomicFileStore.ReadJsonNode(manifestPath).AsObject();
            if (active["version"]?.GetValue<string>() != version)
                throw new InvalidOperationException("Keep the installed OpenCore version when rebuilding an existing VM.");
            string activeConfig = AtomicFileStore.AssertArtifactPath(active["config"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
            efiRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(activeConfig)!)!)!;
            if (!string.Equals(Path.GetFullPath(Path.Combine(efiRoot, "EFI", "OC", "config.plist")), activeConfig, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected active OpenCore configuration layout.");
            destination = Path.GetDirectoryName(efiRoot)!;
            image = AtomicFileStore.AssertArtifactPath(active["image"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
        }

        string fwDir = !string.IsNullOrEmpty(firmwareDirectory)
            ? firmwareDirectory
            : ResolveDefaultFirmwareDir(engineRoot, active);
        var (selCode, selVars, fwSource) = ResolveFirmwarePair(engineRoot, vmDirectory, active, fwDir);

        string codeDst = active is not null ? selCode : Path.Combine(vmDirectory, "firmware", "OVMF_CODE.fd");
        string varsDst = active is not null ? selVars : Path.Combine(vmDirectory, "firmware", "OVMF_VARS.template.fd");
        foreach (var (src, dst) in new[] { (selCode, codeDst), (selVars, varsDst) }) {
            if (File.Exists(dst) && !string.Equals(NativeToolRunner.ComputeSha256Upper(src), NativeToolRunner.ComputeSha256Upper(dst), StringComparison.Ordinal))
                throw new InvalidOperationException("Existing unrecorded firmware differs. Restore its matching manifest or select a new VM folder; no OVMF file was replaced.");
        }
        foreach (var (src, dst) in new[] { (selCode, codeDst), (selVars, varsDst) }) {
            if (!File.Exists(dst)) {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: false);
            }
        }

        Directory.CreateDirectory(Path.Combine(efiRoot, "EFI", "BOOT"));
        Directory.CreateDirectory(Path.Combine(efiRoot, "EFI", "OC", "Drivers"));
        foreach (string rel in new[] { "BOOT/BOOTx64.efi", "OC/OpenCore.efi", "OC/Drivers/OpenRuntime.efi", "OC/Drivers/OpenPartitionDxe.efi", "OC/Drivers/OpenHfsPlus.efi" })
            File.Copy(Path.Combine(package, "X64", "EFI", rel), Path.Combine(efiRoot, "EFI", rel), overwrite: true);

        string plist = Path.Combine(efiRoot, "EFI", "OC", "config.plist");
        if (!File.Exists(plist)) {
            string basePy = NativeStatusService.ResolveScriptAssetPath("opencore/base.py");
            await NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", basePy, Path.Combine(package, "Docs", "Sample.plist"), plist], cancellation: cancellation).ConfigureAwait(false);
        }

        string ocvalidate = Path.Combine(package, "Utilities", "ocvalidate", "ocvalidate.exe");
        string macserial = Path.Combine(package, "Utilities", "macserial", "macserial.exe");
        string outputMsg = "";

        if (generateIdentity) {
            var vmConfig = AtomicFileStore.ReadVmConfig(vmDirectory);
            string model = (await NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", "-c", "import plistlib,sys; print(plistlib.load(open(sys.argv[1],'rb'))['PlatformInfo']['Generic']['SystemProductName'])", plist], cancellation: cancellation).ConfigureAwait(false)).Trim();
            string pairs = await NativeToolRunner.RunCheckedAsync(macserial, ["-m", model, "-n", "1"], cancellation: cancellation).ConfigureAwait(false);
            var m = MacSerialPairRegex().Match(pairs);
            if (!m.Success) throw new InvalidOperationException("macserial did not return a serial/MLB pair.");
            string identityPath = Path.Combine(vmDirectory, "state", "opencore-identity.json");
            var idObj = new JsonObject {
                ["model"] = model,
                ["serial"] = m.Groups[1].Value,
                ["mlb"] = m.Groups[2].Value,
                ["mac"] = vmConfig["mac"]!.GetValue<string>()
            };
            AtomicFileStore.WriteJsonAtomic(identityPath, idObj, engineRoot, vmDirectory);
            string backup = BackupOpenCorePlist(vmDirectory, plist, "identity");
            const string pyGen = """
                import json, pathlib, plistlib, sys, uuid
                p, manifest = map(pathlib.Path, sys.argv[1:])
                c = plistlib.loads(p.read_bytes())
                i = json.loads(manifest.read_text(encoding='utf-8-sig'))
                g = c['PlatformInfo']['Generic']
                assert g['SystemProductName'] == i['model'], 'Keep the installed Mac model'
                uuid.UUID(g['SystemUUID'])
                rom = bytes.fromhex(i['mac'].replace(':', ''))
                assert len(rom) == 6 and not rom[0] & 1, 'ROM must be a unicast VM MAC'
                g.update(SystemSerialNumber=i['serial'], MLB=i['mlb'], ROM=rom)
                c['PlatformInfo'].update(Automatic=True, UpdateDataHub=True, UpdateNVRAM=True, UpdateSMBIOS=True)
                i.update(uuid=g['SystemUUID'], rom=rom.hex())
                p.write_bytes(plistlib.dumps(c, sort_keys=False))
                manifest.write_text(json.dumps(i, indent=2) + '\n', encoding='utf-8')
                """;
            await NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", "-", plist, identityPath], stdinText: pyGen, cancellation: cancellation).ConfigureAwait(false);
            outputMsg = $"Generated a VM-specific identity; preserved SystemUUID. Backup: {backup}";
        }

        if (applyBootProfile) {
            string backup = BackupOpenCorePlist(vmDirectory, plist, "boot-profile");
            string candidate = backup + ".candidate";
            string bootPy = NativeStatusService.ResolveScriptAssetPath("opencore/boot-policy.py");
            await NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", bootPy, plist, candidate], cancellation: cancellation).ConfigureAwait(false);
            await NativeToolRunner.RunCheckedAsync(ocvalidate, [candidate], cancellation: cancellation).ConfigureAwait(false);
            File.Move(candidate, plist, overwrite: true);
            outputMsg = $"起動項目を APFS / HFS に限定しました。変更前の設定: {backup}";
        }

        if (identityJson is not null) {
            string backup = BackupOpenCorePlist(vmDirectory, plist, "edit");
            string candidate = backup + ".candidate";
            string tempIdFile = Path.Combine(vmDirectory, "support", "tmp", $"identity-{Guid.NewGuid():N}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(tempIdFile)!);
            try {
                File.WriteAllText(tempIdFile, identityJson, AtomicFileStore.Utf8NoBom);
                const string pyEdit = """
                    import json, pathlib, plistlib, re, sys, uuid
                    source, destination, identity = map(pathlib.Path, sys.argv[1:])
                    c = plistlib.loads(source.read_bytes())
                    i = json.loads(identity.read_text(encoding='utf-8-sig'))
                    assert set(i) == {'model','serial','mlb','uuid','rom'}, 'Pass all five identity fields'
                    assert all(isinstance(v,str) for v in i.values()), 'Identity fields must be strings'
                    i = {k:v.strip() for k,v in i.items()}
                    assert re.fullmatch(r'(?:iMac|MacPro|Macmini|MacBookPro|MacBookAir|MacBook)\d+,\d+', i['model']), 'Invalid Mac model'
                    assert re.fullmatch(r'[A-Z0-9]{10,12}', i['serial']), 'Serial must be 10 to 12 uppercase letters/digits'
                    assert re.fullmatch(r'[A-Z0-9]{17}', i['mlb']), 'MLB must be 17 uppercase letters/digits'
                    u = uuid.UUID(i['uuid']); assert u.int, 'UUID cannot be zero'
                    assert re.fullmatch(r'[0-9A-Fa-f]{12}', i['rom']), 'ROM must be 12 hex digits'
                    rom = bytes.fromhex(i['rom']); assert any(rom) and not rom[0] & 1, 'ROM must be a nonzero unicast address'
                    c['PlatformInfo']['Generic'].update(SystemProductName=i['model'],SystemSerialNumber=i['serial'],
                        MLB=i['mlb'],SystemUUID=str(u).upper(),ROM=rom)
                    c['PlatformInfo'].update(Automatic=True,UpdateDataHub=True,UpdateNVRAM=True,UpdateSMBIOS=True)
                    destination.write_bytes(plistlib.dumps(c,sort_keys=False))
                    i.update(uuid=str(u).upper(),rom=rom.hex()); identity.write_text(json.dumps(i),encoding='utf-8')
                    """;
                try {
                    await NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", "-", plist, candidate, tempIdFile], stdinText: pyEdit, cancellation: cancellation).ConfigureAwait(false);
                } catch {
                    throw new InvalidDataException("識別情報の形式を確認してください。元の設定は保持されています。");
                }
                var editedModel = AtomicFileStore.ReadJsonNode(tempIdFile)["model"]?.GetValue<string>() ?? "";
                string modelsOut = await NativeToolRunner.RunCheckedAsync(macserial, ["-l"], cancellation: cancellation).ConfigureAwait(false);
                if (!MacSerialModelRegex().Matches(modelsOut).Any(m => string.Equals(m.Groups[1].Value, editedModel, StringComparison.Ordinal)))
                    throw new InvalidOperationException("OpenCore が認識する Mac モデルを指定してください。");
                await NativeToolRunner.RunCheckedAsync(ocvalidate, [candidate], cancellation: cancellation).ConfigureAwait(false);
                File.Move(candidate, plist, overwrite: true);
                File.Copy(tempIdFile, Path.Combine(vmDirectory, "state", "opencore-identity.json"), overwrite: true);
                outputMsg = $"識別情報を保存しました。変更前の設定: {backup}";
            } finally {
                if (File.Exists(tempIdFile)) try { File.Delete(tempIdFile); } catch { }
                if (File.Exists(candidate)) try { File.Delete(candidate); } catch { }
            }
        }

        string validationLog = await NativeToolRunner.RunCheckedAsync(ocvalidate, [plist], cancellation: cancellation).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(destination, "ocvalidate.log"), validationLog, AtomicFileStore.Utf8NoBom);
        string configHash = NativeToolRunner.ComputeSha256Upper(plist);

        if (!rebuild && File.Exists(image) && File.Exists(manifestPath)) {
            var prev = AtomicFileStore.ReadJsonNode(manifestPath).AsObject();
            if (prev["image"]?.GetValue<string>() == image &&
                (!string.Equals(prev["config_sha256"]?.GetValue<string>(), configHash, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(prev["image_sha256"]?.GetValue<string>(), NativeToolRunner.ComputeSha256Upper(image), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("OpenCore config or image changed. Use -Rebuild to validate and rebuild its EFI image.");
        }

        if (rebuild || !File.Exists(image))
            await BuildAndVerifyOpenCoreImageAsync(qemuImg, efiRoot, image, cancellation).ConfigureAwait(false);

        var nextManifest = new JsonObject {
            ["schema"] = 1,
            ["version"] = version,
            ["release_url"] = url,
            ["release_sha256"] = expectedHash,
            ["image"] = image,
            ["image_sha256"] = NativeToolRunner.ComputeSha256Upper(image),
            ["config"] = plist,
            ["config_sha256"] = configHash,
            ["ocvalidate"] = ocvalidate,
            ["firmware_code"] = codeDst,
            ["firmware_vars_template"] = varsDst,
            ["code_sha256"] = NativeToolRunner.ComputeSha256Upper(codeDst),
            ["vars_sha256"] = NativeToolRunner.ComputeSha256Upper(varsDst),
            ["firmware_source"] = fwSource,
            ["validated"] = true
        };
        AtomicFileStore.WriteJsonAtomic(manifestPath, nextManifest, engineRoot, vmDirectory);
        return string.IsNullOrEmpty(outputMsg) ? nextManifest.ToJsonString() : outputMsg;
    }

    private static async Task<string> ApplyGuestBridgeCoreAsync(
        string engineRoot, string toolsRoot, string vmDirectory, string mode,
        string? archivePath, string? explicitSha256, CancellationToken cancellation)
    {
        AtomicFileStore.AssertVmStopped(vmDirectory);
        _ = AtomicFileStore.ReadVmConfig(vmDirectory);
        string pinPath = NativeStatusService.ResolveScriptAssetPath("guest-bridge-lock.json");
        var pin = AtomicFileStore.ReadJsonNode(pinPath, 65536).AsObject();

        string statePath = Path.Combine(vmDirectory, "state", "config.json");
        string ocPath = Path.Combine(vmDirectory, "state", "opencore.json");
        string stateHash = NativeToolRunner.ComputeSha256Upper(statePath);
        string ocHash = NativeToolRunner.ComputeSha256Upper(ocPath);
        var state = AtomicFileStore.ReadJsonNode(statePath).AsObject();
        var oc = AtomicFileStore.ReadJsonNode(ocPath).AsObject();

        string vmPrefix = Path.GetFullPath(vmDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string cfgPlist = AtomicFileStore.AssertArtifactPath(oc["config"]!.GetValue<string>(), engineRoot, vmDirectory);
        string curImg = AtomicFileStore.AssertArtifactPath(oc["image"]!.GetValue<string>(), engineRoot, vmDirectory);
        if (!cfgPlist.StartsWith(vmPrefix, StringComparison.OrdinalIgnoreCase) || !curImg.StartsWith(vmPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenCore files must belong to the selected VM.");
        if (!string.Equals(Path.GetFullPath(state["opencore"]!.GetValue<string>()), curImg, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("VM and OpenCore manifests select different images; reconcile them before applying the bridge.");

        string sourceRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cfgPlist)!)!)!;
        if (!string.Equals(Path.GetFullPath(Path.Combine(sourceRoot, "EFI", "OC", "config.plist")), cfgPlist, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected OpenCore layout.");
        AtomicFileStore.AssertNoReparsePointsInTree(sourceRoot);
        string ocvalidate = AtomicFileStore.AssertArtifactPath(oc["ocvalidate"]!.GetValue<string>(), engineRoot, vmDirectory);
        string python = NativeToolRunner.ResolvePythonExecutable(engineRoot, toolsRoot);
        string patchPy = NativeStatusService.ResolveScriptAssetPath("opencore/guest-bridge.py");

        if (mode == "Enable") {
            await NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", patchPy, "check-release", ".", pinPath], cancellation: cancellation).ConfigureAwait(false);
            string engId = AtomicFileStore.ReadJsonNode(Path.Combine(engineRoot, "reims-engine.json"), 4 * 1024 * 1024)["id"]?.GetValue<string>() ?? "";
            if (!IsGuestBridgeCompatible(pin, engId))
                throw new InvalidOperationException("Select the matching portable engine package before applying this guest bridge.");
        }

        string supportTmp = Path.Combine(vmDirectory, "support", "tmp");
        Directory.CreateDirectory(supportTmp);
        string work = Path.Combine(supportTmp, $"guest-bridge-{Guid.NewGuid():N}");
        string slot = Path.Combine(vmDirectory, "firmware", $"guest-bridge-{Guid.NewGuid():N}");
        bool keepSlot = false;
        FileStream? archiveLock = null;
        Directory.CreateDirectory(work);
        try {
            string package = Path.Combine(work, "package");
            if (mode == "Enable") {
                bool localArchive = !string.IsNullOrWhiteSpace(archivePath);
                if (!localArchive) {
                    var (url, id, _, bytes) = NativeStatusService.ResolveReleaseAssetInfo(pin);
                    string downloads = Path.Combine(vmDirectory, "support", "downloads");
                    Directory.CreateDirectory(downloads);
                    archivePath = Path.Combine(downloads, $"{id}.zip");
                    await NativeToolRunner.DownloadVerifiedAsync(url, null, bytes, archivePath, null, cancellation).ConfigureAwait(false);
                } else {
                    archivePath = Path.GetFullPath(archivePath!);
                    string expected = explicitSha256 ?? File.ReadAllText(archivePath + ".sha256").Trim().Split(' ', '\t')[0];
                    archiveLock = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (!string.Equals(NativeToolRunner.ComputeSha256Lower(archivePath), expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Guest bridge archive SHA256 mismatch.");
                }
                NativeToolRunner.ExpandZipSafely(archivePath!, package, cancellation);
                archiveLock?.Dispose();
                archiveLock = null;
                await NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", patchPy, "verify", package, pinPath], cancellation: cancellation).ConfigureAwait(false);
            }

            Directory.CreateDirectory(slot);
            string slotRoot = Path.Combine(slot, "root");
            CopyDirectoryRecursive(sourceRoot, slotRoot);
            var patchArgs = new List<string> { "-X", "utf8", patchPy, mode.ToLowerInvariant(), slotRoot, pinPath };
            if (mode == "Enable") { patchArgs.Add("--package"); patchArgs.Add(package); }
            await NativeToolRunner.RunCheckedAsync(python, patchArgs, cancellation: cancellation).ConfigureAwait(false);

            string newPlist = Path.Combine(slotRoot, "EFI", "OC", "config.plist");
            await NativeToolRunner.RunCheckedAsync(ocvalidate, [newPlist], cancellation: cancellation).ConfigureAwait(false);
            string newImage = Path.Combine(slot, "OpenCore.img");
            string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");
            await BuildAndVerifyOpenCoreImageAsync(qemuImg, slotRoot, newImage, cancellation).ConfigureAwait(false);

            AtomicFileStore.AssertVmStopped(vmDirectory);
            if (!string.Equals(NativeToolRunner.ComputeSha256Upper(statePath), stateHash, StringComparison.Ordinal) ||
                !string.Equals(NativeToolRunner.ComputeSha256Upper(ocPath), ocHash, StringComparison.Ordinal))
                throw new IOException("VM settings changed during preparation; the active image was not replaced.");

            string beforeCfg = Path.Combine(slot, "before-config.json");
            string beforeOc = Path.Combine(slot, "before-opencore.json");
            File.Copy(statePath, beforeCfg, overwrite: true);
            File.Copy(ocPath, beforeOc, overwrite: true);
            oc["config"] = newPlist;
            oc["config_sha256"] = NativeToolRunner.ComputeSha256Upper(newPlist);
            oc["image"] = newImage;
            oc["image_sha256"] = NativeToolRunner.ComputeSha256Upper(newImage);
            state["opencore"] = newImage;
            keepSlot = true;
            try {
                AtomicFileStore.WriteJsonAtomic(ocPath, oc, engineRoot, vmDirectory);
                AtomicFileStore.WriteJsonAtomic(statePath, state, engineRoot, vmDirectory);
            } catch {
                File.Copy(beforeOc, ocPath, overwrite: true);
                File.Copy(beforeCfg, statePath, overwrite: true);
                throw;
            }
            return $"Metal / WebGL 互換設定: {mode} / {pin["abi_family"]?.GetValue<string>()}。元の EFI と識別情報を保持しました。";
        } finally {
            archiveLock?.Dispose();
            if (Directory.Exists(work)) try { Directory.Delete(work, true); } catch { }
            if (!keepSlot && Directory.Exists(slot)) try { Directory.Delete(slot, true); } catch { }
        }
    }

    private static async Task PrepareVmMediaAndDisksAsync(
        string engineRoot, string toolsRoot, string vmDirectory,
        string isoPath, int diskGiB, int qmpPort, int sshPort, CancellationToken cancellation)
    {
        var isoFile = new FileInfo(isoPath);
        if (!isoFile.Exists) throw new FileNotFoundException("Installer image not found.", isoPath);
        string fullIso = isoFile.FullName;
        long length = isoFile.Length;
        long modified = isoFile.LastWriteTimeUtc.Ticks;

        string sevenZip = NativeToolRunner.ResolveEngineTool(engineRoot, "seven_zip");
        string python = NativeToolRunner.ResolvePythonExecutable(engineRoot, toolsRoot);
        string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");
        string inspection = Path.Combine(vmDirectory, "media", "inspection");
        string stateDir = Path.Combine(vmDirectory, "state");
        Directory.CreateDirectory(inspection);
        Directory.CreateDirectory(stateDir);

        string listing = await NativeToolRunner.RunCheckedAsync(sevenZip, ["l", "-slt", fullIso], timeoutMs: 120_000, cancellation: cancellation).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(inspection, "contents.txt"), listing, AtomicFileStore.Utf8NoBom);
        string[] paths = listing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("Path = ", StringComparison.Ordinal))
            .Select(l => l[7..])
            .ToArray();
        string? verPath = paths.FirstOrDefault(p => Regex.IsMatch(p.Replace('\\', '/'), @"(^|/)System/Library/CoreServices/SystemVersion\.plist$"));
        string? bootPath = paths.FirstOrDefault(p => Regex.IsMatch(p.Replace('\\', '/'), @"(^|/)System/Library/CoreServices/boot\.efi$"));
        if (verPath is null || bootPath is null)
            throw new InvalidDataException("No installer SystemVersion.plist and boot.efi found; media has not been prepared.");

        await NativeToolRunner.RunCheckedAsync(sevenZip, ["e", "-y", $"-o{inspection}", fullIso, verPath, bootPath], timeoutMs: 120_000, cancellation: cancellation).ConfigureAwait(false);
        const string inspectPy = """
            import json, pathlib, plistlib, struct, sys
            image, folder = map(pathlib.Path, sys.argv[1:3])
            with image.open('rb') as f:
                header = f.read(1024)
                f.seek(16 * 2048)
                iso9660 = f.read(7)[1:6] == b'CD001'
            if header[:2] == b'ER' and header[512:514] == b'PM':
                kind, container = 'disk', 'APM/HFS+'
            elif iso9660:
                kind, container = 'cdrom', 'ISO9660'
            else:
                raise SystemExit('Unsupported media header; refusing to infer format from extension.')
            version = plistlib.loads((folder / 'SystemVersion.plist').read_bytes())
            b = (folder / 'boot.efi').read_bytes()
            if b[:2] != b'MZ' or len(b) < 64:
                raise SystemExit('boot.efi is not a PE image.')
            off = struct.unpack_from('<I', b, 0x3c)[0]
            if b[off:off+4] != b'PE\0\0' or struct.unpack_from('<H', b, off+4)[0] != 0x8664:
                raise SystemExit('Installer boot.efi is not x86_64.')
            print(json.dumps({'schema':1, 'path':str(image), 'bytes':image.stat().st_size,
                'container':container, 'media_kind':kind, 'boot_architecture':'x86_64',
                'product_version':version['ProductVersion'], 'product_build':version['ProductBuildVersion']}))
            """;
        string mediaJson = await NativeToolRunner.RunCheckedAsync(python, ["-X", "utf8", "-", fullIso, inspection], stdinText: inspectPy, cancellation: cancellation).ConfigureAwait(false);
        var mediaObj = JsonNode.Parse(mediaJson)!.AsObject();
        mediaObj["last_write_ticks"] = modified;
        isoFile.Refresh();
        if (isoFile.Length != length || isoFile.LastWriteTimeUtc.Ticks != modified)
            throw new IOException("Installer changed during inspection.");

        string mediaStatePath = Path.Combine(stateDir, "media.json");
        AtomicFileStore.WriteJsonAtomic(mediaStatePath, mediaObj, engineRoot, vmDirectory);

        string configPath = Path.Combine(stateDir, "config.json");
        string mediaKind = mediaObj["media_kind"]!.GetValue<string>();
        if (File.Exists(configPath)) {
            var existing = AtomicFileStore.ReadVmConfig(vmDirectory);
            existing["iso"] = fullIso;
            existing["media_kind"] = mediaKind;
            AtomicFileStore.WriteJsonAtomic(configPath, existing, engineRoot, vmDirectory);
            return;
        }

        string ocPath = Path.Combine(stateDir, "opencore.json");
        if (!File.Exists(ocPath)) throw new InvalidOperationException("Prepare OpenCore first.");
        var oc = AtomicFileStore.ReadJsonNode(ocPath).AsObject();
        string ocImage = AtomicFileStore.AssertArtifactPath(oc["image"]!.GetValue<string>(), engineRoot, vmDirectory);
        string fwCode = AtomicFileStore.AssertArtifactPath(oc["firmware_code"]!.GetValue<string>(), engineRoot, vmDirectory);
        string fwTemplate = AtomicFileStore.AssertArtifactPath(oc["firmware_vars_template"]!.GetValue<string>(), engineRoot, vmDirectory);

        string disksDir = Path.Combine(vmDirectory, "disks");
        Directory.CreateDirectory(disksDir);
        string baseDisk = Path.Combine(disksDir, "base.qcow2");
        string activeDisk = Path.Combine(disksDir, "current.qcow2");
        if (!File.Exists(baseDisk))
            await NativeToolRunner.RunCheckedAsync(qemuImg, ["create", "-f", "qcow2", baseDisk, $"{diskGiB}G"], cancellation: cancellation).ConfigureAwait(false);
        using (var baseInfo = JsonDocument.Parse(await NativeToolRunner.RunCheckedAsync(qemuImg, ["info", "--output=json", baseDisk], cancellation: cancellation).ConfigureAwait(false))) {
            var root = baseInfo.RootElement;
            if (root.GetProperty("format").GetString() != "qcow2" ||
                root.GetProperty("virtual-size").GetInt64() != (long)diskGiB * 1024L * 1024L * 1024L ||
                root.TryGetProperty("backing-filename", out _))
                throw new InvalidDataException($"Existing base disk is not an independent {diskGiB} GiB qcow2 image.");
        }
        if (!File.Exists(activeDisk))
            await NativeToolRunner.RunCheckedAsync(qemuImg, ["create", "-f", "qcow2", "-F", "qcow2", "-b", baseDisk, activeDisk], cancellation: cancellation).ConfigureAwait(false);
        using (var activeInfo = JsonDocument.Parse(await NativeToolRunner.RunCheckedAsync(qemuImg, ["info", "--output=json", activeDisk], cancellation: cancellation).ConfigureAwait(false))) {
            var root = activeInfo.RootElement;
            if (root.GetProperty("format").GetString() != "qcow2" ||
                !root.TryGetProperty("full-backing-filename", out var bf) ||
                !string.Equals(Path.GetFullPath(bf.GetString() ?? ""), baseDisk, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Existing current disk is not an overlay of this base disk.");
        }

        string vars = Path.Combine(vmDirectory, "firmware", "OVMF_VARS.fd");
        Directory.CreateDirectory(Path.GetDirectoryName(vars)!);
        if (!File.Exists(vars)) File.Copy(fwTemplate, vars, overwrite: false);

        string rom = ResolveEngineFirmwareOrRomPath(engineRoot, "rom", ".work/target/dist-vulkan/share/reims-vgpu/ReimsVgpu.rom");
        var cfg = new JsonObject {
            ["schema"] = 1,
            ["cpus"] = 8,
            ["memory_mib"] = 8192,
            ["resolution"] = "1920x1080",
            ["disk_gib"] = diskGiB,
            ["cpu"] = "Haswell-noTSX,stepping=3,vendor=GenuineIntel,+ssse3,+sse4.2,+popcnt,+avx,+avx2,+aes,+xsave,+xsaveopt,-arat,enforce",
            ["mac"] = GenerateRandomMac(),
            ["qmp_port"] = qmpPort,
            ["ssh_port"] = sshPort,
            ["iso"] = fullIso,
            ["media_kind"] = mediaKind,
            ["base_disk"] = baseDisk,
            ["active_disk"] = activeDisk,
            ["opencore"] = ocImage,
            ["firmware_code"] = fwCode,
            ["firmware_vars"] = vars,
            ["rom"] = rom
        };
        AtomicFileStore.WriteJsonAtomic(configPath, cfg, engineRoot, vmDirectory);
    }

    private static async Task PrepareToolsCoreAsync(string engineRoot, string toolsRoot, string vmDirectory, CancellationToken cancellation)
    {
        AtomicFileStore.AssertVmStopped(vmDirectory);
        string toolsDir = Path.Combine(vmDirectory, "tools", "root");
        string keyPath = Path.Combine(vmDirectory, "state", "ssh", "id_ed25519");
        Directory.CreateDirectory(toolsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

        if (!File.Exists(keyPath)) {
            string sshKeygen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
            await NativeToolRunner.RunCheckedAsync(sshKeygen, ["-q", "-t", "ed25519", "-N", "", "-C", "reims-vm-control", "-f", keyPath], cancellation: cancellation).ConfigureAwait(false);
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            string icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
            await NativeToolRunner.RunCheckedAsync(icacls, [keyPath, "/inheritance:r", "/grant:r", $"*{sid}:(R)"], cancellation: cancellation).ConfigureAwait(false);
        }

        File.Copy(keyPath + ".pub", Path.Combine(toolsDir, "authorized_key"), overwrite: true);
        File.Copy(NativeStatusService.ResolveScriptAssetPath("guest-setup.command"), Path.Combine(toolsDir, "setup.command"), overwrite: true);

        string image = Path.Combine(vmDirectory, "tools", "ReimsTools.img");
        string partial = image + ".partial";
        if (File.Exists(partial)) File.Delete(partial);
        string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");
        try {
            await NativeToolRunner.RunCheckedAsync(qemuImg, ["convert", "-f", "vvfat", "-O", "raw", "fat:" + toolsDir.Replace('\\', '/'), partial], cancellation: cancellation).ConfigureAwait(false);
            PatchFat16VolumeLabel(partial, "REIMSTOOLS "u8);
            File.Move(partial, image, overwrite: true);
        } finally {
            if (File.Exists(partial)) try { File.Delete(partial); } catch { }
        }
    }

    internal static void PatchFat16VolumeLabel(string imagePath, ReadOnlySpan<byte> label11)
    {
        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        byte[] sector = new byte[512];
        fs.ReadExactly(sector);
        uint lbaStart = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(454, 4));
        long partStart = (long)lbaStart * 512L;
        fs.Seek(partStart, SeekOrigin.Begin);
        fs.ReadExactly(sector);
        if (!Encoding.ASCII.GetString(sector, 54, 3).Equals("FAT", StringComparison.Ordinal))
            throw new InvalidDataException("Expected FAT12/16 tools volume.");

        fs.Seek(partStart + 43, SeekOrigin.Begin);
        fs.Write(label11);

        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(14, 2));
        byte numFats = sector[16];
        ushort rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(17, 2));
        ushort fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(22, 2));
        long rootStart = partStart + (long)(reserved + numFats * fatSectors) * 512L;
        byte[] entry = new byte[32];
        for (int i = 0; i < rootEntries; i++) {
            long offset = rootStart + i * 32L;
            fs.Seek(offset, SeekOrigin.Begin);
            fs.ReadExactly(entry);
            if ((entry[11] & 8) != 0 && entry[11] != 15) {
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(label11);
                return;
            }
        }
        throw new InvalidDataException("FAT root volume label missing.");
    }

    private static async Task BuildAndVerifyOpenCoreImageAsync(string qemuImg, string efiRoot, string destinationImage, CancellationToken cancellation)
    {
        string partial = destinationImage + ".partial";
        if (File.Exists(partial)) File.Delete(partial);
        try {
            await NativeToolRunner.RunCheckedAsync(qemuImg, ["convert", "-f", "vvfat", "-O", "raw", "fat:" + efiRoot.Replace('\\', '/'), partial], cancellation: cancellation).ConfigureAwait(false);
            using (var stream = File.OpenRead(partial)) {
                stream.Seek(510, SeekOrigin.Begin);
                if (stream.ReadByte() != 0x55 || stream.ReadByte() != 0xAA) throw new InvalidDataException("vvfat did not produce an MBR.");
                stream.Seek(450, SeekOrigin.Begin);
                int partType = stream.ReadByte();
                if (partType is not (0x06 or 0x0E)) throw new InvalidDataException("Expected vvfat FAT16 partition type.");
            }
            File.Move(partial, destinationImage, overwrite: true);
        } finally {
            if (File.Exists(partial)) try { File.Delete(partial); } catch { }
        }
    }

    private static (int Qmp, int Ssh) AllocateNewVmPorts(string engineRoot, string vmDirectory)
    {
        var reserved = new HashSet<int>();
        var cfgFiles = new List<string> { Path.Combine(engineRoot, "vm", "state", "config.json") };
        string settingsPath = Path.Combine(ManagerSettings.DataDirectory, "settings.json");
        var vmDescriptors = new List<string>();
        if (File.Exists(settingsPath)) {
            if (AtomicFileStore.ReadJsonNode(settingsPath)["VirtualMachines"] is JsonArray arr) {
                foreach (var n in arr) if (n?.GetValue<string>() is { Length: > 0 } s) vmDescriptors.Add(s);
            }
        }
        string legacyLib = Path.Combine(engineRoot, ".work", "manager-library.json");
        if (File.Exists(legacyLib)) {
            if (AtomicFileStore.ReadJsonNode(legacyLib) is JsonArray arr) {
                foreach (var n in arr) if (n?.GetValue<string>() is { Length: > 0 } s) vmDescriptors.Add(s);
            }
        }
        foreach (string desc in vmDescriptors.Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (!File.Exists(desc)) continue;
            var obj = AtomicFileStore.ReadJsonNode(desc).AsObject();
            string dirRel = obj["directory"]?.GetValue<string>() ?? "";
            if (obj["schema"]?.GetValue<int>() != 1 || string.IsNullOrEmpty(dirRel))
                throw new InvalidDataException($"登録 VM の形式を確認できません: {desc}");
            string dir = Path.GetFullPath(dirRel, Path.GetDirectoryName(Path.GetFullPath(desc))!);
            cfgFiles.Add(Path.Combine(dir, "state", "config.json"));
        }
        foreach (string f in cfgFiles) {
            if (!File.Exists(f)) continue;
            var c = AtomicFileStore.ReadJsonNode(f).AsObject();
            foreach (string key in new[] { "qmp_port", "ssh_port" }) {
                int port = c[key]?.GetValue<int>() ?? 0;
                if (port is < 1024 or > 65535) throw new InvalidDataException($"登録 VM のポートが不正です: {f}");
                reserved.Add(port);
            }
        }
        var found = new List<int>(2);
        for (int p = 4444; p < 65000 && found.Count < 2; p++) {
            if (reserved.Contains(p)) continue;
            TcpListener? listener = null;
            try {
                listener = new TcpListener(System.Net.IPAddress.Loopback, p);
                listener.Start();
                found.Add(p);
            } catch (SocketException) {
            } finally {
                listener?.Stop();
            }
        }
        if (found.Count != 2) throw new InvalidOperationException("VM の接続ポートを確保できません。");
        return (found[0], found[1]);
    }

    private static (string Code, string Vars, string Source) ResolveFirmwarePair(string engineRoot, string vmDirectory, JsonObject? recorded, string firmwareDirectory)
    {
        if (recorded is not null) {
            string code = AtomicFileStore.AssertArtifactPath(recorded["firmware_code"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
            string vars = AtomicFileStore.AssertArtifactPath(recorded["firmware_vars_template"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
            string codeSha = recorded["code_sha256"]?.GetValue<string>() ?? "";
            string varsSha = recorded["vars_sha256"]?.GetValue<string>() ?? "";
            if (!File.Exists(code) || !File.Exists(vars) ||
                !string.Equals(NativeToolRunner.ComputeSha256Upper(code), codeSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(NativeToolRunner.ComputeSha256Upper(vars), varsSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The recorded OVMF pair is missing or changed.");
            string src = recorded["firmware_source"]?.GetValue<string>() ?? Path.GetDirectoryName(code)!;
            return (code, vars, src);
        }
        string codeFile = Path.Combine(firmwareDirectory, "edk2-x86_64-code.fd");
        string varsFile = Path.Combine(firmwareDirectory, "edk2-i386-vars.fd");
        if (!File.Exists(codeFile) || !File.Exists(varsFile))
            throw new FileNotFoundException("Incomplete OVMF pair.", codeFile);
        return (codeFile, varsFile, firmwareDirectory);
    }

    private static string ResolveDefaultFirmwareDir(string engineRoot, JsonObject? recorded)
    {
        if (recorded is not null) return Path.GetDirectoryName(recorded["firmware_code"]!.GetValue<string>())!;
        if (NativeToolRunner.IsPortableEngine(engineRoot)) return ResolveEngineFirmwareOrRomPath(engineRoot, "firmware", "");
        string sourceDir = Path.Combine(engineRoot, "build", "qemu", "pc-bios");
        if (File.Exists(Path.Combine(sourceDir, "edk2-x86_64-code.fd"))) return sourceDir;
        throw new FileNotFoundException("Select a firmware directory containing matched OVMF files.");
    }

    private static string ResolveEngineFirmwareOrRomPath(string engineRoot, string key, string sourceFallback)
    {
        string manifestPath = Path.Combine(engineRoot, "reims-engine.json");
        if (File.Exists(manifestPath)) {
            var doc = AtomicFileStore.ReadJsonNode(manifestPath, 4 * 1024 * 1024).AsObject();
            string rel = doc["paths"]?[key]?.GetValue<string>() ?? throw new InvalidDataException($"Missing engine path: {key}");
            return NativeStatusService.JoinPackagePath(engineRoot, rel);
        }
        return Path.Combine(engineRoot, sourceFallback);
    }

    private static bool IsGuestBridgeCompatible(JsonObject pin, string engineId)
    {
        string? required = pin["required_engine_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(required)) return false;
        if (pin["compatible_engine_ids"] is JsonArray arr && arr.Count > 0)
            return arr.Any(x => x?.GetValue<string>() == engineId);
        return engineId == required;
    }

    private static string BackupOpenCorePlist(string vmDirectory, string source, string reason)
    {
        string dir = Path.Combine(vmDirectory, "backups", "opencore");
        Directory.CreateDirectory(dir);
        string backup = Path.Combine(dir, $"before-{reason}-{Guid.NewGuid():N}.plist");
        File.Copy(source, backup, overwrite: false);
        return backup;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        foreach (string sub in Directory.EnumerateDirectories(sourceDir))
            CopyDirectoryRecursive(sub, Path.Combine(destinationDir, Path.GetFileName(sub)));
    }

    private static string GenerateRandomMac()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(3);
        return $"52:54:00:{bytes[0]:x2}:{bytes[1]:x2}:{bytes[2]:x2}";
    }
}
