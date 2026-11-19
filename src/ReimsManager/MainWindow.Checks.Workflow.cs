using System.IO;
using System.Windows;

namespace ReimsManager;

public partial class MainWindow
{
    private async Task CheckSettingsWorkflow(string folder)
    {
        if (!selfTest) throw new InvalidOperationException("Workflow fixture access only.");
        string state = currentState, stateText = State.Text, feedback = settingsFeedback, memory = Memory.Text;
        object? resolution = Resolution.SelectedItem;
        var hardware = committedHardware;
        bool wasLoaded = loadedHardware, dirtyHardware = hardwareDirty, dirtyIdentity = identityDirty;
        bool wasCompleting = completingOperation;
        int page = Tabs.SelectedIndex;
        double width = Width, height = Height;
        try {
            Width = MinWidth; Height = MinHeight;
            currentState = "stopped"; State.Text = VmActionPolicy.StateLabel(currentState);
            loadedHardware = true; hardwareDirty = identityDirty = false;
            Tabs.SelectedIndex = 3; SettingsSectionNav.SelectedIndex = 0;
            RememberHardwareDraft();
            ShowSettingsSaved("設定を保存しました。次回起動から使用します。");
            UpdatePowerControls();
            if (PendingSaveBar.Visibility != Visibility.Visible || !SettingsHint.Text.Contains("保存しました", StringComparison.Ordinal) ||
                SaveHardwareButton.Visibility != Visibility.Collapsed)
                throw new Exception("Clean saved feedback banner did not show properly.");

            Memory.Text = memory == "12288" ? "16384" : "12288";
            if (!hardwareDirty || !SaveHardwareButton.IsEnabled || SaveHardwareButton.Visibility != Visibility.Visible ||
                !ReloadHardwareButton.IsEnabled || SettingsHint.Text.Contains("保存しました", StringComparison.Ordinal))
                throw new Exception("Editing settings did not replace saved feedback with pending changes.");

            currentState = "running"; State.Text = VmActionPolicy.StateLabel(currentState); UpdatePowerControls();
            if (SaveHardwareButton.IsEnabled || HardwareIsoBrowseButton.IsEnabled ||
                SettingsStateNotice.Visibility != Visibility.Visible || SettingsStateActionButton.Tag?.ToString() != "PowerOff")
                throw new Exception("Running settings permit an impossible save or hide the shutdown recovery action.");
            await ArrangeCheck();
            RequireInside(SettingsStateActionButton, WorkspaceSurface, "Settings shutdown recovery");
            SaveImage(Path.Combine(folder, "settings-running-pending.png"));

            foreach (var sample in new[] {
                (State: "shutdown", Action: "Exit"), (State: "paused", Action: "Resume"),
                (State: "suspended", Action: "Resume")
            }) {
                currentState = sample.State; UpdatePowerControls();
                if (SettingsStateActionButton.Tag?.ToString() != sample.Action || !SettingsStateActionButton.IsEnabled)
                    throw new Exception("Settings recovery is not actionable for " + sample.State);
            }
            currentState = "unknown"; UpdatePowerControls();
            if (SettingsStateActionButton.Visibility != Visibility.Collapsed || !SettingsRefreshButton.IsEnabled || SaveHardwareButton.IsEnabled)
                throw new Exception("Unknown state must offer a refresh and retain edits without allowing a save.");

            currentState = "stopped"; State.Text = VmActionPolicy.StateLabel(currentState);
            if (!RestoreSettingsDrafts()) throw new Exception("Workflow draft fixture could not restore its baseline.");

            InvalidField(GuestHost, "テスト用: macOS の接続先を確認してください。");
            await ArrangeCheck();
            if (Tabs.SelectedIndex != 3 || SettingsSectionTabs.SelectedIndex != 2)
                throw new Exception("Validation did not navigate to the failing network section.");
            RequireInside(GuestHost, BridgeScroll, "Visible validation target");
            Feedback.Visibility = Visibility.Collapsed;

            Tabs.SelectedIndex = 0; currentState = "unprepared"; UpdatePowerControls(); await ArrangeCheck();
            if (SetupCard.Visibility != Visibility.Visible || !SetupCard.IsEnabled || StartButton.IsEnabled)
                throw new Exception("Unprepared VM does not reveal inline setup card.");
            SaveImage(Path.Combine(folder, "unprepared-onboarding-compact.png"));

            currentState = "shutdown"; UpdatePowerControls();
            if (!CanOperate("Exit") || !CanOperate("Start"))
                throw new Exception("Shutdown VM should permit Exit and cold Start.");

            completingOperation = true; Tabs.SelectedIndex = 3; UpdatePowerControls();
            if (SaveHardwareButton.IsEnabled || WorkspaceNavigation.IsEnabled || Library.IsEnabled || CanOperate("Start"))
                throw new Exception("Post-operation refresh permits navigation or another mutation before completion.");
        } finally {
            loadingHardware = true;
            try { Memory.Text = memory; if (resolution is not null) Resolution.SelectedItem = resolution; }
            finally { loadingHardware = false; }
            committedHardware = hardware;
            loadedHardware = wasLoaded; hardwareDirty = dirtyHardware; identityDirty = dirtyIdentity;
            completingOperation = wasCompleting; settingsFeedback = feedback;
            currentState = state; State.Text = stateText;
            Width = width; Height = height; Tabs.SelectedIndex = page;
            Feedback.Visibility = Visibility.Collapsed; UpdatePowerControls();
        }
    }
}
