using System.Windows;
using System.Windows.Controls;

namespace ReimsManager;

public partial class MainWindow
{
    private bool applyingSettings;
    private int hardwareCategory;
    private HardwareDraft? committedHardware;
    private IdentityDraft? committedIdentity;

    private sealed record HardwareDraft(string Memory, string Cpus, string Iso, string Host, string Resolution,
        object? Tap, string Uplink, string Forwarder, int UsbBackend, bool Installer, bool Nat, bool Sound, bool Microphone);
    private sealed record IdentityDraft(string Model, string Serial, string Mlb, string Uuid, string Rom);

    private HardwareDraft CaptureHardwareDraft() => new(Memory.Text, Cpus.Text, HardwareIsoPath.Text, GuestHost.Text,
        Selected(Resolution), Tap.SelectedItem, SelectedUplink, hardwareForwarder, UsbBackend.SelectedIndex, HardwareIso.IsChecked == true,
        Nat.IsChecked == true, Sound.IsChecked == true, Microphone.IsChecked == true);
    private IdentityDraft CaptureIdentityDraft() => new(IdentityModel.Text, IdentitySerial.Text, IdentityMlb.Text, IdentityUuid.Text, IdentityRom.Text);
    private void RememberHardwareDraft() => committedHardware = CaptureHardwareDraft();
    private void RememberIdentityDraft() => committedIdentity = CaptureIdentityDraft();

    private static bool SameNetwork(HardwareDraft left, HardwareDraft right) =>
        left.Nat == right.Nat && Equals(left.Tap, right.Tap) && left.Uplink == right.Uplink &&
        left.Host == right.Host && left.Forwarder == right.Forwarder;
    private bool NetworkDraftChanged => !loadedHardware || committedHardware is not { } saved ||
        !SameNetwork(CaptureHardwareDraft(), saved);

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !HasVm) return;
        Tabs.SelectedIndex = 1;
        SyncNavigation(); UpdatePowerControls();
    }

    private void OpenSettingsCategory(object sender, RoutedEventArgs e)
    {
        if (IsUiBusy || !HasVm) return;
        if (sender is FrameworkElement { Tag: "usb" }) {
            hardwareCategory = 5;
            Tabs.SelectedIndex = 1;
            Devices.SelectedIndex = 5;
            SyncNavigation(); UpdatePowerControls();
        }
    }

    private bool TryCloseSettings()
    {
        if (IsUiBusy) return false;
        if (!SettingsOpen) return true;
        if (!DiscardPendingChanges()) return false;
        if (!RestoreSettingsDrafts()) return false;
        Tabs.SelectedIndex = 0;
        UpdatePowerControls(); UpdateConsole();
        return true;
    }
    private void CloseSettings(object sender, RoutedEventArgs e) => TryCloseSettings();
    private void CancelSettings(object sender, RoutedEventArgs e) => TryCloseSettings();

    // Restore only staged form values. Never reset the VM context, clipboard service,
    // USB registrations, snapshots or any committed disk/guest operation on Cancel.
    private bool RestoreSettingsDrafts()
    {
        if (hardwareDirty && committedHardware is null || identityDirty && committedIdentity is null) {
            ShowError("元の設定をまだ読み込めていません。変更を保持しました。状態を更新して確認してください。");
            return false;
        }
        loadingHardware = loadingIdentity = true;
        try {
            if (hardwareDirty && committedHardware is { } h) {
                Memory.Text = h.Memory; Cpus.Text = h.Cpus; HardwareIsoPath.Text = h.Iso; GuestHost.Text = h.Host;
                Resolution.SelectedItem = Resolution.Items.Cast<ComboBoxItem>().FirstOrDefault(x => x.Content?.ToString() == h.Resolution);
                Tap.SelectedItem = h.Tap; SelectSavedUplink(h.Uplink); UsbBackend.SelectedIndex = h.UsbBackend;
                hardwareForwarder=h.Forwarder;
                HardwareIso.IsChecked = h.Installer; Nat.IsChecked = h.Nat; Bridge.IsChecked = !h.Nat;
                Sound.IsChecked = h.Sound; Microphone.IsChecked = h.Microphone;
            }
            if (identityDirty && committedIdentity is { } i) {
                IdentityModel.Text = i.Model; IdentitySerial.Text = i.Serial; IdentityMlb.Text = i.Mlb;
                IdentityUuid.Text = i.Uuid; IdentityRom.Text = i.Rom;
            }
            hardwareDirty = identityDirty = false;
        } finally { loadingHardware = loadingIdentity = false; }
        UpdatePowerControls(); return true;
    }

    private async void ApplySettings(object sender, RoutedEventArgs e) => await ApplySettingsDrafts();
    private async void SaveAndCloseSettings(object sender, RoutedEventArgs e)
    {
        if (await ApplySettingsDrafts()) { Tabs.SelectedIndex = 0; UpdatePowerControls(); UpdateConsole(); }
    }

    private async Task<bool> ApplySettingsDrafts()
    {
        if (IsUiBusy || !HasVm) return false;
        if (!hardwareDirty && !identityDirty) return true;
        if (hardwareDirty && (!RequireAction("SetHardware") || !ValidateHardware())) return false;
        // Ask before either write. Cancelling identity consent must not save hardware first.
        if (identityDirty && (!RequireAction("SetIdentity") || !ConfirmIdentityChange())) return false;
        applyingSettings = true; UpdatePowerControls();
        try {
            bool saved = await SettingsCommitSequence.Apply(hardwareDirty, identityDirty,
                SaveHardwareCore, () => SaveIdentityCore(confirm: false));
            if (saved && !hardwareDirty && !identityDirty)
                ShowSettingsSaved("変更を保存しました。次回起動から使用します。");
            return saved && !hardwareDirty && !identityDirty;
        } finally { applyingSettings = false; UpdatePowerControls(); }
    }
}
