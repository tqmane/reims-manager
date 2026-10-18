using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ReimsManager;

public partial class MainWindow : Window
{
    private bool busy, refreshing, loadedHardware;
    private readonly ManagerSettings settings;
    private string currentState = "unprepared";
    private bool HasVm => selectedEntry?.IsAvailable == true;
    private bool HasEngine => Directory.Exists(Root.Text) && File.Exists(settings.QemuExecutable);
    private JsonElement latest;
    private readonly DispatcherTimer timer = new() { Interval=TimeSpan.FromSeconds(5) };
    private readonly bool selfTest;

    public MainWindow(bool test = false)
    {
        selfTest = test;
        string? warning = null;
        settings = test ? new ManagerSettings() : ManagerSettings.Load(out warning);
        InitializeComponent();
        InitializeWorkspace();
        clipboardTimer.Tick += async (_,_) => await SyncClipboard();
        Root.Text = settings.EngineRoot;
        PreferencesLocation.Text = ManagerSettings.SettingsFile;
        UpdatePowerControls();
        Devices.SelectedIndex = 0;
        if (warning is not null) ShowError(warning);
        Loaded += async (_,_) => { if (!selfTest) { LoadLibrary(); await RefreshState(); timer.Start(); } };
        timer.Tick += async (_,_) => { if (!busy) await RefreshState(false); };
        Closed += (_,_) => { timer.Stop(); clipboardTimer.Stop(); clipboardCancel.Cancel(); statusCancellation.Cancel(); ConsoleView.Detach(); };
    }

    private async Task<string> Execute(string action, string[] arguments, bool elevate = false, string? input = null, CancellationToken cancellation = default)
    {
        if (selfTest && controlFixtureBackend is not null) return await controlFixtureBackend(action);
        if (!HasEngine) throw new InvalidOperationException("先に Reims エンジンを選択してください。");
        if (!HasVm && action is not ("GetPrerequisites" or "EnableWhpx" or "InstallUsbDk" or "InstallTap" or "CreateBridge"))
            throw new InvalidOperationException("操作する仮想マシンを選択してください。");
        string root = Path.GetFullPath(Root.Text);
        string backend = Path.Combine(AppContext.BaseDirectory,"scripts/manager-backend.ps1");
        if (!File.Exists(backend)) backend = Path.Combine(root,"scripts/windows/manager-backend.ps1");
        if (!File.Exists(backend)) throw new Exception("管理スクリプトが見つかりません。Reims のプロジェクトを選択してください。");
        string pwsh = Path.Combine(AppContext.BaseDirectory,"runtimes/pwsh/pwsh.exe");
        if (!File.Exists(pwsh)) pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PowerShell/7/pwsh.exe");
        if (!File.Exists(pwsh)) throw new Exception("PowerShell 7 をインストールしてください。");
        var info = new ProcessStartInfo(pwsh) { UseShellExecute=elevate, CreateNoWindow=true, WorkingDirectory=root };
        foreach (string arg in new[] {"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",backend,"-Root",root,"-Action",action}.Concat(arguments)) info.ArgumentList.Add(arg);
        if (HasVm && action is not ("GetPrerequisites" or "EnableWhpx" or "InstallUsbDk")) { info.ArgumentList.Add("-VmDirectory"); info.ArgumentList.Add(VmDirectory); }
        info.ArgumentList.Add("-Qemu"); info.ArgumentList.Add(settings.QemuExecutable);
        info.ArgumentList.Add("-ToolsRoot"); info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"runtimes"));
        if (input is not null) info.RedirectStandardInput=true;
        if (elevate) { info.Verb="runas"; info.WindowStyle=ProcessWindowStyle.Hidden; }
        else { info.RedirectStandardOutput=true; info.RedirectStandardError=true; info.StandardOutputEncoding=Encoding.UTF8; info.StandardErrorEncoding=Encoding.UTF8; }
        bool readOnly = action is "Status" or "GetIdentity" or "ListUsb" or "GetPrerequisites";
        return await BackendCommand.Run(info, input, readOnly, readOnly ? cancellation : CancellationToken.None);
    }

    private async Task<bool> Operate(string action, string[]? args = null, bool elevate = false, string? input = null)
    {
        if (!RequireAction(action)) return false;
        if (action == "SnapshotRestore" && !DiscardPendingChanges()) return false;
        // Menus, keyboard shortcuts and buttons use exactly the same installer preference.
        if (action == "Start") { args = VmActionPolicy.StartArguments(ConnectIso.IsChecked == true); PrepareStartConsole(); }
        InvalidateReadRequests();
        busy = true; Tabs.IsEnabled = false;
        ErrorNotice.Text = ""; Feedback.Visibility = Visibility.Collapsed;
        OperationStatus.Text = VmActionPolicy.Label(action) + "しています…";
        OperationProgress.Visibility = Visibility.Visible;
        UpdatePowerControls();
        bool success = false;
        try {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {VmActionPolicy.Label(action)} — {selectedEntry?.Name}\n");
            AppendLog(await Execute(action, args ?? [], elevate, input) + "\n");
            success = true;
            if (action == "SetHardware") hardwareDirty = false;
            if (action is "SetIdentity" or "GenerateIdentity") identityDirty = false;
            if (action is "SetHardware" or "Setup" or "ImportDisk" or "SnapshotRestore" or "ExpandDisk") loadedHardware = false;
            if (action == "SnapshotRestore") hardwareDirty = identityDirty = false;
        } catch (Exception error) { AppendLog(error.Message + "\n"); ShowError(error.Message); }
        finally {
            busy = false; currentState = "unknown"; Tabs.IsEnabled = true;
            OperationProgress.Visibility = Visibility.Collapsed; UpdatePowerControls();
        }
        await RefreshState();
        return success;
    }

    private static string Selected(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? item.Content.ToString()! : combo.SelectedItem?.ToString()??"";
    private static void Fill(ComboBox combo, IEnumerable<string> values, bool preserveMissing = false)
    {
        string previous = Selected(combo);
        var choices = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (preserveMissing && previous.Length > 0 && !choices.Contains(previous, StringComparer.OrdinalIgnoreCase)) choices.Add(previous);
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(x => string.Equals(x, previous, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshState(bool full = true)
    {
        if (busy || closing) return;
        refreshPending = true; refreshFull |= full;
        if (refreshing) { await refreshCompletion!.Task; return; }
        refreshing = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshCompletion = completion;
        try {
            while (refreshPending && !busy && !closing) {
                refreshPending = false;
                bool loadDetails = refreshFull || !loadedHardware || latest.ValueKind != JsonValueKind.Object || selfTest && controlFixtureBackend is not null;
                refreshFull = false;
                if (!HasVm || !HasEngine) {
                    currentState = "unprepared";
                    State.Text = selectedEntry?.IsAvailable == false ? "保存先を確認" : !HasVm ? "未選択" : "エンジン未設定";
                    StatusDetail.Text = selectedEntry?.IsAvailable == false ? selectedEntry.Problem! : !HasEngine ? "Reims エンジンを設定してセットアップを始めます。" : "VM を作成するか、既存の VM を開いてください。";
                    UpdatePowerControls(); continue;
                }
                long version = contextVersion;
                string requestedVm = VmDirectory;
                var cached = latest.ValueKind == JsonValueKind.Object ? latest.Clone() : default;
                var token = statusCancellation.Token;
                try {
                    JsonElement response;
                    if (!loadDetails) {
                        var live = await LiveVmStatus.Read(requestedVm, token);
                        if (version != contextVersion || busy || closing) continue;
                        if (cached.GetProperty("prepared").GetBoolean() == (live.State == "unprepared")) {
                            refreshPending = refreshFull = true; continue;
                        }
                        var values = JsonNode.Parse(cached.GetRawText())!.AsObject();
                        values["state"] = live.State;
                        values["runtime"] = live.Runtime is { } runtime ? JsonNode.Parse(runtime.GetRawText()) : null;
                        response = JsonSerializer.SerializeToElement(values);
                    } else {
                        using var document = JsonDocument.Parse(await Execute("Status", [], cancellation: token));
                        response = document.RootElement.Clone();
                    }
                    if (version != contextVersion || busy || closing) continue;
                    latest = response;
                    currentState = latest.GetProperty("state").GetString() ?? "unknown";
                    State.Text = VmActionPolicy.StateLabel(currentState);
                    StatusDetail.Text = currentState switch {
                        "shutdown" => "macOS は電源オフです。設定変更には「••• → 完全終了」で QEMU も終了してください。",
                        "paused" or "suspended" or "prelaunch" => "「再開」してから macOS の電源操作を行えます。",
                        "unprepared" => "「仮想マシンの設定 → macOS のインストール」から初期セットアップを行ってください。",
                        _ => ""
                    };
                    if (loadDetails) {
                        loadingHardware = true;
                        try {
                            Fill(Snapshots, latest.GetProperty("snapshots").EnumerateArray().Select(x => x.GetString()!));
                            string[] taps = latest.GetProperty("taps").EnumerateArray().Select(x => x.GetString()!).ToArray();
                            Fill(Tap, taps, true); Fill(BridgeTap, taps, true);
                            Fill(Physical, latest.GetProperty("adapters").EnumerateArray().Where(x => x.TryGetProperty("HardwareInterface", out var value) && value.ValueKind == JsonValueKind.True).Select(x => x.GetProperty("Name").GetString()!), true);
                        } finally { loadingHardware = false; }
                        if (!loadedHardware && !hardwareDirty) {
                            if (latest.GetProperty("prepared").GetBoolean()) LoadHardware();
                            loadedHardware = true;
                        }
                        VmSummary.Text = latest.GetProperty("prepared").GetBoolean()
                            ? $"{latest.GetProperty("cpus")} vCPU  ·  {latest.GetProperty("memory_mib").GetDouble() / 1024:0.###} GiB メモリ  ·  {latest.GetProperty("disk_gib")} GiB ディスク  ·  {latest.GetProperty("resolution").GetString()}"
                            : "仮想ディスクはまだ準備されていません";
                    }
                    UpdateConsole(); UpdatePowerControls();
                } catch (Exception error) {
                    if (version != contextVersion || busy || closing) continue;
                    currentState = "unknown";
                    State.Text = VmActionPolicy.StateLabel(currentState);
                    StatusDetail.Text = error is OperationCanceledException ? "状態取得がタイムアウトしました。「更新」で再試行できます。" : error.Message;
                    UpdatePowerControls();
                }
            }
        } finally { refreshing = false; completion.TrySetResult(); UpdatePowerControls(); }
    }

    private void LoadHardware()
    {
        if (latest.ValueKind != JsonValueKind.Object) return;
        loadingHardware = true;
        try {
        Memory.Text=latest.GetProperty("memory_mib").ToString();
        Cpus.Text=latest.GetProperty("cpus").ToString(); DiskSize.Text=latest.GetProperty("disk_gib").ToString();
        DiskInfo.Text=$"最大容量: {DiskSize.Text} GiB\n{latest.GetProperty("disk").GetString()}";
        Iso.Text=HardwareIsoPath.Text=latest.GetProperty("iso").GetString()??"";
        ConnectIso.IsChecked=HardwareIso.IsChecked=latest.GetProperty("with_installer").GetBoolean();
        Nat.IsChecked=latest.GetProperty("network").GetString()=="nat"; Bridge.IsChecked=!Nat.IsChecked;
        string adapter = latest.GetProperty("network_adapter").GetString() ?? "";
        if (adapter.Length > 0 && !Tap.Items.Cast<string>().Contains(adapter)) Fill(Tap, Tap.Items.Cast<string>().Append(adapter));
        Tap.SelectedItem=adapter; GuestHost.Text=latest.GetProperty("guest_host").GetString()??"";
        Sound.IsChecked=latest.GetProperty("audio").GetString()=="dsound";
        Microphone.IsChecked=latest.TryGetProperty("microphone",out var mic)&&mic.GetBoolean();
        UsbBackend.SelectedIndex=latest.TryGetProperty("usb_backend",out var usb)&&usb.GetString()=="usbdk" ? 1 : 0;
        string resolution = latest.GetProperty("resolution").GetString() ?? "";
        var selected = Resolution.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Content?.ToString()==resolution);
        if (selected is null) { selected = new ComboBoxItem { Content = resolution }; Resolution.Items.Add(selected); }
        Resolution.SelectedItem = selected;
        hardwareDirty = false; RememberHardwareDraft();
        } finally { loadingHardware = false; }
    }

    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Editors is null || Devices.SelectedIndex < 0 || e.Source != Devices) return;
        if (Devices.SelectedIndex < Editors.Children.Count) {
            hardwareCategory = Devices.SelectedIndex;
            for (int i = 0; i < Editors.Children.Count; i++)
                ((UIElement)Editors.Children[i]).Visibility = i == hardwareCategory ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!syncingNavigation && !IsUiBusy && HasVm && Devices.SelectedItem is ListBoxItem item && int.TryParse(item.Tag?.ToString(), out int page))
            Tabs.SelectedIndex = page;
    }
    private void MemoryChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Memory is null || syncingMemory) return;
        syncingMemory = true;
        try { Memory.Text = ((int)Math.Round(e.NewValue * 1024 / 4) * 4).ToString(System.Globalization.CultureInfo.InvariantCulture); }
        finally { syncingMemory = false; }
    }
    internal void ConfigureEngine(string root, string? qemu = null)
    {
        if (HasVm && currentState is not ("stopped" or "unprepared" or "unknown")) throw new InvalidOperationException("エンジンを切り替える前に、選択した VM を完全終了してください。");
        root=Path.GetFullPath(root);
        qemu=string.IsNullOrWhiteSpace(qemu) ? Path.Combine(root,File.Exists(Path.Combine(root,"reims-engine.json")) ? "bin/qemu-system-x86_64.exe" : "build/qemu/qemu-system-x86_64.exe") : Path.GetFullPath(qemu);
        if (!File.Exists(qemu)) throw new FileNotFoundException("Reims 対応 QEMU が見つかりません。エンジンのフォルダーを確認してください。");
        Root.Text=root; settings.EngineRoot=root; settings.QemuExecutable=qemu;
        if (!SavePreferences()) throw new IOException("エンジンの保存先を記録できませんでした。アプリ設定の保存先を確認して再試行してください。");
        ResetVmContext(); UpdatePowerControls();
    }

    private bool SelectSetupEngine(bool continueToVm = false)
    {
        var dialog = new EngineSetupWindow(continueToVm) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.InstalledDirectory is not { } folder) return false;
        ConfigureEngine(folder); return true;
    }

    private async void SetupEngine(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !DiscardPendingChanges()) return;
        busy=true; UpdatePowerControls();
        try {
            SelectSetupEngine();
        } catch (Exception error) { ErrorNotice.Text=error.Message; Feedback.Visibility=Visibility.Visible; }
        finally { busy=false; UpdatePowerControls(); }
        await RefreshState();
    }

    private void BrowseRoot(object sender,RoutedEventArgs e)
    {
        if (IsUiBusy || !DiscardPendingChanges()) return;
        var dialog=new OpenFileDialog { Title="Reims 対応の QEMU を選択", Filter="Reims エンジン|qemu-system-x86_64.exe" };
        if (dialog.ShowDialog(this)!=true) return;
        try {
            string folder=Path.GetDirectoryName(dialog.FileName)!;
            for (DirectoryInfo? parent=new(folder);parent is not null;parent=parent.Parent) {
                if (File.Exists(Path.Combine(parent.FullName,"Cargo.toml")) || File.Exists(Path.Combine(parent.FullName,"reims-engine.json"))) {
                    folder=parent.FullName; break;
                }
            }
            ConfigureEngine(folder,dialog.FileName); _ = RefreshState();
        } catch (Exception error) { ErrorNotice.Text=error.Message; Feedback.Visibility=Visibility.Visible; }
    }

    private void UpdatePowerControls()
    {
        if (StartButton is null || WorkspaceNavigation is null || RevealButton is null || SettingsOkButton is null) return;
        StartButton.IsEnabled = CanOperate("Start");
        PowerOffButton.IsEnabled = CanOperate("PowerOff");
        RestartButton.IsEnabled = CanOperate("Restart");
        ResumeButton.IsEnabled = CanOperate("Resume");
        bool resumable = currentState is "paused" or "prelaunch" or "suspended";
        ResumeButton.Visibility = resumable ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = resumable ? Visibility.Collapsed : Visibility.Visible;
        PowerMoreButton.IsEnabled = new[] { "Exit", "Pause", "Resume", "ForceExit" }.Any(CanOperate);
        MenuStart.IsEnabled = StartButton.IsEnabled; MenuPowerOff.IsEnabled = PowerOffButton.IsEnabled;
        MenuRestart.IsEnabled = RestartButton.IsEnabled; MenuForceExit.IsEnabled = CanOperate("ForceExit");
        foreach (var item in PowerMoreButton.ContextMenu.Items.OfType<MenuItem>()) item.IsEnabled = CanOperate(item.Tag?.ToString() ?? "");
        EngineNotice.Visibility = HasEngine ? Visibility.Collapsed : Visibility.Visible;
        EngineSummary.Text = HasEngine ? "エンジン設定済み" : "未設定";
        NewVmWelcomeDescription.Text = HasEngine ? "Windows の準備を確認して ISO を選ぶ" : "エンジンの準備から始める";
        RefreshButton.IsEnabled = HasVm && HasEngine && !busy && !refreshing;
        RevealButton.IsEnabled = selectedEntry is not null;
        foreach (var button in LogicalChildren<Button>(Tabs)) {
            if (button.Tag is string action && !int.TryParse(action, out _)) button.IsEnabled = CanOperate(action);
        }
        ClipboardSharing.IsEnabled = !busy && HasEngine && HasVm && currentState == "running";
        foreach (var button in LogicalChildren<Button>(Tabs).Where(x => x.Tag?.ToString() == "SnapshotRestore")) button.IsEnabled &= Snapshots.SelectedIndex >= 0;
        foreach (var button in LogicalChildren<Button>(Tabs).Where(x => x.Tag?.ToString() is "AttachUsb" or "DetachUsb")) button.IsEnabled &= UsbDevices.SelectedItem is not null;
        ConsoleHeading.Text = currentState switch { "stopped" => "起動する準備ができました", "unprepared" => "macOS をセットアップ", "shutdown" => "macOS は電源オフです", "unknown" => "VM の状態を確認してください", _ => consoleDetached ? "別ウィンドウで表示中" : "VM の画面を待っています" };
        ConsoleDescription.Text = currentState switch { "stopped" => "上の「起動」から macOS を開始できます。", "unprepared" => "設定画面の「macOS のインストール」からインストーラーを選んでください。", "unknown" => "「更新」で状態を取得してから操作を再試行できます。", _ => "入力の解放は Ctrl + Alt + Win。画面は別ウィンドウにも切り替えられます。" };
        if (selectedEntry?.IsAvailable == false) {
            ConsoleHeading.Text = "VM の保存先を確認してください";
            ConsoleDescription.Text = "登録は保持しています。保存先を接続してライブラリを更新するか、「VM を開く」で移動先の .reimsvm を選択してください。";
        }
        UpdateWorkspacePresentation();
    }

    private void DismissError(object sender,RoutedEventArgs e) { Feedback.Visibility=Visibility.Collapsed; }

    private string? ChooseIso() { var dialog=new OpenFileDialog { Filter="macOS インストーラー|*.iso;*.img|すべてのファイル|*.*" }; return dialog.ShowDialog(this)==true ? dialog.FileName : null; }
    private void BrowseIso(object sender,RoutedEventArgs e) { if (ChooseIso() is string path) Iso.Text=path; }
    private void BrowseHardwareIso(object sender,RoutedEventArgs e) { if (ChooseIso() is string path) HardwareIsoPath.Text=path; }
    private async void Setup(object sender,RoutedEventArgs e) {
        if (!RequireAction("Setup")) return;
        if (!File.Exists(Iso.Text.Trim())) { ShowError("使用する ISO ファイルを選択してください。"); Iso.Focus(); return; }
        if (!ManagerValidation.Integer(NewDiskSize.Text, 64, 2048, 1, out _)) { ShowError("ディスク容量は 64〜2048 GiB の整数で指定してください。"); NewDiskSize.Focus(); return; }
        if (await Operate("Setup",["-Iso",Iso.Text.Trim(),"-DiskGiB",NewDiskSize.Text.Trim()])) {
            ConnectIso.IsChecked=true; loadedHardware=false; await RefreshState();
            if (StartAfterSetup.IsChecked==true && await Operate("Start",["-WithInstaller"])) Tabs.SelectedIndex=0;
        }
    }
    private async void RefreshClick(object sender,RoutedEventArgs e) => await RefreshState();
    private async void ActionClick(object sender,RoutedEventArgs e) { string action=((Button)sender).Tag.ToString()!; await Operate(action,action=="Start"&&ConnectIso.IsChecked==true ? ["-WithInstaller"] : []); }
    private async void SaveSnapshot(object sender,RoutedEventArgs e)
    {
        string name = SnapshotName.Text.Trim();
        if (!ManagerValidation.SnapshotName(name)) { ShowError("名前は半角英数字で始まる 1〜64 文字です。英数字・_・-・. が使えます。"); SnapshotName.Focus(); return; }
        await Operate("SnapshotCreate", ["-Name", name]);
    }
    private async void RestoreSnapshot(object sender,RoutedEventArgs e) { if (RequireAction("SnapshotRestore") && Snapshots.SelectedIndex >= 0 && Confirm("選択したスナップショットに切り替えます。現在のディスクも保持します。")) await Operate("SnapshotRestore",["-Name",Selected(Snapshots)]); }
    private async void ForceExit(object sender,RoutedEventArgs e) { if (RequireAction("ForceExit") && Confirm("未保存の macOS 作業を失います。強制終了しますか？")) await Operate("ForceExit"); }
    private async void SaveHardware(object sender,RoutedEventArgs e) => await SaveHardwareCore();
    private async Task<bool> SaveHardwareCore() {
        if (!RequireAction("SetHardware") || !ValidateHardware()) return false;
        var args=new List<string> {"-Cpus",Cpus.Text.Trim(),"-MemoryMiB",Memory.Text.Trim(),"-Resolution",Selected(Resolution),"-Iso",HardwareIsoPath.Text.Trim(),"-Network",Bridge.IsChecked==true?"bridge":"nat","-Adapter",Selected(Tap),"-GuestHost",GuestHost.Text.Trim(),"-Audio",Sound.IsChecked==true?"dsound":"none"};
        if (Microphone.IsChecked==true) args.Add("-Microphone");
        args.AddRange(["-UsbBackend",UsbBackend.SelectedIndex==1?"usbdk":"winusb"]);
        if (HardwareIso.IsChecked==true) args.Add("-WithInstaller");
        bool saved = await Operate("SetHardware",args.ToArray());
        if (saved) {
            // The backend committed these values even if the following status read fails.
            RememberHardwareDraft();
            loadedHardware=false; await RefreshState();
        }
        return saved;
    }
    private async void ReloadHardware(object sender,RoutedEventArgs e) {
        if (!DiscardPendingChanges()) return;
        long version = contextVersion;
        bool wasDirty = hardwareDirty;
        hardwareDirty=false; loadedHardware=false; await RefreshState();
        // A failed reload must not make the still-visible edits appear saved.
        if (version == contextVersion && !loadedHardware) { hardwareDirty = wasDirty; UpdatePowerControls(); }
    }
    private async void ExpandDisk(object sender,RoutedEventArgs e) {
        if (!RequireAction("ExpandDisk")) return;
        if (!ManagerValidation.Integer(DiskSize.Text,64,2048,1,out int size) || latest.ValueKind != JsonValueKind.Object || size <= latest.GetProperty("disk_gib").GetInt32()) { InvalidField(DiskSize,"現在より大きい容量を 64〜2048 GiB の範囲で指定してください。縮小はできません。"); return; }
        if (!Confirm($"仮想ディスクを {size} GiB に拡張します。macOS 側の APFS 拡張も必要です。")) return;
        await Operate("ExpandDisk",["-DiskGiB",size.ToString()]);
    }
    private async void ApplyResolution(object sender,RoutedEventArgs e) => await Operate("ApplyResolution",["-Resolution",Selected(Resolution)]);
    private void OpenBridgeTab(object sender,RoutedEventArgs e) => Tabs.SelectedIndex=7;
    private void TapDownload(object sender,RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://build.openvpn.net/downloads/releases/") { UseShellExecute=true });
    private async void InstallTap(object sender,RoutedEventArgs e) {
        var dialog=new OpenFileDialog {Filter="TAP-Windows インストーラー|tap-windows*.exe"};
        if (dialog.ShowDialog(this)==true) await Operate("InstallTap",["-Installer",dialog.FileName],true);
    }
    private async void CreateBridge(object sender,RoutedEventArgs e) {
        if (!RequireAction("CreateBridge")) return;
        string tap=Selected(BridgeTap), physical=Selected(Physical);
        if (string.IsNullOrWhiteSpace(tap) || string.IsNullOrWhiteSpace(physical) || tap == physical) { ShowError("TAP と物理アダプターをそれぞれ選択してください。"); return; }
        if (Confirm($"TAP: {tap}\n物理: {physical}\nこの 2 つをブリッジ接続します。IP アドレスが変わり、通信が一時的に切れる場合があります。")) await Operate("CreateBridge",["-Adapter",tap,"-PhysicalAdapter",physical],true);
    }
    private async void Pair(object sender,RoutedEventArgs e) => await Operate("Pair",["-User",GuestUser.Text,"-Fingerprint",Fingerprint.Text]);
    private async void GenerateIdentity(object sender,RoutedEventArgs e) { if (Confirm("シリアル・MLB・ROM を更新します。SystemUUID は保持します。") && await Operate("GenerateIdentity")) await ReadIdentity(); }
    private async void EnableWhpx(object sender,RoutedEventArgs e) => await Operate("EnableWhpx",[],true);
    private bool Confirm(string text) => MessageBox.Show(this,text,"Reims Manager",MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)==MessageBoxResult.OK;

    internal async Task RenderChecks(string folder)
    {
        if (!selfTest) throw new InvalidOperationException("Only the self-test renders fixtures.");
        UpdateLayout(); SaveImage(Path.Combine(folder,"library-empty.png"));
        var engine=new EngineSetupWindow { Owner=this, Opacity=0, ShowInTaskbar=false };
        engine.Destination.Text=Path.Combine(folder,"new engine");
        engine.Show(); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); engine.UpdateLayout();
        SaveImage(Path.Combine(folder,"engine-setup.png"),(FrameworkElement)engine.Content);
        engine.Close();
        foreach (string component in new[]{"install-engine.ps1","engine-lock.json","opencore/base.py","opencore/apple-account.py","opencore/boot-policy.py","prepare-guest-bridge.ps1","guest-bridge-lock.json","opencore/guest-bridge.py"})
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory,"scripts",component))) throw new Exception("Missing packaged setup component: "+component);
        ConfigureEngine(folder,Environment.ProcessPath!);
        machines.Add(new VmEntry("macOS Tahoe",Path.Combine(folder,"Tahoe.reimsvm"),Path.Combine(folder,"Tahoe.data")));
        Library.ItemsSource=machines; Library.SelectedIndex=0; State.Text="停止中";
        currentState="stopped"; UpdatePowerControls();
        if (!StartButton.IsEnabled || PowerOffButton.IsEnabled) throw new Exception("Stopped power controls are incorrect");
        currentState="running"; UpdatePowerControls();
        if (StartButton.IsEnabled || !PowerOffButton.IsEnabled) throw new Exception("Running power controls are incorrect");
        CheckWorkspaceBehavior();
        currentState="stopped"; State.Text="完全停止"; UpdatePowerControls();
        if (Application.Current.GetType().GetProperty("ThemeMode")?.GetValue(Application.Current)?.ToString()!="System") throw new Exception("Fluent theme not loaded");
        for (int tab=0;tab<Tabs.Items.Count;tab++) {
            Tabs.SelectedIndex=tab; await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); UpdateLayout();
            if (tab==1) {
                for (int device=0;device<Editors.Children.Count;device++) {
                    Devices.SelectedIndex=device; await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); UpdateLayout();
                    if (((UIElement)Editors.Children[device]).Visibility!=Visibility.Visible) throw new Exception("Missing device editor");
                    SaveImage(Path.Combine(folder,$"hardware-{device}.png"));
                }
            } else SaveImage(Path.Combine(folder,$"manager-{tab}.png"));
        }
        double originalWidth = Width, originalHeight = Height;
        Width = MinWidth; Height = MinHeight; Tabs.SelectedIndex = 1; Devices.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); UpdateLayout();
        SaveImage(Path.Combine(folder,"compact-hardware.png"));
        if (Memory.ActualWidth < 100 || SaveHardwareButton.ActualWidth < 60) throw new Exception("Compact layout hides essential controls");
        Tabs.SelectedIndex = 0; currentState = "paused"; State.Text="一時停止"; UpdatePowerControls();
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); UpdateLayout();
        SaveImage(Path.Combine(folder,"compact-paused.png"));
        Width = originalWidth; Height = originalHeight;
        currentState = "unknown"; State.Text="状態を確認できません";
        StatusDetail.Text="テスト用: 管理接続からの応答がありません。更新して再試行できます。";
        UpdatePowerControls(); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); UpdateLayout();
        SaveImage(Path.Combine(folder,"connection-error.png"));
    }
    private void SaveImage(string path, FrameworkElement? surface = null)
    {
        var content=surface ?? (FrameworkElement)Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(content); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(path); png.Save(output);
    }
}
