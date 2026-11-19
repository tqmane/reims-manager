using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReimsManager;

public partial class MainWindow
{
    private bool loadingHardware, loadingIdentity, hardwareDirty, identityDirty, syncingMemory, syncingResolution;
    private bool restoringSelection, syncingNavigation, closing;
    private VmEntry? selectedEntry;
    private long contextVersion;
    private CancellationTokenSource statusCancellation = new();
    private TaskCompletionSource? refreshCompletion;
    private bool refreshPending, refreshFull;

    private void InitializeWorkspace()
    {
        foreach (TextBox field in new[] { Memory, Cpus, HardwareIsoPath, GuestHost })
            field.TextChanged += (_, _) => MarkHardwareDirty();
        Memory.TextChanged += MemoryTextChanged;
        foreach (ComboBox field in new[] { Resolution, Tap, Uplink, UsbBackend })
            field.SelectionChanged += (_, _) => MarkHardwareDirty();
        foreach (System.Windows.Controls.Primitives.ToggleButton field in new System.Windows.Controls.Primitives.ToggleButton[] { HardwareIso, Nat, Bridge, Sound, Microphone }) {
            field.Checked += (_, _) => MarkHardwareDirty();
            field.Unchecked += (_, _) => MarkHardwareDirty();
        }
        foreach (TextBox field in new[] { IdentityModel, IdentitySerial, IdentityMlb, IdentityUuid, IdentityRom })
            field.TextChanged += (_, _) => { if (!loadingIdentity && HasVm) { ClearSettingsFeedback(); identityDirty = CaptureIdentityDraft() != committedIdentity; UpdatePowerControls(); } };
        Snapshots.SelectionChanged += (_, _) => UpdatePowerControls();
        UsbDevices.SelectionChanged += (_, _) => UpdatePowerControls();
        Closing += ConfirmWindowClose;
        SyncNavigation();
    }

    private bool CanOperate(string action) => !busy && !completingOperation && !closing && VmActionPolicy.CanRun(action, currentState, HasVm, HasEngine);

    private bool RequireAction(string action)
    {
        if (CanOperate(action)) return true;
        string reason = busy || completingOperation ? UiText.T("別の操作を処理しています。") : !HasEngine ? UiText.T("先に Reims エンジンを設定してください。") :
            !HasVm ? UiText.T("操作する VM を選択してください。") : currentState == "unknown" ? UiText.T("VM の状態を確認できません。更新してから再試行してください。") :
            currentState is "paused" or "suspended" or "prelaunch" ? UiText.T("先に VM を再開してください。設定変更には、その後の完全終了が必要です。") :
            currentState == "shutdown" ? UiText.T("macOS は電源オフですが QEMU は残っています。「••• → 完全終了」を選択してください。") :
            UiText.T("現在の状態では実行できません。ディスク・設定変更は VM の完全停止後に行ってください。");
        ShowError(reason);
        return false;
    }

    private void AppendLog(string text)
    {
        const int limit = 65536;
        if (text.Length > limit) text = text[^limit..];
        if (Log.Text.Length + text.Length > limit) Log.Text = Log.Text[^(Math.Max(0, limit - text.Length))..];
        Log.AppendText(text);
        if (Log.Text.Length > limit) Log.Text = Log.Text[^limit..];
        Log.ScrollToEnd();
    }

    private void ShowError(string message)
    {
        if (closing || ErrorNotice is null) return;
        ExitFullScreen();
        ErrorNotice.Text = Services.BackendErrorFormatter.CleanMessage(message);
        Feedback.Visibility = Visibility.Visible;
    }

    private bool SavePreferences()
    {
        if (selfTest) return true;
        try {
            settings.Save();
            if (selectedEntry is not null && (ClipboardSharing.IsChecked == true) != settings.ClipboardEnabled(selectedEntry.File))
                RestoreClipboardPreference();
            if (settings.LastSaveWarning is { } warning) ShowError(warning);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            ShowError(UiText.T("アプリ設定を保存できませんでした。VM のディスクは変更していません。\n") + error.Message);
            AppendLog(error.Message + "\n"); return false;
        }
    }

    private void MarkHardwareDirty()
    {
        if (loadingHardware || !loadedHardware || !HasVm) return;
        ClearSettingsFeedback();
        hardwareDirty = CaptureHardwareDraft() != committedHardware;
        UpdatePowerControls();
    }

    private void MemoryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (syncingMemory || !ManagerValidation.Integer(Memory.Text, 4096, 131072, 4, out int value)) return;
        syncingMemory = true;
        try { MemorySlider.Value = value / 1024.0; }
        finally { syncingMemory = false; }
    }

    private bool DiscardPendingChanges()
    {
        if (!hardwareDirty && !identityDirty) return true;
        if (selfTest) return false;
        return MessageBox.Show(this, UiText.T("保存していないハードウェア設定または Mac の識別情報があります。変更を破棄して続けますか？"),
            UiText.T("未保存の変更"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void ConfirmWindowClose(object? sender, CancelEventArgs e)
    {
        if (IsUiBusy) { ShowError(UiText.T("処理中は閉じられません。ディスクへの書き込みが完了してから閉じてください。")); e.Cancel = true; return; }
        if (!selfTest && !DiscardPendingChanges()) { e.Cancel = true; return; }
        ExitFullScreen();
        try { ConsoleView.Detach(); }
        catch (Exception error) {
            ShowError(UiText.T("VM 画面を復元できないため、管理ウィンドウを閉じられません。") + error.Message);
            e.Cancel = true; return;
        }
        closing = true;
        contextVersion++;
        statusCancellation.Cancel();
    }

    private void InvalidateReadRequests()
    {
        contextVersion++;
        statusCancellation.Cancel();
        statusCancellation.Dispose();
        statusCancellation = new();
    }

    private void ResetVmContext()
    {
        InvalidateReadRequests();
        currentState = HasVm ? "unknown" : "unprepared"; latest = default; loadedHardware = false;
        hardwareDirty = identityDirty = false;
        ClearSettingsFeedback();
        committedHardware = null; committedIdentity = null;
        networkDraftRefreshPending = false;
        Tabs.SelectedIndex = 0;
        if (SettingsSectionNav is not null) SettingsSectionNav.SelectedIndex = 0;
        if (AppSettingsOverlay is not null) AppSettingsOverlay.Visibility = Visibility.Collapsed;
        ConsoleView.Detach(); ConsoleView.Visibility = Visibility.Hidden; ConsolePlaceholder.Visibility = Visibility.Visible;
        consoleDetached = false;
        RestoreClipboardPreference();
        loadingIdentity = loadingHardware = true;
        try {
            foreach (TextBox field in new[] { IdentityModel, IdentitySerial, IdentityMlb, IdentityRom, IdentityUuid, Iso, HardwareIsoPath, GuestHost, GuestUser, Fingerprint }) field.Clear();
            Memory.Text = "4096"; Cpus.Text = Math.Min(4, Environment.ProcessorCount).ToString(); DiskSize.Text = "300"; DiskInfo.Text = UiText.T("構成を読み込んでいます。");
            Sound.IsChecked = Microphone.IsChecked = HardwareIso.IsChecked = ConnectIso.IsChecked = false;
            Nat.IsChecked = true; UsbBackend.SelectedIndex = 0; SelectResolutionItems("1920x1080");
            Tap.ItemsSource = BridgeTap.ItemsSource = Physical.ItemsSource = Uplink.ItemsSource = Snapshots.ItemsSource = null;
            NetworkNotice.Text = "";
            wifiForwarder = null; wifiStatusError = null; hardwareForwarder = ""; PacketFilterTerms.IsChecked = false;
            ResetUsbInventory();
        } finally { loadingIdentity = loadingHardware = false; }
        RememberHardwareDraft(); RememberIdentityDraft();
        State.Text = UiText.T("確認中"); StatusDetail.Text = UiText.T("選択した VM の状態を取得しています。");
        VmSummary.Text = UiText.T("構成を読み込んでいます。");
        if (OverviewCpuValue is not null) OverviewCpuValue.Text = OverviewMemoryValue.Text = OverviewDiskValue.Text = OverviewNetworkValue.Text = "—";
        if (OverviewMediaSummary is not null) OverviewMediaSummary.Text = "";
        UpdatePowerControls();
    }

    private void FilterLibrary(object sender, TextChangedEventArgs e)
    {
        if (Library is null || LibraryEmpty is null) return;
        UpdateLibraryView();
    }

    private void UpdateLibraryView()
    {
        string query = LibrarySearch.Text.Trim();
        var visible = machines.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            Path.GetFileName(x.File).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        restoringSelection = true;
        try { Library.ItemsSource = visible; Library.SelectedItem = visible.FirstOrDefault(x => x == selectedEntry); }
        finally { restoringSelection = false; }
        if (LibrarySearchBox is not null)
            LibrarySearchBox.Visibility = machines.Count > 2 || query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmpty.Text = query.Length == 0 ? UiText.T("「+」から VM を作成または開く") : UiText.T("一致する VM はありません");
        LibraryEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingNavigation || Tabs is null || IsUiBusy || e.Source != WorkspaceNavigation) return;
        if (WorkspaceNavigation.SelectedItem is ListBoxItem item && int.TryParse(item.Tag?.ToString(), out int page) && page >= 0 && page < Tabs.Items.Count)
            Tabs.SelectedIndex = page;
    }

    private void SettingsSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsSectionTabs is null || SettingsSectionNav is null || e.Source != SettingsSectionNav) return;
        if (SettingsSectionNav.SelectedItem is ListBoxItem item && int.TryParse(item.Tag?.ToString(), out int sec) && sec >= 0 && sec < SettingsSectionTabs.Items.Count)
            SettingsSectionTabs.SelectedIndex = sec;
    }

    private void OpenHardwareSettingsClick(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !HasVm) return;
        Tabs.SelectedIndex = 3;
        SettingsSectionNav.SelectedIndex = 0;
        SyncNavigation(); UpdatePowerControls();
    }

    private void OpenStorageSettingsClick(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !HasVm) return;
        Tabs.SelectedIndex = 3;
        SettingsSectionNav.SelectedIndex = 1;
        SyncNavigation(); UpdatePowerControls();
    }

    private void NavigatePage(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !HasVm) return;
        if (sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out int page) && page >= 0 && page < Tabs.Items.Count)
            Tabs.SelectedIndex = page;
    }

    private void DiscardSettingsEdits(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !DiscardPendingChanges() || !RestoreSettingsDrafts()) return;
        ClearSettingsFeedback();
        UpdatePowerControls();
    }

    private async void SettingsStateAction(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || sender is not Button { Tag: string action }) return;
        if (action == "SetupEngine") { SetupEngine(sender, e); return; }
        if (action == "SetupPage") { if (HasVm) Tabs.SelectedIndex = 0; return; }
        await Operate(action);
    }

    private void SyncNavigation()
    {
        if (WorkspaceNavigation is null || Tabs is null || Tabs.SelectedIndex < 0) return;
        syncingNavigation = true;
        try {
            WorkspaceNavigation.SelectedItem = WorkspaceNavigation.Items.Cast<ListBoxItem>()
                .FirstOrDefault(x => x.Tag?.ToString() == Tabs.SelectedIndex.ToString(CultureInfo.InvariantCulture));
        } finally { syncingNavigation = false; }
    }

    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && OverlayOpen && !IsUiBusy) {
            e.Handled = true;
            CloseAppSettings(sender, e);
            return;
        }
        if (!IsUiBusy && e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None) {
            e.Handled = true; ToggleFullScreen(sender, e); return;
        }
        if (IsUiBusy || ConsoleFocused) return;
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await RefreshDisplayedState(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control) {
            if (e.Key == Key.N) { e.Handled = true; NewVm(sender, e); }
            else if (e.Key == Key.O) { e.Handled = true; OpenVm(sender, e); }
            else if (e.Key == Key.S && HasVm && (hardwareDirty || identityDirty)) { e.Handled = true; ApplySettings(sender, e); }
        }
    }

    private void ClearLog(object sender, RoutedEventArgs e) => Log.Clear();

    private new static IEnumerable<T> LogicalChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root)) {
            if (child is T match) yield return match;
            if (child is DependencyObject nested)
                foreach (T descendant in LogicalChildren<T>(nested)) yield return descendant;
        }
    }

    private bool ValidateHardware()
    {
        if (!ManagerValidation.Integer(Cpus.Text, 1, 64, 1, out int cpus) || cpus > Environment.ProcessorCount)
            return InvalidField(Cpus, UiText.Format($"CPU は 1〜{Math.Min(64, Environment.ProcessorCount)} の整数で指定してください。"));
        if (!ManagerValidation.Integer(Memory.Text, 4096, 131072, 4, out _))
            return InvalidField(Memory, UiText.T("メモリは 4096〜131072 MiB、4 MiB 単位で指定してください。"));
        if (!ManagerValidation.Resolution(Selected(Resolution))) return InvalidField(Resolution, UiText.T("対応する起動時の解像度を選び直してください。"));
        if (HardwareIso.IsChecked == true && !File.Exists(HardwareIsoPath.Text.Trim())) return InvalidField(HardwareIsoPath, UiText.T("インストーラーを接続するには、存在する ISO / IMG を選択してください。"));
        if (!string.IsNullOrWhiteSpace(HardwareIsoPath.Text) && !File.Exists(HardwareIsoPath.Text.Trim())) return InvalidField(HardwareIsoPath, UiText.T("ISO が見つかりません。選び直すか、接続をオフにしてパスを空にしてください。"));
        if (NetworkDraftChanged) {
            if (Bridge.IsChecked == true) {
                bool legacy = latest.ValueKind == System.Text.Json.JsonValueKind.Object && latest.TryGetProperty("network", out var mode) && mode.GetString() == "bridge" &&
                    (!latest.TryGetProperty("network_uplink_id", out var saved) || string.IsNullOrEmpty(saved.GetString()));
                if (SelectedUplink.Length == 0 && !legacy) return NetworkPreparationRequired(UiText.T("ホストの Wi-Fi・有線 LAN を選び、ブリッジ・Wi-Fi 接続を準備してください。"));
                bool wifi = Uplink.SelectedItem is NetworkUplink { Kind: "wifi" } || WifiForwarderState.IsMacNat(hardwareForwarder);
                if (wifi) {
                    string savedId = latest.TryGetProperty("network_uplink_id", out var id) ? id.GetString() ?? "" : "";
                    if (wifiForwarder is not { Kind: WifiForwarderState.MacNat, Configured: true, Ipv6Enabled: true } || SelectedUplink != savedId)
                        return NetworkPreparationRequired(UiText.T("先にこの Wi-Fi の互換接続を準備してください。ほかの未保存の変更は保持しています。"));
                } else if (string.IsNullOrWhiteSpace(Selected(Tap))) { NetworkTapDetails.IsExpanded = true; return InvalidField(Tap, UiText.T("ブリッジの準備で選択した Reims 専用 TAP を指定してください。")); }
            }
            if (WifiForwarderState.IsMacNat(hardwareForwarder) && Nat.IsChecked == true) return NetworkPreparationRequired(UiText.T("Wi-Fi 互換接続の解除ボタンで、専用 TAP の設定を復元してから NAT に戻してください。ほかの未保存の変更は保持しています。"));
            if (!ManagerValidation.Host(GuestHost.Text.Trim())) return InvalidField(GuestHost, UiText.T("macOS の IP アドレスまたはホスト名を確認してください。"));
        }
        if (Microphone.IsChecked == true && Sound.IsChecked != true) return InvalidField(Microphone, UiText.T("マイクを使用するには、スピーカー共有も有効にしてください。"));
        return true;
    }

    private bool InvalidField(Control field, string message)
    {
        for (int i = 0; i < Tabs.Items.Count; i++) {
            if (Tabs.Items[i] is TabItem tab && (ReferenceEquals(tab.Content, field) || (tab.Content is DependencyObject content && LogicalChildren<Control>(content).Contains(field)))) {
                Tabs.SelectedIndex = i;
                break;
            }
        }
        if (Tabs.SelectedIndex == 3 && SettingsSectionTabs is not null) {
            for (int s = 0; s < SettingsSectionTabs.Items.Count; s++) {
                if (SettingsSectionTabs.Items[s] is TabItem sec && (ReferenceEquals(sec.Content, field) || (sec.Content is DependencyObject sc && LogicalChildren<Control>(sc).Contains(field)))) {
                    SettingsSectionNav.SelectedIndex = s;
                    SettingsSectionTabs.SelectedIndex = s;
                    break;
                }
            }
        }
        for (DependencyObject? cur = field; cur is not null; cur = LogicalTreeHelper.GetParent(cur)) {
            if (cur is Expander exp) exp.IsExpanded = true;
        }
        ShowError(message);
        field.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => {
            if (!closing && field.IsVisible) { field.BringIntoView(); field.Focus(); }
        }));
        return false;
    }
}
