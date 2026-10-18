using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal sealed record TapInstallResult(string? AdapterId, string? AdapterName, bool RestartRequired, bool Owned)
{
    internal static TapInstallResult Read(string file, DateTime startedUtc)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 65536 || File.GetLastWriteTimeUtc(file) < startedUtc)
            throw new InvalidDataException("最新の TAP 導入結果を確認できません。");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || value.GetProperty("schema").GetInt32() != 1 || value.GetProperty("bridge_created").GetBoolean())
            throw new InvalidDataException("TAP 導入結果の形式が対応していません。");
        bool created = value.GetProperty("created").GetBoolean(), reused = value.GetProperty("reused").GetBoolean();
        bool restart = value.GetProperty("restart_required").GetBoolean(), owned = value.GetProperty("owned").GetBoolean();
        string? id = value.GetProperty("adapter_id").GetString(), name = value.GetProperty("adapter_name").GetString();
        string pnp = value.GetProperty("pnp_instance_id").GetString() ?? "";
        if (created == reused || created && !owned || pnp.Length is < 1 or > 1024 || pnp.Any(char.IsControl) ||
            (id is null) != (name is null) || id is null && !restart ||
            id is not null && (!Guid.TryParse(id, out var guid) || guid == Guid.Empty) ||
            name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)))
            throw new InvalidDataException("TAP 導入結果のデバイス情報を確認できません。");
        return new(id, name, restart, owned);
    }
}
