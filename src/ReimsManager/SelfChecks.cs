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
            foreach (string action in new[] { "ListUsb", "DetachUsb", "AttachUsb" }) {
                bool expected = action switch {
                    "ListUsb" => state is "running" or "stopped" or "shutdown" or "paused" or "prelaunch" or "suspended",
                    "DetachUsb" => state is "running" or "stopped",
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
