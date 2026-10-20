using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;

namespace ReimsManager;

public partial class MainWindow
{
    private readonly WorkspacePresentation presentation = new();
    private bool wasConsoleFocused;
    private GridLength libraryWidth = new(236);
    private string settingsFeedback = "";
    private bool SettingsOpen => Tabs.SelectedIndex > 0;
    private bool IsUiBusy => busy || applyingSettings || completingOperation;
    private bool ConsoleFocused => presentation.Focused(SettingsOpen, consoleDetached);

    private void UpdateWorkspacePresentation()
    {
        if (TitleBar is null || SettingsPage is null) return;
        presentation.Observe(selectedEntry?.File, currentState);
        bool focus = ConsoleFocused;
        bool settingsVisible = HasVm && SettingsOpen;
        bool manager = !focus && !settingsVisible;

        if (focus != wasConsoleFocused) {
            if (focus) libraryWidth = LibraryColumn.Width;
            LibraryColumn.MinWidth = focus ? 0 : 200;
            LibraryColumn.Width = focus ? new GridLength(0) : libraryWidth;
            WorkstationLayout.ColumnDefinitions[1].Width = new GridLength(focus ? 0 : 5);
            wasConsoleFocused = focus;
        }
        WorkstationLayout.Visibility = settingsVisible ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settingsVisible ? Visibility.Visible : Visibility.Collapsed;
        ManagementCommands.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        LibraryPane.Visibility = LibrarySplitter.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        VmHeader.Visibility = manager && selectedEntry is not null ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceNavSection.Visibility = manager && HasVm ? Visibility.Visible : Visibility.Collapsed;
        OperationLog.Visibility = WorkspaceStatusBar.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = selectedEntry is not null ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceContent.Visibility = selectedEntry is not null ? Visibility.Visible : Visibility.Collapsed;
        MainWorkspace.Margin = focus ? new Thickness(0) : new Thickness(8, 14, 14, 10);
        ConsolePage.Margin = focus ? new Thickness(0) : new Thickness(8);
        ConsoleFooter.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceSurface.BorderThickness = ConsoleFrame.BorderThickness = new Thickness(focus ? 0 : 1);
        WorkspaceSurface.CornerRadius = new CornerRadius(focus ? 0 : 8);
        ConsoleFrame.CornerRadius = new CornerRadius(focus ? 0 : 5);
        SessionCommands.Visibility = focus ? Visibility.Visible : Visibility.Collapsed;
        ApplicationBrand.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        SessionTitle.Visibility = HasVm ? Visibility.Visible : Visibility.Collapsed;
        Title = selectedEntry is not null ? $"{selectedEntry.Name} — Reims Manager" : "Reims Manager";

        // Progress and errors have a separate layout row, never an overlay on HwndHost.
        StatusDetail.Visibility = !string.IsNullOrWhiteSpace(StatusDetail.Text) &&
            (!focus || currentState == "unknown" || currentState.Contains("error", StringComparison.Ordinal) || currentState == "guest-panicked")
            ? Visibility.Visible : Visibility.Collapsed;
        OverviewActions.Visibility = currentState is "stopped" or "shutdown" or "unprepared" ? Visibility.Visible : Visibility.Collapsed;
        OverviewActions.IsEnabled = HasVm && !IsUiBusy;
        OverviewStartButton.Visibility = currentState is "stopped" or "shutdown" ? Visibility.Visible : Visibility.Collapsed;
        OverviewSetupButton.Visibility = currentState == "unprepared" ? Visibility.Visible : Visibility.Collapsed;
        OverviewExitButton.Visibility = currentState == "shutdown" ? Visibility.Visible : Visibility.Collapsed;
        OverviewStartButton.IsEnabled = CanOperate("Start") && !applyingSettings;
        ConsoleConnectButton.Visibility = CanOperate("Console") ? Visibility.Visible : Visibility.Collapsed;
        ConsoleConnectButton.IsEnabled = CanOperate("Console") && !applyingSettings;
        foreach (var button in LogicalChildren<Button>(ConsolePage))
            if (button.Tag is string action && !int.TryParse(action, out _)) button.IsEnabled = CanOperate(action) && !applyingSettings;
        OverviewSetupButton.IsEnabled = HasVm && HasEngine && !IsUiBusy;
        foreach (var item in SessionPowerButton.ContextMenu.Items.OfType<MenuItem>()) item.IsEnabled = CanOperate(item.Tag?.ToString() ?? "") && !applyingSettings;
        SessionPowerButton.IsEnabled = new[] { "PowerOff", "Exit", "ForceExit" }.Any(CanOperate) && !applyingSettings;
        bool resume = currentState is "paused" or "prelaunch" or "suspended";
        SessionPauseButton.Tag = resume ? "Resume" : "Pause";
        SessionPauseButton.Content = resume ? "\uE768" : "\uE769";
        SessionPauseButton.ToolTip = resume ? "再開" : "一時停止";
        System.Windows.Automation.AutomationProperties.SetName(SessionPauseButton, resume ? "再開" : "一時停止");
        SessionPauseButton.IsEnabled = CanOperate(resume ? "Resume" : "Pause") && !applyingSettings;
        SessionRestartButton.IsEnabled = CanOperate("Restart") && !applyingSettings;
        SessionPopOutButton.IsEnabled = CanOperate("Console") && !applyingSettings;
        SessionCursorAidButton.IsEnabled = CanOperate("EnableCursorAid") && !applyingSettings;
        foreach (var item in SessionCursorMenuButton.ContextMenu.Items.OfType<MenuItem>())
            if (item.Tag is string action) item.IsEnabled = CanOperate(action) && !applyingSettings;
        SessionCursorMenuButton.IsEnabled = !IsUiBusy && HasVm;
        LibraryCommand.IsEnabled = !IsUiBusy;
        SessionSettingsButton.IsEnabled = SessionUsbButton.IsEnabled = SessionSharingButton.IsEnabled = SessionSnapshotButton.IsEnabled = HasVm && !IsUiBusy;
        bool editHardware = !IsUiBusy && loadedHardware && CanOperate("SetHardware");
        foreach (UIElement field in new UIElement[] { Memory, MemorySlider, Cpus, HardwareIsoPath, GuestHost, Tap, UsbBackend,
            HardwareIso, HardwareIsoBrowseButton, Nat, Bridge, Sound, Microphone, DiskSize }) field.IsEnabled = editHardware;
        Uplink.IsEnabled = editHardware && Bridge.IsChecked == true;
        NetworkTapDetails.Visibility=Bridge.IsChecked==true && Uplink.SelectedItem is not NetworkUplink { Kind:"wifi" } && !WifiForwarderState.IsMacNat(hardwareForwarder) ? Visibility.Visible : Visibility.Collapsed;
        Physical.IsEnabled = BridgeTap.IsEnabled = !IsUiBusy && CanOperate("CreateBridge");
        Resolution.IsEnabled = !IsUiBusy && loadedHardware && (CanOperate("SetHardware") || CanOperate("ApplyResolution"));
        foreach (UIElement field in new UIElement[] { IdentityModel, IdentitySerial, IdentityMlb, IdentityUuid, IdentityRom })
            field.IsEnabled = !IsUiBusy && CanOperate("SetIdentity");
        Devices.IsEnabled = Tabs.IsEnabled = !IsUiBusy;
        AdvancedButton.IsEnabled = HasVm && !IsUiBusy;
        Library.IsEnabled = LibrarySearch.IsEnabled = WorkspaceNavigation.IsEnabled = !IsUiBusy;
        UpdateSettingsWorkflow();
        // Explicitly hide the native child when the settings page owns the workspace.
        // Its HwndHost stays at the same place in the tree: no detach/reparent on navigation.
        if (settingsVisible) ConsoleView.Visibility = Visibility.Hidden;
        UpdateWifiControls();
    }

    private void ShowSettingsSaved(string message)
    {
        settingsFeedback = message;
        UpdateSettingsWorkflow();
    }

    private void ClearSettingsFeedback() => settingsFeedback = "";

    private void UpdateSettingsWorkflow()
    {
        if (SettingsScopeHint is null || SettingsStateNotice is null) return;
        bool pending = hardwareDirty || identityDirty;
        bool canSave = (!hardwareDirty || CanOperate("SetHardware")) && (!identityDirty || CanOperate("SetIdentity"));
        SettingsBackButton.IsEnabled = !IsUiBusy;
        ReloadHardwareButton.IsEnabled = !IsUiBusy && pending;
        SaveHardwareButton.IsEnabled = !IsUiBusy && pending && canSave;
        SettingsOkButton.IsEnabled = !IsUiBusy && (!pending || canSave);
        SettingsOkButton.Content = pending ? "保存して閉じる" : "閉じる";

        string changed = hardwareDirty && identityDirty ? "ハードウェア・識別情報" : hardwareDirty ? "ハードウェア" : "識別情報";
        SettingsHint.Text = IsUiBusy ? "処理が完了するまでお待ちください…" : pending
            ? canSave ? $"未保存の変更: {changed} (Ctrl+S)" : $"未保存の変更: {changed}。保存には VM の完全停止が必要です。"
            : settingsFeedback.Length > 0 ? settingsFeedback : loadedHardware
                ? "未保存の変更はありません。" : "保存済みの構成を確認しています。";
        SettingsHeading.Text = Tabs.SelectedIndex switch {
            4 => "スナップショット", 5 => "macOS のインストール", 6 => "macOS と接続", 7 => "LAN 接続の準備",
            _ => "仮想マシンの設定"
        };
        SettingsScopeHint.Text = Tabs.SelectedIndex switch {
            1 when hardwareCategory == 2 => "ディスクの拡張・整理は、それぞれの操作ボタンで実行します。",
            1 when hardwareCategory == 5 => "USB の登録・解除は即時反映。転送方式は「変更を保存」で確定します。",
            1 when hardwareCategory == 7 => "解像度を macOS に適用すると、次回起動の設定も保存します。カーソル操作はすぐに反映します。",
            1 => "「変更を保存」で確定した設定は、次回の VM 起動から使用します。",
            2 => "自動共有のオン・オフは、この VM の設定としてすぐに保存します。",
            3 => "識別情報は保存時に確認します。起動・GPU の操作は、その場で実行します。",
            4 => "スナップショットの保存・復元は、このページの操作ボタンで実行します。",
            5 => "インストーラーを選択し、「仮想マシンを作成」でディスクを準備します。",
            6 => "接続は「指紋を照合して接続」で実行します。",
            7 => "接続の準備・解除は、それぞれの操作ボタンで実行します。",
            _ => ""
        };

        bool requiresStopped = Tabs.SelectedIndex is 1 or 3 or 4 or 7;
        bool showState = SettingsOpen && (!HasEngine || currentState == "unknown" ||
            requiresStopped && (currentState != "stopped" || Tabs.SelectedIndex == 1 && !loadedHardware));
        SettingsStateNotice.Visibility = showState ? Visibility.Visible : Visibility.Collapsed;
        SettingsRefreshButton.IsEnabled = HasVm && HasEngine && !IsUiBusy && !refreshing;
        string? action = null, label = null;
        if (!HasEngine) {
            SettingsStateHeading.Text = "エンジンを準備してください";
            SettingsStateDescription.Text = "エンジンの保存先を設定すると、VM の構成と動作状態を確認できます。";
            action = "SetupEngine"; label = "エンジンを設定…";
        } else if (currentState == "running") {
            SettingsStateHeading.Text = "VM は実行中です";
            SettingsStateDescription.Text = Tabs.SelectedIndex == 1 && hardwareCategory == 7
                ? "解像度・カーソルの操作は実行できます。その他の起動時設定は、VM の完全終了後に保存できます。"
                : Tabs.SelectedIndex == 1 && hardwareCategory == 5
                    ? "USB の接続操作は実行できます。転送方式の変更は、VM の完全終了後に保存できます。"
                    : "設定変更・ディスク操作には完全停止が必要です。macOS をシャットダウンしてから、VM を完全終了してください。";
            action = "PowerOff"; label = "シャットダウン";
        } else if (currentState == "shutdown") {
            SettingsStateHeading.Text = "macOS は電源オフです。VM はまだ完全停止していません";
            SettingsStateDescription.Text = "VM を完全終了すると、ハードウェア設定やディスクを変更できます。";
            action = "Exit"; label = "VM を完全終了";
        } else if (currentState is "paused" or "prelaunch" or "suspended") {
            SettingsStateHeading.Text = "一時停止中は構成を変更できません";
            SettingsStateDescription.Text = "再開して macOS をシャットダウンし、VM を完全終了してから変更できます。";
            action = "Resume"; label = "VM を再開";
        } else if (currentState == "unprepared") {
            SettingsStateHeading.Text = "セットアップがまだ完了していません";
            SettingsStateDescription.Text = "インストーラーと仮想ディスクを準備してから、構成を変更できます。";
            action = "SetupPage"; label = "セットアップを開く";
        } else {
            SettingsStateHeading.Text = currentState == "stopped" ? "構成を読み込んでいます" : "VM の状態を確認してください";
            SettingsStateDescription.Text = "「状態を更新」で再確認できます。入力中の変更は保持します。";
        }
        SettingsStateActionButton.Tag = action;
        SettingsStateActionButton.Content = label;
        SettingsStateActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        SettingsStateActionButton.IsEnabled = !IsUiBusy && (action is "SetupEngine" or "SetupPage" || action is not null && CanOperate(action));
    }

    private void ShowLibrary(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !TryCloseSettings()) return;
        presentation.ShowManagement();
        UpdatePowerControls();
    }

    private void OpenLibraryConsole(object sender, MouseButtonEventArgs e)
    {
        // Only a real selected row, not a double click on empty library space.
        if (e.OriginalSource is DependencyObject origin &&
            ItemsControl.ContainerFromElement(Library, origin) is ListBoxItem && CanOperate("Console")) DockConsole(sender, e);
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void WindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeCommand is null || ChromeRoot is null) return;
        MaximizeCommand.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        // WindowChrome preserves native resizing; keep content inside its maximized frame.
        ChromeRoot.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
    }
}
