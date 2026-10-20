using System.IO;
using System.Windows;

namespace ReimsManager;

public partial class MainWindow
{
    // Exercise the actual controls and navigation without guest or host mutations.
    private async Task CheckSettingsWorkflow(string folder)
    {
        if (!selfTest) throw new InvalidOperationException("Workflow fixture access only.");
        string state = currentState, stateText = State.Text, feedback = settingsFeedback, memory = Memory.Text;
        object? resolution = Resolution.SelectedItem;
        var hardware = committedHardware;
        bool wasLoaded = loadedHardware, dirtyHardware = hardwareDirty, dirtyIdentity = identityDirty;
        bool wasCompleting = completingOperation;
        int page = Tabs.SelectedIndex, category = Devices.SelectedIndex;
        double width = Width, height = Height;
        try {
            Width = MinWidth; Height = MinHeight;
            currentState = "stopped"; State.Text = VmActionPolicy.StateLabel(currentState);
            loadedHardware = true; hardwareDirty = identityDirty = false;
            Tabs.SelectedIndex = 1; Devices.SelectedIndex = 0;
            RememberHardwareDraft();
            ShowSettingsSaved("設定を保存しました。次回起動から使用します。");
            UpdatePowerControls();
            if (!SettingsHint.Text.Contains("保存しました", StringComparison.Ordinal) || SaveHardwareButton.IsEnabled ||
                ReloadHardwareButton.IsEnabled || SettingsOkButton.Content?.ToString() != "閉じる")
                throw new Exception("Clean settings do not show stable save feedback and a clear close action.");

            Memory.Text = memory == "12288" ? "16384" : "12288";
            if (!hardwareDirty || !SaveHardwareButton.IsEnabled || !SettingsOkButton.IsEnabled ||
                !ReloadHardwareButton.IsEnabled || SettingsHint.Text.Contains("保存しました", StringComparison.Ordinal) ||
                SettingsOkButton.Content?.ToString() != "保存して閉じる")
                throw new Exception("Editing settings did not replace saved feedback with pending changes.");

            currentState = "running"; State.Text = VmActionPolicy.StateLabel(currentState); UpdatePowerControls();
            if (SaveHardwareButton.IsEnabled || SettingsOkButton.IsEnabled || HardwareIsoBrowseButton.IsEnabled ||
                SettingsStateNotice.Visibility != Visibility.Visible || SettingsStateActionButton.Tag?.ToString() != "PowerOff")
                throw new Exception("Running settings permit an impossible save or hide the shutdown recovery action.");
            await ArrangeCheck();
            RequireSettingsGeometry();
            RequireInside(SettingsStateActionButton, SettingsSurface, "Settings shutdown recovery");
            SaveImage(Path.Combine(folder, "settings-running-pending.png"));

            foreach (var sample in new[] {
                (State: "shutdown", Action: "Exit"), (State: "paused", Action: "Resume"),
                (State: "suspended", Action: "Resume"), (State: "unprepared", Action: "SetupPage")
            }) {
                currentState = sample.State; UpdatePowerControls();
                if (SettingsStateActionButton.Tag?.ToString() != sample.Action || !SettingsStateActionButton.IsEnabled)
                    throw new Exception("Settings recovery is not actionable for " + sample.State);
            }
            currentState = "unknown"; UpdatePowerControls();
            if (SettingsStateActionButton.Visibility != Visibility.Collapsed || !SettingsRefreshButton.IsEnabled ||
                SaveHardwareButton.IsEnabled || SettingsOkButton.IsEnabled)
                throw new Exception("Unknown state must offer a refresh and retain edits without allowing a save.");

            currentState = "stopped"; State.Text = VmActionPolicy.StateLabel(currentState);
            if (!RestoreSettingsDrafts()) throw new Exception("Workflow draft fixture could not restore its baseline.");
            Tabs.SelectedIndex = 1; Devices.SelectedIndex = 7;
            await ArrangeCheck(); HardwareScroll.ScrollToBottom(); await ArrangeCheck();
            if (HardwareScroll.VerticalOffset <= 0) throw new Exception("Display navigation fixture did not exercise a long editor.");
            Devices.SelectedIndex = 0; await ArrangeCheck();
            if (HardwareScroll.VerticalOffset > 1) throw new Exception("A new hardware category inherited another page's scroll position.");
            RequireInside(Memory, HardwareScroll, "Memory after display navigation");

            InvalidField(GuestHost, "テスト用: macOS の接続先を確認してください。");
            await ArrangeCheck();
            if (Devices.SelectedIndex != 4 || Tabs.SelectedIndex != 1)
                throw new Exception("Validation did not navigate to the failing hardware category.");
            RequireInside(GuestHost, HardwareScroll, "Visible validation target");
            Feedback.Visibility = Visibility.Collapsed;

            Tabs.SelectedIndex = 4; UpdatePowerControls(); await ArrangeCheck();
            if (SettingsHeading.Text != "スナップショット" || !SettingsScopeHint.Text.Contains("操作ボタン", StringComparison.Ordinal))
                throw new Exception("Snapshot navigation is presented as an unrelated hardware save.");
            SaveImage(Path.Combine(folder, "snapshot-workflow-compact.png"));

            Tabs.SelectedIndex = 0; currentState = "unprepared"; UpdatePowerControls();
            if (OverviewSetupButton.Visibility != Visibility.Visible || !OverviewSetupButton.IsEnabled ||
                OverviewStartButton.Visibility != Visibility.Collapsed)
                throw new Exception("Unprepared VM does not offer an available setup action.");
            currentState = "shutdown"; UpdatePowerControls();
            if (OverviewExitButton.Visibility != Visibility.Visible || !OverviewExitButton.IsEnabled)
                throw new Exception("Powered-off VM hides the complete-exit step needed for configuration.");

            completingOperation = true; Tabs.SelectedIndex = 1; UpdatePowerControls();
            if (SettingsOkButton.IsEnabled || Devices.IsEnabled || Library.IsEnabled || CanOperate("Start"))
                throw new Exception("Post-operation refresh permits navigation or another mutation before completion.");
        } finally {
            loadingHardware = true;
            try { Memory.Text = memory; Resolution.SelectedItem = resolution; }
            finally { loadingHardware = false; }
            committedHardware = hardware;
            loadedHardware = wasLoaded; hardwareDirty = dirtyHardware; identityDirty = dirtyIdentity;
            completingOperation = wasCompleting; settingsFeedback = feedback;
            currentState = state; State.Text = stateText;
            Width = width; Height = height; Devices.SelectedIndex = category; Tabs.SelectedIndex = page;
            Feedback.Visibility = Visibility.Collapsed; UpdatePowerControls();
        }
    }
}
