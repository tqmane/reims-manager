using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ReimsManager;

public partial class MainWindow
{
    // Regression checks use the real controls and save handlers with an isolated
    // backend fixture. They never connect to macOS or read the host clipboard.
    private async Task CheckSettingsReliability(string folder)
    {
        if (!selfTest || selectedEntry is null) throw new InvalidOperationException("Settings fixture requires a selected VM.");
        var entry = selectedEntry;
        var previousBackend = controlFixtureBackend;
        var previousStatus = latest;
        string previousState = currentState;
        int page = Tabs.SelectedIndex, category = Devices.SelectedIndex;
        var preferences = new Dictionary<string, bool>(settings.ClipboardByVm, StringComparer.OrdinalIgnoreCase);
        var second = new VmEntry("Sharing isolation fixture", Path.Combine(folder, "sharing-second.reimsvm"), Path.Combine(folder, "sharing-second.data"));
        string savedResolution = "1920x1080";
        bool failResolution = false, checkCompletion = false, installerPreference = false, attachedInstaller = true;
        int resolutionWrites = 0, completionChecks = 0;
        string Status() => JsonSerializer.Serialize(new {
            state = "running", prepared = true, snapshots = Array.Empty<string>(), taps = Array.Empty<string>(),
            memory_mib = 8192, cpus = 2, disk_gib = 128, disk = "fixture.qcow2", iso = "",
            with_installer = attachedInstaller, connect_installer = installerPreference,
            network = "nat", network_adapter = "", guest_host = "", audio = "none", microphone = false,
            usb_backend = "winusb", resolution = savedResolution
        });
        controlFixtureBackend = action => {
            if (action == "GetNetworkUplinks") return Task.FromResult("{\"schema\":1,\"uplinks\":[],\"tap_adapters\":[]}");
            if (action == "GetIdentity") return Task.FromResult(JsonSerializer.Serialize(new {
                model = IdentityModel.Text, serial = IdentitySerial.Text, mlb = IdentityMlb.Text, uuid = IdentityUuid.Text, rom = IdentityRom.Text
            }));
            if (action == "Status") {
                if (checkCompletion) {
                    completionChecks++;
                    if (!completingOperation || !IsUiBusy || Library.IsEnabled || SettingsOkButton.IsEnabled)
                        throw new Exception("A settings operation unlocked its VM before the final status read.");
                    Library.SelectedItem = second;
                    if (selectedEntry != entry || Library.SelectedItem as VmEntry != entry)
                        throw new Exception("Post-save refresh allowed the committed draft to move to another VM.");
                }
                return Task.FromResult(Status());
            }
            if (action == "ApplyResolution") {
                if (failResolution) throw new IOException("Fixture: the guest rejected the display mode.");
                resolutionWrites++;
                savedResolution = Selected(Resolution);
                return Task.FromResult("Fixture display applied and saved.");
            }
            throw new Exception("Unexpected settings fixture action: " + action);
        };
        machines.Add(second); UpdateLibraryView();
        try {
            hardwareDirty = identityDirty = false;
            currentState = "stopped"; loadedHardware = true; UpdatePowerControls();
            ClipboardSharing.IsChecked = true;
            if (!settings.ClipboardEnabled(entry.File) || !ClipboardSharing.IsEnabled || clipboardTimer.IsEnabled)
                throw new Exception("Sharing preference cannot be saved while stopped or started a fixture transfer.");
            currentState = "running"; UpdatePowerControls();
            lastClipboard = "Authored synchronization cache sentinel";
            var clipboardToken = clipboardCancel.Token;
            var cachedStatus = latest;
            try {
                // Stay synchronous and stop before yielding; no clipboard Tick or SSH runs.
                clipboardTimer.Start();
                currentState = "unknown"; UpdatePowerControls();
                var manualClipboardButtons = LogicalChildren<Button>(Tabs).Where(x => x.Tag?.ToString() == "Clipboard").ToArray();
                if (!clipboardTimer.IsEnabled || clipboardToken.IsCancellationRequested || lastClipboard != "Authored synchronization cache sentinel" ||
                    !CanShareClipboard || !CanOperate("Clipboard") || currentState != "unknown" || !latest.Equals(cachedStatus) ||
                    manualClipboardButtons.Length != 2 || manualClipboardButtons.Any(x => !x.IsEnabled))
                    throw new Exception("A QMP failure cancelled independent clipboard SSH or invented running state.");
                foreach (string state in new[] { "paused", "shutdown", "stopped" }) {
                    currentState = state; UpdatePowerControls();
                    if (clipboardTimer.IsEnabled || CanShareClipboard || CanOperate("Clipboard"))
                        throw new Exception("Clipboard transfer is eligible while " + state);
                }
                if (!clipboardToken.IsCancellationRequested || lastClipboard is not null)
                    throw new Exception("Stopping the VM did not cancel the old clipboard session.");
            } finally { clipboardTimer.Stop(); }
            Library.SelectedItem = second;
            if (ClipboardSharing.IsChecked == true || settings.ClipboardEnabled(second.File))
                throw new Exception("A different VM inherited another VM's clipboard sharing.");
            Library.SelectedItem = entry;
            if (ClipboardSharing.IsChecked != true || !settings.ClipboardEnabled(entry.File))
                throw new Exception("VM selection reset a saved clipboard preference.");
            ClipboardSharing.IsChecked = false;
            RestoreClipboardPreference();
            if (ClipboardSharing.IsChecked == true || settings.ClipboardEnabled(entry.File))
                throw new Exception("Explicit clipboard disable was not retained.");

            latest = JsonSerializer.Deserialize<JsonElement>(Status()); LoadHardware(); loadedHardware = true;
            if (HardwareIso.IsChecked == true || ConnectIso.IsChecked == true || hardwareDirty)
                throw new Exception("Live installer attachment overwrote the saved next-boot preference.");
            installerPreference = true; attachedInstaller = false;
            latest = JsonSerializer.Deserialize<JsonElement>(Status()); LoadHardware();
            if (HardwareIso.IsChecked != true || ConnectIso.IsChecked != true || hardwareDirty)
                throw new Exception("A one-off boot without installer cleared the stored preference.");
            installerPreference = false;
            latest = JsonSerializer.Deserialize<JsonElement>(Status()); LoadHardware();

            currentState = "stopped"; UpdatePowerControls();
            Memory.Text = "12288";
            if (await Operate("Start") || Tabs.SelectedIndex != 1 || !hardwareDirty || Memory.Text != "12288")
                throw new Exception("Start hid pending settings and launched with different saved values.");
            Resolution.SelectedItem = Resolution.Items.Cast<ComboBoxItem>().Single(x => x.Content?.ToString() == "2560x1440");
            currentState = "running"; UpdatePowerControls(); checkCompletion = true;
            if (!await ApplyResolutionCore() || resolutionWrites != 1 || completionChecks == 0 || currentState != "running" || savedResolution != "2560x1440" ||
                !hardwareDirty || committedHardware?.Memory != "8192" || committedHardware?.Resolution != "2560x1440" || Memory.Text != "12288")
                throw new Exception("Live display save discarded unrelated pending settings or failed to retain its own value.");
            if (!RestoreSettingsDrafts() || Memory.Text != "8192" || Selected(Resolution) != "2560x1440" || hardwareDirty)
                throw new Exception("Discard reverted a resolution that was already applied and saved.");

            Resolution.SelectedItem = Resolution.Items.Cast<ComboBoxItem>().Single(x => x.Content?.ToString() == "3840x2160");
            failResolution = true;
            if (await ApplyResolutionCore() || resolutionWrites != 1 || currentState != "running" || savedResolution != "2560x1440" || !hardwareDirty ||
                Selected(Resolution) != "3840x2160" || committedHardware?.Resolution != "2560x1440")
                throw new Exception("Rejected live resolution appeared saved or lost the user's pending choice.");
            RestoreSettingsDrafts();
            checkCompletion = false;
            savedResolution = "1280x720";
            await RefreshDisplayedState();
            if (Selected(Resolution) != "1280x720" || hardwareDirty)
                throw new Exception("Explicit refresh updated the summary but left the clean form stale.");
            currentState = "stopped"; UpdatePowerControls(); Memory.Text = "12288";
            savedResolution = "1920x1080";
            await RefreshDisplayedState();
            if (Memory.Text != "12288" || !hardwareDirty || Selected(Resolution) != "1280x720")
                throw new Exception("Explicit refresh discarded a pending settings draft.");
            RestoreSettingsDrafts();
            File.WriteAllText(Path.Combine(folder, "settings-reliability-result.json"), JsonSerializer.Serialize(new {
                clipboard_vm_switch = true, clipboard_disable_retained = true, no_fixture_clipboard_io = true,
                saved_installer_separate_from_runtime = true, live_resolution_commit = true,
                unrelated_edits_retained = true, failed_resolution_stays_pending = true,
                vm_selection_locked_through_completion = true, start_preserves_pending_settings = true,
                explicit_refresh_reloads_clean_forms = true, explicit_refresh_preserves_drafts = true
            }));
        } finally {
            checkCompletion = false; controlFixtureBackend = previousBackend;
            hardwareDirty = identityDirty = false; completingOperation = false;
            if (selectedEntry != entry) { Library.SelectedItem = entry; }
            machines.Remove(second); UpdateLibraryView();
            settings.ClipboardByVm = preferences; RestoreClipboardPreference();
            latest = previousStatus; LoadHardware(); loadedHardware = true;
            currentState = previousState; Devices.SelectedIndex = category; Tabs.SelectedIndex = page;
            Feedback.Visibility = Visibility.Collapsed; ClearSettingsFeedback(); UpdatePowerControls();
        }
    }
}
