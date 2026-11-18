using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ReimsManager.Services;

internal static class QmpClient
{
    internal static Task<string> QueryStatusAsync(int port, CancellationToken cancellation = default) =>
        ExecuteAsync(port, "query-status", null, cancellation).ContinueWith(t => {
            using var doc = t.GetAwaiter().GetResult();
            return doc.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                ? status.GetString() ?? "unknown"
                : "unknown";
        }, TaskContinuationOptions.ExecuteSynchronously);

    internal static Task<JsonDocument> ExecuteAsync(int port, string command, object? arguments = null, CancellationToken cancellation = default) =>
        Task.Run(() => {
            if (port is < 1024 or > 65535) throw new InvalidDataException(UiText.T("VM の管理ポートが不正です。"));
            using var mutex = new Mutex(false, $"Local\\ReimsManager.Qmp.{port}");
            bool locked = false;
            try {
                try { locked = WaitHandle.WaitAny([mutex, cancellation.WaitHandle], TimeSpan.FromSeconds(5)) == 0; }
                catch (AbandonedMutexException) { locked = true; }
                cancellation.ThrowIfCancellationRequested();
                if (!locked) throw new IOException(UiText.T("VM の管理接続を使用中です。操作の完了後に再試行してください。"));
                // A named mutex must be released by its acquiring thread.
                return ExecuteCoreAsync(port, command, arguments, cancellation).GetAwaiter().GetResult();
            } finally {
                if (locked) mutex.ReleaseMutex();
            }
        }, cancellation);

    internal static async Task<string> HumanMonitorCommandAsync(int port, string commandLine, CancellationToken cancellation = default)
    {
        using var doc = await ExecuteAsync(port, "human-monitor-command", new Dictionary<string, object> { ["command-line"] = commandLine }, cancellation).ConfigureAwait(false);
        return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? "" : "";
    }

    internal static async Task<List<(string Name, string Type)>> QomListAsync(int port, string path, CancellationToken cancellation = default)
    {
        using var doc = await ExecuteAsync(port, "qom-list", new { path }, cancellation).ConfigureAwait(false);
        var list = new List<(string, string)>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array) {
            foreach (var item in doc.RootElement.EnumerateArray()) {
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (name.Length > 0) list.Add((name, type));
            }
        }
        return list;
    }

    internal static async Task<bool> QomGetBoolAsync(int port, string path, string property, CancellationToken cancellation = default)
    {
        using var doc = await ExecuteAsync(port, "qom-get", new { path, property }, cancellation).ConfigureAwait(false);
        if (doc.RootElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException(UiText.T("QEMU から不正なプロパティ値が返されました。"));
        return doc.RootElement.GetBoolean();
    }

    internal static async Task QomSetBoolAsync(int port, string path, string property, bool value, CancellationToken cancellation = default)
    {
        using var _ = await ExecuteAsync(port, "qom-set", new { path, property, value }, cancellation).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ExecuteCoreAsync(int port, string command, object? arguments, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\n" };
        using (var greeting = JsonDocument.Parse(await ReadLineAsync(reader, token).ConfigureAwait(false))) {
            if (!greeting.RootElement.TryGetProperty("QMP", out _))
                throw new InvalidDataException(UiText.T("VM の管理接続を確認できませんでした。"));
        }

        JsonDocument? finalResult = null;
        var steps = new (string Cmd, object? Args)[] { ("qmp_capabilities", null), (command, arguments) };
        foreach (var (cmd, args) in steps) {
            string id = Guid.NewGuid().ToString("N");
            object payload = args is null
                ? new { execute = cmd, id }
                : new { execute = cmd, arguments = args, id };
            await writer.WriteLineAsync(JsonSerializer.Serialize(payload).AsMemory(), token).ConfigureAwait(false);
            while (true) {
                using var response = JsonDocument.Parse(await ReadLineAsync(reader, token).ConfigureAwait(false));
                var message = response.RootElement;
                if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.String || responseId.GetString() != id)
                    continue;
                if (message.TryGetProperty("error", out var err)) {
                    string desc = err.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                    throw new IOException(string.IsNullOrWhiteSpace(desc)
                        ? UiText.T("VM が管理コマンドを拒否しました。")
                        : UiText.Format($"VM が管理コマンドを拒否しました: {desc}"));
                }
                if (cmd == command) {
                    string raw = message.TryGetProperty("return", out var ret) ? ret.GetRawText() : "{}";
                    finalResult = JsonDocument.Parse(raw);
                }
                break;
            }
        }
        return finalResult ?? JsonDocument.Parse("{}");
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (line.Length < 1024 * 1024) {
            if (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) == 0)
                throw new EndOfStreamException(UiText.T("VM の管理接続が閉じられました。"));
            if (character[0] == '\n') return line.ToString();
            if (character[0] != '\r') line.Append(character[0]);
        }
        throw new InvalidDataException(UiText.T("VM の応答が上限を超えました。"));
    }
}
