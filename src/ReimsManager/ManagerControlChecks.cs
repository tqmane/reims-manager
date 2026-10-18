using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal static class ManagerControlChecks
{
    // CPU-only target selection: the clone has the same basename, a different
    // actual executable path, no windows, no VM, and no user preferences.
    internal static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);
        string cloneFolder = Path.Combine(folder, "same basename 日本語"); Directory.CreateDirectory(cloneFolder);
        string executable = Environment.ProcessPath!, cloneExecutable = Path.Combine(cloneFolder, Path.GetFileName(executable));
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) {
            string cloneFile = Path.Combine(cloneFolder, Path.GetFileName(file));
            if (file.Equals(executable, StringComparison.OrdinalIgnoreCase)) File.Copy(file, cloneFile);
            else if (!CreateHardLink(cloneFile, file, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        string ready = Path.Combine(folder, "clone-ready.txt");
        var cloneInfo = new ProcessStartInfo(cloneExecutable) { UseShellExecute = false, CreateNoWindow = true };
        cloneInfo.ArgumentList.Add("--control-target-hold"); cloneInfo.ArgumentList.Add(ready);
        using var clone = Process.Start(cloneInfo) ?? throw new Exception("Selection clone did not start.");
        try {
            var clock = Stopwatch.StartNew();
            while (!File.Exists(ready) && clock.Elapsed < TimeSpan.FromSeconds(10) && !clone.HasExited) await Task.Delay(50);
            if (!File.Exists(ready) || clone.HasExited || clone.ProcessName != Process.GetCurrentProcess().ProcessName ||
                !string.Equals(clone.MainModule?.FileName, cloneExecutable, StringComparison.OrdinalIgnoreCase) || ManagerControl.IsManager(clone))
                throw new Exception("Different-path same-basename fixture was not rejected.");
            using var server = new ManagerControlServer(_ => Task.FromResult(JsonSerializer.SerializeToElement(new {
                schema = 1, ok = true, manager_pid = Environment.ProcessId
            })));
            var discovered = await Client([]);
            if (!discovered.GetProperty("ok").GetBoolean() || discovered.GetProperty("manager_pid").GetInt32() != Environment.ProcessId)
                throw new Exception("Auto-selection included a different executable path.");
            var rejected = await Client(["--pid", clone.Id.ToString()]);
            if (rejected.GetProperty("ok").GetBoolean() || rejected.GetProperty("error").GetString() != "control_error" ||
                !rejected.GetProperty("message").GetString()!.Contains("same Reims Manager executable", StringComparison.Ordinal))
                throw new Exception("Explicit PID bypassed executable path validation.");
            File.WriteAllText(Path.Combine(folder, "control-target-result.json"), JsonSerializer.Serialize(new {
                actual_same_basename_clone = true, different_executable_path_excluded = true,
                automatic_selection_same_executable = true, explicit_wrong_path_pid_rejected = true,
                uppercase_cli_path_accepted = true, no_windows_or_vm = true
            }));
        } finally { if (!clone.HasExited) { clone.Kill(); await clone.WaitForExitAsync(); } }

        static async Task<JsonElement> Client(string[] extra)
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!.ToUpperInvariant()) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in new[] { "--control", "status" }.Concat(extra)) info.ArgumentList.Add(argument);
            using var client = Process.Start(info) ?? throw new Exception("Selection CLI did not start.");
            var stdout = client.StandardOutput.ReadToEndAsync(); var stderr = client.StandardError.ReadToEndAsync();
            try { await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!client.HasExited) { client.Kill(); await client.WaitForExitAsync(); } }
            using var document = JsonDocument.Parse(await stdout);
            var reply = document.RootElement.Clone();
            if ((await stderr).Length != 0 || (client.ExitCode == 0) != reply.GetProperty("ok").GetBoolean()) throw new Exception("Selection CLI response failed.");
            return reply;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, nint securityAttributes);
}
