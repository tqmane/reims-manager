using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace ReimsManager;

public partial class EngineSetupWindow : Window
{
    private bool installing;
    public string? InstalledDirectory { get; private set; }

    public EngineSetupWindow()
    {
        InitializeComponent();
        Destination.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Reims","Engines","ReimsEngine");
        Closing += (_,e) => { if (installing) e.Cancel=true; };
    }

    private void BrowseDestination(object sender, RoutedEventArgs e)
    {
        var dialog=new OpenFolderDialog { Title="エンジンの親フォルダーを選択" };
        if (dialog.ShowDialog(this)==true) Destination.Text=Path.Combine(dialog.FolderName,"ReimsEngine");
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
            string destination=Path.GetFullPath(Destination.Text);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("既存のフォルダーには上書きしません。新しい保存先を指定してください。");
            string script=Path.Combine(AppContext.BaseDirectory,"scripts/install-engine.ps1");
            string pwsh=Path.Combine(AppContext.BaseDirectory,"runtimes/pwsh/pwsh.exe");
            if (!File.Exists(pwsh)) pwsh=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PowerShell/7/pwsh.exe");
            var info=new ProcessStartInfo(pwsh) { UseShellExecute=false, CreateNoWindow=true,
                RedirectStandardOutput=true, RedirectStandardError=true, StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8 };
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
