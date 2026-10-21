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
            if(PrepareWifiButton.IsEnabled || DisableWifiButton.IsEnabled || !WifiForwarderSummary.Text.Contains("転送プロセス") || !WifiForwarderSummary.Text.Contains("IPv4 / IPv6"))
                throw new Exception("Running Wi-Fi permits a host configuration change or loses protocol capability.");
            WifiForwarderSummary.BringIntoView();await ArrangeCheck();SaveImage(Path.Combine(folder,"wifi-compat-ready.png"));
            latest=WifiUiChecks.Snapshot(running:true,ready:true,pid:1234,error:"Authored forwarding error");ReadWifiStatus();UpdatePowerControls();
            if(!WifiForwarderSummary.Text.Contains("エラー") || !WifiForwarderDetail.Text.Contains("Authored forwarding error")) throw new Exception("Worker error was hidden behind a ready flag.");
            await ArrangeCheck();SaveImage(Path.Combine(folder,"wifi-compat-error.png"));
            currentState="stopped";State.Text="完全停止";
            latest=WifiUiChecks.Snapshot(kind:WifiForwarderState.LegacyMacNat,configured:false,error:"Reapply PrepareWifi for IPv6");ReadWifiStatus();hardwareForwarder=WifiForwarderState.LegacyMacNat;
            loadingHardware=true;
            try { Nat.IsChecked=false;Bridge.IsChecked=true;SelectSavedUplink(id);Tap.SelectedItem="Dedicated test TAP"; }
            finally { loadingHardware=false; }
            hardwareDirty=identityDirty=false;RememberHardwareDraft();Memory.Text=(int.Parse(Memory.Text)+1024).ToString();Nat.IsChecked=true;
            if(!RestoreSettingsDrafts() || hardwareForwarder!=WifiForwarderState.LegacyMacNat || Nat.IsChecked==true || SelectedUplink!=id)
                throw new Exception("Cancelling settings upgraded or dropped the legacy IPv4 profile.");
            UpdatePowerControls();
            if(!PrepareWifiButton.IsEnabled || !DisableWifiButton.IsEnabled || !WifiForwarderDetail.Text.Contains("再実行") ||
                !WifiForwarderDetail.Text.Contains("Reapply PrepareWifi for IPv6") || ValidateHardware())
                throw new Exception("Legacy Wi-Fi was silently qualified or cannot prepare/clean up its owned TAP.");
            Feedback.Visibility=Visibility.Collapsed;WifiForwarderSummary.BringIntoView();await ArrangeCheck();SaveImage(Path.Combine(folder,"wifi-compat-legacy.png"));
            currentState="stopped";State.Text="完全停止";latest=WifiUiChecks.Snapshot();ReadWifiStatus();hardwareForwarder=WifiForwarderState.MacNat;
            loadingHardware=true;
            try { Nat.IsChecked=false;Bridge.IsChecked=true;SelectSavedUplink(id);Tap.SelectedItem="Dedicated test TAP"; }
            finally { loadingHardware=false; }
            hardwareDirty=identityDirty=false;RememberHardwareDraft();
            string memory=Memory.Text;Memory.Text=(int.Parse(memory)+1024).ToString();
            if(!CanCommitWifiOperation() || Memory.Text==memory || !hardwareDirty) throw new Exception("Unrelated staged memory blocked Wi-Fi preparation or was discarded.");
            Nat.IsChecked=true;
            if(!RestoreSettingsDrafts() || hardwareForwarder!=WifiForwarderState.MacNat || SelectedUplink!=id || Nat.IsChecked==true)
                throw new Exception("Cancelling another hardware edit dropped the Wi-Fi forwarding configuration.");
            UpdatePowerControls();
            if(!DisableWifiButton.IsEnabled || !ValidateHardware()) throw new Exception("Configured Wi-Fi cannot preserve its owned GUID without TAP plumbing.");
            await CheckWifiDraftRetention();
            File.WriteAllText(Path.Combine(folder,"wifi-page-result.json"),"{\"single_wifi_card\":true,\"terms_before_missing_driver\":true,\"missing_status_unconfirmed\":true,\"ready_is_worker_only\":true,\"errors_visible\":true,\"stopped_only\":true,\"draft_forwarder_retained\":true,\"unrelated_edits_preserved\":true,\"no_host_network_calls\":true,\"ipv6_capability_not_lan\":true,\"legacy_ipv4_cancel_retains_profile\":true,\"legacy_requires_reprepare_allows_cleanup\":true,\"prepare_disable_preserve_hardware_identity_drafts\":true,\"cancel_retains_committed_network_only\":true,\"failure_restart_preserve_network_draft\":true,\"failed_status_poll_recovers_network_baseline\":true,\"context_reset_clears_pending_network_refresh\":true,\"save_reveals_network_preparation\":true}");
        } finally {
            committedHardware=oldDraft;hardwareDirty=true;identityDirty=false;RestoreSettingsDrafts();committedHardware=oldCommitted;
            latest=oldLatest;wifiForwarder=oldWifi;wifiStatusError=oldStatusError;hardwareForwarder=oldDraft.Forwarder;currentState=oldState;State.Text=oldStateText;loadedHardware=oldLoaded;
            Physical.SelectedValue=physical;PacketFilterTerms.IsChecked=false;hardwareDirty=false;Tabs.SelectedIndex=0;Feedback.Visibility=Visibility.Collapsed;UpdatePowerControls();
        }
    }

    private async Task CheckWifiDraftRetention()
    {
        var previousBackend=controlFixtureBackend;var previousStatus=latest;var previousWifi=wifiForwarder;
        var previousDraft=CaptureHardwareDraft();var previousIdentity=CaptureIdentityDraft();
        var previousCommitted=committedHardware;var previousCommittedIdentity=committedIdentity;
        bool previousLoaded=loadedHardware,previousDirty=hardwareDirty,previousIdentityDirty=identityDirty,previousPending=networkDraftRefreshPending;
        string previousState=currentState,previousStateText=State.Text;string? previousError=wifiStatusError;
        string previousUser=GuestUser.Text,previousFingerprint=Fingerprint.Text,previousIso=Iso.Text,previousDisk=DiskSize.Text,previousDiskInfo=DiskInfo.Text;
        string previousSummary=VmSummary.Text,previousDetail=StatusDetail.Text,previousNetworkNotice=NetworkNotice.Text,previousUsbHint=UsbHint.Text;
        var previousTaps=Tap.ItemsSource;var previousUplinks=Uplink.ItemsSource;var previousPhysical=Physical.ItemsSource;var previousBridgeTaps=BridgeTap.ItemsSource;var previousSnapshots=Snapshots.ItemsSource;
        var previousPhysicalValue=Physical.SelectedValue;var previousBridgeTap=BridgeTap.SelectedItem;var previousSnapshot=Snapshots.SelectedItem;
        var previousUsbItems=UsbDevices.ItemsSource;var previousUsbSelection=UsbDevices.SelectedItem;bool previousUsbCurrent=usbInventoryContext==contextVersion;
        bool? previousInstaller=ConnectIso.IsChecked,previousTerms=PacketFilterTerms.IsChecked;bool previousDetached=consoleDetached;int previousPage=Tabs.SelectedIndex;
        bool configured=false,failWrite=false,restart=false,failStatus=false;int memory=8192,writes=0;
        string host="saved-guest.local";const string id="aaaaaaaa-1111-4111-8111-111111111111",tap="Owned fixture TAP";
        string Status() => JsonSerializer.Serialize(new {
            state="stopped",prepared=true,snapshots=Array.Empty<string>(),taps=new[]{tap},memory_mib=memory,cpus=1,disk_gib=128,disk="fixture.qcow2",iso="",
            with_installer=false,connect_installer=false,network=configured?"bridge":"nat",network_adapter=configured?tap:"",network_uplink_id=configured?id:"",
            guest_host=host,audio="none",microphone=false,usb_backend="winusb",resolution="1920x1080",
            network_forwarder=new {kind=configured?WifiForwarderState.MacNat:"none",installed=true,configured,running=false,ready=false,pid=(int?)null,error=(string?)null,ipv4_only=!configured,ipv6_enabled=configured}
        });
        controlFixtureBackend = action => {
            if(action=="GetNetworkUplinks") return Task.FromResult(OnboardingChecks.Network);
            if(action=="Status") { if(failStatus) throw new IOException("Fixture status unavailable.");return Task.FromResult(Status()); }
            if(action is not ("PrepareWifi" or "DisableWifi")) throw new Exception("Unexpected Wi-Fi fixture write: "+action);
            writes++;if(failWrite) throw new IOException("Fixture preparation failed.");
            if(action=="PrepareWifi") {
                if(!restart) { configured=true;host=GuestHost.Text.Trim(); }
                string result=Path.Combine(VmDirectory,"state","wifi-prepare-result.json");Directory.CreateDirectory(Path.GetDirectoryName(result)!);
                File.WriteAllText(result,JsonSerializer.Serialize(new{schema=1,prepared=!restart,configured=!restart,restart_required=restart}));
                File.SetLastWriteTimeUtc(result,DateTime.UtcNow);
            } else configured=false;
            return Task.FromResult("Authored network-only commit.");
        };
        void LoadBaseline() {
            networkDraftRefreshPending=false;latest=JsonSerializer.Deserialize<JsonElement>(Status());ReadWifiStatus();
            currentState="stopped";State.Text="完全停止";LoadHardware();loadedHardware=true;
            RememberIdentityDraft();identityDirty=false;Physical.SelectedValue=id;UpdatePowerControls();
        }
        void Stage() {
            Memory.Text="12288";Cpus.Text=Math.Min(2,Environment.ProcessorCount).ToString();IdentitySerial.Text=committedIdentity?.Serial=="DRAFT-SERIAL"?"OTHER-DRAFT":"DRAFT-SERIAL";
            Nat.IsChecked=false;Bridge.IsChecked=true;SelectSavedUplink(id);GuestHost.Text="draft-guest.local";
        }
        void RequirePending(HardwareDraft hardware,IdentityDraft identity) {
            if(CaptureHardwareDraft()!=hardware || CaptureIdentityDraft()!=identity || !hardwareDirty || !identityDirty)
                throw new Exception("Wi-Fi operation discarded or marked unrelated drafts saved.");
        }
        try {
            LoadBaseline();var originalIdentity=CaptureIdentityDraft();Stage();var draft=CaptureHardwareDraft();var identity=CaptureIdentityDraft();
            if(await ApplySettingsDrafts() || Tabs.SelectedIndex!=7 || writes!=0) throw new Exception("Save did not reveal Wi-Fi preparation without writing hardware/identity.");
            RequirePending(draft,identity);
            if(!await PrepareWifiCore() || Memory.Text!=draft.Memory || Cpus.Text!=draft.Cpus || CaptureIdentityDraft()!=identity || !hardwareDirty || !identityDirty ||
                committedHardware?.Memory!="8192" || committedHardware?.Nat!=false || committedHardware?.Uplink!=id || !ValidateHardware())
                throw new Exception("Preparing Wi-Fi lost staged hardware/identity or failed to commit only network fields.");
            if(!RestoreSettingsDrafts() || Memory.Text!="8192" || Cpus.Text!="1" || CaptureIdentityDraft()!=originalIdentity || hardwareDirty || identityDirty ||
                Nat.IsChecked==true || hardwareForwarder!=WifiForwarderState.MacNat || SelectedUplink!=id || Selected(Tap)!=tap)
                throw new Exception("Cancel undid committed Wi-Fi or retained unsaved hardware/identity.");
            Stage();Nat.IsChecked=true;var disableDraft=CaptureHardwareDraft();var disableIdentity=CaptureIdentityDraft();
            if(!await DisableWifiCore() || Memory.Text!=disableDraft.Memory || Cpus.Text!=disableDraft.Cpus || CaptureIdentityDraft()!=disableIdentity || !hardwareDirty || !identityDirty ||
                Nat.IsChecked!=true || hardwareForwarder!="" || committedHardware?.Nat!=true)
                throw new Exception("Disabling Wi-Fi lost pending hardware/identity or retained committed Wi-Fi.");
            if(!RestoreSettingsDrafts() || Nat.IsChecked!=true || hardwareForwarder!="" || Memory.Text!="8192" || CaptureIdentityDraft()!=originalIdentity)
                throw new Exception("Cancel undid a committed Wi-Fi disable.");
            foreach(bool needsRestart in new[]{false,true}) {
                LoadBaseline();Stage();draft=CaptureHardwareDraft();identity=CaptureIdentityDraft();var baseline=committedHardware;
                failWrite=!needsRestart;restart=needsRestart;
                if(await PrepareWifiCore()) throw new Exception("Failed/restart-only preparation was reported complete.");
                RequirePending(draft,identity);
                if(committedHardware!=baseline) throw new Exception("Failed/restart-only preparation replaced the saved network baseline.");
                RestoreSettingsDrafts();failWrite=restart=false;
            }
            LoadBaseline();Stage();draft=CaptureHardwareDraft();identity=CaptureIdentityDraft();var before=committedHardware;
            failStatus=true;
            if(!await PrepareWifiCore() || currentState!="unknown" || !networkDraftRefreshPending || committedHardware!=before)
                throw new Exception("Unavailable post-prepare status was treated as verified network state.");
            RequirePending(draft,identity);
            failStatus=false;await RefreshState(full:false);
            if(networkDraftRefreshPending || committedHardware?.Nat!=false || committedHardware?.Memory!="8192" || Memory.Text!=draft.Memory || CaptureIdentityDraft()!=identity || !hardwareDirty || !identityDirty)
                throw new Exception("Normal status polling failed to recover only the committed network baseline.");
            RestoreSettingsDrafts();
            failStatus=true;await DisableWifiCore();
            if(!networkDraftRefreshPending) throw new Exception("Failed status did not retain the pending network read.");
            ResetVmContext();
            if(networkDraftRefreshPending || loadedHardware || Memory.Text!="4096") throw new Exception("VM context reset retained another context's network commit or loaded state.");
            failStatus=false;memory=16384;await RefreshState(full:false);
            if(!loadedHardware || Memory.Text!="16384" || Cpus.Text!="1" || committedHardware?.Memory!="16384")
                throw new Exception("A stale network rebase skipped the new VM's full hardware load.");
        } finally {
            loadingHardware=loadingIdentity=true;
            try {
                Tap.ItemsSource=previousTaps;Uplink.ItemsSource=previousUplinks;Physical.ItemsSource=previousPhysical;BridgeTap.ItemsSource=previousBridgeTaps;Snapshots.ItemsSource=previousSnapshots;
                Physical.SelectedValue=previousPhysicalValue;BridgeTap.SelectedItem=previousBridgeTap;Snapshots.SelectedItem=previousSnapshot;
                GuestUser.Text=previousUser;Fingerprint.Text=previousFingerprint;Iso.Text=previousIso;DiskSize.Text=previousDisk;DiskInfo.Text=previousDiskInfo;
                ConnectIso.IsChecked=previousInstaller;PacketFilterTerms.IsChecked=previousTerms;
            } finally { loadingHardware=loadingIdentity=false; }
            controlFixtureBackend=previousBackend;committedHardware=previousDraft;committedIdentity=previousIdentity;
            hardwareDirty=identityDirty=true;RestoreSettingsDrafts();committedHardware=previousCommitted;committedIdentity=previousCommittedIdentity;
            latest=previousStatus;wifiForwarder=previousWifi;wifiStatusError=previousError;currentState=previousState;State.Text=previousStateText;
            loadedHardware=previousLoaded;hardwareDirty=previousDirty;identityDirty=previousIdentityDirty;networkDraftRefreshPending=previousPending;UpdatePowerControls();
            UsbDevices.ItemsSource=previousUsbItems;UsbDevices.SelectedItem=previousUsbSelection;usbInventoryContext=previousUsbCurrent?contextVersion:-1;UsbHint.Text=previousUsbHint;
            VmSummary.Text=previousSummary;StatusDetail.Text=previousDetail;NetworkNotice.Text=previousNetworkNotice;consoleDetached=previousDetached;Tabs.SelectedIndex=previousPage;
        }
    }
}
