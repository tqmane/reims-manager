using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal sealed record SetupCheck(string Id, string Label, bool Ready, bool Required, string Message, string? Action)
{
    public string Status => Ready ? "準備済み" : Required ? "要対応" : "任意";
}

internal sealed record SetupRequirements(bool Ready, bool RestartRequired, SetupCheck[] Checks, int Cpus, int MemoryMiB, int? AvailableMemoryMiB, bool StartReady)
{
    internal static SetupRequirements Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var checks = root.GetProperty("checks").EnumerateArray().Select(value => new SetupCheck(
            value.GetProperty("id").GetString() ?? "", value.GetProperty("label").GetString() ?? "",
            value.GetProperty("ready").GetBoolean(), !value.TryGetProperty("required", out var required) || required.GetBoolean(),
            value.GetProperty("message").GetString() ?? "", value.TryGetProperty("action", out var action) ? action.GetString() : null)).ToArray();
        if (checks.Length == 0 || checks.Any(x => x.Id.Length == 0 || x.Label.Length == 0 || x.Action is not (null or "EnableWhpx" or "InstallUsbDk")))
            throw new InvalidDataException("必要条件の確認結果を読み込めません。対応する Manager を使ってください。");
        var plan = root.GetProperty("plan");
        int cpus = plan.GetProperty("cpus").GetInt32(), memory = plan.GetProperty("memory_mib").GetInt32();
        int? available = plan.GetProperty("available_memory_mib").ValueKind == JsonValueKind.Null ? null : plan.GetProperty("available_memory_mib").GetInt32();
        if (cpus is < 1 or > 64 || memory is < 4096 or > 131072 || memory % 4 != 0 || available < 0)
            throw new InvalidDataException("CPU またはメモリの計画を確認できません。");
        bool ready = root.GetProperty("ready").GetBoolean() && checks.All(x => !x.Required || x.Ready);
        bool restart = root.GetProperty("restart_required").GetBoolean();
        return new(ready, restart, checks, cpus, memory, available,
            ready && !restart && available >= memory + 1024 && plan.GetProperty("start_ready").GetBoolean());
    }
}
