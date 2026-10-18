using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReimsManager;

public partial class MainWindow
{
    // Uses the actual STA WPF renderer, not a browser mockup. Guest pixels are never fabricated.
#pragma warning disable WPF0001
    internal async Task CheckThemeRendering(string folder)
    {
        await CheckPresentationPolicy();
        await CheckSettingsDraftBehavior();
        await CheckNetworkChoices(folder);
        await CheckWifiCard(folder);
        await CheckCursorAidCard(folder);
        double width = Width, height = Height;
        string title = VmTitle.Text;
        int count = machines.Count;
        try {
            foreach (bool dark in new[] { false, true }) {
                Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
                foreach (bool compact in new[] { false, true }) {
                    Width = compact ? MinWidth : 1380;
                    Height = compact ? MinHeight : 880;
                    string prefix = $"{(dark ? "dark" : "light")}-{(compact ? "compact" : "normal")}";
                    currentState = "stopped"; State.Text = "完全停止"; StatusDetail.Text = "";
                    Tabs.SelectedIndex = 0; UpdatePowerControls(); await ArrangeCheck();
                    RequireManagementGeometry();
                    SaveImage(Path.Combine(folder, prefix + "-powered-off.png"));
                    Tabs.SelectedIndex = 1; Devices.SelectedIndex = 0; UpdatePowerControls(); await ArrangeCheck();
                    RequireSettingsGeometry();
                    if (SettingsSurface.Background is SolidColorBrush card &&
                        TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush canvas && Foreground is SolidColorBrush foreground &&
                        Contrast(foreground.Color, Blend(card.Color, canvas.Color)) < 4.5)
                        throw new Exception("Settings text fails 4.5:1 contrast");
                    SaveImage(Path.Combine(folder, prefix + "-settings.png"));
                    Tabs.SelectedIndex = 0; currentState = "running"; State.Text = "実行中";
                    presentation.ShowConsole(); UpdatePowerControls(); await ArrangeCheck();
                    RequireFocusedGeometry();
                    SaveImage(Path.Combine(folder, prefix + "-powered-on.png"));
                    currentState = "paused"; State.Text = "一時停止"; UpdatePowerControls(); await ArrangeCheck();
                    RequireFocusedGeometry();
                    if (SessionPauseButton.Tag?.ToString() != "Resume" || !SessionPauseButton.IsEnabled)
                        throw new Exception("The paused console cannot resume from its title bar");
                    SaveImage(Path.Combine(folder, prefix + "-paused.png"));
                    OpenSettings(this, new RoutedEventArgs()); await ArrangeCheck();
                    RequireSettingsGeometry();
                    // A timer update must not dismiss a settings page while the guest is running.
                    currentState = "running"; UpdatePowerControls(); await ArrangeCheck();
                    if (!SettingsOpen || SettingsPage.Visibility != Visibility.Visible)
                        throw new Exception("Live status dismissed the settings page");
                    if (!TryCloseSettings()) throw new Exception("Clean settings could not close");
                    await ArrangeCheck(); RequireFocusedGeometry();
                }
            }
            currentState = "stopped"; State.Text = "完全停止"; UpdatePowerControls();
            Tabs.SelectedIndex = 1;
            for (int category = 0; category < Devices.Items.Count; category++) {
                Devices.SelectedIndex = category;
                Devices.ScrollIntoView(Devices.SelectedItem);
                await ArrangeCheck();
                var item = (ListBoxItem)Devices.SelectedItem;
                if (Tabs.SelectedIndex.ToString() != item.Tag.ToString()) throw new Exception("Settings route is disconnected");
                RequireSettingsGeometry(); RequireInside(item, Devices, "Selected settings category");
                if (category < Editors.Children.Count && Editors.Children.Cast<UIElement>().Count(x => x.Visibility == Visibility.Visible) != 1)
                    throw new Exception("Settings shows multiple hardware editors");
                SaveImage(Path.Combine(folder, $"settings-category-{category:00}.png"));
            }
            Tabs.SelectedIndex = 3;
            foreach (var button in new[] { ApplyBootProfileButton, EnableGuestBridgeButton, DisableGuestBridgeButton }) {
                button.BringIntoView(); await ArrangeCheck();
                RequireInside(button, OpenCoreScroll, "OpenCore reapply controls");
                if (!button.IsEnabled) throw new Exception("An offline OpenCore control is disabled");
            }
            SaveImage(Path.Combine(folder, "opencore-bridge-controls.png")); OpenCoreScroll.ScrollToTop();
            Tabs.SelectedIndex = 0; Width = MinWidth; Height = MinHeight;
            EngineSettingsExpander.IsExpanded = true; await ArrangeCheck();
            SidebarScroll.ScrollToEnd(); await ArrangeCheck();
            RequireInside(SelectExistingEngineButton, SidebarScroll, "Engine controls");
            SaveImage(Path.Combine(folder, "compact-engine-settings.png"));
            EngineSettingsExpander.IsExpanded = false; SidebarScroll.ScrollToTop();
            PreferencesExpander.IsExpanded = true;
            await Task.Delay(500); await ArrangeCheck(); SidebarScroll.ScrollToEnd(); await ArrangeCheck();
            RequireInside(ImportPreferencesButton, SidebarScroll, "Preferences recovery controls");
            SaveImage(Path.Combine(folder, "compact-preferences.png"));
            PreferencesExpander.IsExpanded = false; SidebarScroll.ScrollToTop();
            // Native Fluent expanders animate; dispatcher-idle is not animation completion.
            await Task.Delay(500); await ArrangeCheck();
            await CheckDuplicateVmPaths(folder);
            VmTitle.Text = string.Concat(Enumerable.Repeat("日本語の長い仮想マシン名・", 10));
            for (int i = 0; i < 50; i++) machines.Add(new VmEntry($"検証用 macOS {i:00}", Path.Combine(folder, $"fixture-{i}.reimsvm"), Path.Combine(folder, $"fixture-{i}.data")));
            UpdateLibraryView(); await ArrangeCheck();
            SaveImage(Path.Combine(folder, "long-name-library.png")); RequireManagementGeometry();
            RequireInside(State, VmHeader, "State with long VM name");
            RequireInside(VmTitle, VmHeader, "Long VM name");
            SaveImage(Path.Combine(folder, "long-name-library.png"));
            currentState = "running"; State.Text = "実行中"; UpdatePowerControls(); await ArrangeCheck();
            RequireFocusedGeometry();
            SaveImage(Path.Combine(folder, "long-name-session.png"));
            currentState = "unknown"; State.Text = "状態を確認できません";
            StatusDetail.Text = "テスト用: 接続の応答がありません。VM画面は維持し、電源操作は無効化します。";
            ShowError("テスト用の接続エラー。設定やディスクを変更していません。");
            UpdatePowerControls(); await ArrangeCheck();
            if (!ConsoleFocused || SessionPauseButton.IsEnabled || SessionPowerButton.IsEnabled)
                throw new Exception("Unknown state loses the console or permits mutations");
            RequireInside(Feedback, AppLayout, "Error feedback");
            RequireInside(SessionCommands, TitleBar, "Error recovery navigation");
            SaveImage(Path.Combine(folder, "focused-connection-error.png"));
            DismissError(this, new RoutedEventArgs()); StatusDetail.Text = "";
            currentState = "stopped"; UpdatePowerControls(); await ArrangeCheck(); RequireManagementGeometry();
            // Exercise the new caption controls without closing the running test window.
            MaximizeWindow(this, new RoutedEventArgs()); await ArrangeCheck();
            RequireInside(CaptionCommands, TitleBar, "Maximized caption commands");
            MaximizeWindow(this, new RoutedEventArgs()); await ArrangeCheck();
        } finally {
            machines.RemoveRange(count, machines.Count - count); UpdateLibraryView();
            VmTitle.Text = title; Width = width; Height = height; WindowState = WindowState.Normal;
            Tabs.SelectedIndex = 0; currentState = "stopped"; StatusDetail.Text = "";
            Feedback.Visibility = Visibility.Collapsed; UpdatePowerControls();
            Application.Current.ThemeMode = ThemeMode.System;
        }
    }
#pragma warning restore WPF0001

    private async Task CheckCursorAidCard(string folder)
    {
        if (!selfTest) throw new InvalidOperationException("Cursor aid fixture access only.");
        string oldState=currentState, oldStateText=State.Text;
        int tab=Tabs.SelectedIndex, device=Devices.SelectedIndex;
        double width=Width, height=Height;
        try {
            Tabs.SelectedIndex=1;Devices.SelectedIndex=7;Width=MinWidth;Height=MinHeight;
            if (EnableCursorAidButton.Tag?.ToString()!="EnableCursorAid" || DisableCursorAidButton.Tag?.ToString()!="DisableCursorAid")
                throw new Exception("Cursor aid controls do not use the paired backend actions.");
            foreach (string state in new[] { "unknown", "unprepared", "stopped", "shutdown", "paused", "running" }) {
                currentState=state;State.Text=VmActionPolicy.StateLabel(state);UpdatePowerControls();
                if (EnableCursorAidButton.IsEnabled!=(state=="running") || DisableCursorAidButton.IsEnabled!=(state=="running"))
                    throw new Exception("Cursor aid controls are enabled outside a running VM.");
            }
            await ArrangeCheck();CursorAidCard.BringIntoView();await ArrangeCheck();
            RequireInside(EnableCursorAidButton,HardwareScroll,"Cursor compatibility enable");
            RequireInside(DisableCursorAidButton,HardwareScroll,"Cursor compatibility disable");
            MoveCursorExtendedButton.BringIntoView();await ArrangeCheck();
            RequireInside(MoveCursorMainButton,HardwareScroll,"Main display cursor move");
            RequireInside(MoveCursorExtendedButton,HardwareScroll,"Extended display cursor move");
            ShowHostCursorButton.BringIntoView();await ArrangeCheck();
            RequireInside(HideHostCursorButton,HardwareScroll,"Hide host cursor");
            RequireInside(ShowHostCursorButton,HardwareScroll,"Show host cursor");
            SaveImage(Path.Combine(folder,"cursor-aid-running-compact.png"));
            ExtendedDisplayCard.BringIntoView();await ArrangeCheck();
            RequireInside(LightenExtendedDisplayButton,HardwareScroll,"Extended display performance preset");
            RequireInside(RestoreExtendedDisplayButton,HardwareScroll,"Extended display restoration");
            SaveImage(Path.Combine(folder,"extended-display-compact.png"));
            File.WriteAllText(Path.Combine(folder,"cursor-aid-page-result.json"),"{\"existing_display_page\":true,\"explicit_on_off_actions\":true,\"running_only\":true,\"compact_actions_reachable\":true,\"no_guest_calls\":true}");
        } finally {
            currentState=oldState;State.Text=oldStateText;Tabs.SelectedIndex=tab;Devices.SelectedIndex=device;Width=width;Height=height;UpdatePowerControls();
        }
    }

    private async Task CheckDuplicateVmPaths(string folder)
    {
        string name = "macOS Tahoe（同名の確認）", query = LibrarySearch.Text;
        double width = Width, height = Height;
        int count = machines.Count;
        var entries = new[] { "Existing VMs", "Virtual Machines" }.Select(location => {
            string directory = Path.Combine(Path.GetPathRoot(folder)!, location, "日本語 macOS Tahoe");
            return new VmEntry(name, Path.Combine(directory, "macOS Tahoe.reimsvm"), directory);
        }).ToArray();
        try {
            Width = 1380; Height = 880;
            machines.AddRange(entries); LibrarySearch.Text = name; UpdateLibraryView(); await ArrangeCheck();
            foreach (var entry in entries) {
                Library.ScrollIntoView(entry); await ArrangeCheck();
                if (Library.ItemContainerGenerator.ContainerFromItem(entry) is not ListBoxItem row)
                    throw new Exception("Duplicate VM row did not render");
                var path = FindPath(row) ?? throw new Exception("VM path subtitle is missing");
                if (entry.Name != name || path.Text != entry.Directory || path.ToolTip?.ToString() != entry.Directory ||
                    path.TextTrimming != TextTrimming.CharacterEllipsis || path.TextWrapping != TextWrapping.NoWrap ||
                    System.Windows.Automation.AutomationProperties.GetName(row) != name ||
                    System.Windows.Automation.AutomationProperties.GetHelpText(row) != entry.Directory)
                    throw new Exception("Duplicate VM paths are indistinguishable or inaccessible");
                RequireInside(path, row, "Single-line VM path");
            }
            if (entries[0].Subtitle == entries[1].Subtitle) throw new Exception("Duplicate names hide distinct locations");
            SaveImage(Path.Combine(folder, "duplicate-vm-paths.png"));
        } finally {
            machines.RemoveRange(count, machines.Count - count); LibrarySearch.Text = query; UpdateLibraryView();
            Width = width; Height = height;
        }
        static TextBlock? FindPath(DependencyObject root) {
            if (root is TextBlock { Name: "VmPathLabel" } path) return path;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
                var found = FindPath(VisualTreeHelper.GetChild(root, i));
                if (found is not null) return found;
            }
            return null;
        }
    }

    private async Task ArrangeCheck()
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        UpdateLayout();
    }

    private void RequireManagementGeometry()
    {
        if (SettingsPage.Visibility != Visibility.Collapsed || ConsoleFocused || LibraryPane.Visibility != Visibility.Visible)
            throw new Exception("Powered-off layout is not the management view");
        foreach (FrameworkElement control in new FrameworkElement[] { NewVmCommand, StartButton, PowerOffButton, RestartButton, PowerMoreButton, ConsoleCommand, RefreshButton })
            RequireInside(control, CommandBar, "Management command bar");
        Rect console = ConsoleCommand.TransformToAncestor(CommandBar).TransformBounds(new Rect(ConsoleCommand.RenderSize));
        Rect refresh = RefreshButton.TransformToAncestor(CommandBar).TransformBounds(new Rect(RefreshButton.RenderSize));
        if (console.Right > refresh.Left + 1) throw new Exception("Management commands overlap");
        RequireInside(WorkspaceSurface, WorkstationLayout, "Workspace");
        RequireInside(AdvancedButton, WorkspaceNavSection, "Settings destination");
        if (AdvancedButton.ContextMenu is not null) throw new Exception("Settings categories still use a dropdown");
        if (Library.ActualHeight < 80 || Library.ActualHeight > SidebarScroll.ActualHeight + 1) throw new Exception($"Library viewport is not bounded: list={Library.ActualHeight:0.##}, sidebar={SidebarScroll.ActualHeight:0.##}, expanded={EngineSettingsExpander.IsExpanded}");
    }

    private void RequireSettingsGeometry()
    {
        if (WorkstationLayout.Visibility != Visibility.Collapsed || SettingsPage.Visibility != Visibility.Visible || !SettingsOpen)
            throw new Exception("Settings is not a dedicated page");
        RequireInside(SettingsSurface, SettingsPage, "Settings editor");
        RequireInside(SaveHardwareButton, SettingsFooter, "Apply settings");
        RequireInside(ReloadHardwareButton, SettingsFooter, "Cancel settings");
        RequireInside(SettingsOkButton, SettingsFooter, "OK settings");
        RequireInside(SettingsBackButton, SettingsPage, "Settings back button");
        if (SettingsSurface.ActualWidth < 400 || Devices.ActualHeight < 150) throw new Exception("Settings editor or category list is too small");
    }

    private void RequireFocusedGeometry()
    {
        if (!ConsoleFocused || ManagementCommands.Visibility != Visibility.Collapsed || LibraryPane.Visibility != Visibility.Collapsed ||
            WorkspaceNavSection.Visibility != Visibility.Collapsed || VmHeader.Visibility != Visibility.Collapsed ||
            OperationLog.Visibility != Visibility.Collapsed || WorkspaceStatusBar.Visibility != Visibility.Collapsed ||
            ConsoleFooter.Visibility != Visibility.Collapsed || SettingsPage.Visibility != Visibility.Collapsed)
            throw new Exception("Powered-on view still contains management chrome");
        foreach (FrameworkElement control in new FrameworkElement[] { LibraryCommand, SessionPowerButton, SessionPauseButton, SessionRestartButton, SessionSettingsButton, SessionCursorAidButton, SessionCursorMenuButton, SessionUsbButton, SessionSharingButton, SessionSnapshotButton, SessionPopOutButton, CaptionCommands })
            RequireInside(control, TitleBar, "Session title bar");
        if (SessionCursorAidButton.Tag?.ToString()!="EnableCursorAid" || SessionCursorAidButton.Content?.ToString()!="カーソルを表示" ||
            SessionCursorAidButton.IsEnabled!=(currentState=="running" && !IsUiBusy))
            throw new Exception("The one-click cursor toolbar action lost its label, route or running-only policy.");
        Rect icons = SessionCommands.TransformToAncestor(TitleBar).TransformBounds(new Rect(SessionCommands.RenderSize));
        Rect caption = CaptionCommands.TransformToAncestor(TitleBar).TransformBounds(new Rect(CaptionCommands.RenderSize));
        if (icons.Right > caption.Left || TitleBar.ActualHeight > 45) throw new Exception("Session title bar overlaps or is not compact");
        if (Math.Abs(WorkspaceSurface.ActualWidth - WorkstationLayout.ActualWidth) > 2 ||
            Math.Abs(WorkspaceSurface.ActualHeight - WorkstationLayout.ActualHeight) > 2)
            throw new Exception("Console does not fill the workspace");
    }

    private static async Task CheckPresentationPolicy()
    {
        var mode = new WorkspacePresentation();
        mode.Observe("one", "stopped"); if (mode.Focused(false, false)) throw new Exception("Stopped VM focused");
        mode.Observe("one", "running"); if (!mode.Focused(false, false)) throw new Exception("Running VM did not focus");
        foreach (string state in new[] { "paused", "unknown", "running", "suspended" }) {
            mode.Observe("one", state); if (!mode.Focused(false, false)) throw new Exception("Console layout flickers during transitions");
        }
        if (mode.Focused(true, false) || mode.Focused(false, true)) throw new Exception("Settings/pop-out retained console focus");
        mode.ShowManagement(); mode.Observe("one", "running");
        if (mode.Focused(false, false)) throw new Exception("Polling undid the Library choice");
        mode.ShowConsole(); if (!mode.Focused(false, false)) throw new Exception("Return to console failed");
        mode.Observe("two", "unknown"); if (mode.Focused(false, false)) throw new Exception("New VM inherited console state");
        mode.Observe("two", "running"); mode.Observe("two", "shutdown"); if (mode.Focused(false, false)) throw new Exception("Shutdown retained console focus");
        mode.Observe(null, "running"); if (mode.Focused(false, false)) throw new Exception("Missing VM focused");
        foreach (bool hardwareOk in new[] { false, true }) foreach (bool identityOk in new[] { false, true }) {
            int hardwareCalls = 0, identityCalls = 0;
            bool ok = await SettingsCommitSequence.Apply(true, true,
                () => { hardwareCalls++; return Task.FromResult(hardwareOk); },
                () => { identityCalls++; return Task.FromResult(identityOk); });
            if (hardwareCalls != 1 || identityCalls != (hardwareOk ? 1 : 0) || ok != (hardwareOk && identityOk))
                throw new Exception("Settings save sequence did not stop on failure");
        }
    }

    private async Task CheckSettingsDraftBehavior()
    {
        currentState = "stopped"; Tabs.SelectedIndex = 1; Devices.SelectedIndex = 0;
        UpdatePowerControls();
        RememberHardwareDraft(); RememberIdentityDraft();
        string memory = Memory.Text, serial = IdentitySerial.Text;
        long version = contextVersion;
        Memory.Text = "12288"; IdentitySerial.Text = "UNSAVED-FIXTURE";
        if (!hardwareDirty || !identityDirty || !SaveHardwareButton.IsEnabled) throw new Exception("Settings dirty state not reflected");
        Devices.SelectedIndex = 8; Devices.SelectedIndex = 10;
        if (Memory.Text != "12288" || IdentitySerial.Text != "UNSAVED-FIXTURE") throw new Exception("Category navigation discarded edits");
        if (TryCloseSettings() || !SettingsOpen) throw new Exception("Settings closed without discard consent");
        bool sharing = ClipboardSharing.IsChecked == true;
        if (!RestoreSettingsDrafts() || Memory.Text != memory || IdentitySerial.Text != serial || hardwareDirty || identityDirty ||
            contextVersion != version || (ClipboardSharing.IsChecked == true) != sharing)
            throw new Exception("Cancel did not restore only the staged forms");
        Memory.Text = "not-a-number"; Tabs.SelectedIndex = 3;
        if (await ApplySettingsDrafts() || Tabs.SelectedIndex != 1 || Devices.SelectedIndex != 0 || !hardwareDirty)
            throw new Exception("Invalid hardware did not stay open on its failing editor");
        RestoreSettingsDrafts(); Feedback.Visibility = Visibility.Collapsed;
        applyingSettings = true; UpdatePowerControls();
        if (TryCloseSettings() || SettingsOkButton.IsEnabled || ReloadHardwareButton.IsEnabled || Devices.IsEnabled)
            throw new Exception("Settings can be abandoned while committing");
        applyingSettings = false; UpdatePowerControls();
        if (!TryCloseSettings()) throw new Exception("Clean settings failed to close");
        currentState = "running"; UpdatePowerControls();
        OpenSettings(this, new RoutedEventArgs());
        currentState = "stopped"; UpdatePowerControls();
        if (!SettingsOpen) throw new Exception("Power-off during editing dismissed settings");
        TryCloseSettings();
    }

    private static void RequireInside(FrameworkElement element, FrameworkElement viewport, string description)
    {
        Rect bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        if (element.ActualWidth < 1 || element.ActualHeight < 1 || bounds.Left < -1 || bounds.Top < -1 ||
            bounds.Right > viewport.ActualWidth + 1 || bounds.Bottom > viewport.ActualHeight + 1)
            throw new Exception($"{description}: {element.Name} falls outside its viewport ({bounds}; {viewport.RenderSize})");
    }

    private static Color Blend(Color front, Color back)
    {
        double alpha = front.A / 255.0;
        return Color.FromRgb((byte)Math.Round(front.R * alpha + back.R * (1 - alpha)),
            (byte)Math.Round(front.G * alpha + back.G * (1 - alpha)),
            (byte)Math.Round(front.B * alpha + back.B * (1 - alpha)));
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { double c = value / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color value) => 0.2126 * Channel(value.R) + 0.7152 * Channel(value.G) + 0.0722 * Channel(value.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    // Runs on the real STA WPF dispatcher. No QEMU, host settings or user data are touched.
    private void CheckWorkspaceBehavior()
    {
        if (!selfTest) throw new InvalidOperationException("Fixture access only");
        latest = JsonSerializer.SerializeToElement(new {
            state = "stopped", prepared = true, memory_mib = 8196, cpus = 2, disk_gib = 128,
            disk = "fixture.data/disks/current.qcow2", iso = "", with_installer = false,
            network = "nat", network_adapter = "", guest_host = "", audio = "none",
            microphone = false, usb_backend = "winusb", resolution = "1920x1080"
        });
        LoadHardware(); loadedHardware = true;
        currentState = "stopped"; UpdatePowerControls();
        if (Memory.Text != "8196" || Math.Abs(MemorySlider.Value * 1024 - 8196) > 0.01 || hardwareDirty || SaveHardwareButton.IsEnabled)
            throw new Exception("Loading exact 4 MiB memory values dirtied or rounded the settings");
        Memory.Text = "12292";
        if (!hardwareDirty || !SaveHardwareButton.IsEnabled || Math.Abs(MemorySlider.Value * 1024 - 12292) > 0.01)
            throw new Exception("Text input is not synchronized with the memory slider");
        MemorySlider.Value = 16;
        if (Memory.Text != "16384") throw new Exception("Slider input did not update memory text");
        var first = selectedEntry!;
        var second = new VmEntry("macOS - 日本語の長い名前で画面を確認するための検証用仮想マシン", Path.Combine(Path.GetDirectoryName(first.File)!, "second.reimsvm"), Path.Combine(Path.GetDirectoryName(first.File)!, "second.data"));
        machines.Add(second); UpdateLibraryView();
        Library.SelectedItem = second;
        if (selectedEntry != first || (Library.SelectedItem as VmEntry) != first || !hardwareDirty)
            throw new Exception("Changing machines discarded unsaved settings");
        hardwareDirty = false;
        long version = contextVersion;
        var token = statusCancellation.Token;
        currentState = "running"; UpdatePowerControls();
        Library.SelectedItem = second;
        if (selectedEntry != second || contextVersion <= version || !token.IsCancellationRequested || currentState != "unknown" || StartButton.IsEnabled || PowerOffButton.IsEnabled)
            throw new Exception("Switching VM retained the previous VM's state or pending requests");
        LibrarySearch.Text = "no-match-fixture";
        if (Library.Items.Count != 0 || selectedEntry != second || !HasVm)
            throw new Exception("Library filtering changed the active VM");
        LibrarySearch.Clear(); Library.SelectedItem = first;
        var unavailable = VmLibrary.Registered(Path.Combine(Path.GetDirectoryName(first.File)!, "disconnected.reimsvm"));
        machines.Add(unavailable); UpdateLibraryView(); Library.SelectedItem = unavailable;
        if (selectedEntry != unavailable || HasVm || StartButton.IsEnabled || WorkspaceContent.Visibility != Visibility.Visible || !Library.Items.Contains(unavailable))
            throw new Exception("An unavailable VM disappeared or permits guest operations");
        Library.SelectedItem = first; machines.Remove(unavailable); UpdateLibraryView();
        foreach (string state in new[] { "unknown", "paused", "shutdown", "stopped", "running", "unprepared", "suspended" }) {
            currentState = state; UpdatePowerControls();
            if (StartButton.IsEnabled != VmActionPolicy.CanRun("Start", state, true, true) ||
                MenuStart.IsEnabled != StartButton.IsEnabled ||
                PowerOffButton.IsEnabled != VmActionPolicy.CanRun("PowerOff", state, true, true) ||
                ResumeButton.IsEnabled != VmActionPolicy.CanRun("Resume", state, true, true))
                throw new Exception("Power controls disagree with their handler policy: " + state);
        }
        currentState = "stopped"; hardwareDirty = true; UpdatePowerControls();
        busy = true; UpdatePowerControls();
        if (StartButton.IsEnabled || SaveHardwareButton.IsEnabled || Library.IsEnabled || WorkspaceNavigation.IsEnabled)
            throw new Exception("Busy state leaves mutating controls enabled");
        busy = false; hardwareDirty = identityDirty = false;
        latest = JsonSerializer.SerializeToElement(new {
            memory_mib = 8192, cpus = 2, disk_gib = 128, disk = "fixture.data/disks/current.qcow2", iso = "",
            with_installer = false, network = "nat", network_adapter = "", guest_host = "", audio = "none",
            microphone = false, usb_backend = "winusb", resolution = "1920x1080"
        });
        LoadHardware(); loadedHardware = true;
        VmSummary.Text = "2 vCPU  ·  8 GiB メモリ  ·  128 GiB ディスク  ·  1920x1080";
        StatusDetail.Text = ""; UpdatePowerControls();
        AppendLog(new string('x', 100000)); AppendLog("\nfixture-end\n");
        if (Log.Text.Length > 65536 || !Log.Text.Replace("\r\n", "\n").EndsWith("fixture-end\n")) throw new Exception("UI diagnostic log is not bounded");
        Log.Clear();
        foreach (ListBoxItem item in WorkspaceNavigation.Items) {
            WorkspaceNavigation.SelectedItem = item;
            if (Tabs.SelectedIndex.ToString() != item.Tag.ToString()) throw new Exception("Workspace navigation is disconnected");
        }
    }
}
