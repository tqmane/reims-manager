using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ReimsManager;

public partial class MainWindow
{
    private readonly DispatcherTimer clipboardTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource clipboardCancel = new();
    private bool clipboardBusy, loadingClipboard;
    private string? lastClipboard;

    private void ResetClipboard() { clipboardCancel.Cancel(); clipboardCancel.Dispose(); clipboardCancel = new(); lastClipboard = null; }
    private void ClipboardChanged(object sender, RoutedEventArgs e)
    {
        if (ClipboardStatus is null || loadingClipboard || selectedEntry is null) return;
        string file = selectedEntry.File;
        bool hadPreference = settings.ClipboardByVm.TryGetValue(file, out bool previous);
        settings.SetClipboardEnabled(file, ClipboardSharing.IsChecked == true);
        if (!SavePreferences()) {
            if (hadPreference) settings.SetClipboardEnabled(file, previous);
            else settings.ClipboardByVm.Remove(file);
            RestoreClipboardPreference();
            return;
        }
        ResetClipboard();
        UpdateClipboardAvailability();
    }

    private void RestoreClipboardPreference()
    {
        clipboardTimer.Stop(); ResetClipboard();
        loadingClipboard = true;
        try { ClipboardSharing.IsChecked = selectedEntry is not null && settings.ClipboardEnabled(selectedEntry.File); }
        finally { loadingClipboard = false; }
        UpdateClipboardAvailability();
    }

    private void UpdateClipboardAvailability()
    {
        if (ClipboardSharing is null || ClipboardStatus is null) return;
        // The preference belongs to the VM and can be set before it is running.
        ClipboardSharing.IsEnabled = !IsUiBusy && HasVm;
        if (ClipboardSharing.IsChecked != true) {
            if (clipboardTimer.IsEnabled) { clipboardTimer.Stop(); ResetClipboard(); }
            ClipboardStatus.Text = "自動共有はオフです。この VM の設定として保存されます。";
        } else if (!HasVm || !HasEngine || currentState != "running" || closing) {
            if (clipboardTimer.IsEnabled) { clipboardTimer.Stop(); ResetClipboard(); }
            ClipboardStatus.Text = "自動共有はオンです。VM が実行中になり、SSH に接続できると共有を再開します。";
        } else if (!clipboardTimer.IsEnabled) {
            ClipboardStatus.Text = "自動共有はオンです。SSH の接続を確認しています。";
            if (!selfTest) clipboardTimer.Start();
        }
    }

    private async Task SyncClipboard()
    {
        if (clipboardBusy || busy || ClipboardSharing.IsChecked != true || latest.ValueKind != JsonValueKind.Object || currentState != "running") return;
        clipboardBusy = true;
        string directory = VmDirectory;
        var token = clipboardCancel.Token;
        try {
            string? host = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            if (host is not null && host != lastClipboard) {
                await GuestClipboard.Transfer(directory, host, token);
                token.ThrowIfCancellationRequested();
                lastClipboard = host;
            } else {
                string guest = await GuestClipboard.Transfer(directory, null, token);
                token.ThrowIfCancellationRequested();
                string? current = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                // A new local copy made during SSH always wins over an older guest read.
                if (guest != lastClipboard && current == host && guest.Length > 0) Clipboard.SetText(guest);
                if (current == host) lastClipboard = guest;
            }
            token.ThrowIfCancellationRequested();
            ClipboardStatus.Text = "テキストを共有しています。";
        } catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !closing) ClipboardStatus.Text = error.Message; }
        finally { clipboardBusy = false; }
    }

    private async void CopyToGuest(object sender, RoutedEventArgs e)
    {
        if (clipboardBusy || busy || !HasVm || !HasEngine || currentState != "running") return; clipboardBusy = true;
        var token = clipboardCancel.Token;
        try { if (Clipboard.ContainsText()) { await GuestClipboard.Transfer(VmDirectory, Clipboard.GetText(), token); token.ThrowIfCancellationRequested(); ClipboardStatus.Text = "macOS にコピーしました。"; } }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !closing) ClipboardStatus.Text = error.Message; }
        finally { clipboardBusy = false; }
    }
    private async void CopyFromGuest(object sender, RoutedEventArgs e)
    {
        if (clipboardBusy || busy || !HasVm || !HasEngine || currentState != "running") return; clipboardBusy = true;
        var token = clipboardCancel.Token;
        try { string text = await GuestClipboard.Transfer(VmDirectory, null, token); token.ThrowIfCancellationRequested(); if (text.Length > 0) Clipboard.SetText(text); ClipboardStatus.Text = "Windows にコピーしました。"; }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !closing) ClipboardStatus.Text = error.Message; }
        finally { clipboardBusy = false; }
    }

    private async Task<bool> ReadIdentity()
    {
        if (!CanOperate("GetIdentity") || identityDirty) return false;
        long version = contextVersion;
        try {
            using var data = JsonDocument.Parse(await Execute("GetIdentity", [], cancellation: statusCancellation.Token));
            if (version != contextVersion || closing || identityDirty) return false;
            var i = data.RootElement;
            loadingIdentity = true;
            try {
                IdentityModel.Text = i.GetProperty("model").GetString(); IdentitySerial.Text = i.GetProperty("serial").GetString();
                IdentityMlb.Text = i.GetProperty("mlb").GetString(); IdentityUuid.Text = i.GetProperty("uuid").GetString(); IdentityRom.Text = i.GetProperty("rom").GetString();
                identityDirty = false; RememberIdentityDraft();
            } finally { loadingIdentity = false; }
            UpdatePowerControls(); return true;
        } catch (Exception error) { if (version == contextVersion && !closing && error is not OperationCanceledException) ShowError(error.Message); return false; }
    }
    private async void LoadIdentityClick(object sender, RoutedEventArgs e) {
        if (!DiscardPendingChanges()) return;
        bool wasDirty = identityDirty;
        long version = contextVersion;
        identityDirty = false;
        if (!await ReadIdentity() && version == contextVersion) identityDirty = wasDirty;
        UpdatePowerControls();
    }
    private bool ConfirmIdentityChange() => !selfTest && Confirm("この VM の識別情報を変更します。Apple Account の再認証が必要になることがあります。");
    private async void SaveIdentity(object sender, RoutedEventArgs e) => await SaveIdentityCore();
    private async Task<bool> SaveIdentityCore(bool confirm = true)
    {
        if (!RequireAction("SetIdentity") || confirm && !ConfirmIdentityChange()) return false;
        string data = JsonSerializer.Serialize(new { model = IdentityModel.Text, serial = IdentitySerial.Text, mlb = IdentityMlb.Text, uuid = IdentityUuid.Text, rom = IdentityRom.Text });
        if (!await Operate("SetIdentity", input: data)) return false;
        RememberIdentityDraft();
        await ReadIdentity();
        return true;
    }

    private sealed record UsbEntry(string Id, string Label);
    private async void InstallUsbDk(object sender, RoutedEventArgs e) => await Operate("InstallUsbDk", elevate: true);
    private async void RefreshUsb(object sender, RoutedEventArgs e)
    {
        if (!RequireAction("ListUsb")) return;
        long version = contextVersion;
        try {
            using var list = JsonDocument.Parse(await Execute("ListUsb", [], cancellation: statusCancellation.Token));
            if (version != contextVersion || closing) return;
            UsbDevices.ItemsSource = list.RootElement.EnumerateArray().Select(x => new UsbEntry(x.GetProperty("id").GetString()!, x.GetProperty("label").GetString()!)).ToArray();
            UsbHint.Text = "登録した機器は同じ USB ポートへの抜き差し後も自動接続します。別のポートでは選び直してください。新しい機器の検索には VM の起動が必要です。";
        } catch (Exception error) { if (version == contextVersion && !closing && error is not OperationCanceledException) UsbHint.Text = error.Message; }
        UpdatePowerControls();
    }
    private async void AttachUsb(object sender, RoutedEventArgs e) { if (UsbDevices.SelectedItem is UsbEntry u && await Operate("AttachUsb", ["-UsbId", u.Id])) RefreshUsb(sender, e); }
    private async void DetachUsb(object sender, RoutedEventArgs e) { if (UsbDevices.SelectedItem is UsbEntry u && await Operate("DetachUsb", ["-UsbId", u.Id])) RefreshUsb(sender, e); }
}
