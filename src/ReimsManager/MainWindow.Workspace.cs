using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ReimsManager;

public partial class MainWindow
{
    private bool loadingHardware, loadingIdentity, hardwareDirty, identityDirty, syncingMemory;
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
        foreach (ComboBox field in new[] { Resolution, Tap, UsbBackend })
            field.SelectionChanged += (_, _) => MarkHardwareDirty();
        foreach (System.Windows.Controls.Primitives.ToggleButton field in new System.Windows.Controls.Primitives.ToggleButton[] { HardwareIso, Nat, Bridge, Sound, Microphone }) {
            field.Checked += (_, _) => MarkHardwareDirty();
            field.Unchecked += (_, _) => MarkHardwareDirty();
        }
        foreach (TextBox field in new[] { IdentityModel, IdentitySerial, IdentityMlb, IdentityUuid, IdentityRom })
            field.TextChanged += (_, _) => { if (!loadingIdentity && HasVm) identityDirty = true; };
        Snapshots.SelectionChanged += (_, _) => UpdatePowerControls();
        UsbDevices.SelectionChanged += (_, _) => UpdatePowerControls();
        Closing += ConfirmWindowClose;
        SyncNavigation();
    }

    private bool CanOperate(string action) => !busy && !closing && VmActionPolicy.CanRun(action, currentState, HasVm, HasEngine);

    private bool RequireAction(string action)
    {
        if (CanOperate(action)) return true;
        string reason = busy ? "別の操作を処理しています。" : !HasEngine ? "先に Reims エンジンを設定してください。" :
            !HasVm ? "操作する VM を選択してください。" : currentState == "unknown" ? "VM の状態を確認できません。更新してから再試行してください。" :
            currentState is "paused" or "suspended" or "prelaunch" ? "先に VM を再開してください。設定変更には、その後の完全終了が必要です。" :
            currentState == "shutdown" ? "macOS は電源オフですが QEMU は残っています。「••• → 完全終了」を選択してください。" :
            "現在の状態では実行できません。ディスク・設定変更は VM の完全停止後に行ってください。";
        ShowError(reason);
        return false;
    }

    private void AppendLog(string text)
    {
        // Diagnostics stay useful even after a long session; clipboard/identity payloads never enter here.
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
        ErrorNotice.Text = message;
        Feedback.Visibility = Visibility.Visible;
    }

    private bool SavePreferences()
    {
        if (selfTest) return true;
        try { settings.Save(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            ShowError("アプリ設定を保存できませんでした。VM のディスクは変更していません。\n" + error.Message);
            AppendLog(error.Message + "\n"); return false;
        }
    }

    private void MarkHardwareDirty()
    {
        if (loadingHardware || !loadedHardware || !HasVm) return;
        hardwareDirty = true;
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
        return MessageBox.Show(this, "保存していないハードウェア設定または Mac の識別情報があります。変更を破棄して続けますか？",
            "未保存の変更", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void ConfirmWindowClose(object? sender, CancelEventArgs e)
    {
        if (busy) { ShowError("処理中は閉じられません。ディスクへの書き込みが完了してから閉じてください。"); e.Cancel = true; return; }
        if (!selfTest && !DiscardPendingChanges()) { e.Cancel = true; return; }
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
        currentState = "unknown"; latest = default; loadedHardware = false;
        hardwareDirty = identityDirty = false;
        ConsoleView.Detach(); ConsoleView.Visibility = Visibility.Hidden; ConsolePlaceholder.Visibility = Visibility.Visible;
        consoleDetached = false;
        ClipboardSharing.IsChecked = false; ResetClipboard();
        loadingIdentity = loadingHardware = true;
        try {
            foreach (TextBox field in new[] { IdentityModel, IdentitySerial, IdentityMlb, IdentityRom, IdentityUuid, Iso, HardwareIsoPath, GuestHost, GuestUser, Fingerprint }) field.Clear();
            Memory.Text = "8192"; Cpus.Text = "8"; DiskSize.Text = "128"; DiskInfo.Text = "構成を読み込んでいます。";
            Sound.IsChecked = Microphone.IsChecked = HardwareIso.IsChecked = ConnectIso.IsChecked = false;
            Nat.IsChecked = true; UsbBackend.SelectedIndex = 0; Resolution.SelectedIndex = 1;
            Tap.ItemsSource = BridgeTap.ItemsSource = Physical.ItemsSource = Snapshots.ItemsSource = null;
            UsbDevices.ItemsSource = null;
        } finally { loadingIdentity = loadingHardware = false; }
        State.Text = "確認中"; StatusDetail.Text = "選択した VM の状態を取得しています。";
        VmSummary.Text = "構成を読み込んでいます。";
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
        LibraryEmpty.Text = query.Length == 0 ? "「新規」または「開く」から追加" : "一致する VM はありません";
        LibraryEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingNavigation || Tabs is null || busy || e.Source != WorkspaceNavigation) return;
        if (WorkspaceNavigation.SelectedItem is ListBoxItem item && int.TryParse(item.Tag?.ToString(), out int page)) Tabs.SelectedIndex = page;
    }

    private void NavigatePage(object sender, RoutedEventArgs e)
    {
        if (busy || !HasVm) return;
        if (sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out int page) && page >= 0 && page < Tabs.Items.Count)
            Tabs.SelectedIndex = page;
    }

    private void SyncNavigation()
    {
        if (WorkspaceNavigation is null || Tabs is null || Tabs.SelectedIndex < 0) return;
        syncingNavigation = true;
        try {
            WorkspaceNavigation.SelectedItem = WorkspaceNavigation.Items.Cast<ListBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == Tabs.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            WorkspaceTitle.Text = ((TabItem)Tabs.SelectedItem).Header?.ToString() ?? "";
            AdvancedButton.Content = WorkspaceNavigation.SelectedItem is null ? WorkspaceTitle.Text + "  ▾" : "接続・詳細設定  ▾";
        } finally { syncingNavigation = false; }
    }

    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (busy) return;
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await RefreshState(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control) {
            if (e.Key == Key.N) { e.Handled = true; NewVm(sender, e); }
            else if (e.Key == Key.O) { e.Handled = true; OpenVm(sender, e); }
            else if (e.Key == Key.S && Tabs.SelectedIndex == 1) { e.Handled = true; SaveHardware(sender, e); }
        }
    }

    private void ClearLog(object sender, RoutedEventArgs e) => Log.Clear();

    private static IEnumerable<T> LogicalChildren<T>(DependencyObject root) where T : DependencyObject
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
            return InvalidField(Cpus, $"CPU は 1〜{Math.Min(64, Environment.ProcessorCount)} の整数で指定してください。");
        if (!ManagerValidation.Integer(Memory.Text, 4096, 131072, 4, out _))
            return InvalidField(Memory, "メモリは 4096〜131072 MiB、4 MiB 単位で指定してください。");
        if (!ManagerValidation.Resolution(Selected(Resolution))) return InvalidField(Resolution, "対応する起動時の解像度を選び直してください。");
        if (HardwareIso.IsChecked == true && !File.Exists(HardwareIsoPath.Text.Trim())) return InvalidField(HardwareIsoPath, "インストーラーを接続するには、存在する ISO / IMG を選択してください。");
        if (!string.IsNullOrWhiteSpace(HardwareIsoPath.Text) && !File.Exists(HardwareIsoPath.Text.Trim())) return InvalidField(HardwareIsoPath, "ISO が見つかりません。選び直すか、接続をオフにしてパスを空にしてください。");
        if (Bridge.IsChecked == true && string.IsNullOrWhiteSpace(Selected(Tap))) return InvalidField(Tap, "ブリッジ接続に使用する TAP アダプターを選択してください。");
        if (!ManagerValidation.Host(GuestHost.Text.Trim())) return InvalidField(GuestHost, "macOS の IP アドレスまたはホスト名を確認してください。");
        if (Microphone.IsChecked == true && Sound.IsChecked != true) return InvalidField(Microphone, "マイクを使用するには、スピーカー共有も有効にしてください。");
        return true;
    }

    private bool InvalidField(Control field, string message)
    {
        // Reveal the failing editor instead of focusing an invisible field on another page.
        for (int i = 0; i < Editors.Children.Count; i++)
            if (ReferenceEquals(Editors.Children[i], field) || LogicalChildren<Control>(Editors.Children[i]).Contains(field)) { Tabs.SelectedIndex = 1; Devices.SelectedIndex = i; break; }
        ShowError(message); field.Focus();
        return false;
    }
}
