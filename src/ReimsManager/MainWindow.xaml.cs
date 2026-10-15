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
    private bool HasVm => Library.SelectedItem is VmEntry;
    private bool HasEngine => Directory.Exists(Root.Text) && File.Exists(settings.QemuExecutable);
    private JsonElement latest;
    private string loadedRoot = "";
    private readonly DispatcherTimer timer = new() { Interval=TimeSpan.FromSeconds(5) };
    private readonly bool selfTest;

    public MainWindow(bool test = false)
    {
        selfTest = test;
        settings = test ? new ManagerSettings() : ManagerSettings.Load(out _);
        InitializeComponent();
        clipboardTimer.Tick += async (_,_) => await SyncClipboard();
        Root.Text = settings.EngineRoot;
        UpdatePowerControls();
        Devices.SelectedIndex = 0;
        Loaded += async (_,_) => { if (!selfTest) { LoadLibrary(); await RefreshState(); timer.Start(); } };
        timer.Tick += async (_,_) => { if (!busy) await RefreshState(false); };
        Closed += (_,_) => { timer.Stop(); clipboardTimer.Stop(); clipboardCancel.Cancel(); ConsoleView.Detach(); };
    }

    private async Task<string> Execute(string action, string[] arguments, bool elevate = false, string? input = null)
    {
        if (!HasEngine) throw new InvalidOperationException("先に Reims エンジンを選択してください。");
        if (!HasVm && action is not ("EnableWhpx" or "InstallUsbDk" or "InstallTap" or "CreateBridge"))
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
        info.ArgumentList.Add("-VmDirectory"); info.ArgumentList.Add(VmDirectory);
        info.ArgumentList.Add("-Qemu"); info.ArgumentList.Add(settings.QemuExecutable);
        info.ArgumentList.Add("-ToolsRoot"); info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"runtimes"));
        if (input is not null) info.RedirectStandardInput=true;
        if (elevate) { info.Verb="runas"; info.WindowStyle=ProcessWindowStyle.Hidden; }
        else { info.RedirectStandardOutput=true; info.RedirectStandardError=true; info.StandardOutputEncoding=Encoding.UTF8; info.StandardErrorEncoding=Encoding.UTF8; }
        using var process = Process.Start(info)!;
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        var stdout = elevate ? Task.FromResult("") : process.StandardOutput.ReadToEndAsync();
        var stderr = elevate ? Task.FromResult("") : process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); string output=await stdout, error=await stderr;
        if (process.ExitCode != 0) throw new Exception(error.Length > 0 ? error.Trim() : $"操作に失敗しました ({process.ExitCode})。");
        return output.Trim();
    }

    private async Task<bool> Operate(string action, string[]? args = null, bool elevate = false, string? input = null)
    {
        if (busy) return false;
        busy=true; Tabs.IsEnabled=false; Root.IsEnabled=false; Library.IsEnabled=false; UpdatePowerControls();
        ErrorNotice.Text=""; Feedback.Visibility=Visibility.Collapsed;
        State.Text="処理しています…"; Progress.Visibility=Visibility.Visible;
        bool success=false;
        try { Log.AppendText($"{action}…\n" + await Execute(action,args??[],elevate,input) + "\n"); success=true; }
        catch (Exception error) { Log.AppendText(error.Message+"\n"); ErrorNotice.Text=error.Message; Feedback.Visibility=Visibility.Visible; }
        finally { busy=false; Tabs.IsEnabled=true; Root.IsEnabled=true; Library.IsEnabled=true; Progress.Visibility=Visibility.Collapsed; Log.ScrollToEnd(); UpdatePowerControls(); }
        await RefreshState(); return success;
    }

    private static string Selected(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? item.Content.ToString()! : combo.SelectedItem?.ToString()??"";
    private static void Fill(ComboBox combo, IEnumerable<string> values) {
        string previous=Selected(combo); combo.ItemsSource=values.ToArray();
        combo.SelectedItem=previous; if (combo.SelectedIndex<0 && combo.Items.Count>0) combo.SelectedIndex=0;
    }
    private async Task RefreshState(bool full = true)
    {
        if (busy || refreshing) return;
        if (!HasVm || !HasEngine) {
            currentState="unprepared";
            State.Text=!HasEngine ? "Reims エンジンを選択してセットアップを始めます。" : "仮想マシンを作成するか、既存の VM を開いてください。";
            UpdatePowerControls(); return;
        }
        refreshing=true;
        try {
            string requestedRoot=Path.GetFullPath(Root.Text);
            string requestedVm=VmDirectory;
            JsonElement response;
            if (!full && latest.ValueKind == JsonValueKind.Object) {
                var live = await LiveVmStatus.Read(requestedVm);
                var values = JsonNode.Parse(latest.GetRawText())!.AsObject();
                values["state"] = live.State;
                values["runtime"] = live.Runtime is { } runtime ? JsonNode.Parse(runtime.GetRawText()) : null;
                response = JsonSerializer.SerializeToElement(values);
            } else {
                using var document = JsonDocument.Parse(await Execute("Status",[]));
                response = document.RootElement.Clone();
            }
            if (busy) return;
            if (!string.Equals(requestedRoot,Path.GetFullPath(Root.Text),StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(requestedVm,VmDirectory,StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(loadedRoot,requestedRoot,StringComparison.OrdinalIgnoreCase)) { loadedHardware=false; loadedRoot=requestedRoot; }
            latest=response;
            UpdateConsole();
            string raw=latest.GetProperty("state").GetString()!; currentState=raw; UpdatePowerControls();
            State.Text=(raw switch { "running"=>"実行中", "shutdown"=>"電源オフ", "paused"=>"一時停止", "stopped"=>"停止中", "unprepared"=>"セットアップ待ち", "prelaunch"=>"起動待ち", _=>raw });
            if (!full) return;
            Fill(Snapshots,latest.GetProperty("snapshots").EnumerateArray().Select(x=>x.GetString()!));
            string[] taps=latest.GetProperty("taps").EnumerateArray().Select(x=>x.GetString()!).ToArray(); Fill(Tap,taps); Fill(BridgeTap,taps);
            Fill(Physical,latest.GetProperty("adapters").EnumerateArray().Where(x=>x.TryGetProperty("HardwareInterface",out var value) && value.ValueKind==JsonValueKind.True).Select(x=>x.GetProperty("Name").GetString()!));
            if (!loadedHardware && latest.GetProperty("prepared").GetBoolean()) { LoadHardware(); loadedHardware=true; }
        } catch (Exception error) { State.Text=error is OperationCanceledException ? "VM の管理接続から応答がありません。再試行できます。" : error.Message; }
        finally { refreshing=false; }
    }

    private void LoadHardware()
    {
        if (latest.ValueKind != JsonValueKind.Object) return;
        MemorySlider.Value=Math.Clamp(latest.GetProperty("memory_mib").GetDouble()/1024,4,64); Memory.Text=latest.GetProperty("memory_mib").ToString();
        Cpus.Text=latest.GetProperty("cpus").ToString(); DiskSize.Text=latest.GetProperty("disk_gib").ToString();
        DiskInfo.Text=$"最大容量: {DiskSize.Text} GiB\n{latest.GetProperty("disk").GetString()}";
        Iso.Text=HardwareIsoPath.Text=latest.GetProperty("iso").GetString()??"";
        ConnectIso.IsChecked=HardwareIso.IsChecked=latest.GetProperty("with_installer").GetBoolean();
        Nat.IsChecked=latest.GetProperty("network").GetString()=="nat"; Bridge.IsChecked=!Nat.IsChecked;
        Tap.SelectedItem=latest.GetProperty("network_adapter").GetString(); GuestHost.Text=latest.GetProperty("guest_host").GetString()??"";
        Sound.IsChecked=latest.GetProperty("audio").GetString()=="dsound";
        Microphone.IsChecked=latest.TryGetProperty("microphone",out var mic)&&mic.GetBoolean();
        UsbBackend.SelectedIndex=latest.TryGetProperty("usb_backend",out var usb)&&usb.GetString()=="usbdk" ? 1 : 0;
        Resolution.SelectedItem=Resolution.Items.Cast<ComboBoxItem>().First(x=>x.Content.ToString()==latest.GetProperty("resolution").GetString());
    }

    private void DeviceChanged(object sender, SelectionChangedEventArgs e) {
        if (Editors is null) return;
        for (int i=0;i<Editors.Children.Count;i++) ((UIElement)Editors.Children[i]).Visibility=i==Devices.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }
    private void MemoryChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Memory != null) Memory.Text=((int)Math.Round(e.NewValue)*1024).ToString(); }
    internal void ConfigureEngine(string root, string? qemu = null)
    {
        root=Path.GetFullPath(root);
        qemu=string.IsNullOrWhiteSpace(qemu) ? Path.Combine(root,"build/qemu/qemu-system-x86_64.exe") : Path.GetFullPath(qemu);
        if (!File.Exists(qemu)) throw new FileNotFoundException("Reims 対応 QEMU が見つかりません。エンジンのフォルダーを確認してください。");
        ConsoleView.Detach(); Root.Text=root; settings.EngineRoot=root; settings.QemuExecutable=qemu;
        if (!selfTest) settings.Save();
        loadedHardware=false; UpdatePowerControls();
    }

    private void BrowseRoot(object sender,RoutedEventArgs e)
    {
        if (busy) return;
        var dialog=new OpenFileDialog { Title="Reims 対応の QEMU を選択", Filter="Reims エンジン|qemu-system-x86_64.exe" };
        if (dialog.ShowDialog(this)!=true) return;
        try {
            string folder=Path.GetDirectoryName(dialog.FileName)!;
            for (DirectoryInfo? parent=new(folder);parent is not null;parent=parent.Parent) {
                if (File.Exists(Path.Combine(parent.FullName,"Cargo.toml")) || File.Exists(Path.Combine(parent.FullName,"reims-engine.json"))) {
                    folder=parent.FullName; break;
                }
            }
            ConfigureEngine(folder,dialog.FileName); LoadLibrary();
        } catch (Exception error) { ErrorNotice.Text=error.Message; Feedback.Visibility=Visibility.Visible; }
    }

    private void UpdatePowerControls()
    {
        if (StartButton is null) return;
        bool usable=!busy && HasVm && HasEngine;
        StartButton.IsEnabled=usable && currentState is "stopped" or "shutdown";
        PowerOffButton.IsEnabled=usable && currentState is "running" or "paused";
        RestartButton.IsEnabled=PowerOffButton.IsEnabled;
        PowerMoreButton.IsEnabled=usable && currentState is not ("unprepared" or "stopped");
        MenuStart.IsEnabled=StartButton.IsEnabled;
        MenuPowerOff.IsEnabled=PowerOffButton.IsEnabled;
        MenuRestart.IsEnabled=RestartButton.IsEnabled;
        MenuForceExit.IsEnabled=PowerMoreButton.IsEnabled;
        EngineNotice.Visibility=HasEngine ? Visibility.Collapsed : Visibility.Visible;
        Welcome.Visibility=HasVm ? Visibility.Collapsed : Visibility.Visible;
        Tabs.Visibility=HasVm ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in PowerMoreButton.ContextMenu.Items.OfType<MenuItem>()) {
            item.IsEnabled=usable && (item.Tag?.ToString() switch {
                "Pause" => currentState=="running", "Resume" => currentState is "paused" or "prelaunch",
                _ => currentState is not ("stopped" or "unprepared")
            });
        }
    }

    private void DismissError(object sender,RoutedEventArgs e) { Feedback.Visibility=Visibility.Collapsed; }

    private string? ChooseIso() { var dialog=new OpenFileDialog { Filter="macOS インストーラー|*.iso;*.img|すべてのファイル|*.*" }; return dialog.ShowDialog(this)==true ? dialog.FileName : null; }
    private void BrowseIso(object sender,RoutedEventArgs e) { if (ChooseIso() is string path) Iso.Text=path; }
    private void BrowseHardwareIso(object sender,RoutedEventArgs e) { if (ChooseIso() is string path) HardwareIsoPath.Text=path; }
    private async void Setup(object sender,RoutedEventArgs e) {
        if (!File.Exists(Iso.Text)) { ErrorNotice.Text="使用する ISO ファイルを選択してください。"; Feedback.Visibility=Visibility.Visible; return; }
        if (await Operate("Setup",["-Iso",Iso.Text,"-DiskGiB",NewDiskSize.Text])) {
            ConnectIso.IsChecked=true; loadedHardware=false; await RefreshState();
            if (StartAfterSetup.IsChecked==true && await Operate("Start",["-WithInstaller"])) Tabs.SelectedIndex=0;
        }
    }
    private async void RefreshClick(object sender,RoutedEventArgs e) => await RefreshState();
    private async void ActionClick(object sender,RoutedEventArgs e) { string action=((Button)sender).Tag.ToString()!; await Operate(action,action=="Start"&&ConnectIso.IsChecked==true ? ["-WithInstaller"] : []); }
    private async void SaveSnapshot(object sender,RoutedEventArgs e) => await Operate("SnapshotCreate",["-Name",SnapshotName.Text]);
    private async void RestoreSnapshot(object sender,RoutedEventArgs e) { if (Confirm("選択したスナップショットに切り替えます。現在のディスクも保持します。")) await Operate("SnapshotRestore",["-Name",Selected(Snapshots)]); }
    private async void ForceExit(object sender,RoutedEventArgs e) { if (Confirm("未保存の macOS 作業を失います。強制終了しますか？")) await Operate("ForceExit"); }
    private async void SaveHardware(object sender,RoutedEventArgs e) {
        var args=new List<string> {"-Cpus",Cpus.Text,"-MemoryMiB",Memory.Text,"-Resolution",Selected(Resolution),"-Iso",HardwareIsoPath.Text,"-Network",Bridge.IsChecked==true?"bridge":"nat","-Adapter",Selected(Tap),"-GuestHost",GuestHost.Text,"-Audio",Sound.IsChecked==true?"dsound":"none"};
        if (Microphone.IsChecked==true) args.Add("-Microphone");
        args.AddRange(["-UsbBackend",UsbBackend.SelectedIndex==1?"usbdk":"winusb"]);
        if (HardwareIso.IsChecked==true) args.Add("-WithInstaller");
        if (await Operate("SetHardware",args.ToArray())) { loadedHardware=false; await RefreshState(); }
    }
    private async void ReloadHardware(object sender,RoutedEventArgs e) { loadedHardware=false; await RefreshState(); }
    private async void ExpandDisk(object sender,RoutedEventArgs e) { if (await Operate("ExpandDisk",["-DiskGiB",DiskSize.Text])) { loadedHardware=false; await RefreshState(); } }
    private async void ApplyResolution(object sender,RoutedEventArgs e) => await Operate("ApplyResolution",["-Resolution",Selected(Resolution)]);
    private void OpenBridgeTab(object sender,RoutedEventArgs e) => Tabs.SelectedIndex=7;
    private void TapDownload(object sender,RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://build.openvpn.net/downloads/releases/") { UseShellExecute=true });
    private async void InstallTap(object sender,RoutedEventArgs e) {
        var dialog=new OpenFileDialog {Filter="TAP-Windows インストーラー|tap-windows*.exe"};
        if (dialog.ShowDialog(this)==true) await Operate("InstallTap",["-Installer",dialog.FileName],true);
    }
    private async void CreateBridge(object sender,RoutedEventArgs e) {
        string tap=Selected(BridgeTap), physical=Selected(Physical);
        if (Confirm($"TAP: {tap}\n物理: {physical}\nこの 2 つをブリッジ接続します。IP アドレスが変わり、通信が一時的に切れる場合があります。")) await Operate("CreateBridge",["-Adapter",tap,"-PhysicalAdapter",physical],true);
    }
    private async void Pair(object sender,RoutedEventArgs e) => await Operate("Pair",["-User",GuestUser.Text,"-Fingerprint",Fingerprint.Text]);
    private async void GenerateIdentity(object sender,RoutedEventArgs e) { if (Confirm("シリアル・MLB・ROM を更新します。SystemUUID は保持します。")) await Operate("GenerateIdentity"); }
    private async void EnableWhpx(object sender,RoutedEventArgs e) => await Operate("EnableWhpx",[],true);
    private bool Confirm(string text) => MessageBox.Show(this,text,"Reims Manager",MessageBoxButton.OKCancel)==MessageBoxResult.OK;

    internal async Task RenderChecks(string folder)
    {
        if (!selfTest) throw new InvalidOperationException("Only the self-test renders fixtures.");
        UpdateLayout(); SaveImage(Path.Combine(folder,"library-empty.png"));
        ConfigureEngine(folder,Environment.ProcessPath!);
        machines.Add(new VmEntry("macOS Tahoe",Path.Combine(folder,"Tahoe.reimsvm"),Path.Combine(folder,"Tahoe.data")));
        Library.ItemsSource=machines; Library.SelectedIndex=0; State.Text="停止中";
        currentState="stopped"; UpdatePowerControls();
        if (!StartButton.IsEnabled || PowerOffButton.IsEnabled) throw new Exception("Stopped power controls are incorrect");
        currentState="running"; UpdatePowerControls();
        if (StartButton.IsEnabled || !PowerOffButton.IsEnabled) throw new Exception("Running power controls are incorrect");
        currentState="stopped"; UpdatePowerControls();
        if (Application.Current.GetType().GetProperty("ThemeMode")?.GetValue(Application.Current)?.ToString()!="System") throw new Exception("Fluent theme not loaded");
        for (int tab=0;tab<Tabs.Items.Count;tab++) {
            Tabs.SelectedIndex=tab; await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); UpdateLayout();
            if (tab==1) {
                for (int device=0;device<Devices.Items.Count;device++) {
                    Devices.SelectedIndex=device; await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); UpdateLayout();
                    if (((UIElement)Editors.Children[device]).Visibility!=Visibility.Visible) throw new Exception("Missing device editor");
                    SaveImage(Path.Combine(folder,$"hardware-{device}.png"));
                }
            } else SaveImage(Path.Combine(folder,$"manager-{tab}.png"));
        }
    }
    private void SaveImage(string path)
    {
        var content=(FrameworkElement)Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(content); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(path); png.Save(output);
    }
}
