using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal static class SshConfigChecks
{
    internal static async Task Run(string folder)
    {
        await CheckResolverLifetime(folder);
        const string host = "guest-fixture.local";
        var own = IPAddress.Parse("fe80::1234%24");
        var other = IPAddress.Parse("fe80::5678%11");
        if (GuestClipboard.SelectScopedHost(host, [IPAddress.Parse("192.0.2.1"), IPAddress.Parse("2001:db8::1"), other, own, own], 24) != own.ToString() ||
            GuestClipboard.SelectScopedHost(host, [other], 24) != host ||
            GuestClipboard.SelectScopedHost(host, [IPAddress.Parse("fe80::1234")], 24) != host ||
            GuestClipboard.SelectScopedHost(host, [own], 0) != host ||
            GuestClipboard.SelectScopedHost(host, [], 24) != host)
            throw new Exception("Clipboard SSH did not keep scoped management on its own TAP");
        bool ambiguous = false;
        try { GuestClipboard.SelectScopedHost(host, [own, IPAddress.Parse("fe80::5678%24")], 24); }
        catch (InvalidOperationException) { ambiguous = true; }
        if (!ambiguous) throw new Exception("Ambiguous scoped guest endpoints were accepted");
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
            host_key_alias = true, unquoted_negative_controls = 2, invalid_paths_rejected = 5, scoped_guest_endpoint = true
        }));
    }

    internal static async Task CheckResolverLifetime(string folder)
    {
        const string host = "guest-fixture.local";
        await Check("scoped", "[Console]::Out.Write('[\"192.0.2.1\",\"fe80::1234%24\",\"fe80::5678%11\",\"fe80::1234%24\"]')", "fe80::1234%24");
        await Check("ipv4-only", "[Console]::Out.Write('[\"192.0.2.1\"]')", host);
        await Check("empty", "[Console]::Out.Write('[]')", host);
        await Check("malformed-json", "[Console]::Out.Write('not-json')", null);
        await Check("malformed-address", "[Console]::Out.Write('[\"not-an-address\"]')", null);
        await Check("failure", "[Console]::Error.Write('fixture error'); exit 7", null);
        await Check("bounded-output", "[Console]::Out.Write(('x' * 65537)); Start-Sleep -Seconds 60", null);
        await Check("bounded-error", "[Console]::Error.Write(('x' * 65537)); Start-Sleep -Seconds 60", null);
        await Check("invalid-encoding", "[Console]::OpenStandardOutput().WriteByte(255); Start-Sleep -Seconds 60", null);
        await Check("cancel", "Start-Sleep -Seconds 60", null, cancel: true);
        File.WriteAllText(Path.Combine(folder, "resolver-lifetime.json"), JsonSerializer.Serialize(new {
            synthetic_only = true, scoped_result = true, ipv4_fallback = true, malformed_rejected = true,
            bounded_stdout_stderr = true, cancellation_reaps_child = true
        }));

        async Task Check(string name, string body, string? expected, bool cancel = false)
        {
            string root = Path.GetFullPath(Path.Combine(folder, "resolver-" + name));
            Directory.CreateDirectory(root);
            string metadata = Path.Combine(root, "process.json");
            if (File.Exists(metadata)) File.Delete(metadata);
            var info = GuestClipboard.ResolverStartInfo();
            info.WorkingDirectory = root;
            info.ArgumentList[info.ArgumentList.Count - 1] = """
                $ErrorActionPreference='Stop'
                [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
                [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
                if([Console]::In.ReadToEnd() -cne 'guest-fixture.local'){exit 8}
                $self=Get-Process -Id $PID
                [IO.File]::WriteAllText((Join-Path (Get-Location).Path 'process.tmp'),(@{pid=$PID;start=$self.StartTime.ToUniversalTime().Ticks}|ConvertTo-Json -Compress))
                [IO.File]::Move((Join-Path (Get-Location).Path 'process.tmp'),(Join-Path (Get-Location).Path 'process.json'))
                """ + "\n" + body;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<IPAddress[]> run = GuestClipboard.ResolveAddresses(info, host, cancellation.Token);
            Process? child = null;
            try {
                if (cancel) {
                    var clock = Stopwatch.StartNew();
                    while (!File.Exists(metadata)) {
                        if (run.IsCompleted) { await run; throw new Exception("Resolver exited before fixture readiness"); }
                        if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Resolver fixture did not publish its owned PID");
                        await Task.Delay(20).ConfigureAwait(false);
                    }
                    using var document = JsonDocument.Parse(File.ReadAllText(metadata));
                    child = Process.GetProcessById(document.RootElement.GetProperty("pid").GetInt32());
                    _ = child.SafeHandle;
                    if (child.StartTime.ToUniversalTime().Ticks != document.RootElement.GetProperty("start").GetInt64())
                        throw new Exception("Resolver fixture PID changed");
                    cancellation.Cancel();
                }
                try {
                    var addresses = await run.ConfigureAwait(false);
                    if (expected is null || GuestClipboard.SelectScopedHost(host, addresses, 24) != expected)
                        throw new Exception("Resolver fixture accepted invalid output or changed scoped selection");
                } catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested) { }
                catch (Exception failure) when (expected is null && !cancel && failure is JsonException or IOException or DecoderFallbackException) { }
                if (child is not null && !child.HasExited) throw new Exception("Cancellation returned before the resolver was reaped");
                using var receipt = JsonDocument.Parse(File.ReadAllText(metadata));
                try {
                    using var remaining = Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32());
                    if (!remaining.HasExited && remaining.StartTime.ToUniversalTime().Ticks == receipt.RootElement.GetProperty("start").GetInt64())
                        throw new Exception("Resolver returned with its child still running");
                } catch (ArgumentException) { }
            } finally {
                cancellation.Cancel();
                try { await run.ConfigureAwait(false); } catch { }
                child?.Dispose();
            }
        }
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
