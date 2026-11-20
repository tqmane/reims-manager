using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal static class SelfChecks
{
    internal static async Task Run(string folder)
    {
        await SshConfigChecks.Run(folder);
        CheckPolicy();
        OnboardingChecks.Run(Path.Combine(folder, "onboarding-checks"));
        await CheckBackend();
        await BackendPipeChecks.Run(Path.Combine(folder, "backend-pipe-checks"));
        await BackendEncodingChecks.Run(Path.Combine(folder, "backend-encoding-checks"));
        await ManagerControlChecks.Run(Path.Combine(folder, "control-target-checks"));
        CheckSafeCreation(folder);
        CheckUsbDriverResult(folder);
        await CheckQmp(false);
        await CheckQmp(true);
        string runtimeDirectory=Path.Combine(folder,"identity-check","state");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(Path.Combine(runtimeDirectory,"config.json"),"{}");
        using var self=Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(runtimeDirectory,"runtime.json"),JsonSerializer.Serialize(new {
            pid=self.Id,start_ticks=self.StartTime.ToUniversalTime().Ticks+1,executable=Environment.ProcessPath,qmp_port=4444
        }));
        var stale = await LiveVmStatus.Read(Path.GetDirectoryName(runtimeDirectory)!);
        if (stale.State != "stopped" || stale.Runtime is not null)
            throw new Exception("A reused PID did not recover to a stopped VM");
        File.WriteAllText(Path.Combine(runtimeDirectory,"runtime.json"),JsonSerializer.Serialize(new {
            pid=self.Id,start_ticks=self.StartTime.ToUniversalTime().Ticks,executable="unrelated.exe",qmp_port=4444
        }));
        stale = await LiveVmStatus.Read(Path.GetDirectoryName(runtimeDirectory)!);
        if (stale.State != "stopped" || stale.Runtime is not null || self.HasExited)
            throw new Exception("An unrelated process was contacted or affected by stale VM metadata");

        string? previous=Environment.GetEnvironmentVariable("REIMS_MANAGER_HOME");
        Environment.SetEnvironmentVariable("REIMS_MANAGER_HOME",Path.Combine(folder,"settings-check"));
        try {
            var settings=new ManagerSettings { EngineRoot=Path.Combine(folder,"日本語 engine"),VirtualMachines=["example.reimsvm"] };
            settings.Save();
            var saved=ManagerSettings.Load(out var warning);
            if (warning is not null || saved.EngineRoot!=settings.EngineRoot || saved.VirtualMachines.Single()!="example.reimsvm")
                throw new Exception("Settings roundtrip failed");
            string corrupt = "{original-unreadable-preferences";
            File.WriteAllText(Path.Combine(ManagerSettings.DataDirectory, "settings.json"), corrupt);
            var recovered = ManagerSettings.Load(out warning);
            if (warning is null) throw new Exception("Corrupt preferences were silently ignored");
            recovered.Save();
            string backup = Directory.GetFiles(ManagerSettings.DataDirectory, "*.unreadable-*.json").Single();
            if (File.ReadAllText(backup) != corrupt) throw new Exception("Original corrupt preferences were lost");
        } finally { Environment.SetEnvironmentVariable("REIMS_MANAGER_HOME",previous); }

        string escaped=Path.Combine(folder,"escaped.reimsvm");
        File.WriteAllText(escaped,"{\"schema\":1,\"name\":\"Escaped\",\"directory\":\"../outside\"}");
        try { VmLibrary.Open(escaped); throw new Exception("A VM path outside its package was accepted"); }
        catch (InvalidDataException) { }

        await CheckNativeServicesAsync(folder);
    }

    private static async Task CheckNativeServicesAsync(string folder)
    {
        string rawPsError = "\x1B[31;1mException: D:\\Reims\\Manager\\scripts\\iphone-usb.ps1:176\r\nLine |\r\n 176 |     if($failures.Count){throw ($failures -join \"`n\")}\r\n     |     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\r\n     | 前回の iPhone の復元が未完了です。\x1B[0m";
        if (Services.BackendErrorFormatter.CleanMessage(rawPsError) != "前回の iPhone の復元が未完了です。")
            throw new Exception("BackendErrorFormatter failed to strip PowerShell error frame.");
        if (!Services.BackendErrorFormatter.CleanMessage("Current driver is foreign to this receipt; refusing to overwrite it.").Contains("リセット", StringComparison.Ordinal))
            throw new Exception("BackendErrorFormatter did not translate foreign driver receipt diagnostic.");

        string nativeVm = Path.Combine(folder, "native-service-vm");
        string stateDir = Path.Combine(nativeVm, "state");
        Directory.CreateDirectory(stateDir);
        string plistPath = Path.Combine(nativeVm, "config.plist");
        byte[] romRaw = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
        File.WriteAllText(plistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
              <key>PlatformInfo</key>
              <dict>
                <key>Generic</key>
                <dict>
                  <key>SystemProductName</key><string>MacPro7,1</string>
                  <key>SystemSerialNumber</key><string>F5K123456789</string>
                  <key>MLB</key><string>F5K12345678901234</string>
                  <key>SystemUUID</key><string>11111111-2222-3333-4444-555555555555</string>
                  <key>ROM</key><data>{Convert.ToBase64String(romRaw)}</data>
                </dict>
              </dict>
            </dict>
            </plist>
            """, Encoding.UTF8);
        File.WriteAllText(Path.Combine(stateDir, "opencore.json"), JsonSerializer.Serialize(new { config = plistPath }));
        var identity = Services.NativeOpenCore.ReadIdentity(folder, nativeVm);
        if (identity.Model != "MacPro7,1" || identity.Serial != "F5K123456789" || identity.Mlb != "F5K12345678901234" ||
            identity.Uuid != "11111111-2222-3333-4444-555555555555" || identity.Rom != "112233445566")
            throw new Exception("NativeOpenCore XML identity parsing mismatch.");

        string escapedOc = Path.Combine(folder, "native-outside.plist");
        File.WriteAllText(escapedOc, "<plist/>");
        File.WriteAllText(Path.Combine(stateDir, "opencore.json"), JsonSerializer.Serialize(new { config = escapedOc }));
        try { Services.NativeOpenCore.ReadIdentity(Path.Combine(folder, "isolated-root"), nativeVm); throw new Exception("NativeOpenCore accepted an escaping plist path."); }
        catch (InvalidDataException) { }

        File.WriteAllText(Path.Combine(stateDir, "config.json"), JsonSerializer.Serialize(new {
            schema = 1, cpus = 4, memory_mib = 8192, disk_gib = 128, active_disk = "disk.qcow2", resolution = "1920x1080",
            network = new { mode = "nat", adapter = "", guest_host = "" }, audio = "none", microphone = false, usb_backend = "winusb"
        }));
        File.WriteAllText(Path.Combine(stateDir, "host-cursor.json"), "{\"schema\":1,\"host_cursor_hidden\":true}");
        using var cur = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(stateDir, "runtime.json"), JsonSerializer.Serialize(new {
            pid = cur.Id, start_ticks = cur.StartTime.ToUniversalTime().Ticks + 100, executable = Environment.ProcessPath, qmp_port = 4445
        }));
        string statusJson = await Services.NativeStatusService.GetStatusJsonAsync(folder, nativeVm);
        using (var doc = JsonDocument.Parse(statusJson)) {
            var root = doc.RootElement;
            if (root.GetProperty("state").GetString() != "stopped" ||
                !root.GetProperty("host_cursor_hidden").GetBoolean() ||
                root.GetProperty("cpus").GetInt32() != 4)
                throw new Exception("NativeStatusService returned unexpected fixture status.");
        }

        string prereqJson = await Services.NativeStatusService.GetPrerequisitesJsonAsync(folder, folder, 4, 8192, CancellationToken.None);
        using (var prereqDoc = JsonDocument.Parse(prereqJson)) {
            var checkIds = prereqDoc.RootElement.GetProperty("checks").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString())
                .ToArray();
            if (!checkIds.Contains("whpx") || !checkIds.Contains("vulkan"))
                throw new Exception($"GetPrerequisitesJsonAsync omitted expected checks: {string.Join(", ", checkIds)}");
        }

        var netInv = NetworkInventory.Parse(Services.NativeHostNetwork.GetNetworkInventoryJson());
        if (netInv is null) throw new Exception("NativeHostNetwork returned null inventory.");

        if (!Services.NativePnpUsb.IsAppleCandidateInstance(@"USB\VID_05AC&PID_12A8\0000811000123456") ||
            Services.NativePnpUsb.IsAppleCandidateInstance(@"USB\VID_05AC&PID_9999\1234") ||
            Services.NativePnpUsb.IsAppleCandidateInstance(@"USB\VID_05AC&PID_12A8\..\bad"))
            throw new Exception("NativePnpUsb Apple candidate validation failed.");

        var names = Services.NativePnpUsb.BuildProductNames([
            new(@"USB\VID_0781&PID_55A9\12345", "USB", "USB Composite Device", "SanDisk 3.2Gen1 _SN:ABC"),
            new(@"USB\VID_0781&PID_55A9&MI_00\12345", "WPD", "MTP USB Device", "Ignored"),
            new(@"USB\VID_04E8&PID_6860\R5GL254V74T", "USB", "SAMSUNG Mobile USB Composite Device", "SAMSUNG Mobile USB Composite Device"),
            new(@"USB\VID_04E8&PID_6860&ADB\7&1CCB6F3C&0&0003", "AndroidUsbDeviceClass", "SAMSUNG Android ADB Interface", ""),
            new(@"USB\VID_04E8&PID_6860&MS_COMP_MTP&SAMSUNG_ANDROID\7&1CCB6F3C&0&0000", "WPD", "Galaxy S26 Ultra", "")
        ]);
        if (!names.TryGetValue("0781:55a9", out string? prodName) || prodName != "SanDisk 3.2Gen1")
            throw new Exception("NativePnpUsb product name normalization failed.");
        if (!names.TryGetValue("04e8:6860", out string? galaxyName) || galaxyName != "Galaxy S26 Ultra")
            throw new Exception("NativePnpUsb non-MI interface product name resolution failed.");

        // Test FAT16 label patcher on synthetic MBR + FAT16 sector image
        string fatImg = Path.Combine(folder, "tools-fat.img");
        byte[] rawImg = new byte[4096];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(rawImg.AsSpan(454, 4), 1); // LBA 1
        Encoding.ASCII.GetBytes("FAT16   ").CopyTo(rawImg, 512 + 54);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(rawImg.AsSpan(512 + 14, 2), 1); // reserved = 1
        rawImg[512 + 16] = 1; // 1 FAT
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(rawImg.AsSpan(512 + 17, 2), 16); // root entries
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(rawImg.AsSpan(512 + 22, 2), 1); // fat sectors = 1 -> root at LBA 3 = 1536
        Encoding.ASCII.GetBytes("QEMU VVFAT ").CopyTo(rawImg, 512 + 43);
        Encoding.ASCII.GetBytes("QEMU VVFAT ").CopyTo(rawImg, 1536);
        rawImg[1536 + 11] = 0x08; // volume label attribute
        File.WriteAllBytes(fatImg, rawImg);
        Services.NativeOpenCoreService.PatchFat16VolumeLabel(fatImg, "REIMSTOOLS "u8);
        byte[] patched = File.ReadAllBytes(fatImg);
        if (Encoding.ASCII.GetString(patched, 512 + 43, 11) != "REIMSTOOLS " ||
            Encoding.ASCII.GetString(patched, 1536, 11) != "REIMSTOOLS ")
            throw new Exception("PatchFat16VolumeLabel did not update both VBR and root label.");

        // Test safe zip extraction against zip-slip and symlinks
        string slipZip = Path.Combine(folder, "slip.zip");
        using (var zs = System.IO.Compression.ZipFile.Open(slipZip, System.IO.Compression.ZipArchiveMode.Create)) {
            var e = zs.CreateEntry("../escape.txt");
            using var w = new StreamWriter(e.Open()); w.Write("bad");
        }
        try { Services.NativeToolRunner.ExpandZipSafely(slipZip, Path.Combine(folder, "slip-out")); throw new Exception("Zip-slip entry accepted."); }
        catch (InvalidDataException) { }

        if (!Services.NativeVmControlService.CheckHostHardwareCapacity(1, 4096) ||
            Services.NativeVmControlService.CheckHostHardwareCapacity(1, 999_999_999))
            throw new Exception("CheckHostHardwareCapacity failed hardware bounds check.");

        string diagResult = await Services.NativeStorageService.CollectLogsAsync(folder, "", screenshot: true);
        if (!diagResult.Contains("Local diagnostics:", StringComparison.Ordinal))
            throw new Exception("Native CollectLogsAsync failed without VM.");
    }

    private static void CheckUsbDriverResult(string folder)
    {
        string receipt = Path.Combine(folder, "usb-install-result.json");
        void Reject(string json)
        {
            File.WriteAllText(receipt, json);
            try { UsbDriverInstallResult.Read(receipt); throw new Exception("An unconfirmed USB driver result was accepted."); }
            catch (InvalidDataException) { }
        }
        try {
            foreach (bool restart in new[] { false, true }) {
                string json = restart
                    ? "{\"schema\":1,\"installed\":true,\"restart_required\":true,\"exit_code\":3010}"
                    : "{\"schema\":1,\"installed\":true,\"restart_required\":false,\"exit_code\":0}";
                File.WriteAllText(receipt, json);
                if (UsbDriverInstallResult.Read(receipt).RestartRequired != restart || UsbDriverInstallResult.Parse(json).RestartRequired != restart)
                    throw new Exception("The elevated USB installer reboot requirement was lost.");
            }
            foreach (string json in new[] {
                "", "{}", "not JSON", "[]", new string('x', 4097),
                "{\"schema\":1,\"installed\":true,\"restart_required\":false,\"exit_code\":3010}",
                "{\"schema\":1,\"installed\":true,\"restart_required\":true,\"exit_code\":0}",
                "{\"schema\":1,\"installed\":false,\"restart_required\":false,\"exit_code\":0}",
                "{\"schema\":1,\"installed\":true,\"restart_required\":false,\"exit_code\":1603}",
                "{\"schema\":2,\"installed\":true,\"restart_required\":false,\"exit_code\":0}",
                "{\"schema\":1,\"installed\":true,\"restart_required\":\"false\",\"exit_code\":0}"
            }) Reject(json);
            File.Delete(receipt);
            try { UsbDriverInstallResult.Read(receipt); throw new Exception("A missing USB driver result was accepted."); }
            catch (InvalidDataException) { }
        } finally { File.Delete(receipt); }
    }

    private static void CheckPolicy()
    {
        foreach (string state in new[] { "unknown", "future-state", "paused", "prelaunch", "suspended", "shutdown", "running", "stopped", "unprepared" }) {
            foreach (string action in new[] { "Start", "ForceExit", "SetHardware", "SnapshotRestore", "Setup", "ListUsb", "DetachUsb", "AttachUsb", "Clipboard", "EnableCursorAid", "DisableCursorAid", "MoveCursorToMainDisplay", "MoveCursorToExtendedDisplay", "HideHostCursor", "ShowHostCursor", "LightenExtendedDisplay", "RestoreExtendedDisplay" }) {
                if (VmActionPolicy.CanRun(action, state, true, false) || VmActionPolicy.CanRun(action, state, false, true))
                    throw new Exception("An operation bypassed its engine/VM prerequisite");
            }
            if (VmActionPolicy.CanRun("Clipboard", state, true, true) != (state is "running" or "unknown"))
                throw new Exception("Independent clipboard SSH eligibility failed for " + state);
            foreach (string action in new[] { "ListUsb", "DetachUsb", "ResetIphoneUsb", "AttachUsb" }) {
                bool expected = action switch {
                    "ListUsb" => state is "running" or "stopped" or "shutdown" or "paused" or "prelaunch" or "suspended",
                    "DetachUsb" or "ResetIphoneUsb" => state is "running" or "stopped",
                    _ => state == "running"
                };
                if (VmActionPolicy.CanRun(action, state, true, true) != expected)
                    throw new Exception("USB listing/removal/attachment policy failed: " + action + " in " + state);
            }
            if (VmActionPolicy.CanRun("InstallUsbDk", state, true, true) != (state is "stopped" or "unprepared") ||
                !VmActionPolicy.CanRun("InstallUsbDk", state, false, true) || VmActionPolicy.CanRun("InstallUsbDk", state, true, false))
                throw new Exception("USB driver installation bypassed the stopped VM policy or required a VM.");
            foreach (string action in new[] { "SetHardware", "SnapshotRestore", "SetIdentity", "ExpandDisk", "ApplyBootProfile", "EnableGuestBridge", "DisableGuestBridge", "RemoveBridge", "InstallTap", "PrepareWifi", "DisableWifi" })
                if (VmActionPolicy.CanRun(action, state, true, true) != (state == "stopped")) throw new Exception("An offline operation is enabled for " + state);
            foreach (string action in new[] { "EnableCursorAid", "DisableCursorAid", "MoveCursorToMainDisplay", "MoveCursorToExtendedDisplay", "HideHostCursor", "LightenExtendedDisplay", "RestoreExtendedDisplay" })
                if (VmActionPolicy.CanRun(action, state, true, true) != (state == "running")) throw new Exception("Cursor compatibility bypassed its running VM policy.");
            if (VmActionPolicy.CanRun("ShowHostCursor",state,true,true) != (state is "running" or "paused" or "stopped" or "shutdown" or "prelaunch" or "suspended"))
                throw new Exception("Host cursor recovery is unavailable or bypasses the known-state policy.");
            foreach (string action in new[] { "EnableAppleAccount", "DisableAppleAccount" })
                if (VmActionPolicy.CanRun(action, state, true, true)) throw new Exception("A retired version-specific account patch remains a UI action.");
        }
        if (VmActionPolicy.CanRun("PowerOff", "paused", true, true) || VmActionPolicy.CanRun("Restart", "paused", true, true) ||
            VmActionPolicy.CanRun("ForceExit", "unknown", true, true) || !VmActionPolicy.CanRun("Resume", "paused", true, true))
            throw new Exception("Pause and unknown-state policy failed");
        if (GuestClipboard.ShouldPush("unknown", "old local", null) || GuestClipboard.ShouldPush("unknown", null, null) ||
            !GuestClipboard.ShouldPush("running", "old local", null) || GuestClipboard.ShouldPush("unknown", "shared", "shared") ||
            !GuestClipboard.ShouldPush("unknown", "new local", "guest baseline") ||
            !GuestClipboard.ShouldPush("running", "new local", "guest baseline") || GuestClipboard.ShouldPush("paused", "new local", "guest baseline"))
            throw new Exception("Cold unknown clipboard must pull first and preserve a newer local copy against the guest baseline.");
        if (!VmActionPolicy.StartArguments(true).SequenceEqual(new[] { "-WithInstaller" }) || VmActionPolicy.StartArguments(false).Length != 0)
            throw new Exception("Installer preference is not applied consistently");
        if (!ManagerValidation.Integer(" 8196 ", 4096, 131072, 4, out _) ||
            ManagerValidation.Integer("8193", 4096, 131072, 4, out _) || ManagerValidation.Integer("1e4", 4096, 131072, 4, out _) ||
            ManagerValidation.Integer("-1", 1, 64, 1, out _) || ManagerValidation.Integer("999999999999", 1, 64, 1, out _))
            throw new Exception("Numeric validation failed");
        if (!ManagerValidation.SnapshotName("before-update_1.0") || ManagerValidation.SnapshotName("../escape") ||
            ManagerValidation.SnapshotName("a\n") || ManagerValidation.SnapshotName(new string('a', 65))) throw new Exception("Snapshot name validation failed");
    }

    private static void CheckSafeCreation(string folder)
    {
        string package = Path.Combine(folder, "create-fixtures"); Directory.CreateDirectory(package);
        string descriptor = Path.Combine(package, "occupied.reimsvm"), data = Path.Combine(package, "occupied.data");
        Directory.CreateDirectory(data); string sentinel = Path.Combine(data, "keep.txt"); File.WriteAllText(sentinel, "keep");
        try { VmLibrary.Create(descriptor, data); throw new Exception("Occupied VM data was accepted"); } catch (IOException) { }
        if (File.Exists(descriptor) || File.ReadAllText(sentinel) != "keep") throw new Exception("Rejected creation modified VM data");
        string escape = Path.Combine(package, "escape.reimsvm");
        try { VmLibrary.Create(escape, Path.Combine(folder, "outside-data")); throw new Exception("Escaping data path accepted"); } catch (InvalidDataException) { }
        if (File.Exists(escape)) throw new Exception("Invalid descriptor was written before validation");
        string valid = Path.Combine(package, "valid.reimsvm");
        var entry = VmLibrary.Create(valid, Path.Combine(package, "valid.data"));
        string original = File.ReadAllText(valid);
        try { VmLibrary.Create(valid, Path.Combine(package, "other.data")); throw new Exception("An existing descriptor was overwritten"); } catch (IOException) { }
        if (VmLibrary.Open(valid) != entry || File.ReadAllText(valid) != original || Directory.GetFiles(package, "*.tmp").Length != 0)
            throw new Exception("Atomic VM creation failed");
    }

    internal static async Task CheckBackend()
    {
        string pwsh = Path.Combine(AppContext.BaseDirectory, "runtimes/pwsh/pwsh.exe");
        ProcessStartInfo Command(string script) {
            var info = new ProcessStartInfo(pwsh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(arg);
            return info;
        }
        string output = await BackendCommand.Run(Command("[Console]::Out.Write('ok'); [Console]::Error.Write('diagnostic')"), null, true);
        if (output != "ok") throw new Exception("Backend pipe capture failed");
        try { await BackendCommand.Run(Command("[Console]::Error.Write('expected failure'); exit 7"), null, true); throw new Exception("Backend failure was swallowed"); }
        catch (IOException error) when (error.Message.Contains("expected failure")) { }
        try { await BackendCommand.Run(Command("[Console]::Out.Write(('x' * 1100000))"), null, true); throw new Exception("Unbounded backend output accepted"); }
        catch (InvalidDataException) { }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try { await BackendCommand.Run(Command("Start-Sleep -Seconds 30"), null, true, cancellation.Token); throw new Exception("Read-only helper cancellation was ignored"); }
        catch (OperationCanceledException) { }
    }

    private static async Task CheckQmp(bool error)
    {
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        Task server=Task.Run(async ()=> {
            for(int attempt=0;attempt<2;attempt++) {
            using var socket=await listener.AcceptTcpClientAsync();
            using var lease=new Mutex(false,$"Local\\ReimsManager.Qmp.{port}");
            if(lease.WaitOne(0)) { lease.ReleaseMutex();throw new Exception("QMP query did not acquire the shared connection lease"); }
            using var stream=socket.GetStream();
            using var reader=new StreamReader(stream,Encoding.UTF8,false,1024,true);
            using var writer=new StreamWriter(stream,new UTF8Encoding(false),1024,true) {AutoFlush=true};
            await writer.WriteLineAsync("{\"QMP\":{}}");
            foreach (string command in new[]{"qmp_capabilities","query-status"}) {
                using var request=JsonDocument.Parse(await reader.ReadLineAsync() ?? throw new Exception("Missing QMP request"));
                if(request.RootElement.GetProperty("execute").GetString()!=command) throw new Exception("QMP handshake order changed");
                string id=request.RootElement.GetProperty("id").GetString()!;
                await writer.WriteLineAsync("{\"event\":\"RESUME\"}");
                object response=error && command=="query-status"
                    ? new {error=new {desc="Expected rejection"},id}
                    : (object)new {@return=new {status="running"},id};
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            }
        });
        try {
            try {
                string[] results=await Task.WhenAll(LiveVmStatus.Query(port),LiveVmStatus.Query(port));
                if(error || results.Any(result=>result!="running")) throw new Exception("QMP response validation failed");
            } catch (IOException) when(error) { }
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        } finally { listener.Stop(); }
    }
}
