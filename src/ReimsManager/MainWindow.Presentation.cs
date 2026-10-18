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
    private bool SettingsOpen => Tabs.SelectedIndex > 0;
    private bool IsUiBusy => busy || applyingSettings;
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
        OverviewStartButton.IsEnabled = CanOperate("Start") && !applyingSettings;
        ConsoleConnectButton.Visibility = CanOperate("Console") ? Visibility.Visible : Visibility.Collapsed;
        ConsoleConnectButton.IsEnabled = CanOperate("Console") && !applyingSettings;
        foreach (var button in LogicalChildren<Button>(ConsolePage))
            if (button.Tag is string action) button.IsEnabled = CanOperate(action) && !applyingSettings;
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
        LibraryCommand.IsEnabled = !IsUiBusy;
        SessionSettingsButton.IsEnabled = SessionUsbButton.IsEnabled = SessionSharingButton.IsEnabled = SessionSnapshotButton.IsEnabled = HasVm && !IsUiBusy;
        bool editHardware = !IsUiBusy && loadedHardware && CanOperate("SetHardware");
        foreach (UIElement field in new UIElement[] { Memory, MemorySlider, Cpus, HardwareIsoPath, GuestHost, Tap, UsbBackend,
            HardwareIso, Nat, Bridge, Sound, Microphone, DiskSize }) field.IsEnabled = editHardware;
        Uplink.IsEnabled = editHardware && Bridge.IsChecked == true;
        NetworkTapDetails.Visibility=Bridge.IsChecked==true && Uplink.SelectedItem is not NetworkUplink { Kind:"wifi" } && !WifiForwarderState.IsMacNat(hardwareForwarder) ? Visibility.Visible : Visibility.Collapsed;
        Physical.IsEnabled = BridgeTap.IsEnabled = !IsUiBusy && CanOperate("CreateBridge");
        Resolution.IsEnabled = !IsUiBusy && loadedHardware && (CanOperate("SetHardware") || CanOperate("ApplyResolution"));
        foreach (UIElement field in new UIElement[] { IdentityModel, IdentitySerial, IdentityMlb, IdentityUuid, IdentityRom })
            field.IsEnabled = !IsUiBusy && CanOperate("SetIdentity");
        Devices.IsEnabled = Tabs.IsEnabled = !IsUiBusy;
        AdvancedButton.IsEnabled = HasVm && !IsUiBusy;
        Library.IsEnabled = LibrarySearch.IsEnabled = WorkspaceNavigation.IsEnabled = !IsUiBusy;
        SettingsBackButton.IsEnabled = ReloadHardwareButton.IsEnabled = !IsUiBusy;
        SaveHardwareButton.IsEnabled = !IsUiBusy && (hardwareDirty || identityDirty) &&
            (!hardwareDirty || CanOperate("SetHardware")) && (!identityDirty || CanOperate("SetIdentity"));
        SettingsOkButton.IsEnabled = !IsUiBusy;
        SettingsHint.Text = IsUiBusy ? "設定を処理しています…" : hardwareDirty || identityDirty
            ? currentState == "stopped" ? "未保存の変更があります (Ctrl+S)。" : "未保存の変更があります。保存には完全停止が必要です。"
            : currentState == "stopped" ? "変更はありません。" : "ハードウェア・識別情報の保存は完全停止時のみです。";
        // Explicitly hide the native child when the settings page owns the workspace.
        // Its HwndHost stays at the same place in the tree: no detach/reparent on navigation.
        if (settingsVisible) ConsoleView.Visibility = Visibility.Hidden;
        UpdateWifiControls();
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
