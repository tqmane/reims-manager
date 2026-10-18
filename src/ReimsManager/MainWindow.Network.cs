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

    private async Task RefreshNetworkChoices()
    {
        long version = contextVersion;
        try {
            var inventory = NetworkInventory.Parse(await Execute("GetNetworkUplinks", [], cancellation: statusCancellation.Token));
            if (version != contextVersion || busy || closing) return;
            ApplyNetworkChoices(inventory);
            NetworkNotice.Text = "物理リンクの表示は LAN・インターネットの疎通確認ではありません。Wi-Fi の互換接続は IPv4 専用です。";
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
        bool configured = wifiForwarder is { Kind: null or WifiForwarderState.MacNat } &&
            (wifiForwarder.Configured || wifiForwarder.Running || !string.IsNullOrWhiteSpace(wifiForwarder.Error));
        WifiCompatCard.Visibility = selectedWifi || configured || hardwareForwarder == WifiForwarderState.MacNat ? Visibility.Visible : Visibility.Collapsed;
        WiredBridgeControls.Visibility = selectedWifi || configured || hardwareForwarder==WifiForwarderState.MacNat ? Visibility.Collapsed : Visibility.Visible;
        WifiForwarderSummary.Text = currentState == "unknown" || wifiForwarder is null ? "転送状態は未確認です" : wifiForwarder.Summary(currentState);
        string? error = currentState=="unknown" ? StatusDetail.Text : wifiStatusError ?? wifiForwarder?.Error;
        WifiForwarderDetail.Text = string.IsNullOrWhiteSpace(error)
            ? "準備完了はプロセスの初期化状態です。LAN・インターネットの疎通は別途確認してください。外部 IPv6 には対応していません。"
            : error.Length > 2048 ? error[..2048]+"…" : error;
        PrepareWifiButton.IsEnabled = !IsUiBusy && CanOperate("PrepareWifi") && selectedWifi && wifiForwarder is { Kind: null or WifiForwarderState.MacNat } &&
            (wifiForwarder.Installed || PacketFilterTerms.IsChecked == true);
        DisableWifiButton.IsEnabled = !IsUiBusy && CanOperate("DisableWifi") && (configured || hardwareForwarder==WifiForwarderState.MacNat);
        PacketFilterTerms.IsEnabled = !IsUiBusy;
    }

    private void WifiSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWifiControls();
    private void WifiTermsChanged(object sender, RoutedEventArgs e) => UpdatePowerControls();

    private bool CanCommitWifiOperation()
    {
        var saved=committedHardware;
        if (identityDirty || hardwareDirty && (saved is null || CaptureHardwareDraft() != (saved with {
            Nat = Nat.IsChecked == true, Tap = Tap.SelectedItem, Uplink = SelectedUplink, Host = GuestHost.Text }))) {
            ShowError("先にネットワーク以外の未保存設定を適用またはキャンセルしてください。");return false;
        }
        return true;
    }

    private async void PrepareWifi(object sender, RoutedEventArgs e)
    {
        if (!RequireAction("PrepareWifi") || !CanCommitWifiOperation()) return;
        if (Physical.SelectedItem is not NetworkUplink { Kind: "wifi" } physical || wifiForwarder is null) { ShowError("物理 Wi-Fi と転送状態を確認してください。");return; }
        string[] arguments;
        try { arguments = WifiForwarderState.PrepareArguments(physical.Id,wifiForwarder.Installed,PacketFilterTerms.IsChecked == true); }
        catch(Exception error) { ShowError(error.Message);return; }
        if (!ManagerValidation.Host(GuestHost.Text.Trim())) { ShowError("macOS の IP アドレス・ホスト名を確認してください。");return; }
        if (GuestHost.Text.Trim().Length>0) arguments=arguments.Concat(new[]{"-GuestHost",GuestHost.Text.Trim()}).ToArray();
        string resultFile=System.IO.Path.Combine(VmDirectory,"state","wifi-prepare-result.json");
        var entry=selectedEntry;DateTime started=DateTime.UtcNow;
        applyingSettings=true;UpdatePowerControls();
        try { if (await Operate("PrepareWifi",arguments,true) && entry==selectedEntry && !closing) {
            try {
                var result=WifiPrepareResult.Read(resultFile,started);
                if (result.RestartRequired) {
                    MessageBox.Show(this,"準備を続けるには Windows の再起動が必要です。作業を保存して再起動し、Wi-Fi の準備をもう一度実行してください。","Wi-Fi 互換接続",MessageBoxButton.OK,MessageBoxImage.Information);
                } else if (!result.Prepared || !result.Configured) { ShowError("Wi-Fi の準備は未完了です。転送状態とログを確認してください。"); }
                if (result.Configured) { hardwareDirty=false;loadedHardware=false;await RefreshState(); }
            } catch (Exception error) { ShowError("Wi-Fi の準備結果を確認できません。変更済みの可能性があるため、状態とログを確認してください。\n"+error.Message); }
        }} finally { applyingSettings=false;UpdatePowerControls(); }
    }

    private async void DisableWifi(object sender, RoutedEventArgs e)
    {
        if (!RequireAction("DisableWifi") || !CanCommitWifiOperation()) return;
        applyingSettings=true;UpdatePowerControls();
        try { if (await Operate("DisableWifi",[],true)) { hardwareDirty=false;loadedHardware=false;await RefreshState(); } }
        finally { applyingSettings=false;UpdatePowerControls(); }
    }

    private void OpenPacketFilterTerms(object sender,System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme=="https" && e.Uri.Host=="www.ntkernel.com")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute=true });
        e.Handled=true;
    }
}
