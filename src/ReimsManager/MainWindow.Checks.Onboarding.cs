using System.IO;
using System.Text.Json;
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
        await ArrangeCheck();InstallTapButton.BringIntoView();await ArrangeCheck();
        RequireInside(InstallTapButton,BridgeScroll,"Dedicated TAP preparation");
        SaveImage(Path.Combine(folder,"network-auto-tap-compact.png"));
        RemoveBridgeButton.BringIntoView();await ArrangeCheck();
        RequireInside(RemoveBridgeButton,BridgeScroll,"Owned bridge removal");
        SaveImage(Path.Combine(folder,"network-remove-bridge-compact.png"));
        await CheckWifiCard(folder);
        File.WriteAllText(Path.Combine(folder,"network-page-result.json"),"{\"physical_guid_draft\":true,\"missing_uplink_retained\":true,\"cancel_restores_nat\":true,\"remove_stopped_only\":true,\"remove_control_reachable\":true,\"no_host_network_calls\":true}");
    }

    private async Task CheckOnboarding(string folder)
    {
        await CheckOnboardingRecovery(folder);
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

#pragma warning disable WPF0001
    private async Task CheckOnboardingRecovery(string folder)
    {
        // Exercise an initial malformed response like the reported invalid UTF-8
        // escape, then retry through the same WPF entrypoint. No host work runs.
        string parent = Path.Combine(folder, "wizard-recovery"), iso = Path.Combine(folder, "wizard-recovery.iso");
        File.WriteAllText(iso, "Authored UI fixture; not a bootable installer.");
        string malformed = "{\"probe\":\"C:\\" + '\uFFFD' + "\"}";
        bool malformedResponse = true;
        int queries = 0;
        var previousTheme = Application.Current.ThemeMode;
        var wizard = new NewVmWindow((action, arguments, elevated, cancellation) => {
            if (action != "GetPrerequisites" || elevated || !cancellation.CanBeCanceled)
                throw new Exception("Recovery fixture attempted a host mutation.");
            queries++;
            return Task.FromResult(malformedResponse ? malformed : OnboardingChecks.Requirements(int.Parse(arguments[1]), int.Parse(arguments[3])));
        }, checkOnLoad: false) { Owner = this, Opacity = 0, ShowInTaskbar = false };
        try {
            Application.Current.ThemeMode = ThemeMode.Dark;
            wizard.Width = wizard.MinWidth; wizard.Height = wizard.MinHeight;
            wizard.Show(); wizard.ParentFolder.Text = parent; wizard.Installer.Text = iso;
            if (await wizard.ReadRequirements() || wizard.RequirementsList.Items.Count != 0 || wizard.Request is not null ||
                wizard.NextButton.IsEnabled || wizard.CreateButton.IsEnabled || wizard.StartAfterCreation.IsEnabled ||
                wizard.StartAfterCreation.IsChecked == true || wizard.Progress.Visibility != Visibility.Collapsed ||
                !wizard.RefreshRequirementsButton.IsEnabled || !wizard.CancelButton.IsEnabled ||
                !wizard.RequirementsSummary.Text.Contains("確認できませんでした", StringComparison.Ordinal) ||
                wizard.RequirementsSummary.Text.Contains("確認しています", StringComparison.Ordinal) ||
                !wizard.Notice.Text.Contains("必要条件", StringComparison.Ordinal) || !wizard.Notice.Text.Contains("0xEF", StringComparison.Ordinal) ||
                !wizard.Notice.Text.Contains("BytePositionInLine", StringComparison.Ordinal))
                throw new Exception("Initial malformed prerequisite JSON leaves stale progress or hides recovery/diagnostics.");
            await CaptureRecovery("onboarding-json-error-dark.png");

            malformedResponse = false;
            if (!await wizard.ReadRequirements() || wizard.RequirementsList.Items.Count == 0 || !wizard.NextButton.IsEnabled ||
                !wizard.StartAfterCreation.IsEnabled || wizard.StartAfterCreation.IsChecked != true || wizard.Notice.Text.Length != 0 ||
                !wizard.RequirementsSummary.Text.Contains("準備ができました", StringComparison.Ordinal) || queries != 2)
                throw new Exception("Retry does not restore the prerequisite list, readiness and requested automatic start.");
            await CaptureRecovery("onboarding-recovered-dark-compact.png");
            wizard.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!await wizard.TryCreateRequest() || wizard.Request?.AutoStart != true || wizard.InstallUsbButton.Visibility != Visibility.Visible)
                throw new Exception("Recovery fixture failed to establish a prior ready request and repair action.");
            wizard.StartAfterCreation.IsChecked = false;
            wizard.StartAfterCreation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            malformedResponse = true;
            if (await wizard.ReadRequirements() || wizard.Request is not null || wizard.RequirementsList.Items.Count != 0 ||
                wizard.EnableWhpxButton.Visibility != Visibility.Collapsed || wizard.InstallUsbButton.Visibility != Visibility.Collapsed ||
                wizard.StartAfterCreation.IsEnabled || wizard.StartAfterCreation.IsChecked == true || wizard.NextButton.IsEnabled || wizard.CreateButton.IsEnabled ||
                !wizard.RefreshRequirementsButton.IsEnabled || !wizard.CancelButton.IsEnabled ||
                !wizard.RequirementsSummary.Text.Contains("確認できませんでした", StringComparison.Ordinal))
                throw new Exception("Failed recheck preserves stale prerequisite controls or an already prepared creation request.");
            malformedResponse = false;
            if (!await wizard.ReadRequirements() || !wizard.StartAfterCreation.IsEnabled || wizard.StartAfterCreation.IsChecked == true ||
                !await wizard.TryCreateRequest() || wizard.Request is not { AutoStart: false } request ||
                wizard.ParentFolder.Text != parent || wizard.Installer.Text != iso || File.Exists(request.Vm.File) || Directory.Exists(parent))
                throw new Exception("Recovery lost the user's plan or auto-start choice, or wrote VM data during validation.");
            File.WriteAllText(Path.Combine(folder, "onboarding-recovery-result.json"), JsonSerializer.Serialize(new {
                initial_invalid_json_failure = true, japanese_error_context = true, raw_byte_diagnostic_retained = true,
                stale_progress_cleared = true, retry_and_cancel_available = true, successful_retry_restores_readiness = true,
                stale_requirement_actions_cleared = true, failed_recheck_invalidates_request = true,
                user_auto_start_choice_preserved = true, vm_plan_preserved_without_writes = true
            }));
        } finally { wizard.Close(); Application.Current.ThemeMode = previousTheme; }

        async Task CaptureRecovery(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); wizard.UpdateLayout();
            var content = (FrameworkElement)wizard.Content;
            RequireInside(wizard.RequirementsSummary, content, "Wizard prerequisite failure/recovery summary");
            RequireInside(wizard.CancelButton, content, "Wizard recovery cancel");
            RequireInside(wizard.RefreshRequirementsButton, content, "Wizard recovery retry");
            RequireInside(wizard.NextButton, content, "Wizard recovery next");
            SaveImage(Path.Combine(folder, name), content);
        }
    }
#pragma warning restore WPF0001

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
        Physical.SelectedValue = "bbbbbbbb-2222-4222-8222-222222222222"; Tabs.SelectedIndex = 7; await ArrangeCheck();
        RemoveBridgeButton.BringIntoView();await ArrangeCheck();
        RequireInside(RemoveBridgeButton,BridgeScroll,"Owned bridge removal");
        SaveImage(Path.Combine(folder, "network-bridge-preparation.png"));
        foreach (string state in new[] { "running", "shutdown", "paused", "unknown", "unprepared", "stopped" }) {
            currentState=state;UpdatePowerControls();
            if (RemoveBridgeButton.IsEnabled != (state == "stopped") || InstallTapButton.IsEnabled != (state == "stopped")) throw new Exception("Host network changes are enabled outside a fully stopped VM.");
        }
        Tabs.SelectedIndex = 0; hardwareDirty = identityDirty = false; UpdatePowerControls();
    }
}
