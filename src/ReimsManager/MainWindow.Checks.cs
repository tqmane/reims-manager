using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReimsManager;

public partial class MainWindow
{
    // ThemeMode is already used by App.xaml. Opt in only for this isolated render fixture.
#pragma warning disable WPF0001
    internal async Task CheckThemeRendering(string folder)
    {
        Application.Current.ThemeMode = ThemeMode.Dark;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        currentState = "stopped"; State.Text = "完全停止"; StatusDetail.Text = "";
        Tabs.SelectedIndex = 1; Devices.SelectedIndex = 0; UpdatePowerControls(); UpdateLayout();
        if (WorkspaceSurface.Background is not SolidColorBrush card ||
            TryFindResource("ApplicationBackgroundBrush") is not SolidColorBrush canvas ||
            Foreground is not SolidColorBrush foreground)
            throw new Exception("Expected native Fluent color resources");
        var surface = Blend(card.Color, canvas.Color);
        double contrast = Contrast(foreground.Color, surface);
        if (contrast < 4.5) throw new Exception($"Dark workspace contrast is too low: {contrast:0.00}");
        SaveImage(Path.Combine(folder, "dark-hardware.png"));
        Tabs.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        UpdateLayout(); SaveImage(Path.Combine(folder, "dark-console.png"));
        double previousWidth = Width, previousHeight = Height;
        Width = MinWidth; Height = MinHeight;
        EngineSettingsExpander.IsExpanded = true;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        UpdateLayout(); SidebarScroll.ScrollToEnd();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        var bottomButton = SelectExistingEngineButton.TransformToAncestor(SidebarScroll)
            .TransformBounds(new Rect(SelectExistingEngineButton.RenderSize));
        if (bottomButton.Top < -1 || bottomButton.Bottom > SidebarScroll.ActualHeight + 1)
            throw new Exception("Compact sidebar hides the engine controls");
        SaveImage(Path.Combine(folder, "compact-engine-settings.png"));
        EngineSettingsExpander.IsExpanded = false; SidebarScroll.ScrollToTop();
        Width = previousWidth; Height = previousHeight;
        Application.Current.ThemeMode = ThemeMode.System;
    }

#pragma warning restore WPF0001

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
