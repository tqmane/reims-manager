using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal sealed record NetworkUplink(string Id, string Name, string Description, string Kind, string Status, bool Connected)
{
    public string DisplayName => Name + " · " + (Kind switch { "wifi" => "Wi-Fi", "ethernet" => "有線 LAN", _ => "その他" }) +
        " · " + (Connected ? "物理リンクあり" : Status);
}

internal sealed record NetworkInventory(NetworkUplink[] Uplinks, string[] Taps)
{
    internal static NetworkInventory Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("ネットワーク一覧の形式に対応していません。");
        var uplinks = root.GetProperty("uplinks").EnumerateArray().Select(value => new NetworkUplink(
            Guid.Parse(value.GetProperty("id").GetString()!).ToString("D"), value.GetProperty("name").GetString()!,
            value.GetProperty("description").GetString()!, value.GetProperty("kind").GetString()!,
            value.GetProperty("status").GetString()!, value.GetProperty("connected").GetBoolean())).ToArray();
        if (uplinks.Any(value => string.IsNullOrWhiteSpace(value.Name)) || uplinks.Select(value => value.Id).Distinct().Count() != uplinks.Length)
            throw new InvalidDataException("物理アダプターの一覧を確認できません。");
        return new(uplinks, root.GetProperty("tap_adapters").EnumerateArray().Select(value => value.GetProperty("name").GetString()!).ToArray());
    }
}
