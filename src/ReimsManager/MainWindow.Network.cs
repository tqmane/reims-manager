using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ReimsManager;

public partial class MainWindow
{
    private string SelectedUplink => Uplink.SelectedValue as string ?? "";

    private async Task RefreshNetworkChoices()
    {
        long version = contextVersion;
        try {
            var inventory = NetworkInventory.Parse(await Execute("GetNetworkUplinks", [], cancellation: statusCancellation.Token));
            if (version != contextVersion || busy || closing) return;
            ApplyNetworkChoices(inventory);
            NetworkNotice.Text = "Wi-Fi のブリッジ接続はドライバーとアクセスポイントに依存します。接続中の表示は、VM の通信確認を意味しません。";
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
            Fill(Tap, inventory.Taps, true); Fill(BridgeTap, inventory.Taps, true);
        } finally { loadingHardware = loading; }
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
}
