using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ReimsManager;

public partial class MainWindow
{
    internal async Task CheckThemeRendering(string folder)
    {
        Application.Current.ThemeMode = ThemeMode.Dark;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (TryFindResource("CardBackgroundFillColorDefault") is null) throw new Exception("Fluent card theme resource missing");
        currentState = "stopped"; State.Text = "完全停止"; StatusDetail.Text = "";
        Tabs.SelectedIndex = 1; Devices.SelectedIndex = 0; UpdatePowerControls(); UpdateLayout();
        SaveImage(Path.Combine(folder, "dark-hardware.png"));
        Tabs.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        UpdateLayout(); SaveImage(Path.Combine(folder, "dark-console.png"));
        Application.Current.ThemeMode = ThemeMode.System;
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
        if (selectedEntry != first || Library.SelectedItem != first || !hardwareDirty)
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
