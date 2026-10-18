using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ReimsManager;

public partial class MainWindow
{
    internal async Task CheckNetworkPage(string folder)
    {
        if (!selfTest) throw new InvalidOperationException("Network fixture access only.");
        Directory.CreateDirectory(folder);
        ConfigureEngine(folder,Environment.ProcessPath!);
        AddVm(new("Synthetic network VM",Path.Combine(folder,"fixture.reimsvm"),Path.Combine(folder,"fixture.data")));
        State.Text="完全停止";StatusDetail.Text="";Feedback.Visibility=Visibility.Collapsed;
        await CheckNetworkChoices(folder);
        Width=MinWidth;Height=MinHeight;Tabs.SelectedIndex=7;currentState="stopped";UpdatePowerControls();
        await ArrangeCheck();RemoveBridgeButton.BringIntoView();await ArrangeCheck();
        RequireInside(RemoveBridgeButton,BridgeScroll,"Owned bridge removal");
        SaveImage(Path.Combine(folder,"network-remove-bridge-compact.png"));
        File.WriteAllText(Path.Combine(folder,"network-page-result.json"),"{\"physical_guid_draft\":true,\"missing_uplink_retained\":true,\"cancel_restores_nat\":true,\"remove_stopped_only\":true,\"remove_control_reachable\":true,\"no_host_network_calls\":true}");
    }

    private async Task CheckOnboarding(string folder)
    {
        string parent = Path.Combine(folder, "wizard 日本語 VM"), iso = Path.Combine(folder, "synthetic.iso");
        File.WriteAllText(iso, "Synthetic UI input, not a bootable installer.");
        bool fail = false; int available = 2048, queries = 0;
        var wizard = new NewVmWindow((action, arguments, elevated, cancellation) => {
            if (action != "GetPrerequisites" || elevated || !cancellation.CanBeCanceled) throw new Exception("Wizard invoked a host mutation.");
            queries++;
            if (fail) throw new IOException("Synthetic prerequisite unavailable");
            return Task.FromResult(OnboardingChecks.Requirements(int.Parse(arguments[1]), int.Parse(arguments[3]), available));
        }, checkOnLoad: false) { Owner = this, Opacity = 0, ShowInTaskbar = false };
        try {
            wizard.Show();
            wizard.ParentFolder.Text = parent; wizard.Installer.Text = iso;
            int cpus = Math.Min(4, Environment.ProcessorCount);
            wizard.ApplyRequirements(SetupRequirements.Parse(OnboardingChecks.Requirements(cpus, available: available)));
            if (!wizard.NextButton.IsEnabled || wizard.StartAfterCreation.IsEnabled || wizard.StartAfterCreation.IsChecked == true)
                throw new Exception("Low memory blocked creation or enabled automatic start.");
            await Capture("onboarding-prerequisites-low-memory.png");
            wizard.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (wizard.VmPage.Visibility != Visibility.Visible || wizard.Capacity.Text != "300") throw new Exception("Wizard did not advance to the 300 GiB VM plan.");
            if (!await wizard.TryCreateRequest() || wizard.Request is not { DiskGiB: 300, AutoStart: false } request ||
                request.Cpus != cpus || request.MemoryMiB != 4096 || File.Exists(request.Vm.File) || Directory.Exists(parent))
                throw new Exception("Wizard wrote a descriptor early or lost its plan.");
            wizard.VmPage.ScrollToEnd(); await Capture("onboarding-vm-plan.png");
            available = 8192;
            if (!await wizard.TryCreateRequest() || wizard.Request?.AutoStart != true || queries != 2) throw new Exception("Create did not refresh current capacity.");
            fail = true;
            if (await wizard.TryCreateRequest() || wizard.Request is not null || !wizard.Notice.Text.Contains("Synthetic prerequisite unavailable"))
                throw new Exception("A prerequisite failure was hidden or left a ready request.");
            wizard.ApplyRequirements(SetupRequirements.Parse(OnboardingChecks.Requirements(cpus, whpx: false, restart: true)));
            if (wizard.NextButton.IsEnabled || wizard.EnableWhpxButton.Visibility != Visibility.Visible) throw new Exception("A missing WHPX requirement was skipped.");
            await Capture("onboarding-restart-required.png");
            string occupied = VmLibrary.PlanCreate(parent, wizard.VmName.Text).Directory;
            Directory.CreateDirectory(occupied); string sentinel = Path.Combine(occupied, "keep.txt"); File.WriteAllText(sentinel, "keep");
            if (await wizard.TryCreateRequest() || File.ReadAllText(sentinel) != "keep") throw new Exception("Wizard overwrote an existing destination.");
        } finally { wizard.Close(); }

        async Task Capture(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); wizard.UpdateLayout();
            var content = (FrameworkElement)wizard.Content;
            RequireInside(wizard.CancelButton, content, "Wizard cancel");
            RequireInside(wizard.VmPage.Visibility == Visibility.Visible ? wizard.CreateButton : wizard.NextButton, content, "Wizard primary action");
            SaveImage(Path.Combine(folder, name), content);
        }
    }

    private async Task CheckNetworkChoices(string folder)
    {
        currentState = "stopped"; Tabs.SelectedIndex = 1; Devices.SelectedIndex = 4;
        loadedHardware = true; hardwareDirty = identityDirty = false;
        ApplyNetworkChoices(NetworkInventory.Parse(OnboardingChecks.Network));
        string id = "aaaaaaaa-1111-4111-8111-111111111111";
        Uplink.SelectedValue = ""; Nat.IsChecked = true; RememberHardwareDraft();
        UpdatePowerControls();
        if (Uplink.IsEnabled) throw new Exception("NAT required a physical uplink.");
        Bridge.IsChecked = true; Uplink.SelectedValue = id; Tap.SelectedItem = "Dedicated test TAP";
        if (!hardwareDirty || SelectedUplink != id || !Uplink.IsEnabled) throw new Exception("Physical uplink was not staged by ID.");
        ApplyNetworkChoices(new([], ["Dedicated test TAP"]));
        if (SelectedUplink != id) throw new Exception("A temporarily missing physical uplink was silently dropped.");
        ApplyNetworkChoices(NetworkInventory.Parse(OnboardingChecks.Network));
        await ArrangeCheck(); SaveImage(Path.Combine(folder, "network-friendly-uplinks.png"));
        if (!RestoreSettingsDrafts() || SelectedUplink.Length != 0 || Nat.IsChecked != true) throw new Exception("Cancel lost the saved network selection.");
        Physical.SelectedValue = id; Tabs.SelectedIndex = 7; await ArrangeCheck();
        RemoveBridgeButton.BringIntoView();await ArrangeCheck();
        RequireInside(RemoveBridgeButton,BridgeScroll,"Owned bridge removal");
        SaveImage(Path.Combine(folder, "network-bridge-preparation.png"));
        foreach (string state in new[] { "running", "shutdown", "paused", "unknown", "unprepared", "stopped" }) {
            currentState=state;UpdatePowerControls();
            if (RemoveBridgeButton.IsEnabled != (state == "stopped")) throw new Exception("Bridge removal is enabled outside a fully stopped VM.");
        }
        Tabs.SelectedIndex = 0; hardwareDirty = identityDirty = false; UpdatePowerControls();
    }
}
