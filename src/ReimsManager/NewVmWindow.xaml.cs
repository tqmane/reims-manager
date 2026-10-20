using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ReimsManager;

internal sealed record NewVmRequest(VmEntry Vm, string Installer, int DiskGiB, int Cpus, int MemoryMiB, bool AutoStart);

public partial class NewVmWindow : Window
{
    private readonly Func<string, string[], bool, CancellationToken, Task<string>> backend;
    private readonly CancellationTokenSource lifetime = new();
    private SetupRequirements? requirements;
    private UsbDriverInstallResult? usbDriverInstallResult;
    private bool checking, changingSystem, closed, wantsStart = true;
    internal NewVmRequest? Request { get; private set; }

    internal NewVmWindow(Func<string, string[], bool, CancellationToken, Task<string>> call, bool checkOnLoad = true)
    {
        backend = call;
        InitializeComponent();
        ParentFolder.Text = VmLibrary.DefaultRoot;
        CpuCount.Text = Math.Min(4, Environment.ProcessorCount).ToString();
        CpuCount.TextChanged += PlanChanged; MemorySize.TextChanged += PlanChanged;
        StartAfterCreation.Click += (_, _) => wantsStart = StartAfterCreation.IsChecked == true;
        if (checkOnLoad) Loaded += async (_, _) => await ReadRequirements();
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; lifetime.Cancel(); };
    }

    private void PlanChanged(object sender, RoutedEventArgs e)
    {
        StartAfterCreation.IsEnabled = false; StartAfterCreation.IsChecked = false;
        MemoryPlan.Text = "割り当てを変更しました。「割り当てを再確認」で現在の空き RAM を確認できます。作成時にも再確認します。";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (changingSystem) { e.Cancel = true; Notice.Text = "Windows の設定変更が完了してから閉じてください。"; }
        else lifetime.Cancel();
    }

    private void UpdateBusy()
    {
        bool busy = checking || changingSystem;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RequirementsPage.IsEnabled = VmPage.IsEnabled = BackButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !changingSystem;
        NextButton.IsEnabled = CreateButton.IsEnabled = !busy && requirements?.Ready == true;
    }

    internal void ApplyRequirements(SetupRequirements value)
    {
        if (usbDriverInstallResult?.RestartRequired == true)
            value = value with { RestartRequired = true, StartReady = false };
        requirements = value;
        RequirementsList.ItemsSource = value.Checks;
        EnableWhpxButton.Visibility = value.Checks.Any(x => !x.Ready && x.Action == "EnableWhpx") ? Visibility.Visible : Visibility.Collapsed;
        InstallUsbButton.Visibility = value.Checks.Any(x => !x.Ready && x.Action == "InstallUsbDk") ? Visibility.Visible : Visibility.Collapsed;
        RequirementsSummary.Text = usbDriverInstallResult?.RestartRequired == true && value.Ready
            ? "UsbDk の適用には Windows の再起動が必要です。VM の作成は進められますが、自動起動は保留します。"
            : value.RestartRequired
            ? "Windows の再起動が必要です。作業を保存して再起動し、Manager でセットアップを再開してください。エンジンの保存先は記録済みです。"
            : value.Ready ? "必須の準備ができました。USB 転送など任意の機能は後から追加できます。" : "要対応の項目を確認して、再確認してください。VM はまだ作成していません。";
        MemoryPlan.Text = $"割り当て: {value.Cpus} vCPU / {value.MemoryMiB} MiB。Windows の空き RAM: {value.AvailableMemoryMiB?.ToString() ?? "未確認"} MiB。" +
            (value.StartReady ? "" : " 現在は自動起動を保留します。VM の作成はできます。");
        StartAfterCreation.IsEnabled = value.StartReady;
        StartAfterCreation.IsChecked = value.StartReady && wantsStart;
        if (usbDriverInstallResult is not null) Notice.Text = UsbDriverNotice;
        UpdateBusy();
    }

    private string UsbDriverNotice => usbDriverInstallResult?.RestartRequired == true
        ? "UsbDk の導入は完了しました。適用には Windows の再起動が必要です。作業を保存して Windows を再起動してください。自動起動は保留します。再起動後、VM の USB 設定で UsbDk を選んで保存してください。"
        : usbDriverInstallResult is not null
            ? "UsbDk を導入しました。VM の USB 設定で UsbDk を選んで保存し、VM を完全終了してから起動してください。"
            : "";

    private bool ValidatePlan(out int cpus, out int memory)
    {
        bool valid = ManagerValidation.Integer(CpuCount.Text, 1, Math.Min(64, Environment.ProcessorCount), 1, out cpus);
        valid &= ManagerValidation.Integer(MemorySize.Text, 4096, 131072, 4, out memory);
        if (!valid) Notice.Text = "CPU 数はホストの範囲内、メモリは 4096 MiB 以上・4 MiB 単位で指定してください。";
        return valid;
    }

    internal async Task<bool> ReadRequirements()
    {
        if (checking || changingSystem || closed || !ValidatePlan(out int cpus, out int memory)) return false;
        checking = true; Request = null;
        RequirementsSummary.Text = Notice.Text = "必要な機能と空きメモリを確認しています…";
        UpdateBusy();
        try {
            string json = await backend("GetPrerequisites", ["-Cpus", cpus.ToString(), "-MemoryMiB", memory.ToString()], false, lifetime.Token);
            if (closed) return false;
            ApplyRequirements(SetupRequirements.Parse(json)); Notice.Text = UsbDriverNotice;
            return requirements!.Ready;
        } catch (Exception error) {
            if (!closed) {
                requirements = null; Request = null;
                RequirementsList.ItemsSource = null;
                EnableWhpxButton.Visibility = InstallUsbButton.Visibility = Visibility.Collapsed;
                StartAfterCreation.IsEnabled = false; StartAfterCreation.IsChecked = false;
                MemoryPlan.Text = "CPU と空きメモリを確認できていません。再確認が完了するまで自動起動を保留します。";
                RequirementsSummary.Text = error is OperationCanceledException
                    ? "必要条件の確認を中断しました。「再確認」でやり直してください。"
                    : "Windows の準備を確認できませんでした。「再確認」でやり直してください。";
                Notice.Text = (error is OperationCanceledException ? "必要条件の確認を中断しました。" : "必要条件の確認結果を取得・読み取りできませんでした。") +
                    "\n詳細: " + error.Message +
                    (usbDriverInstallResult?.RestartRequired == true ? "\n" + UsbDriverNotice : "");
            }
            return false;
        } finally { checking = false; UpdateBusy(); }
    }

    private async void RepairRequirement(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action }) await RepairRequirementCore(action);
    }

    internal async Task<bool> RepairRequirementCore(string action)
    {
        if (checking || changingSystem || closed || action is not ("EnableWhpx" or "InstallUsbDk") ||
            requirements?.Checks.Any(x => !x.Ready && x.Action == action) != true) return false;
        changingSystem = true; Notice.Text = "Windows の確認画面で管理者権限を許可してください。自動再起動は行いません。"; UpdateBusy();
        try {
            string response = await backend(action, [], true, CancellationToken.None);
            if (action == "InstallUsbDk") {
                var installed = UsbDriverInstallResult.Parse(response);
                // A later probe or reinstall cannot prove that Windows restarted.
                if (usbDriverInstallResult?.RestartRequired != true) usbDriverInstallResult = installed;
            }
        }
        catch (Exception error) {
            Notice.Text = error.Message + (usbDriverInstallResult?.RestartRequired == true ? "\n" + UsbDriverNotice : "");
            return false;
        }
        finally { changingSystem = false; UpdateBusy(); }
        await ReadRequirements();
        return true;
    }

    private async void RefreshRequirementsClick(object sender, RoutedEventArgs e) => await ReadRequirements();
    private void ChangePage(bool vm)
    {
        RequirementsPage.Visibility = vm ? Visibility.Collapsed : Visibility.Visible;
        VmPage.Visibility = CreateButton.Visibility = BackButton.Visibility = vm ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = vm ? Visibility.Collapsed : Visibility.Visible;
        StepTitle.Text = vm ? "3 / 3  仮想マシンを作成" : "2 / 3  Windows の準備";
        StepHint.Text = vm ? "保存先とインストーラーを選びます。作成前に必要条件とメモリをもう一度確認します。" : "必須の項目を確認します。USB・ブリッジ接続の準備は後からでも行えます。";
    }
    private void Next(object sender, RoutedEventArgs e) { if (!checking && requirements?.Ready == true) ChangePage(true); }
    private async void Back(object sender, RoutedEventArgs e) { ChangePage(false); await ReadRequirements(); }
    private void Cancel(object sender, RoutedEventArgs e) => Close();

    private void BrowseInstaller(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "macOS インストーラー|*.iso;*.img", Title = "macOS のインストーラーを選択" };
        if (dialog.ShowDialog(this) == true) Installer.Text = dialog.FileName;
    }
    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "VM を保存する親フォルダー" };
        if (dialog.ShowDialog(this) == true) ParentFolder.Text = dialog.FolderName;
    }
    private void VmLocationChanged(object sender, RoutedEventArgs e)
    {
        if (LocationPreview is null || ParentFolder is null || VmName is null) return;
        try { LocationPreview.Text = "VM の保存先: " + VmLibrary.PlanCreate(ParentFolder.Text, VmName.Text.Trim()).Directory; }
        catch (ArgumentException) { LocationPreview.Text = "VM の名前と保存先を確認してください。"; }
    }
    private async void Create(object sender, RoutedEventArgs e) { if (await TryCreateRequest()) DialogResult = true; }

    internal async Task<bool> TryCreateRequest()
    {
        Request = null;
        if (checking || changingSystem || !ValidatePlan(out int cpus, out int memory)) return false;
        try {
            var entry = VmLibrary.PlanCreate(ParentFolder.Text, VmName.Text.Trim());
            string installer = Installer.Text.Trim();
            if (!Path.IsPathFullyQualified(installer) || !File.Exists(installer) || Path.GetExtension(installer).ToLowerInvariant() is not (".iso" or ".img"))
                throw new IOException("macOS の ISO / IMG を参照ボタンから選択するか、完全なパスを指定してください。");
            if (!ManagerValidation.Integer(Capacity.Text, 64, 2048, 1, out int disk)) throw new ArgumentException("ディスク容量は 64〜2048 GiB の整数で指定してください。");
            if (File.Exists(entry.File) || Directory.Exists(entry.Directory) && Directory.EnumerateFileSystemEntries(entry.Directory).Any())
                throw new IOException("この保存先にはデータがあります。別の名前または親フォルダーを選んでください。");
            if (!await ReadRequirements()) { ChangePage(false); return false; }
            if (requirements!.Cpus != cpus || requirements.MemoryMiB != memory)
                throw new IOException("確認した割り当てが一致しません。再確認してください。");
            Request = new(entry, Path.GetFullPath(Installer.Text.Trim()), disk, requirements!.Cpus, requirements.MemoryMiB,
                requirements.StartReady && StartAfterCreation.IsChecked == true);
            return true;
        } catch (Exception error) { Notice.Text = error.Message; return false; }
    }
}
