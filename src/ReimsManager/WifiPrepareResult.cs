using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal sealed record WifiPrepareResult(bool Prepared, bool Configured, bool RestartRequired, string? Error=null)
{
    internal static WifiPrepareResult Read(string path, DateTime startedUtc)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (stream.Length is < 1 or > 65536 || File.GetLastWriteTimeUtc(path)<startedUtc) throw new InvalidDataException("最新の Wi-Fi 準備結果を確認できません。");
        using var document=JsonDocument.Parse(stream,new JsonDocumentOptions { MaxDepth=8 });
        var value=document.RootElement;
        if (value.GetProperty("schema").GetInt32()!=1) throw new InvalidDataException("Wi-Fi 準備結果の形式に対応していません。");
        string? error=null;
        if(value.TryGetProperty("error",out var detail)) {
            if(detail.ValueKind!=JsonValueKind.String || detail.GetString() is not { Length: <=2048 } message)
                throw new InvalidDataException("Wi-Fi 準備結果のエラー形式が正しくありません。");
            error=message;
        }
        var result=new WifiPrepareResult(value.GetProperty("prepared").GetBoolean(),value.GetProperty("configured").GetBoolean(),value.GetProperty("restart_required").GetBoolean(),error);
        if (result.RestartRequired && (result.Prepared || result.Configured)) throw new InvalidDataException("Wi-Fi 準備結果が一致しません。");
        if(!string.IsNullOrWhiteSpace(error) && (result.Prepared || result.Configured || result.RestartRequired)) throw new InvalidDataException("Wi-Fi 準備結果が一致しません。");
        return result;
    }
}
