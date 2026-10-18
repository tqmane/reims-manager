using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal static class SshConfigChecks
{
    internal static async Task Run(string folder)
    {
        string[] paths = [Path.Combine(folder, "Virtual Machines", "日本語 VM", "state", "known_hosts"),
            Path.Combine(folder, "Virtual Machines", "日本語 # archive", "state", "known_hosts"),
            Path.Combine(folder, string.Concat(Enumerable.Repeat("part ", 40)) + "日本語", "known_hosts")];
        foreach (string path in paths) {
            var result = await Parse(GuestClipboard.KnownHostsOption(path));
            if (result.Code != 0 || !result.Lines.Contains("userknownhostsfile " + path.Replace('\\', '/')) ||
                !result.Lines.Any(x => x is "stricthostkeychecking true" or "stricthostkeychecking yes") ||
                !result.Lines.Contains("hostkeyalias reims-offline-fixture"))
                throw new Exception("Windows OpenSSH did not preserve a quoted clipboard known-hosts path and strict checking");
        }
        var comment = await Parse("UserKnownHostsFile=" + paths[1].Replace('\\', '/'));
        var split = await Parse("UserKnownHostsFile=" + paths[2].Replace('\\', '/'));
        if (comment.Code != 0 || comment.Lines.Contains("userknownhostsfile " + paths[1].Replace('\\', '/')) ||
            split.Code != 255 || !split.Lines.Any(x => x.Contains("too many userknownhostsfile entries")))
            throw new Exception("Unquoted SSH parser negative controls did not reproduce the bug");
        foreach (string invalid in new[] { "", "bad\"path", "bad\npath", "bad\rpath", "bad\0path" }) {
            try { GuestClipboard.KnownHostsOption(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("An invalid path reached SSH configuration");
        }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ssh-config.json"), JsonSerializer.Serialize(new {
            windows_openssh = true, offline = true, quoted_unicode_paths = 3, strict_host_key_checking = true,
            host_key_alias = true, unquoted_negative_controls = 2, invalid_paths_rejected = 5
        }));
    }

    private static async Task<(int Code, string[] Lines)> Parse(string option)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/OpenSSH/ssh.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-G", "-F", "NUL", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "IdentitiesOnly=yes", "-o", "HostKeyAlias=reims-offline-fixture", "-o", option, "fixture.invalid" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Windows OpenSSH did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, ((await stdout) + "\n" + (await stderr)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }
}
