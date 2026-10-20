using System.Diagnostics;
using System.IO;
using System.Text.Json;

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
        await Child(profile, "remove-primary");
        await Child(profile, "missing-primary");
        await Child(profile, "primary-restored");
        await Child(profile, "recover");
        await Child(profile, "recovered");
        await Child(profile, "import-failure");
        await Child(profile, "after-import-failure");

        string firstSave = Path.Combine(folder, "first-save-preferences");
        Directory.CreateDirectory(firstSave);
        await Child(firstSave, "write");
        await Child(firstSave, "remove-primary");
        await Child(firstSave, "first-save-recovery");
        await Child(firstSave, "read");

        string synchronize = Path.Combine(folder, "synchronized-preferences");
        Directory.CreateDirectory(synchronize);
        await Child(synchronize, "write");
        await Child(synchronize, "synchronize");
        await Child(synchronize, "after-synchronize");

        string rejectedPrimary = Path.Combine(folder, "rejected-primary-preferences");
        Directory.CreateDirectory(rejectedPrimary);
        await Child(rejectedPrimary, "write");
        await Child(rejectedPrimary, "blocked-primary");
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
                settings.SetClipboardEnabled(Vm("first"), true);
                settings.SetClipboardEnabled(Vm("disconnected"), false);
                settings.Save();
                break;
            case "read":
                Require(warning is null && settings.EngineRoot == Engine("engine-one") && settings.SelectedVm == Vm("disconnected") && settings.VirtualMachines.Count == 2,
                    "A new process lost engine, selected or disconnected VM paths");
                Require(settings.ClipboardEnabled(Vm("first").ToUpperInvariant()) && !settings.ClipboardEnabled(Vm("disconnected")),
                    "A new process lost per-VM clipboard preferences or their case-insensitive lookup");
                var missing = VmLibrary.Registered(Vm("disconnected"));
                Require(!missing.IsAvailable && missing.File == Vm("disconnected") && missing.Name == "disconnected", "Unavailable VM registration disappeared");
                string descriptor = Vm("available"); Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!);
                File.WriteAllText(descriptor, "{\"schema\":1,\"name\":\"Available\",\"directory\":\".\"}");
                Require(VmLibrary.Registered(descriptor).IsAvailable, "A reconnected descriptor could not reload");
                break;
            case "stale":
                File.WriteAllText(Path.Combine(profile, "ready"), "loaded");
                await WaitFor(Path.Combine(profile, "release"));
                settings.VirtualMachines.Add(Vm("stale-instance"));
                settings.SetClipboardEnabled(Vm("stale-instance"), true); settings.Save();
                Require(settings.EngineRoot == Engine("engine-one") && settings.SelectedVm == Vm("disconnected"), "An unrelated save switched the active window's engine or selection");
                settings.Save();
                break;
            case "update":
                settings.EngineRoot = Engine("engine-two");
                settings.QemuExecutable = Path.Combine(settings.EngineRoot, "bin", "qemu-system-x86_64.exe");
                settings.VirtualMachines.Add(Vm("second-instance")); settings.SelectedVm = Vm("second-instance");
                settings.SetClipboardEnabled(Vm("first"), false);
                settings.SetClipboardEnabled(Vm("second-instance"), true); settings.Save();
                break;
            case "merged":
                Require(warning is null && settings.EngineRoot == Engine("engine-two") && settings.QemuExecutable == Path.Combine(Engine("engine-two"), "bin", "qemu-system-x86_64.exe") &&
                    settings.SelectedVm == Vm("second-instance") && settings.VirtualMachines.Count == 4 && settings.VirtualMachines.Contains(Vm("stale-instance")),
                    "A stale manager process overwrote another process's engine or registrations");
                Require(!settings.ClipboardEnabled(Vm("first")) && settings.ClipboardEnabled(Vm("second-instance")) && settings.ClipboardEnabled(Vm("stale-instance")),
                    "A stale manager process reverted another VM's clipboard preference or an explicit disable");
                settings.Save();
                break;
            case "remove-primary":
                File.Delete(ManagerSettings.SettingsFile);
                break;
            case "missing-primary":
                Require(warning is not null && settings.VirtualMachines.Count == 4 && settings.EngineRoot == Engine("engine-two") &&
                    settings.ClipboardEnabled(Vm("second-instance")) && !settings.ClipboardEnabled(Vm("first")),
                    "Missing primary settings silently reset the profile instead of recovering its backup");
                settings.Save();
                break;
            case "primary-restored":
                Require(warning is null && settings.VirtualMachines.Count == 4 && settings.SelectedVm == Vm("second-instance"),
                    "Recovered missing-primary settings did not survive a process restart");
                break;
            case "first-save-recovery":
                Require(warning is not null && settings.VirtualMachines.Count == 2 && settings.EngineRoot == Engine("engine-one") && settings.ClipboardEnabled(Vm("first")),
                    "The first successful save had no recoverable backup");
                settings.Save();
                break;
            case "recover":
                File.WriteAllText(ManagerSettings.SettingsFile, "{interrupted-json");
                settings = ManagerSettings.Load(out warning);
                Require(warning is not null && settings.VirtualMachines.Count == 4 && settings.ClipboardEnabled(Vm("second-instance")), "The last good settings backup was not restored");
                settings.Save();
                Require(Directory.GetFiles(profile, "*.unreadable-*.json").Any(x => File.ReadAllText(x) == "{interrupted-json"), "Corrupt settings were not preserved");
                break;
            case "recovered":
                Require(warning is null && settings.VirtualMachines.Count == 4 && settings.EngineRoot == Engine("engine-two"), "Recovery did not survive a new process");
                string imported = Path.Combine(profile, "import.json");
                File.WriteAllText(imported, JsonSerializer.Serialize(new ManagerSettings {
                    EngineRoot = "other-engine", VirtualMachines = ["imported.reimsvm"],
                    ClipboardByVm = new(StringComparer.OrdinalIgnoreCase) { [Vm("first")] = true, ["imported.reimsvm"] = true }
                }));
                settings.Import(imported);
                Require(settings.EngineRoot == Engine("engine-two") && settings.VirtualMachines.Count == 5 && !settings.ClipboardEnabled(Vm("first")) && settings.ClipboardEnabled("imported.reimsvm"),
                    "Import lost existing engine, VM paths or explicit clipboard preferences");
                break;
            case "import-failure":
                string rejectedImport = Path.Combine(profile, "rejected-import.json");
                File.WriteAllText(rejectedImport, JsonSerializer.Serialize(new ManagerSettings {
                    EngineRoot = "rejected-engine", SelectedVm = Vm("rejected"), VirtualMachines = [Vm("rejected")],
                    ClipboardByVm = new(StringComparer.OrdinalIgnoreCase) { [Vm("rejected")] = true }
                }));
                string original = JsonSerializer.Serialize(settings);
                bool rejected = false;
                using (var locked = new FileStream(ManagerSettings.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                    try { settings.Import(rejectedImport); }
                    catch (IOException) { rejected = true; }
                }
                Require(rejected && JsonSerializer.Serialize(settings) == original,
                    "An import that failed to save still changed the active window's settings");
                string diskBefore = File.ReadAllText(ManagerSettings.SettingsFile);
                var oversized = ManagerSettings.Load(out _);
                oversized.EngineRoot = new string('x', 1024 * 1024);
                rejected = false;
                try { oversized.Save(); }
                catch (IOException) { rejected = true; }
                Require(rejected && File.ReadAllText(ManagerSettings.SettingsFile) == diskBefore && Directory.GetFiles(profile, "*.tmp").Length == 0,
                    "Saving oversized preferences wrote a profile that cannot be reloaded or left temporary files");
                break;
            case "after-import-failure":
                Require(warning is null && settings.EngineRoot == Engine("engine-two") && settings.VirtualMachines.Count == 5 &&
                    !settings.VirtualMachines.Contains(Vm("rejected")) && !settings.ClipboardEnabled(Vm("rejected")),
                    "A failed import changed persisted settings");
                break;
            case "synchronize":
                var otherWindow = ManagerSettings.Load(out _);
                otherWindow.EngineRoot = Engine("engine-two");
                otherWindow.QemuExecutable = Path.Combine(otherWindow.EngineRoot, "qemu.exe");
                otherWindow.SelectedVm = Vm("first");
                otherWindow.SetClipboardEnabled(Vm("first"), false); otherWindow.Save();
                settings.SynchronizeFrom(ManagerSettings.Load(out _), adoptEngine: false);
                Require(settings.EngineRoot == Engine("engine-one") && settings.SelectedVm == Vm("disconnected") && !settings.ClipboardEnabled(Vm("first")),
                    "Refreshing preferences changed the running engine/active VM or missed the clipboard preference");
                otherWindow.EngineRoot = Engine("engine-three");
                otherWindow.QemuExecutable = Path.Combine(otherWindow.EngineRoot, "qemu.exe");
                otherWindow.SetClipboardEnabled(Vm("first"), true); otherWindow.Save();
                settings.SetClipboardEnabled(Vm("disconnected"), true); settings.Save();
                var synchronized = ManagerSettings.Load(out _);
                Require(synchronized.EngineRoot == Engine("engine-three") && synchronized.SelectedVm == Vm("first") && synchronized.ClipboardEnabled(Vm("first")),
                    "A refresh incorrectly marked external preferences as edits and reverted a newer save");
                settings.SynchronizeFrom(synchronized, adoptEngine: true);
                Require(settings.EngineRoot == Engine("engine-three") && settings.SelectedVm == Vm("disconnected"),
                    "An allowed engine refresh lost the active VM selection");
                otherWindow.EngineRoot = Engine("engine-four");
                otherWindow.QemuExecutable = Path.Combine(otherWindow.EngineRoot, "qemu.exe");
                otherWindow.SetClipboardEnabled(Vm("first"), false); otherWindow.Save();
                settings.SetClipboardEnabled(Vm("disconnected"), false); settings.Save();
                break;
            case "after-synchronize":
                Require(warning is null && settings.EngineRoot == Engine("engine-four") && settings.QemuExecutable == Path.Combine(Engine("engine-four"), "qemu.exe") &&
                    settings.SelectedVm == Vm("first") && !settings.ClipboardEnabled(Vm("first")) && !settings.ClipboardEnabled(Vm("disconnected")),
                    "Saving after engine synchronization reverted another window's newer engine or preferences");
                break;
            case "blocked-primary":
                string blockedImport = Path.Combine(profile, "blocked-import.json");
                File.WriteAllText(blockedImport, JsonSerializer.Serialize(new ManagerSettings {
                    VirtualMachines = [Vm("rejected")], ClipboardByVm = new() { [Vm("rejected")] = true }
                }));
                string primaryFile = ManagerSettings.SettingsFile, backupFile = primaryFile + ".backup.json";
                File.Delete(backupFile);
                const string corruptPrimary = "{read-only-primary";
                File.WriteAllText(primaryFile, corruptPrimary);
                FileAttributes attributes = File.GetAttributes(primaryFile);
                string activeBefore = JsonSerializer.Serialize(settings);
                File.SetAttributes(primaryFile, attributes | FileAttributes.ReadOnly);
                try {
                    bool blocked = false;
                    try { settings.Import(blockedImport); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { blocked = true; }
                    Require(blocked && JsonSerializer.Serialize(settings) == activeBefore && File.ReadAllText(primaryFile) == corruptPrimary && !File.Exists(backupFile),
                        "A rejected primary write changed active settings or left its rejected candidate in the recovery backup");
                    var rejectedRecovery = ManagerSettings.Load(out var recoveryWarning);
                    Require(recoveryWarning is not null && !rejectedRecovery.VirtualMachines.Contains(Vm("rejected")) && !rejectedRecovery.ClipboardEnabled(Vm("rejected")),
                        "A rejected settings import was recovered as if it had succeeded");
                } finally { File.SetAttributes(primaryFile, attributes); }
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
