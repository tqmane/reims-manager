using System.IO;
using System.Text.Json;
using System.Windows;

namespace ReimsManager;

public partial class MainWindow
{
    // Real WPF controls with authored inventory/QMP results; no physical USB,
    // guest connection, Windows driver installation or elevation is performed.
    private async Task CheckUsbWorkflow(string folder)
    {
        if (!selfTest || selectedEntry is null) throw new InvalidOperationException("USB fixture requires a selected VM.");
        var entry = selectedEntry;
        var previousBackend = controlFixtureBackend;
        var previousStatus = latest;
        string previousState = currentState, previousStateText = State.Text;
        string? previousSelectedVm = settings.SelectedVm;
        int page = Tabs.SelectedIndex, category = Devices.SelectedIndex;
        double width = Width, height = Height;
        bool wasLoaded = loadedHardware, wasHardwareDirty = hardwareDirty, wasIdentityDirty = identityDirty;
        var previousHardware = CaptureHardwareDraft(); var previousIdentity = CaptureIdentityDraft();
        var previousCommittedHardware = committedHardware; var previousCommittedIdentity = committedIdentity;
        var previousItems = UsbDevices.ItemsSource; var previousSelection = UsbDevices.SelectedItem;
        string previousHint = UsbHint.Text;
        bool previousInventoryCurrent = usbInventoryContext == contextVersion;
        var second = new VmEntry("USB isolation fixture", Path.Combine(folder, "usb-second.reimsvm"), Path.Combine(folder, "usb-second.data"));
        var pending = new Queue<Task<string>>();
        var delayed = new List<TaskCompletionSource<string>>();
        int attachCalls = 0, detachCalls = 0, installCalls = 0;
        bool failAfterDetach = false, usbdkInstalled = false;
        string fixtureState = "running", savedBackend = "winusb", activeBackend = "winusb";
        UsbEntry[] rows = [
            new("hostusb-1-2-04b00422", "04b0:0422 Camera — USB 1 / 2", true, false, false, false),
            new("hostusb-1-4-07815599", "0781:5599 Card reader ［登録済み・接続待ち］", false, true, false, false),
            new("hostusb-1-5-1234abcd", "1234:abcd USB audio ［自動接続・接続待ち］", true, true, true, false),
            new("hostusb-1-6-05551234", "0555:1234 Camera ［接続中］", true, true, true, true),
            new("hostusb-1-7-4321abcd", "4321:abcd USB adapter ［接続中・未登録］", true, false, true, true)
        ];
        string Inventory(IEnumerable<UsbEntry> devices) => JsonSerializer.Serialize(devices.Select(x => new {
            id = x.Id, label = x.Label, present = x.Present, selected = x.Selected, active = x.Active, attached = x.Attached
        }));
        string Status() => JsonSerializer.Serialize(new {
            state = fixtureState, prepared = true, snapshots = Array.Empty<string>(), taps = Array.Empty<string>(),
            memory_mib = 8192, cpus = 2, disk_gib = 128, disk = "fixture.qcow2", iso = "",
            with_installer = false, connect_installer = false, network = "nat", network_adapter = "", guest_host = "",
            audio = "none", microphone = false, usb_backend = savedBackend,
            usb_backend_active = fixtureState == "stopped" ? null : activeBackend, usb_usbdk_installed = usbdkInstalled, resolution = "1920x1080"
        });
        void Select(string id) => UsbDevices.SelectedItem = UsbDevices.Items.Cast<UsbEntry>().Single(x => x.Id == id);
        TaskCompletionSource<string> Defer()
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            delayed.Add(completion); pending.Enqueue(completion.Task); return completion;
        }
        controlFixtureBackend = action => {
            if (action == "ListUsb") return pending.Count > 0 ? pending.Dequeue() : Task.FromResult(Inventory(rows));
            if (action == "Status") return Task.FromResult(Status());
            if (action == "GetNetworkUplinks") return Task.FromResult("{\"schema\":1,\"uplinks\":[],\"tap_adapters\":[]}");
            if (action == "InstallUsbDk") { installCalls++; usbdkInstalled = true; return Task.FromResult("Fixture: driver installation completed."); }
            if (action is "AttachUsb" or "DetachUsb") {
                var selected = (UsbEntry)UsbDevices.SelectedItem;
                if (action == "AttachUsb") {
                    attachCalls++;
                    rows = rows.Select(x => x.Id == selected.Id ? x with { Selected = true, Active = true, Attached = true, Label = "USB audio ［接続中］" } : x).ToArray();
                } else {
                    detachCalls++;
                    rows = rows.Where(x => x.Id != selected.Id || x.Present).Select(x => x.Id == selected.Id ? x with { Selected = false, Active = false, Attached = false } : x).ToArray();
                    if (failAfterDetach) throw new IOException("Fixture partial detach failure.");
                }
                return Task.FromResult("Fixture: USB operation committed.");
            }
            throw new Exception("Unexpected USB fixture action: " + action);
        };
        machines.Add(second); UpdateLibraryView();
        try {
            hardwareDirty = identityDirty = false;
            latest = JsonSerializer.Deserialize<JsonElement>(Status()); LoadHardware(); loadedHardware = true;
            currentState = fixtureState; Tabs.SelectedIndex = 1; Devices.SelectedIndex = 5; ResetUsbInventory(); UpdatePowerControls();
            if (!await RefreshUsbCore() || UsbDevices.Items.Count != 5 || UsbAttachButton.IsEnabled || UsbDetachButton.IsEnabled)
                throw new Exception("USB inventory requires a deliberate selection before a mutation.");

            Select("hostusb-1-2-04b00422");
            if (!UsbAttachButton.IsEnabled || UsbDetachButton.IsEnabled || await ChangeUsbConnection(false) || detachCalls != 0)
                throw new Exception("An unregistered USB can incorrectly be detached.");
            Select("hostusb-1-4-07815599");
            if (UsbAttachButton.IsEnabled || !UsbDetachButton.IsEnabled || await ChangeUsbConnection(true) || attachCalls != 0)
                throw new Exception("A missing saved USB is offered as an immediately attachable device.");
            Select("hostusb-1-6-05551234");
            if (UsbAttachButton.IsEnabled || !UsbDetachButton.IsEnabled || await ChangeUsbConnection(true) || attachCalls != 0)
                throw new Exception("An attached USB accepts a duplicate attachment.");
            Select("hostusb-1-5-1234abcd");
            if (!UsbAttachButton.IsEnabled || !UsbDetachButton.IsEnabled || UsbAttachButton.Content?.ToString() != "再接続")
                throw new Exception("A saved USB with a failed watcher has no reconnect action.");
            Width = 1160; Height = 880; UpdatePowerControls(); await ArrangeCheck();
            UsbAttachButton.BringIntoView(); await ArrangeCheck();
            RequireInside(UsbAttachButton, HardwareScroll, "USB reconnect action");
            SaveImage(Path.Combine(folder, "usb-reconnect-running.png"));
            if (!await ChangeUsbConnection(true) || attachCalls != 1 || UsbDevices.SelectedItem is not UsbEntry { Id: "hostusb-1-5-1234abcd", Attached: true } || UsbAttachButton.IsEnabled)
                throw new Exception("USB reconnect lost its selection or failed to publish the current attachment state.");
            failAfterDetach = true;
            if (await ChangeUsbConnection(false) || UsbDevices.SelectedItem is not UsbEntry { Attached: false, Selected: false } ||
                !UsbAttachButton.IsEnabled || UsbDetachButton.IsEnabled || !ErrorNotice.Text.Contains("Fixture partial detach failure.", StringComparison.Ordinal))
                throw new Exception("A failed USB mutation retained stale attachment state or hid its failure.");
            failAfterDetach = false;

            Select("hostusb-1-7-4321abcd");
            if (!UsbDetachButton.IsEnabled || !await ChangeUsbConnection(false) || detachCalls != 2)
                throw new Exception("An active orphan USB watcher cannot be released.");
            fixtureState = currentState = "stopped"; UpdatePowerControls();
            Select("hostusb-1-4-07815599");
            if (UsbAttachButton.IsEnabled || !UsbDetachButton.IsEnabled || !await ChangeUsbConnection(false) || detachCalls != 3 || UsbDevices.SelectedItem is not null)
                throw new Exception("An unplugged USB registration cannot be removed while stopped.");

            int selectedBackend = UsbBackend.SelectedIndex;
            if (!await InstallUsbDkCore() || installCalls != 1 || UsbBackend.SelectedIndex != selectedBackend || hardwareDirty || !UsbHint.Text.Contains("選んで保存", StringComparison.Ordinal))
                throw new Exception("Installing a driver silently changed the saved USB backend or omitted the next step.");
            savedBackend = "usbdk"; usbdkInstalled = false; fixtureState = currentState = "running";
            latest = JsonSerializer.Deserialize<JsonElement>(Status()); LoadHardware(); UpdatePowerControls();
            if (!UsbBackendHint.Text.Contains("保存済み: UsbDk", StringComparison.Ordinal) || !UsbBackendHint.Text.Contains("実行中: WinUSB", StringComparison.Ordinal) ||
                !UsbBackendHint.Text.Contains("UsbDk の導入を確認できません", StringComparison.Ordinal) || InstallUsbDkButton.IsEnabled)
                throw new Exception("USB guidance confuses the active driver mode with the saved next-boot mode.");
            fixtureState = currentState = "stopped"; UsbBackend.SelectedIndex = 0; UpdatePowerControls();
            if (!hardwareDirty || !UsbBackendHint.Text.Contains("未保存の変更: WinUSB", StringComparison.Ordinal))
                throw new Exception("A staged USB backend is presented as already saved.");
            Width = MinWidth; Height = MinHeight; await ArrangeCheck(); UsbBackend.BringIntoView(); await ArrangeCheck();
            RequireInside(UsbBackend, HardwareScroll, "USB transfer mode at minimum window size");
            SaveImage(Path.Combine(folder, "usb-driver-mode-compact.png"));
            RestoreSettingsDrafts();

            fixtureState = currentState = "running"; UpdatePowerControls();
            pending.Enqueue(Task.FromException<string>(new IOException("Fixture inventory failed.")));
            if (await RefreshUsbCore() || UsbDevices.Items.Count != 0 || UsbAttachButton.IsEnabled || UsbDetachButton.IsEnabled || !UsbHint.Text.Contains("Fixture inventory failed.", StringComparison.Ordinal))
                throw new Exception("Failed USB inventory leaves stale actionable devices on screen.");
            var older = Defer(); Task<bool> olderRead = RefreshUsbCore();
            if (UsbRefreshButton.IsEnabled || UsbDevices.IsEnabled) throw new Exception("USB mutation controls remain active during inventory reload.");
            if (!await RefreshUsbCore()) throw new Exception("A newer USB inventory could not complete.");
            older.SetResult(Inventory([new UsbEntry("outdated", "Outdated inventory", true, false, false, false)]));
            if (await olderRead || UsbDevices.Items.Cast<UsbEntry>().Any(x => x.Id == "outdated"))
                throw new Exception("An older same-VM USB response overwrote the newer inventory.");

            var stale = Defer(); Task<bool> staleRead = RefreshUsbCore();
            Library.SelectedItem = second;
            if (selectedEntry != second || UsbDevices.Items.Count != 0) throw new Exception("A VM switch retains another VM's USB inventory.");
            stale.SetResult(Inventory(rows));
            if (await staleRead || UsbDevices.Items.Count != 0 || UsbAttachButton.IsEnabled || UsbDetachButton.IsEnabled)
                throw new Exception("An in-flight USB response leaked across the VM selection boundary.");
            File.WriteAllText(Path.Combine(folder, "usb-workflow-result.json"), JsonSerializer.Serialize(new {
                selection_capabilities = true, actual_attachment_state = true, failed_watcher_reconnect = true,
                disconnected_registration_removed = true, orphan_watcher_release = true, selection_preserved = true,
                driver_install_keeps_saved_mode = true, active_mode_separate_from_saved_mode = true,
                failed_operation_reloads_actual_state = true, failed_inventory_disables_actions = true,
                latest_inventory_wins = true, cross_vm_response_rejected = true
            }));
        } finally {
            foreach (var completion in delayed) completion.TrySetCanceled();
            controlFixtureBackend = previousBackend;
            hardwareDirty = identityDirty = false;
            if (selectedEntry != entry) Library.SelectedItem = entry;
            machines.Remove(second); UpdateLibraryView(); settings.SelectedVm = previousSelectedVm;
            latest = previousStatus; LoadHardware();
            committedHardware = previousHardware; committedIdentity = previousIdentity;
            hardwareDirty = identityDirty = true; RestoreSettingsDrafts();
            committedHardware = previousCommittedHardware; committedIdentity = previousCommittedIdentity;
            loadedHardware = wasLoaded; hardwareDirty = wasHardwareDirty; identityDirty = wasIdentityDirty;
            ResetUsbInventory(); UsbDevices.ItemsSource = previousItems; UsbDevices.SelectedItem = previousSelection;
            usbInventoryContext = previousInventoryCurrent ? contextVersion : -1; UsbHint.Text = previousHint;
            currentState = previousState; State.Text = previousStateText; Width = width; Height = height;
            Devices.SelectedIndex = category; Tabs.SelectedIndex = page;
            Feedback.Visibility = Visibility.Collapsed; ClearSettingsFeedback(); UpdatePowerControls();
        }
    }
}
