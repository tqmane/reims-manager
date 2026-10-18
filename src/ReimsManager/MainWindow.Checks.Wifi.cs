using System.IO;
using System.Text.Json;
using System.Windows;

namespace ReimsManager;

public partial class MainWindow
{
    private async Task CheckWifiCard(string folder)
    {
        if(!selfTest) throw new InvalidOperationException("Wi-Fi fixture access only.");
        var oldLatest=latest;var oldWifi=wifiForwarder;var oldDraft=CaptureHardwareDraft();var oldCommitted=committedHardware;
        string oldState=currentState,oldStateText=State.Text;string? oldStatusError=wifiStatusError;bool oldLoaded=loadedHardware;string? physical=Physical.SelectedValue as string;
        try {
            currentState="stopped";State.Text="完全停止";StatusDetail.Text="";loadedHardware=true;
            ApplyNetworkChoices(NetworkInventory.Parse(OnboardingChecks.Network));
            const string id="aaaaaaaa-1111-4111-8111-111111111111";
            Uplink.SelectedValue="";Physical.SelectedValue="";
            ApplyNetworkChoices(NetworkInventory.Parse(OnboardingChecks.Network));OpenBridgeTab(this,new RoutedEventArgs());
            if(Physical.SelectedValue as string!=id) throw new Exception("Opening LAN preparation discarded the only connected Wi-Fi suggestion.");
            Physical.SelectedValue=id;Tabs.SelectedIndex=7;PacketFilterTerms.IsChecked=false;
            latest=WifiUiChecks.Snapshot(kind:"none",installed:false,configured:false);ReadWifiStatus();UpdatePowerControls();
            if(WifiCompatCard.Visibility!=Visibility.Visible || WiredBridgeControls.Visibility!=Visibility.Collapsed || PrepareWifiButton.IsEnabled)
                throw new Exception("Wi-Fi requires manual plumbing or installs without terms consent.");
            PacketFilterTerms.IsChecked=true;UpdatePowerControls();
            if(!PrepareWifiButton.IsEnabled || DisableWifiButton.IsEnabled) throw new Exception("Wi-Fi preparation/disable policy is inconsistent.");
            PrepareWifiButton.BringIntoView();await ArrangeCheck();
            RequireInside(PrepareWifiButton,BridgeScroll,"Wi-Fi setup action");RequireInside(PacketFilterTerms,BridgeScroll,"Driver terms consent");
            SaveImage(Path.Combine(folder,"wifi-compat-consent.png"));
            latest=JsonSerializer.SerializeToElement(new {});ReadWifiStatus();UpdatePowerControls();
            if(PrepareWifiButton.IsEnabled || !WifiForwarderSummary.Text.Contains("未確認")) throw new Exception("Missing status was reported ready.");
            latest=WifiUiChecks.Snapshot(running:true,ready:true,pid:1234);ReadWifiStatus();currentState="running";State.Text="実行中";UpdatePowerControls();
            if(PrepareWifiButton.IsEnabled || DisableWifiButton.IsEnabled || !WifiForwarderSummary.Text.Contains("転送プロセス")) throw new Exception("Running Wi-Fi permits a host configuration change.");
            WifiForwarderSummary.BringIntoView();await ArrangeCheck();SaveImage(Path.Combine(folder,"wifi-compat-ready.png"));
            latest=WifiUiChecks.Snapshot(running:true,ready:true,pid:1234,error:"Authored forwarding error");ReadWifiStatus();UpdatePowerControls();
            if(!WifiForwarderSummary.Text.Contains("エラー") || !WifiForwarderDetail.Text.Contains("Authored forwarding error")) throw new Exception("Worker error was hidden behind a ready flag.");
            await ArrangeCheck();SaveImage(Path.Combine(folder,"wifi-compat-error.png"));
            currentState="stopped";State.Text="完全停止";latest=WifiUiChecks.Snapshot();ReadWifiStatus();hardwareForwarder=WifiForwarderState.MacNat;
            loadingHardware=true;
            try { Nat.IsChecked=false;Bridge.IsChecked=true;SelectSavedUplink(id);Tap.SelectedItem="Dedicated test TAP"; }
            finally { loadingHardware=false; }
            hardwareDirty=identityDirty=false;RememberHardwareDraft();
            string memory=Memory.Text;Memory.Text=(int.Parse(memory)+1024).ToString();
            if(CanCommitWifiOperation()) throw new Exception("One-click Wi-Fi discarded unrelated staged memory.");
            Nat.IsChecked=true;
            if(!RestoreSettingsDrafts() || hardwareForwarder!=WifiForwarderState.MacNat || SelectedUplink!=id || Nat.IsChecked==true)
                throw new Exception("Cancelling another hardware edit dropped the Wi-Fi forwarding configuration.");
            UpdatePowerControls();
            if(!DisableWifiButton.IsEnabled || !ValidateHardware()) throw new Exception("Configured Wi-Fi cannot preserve its owned GUID without TAP plumbing.");
            File.WriteAllText(Path.Combine(folder,"wifi-page-result.json"),"{\"single_wifi_card\":true,\"terms_before_missing_driver\":true,\"missing_status_unconfirmed\":true,\"ready_is_worker_only\":true,\"errors_visible\":true,\"stopped_only\":true,\"draft_forwarder_retained\":true,\"unrelated_edits_preserved\":true,\"no_host_network_calls\":true}");
        } finally {
            committedHardware=oldDraft;hardwareDirty=true;identityDirty=false;RestoreSettingsDrafts();committedHardware=oldCommitted;
            latest=oldLatest;wifiForwarder=oldWifi;wifiStatusError=oldStatusError;hardwareForwarder=oldDraft.Forwarder;currentState=oldState;State.Text=oldStateText;loadedHardware=oldLoaded;
            Physical.SelectedValue=physical;PacketFilterTerms.IsChecked=false;hardwareDirty=false;Tabs.SelectedIndex=0;Feedback.Visibility=Visibility.Collapsed;UpdatePowerControls();
        }
    }
}
