using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal sealed record LiveVmStatus(string State, JsonElement? Runtime)
{
    internal static async Task<LiveVmStatus> Read(string directory, CancellationToken cancellation = default)
    {
        string configuration = Path.Combine(directory, "state", "config.json");
        if (!File.Exists(configuration)) return new("unprepared", null);
        string path = Path.Combine(directory, "state", "runtime.json");
        if (!File.Exists(path)) return new("stopped", null);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("VM の実行記録が大きすぎます。");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellation));
        var runtime = document.RootElement.Clone();
        int pid = runtime.GetProperty("pid").GetInt32();
        try {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return new("stopped", null);
            if (process.StartTime.ToUniversalTime().Ticks != runtime.GetProperty("start_ticks").GetInt64() ||
                !string.Equals(process.MainModule?.FileName, runtime.GetProperty("executable").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("実行記録の PID が別のプロセスを指しています。VM の操作を中止しました。");
        } catch (ArgumentException) { return new("stopped", null); }
        int port = runtime.GetProperty("qmp_port").GetInt32();
        if (port < 1024 || port > 65535) throw new InvalidDataException("VM の管理ポートが不正です。");
        return new(await Query(port, cancellation), runtime);
    }

    internal static async Task<string> Query(int port, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var token = deadline.Token;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\n" };
        using (var greeting = JsonDocument.Parse(await ReadLine(reader, token))) {
            if (!greeting.RootElement.TryGetProperty("QMP", out _)) throw new InvalidDataException("VM の管理接続を確認できませんでした。");
        }
        string state = "unknown";
        foreach (string command in new[] { "qmp_capabilities", "query-status" }) {
            string id = Guid.NewGuid().ToString("N");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { execute = command, id }).AsMemory(), token);
            while (true) {
                using var response = JsonDocument.Parse(await ReadLine(reader, token));
                var message = response.RootElement;
                if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.String || responseId.GetString() != id) continue;
                if (message.TryGetProperty("error", out _)) throw new IOException("VM が管理コマンドを拒否しました。");
                if (command == "query-status") state = message.GetProperty("return").GetProperty("status").GetString() ?? "unknown";
                break;
            }
        }
        return state;
    }

    private static async Task<string> ReadLine(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (line.Length < 65536) {
            if (await reader.ReadAsync(character.AsMemory(), token) == 0) throw new EndOfStreamException("VM の管理接続が閉じられました。");
            if (character[0] == '\n') return line.ToString();
            if (character[0] != '\r') line.Append(character[0]);
        }
        throw new InvalidDataException("VM の応答が上限を超えました。");
    }
}
