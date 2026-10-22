using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ReimsManager;

public partial class MainWindow
{
    private void OpenArm64Workflow(object sender, RoutedEventArgs e)
    {
        try {
            Process.Start(new ProcessStartInfo("https://github.com/tqmane/reims-manager/actions/workflows/arm64-compat.yml") {
                UseShellExecute = true
            });
        } catch (Exception error) { ShowError("GitHub Actions を開けませんでした: " + error.Message); }
    }

    private void BrowseArm64Runtime(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy) return;
        var dialog = new OpenFileDialog {
            Title = "ReimsArm64Maclator (GitHub Actions ZIP)",
            Filter = "GitHub Actions ZIP|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) Arm64RuntimeArchive.Text = dialog.FileName;
    }

    private void BrowseArm64Sysroot(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy) return;
        var dialog = new OpenFileDialog {
            Title = "ReimsArm64AppleSysroot (GitHub Actions ZIP)",
            Filter = "GitHub Actions ZIP|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) Arm64SysrootArchive.Text = dialog.FileName;
    }

    private async void InstallArm64Compatibility(object sender, RoutedEventArgs e)
    {
        if (!RequireAction("InstallArm64Compat")) return;
        string runtime = Arm64RuntimeArchive.Text.Trim(), sysroot = Arm64SysrootArchive.Text.Trim();
        if (!File.Exists(runtime)) { ShowError("Maclator の GitHub Actions ZIP を選択してください。"); return; }
        if (!File.Exists(sysroot)) { ShowError("Apple Silicon dyld の ZIP を選択してください。"); return; }
        if (!Confirm("既存の SSH ペアリングを使用して ARM64 互換ランタイムをゲストのユーザーフォルダーへ導入します。Apple の利用条件を確認済みですか？")) return;
        Arm64InstallStatus.Text = "導入中。大容量の dyld キャッシュを転送しています。";
        bool success = await Operate("InstallArm64Compat", [
            "-RuntimeArchive", runtime, "-SysrootArchive", sysroot
        ]);
        Arm64InstallStatus.Text = success
            ? "導入が完了しました。macOS の Terminal で ~/bin/reims-arm64 を使用してください。実際の ARM64 アプリ起動は未検証です。"
            : "導入に失敗しました。エラーとログを確認してください。";
    }

    private async void CheckArm64Compatibility(object sender, RoutedEventArgs e)
    {
        if (!CanOperate("Arm64Status") || selectedEntry is not { } vm) return;
        string vmFile = vm.File;
        Arm64InstallStatus.Text = "macOS に問い合わせています…";
        try {
            string result = await Execute("Arm64Status", [], cancellation: statusCancellation.Token);
            if (!closing && selectedEntry?.File == vmFile)
                Arm64InstallStatus.Text = result.Contains("installed:", StringComparison.Ordinal)
                    ? "導入済み: ~/bin/reims-arm64（アプリ動作は別途検証してください）"
                    : "未導入。Maclator と Apple Silicon dyld の ZIP を選択してください。";
        } catch (OperationCanceledException) { /* VM changed; ignore stale status. */ }
        catch (Exception error) {
            if (!closing && selectedEntry?.File == vmFile) {
                Arm64InstallStatus.Text = "取得失敗: " + error.Message;
                ShowError(error.Message);
            }
        }
    }
}
