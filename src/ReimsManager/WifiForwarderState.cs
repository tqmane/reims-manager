using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal sealed record WifiForwarderState(string? Kind, bool Installed, bool Configured, bool Running, bool Ready, int? Pid, string? Error, bool Ipv4Only)
{
    internal const string MacNat = "macnat-ipv4";
    internal static WifiForwarderState? Read(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("network_forwarder", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("転送状態の形式が対応していません。");
        string? kind=value.GetProperty("kind").GetString();
        if (kind == "none") kind=null;
        var result = new WifiForwarderState(kind, value.GetProperty("installed").GetBoolean(),
            value.GetProperty("configured").GetBoolean(), value.GetProperty("running").GetBoolean(), value.GetProperty("ready").GetBoolean(),
            value.GetProperty("pid").ValueKind == JsonValueKind.Null ? null : value.GetProperty("pid").GetInt32(),
            value.GetProperty("error").GetString(), value.GetProperty("ipv4_only").GetBoolean());
        if (result.Pid <= 0 || result.Ready && !result.Running || result.Kind==MacNat && !result.Ipv4Only) throw new InvalidDataException("転送プロセスの状態を確認できません。");
        return result;
    }

    internal string Summary(string vmState) => !string.IsNullOrWhiteSpace(Error) ? "転送エラー" : Kind is not (null or MacNat) ? "未対応の転送方式" :
        Running && Ready ? "転送プロセスの準備完了（IPv4）" : Running ? "転送プロセスを初期化しています" :
        !Installed ? "ドライバー・転送ツールの準備が必要です" : !Configured ? "Wi-Fi 互換接続は未設定です" :
        vmState is "stopped" or "shutdown" ? "設定済み・VM の起動待ち" : "転送プロセスは停止しています";

    internal static string[] PrepareArguments(string physicalId, bool installed, bool consent)
    {
        if (!Guid.TryParse(physicalId, out var id) || id == Guid.Empty) throw new ArgumentException("物理 Wi-Fi を選択してください。");
        if (!installed && !consent) throw new InvalidOperationException("ドライバーの利用条件への同意が必要です。");
        return consent ? ["-PhysicalAdapter", id.ToString("D"), "-AcceptPacketFilterTerms"] : ["-PhysicalAdapter", id.ToString("D")];
    }
}
