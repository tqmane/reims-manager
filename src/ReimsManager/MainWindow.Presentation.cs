using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ReimsManager;

public partial class MainWindow
{
    private readonly WorkspacePresentation presentation = new();
    private bool wasConsoleFocused;
    private GridLength libraryWidth = new(228);
    private string settingsFeedback = "";
    private bool SettingsOpen => Tabs.SelectedIndex != 1;
    private bool OverlayOpen => AppSettingsOverlay?.Visibility == Visibility.Visible;
    private bool IsUiBusy => busy || applyingSettings || completingOperation;
    private bool ConsoleFocused => presentation.Focused(SettingsOpen || OverlayOpen, consoleDetached);

    private static Brush StatusDotBrush(string state) => state switch {
        "running" => new SolidColorBrush(Color.FromRgb(0x16, 0xC6, 0x0C)),
        "paused" or "prelaunch" or "suspended" => new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C)),
        "shutdown" or "unknown" or "guest-panicked" => new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
        _ => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A))
    };

    private void UpdateWorkspacePresentation()
    {
        if (TitleBar is null || Tabs is null || PendingSaveBar is null) return;
        presentation.Observe(selectedEntry?.File, currentState);
        bool focus = ConsoleFocused;
        if (fullScreen && (!focus || !ConsoleView.Attached)) ExitFullScreen(refresh: false);

        TitleBar.Visibility = GlobalNotices.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        ChromeRoot.BorderThickness = new Thickness(fullScreen ? 0 : 1);
        SessionFullScreenButton.IsEnabled = !IsUiBusy && (fullScreen || CanOperate("Console"));
        SessionFullScreenButton.Content = fullScreen ? "\uE73F" : "\uE740";
        SessionFullScreenButton.ToolTip = fullScreen ? UiText.T("全画面を終了") : UiText.T("全画面表示 (F11)");
        System.Windows.Automation.AutomationProperties.SetName(SessionFullScreenButton, fullScreen ? UiText.T("全画面を終了") : UiText.T("全画面表示"));

        bool manager = !focus;
        if (focus != wasConsoleFocused) {
            if (focus) libraryWidth = LibraryColumn.Width;
            LibraryColumn.MinWidth = focus ? 0 : 196;
            LibraryColumn.Width = focus ? new GridLength(0) : libraryWidth;
            WorkstationLayout.ColumnDefinitions[1].Width = new GridLength(focus ? 0 : 1);
            wasConsoleFocused = focus;
        }

        WorkstationLayout.Visibility = Visibility.Visible;
        ManagementCommands.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        LibraryPane.Visibility = LibrarySplitter.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        VmHeader.Visibility = manager && selectedEntry is not null ? Visibility.Visible : Visibility.Collapsed;
        PivotBarContainer.Visibility = manager && selectedEntry is not null ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = selectedEntry is not null ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceContent.Visibility = selectedEntry is not null ? Visibility.Visible : Visibility.Collapsed;
        ConsoleToolbar.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;

        MainWorkspace.Margin = focus ? new Thickness(0) : new Thickness(24, 10, 24, 16);
        ConsoleFrame.BorderThickness = new Thickness(focus ? 0 : 1);
        ConsoleFrame.CornerRadius = new CornerRadius(focus ? 0 : 8);
        SessionCommands.Visibility = focus ? Visibility.Visible : Visibility.Collapsed;
        ApplicationBrand.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        SessionTitle.Visibility = focus && HasVm ? Visibility.Visible : Visibility.Collapsed;
        Title = selectedEntry is not null ? $"{selectedEntry.Name} — Reims Manager" : "Reims Manager";

        if (StateDot is not null) StateDot.Fill = StatusDotBrush(currentState);
        if (EngineStatusDot is not null) {
            EngineStatusDot.Fill = HasEngine
                ? new SolidColorBrush(Color.FromRgb(0x16, 0xC6, 0x0C))
                : new SolidColorBrush(Color.FromRgb(0xD8, 0x7E, 0x00));
        }
        if (EngineSummary is not null) {
            EngineSummary.Visibility = HasEngine ? Visibility.Collapsed : Visibility.Visible;
        }

        StatusDetail.Visibility = !string.IsNullOrWhiteSpace(StatusDetail.Text) &&
            (!focus || currentState == "unknown" || currentState.Contains("error", StringComparison.Ordinal) || currentState == "guest-panicked")
            ? Visibility.Visible : Visibility.Collapsed;

        bool unprepared = HasVm && currentState == "unprepared";
        if (SetupCard is not null) SetupCard.Visibility = unprepared ? Visibility.Visible : Visibility.Collapsed;
        if (OverviewReadyPanel is not null) OverviewReadyPanel.Visibility = HasVm && !unprepared ? Visibility.Visible : Visibility.Collapsed;

        ConsoleConnectButton.Visibility = CanOperate("Console") ? Visibility.Visible : Visibility.Collapsed;
        ConsoleConnectButton.IsEnabled = CanOperate("Console") && !applyingSettings;
        if (ConsolePopOutButton is not null) ConsolePopOutButton.IsEnabled = CanOperate("Console") && !applyingSettings;

        foreach (var button in LogicalChildren<Button>(ConsolePage))
            if (button.Tag is string action && !int.TryParse(action, out _))
                button.IsEnabled = CanOperate(action) && !applyingSettings;

        foreach (var item in SessionPowerButton.ContextMenu.Items.OfType<MenuItem>())
            item.IsEnabled = CanOperate(item.Tag?.ToString() ?? "") && !applyingSettings;
        SessionPowerButton.IsEnabled = new[] { "PowerOff", "Exit", "ForceExit" }.Any(CanOperate) && !applyingSettings;
        bool resume = currentState is "paused" or "prelaunch" or "suspended";
        SessionPauseButton.Tag = resume ? "Resume" : "Pause";
        SessionPauseButton.Content = resume ? "\uE768" : "\uE769";
        SessionPauseButton.ToolTip = resume ? UiText.T("再開") : UiText.T("一時停止");
        System.Windows.Automation.AutomationProperties.SetName(SessionPauseButton, resume ? UiText.T("再開") : UiText.T("一時停止"));
        SessionPauseButton.IsEnabled = CanOperate(resume ? "Resume" : "Pause") && !applyingSettings;
        SessionRestartButton.IsEnabled = CanOperate("Restart") && !applyingSettings;
        SessionPopOutButton.IsEnabled = CanOperate("Console") && !applyingSettings;
        SessionCursorAidButton.IsEnabled = CanOperate("EnableCursorAid") && !applyingSettings;
        foreach (var item in SessionCursorMenuButton.ContextMenu.Items.OfType<MenuItem>())
            if (item.Tag is string action) item.IsEnabled = CanOperate(action) && !applyingSettings;
        SessionCursorMenuButton.IsEnabled = !IsUiBusy && HasVm;
        SessionSettingsButton.IsEnabled = SessionUsbButton.IsEnabled = SessionSharingButton.IsEnabled = HasVm && !IsUiBusy;

        bool editHardware = !IsUiBusy && loadedHardware && CanOperate("SetHardware");
        foreach (UIElement field in new UIElement[] { Memory, MemorySlider, Cpus, HardwareIsoPath, GuestHost, Tap, UsbBackend,
            HardwareIso, HardwareIsoBrowseButton, Nat, Bridge, Sound, Microphone })
            field.IsEnabled = editHardware;
        if (DiskSize is not null) DiskSize.IsEnabled = !IsUiBusy && CanOperate("ExpandDisk");
        if (Iso is not null) Iso.IsEnabled = NewDiskSize.IsEnabled = StartAfterSetup.IsEnabled = !IsUiBusy && CanOperate("Setup");
        Uplink.IsEnabled = editHardware && Bridge.IsChecked == true;
        NetworkTapDetails.Visibility = Bridge.IsChecked == true && Uplink.SelectedItem is not NetworkUplink { Kind: "wifi" } && !WifiForwarderState.IsMacNat(hardwareForwarder)
            ? Visibility.Visible : Visibility.Collapsed;
        Physical.IsEnabled = BridgeTap.IsEnabled = !IsUiBusy && CanOperate("CreateBridge");
        bool canRes = !IsUiBusy && loadedHardware && (CanOperate("SetHardware") || CanOperate("ApplyResolution"));
        Resolution.IsEnabled = canRes;
        if (ConsoleResolution is not null) ConsoleResolution.IsEnabled = !IsUiBusy && loadedHardware && CanOperate("ApplyResolution");
        if (ConnectIso is not null) ConnectIso.IsEnabled = !IsUiBusy && HasVm;
        if (CopyToGuestButton is not null && CopyFromGuestButton is not null) {
            var clipVis = CanOperate("Clipboard") ? Visibility.Visible : Visibility.Collapsed;
            CopyToGuestButton.Visibility = CopyFromGuestButton.Visibility = clipVis;
        }
        foreach (UIElement field in new UIElement[] { IdentityModel, IdentitySerial, IdentityMlb, IdentityUuid, IdentityRom })
            field.IsEnabled = !IsUiBusy && CanOperate("SetIdentity");

        Tabs.IsEnabled = !IsUiBusy;
        Library.IsEnabled = LibrarySearch.IsEnabled = WorkspaceNavigation.IsEnabled = SettingsSectionNav.IsEnabled = !IsUiBusy;
        AddVmMenuButton.IsEnabled = OpenAppSettingsButton.IsEnabled = !IsUiBusy;

        UpdateSettingsWorkflow();
        if (SettingsOpen || OverlayOpen) ConsoleView.Visibility = Visibility.Hidden;
        UpdateWifiControls();
    }

    private void ShowSettingsSaved(string message)
    {
        settingsFeedback = message;
        UpdateSettingsWorkflow();
    }

    private void ClearSettingsFeedback() => settingsFeedback = "";

    private void DismissSavedFeedback(object sender, RoutedEventArgs e)
    {
        ClearSettingsFeedback();
        UpdateSettingsWorkflow();
    }

    private void UpdateSettingsWorkflow()
    {
        if (PendingSaveBar is null || SettingsStateNotice is null) return;
        bool pending = hardwareDirty || identityDirty;
        bool canSave = (!hardwareDirty || CanOperate("SetHardware")) && (!identityDirty || CanOperate("SetIdentity"));

        PendingSaveBar.Visibility = !ConsoleFocused && HasVm && (pending || settingsFeedback.Length > 0)
            ? Visibility.Visible : Visibility.Collapsed;
        ReloadHardwareButton.Visibility = SaveHardwareButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        DismissSaveFeedbackButton.Visibility = !pending && settingsFeedback.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReloadHardwareButton.IsEnabled = !IsUiBusy && pending;
        SaveHardwareButton.IsEnabled = !IsUiBusy && pending && canSave;
        SaveBarDot.Fill = new SolidColorBrush(pending ? Color.FromRgb(0xD8, 0x7E, 0x00) : Color.FromRgb(0x16, 0xC6, 0x0C));

        string changed = hardwareDirty && identityDirty ? UiText.T("ハードウェア・識別情報") : hardwareDirty ? UiText.T("ハードウェア") : UiText.T("識別情報");
        SettingsHint.Text = IsUiBusy ? UiText.T("処理が完了するまでお待ちください…") : pending
            ? canSave ? UiText.Format($"未保存の変更: {changed} (Ctrl+S)") : UiText.Format($"未保存の変更: {changed}。保存には VM の完全停止が必要です。")
            : settingsFeedback;

        bool requiresOfflineNotice = HasVm && !ConsoleFocused &&
            (!HasEngine || currentState is "running" or "shutdown" or "paused" or "prelaunch" or "suspended" or "unknown" ||
             (currentState == "stopped" && !loadedHardware));
        SettingsStateNotice.Visibility = Tabs.SelectedIndex == 3 && requiresOfflineNotice ? Visibility.Visible : Visibility.Collapsed;
        SettingsRefreshButton.IsEnabled = HasVm && HasEngine && !IsUiBusy && !refreshing;

        string? action = null, label = null;
        if (!HasEngine) {
            SettingsStateHeading.Text = UiText.T("エンジンを準備してください");
            SettingsStateDescription.Text = UiText.T("エンジンの保存先を設定すると、VM の構成と動作状態を確認できます。");
            action = "SetupEngine"; label = UiText.T("エンジンを設定…");
        } else if (currentState == "running") {
            SettingsStateHeading.Text = UiText.T("VM は実行中です");
            SettingsStateDescription.Text = UiText.T("構成・ディスク変更には完全停止が必要です。USB 接続や解像度適用は各タブから行えます。");
            action = "PowerOff"; label = UiText.T("シャットダウン");
        } else if (currentState == "shutdown") {
            SettingsStateHeading.Text = UiText.T("macOS は電源オフです。VM はまだ完全停止していません");
            SettingsStateDescription.Text = UiText.T("VM を完全終了すると、ハードウェア設定やディスクを変更できます。");
            action = "Exit"; label = UiText.T("VM を完全終了");
        } else if (currentState is "paused" or "prelaunch" or "suspended") {
            SettingsStateHeading.Text = UiText.T("一時停止中は構成を変更できません");
            SettingsStateDescription.Text = UiText.T("再開して macOS をシャットダウンし、VM を完全終了してから変更できます。");
            action = "Resume"; label = UiText.T("VM を再開");
        } else {
            SettingsStateHeading.Text = currentState == "stopped" ? UiText.T("構成を読み込んでいます") : UiText.T("VM の状態を確認してください");
            SettingsStateDescription.Text = UiText.T("「更新」で再確認できます。入力中の変更は保持します。");
        }
        SettingsStateActionButton.Tag = action;
        SettingsStateActionButton.Content = label;
        SettingsStateActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        SettingsStateActionButton.IsEnabled = !IsUiBusy && (action is "SetupEngine" or "SetupPage" || (action is not null && CanOperate(action)));
    }

    private void ShowLibrary(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !TryCloseSettings()) return;
        presentation.ShowManagement();
        Tabs.SelectedIndex = 0;
        UpdatePowerControls();
    }

    private void OpenLibraryConsole(object sender, MouseButtonEventArgs e)
    {
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
        ChromeRoot.Margin = !fullScreen && WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
    }
}
