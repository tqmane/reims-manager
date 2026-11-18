using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace ReimsManager.Services;

internal static class NativeOpenCore
{
    internal sealed record IdentityValues(string Model, string Serial, string Mlb, string Uuid, string Rom);

    internal static IdentityValues ReadIdentity(string projectRoot, string vmRoot)
    {
        string manifestPath = Path.Combine(vmRoot, "state", "opencore.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException(UiText.T("先に VM のセットアップを完了してください。"));

        using var manifestDoc = JsonDocument.Parse(AtomicFileStore.ReadBoundedText(manifestPath));
        if (!manifestDoc.RootElement.TryGetProperty("config", out var configProp) || configProp.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(UiText.T("識別情報を読み込めません。"));

        string configPath = AtomicFileStore.AssertArtifactPath(configProp.GetString()!, projectRoot, vmRoot);
        var doc = XDocument.Parse(AtomicFileStore.ReadBoundedText(configPath, 4 * 1024 * 1024));
        var rootDict = doc.Root?.Element("dict")
            ?? throw new InvalidDataException(UiText.T("OpenCore 設定のルート辞書がありません。"));
        var platformInfo = FindDictValue(rootDict, "PlatformInfo")
            ?? throw new InvalidDataException(UiText.T("PlatformInfo が見つかりません。"));
        var generic = FindDictValue(platformInfo, "Generic")
            ?? throw new InvalidDataException(UiText.T("PlatformInfo/Generic が見つかりません。"));

        string model = ReadStringValue(generic, "SystemProductName");
        string serial = ReadStringValue(generic, "SystemSerialNumber");
        string mlb = ReadStringValue(generic, "MLB");
        string uuid = ReadStringValue(generic, "SystemUUID");
        byte[] romBytes = ReadDataValue(generic, "ROM");
        string rom = Convert.ToHexString(romBytes).ToLowerInvariant();
        return new IdentityValues(model, serial, mlb, uuid, rom);
    }

    private static XElement? FindValueElement(XElement dict, string key)
    {
        var children = dict.Elements().ToList();
        for (int i = 0; i + 1 < children.Count; i++) {
            if (children[i].Name.LocalName == "key" && string.Equals(children[i].Value, key, StringComparison.Ordinal))
                return children[i + 1];
        }
        return null;
    }

    private static XElement? FindDictValue(XElement dict, string key)
    {
        var el = FindValueElement(dict, key);
        return el is { Name.LocalName: "dict" } ? el : null;
    }

    private static string ReadStringValue(XElement dict, string key)
    {
        var el = FindValueElement(dict, key);
        if (el is not { Name.LocalName: "string" })
            throw new InvalidDataException(UiText.Format($"OpenCore 識別情報 '{key}' が見つかりません。"));
        return el.Value;
    }

    private static byte[] ReadDataValue(XElement dict, string key)
    {
        var el = FindValueElement(dict, key);
        if (el is not { Name.LocalName: "data" })
            throw new InvalidDataException(UiText.Format($"OpenCore 識別情報 '{key}' が見つかりません。"));
        string base64 = new string(el.Value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Convert.FromBase64String(base64);
    }
}
