using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReimsManager;

internal static class GuestClipboard
{
    internal const int MaxBytes = 1024 * 1024;

    internal static bool ShouldPush(string state, string? host, string? previous) =>
        host is not null && host != previous && (state == "running" || state == "unknown" && previous is not null);

    internal static string KnownHostsOption(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Invalid SSH known-hosts path.");
        return "UserKnownHostsFile=\"" + path.Replace('\\', '/') + "\"";
    }

    internal static string SelectScopedHost(string host, IEnumerable<IPAddress> addresses, int interfaceIndex)
    {
        var matching = addresses.Where(address => address.IsIPv6LinkLocal && address.ScopeId == interfaceIndex && interfaceIndex > 0).Distinct().ToArray();
        return matching.Length switch {
            0 => host,
            1 => matching[0].ToString(),
            _ => throw new InvalidOperationException("専用 TAP 上の macOS 接続先が複数あります。接続設定を確認してください。")
        };
    }

    private static async Task<string> ResolveScopedHost(JsonElement network, string host, CancellationToken cancellation)
    {
        if (network.ValueKind != JsonValueKind.Object || network.GetProperty("mode").GetString() != "bridge" ||
            !network.TryGetProperty("forwarder", out var forwarder) || forwarder.GetString() is not ("macnat" or "macnat-ipv4") ||
            !network.TryGetProperty("tap_id", out var tap) || tap.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(tap.GetString(), out var tapId) || tapId == Guid.Empty || IPAddress.TryParse(host, out _)) return host;
        var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(candidate => Guid.TryParse(candidate.Id, out var id) && id == tapId)
            ?? throw new InvalidOperationException("設定された TAP インターフェースを確認できません。");
        int index = adapter.GetIPProperties().GetIPv6Properties()?.Index ?? 0;
        if (index <= 0) throw new InvalidOperationException("設定された TAP の IPv6 インターフェース番号を確認できません。");
        return SelectScopedHost(host, await ResolveAddresses(ResolverStartInfo(), host, cancellation), index);
    }

    internal static ProcessStartInfo ResolverStartInfo()
    {
        string shell = Path.Combine(AppContext.BaseDirectory, "runtimes/pwsh/pwsh.exe");
        if (!File.Exists(shell)) shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe");
        var info = new ProcessStartInfo(shell) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", """
            $ErrorActionPreference='Stop'
            [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            try {
                $name=[Console]::In.ReadToEnd()
                $addresses=@([Net.Dns]::GetHostAddresses($name) | ForEach-Object { $_.ToString() })
                [Console]::Out.Write((ConvertTo-Json -InputObject $addresses -Compress))
            } catch { [Console]::Error.Write('Guest name resolution failed.'); exit 1 }
            """ }) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task<IPAddress[]> ResolveAddresses(ProcessStartInfo info, string host, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(host, "^[A-Za-z0-9][A-Za-z0-9.:-]*$")) throw new ArgumentException("Invalid guest hostname.");
        cancellation.ThrowIfCancellationRequested();
        // A native name-provider loop must not outlive its owned resolver process.
        using var process = Process.Start(info) ?? throw new IOException("名前解決プロセスを起動できません。");
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var kill = cancellation.Register(() => {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        Task<string> output = ReadBounded(process.StandardOutput), error = ReadBounded(process.StandardError);
        try {
            await process.StandardInput.WriteAsync(host.AsMemory(), cancellation).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellation)).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("macOS の接続先を名前解決できませんでした。" + error.Result);
            using var document = JsonDocument.Parse(output.Result);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("名前解決の応答が不正です。");
            return document.RootElement.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.String && IPAddress.TryParse(value.GetString(), out var address)
                    ? address : throw new InvalidDataException("名前解決のアドレスが不正です。")).ToArray();
        } finally {
            capture.Cancel();
            Stop();
            await process.WaitForExitAsync().ConfigureAwait(false);
            // Observe both pipe reads even when writing stdin or waiting for exit failed.
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch { }
        }

        void Stop()
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        }

        async Task<string> ReadBounded(StreamReader reader)
        {
            var result = new StringBuilder();
            char[] buffer = new char[1024];
            try {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), capture.Token).ConfigureAwait(false)) != 0) {
                    if (result.Length + count > 65536) throw new InvalidDataException("名前解決の応答が上限を超えました。");
                    result.Append(buffer, 0, count);
                }
                return result.ToString();
            } catch { Stop(); throw; }
        }
    }

    internal static async Task<string> Transfer(string vmDirectory, string? text, CancellationToken cancellation = default)
    {
        if (text is not null && Encoding.UTF8.GetByteCount(text) > MaxBytes)
            throw new InvalidOperationException("共有できるテキストは 1 MiB までです。");
        string state = Path.Combine(vmDirectory, "state");
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "config.json")));
        using var paired = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "guest.json")));
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "runtime.json")));
        var r = runtime.RootElement;
        using var vm = Process.GetProcessById(r.GetProperty("pid").GetInt32());
        if (vm.StartTime.ToUniversalTime().Ticks != r.GetProperty("start_ticks").GetInt64() ||
            !string.Equals(vm.MainModule?.FileName, r.GetProperty("executable").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("選択した VM のプロセスを確認できません。");
        var c = config.RootElement;
        string host = "127.0.0.1", port = c.GetProperty("ssh_port").ToString();
        if (c.TryGetProperty("network", out var network) && network.GetProperty("mode").GetString() == "bridge") {
            host = network.GetProperty("guest_host").GetString() ?? "";
            port = "22";
        }
        string user = paired.RootElement.GetProperty("user").GetString() ?? "";
        if (!Regex.IsMatch(user, "^[a-z_][a-z0-9_-]*$") || !Regex.IsMatch(host, "^[A-Za-z0-9][A-Za-z0-9.:-]*$"))
            throw new InvalidOperationException("macOS 接続の設定を確認してください。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        host = await ResolveScopedHost(network, host, timeout.Token);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/OpenSSH/ssh.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-F", "NUL", "-T", "-o", "ConnectTimeout=5", "-o", "BatchMode=yes",
            "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=yes", "-o", "KexAlgorithms=curve25519-sha256",
            "-o", KnownHostsOption(Path.Combine(state, "known_hosts")), "-p", port,
            "-i", Path.Combine(state, "ssh/id_ed25519") }) info.ArgumentList.Add(arg);
        if (paired.RootElement.TryGetProperty("host_key_alias", out var alias)) {
            info.ArgumentList.Add("-o"); info.ArgumentList.Add("HostKeyAlias=" + alias.GetString());
        }
        info.ArgumentList.Add(user + "@" + host);
        info.ArgumentList.Add(text is null ? "LC_CTYPE=en_US.UTF-8 /usr/bin/pbpaste" : "/usr/bin/base64 -D | LC_CTYPE=en_US.UTF-8 /usr/bin/pbcopy");
        using var process = Process.Start(info)!;
        using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        // Clipboard contents travel only through stdin/stdout, never argv or logs.
        var output = ReadBounded(process.StandardOutput, timeout.Token);
        if (text is not null) await process.StandardInput.WriteAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).AsMemory(), timeout.Token);
        process.StandardInput.Close();
        try {
            string result = await output;
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("クリップボード接続に失敗しました。VM の起動と SSH ペアリングを確認してください。");
            return result;
        } finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        int bytes = 0, count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0) {
            bytes += Encoding.UTF8.GetByteCount(buffer, 0, count);
            if (bytes > MaxBytes) throw new InvalidOperationException("共有できるテキストは 1 MiB までです。");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
