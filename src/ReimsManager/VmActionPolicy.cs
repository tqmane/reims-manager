using System.Globalization;
using System.Text.RegularExpressions;

namespace ReimsManager;

/// <summary>One fail-closed action policy for buttons, menus and event handlers.</summary>
internal static class VmActionPolicy
{
    internal static bool CanRun(string action, string state, bool hasVm, bool hasEngine)
    {
        if (!hasEngine) return false;
        if (action == "InstallUsbDk") return !hasVm || state is "stopped" or "unprepared";
        if (action is "EnableWhpx" or "CreateBridge") return !hasVm || state is "stopped" or "unprepared";
        if (!hasVm) return false;
        return action switch {
            "Status" or "CollectLogs" => true,
            "Setup" or "ImportDisk" => state == "unprepared",
            "Start" => state is "stopped" or "shutdown",
            "Clipboard" => state is "running" or "unknown",
            "PowerOff" or "Restart" or "Pause" or "Pair" or "ApplyResolution" or "AttachUsb" or "EnableCursorAid" or "DisableCursorAid" or "MoveCursorToMainDisplay" or "MoveCursorToExtendedDisplay" or "HideHostCursor" or "LightenExtendedDisplay" or "RestoreExtendedDisplay" => state == "running",
            "ShowHostCursor" => state is "running" or "paused" or "stopped" or "shutdown" or "prelaunch" or "suspended",
            "ListUsb" => state is "running" or "stopped" or "shutdown" or "paused" or "prelaunch" or "suspended",
            "DetachUsb" => state is "running" or "stopped",
            "Resume" => state is "paused" or "prelaunch" or "suspended",
            "Exit" => state is "running" or "shutdown",
            "ForceExit" => state is "running" or "paused" or "prelaunch" or "suspended" or "shutdown" or "io-error" or "internal-error" or "guest-panicked" or "watchdog",
            "Console" => state is "running" or "paused" or "prelaunch" or "suspended",
            "GetIdentity" => state is "stopped" or "running" or "paused" or "shutdown" or "prelaunch" or "suspended",
            "SetHardware" or "ExpandDisk" or "OptimizeDisk" or "SnapshotCreate" or "SnapshotRestore" or "SetIdentity" or "GenerateIdentity" or "ApplyBootProfile" or "EnableGuestBridge" or "DisableGuestBridge" or "PrepareUpdates" or "BuildEngine" or "SetResolution" or "SetNetwork" or "RemoveBridge" or "InstallTap" or "PrepareWifi" or "DisableWifi" => state == "stopped",
            _ => false
        };
    }

    internal static string[] StartArguments(bool withInstaller) => withInstaller ? ["-WithInstaller"] : [];

    internal static string StateLabel(string state) => state switch {
        "running" => "実行中", "shutdown" => "電源オフ", "paused" => "一時停止",
        "suspended" => "サスペンド", "stopped" => "完全停止", "unprepared" => "未セットアップ",
        "prelaunch" => "起動待ち", "unknown" => "状態を確認できません", _ => "要確認: " + state
    };

    internal static string Label(string action) => action switch {
        "Start" => "VM を起動", "PowerOff" => "macOS をシャットダウン", "Restart" => "macOS を再起動",
        "Exit" => "VM を完全終了", "ForceExit" => "VM を強制終了", "Pause" => "VM を一時停止", "Resume" => "VM を再開",
        "Setup" => "仮想ディスクとインストーラーを準備", "ImportDisk" => "元ディスクを保持して取り込み",
        "SetHardware" => "ハードウェア設定を保存", "SnapshotCreate" => "スナップショットを保存", "SnapshotRestore" => "スナップショットを復元",
        "ExpandDisk" => "仮想ディスクを拡張", "OptimizeDisk" => "ディスク差分を整理", "Pair" => "SSH の指紋を確認",
        "SetIdentity" => "Mac の識別情報を保存", "GenerateIdentity" => "Mac の識別情報を生成", "CollectLogs" => "診断ログを収集",
        "ApplyBootProfile" => "OpenCore の起動プロファイルを再適用",
        "EnableGuestBridge" => "GPU 互換ブリッジを適用", "DisableGuestBridge" => "GPU 互換ブリッジを解除",
        "RemoveBridge" => "ブリッジを解除して NAT に復帰",
        "InstallTap" => "専用 TAP ドライバーを準備",
        "PrepareWifi" => "Wi-Fi 互換接続を準備", "DisableWifi" => "Wi-Fi 互換接続を解除",
        "EnableCursorAid" => "カーソル互換表示を有効化", "DisableCursorAid" => "カーソル互換表示を無効化",
        "MoveCursorToMainDisplay" => "カーソルを VM 本体の画面へ移動", "MoveCursorToExtendedDisplay" => "カーソルを拡張画面へ移動",
        "HideHostCursor" => "Windows 側のカーソルを隠す", "ShowHostCursor" => "Windows 側のカーソルを表示",
        "LightenExtendedDisplay" => "拡張画面を軽量化", "RestoreExtendedDisplay" => "拡張画面の画質を戻す",
        "ApplyResolution" => "ゲストの解像度を変更",
        "ListUsb" => "USB デバイスを取得", "AttachUsb" => "USB の接続を確認", "DetachUsb" => "USB の登録を解除",
        "InstallUsbDk" => "USB 転送ドライバーを導入", _ => action
    };
}

internal static class ManagerValidation
{
    internal static bool Integer(string text, int min, int max, int multiple, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
        value >= min && value <= max && value % multiple == 0;

    internal static bool SnapshotName(string text) => Regex.IsMatch(text, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant);
    internal static bool Resolution(string value) => value is "1280x720" or "1920x1080" or "2560x1440" or "3840x2160";
    internal static bool Host(string value) => value.Length == 0 || Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9.:-]*\z", RegexOptions.CultureInvariant);
}
