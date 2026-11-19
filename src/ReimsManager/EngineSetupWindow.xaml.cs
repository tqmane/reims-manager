using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace ReimsManager;

public partial class EngineSetupWindow : Window
{
    private bool installing;
    private readonly bool continueToVm;
    public string? InstalledDirectory { get; private set; }

    internal static string DefaultDestination {
        get {
            using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts/engine-lock.json")));
            string id = pin.RootElement.GetProperty("id").GetString() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,127}$")) throw new InvalidDataException(UiText.T("エンジンの固定版情報を読み込めません。"));
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reims", "Engines", id);
        }
    }

    public EngineSetupWindow(bool continueToVm = false)
    {
        this.continueToVm = continueToVm;
        InitializeComponent();
        Destination.Text=DefaultDestination;
        Existing.Checked += (_, _) => UpdateInstallLabel();
        Online.Checked += (_, _) => UpdateInstallLabel();
        Local.Checked += (_, _) => UpdateInstallLabel();
        if (Directory.Exists(DefaultDestination)) Existing.IsChecked = true;
        if (continueToVm) {
            Title=UiText.T("1 / 3  エンジンの準備");
            InstallButton.Content=UiText.T("ダウンロードして続ける");
            Status.Text=UiText.T("必要な部品を固定版から取得します。導入後、そのまま VM の作成に進みます。");
        }
        Closing += (_,e) => { if (installing) e.Cancel=true; };
        UpdateInstallLabel();
    }

    private void UpdateInstallLabel() => InstallButton.Content = Existing.IsChecked == true
        ? continueToVm ? UiText.T("検証して続ける") : UiText.T("検証して選択") : continueToVm ? UiText.T("セットアップして続ける") : UiText.T("セットアップ");

    private void BrowseDestination(object sender, RoutedEventArgs e)
    {
        var dialog=new OpenFolderDialog { Title=Existing.IsChecked == true ? UiText.T("導入済みエンジンのフォルダー") : UiText.T("エンジンの親フォルダーを選択") };
        if (dialog.ShowDialog(this)==true) Destination.Text=Existing.IsChecked == true ? dialog.FolderName : Path.Combine(dialog.FolderName,Path.GetFileName(DefaultDestination));
    }

    private void BrowseArchive(object sender, RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog { Title=UiText.T("CI またはリリースから取得した Reims エンジン"), Filter=UiText.T("Reims エンジン|*.zip") };
        if (dialog.ShowDialog(this)==true) { Archive.Text=dialog.FileName; Local.IsChecked=true; }
    }

    private async void Install(object sender, RoutedEventArgs e)
    {
        if (installing) return;
        installing=true; Inputs.IsEnabled=false; InstallButton.IsEnabled=false; CloseButton.IsEnabled=false;
        Progress.Visibility=Visibility.Visible; Details.Clear();
        try {
            if (!Path.IsPathFullyQualified(Destination.Text.Trim())) throw new ArgumentException(UiText.T("エンジンの保存先を参照ボタンから選択するか、完全なパスを指定してください。"));
            string destination=Path.GetFullPath(Destination.Text.Trim());
            if (Existing.IsChecked != true && (Directory.Exists(destination) || File.Exists(destination))) throw new IOException(UiText.T("既存のフォルダーには上書きしません。「再利用」を選択するか、新しい保存先を指定してください。"));
            if (Existing.IsChecked == true) {
                Status.Text=UiText.T("導入済みのエンジンを検証しています…");
                string prereqJson = await ReimsManager.Services.NativeStatusService.GetPrerequisitesJsonAsync(
                    destination, Path.Combine(AppContext.BaseDirectory, "runtimes"), Math.Min(4, Environment.ProcessorCount), 4096, CancellationToken.None);
                var requirements = SetupRequirements.Parse(prereqJson);
                var engine = requirements.Checks.FirstOrDefault(check => check.Id == "engine");
                if (engine?.Ready != true) throw new IOException(engine?.Message ?? UiText.T("対応エンジンの検証結果を確認できません。"));
                InstalledDirectory=destination; installing=false; DialogResult=true; return;
            }
            string? archivePath = null;
            if (Local.IsChecked == true) {
                if (!File.Exists(Archive.Text)) throw new FileNotFoundException(UiText.T("エンジン ZIP を選択してください。"));
                if (!File.Exists(Archive.Text + ".sha256")) throw new FileNotFoundException(UiText.T("配布元の ZIP と同じ場所に「") + Path.GetFileName(Archive.Text) + UiText.T(".sha256」を保存してください。"));
                archivePath = Archive.Text;
            }
            Status.Text = UiText.T("準備しています…");
            string lastMsg = "";
            await ReimsManager.Services.NativeEngineService.InstallEngineAsync(
                destination,
                archivePath,
                null,
                (msg, pct) => Dispatcher.Invoke(() => {
                    if (pct is double p) {
                        Progress.IsIndeterminate = false;
                        Progress.Value = p;
                    } else {
                        Progress.IsIndeterminate = true;
                    }
                    Status.Text = msg;
                    if (!string.Equals(msg, lastMsg, StringComparison.Ordinal) && !msg.StartsWith(UiText.T("ダウンロード中") + ":", StringComparison.Ordinal)) {
                        lastMsg = msg;
                        Details.AppendText(msg + "\n");
                        Details.ScrollToEnd();
                    }
                }));
            InstalledDirectory = destination; installing = false; DialogResult = true;
        } catch (Exception error) {
            Status.Text=UiText.T("セットアップできませんでした。内容を確認して再試行できます。");
            Details.AppendText(error.Message+"\n"); Details.ScrollToEnd();
        } finally {
            installing=false; Inputs.IsEnabled=true; InstallButton.IsEnabled=true; CloseButton.IsEnabled=true; Progress.Visibility=Visibility.Collapsed;
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
