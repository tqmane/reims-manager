using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal sealed record UsbDriverInstallResult(bool RestartRequired)
{
    private const int MaximumBytes = 4096;
    private const string Unconfirmed = "UsbDk の導入結果を確認できません。Windows の再起動が必要か不明です。USB 設定から導入状態を再確認してください。";

    internal static UsbDriverInstallResult Read(string path)
    {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > MaximumBytes) throw new InvalidDataException(Unconfirmed);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            return Validate(document.RootElement);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) {
            throw new InvalidDataException(Unconfirmed, error);
        }
    }

    internal static UsbDriverInstallResult Parse(string json)
    {
        if (json.Length is < 1 or > MaximumBytes) throw new InvalidDataException(Unconfirmed);
        try {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            return Validate(document.RootElement);
        } catch (JsonException error) { throw new InvalidDataException(Unconfirmed, error); }
    }

    private static UsbDriverInstallResult Validate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1 ||
            !value.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.True ||
            !value.TryGetProperty("restart_required", out var restart) || restart.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !value.TryGetProperty("exit_code", out var exit) || exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out int code) ||
            code is not (0 or 3010) || restart.GetBoolean() != (code == 3010))
            throw new InvalidDataException(Unconfirmed);
        return new(restart.GetBoolean());
    }

    internal string ToJson() => JsonSerializer.Serialize(new {
        schema = 1, installed = true, restart_required = RestartRequired, exit_code = RestartRequired ? 3010 : 0
    });
}
