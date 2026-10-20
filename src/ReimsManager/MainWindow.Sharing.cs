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
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !closing) ClipboardStatus.Text = error.Message; }
        finally { clipboardBusy = false; }
    }

    private async void CopyToGuest(object sender, RoutedEventArgs e)
    {
        if (clipboardBusy || busy || !HasVm || !HasEngine || currentState != "running") return; clipboardBusy = true;
        var token = clipboardCancel.Token;
        try { if (Clipboard.ContainsText()) { await GuestClipboard.Transfer(VmDirectory, Clipboard.GetText(), token); token.ThrowIfCancellationRequested(); ClipboardStatus.Text = "macOS にコピーしました。"; } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !closing) ClipboardStatus.Text = error.Message; }
        finally { clipboardBusy = false; }
    }
    private async void CopyFromGuest(object sender, RoutedEventArgs e)
    {
        if (clipboardBusy || busy || !HasVm || !HasEngine || currentState != "running") return; clipboardBusy = true;
        var token = clipboardCancel.Token;
        try { string text = await GuestClipboard.Transfer(VmDirectory, null, token); token.ThrowIfCancellationRequested(); if (text.Length > 0) Clipboard.SetText(text); ClipboardStatus.Text = "Windows にコピーしました。"; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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

    private sealed record UsbEntry(string Id, string Label, bool Present, bool Selected, bool Active, bool Attached);
    private long usbRequestVersion, usbInventoryContext = -1;
    private long usbAutoRefreshAttemptContext = -1;
    private bool refreshingUsb, usbDriverRestartPending;
    private bool usbAutoRefreshQueued;
    private Task usbAutoRefreshTask = Task.CompletedTask;

    private void ResetUsbInventory()
    {
        usbRequestVersion++;
        usbInventoryContext = -1; refreshingUsb = false;
        usbAutoRefreshAttemptContext = -1;
        UsbDevices.ItemsSource = null;
        UsbHint.Text = "「デバイス一覧を更新」で、この VM の USB 登録と接続状態を確認します。";
    }

    private void UpdateUsbControls()
    {
        if (UsbRefreshButton is null) return;
        bool current = usbInventoryContext == contextVersion;
        bool available = current && !refreshingUsb && !IsUiBusy;
        var selected = UsbDevices.SelectedItem as UsbEntry;
        UsbRefreshButton.IsEnabled = !IsUiBusy && !refreshingUsb && CanOperate("ListUsb");
        InstallUsbDkButton.IsEnabled = !IsUiBusy && !refreshingUsb && CanOperate("InstallUsbDk");
        UsbDevices.IsEnabled = available;
        UsbAttachButton.IsEnabled = available && CanOperate("AttachUsb") && selected is { Present: true, Attached: false };
        UsbDetachButton.IsEnabled = available && CanOperate("DetachUsb") && selected is not null && (selected.Selected || selected.Active);
        UsbAttachButton.Content = selected is { Selected: true } or { Active: true } ? "再接続" : "接続して自動接続に登録";
        UsbSelectionHint.Text = refreshingUsb ? "USB の一覧を確認しています…" : !current
            ? "一覧を更新してから操作してください。" : selected is null
                ? "操作する USB デバイスを一覧から選択してください。" : selected.Attached
                    ? "VM に接続中です。解除すると自動接続の登録も削除します。" : selected.Selected || selected.Active
                        ? selected.Present ? "接続待ちです。「再接続」で転送をやり直せます。" : "この機器は接続待ちです。登録の解除は VM の停止中でもできます。"
                        : "接続すると、この VM の次回起動時にも同じ USB ポートから自動接続します。";

        string Mode(int index) => index == 1 ? "UsbDk" : "WinUSB";
        if (!loadedHardware || committedHardware is null) {
            UsbBackendHint.Text = "VM の保存済みの転送方式を確認しています。";
            return;
        }
        string saved = Mode(committedHardware.UsbBackend);
        string active = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("usb_backend_active", out var backend) && backend.ValueKind == JsonValueKind.String
            ? backend.GetString() switch { "usbdk" => "UsbDk", "winusb" => "WinUSB", _ => "未確認" } : "未確認";
        UsbBackendHint.Text = $"保存済み: {saved}" + (currentState is "stopped" or "unprepared" ? "。" : $" ／ 実行中: {active}。") +
            (UsbBackend.SelectedIndex != committedHardware.UsbBackend ? $" 未保存の変更: {Mode(UsbBackend.SelectedIndex)}。" : "") +
            "\n方式を変更するには USB の登録を解除し、VM を完全終了して変更を保存します。UsbDk は導入後にここで選択してください。再起動後、機器を選び直します。";
        if (active == "未確認" && currentState is not ("stopped" or "unprepared"))
            UsbBackendHint.Text += " 現在の方式は VM の完全終了と再起動後に確認できます。";
        if ((saved == "UsbDk" || active == "UsbDk" || UsbBackend.SelectedIndex == 1) && latest.ValueKind == JsonValueKind.Object &&
            latest.TryGetProperty("usb_usbdk_installed", out var installed) && installed.ValueKind == JsonValueKind.False)
            UsbBackendHint.Text += "\nUsbDk の導入を確認できません。VM を完全終了して「UsbDk を導入」を実行してください。";
        if (usbDriverRestartPending)
            UsbBackendHint.Text += "\nUsbDk の適用には Windows の再起動が必要です。作業を保存してから Windows を再起動してください。";
    }

    private bool UsbAutoRefreshEligible => UsbPage is not null && UsbPage.IsVisible && !IsUiBusy && CanOperate("ListUsb");

    private void ScheduleUsbAutoRefresh()
    {
        if (selfTest && !checkingUsbAutoRefresh) return;
        if (!UsbAutoRefreshEligible) { usbAutoRefreshAttemptContext = -1; return; }
        // Polling status or completing this read must not re-arm a failed attempt.
        if (usbAutoRefreshAttemptContext == contextVersion || usbAutoRefreshQueued || refreshing || refreshingUsb) return;
        usbAutoRefreshQueued = true;
        usbAutoRefreshTask = Dispatcher.InvokeAsync(async () => {
            usbAutoRefreshQueued = false;
            if (selfTest && !checkingUsbAutoRefresh) return;
            if (!UsbAutoRefreshEligible || refreshing || refreshingUsb || usbAutoRefreshAttemptContext == contextVersion) return;
            await RefreshUsbCore();
        }, DispatcherPriority.Background).Task.Unwrap();
    }

    private void UsbPageVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => ScheduleUsbAutoRefresh();

    private async void InstallUsbDk(object sender, RoutedEventArgs e) => await InstallUsbDkCore();
    private async Task<bool> InstallUsbDkCore()
    {
        if (IsUiBusy || refreshingUsb) return false;
        if (!await Operate("InstallUsbDk", elevate: true)) {
            UsbHint.Text = "UsbDk の導入結果を確認できません。表示されたエラーを確認し、再試行してください。";
            return false;
        }
        if (usbDriverInstallResult is not { } result) {
            ShowError("UsbDk の導入結果を確認できません。必要条件を再確認してください。");
            return false;
        }
        usbDriverRestartPending |= result.RestartRequired;
        UsbHint.Text = usbDriverRestartPending
            ? "UsbDk の導入は完了しました。適用には Windows の再起動が必要です。作業を保存して Windows を再起動し、転送方式で UsbDk を選んで保存してください。"
            : "UsbDk を導入しました。転送方式で UsbDk を選んで保存し、VM を完全終了してから起動してください。";
        UpdatePowerControls();
        if (result.RestartRequired && !selfTest)
            MessageBox.Show(this, UsbHint.Text, "Windows の再起動が必要です", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private async void RefreshUsb(object sender, RoutedEventArgs e) => await RefreshUsbCore();
    private async Task<bool> RefreshUsbCore()
    {
        if (IsUiBusy || !RequireAction("ListUsb")) return false;
        long version = contextVersion, request = ++usbRequestVersion;
        usbAutoRefreshAttemptContext = version;
        var cancellation = statusCancellation.Token;
        string? selectedId = (UsbDevices.SelectedItem as UsbEntry)?.Id;
        usbInventoryContext = -1; refreshingUsb = true;
        UsbHint.Text = "USB の登録と接続状態を確認しています…";
        UpdatePowerControls();
        try {
            using var list = JsonDocument.Parse(await Execute("ListUsb", [], cancellation: cancellation));
            if (version != contextVersion || request != usbRequestVersion || closing || cancellation.IsCancellationRequested) return false;
            var devices = list.RootElement.EnumerateArray().Select(x => new UsbEntry(
                x.GetProperty("id").GetString() ?? throw new JsonException("USB の識別情報がありません。"),
                x.GetProperty("label").GetString() ?? "USB デバイス", x.GetProperty("present").GetBoolean(), x.GetProperty("selected").GetBoolean(),
                x.TryGetProperty("active", out var active) && active.GetBoolean(), x.TryGetProperty("attached", out var attached) && attached.GetBoolean())).ToArray();
            UsbDevices.ItemsSource = devices;
            UsbDevices.SelectedItem = devices.FirstOrDefault(x => x.Id == selectedId);
            usbInventoryContext = version;
            UsbHint.Text = currentState == "stopped"
                ? "停止中も Windows に接続した USB と登録済みの未接続機器を確認できます。接続は VM の起動後、登録の解除は停止中も行えます。"
                : devices.Length == 0 ? "現在の方式では USB を取得できていません。機器の接続・ドライバー・Windows 側で使用中のアプリを確認し、一覧を更新してください。"
                    : "登録はこの VM に保存されます。別の USB ポートへ移した機器は、古い登録を解除して選び直してください。";
            return true;
        } catch (Exception error) {
            if (version == contextVersion && request == usbRequestVersion && !closing) {
                UsbDevices.ItemsSource = null;
                UsbHint.Text = error is OperationCanceledException ? "USB の確認を中断しました。一覧を更新して再試行してください。" : "USB の一覧を取得できません。「デバイス一覧を更新」で再試行してください。\n" + error.Message;
            }
            return false;
        } finally {
            if (request == usbRequestVersion) { refreshingUsb = false; UpdatePowerControls(); }
        }
    }

    private async void AttachUsb(object sender, RoutedEventArgs e) => await ChangeUsbConnection(true);
    private async void DetachUsb(object sender, RoutedEventArgs e) => await ChangeUsbConnection(false);
    private async Task<bool> ChangeUsbConnection(bool connect)
    {
        if (IsUiBusy || refreshingUsb || !RequireAction(connect ? "AttachUsb" : "DetachUsb")) return false;
        if (usbInventoryContext != contextVersion || UsbDevices.SelectedItem is not UsbEntry selected ||
            (connect ? !selected.Present || selected.Attached : !selected.Selected && !selected.Active)) return false;
        var entry = selectedEntry;
        bool success = await Operate(connect ? "AttachUsb" : "DetachUsb", ["-UsbId", selected.Id]);
        // A failed transaction may still have changed a live watcher. Read back
        // its actual state without clearing Operate's error message.
        if (selectedEntry == entry && !closing && CanOperate("ListUsb")) await RefreshUsbCore();
        return success;
    }
}
