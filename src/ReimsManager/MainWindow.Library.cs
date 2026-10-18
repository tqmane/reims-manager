using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ReimsManager;

public partial class MainWindow
{
    private readonly List<VmEntry> machines = [];
    private bool consoleDetached;
    private string VmDirectory => selectedEntry?.Directory ?? Path.Combine(ManagerSettings.DataDirectory, "Setup");

    private void LoadLibrary()
    {
        machines.Clear();
        foreach (string path in settings.VirtualMachines.Distinct(StringComparer.OrdinalIgnoreCase)) {
            var entry = VmLibrary.Registered(path);
            machines.Add(entry);
            if (!entry.IsAvailable) AppendLog($"VM を読み込めません: {path}\n{entry.Problem}\n");
        }
        UpdateLibraryView();
        if (machines.Count > 0) Library.SelectedItem = machines.FirstOrDefault(x => string.Equals(x.File, settings.SelectedVm, StringComparison.OrdinalIgnoreCase)) ?? machines[0];
        else if (selectedEntry is not null) { selectedEntry = null; ResetVmContext(); }
        // Disconnected drives and malformed descriptors must not disappear on the next save.
        UpdatePowerControls();
    }

    private bool AddVm(VmEntry entry)
    {
        int existing = machines.FindIndex(x => string.Equals(x.File, entry.File, StringComparison.OrdinalIgnoreCase));
        if (existing < 0) machines.Add(entry); else machines[existing] = entry;
        settings.VirtualMachines = settings.VirtualMachines.Append(entry.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool saved = SavePreferences();
        LibrarySearch.Clear();
        UpdateLibraryView();
        Library.SelectedItem = machines.First(x => string.Equals(x.File, entry.File, StringComparison.OrdinalIgnoreCase));
        return saved;
    }

    private async void VmChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs is null || restoringSelection || e.Source != Library || Library.SelectedItem is not VmEntry entry || entry == selectedEntry) return;
        if (IsUiBusy || !DiscardPendingChanges()) {
            restoringSelection = true;
            try { Library.SelectedItem = selectedEntry; }
            finally { restoringSelection = false; }
            return;
        }
        selectedEntry = entry;
        ResetVmContext();
        VmTitle.Text = entry.Name; VmLocation.Text = entry.Directory;
        settings.SelectedVm = entry.File;
        SavePreferences();
        if (!selfTest) {
            await RefreshState();
            if (entry.IsAvailable && entry == selectedEntry && !closing && HasEngine && currentState != "unprepared") await ReadIdentity();
        }
    }

    internal void OpenVmFile(string file)
    {
        if (IsUiBusy) return;
        try {
            var entry = VmLibrary.Open(file);
            if (!DiscardPendingChanges()) return;
            hardwareDirty = identityDirty = false;
            AddVm(entry);
        } catch (Exception error) { ShowError(error.Message); }
    }

    private void OpenVm(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy) return;
        var dialog = new OpenFileDialog { Filter = "Reims 仮想マシン|*.reimsvm", Title = "保存済みの VM を開く" };
        if (dialog.ShowDialog(this) == true) OpenVmFile(dialog.FileName);
    }

    private async void ReloadLibrary(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !DiscardPendingChanges()) return;
        hardwareDirty = identityDirty = false;
        var saved = ManagerSettings.Load(out var warning);
        if (saved.EngineRoot != settings.EngineRoot || saved.QemuExecutable != settings.QemuExecutable) {
            if (!HasVm || currentState is "stopped" or "unprepared") {
                settings.EngineRoot = saved.EngineRoot; settings.QemuExecutable = saved.QemuExecutable;
                Root.Text = settings.EngineRoot; ResetVmContext();
            } else ShowError("別のウィンドウでエンジンの設定が変わりました。現在の接続を維持しています。VM の完全終了後にライブラリを更新すると反映します。");
        }
        settings.VirtualMachines = settings.VirtualMachines.Concat(saved.VirtualMachines).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LoadLibrary();
        if (warning is not null) ShowError(warning);
        await RefreshState();
    }

    private void OpenPreferencesFolder(object sender, RoutedEventArgs e)
    {
        try {
            Directory.CreateDirectory(ManagerSettings.DataDirectory);
            Process.Start(new ProcessStartInfo(ManagerSettings.DataDirectory) { UseShellExecute = true });
        } catch (Exception error) { ShowError(error.Message); }
    }

    private async void ImportPreferences(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !DiscardPendingChanges()) return;
        var dialog = new OpenFileDialog { Filter = "Reims Manager の設定|*.json", FileName = "settings.json", Title = "以前のアプリ設定を取り込む" };
        if (dialog.ShowDialog(this) != true) return;
        try {
            settings.Import(dialog.FileName);
            hardwareDirty = identityDirty = false;
            Root.Text = settings.EngineRoot;
            LoadLibrary();
            await RefreshState();
        } catch (Exception error) { ShowError(error.Message); }
    }

    private VmEntry? CreateVm()
    {
        if (!HasEngine) { ShowError("先に Reims エンジンを設定してください。VM ファイルはまだ作成していません。"); return null; }
        var dialog = new SaveFileDialog { Filter = "Reims 仮想マシン|*.reimsvm", DefaultExt = ".reimsvm", AddExtension = true,
            FileName = "macOS.reimsvm", Title = "新しい VM の名前と保存先", OverwritePrompt = true,
            InitialDirectory = Directory.Exists(VmLibrary.DefaultRoot) ? VmLibrary.DefaultRoot : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true || !DiscardPendingChanges()) return null;
        string directory = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName) + ".data");
        try {
            var entry = VmLibrary.Create(dialog.FileName, directory);
            hardwareDirty = identityDirty = false;
            return AddVm(entry) ? entry : null;
        } catch (Exception error) { ShowError(error.Message); return null; }
    }

    private async void NewVm(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !DiscardPendingChanges()) return;
        NewVmRequest? request = null;
        busy = true; UpdatePowerControls();
        try {
            if (!HasEngine && !SelectSetupEngine(continueToVm: true)) return;
            var wizard = new NewVmWindow((action, arguments, elevate, cancellation) =>
                Execute(action, arguments, elevate, cancellation: cancellation)) { Owner = this };
            if (wizard.ShowDialog() == true) request = wizard.Request;
        } catch (Exception error) { ShowError(error.Message); }
        finally { busy = false; UpdatePowerControls(); }
        if (request is null) return;
        try {
            var entry = VmLibrary.Create(request.Vm.File, request.Vm.Directory);
            hardwareDirty = identityDirty = false;
            if (!AddVm(entry)) return;
            Iso.Text=request.Installer; NewDiskSize.Text=request.DiskGiB.ToString();
            Cpus.Text=request.Cpus.ToString(); Memory.Text=request.MemoryMiB.ToString();
            await RefreshState();
            if (entry != selectedEntry) return;
            if (!await Operate("Setup", ["-Iso", request.Installer, "-DiskGiB", request.DiskGiB.ToString(),
                "-Cpus", request.Cpus.ToString(), "-MemoryMiB", request.MemoryMiB.ToString()])) return;
            ConnectIso.IsChecked = true;
            if (request.AutoStart) await StartPreparedVm(entry, request.Cpus, request.MemoryMiB);
            else { StatusDetail.Text = "VM の準備ができました。上の「起動」から macOS のインストールを始められます。"; UpdatePowerControls(); }
        } catch (Exception error) { ShowError(error.Message); }
    }

    private async Task StartPreparedVm(VmEntry entry, int cpus, int memory)
    {
        long version = contextVersion;
        busy = true; UpdatePowerControls();
        SetupRequirements? requirements = null;
        try {
            requirements = SetupRequirements.Parse(await Execute("GetPrerequisites", ["-Cpus", cpus.ToString(), "-MemoryMiB", memory.ToString()], cancellation: statusCancellation.Token));
        } catch (Exception error) { ShowError("自動起動を保留しました。必要条件を確認してから起動してください。\n" + error.Message); }
        finally { busy = false; UpdatePowerControls(); }
        if (version != contextVersion || entry != selectedEntry || closing) return;
        if (requirements?.StartReady == true) await Operate("Start");
        else { StatusDetail.Text = "VM の準備は完了しました。自動起動は保留しています。空き RAM と Windows の再起動要否を確認してください。"; UpdatePowerControls(); }
    }

    private async void ImportVm(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !DiscardPendingChanges()) return;
        if (!HasEngine) {
            busy=true; UpdatePowerControls();
            try { if (!SelectSetupEngine()) return; }
            catch (Exception error) { ShowError(error.Message); return; }
            finally { busy=false; UpdatePowerControls(); }
        }
        var disk = new OpenFileDialog { Filter = "仮想ディスク|*.vmdk;*.qcow2;*.img;*.raw", Title = "完全に停止した VM のディスクを選択（元データは保持）" };
        if (disk.ShowDialog(this) != true || CreateVm() is not { } entry) return;
        await RefreshState();
        if (entry != selectedEntry) return;
        if (await Operate("ImportDisk", ["-DiskPath", disk.FileName])) { await ReadIdentity(); Tabs.SelectedIndex = 0; }
    }

    private void RevealVm(object sender, RoutedEventArgs e)
    {
        if (selectedEntry is null) return;
        string? folder = Directory.Exists(VmDirectory) ? VmDirectory : Path.GetDirectoryName(selectedEntry!.File);
        try {
            if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            else ShowError("VM の保存先が見つかりません。ドライブの接続を確認してください。");
        } catch (Exception error) { ShowError(error.Message); }
    }

    private void ShowPowerMenu(object sender, RoutedEventArgs e) { var menu = ((Button)sender).ContextMenu; menu.PlacementTarget = (Button)sender; menu.IsOpen = true; }
    private void PrepareStartConsole()
    {
        consoleDetached = false; presentation.ShowConsole(); Tabs.SelectedIndex = 0;
    }
    private async void MenuAction(object sender, RoutedEventArgs e) => await Operate(((MenuItem)sender).Tag.ToString()!);
    private void OpenSoundSettings(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
    private void PopOutConsole(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !RequireAction("Console") || !TryCloseSettings()) return;
        consoleDetached = true; ConsoleView.Detach(); ConsoleView.Visibility = Visibility.Hidden;
        ConsolePlaceholder.Visibility = Visibility.Visible; UpdatePowerControls();
    }
    private void DockConsole(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !RequireAction("Console") || !TryCloseSettings()) return;
        consoleDetached = false; presentation.ShowConsole(); Tabs.SelectedIndex = 0;
        UpdatePowerControls(); UpdateLayout(); UpdateConsole();
    }
    private void TabChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == Tabs && ConsoleView is not null) { SyncNavigation(); UpdateConsole(); UpdatePowerControls(); } }

    private void UpdateConsole()
    {
        if (SettingsOpen || consoleDetached) { ConsoleView.Visibility = Visibility.Hidden; return; }
        if (selfTest && !useFixtureConsole || latest.ValueKind != JsonValueKind.Object) return;
        if (currentState is "shutdown" or "stopped" or "unprepared") {
            ConsoleView.Visibility = Visibility.Hidden; ConsolePlaceholder.Visibility = Visibility.Visible; return;
        }
        // Never attach a new child from stale metadata after an unknown status result.
        if (currentState == "unknown") {
            ConsoleView.Visibility = ConsoleView.Attached ? Visibility.Visible : Visibility.Hidden;
            ConsolePlaceholder.Visibility = ConsoleView.Attached ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        if (latest.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object) {
            try {
                ConsoleView.Visibility = Visibility.Visible;
                if (ConsoleView.Attach(runtime.GetProperty("pid").GetInt32(), runtime.GetProperty("start_ticks").GetInt64(), runtime.GetProperty("executable").GetString()!)) {
                    ConsolePlaceholder.Visibility = Visibility.Collapsed; return;
                }
            } catch (Exception error) { AppendLog(error.Message + "\n"); }
        }
        ConsoleView.Visibility = Visibility.Hidden; ConsolePlaceholder.Visibility = Visibility.Visible;
    }
}
