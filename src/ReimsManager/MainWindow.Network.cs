using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ReimsManager;

public partial class MainWindow
{
    private string SelectedUplink => Uplink.SelectedValue as string ?? "";
    private WifiForwarderState? wifiForwarder;
    private string? wifiStatusError;
    private string hardwareForwarder = "";
    private bool networkDraftRefreshPending;

    private void RebaseNetworkDraft()
    {
        if (!networkDraftRefreshPending || committedHardware is not { } saved ||
            currentState is not ("stopped" or "shutdown" or "running" or "paused" or "suspended" or "prelaunch") ||
            wifiForwarder is not { KnownKind: true } || !latest.GetProperty("prepared").GetBoolean()) return;
        string mode = latest.GetProperty("network").GetString() ?? "";
        if (mode is not ("nat" or "bridge")) return;
        string adapter = latest.GetProperty("network_adapter").GetString() ?? "";
        string uplink = latest.GetProperty("network_uplink_id").GetString() ?? "";
        string host = latest.GetProperty("guest_host").GetString() ?? "";
        string forwarder = wifiForwarder.Kind ?? "";
        var draft = CaptureHardwareDraft();
        loadingHardware = true;
        try {
            if (adapter.Length > 0 && !Tap.Items.Cast<string>().Contains(adapter))
                Fill(Tap, Tap.Items.Cast<string>().Append(adapter), true);
            object? tap = adapter.Length == 0 ? null : Tap.Items.Cast<string>().First(x => x == adapter);
            bool nat = mode == "nat";
            // Only changed backend fields replace network edits. Failed/restart-only
            // preparation must not erase a draft when the saved field stayed put.
            Nat.IsChecked = nat == saved.Nat ? draft.Nat : nat; Bridge.IsChecked = Nat.IsChecked != true;
            Tap.SelectedItem = Equals(tap, saved.Tap) ? draft.Tap : tap;
            SelectSavedUplink(uplink == saved.Uplink ? draft.Uplink : uplink);
            GuestHost.Text = host == saved.Host ? draft.Host : host;
            hardwareForwarder = forwarder == saved.Forwarder ? draft.Forwarder : forwarder;
            committedHardware = saved with { Nat = nat, Tap = tap, Uplink = uplink, Host = host, Forwarder = forwarder };
            hardwareDirty = CaptureHardwareDraft() != committedHardware;
            loadedHardware = true;
            networkDraftRefreshPending = false;
        } finally { loadingHardware = false; }
    }

    private bool NetworkPreparationRequired(string message)
    {
        OpenBridgeTab(this, new RoutedEventArgs());
        ShowError(message);
        return false;
    }

    private async Task RefreshNetworkChoices()
    {
        long version = contextVersion;
        try {
            var inventory = NetworkInventory.Parse(await Execute("GetNetworkUplinks", [], cancellation: statusCancellation.Token));
            if (version != contextVersion || busy || closing) return;
            ApplyNetworkChoices(inventory);
            NetworkNotice.Text = "物理リンクの表示は LAN・インターネットの疎通確認ではありません。IPv4 / IPv6 の利用可否は回線・ルーターにも依存します。";
        } catch (Exception error) {
            if (version == contextVersion && !closing) NetworkNotice.Text = "接続先を取得できません。現在の VM 設定は保持しています。\n" + error.Message;
        }
    }

    private void ApplyNetworkChoices(NetworkInventory inventory)
    {
        string selected = SelectedUplink, physical = Physical.SelectedValue as string ?? "";
        bool loading = loadingHardware; loadingHardware = true;
        try {
            var values = inventory.Uplinks.ToList();
            foreach (string saved in new[] { selected, physical }.Where(value => value.Length > 0).Distinct())
                if (values.All(value => value.Id != saved)) values.Add(new(saved, "保存済みの接続先", "現在見つかりません", "other", "見つかりません", false));
            Uplink.ItemsSource = Physical.ItemsSource = values;
            Uplink.SelectedValue = selected; Physical.SelectedValue = physical;
            var connectedWifi = values.Where(value => value.Kind == "wifi" && value.Connected).ToArray();
            if (physical.Length == 0 && connectedWifi.Length == 1) Physical.SelectedValue = connectedWifi[0].Id;
            Fill(Tap, inventory.Taps, true); Fill(BridgeTap, inventory.Taps, true);
        } finally { loadingHardware = loading; }
        UpdateWifiControls();
    }

    private void SelectSavedUplink(string id)
    {
        if (id.Length > 0 && Uplink.Items.Cast<NetworkUplink>().All(value => value.Id != id)) {
            var values = Uplink.Items.Cast<NetworkUplink>().Append(new(id, "保存済みの接続先", "現在見つかりません", "other", "見つかりません", false)).ToArray();
            Uplink.ItemsSource = values;
        }
        Uplink.SelectedValue = id;
    }

    private async void ReloadNetworkChoices(object sender, RoutedEventArgs e)
    {
        if (!IsUiBusy && HasEngine) await RefreshNetworkChoices();
    }

    private void ReadWifiStatus()
    {
        wifiStatusError = null;
        try { wifiForwarder = WifiForwarderState.Read(latest); }
        catch (Exception error) { wifiForwarder = null; wifiStatusError = error.Message; }
    }

    private void UpdateWifiControls()
    {
        if (WifiCompatCard is null) return;
        bool selectedWifi = Physical.SelectedItem is NetworkUplink { Kind: "wifi" };
        bool configured = wifiForwarder is { KnownKind: true } &&
            (wifiForwarder.Configured || wifiForwarder.Running || wifiForwarder.Kind==WifiForwarderState.LegacyMacNat || !string.IsNullOrWhiteSpace(wifiForwarder.Error));
        WifiCompatCard.Visibility = selectedWifi || configured || WifiForwarderState.IsMacNat(hardwareForwarder) ? Visibility.Visible : Visibility.Collapsed;
        WiredBridgeControls.Visibility = selectedWifi || configured || WifiForwarderState.IsMacNat(hardwareForwarder) ? Visibility.Collapsed : Visibility.Visible;
        WifiForwarderSummary.Text = currentState == "unknown" || wifiForwarder is null ? "転送状態は未確認です" : wifiForwarder.Summary(currentState);
        string? error = currentState=="unknown" ? StatusDetail.Text : wifiStatusError ?? wifiForwarder?.Error;
        WifiForwarderDetail.Text = string.IsNullOrWhiteSpace(error)
            ? "準備完了はプロセスの初期化状態です。LAN・インターネットの IPv4 / IPv6 疎通は別途確認してください。"
            : error.Length > 2048 ? error[..2048]+"…" : error;
        if (wifiForwarder?.Kind==WifiForwarderState.LegacyMacNat)
            WifiForwarderDetail.Text="旧 IPv4 設定です。「Wi-Fi 互換接続を準備」を再実行して、IPv6 用の設定を準備してください。\n"+WifiForwarderDetail.Text;
        PrepareWifiButton.IsEnabled = !IsUiBusy && CanOperate("PrepareWifi") && selectedWifi && wifiForwarder is { KnownKind: true } &&
            (wifiForwarder.Installed || PacketFilterTerms.IsChecked == true);
        DisableWifiButton.IsEnabled = !IsUiBusy && CanOperate("DisableWifi") && (configured || WifiForwarderState.IsMacNat(hardwareForwarder));
        PacketFilterTerms.IsEnabled = !IsUiBusy;
    }

    private void WifiSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWifiControls();
    private void WifiTermsChanged(object sender, RoutedEventArgs e) => UpdatePowerControls();

    private bool CanCommitWifiOperation()
    {
        if (!loadedHardware || committedHardware is null) {
            ShowError("元の設定をまだ読み込めていません。変更を保持しました。状態を更新して確認してください。");return false;
        }
        return true;
    }

    private async void PrepareWifi(object sender, RoutedEventArgs e) => await PrepareWifiCore();

    private async Task<bool> PrepareWifiCore()
    {
        if (!RequireAction("PrepareWifi") || !CanCommitWifiOperation()) return false;
        if (Physical.SelectedItem is not NetworkUplink { Kind: "wifi" } physical || wifiForwarder is not { KnownKind: true }) { ShowError("物理 Wi-Fi と転送状態を確認してください。");return false; }
        string[] arguments;
        try { arguments = WifiForwarderState.PrepareArguments(physical.Id,wifiForwarder.Installed,PacketFilterTerms.IsChecked == true); }
        catch(Exception error) { ShowError(error.Message);return false; }
        if (!ManagerValidation.Host(GuestHost.Text.Trim())) { ShowError("macOS の IP アドレス・ホスト名を確認してください。");return false; }
        if (GuestHost.Text.Trim().Length>0) arguments=arguments.Concat(new[]{"-GuestHost",GuestHost.Text.Trim()}).ToArray();
        string resultFile=System.IO.Path.Combine(VmDirectory,"state","wifi-prepare-result.json");
        var entry=selectedEntry;DateTime started=DateTime.UtcNow;
        applyingSettings=true;UpdatePowerControls();
        try {
            bool completed=await Operate("PrepareWifi",arguments,true);
            if(entry!=selectedEntry || closing) return false;
            if(!completed) {
                try {
                    string? error=WifiPrepareResult.Read(resultFile,started).Error;
                    if(!string.IsNullOrWhiteSpace(error)) ShowError("Wi-Fi の準備に失敗しました。一部の設定が変更済みの可能性があるため、状態を確認してください。\n"+error);
                } catch { } // A missing/stale/malformed optional receipt must not replace the original error.
                return false;
            }
            try {
                var result=WifiPrepareResult.Read(resultFile,started);
                if (result.RestartRequired) {
                    string message="準備を続けるには Windows の再起動が必要です。未保存の変更は保持しています。再起動前に保存または破棄し、Wi-Fi の準備をもう一度実行してください。";
                    if (selfTest) ShowError(message);
                    else MessageBox.Show(this,message,"Wi-Fi 互換接続",MessageBoxButton.OK,MessageBoxImage.Information);
                } else if (!result.Prepared || !result.Configured) { ShowError("Wi-Fi の準備は未完了です。転送状態とログを確認してください。"); }
                return !result.RestartRequired && result.Prepared && result.Configured;
            } catch (Exception error) { ShowError("Wi-Fi の準備結果を確認できません。変更済みの可能性があるため、状態とログを確認してください。\n"+error.Message); }
        } finally { applyingSettings=false;UpdatePowerControls(); }
        return false;
    }

    private async void DisableWifi(object sender, RoutedEventArgs e) => await DisableWifiCore();

    private async Task<bool> DisableWifiCore()
    {
        if (!RequireAction("DisableWifi") || !CanCommitWifiOperation()) return false;
        applyingSettings=true;UpdatePowerControls();
        try { return await Operate("DisableWifi",[],true); }
        finally { applyingSettings=false;UpdatePowerControls(); }
    }

    private void OpenPacketFilterTerms(object sender,System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme=="https" && e.Uri.Host=="www.ntkernel.com")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute=true });
        e.Handled=true;
    }
}
