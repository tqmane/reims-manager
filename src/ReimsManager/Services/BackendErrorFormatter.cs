using System.Text.RegularExpressions;

namespace ReimsManager.Services;

internal static partial class BackendErrorFormatter
{
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"^\s*(?:Exception:|Line\s*\||\d+\s*\||\s*\|?\s*~+\s*$)", RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellFrameRegex();

    /// <summary>
    /// Extracts the clean human-readable message from PowerShell formatted exceptions
    /// so UI banners never show noisy script line numbers or tilde underlines.
    /// </summary>
    internal static string CleanMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string stripped = AnsiEscapeRegex().Replace(raw, "").Replace("\r\n", "\n").Trim();
        var lines = stripped.Split('\n');
        var cleaned = new List<string>(lines.Length);
        bool sawFrame = false;
        foreach (string line in lines) {
            if (PowerShellFrameRegex().IsMatch(line)) {
                sawFrame = true;
                continue;
            }
            string item = line.Trim();
            if (sawFrame && item.StartsWith('|'))
                item = item[1..].Trim();
            if (item.Length > 0) cleaned.Add(item);
        }
        string message = cleaned.Count > 0 ? string.Join("\n", cleaned) : stripped;
        return TranslateKnownDiagnostic(message);
    }

    internal static string TranslateKnownDiagnostic(string message)
    {
        if (message.Contains("Current driver is foreign to this receipt", StringComparison.OrdinalIgnoreCase))
            return UiText.T("現在のドライバーが保存済みレシートと一致しないため自動上書きを中止しました。「登録を解除して Windows に戻す」または「復元エラーをリセット」で復旧できます。");
        return message;
    }
}
