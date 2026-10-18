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
        await ManagerControlChecks.Run(Path.Combine(folder, "control-target-checks"));
        CheckSafeCreation(folder);
        await CheckQmp(false);
        await CheckQmp(true);
        string runtimeDirectory=Path.Combine(folder,"identity-check","state");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(Path.Combine(runtimeDirectory,"config.json"),"{}");
        using var self=Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(runtimeDirectory,"runtime.json"),JsonSerializer.Serialize(new {
            pid=self.Id,start_ticks=self.StartTime.ToUniversalTime().Ticks+1,executable=Environment.ProcessPath,qmp_port=4444
        }));
        try {
            await LiveVmStatus.Read(Path.GetDirectoryName(runtimeDirectory)!);
            throw new Exception("A reused PID was accepted");
        } catch (InvalidDataException) { }

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

    private static void CheckPolicy()
    {
        foreach (string state in new[] { "unknown", "future-state", "paused", "prelaunch", "suspended", "shutdown", "running", "stopped", "unprepared" }) {
            foreach (string action in new[] { "Start", "ForceExit", "SetHardware", "SnapshotRestore", "Setup", "ListUsb", "DetachUsb", "AttachUsb" }) {
                if (VmActionPolicy.CanRun(action, state, true, false) || VmActionPolicy.CanRun(action, state, false, true))
                    throw new Exception("An operation bypassed its engine/VM prerequisite");
            }
            foreach (string action in new[] { "ListUsb", "DetachUsb", "AttachUsb" }) {
                bool expected = state == "running" || state == "stopped" && action != "AttachUsb";
                if (VmActionPolicy.CanRun(action, state, true, true) != expected)
                    throw new Exception("USB listing/removal/attachment policy failed: " + action + " in " + state);
            }
            foreach (string action in new[] { "SetHardware", "SnapshotRestore", "SetIdentity", "ExpandDisk", "ApplyBootProfile", "EnableGuestBridge", "DisableGuestBridge", "RemoveBridge", "InstallTap" })
                if (VmActionPolicy.CanRun(action, state, true, true) != (state == "stopped")) throw new Exception("An offline operation is enabled for " + state);
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
            using var socket=await listener.AcceptTcpClientAsync();
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
        });
        try {
            try {
                string result=await LiveVmStatus.Query(port);
                if(error || result!="running") throw new Exception("QMP response validation failed");
            } catch (IOException) when(error) { }
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        } finally { listener.Stop(); }
    }
}
