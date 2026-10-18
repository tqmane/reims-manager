using System.Diagnostics;
using System.IO;

namespace ReimsManager;

internal static class PreferencesChecks
{
    internal static async Task Run(string folder)
    {
        string profile = Path.Combine(folder, "restart-preferences");
        Directory.CreateDirectory(profile);
        await Child(profile, "write");
        await Child(profile, "read");

        using var stale = Start(profile, "stale");
        await WaitFor(Path.Combine(profile, "ready"));
        try {
            await Child(profile, "update");
        } finally { File.WriteAllText(Path.Combine(profile, "release"), "continue"); }
        await Complete(stale, profile);
        await Child(profile, "merged");
        await Child(profile, "recover");
        await Child(profile, "recovered");
    }

    internal static async Task Probe(string profile, string mode)
    {
        Environment.SetEnvironmentVariable("REIMS_MANAGER_HOME", profile);
        var settings = ManagerSettings.Load(out var warning);
        string Vm(string name) => Path.Combine(profile, "日本語 VM", name + ".reimsvm");
        string Engine(string name) => Path.Combine(profile, name);
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        switch (mode) {
            case "write":
                settings.EngineRoot = Engine("engine-one");
                settings.QemuExecutable = Path.Combine(settings.EngineRoot, "bin", "qemu-system-x86_64.exe");
                settings.VirtualMachines = [Vm("first"), Vm("disconnected")]; settings.SelectedVm = Vm("disconnected");
                settings.Save();
                break;
            case "read":
                Require(warning is null && settings.EngineRoot == Engine("engine-one") && settings.SelectedVm == Vm("disconnected") && settings.VirtualMachines.Count == 2,
                    "A new process lost engine, selected or disconnected VM paths");
                var missing = VmLibrary.Registered(Vm("disconnected"));
                Require(!missing.IsAvailable && missing.File == Vm("disconnected") && missing.Name == "disconnected", "Unavailable VM registration disappeared");
                string descriptor = Vm("available"); Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!);
                File.WriteAllText(descriptor, "{\"schema\":1,\"name\":\"Available\",\"directory\":\".\"}");
                Require(VmLibrary.Registered(descriptor).IsAvailable, "A reconnected descriptor could not reload");
                break;
            case "stale":
                File.WriteAllText(Path.Combine(profile, "ready"), "loaded");
                await WaitFor(Path.Combine(profile, "release"));
                settings.VirtualMachines.Add(Vm("stale-instance")); settings.Save();
                Require(settings.EngineRoot == Engine("engine-one") && settings.SelectedVm == Vm("disconnected"), "An unrelated save switched the active window's engine or selection");
                settings.Save();
                break;
            case "update":
                settings.EngineRoot = Engine("engine-two");
                settings.QemuExecutable = Path.Combine(settings.EngineRoot, "bin", "qemu-system-x86_64.exe");
                settings.VirtualMachines.Add(Vm("second-instance")); settings.SelectedVm = Vm("second-instance"); settings.Save();
                break;
            case "merged":
                Require(warning is null && settings.EngineRoot == Engine("engine-two") && settings.QemuExecutable == Path.Combine(Engine("engine-two"), "bin", "qemu-system-x86_64.exe") &&
                    settings.SelectedVm == Vm("second-instance") && settings.VirtualMachines.Count == 4 && settings.VirtualMachines.Contains(Vm("stale-instance")),
                    "A stale manager process overwrote another process's engine or registrations");
                settings.Save();
                break;
            case "recover":
                File.WriteAllText(ManagerSettings.SettingsFile, "{interrupted-json");
                settings = ManagerSettings.Load(out warning);
                Require(warning is not null && settings.VirtualMachines.Count == 4, "The last good settings backup was not restored");
                settings.Save();
                Require(Directory.GetFiles(profile, "*.unreadable-*.json").Any(x => File.ReadAllText(x) == "{interrupted-json"), "Corrupt settings were not preserved");
                break;
            case "recovered":
                Require(warning is null && settings.VirtualMachines.Count == 4 && settings.EngineRoot == Engine("engine-two"), "Recovery did not survive a new process");
                string imported = Path.Combine(profile, "import.json");
                File.WriteAllText(imported, "{\"EngineRoot\":\"other-engine\",\"VirtualMachines\":[\"imported.reimsvm\"]}");
                settings.Import(imported);
                Require(settings.EngineRoot == Engine("engine-two") && settings.VirtualMachines.Count == 5, "Import lost existing engine or VM paths");
                break;
            default: throw new ArgumentException("Unknown preferences check");
        }
    }

    private static Process Start(string profile, string mode)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = profile };
        foreach (string arg in new[] { "--preferences-probe", profile, mode }) info.ArgumentList.Add(arg);
        // The child must select its profile explicitly, never inherit a test process's default.
        info.Environment.Remove("REIMS_MANAGER_HOME");
        return Process.Start(info) ?? throw new IOException("Preferences check did not start");
    }

    private static async Task Child(string profile, string mode)
    {
        using var child = Start(profile, mode);
        await Complete(child, profile);
    }

    private static async Task Complete(Process child, string profile)
    {
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        if (child.ExitCode != 0) throw new Exception("Preferences process check failed: " + File.ReadAllText(Path.Combine(profile, "probe-error.txt")));
    }

    private static async Task WaitFor(string file)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(file)) await Task.Delay(30, timeout.Token);
    }
}
