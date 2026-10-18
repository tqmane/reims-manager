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
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,127}$")) throw new InvalidDataException("エンジンの固定版情報を読み込めません。");
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
            Title="1 / 3  エンジンの準備";
            InstallButton.Content="ダウンロードして続ける";
            Status.Text="必要な部品を固定版から取得します。導入後、そのまま VM の作成に進みます。";
        }
        Closing += (_,e) => { if (installing) e.Cancel=true; };
        UpdateInstallLabel();
    }

    private void UpdateInstallLabel() => InstallButton.Content = Existing.IsChecked == true
        ? continueToVm ? "検証して続ける" : "検証して選択" : continueToVm ? "セットアップして続ける" : "セットアップ";

    private void BrowseDestination(object sender, RoutedEventArgs e)
    {
        var dialog=new OpenFolderDialog { Title=Existing.IsChecked == true ? "導入済みエンジンのフォルダー" : "エンジンの親フォルダーを選択" };
        if (dialog.ShowDialog(this)==true) Destination.Text=Existing.IsChecked == true ? dialog.FolderName : Path.Combine(dialog.FolderName,Path.GetFileName(DefaultDestination));
    }

    private void BrowseArchive(object sender, RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog { Title="CI またはリリースから取得した Reims エンジン", Filter="Reims エンジン|*.zip" };
        if (dialog.ShowDialog(this)==true) { Archive.Text=dialog.FileName; Local.IsChecked=true; }
    }

    private async void Install(object sender, RoutedEventArgs e)
    {
        if (installing) return;
        installing=true; Inputs.IsEnabled=false; InstallButton.IsEnabled=false; CloseButton.IsEnabled=false;
        Progress.Visibility=Visibility.Visible; Details.Clear();
        try {
            if (!Path.IsPathFullyQualified(Destination.Text.Trim())) throw new ArgumentException("エンジンの保存先を参照ボタンから選択するか、完全なパスを指定してください。");
            string destination=Path.GetFullPath(Destination.Text.Trim());
            if (Existing.IsChecked != true && (Directory.Exists(destination) || File.Exists(destination))) throw new IOException("既存のフォルダーには上書きしません。「再利用」を選択するか、新しい保存先を指定してください。");
            string script=Path.Combine(AppContext.BaseDirectory,"scripts/install-engine.ps1");
            string pwsh=Path.Combine(AppContext.BaseDirectory,"runtimes/pwsh/pwsh.exe");
            if (!File.Exists(pwsh)) pwsh=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PowerShell/7/pwsh.exe");
            var info=new ProcessStartInfo(pwsh) { UseShellExecute=false, CreateNoWindow=true,
                RedirectStandardOutput=true, RedirectStandardError=true, StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8 };
            if (Existing.IsChecked == true) {
                Status.Text="導入済みのエンジンを検証しています…";
                foreach (string arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(AppContext.BaseDirectory,"scripts/manager-backend.ps1"),
                    "-Action","GetPrerequisites","-Root",destination,"-ToolsRoot",Path.Combine(AppContext.BaseDirectory,"runtimes"),"-Cpus",Math.Min(4,Environment.ProcessorCount).ToString(),"-MemoryMiB","4096"}) info.ArgumentList.Add(arg);
                var requirements = SetupRequirements.Parse(await BackendCommand.Run(info, null, true));
                var engine = requirements.Checks.FirstOrDefault(check => check.Id == "engine");
                if (engine?.Ready != true) throw new IOException(engine?.Message ?? "対応エンジンの検証結果を確認できません。");
                InstalledDirectory=destination; installing=false; DialogResult=true; return;
            }
            foreach (string arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-Destination",destination}) info.ArgumentList.Add(arg);
            if (Local.IsChecked==true) {
                if (!File.Exists(Archive.Text)) throw new FileNotFoundException("エンジン ZIP を選択してください。");
                if (!File.Exists(Archive.Text+".sha256")) throw new FileNotFoundException("配布元の ZIP と同じ場所に「"+Path.GetFileName(Archive.Text)+".sha256」を保存してください。");
                info.ArgumentList.Add("-Archive"); info.ArgumentList.Add(Archive.Text);
            }
            Status.Text="準備しています…";
            using var process=Process.Start(info) ?? throw new IOException("セットアップを起動できませんでした。");
            var errors=process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line) {
                Status.Text=line; Details.AppendText(line+"\n"); Details.ScrollToEnd();
            }
            await process.WaitForExitAsync();
            if (process.ExitCode!=0) throw new IOException((await errors).Trim());
            InstalledDirectory=destination; installing=false; DialogResult=true;
        } catch (Exception error) {
            Status.Text="セットアップできませんでした。内容を確認して再試行できます。";
            Details.AppendText(error.Message+"\n"); Details.ScrollToEnd();
        } finally {
            installing=false; Inputs.IsEnabled=true; InstallButton.IsEnabled=true; CloseButton.IsEnabled=true; Progress.Visibility=Visibility.Collapsed;
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
