using System.IO;
using System.Text.Json;
using ReimsManager.Services;

namespace ReimsManager;

internal sealed record LiveVmStatus(string State, JsonElement? Runtime)
{
    internal static async Task<LiveVmStatus> Read(string directory, CancellationToken cancellation = default)
    {
        string configuration = Path.Combine(directory, "state", "config.json");
        if (!File.Exists(configuration)) return new("unprepared", null);
        string path = Path.Combine(directory, "state", "runtime.json");
        if (!File.Exists(path)) return new("stopped", null);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException(UiText.T("VM の実行記録が大きすぎます。"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellation));
        var runtime = document.RootElement.Clone();
        int pid = runtime.GetProperty("pid").GetInt32();
        long ticks = runtime.GetProperty("start_ticks").GetInt64();
        string exe = runtime.GetProperty("executable").GetString() ?? "";
        if (!ProcessIdentity.IsAlive(pid, ticks, exe)) return new("stopped", null);
        int port = runtime.GetProperty("qmp_port").GetInt32();
        if (port < 1024 || port > 65535) throw new InvalidDataException(UiText.T("VM の管理ポートが不正です。"));
        return new(await Query(port, cancellation), runtime);
    }

    internal static Task<string> Query(int port, CancellationToken cancellation = default) =>
        QmpClient.QueryStatusAsync(port, cancellation);
}
