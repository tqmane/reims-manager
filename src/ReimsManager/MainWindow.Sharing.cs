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
    // SSH verifies the recorded process and pairing independently of QMP status.
    private bool CanShareClipboard => VmActionPolicy.CanRun("Clipboard", currentState, HasVm, HasEngine);

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
            ClipboardStatus.Text = UiText.T("自動共有はオフです。この VM の設定として保存されます。");
        } else if (!CanShareClipboard || closing) {
            if (clipboardTimer.IsEnabled) { clipboardTimer.Stop(); ResetClipboard(); }
            ClipboardStatus.Text = UiText.T("自動共有はオンです。VM が実行中になり、SSH に接続できると共有を再開します。");
        } else if (!clipboardTimer.IsEnabled) {
            ClipboardStatus.Text = UiText.T("自動共有はオンです。SSH の接続を確認しています。");
            if (!selfTest) clipboardTimer.Start();
        }
    }

    private async Task SyncClipboard()
    {
        if (clipboardBusy || busy || ClipboardSharing.IsChecked != true || !CanShareClipboard || closing) return;
        clipboardBusy = true;
        string directory = VmDirectory;
        var token = clipboardCancel.Token;
        try {
            string? host = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            bool coldUnknown = currentState == "unknown" && lastClipboard is null;
            if (GuestClipboard.ShouldPush(currentState, host, lastClipboard)) {
                await GuestClipboard.Transfer(directory, host, token);
                token.ThrowIfCancellationRequested();
                lastClipboard = host;
            } else {
                string guest = await GuestClipboard.Transfer(directory, null, token);
                token.ThrowIfCancellationRequested();
                string? current = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                // A new local copy made during SSH always wins over an older guest read.
                if (guest != lastClipboard && current == host && guest.Length > 0) Clipboard.SetText(guest);
                // Establish the guest baseline on the first unknown-state read, so
                // a newer local copy can win on the next tick without being overwritten.
                if (current == host || coldUnknown) lastClipboard = guest;
            }
            token.ThrowIfCancellationRequested();
            ClipboardStatus.Text = UiText.T("テキストを共有しています。");
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !closing) ClipboardStatus.Text = error.Message; }
        finally { clipboardBusy = false; }
    }

    private async void CopyToGuest(object sender, RoutedEventArgs e)
    {
        if (clipboardBusy || !CanOperate("Clipboard") || closing) return; clipboardBusy = true;
        var token = clipboardCancel.Token;
        try { if (Clipboard.ContainsText()) { await GuestClipboard.Transfer(VmDirectory, Clipboard.GetText(), token); token.ThrowIfCancellationRequested(); ClipboardStatus.Text = UiText.T("macOS にコピーしました。"); } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !closing) ClipboardStatus.Text = error.Message; }
        finally { clipboardBusy = false; }
    }
    private async void CopyFromGuest(object sender, RoutedEventArgs e)
    {
        if (clipboardBusy || !CanOperate("Clipboard") || closing) return; clipboardBusy = true;
        var token = clipboardCancel.Token;
        try { string text = await GuestClipboard.Transfer(VmDirectory, null, token); token.ThrowIfCancellationRequested(); if (text.Length > 0) Clipboard.SetText(text); ClipboardStatus.Text = UiText.T("Windows にコピーしました。"); }
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
    private bool ConfirmIdentityChange() => !selfTest && Confirm(UiText.T("この VM の識別情報を変更します。Apple Account の再認証が必要になることがあります。"));
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

    private sealed record UsbEntry(string Id, string Label, bool Present, bool Selected, bool Active, bool Attached, string ErrorText = "")
    {
        public string DisplayLabel => UiText.UsbLabel(Label);
        public bool IsAppleDevice => Id.StartsWith("hostiphone-", StringComparison.Ordinal);
        public string DeviceGlyph => IsAppleDevice ? "\uE8EA" : "\uECF0";
        public string CleanErrorText => Services.BackendErrorFormatter.CleanMessage(ErrorText);
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
        public string StatusBadgeText {
            get {
                var match = System.Text.RegularExpressions.Regex.Match(DisplayLabel, @"(?:［([^］]+)］|\[([^\]]+)\])$");
                if (!match.Success) return "";
                return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            }
        }
        public string TitleText => System.Text.RegularExpressions.Regex.Replace(DisplayLabel, @"\s*(?:［[^］]+］|\[[^\]]+\])$", "").Trim();
        public bool HasStatusBadge => StatusBadgeText.Length > 0;
        public bool IsErrorState => HasError || Label.Contains("エラー", StringComparison.Ordinal) || Label.Contains("復元待ち", StringComparison.Ordinal) || Label.Contains("確認が必要", StringComparison.Ordinal);
        public bool IsPendingState => !Attached && !IsErrorState && (Selected || Active);
        public bool CanResetIphone => IsAppleDevice && !Attached && (HasError || Label.Contains("復元待ち", StringComparison.Ordinal) || Label.Contains("エラー", StringComparison.Ordinal) || Label.Contains("確認が必要", StringComparison.Ordinal) || Label.Contains("完全終了後", StringComparison.Ordinal));
    }
    private long usbRequestVersion, usbInventoryContext = -1;
    private long usbAutoRefreshAttemptContext = -1;
    private bool refreshingUsb, usbDriverRestartPending, usbLivePollBusy;
    private bool usbAutoRefreshQueued;
    private Task usbAutoRefreshTask = Task.CompletedTask;
    private DispatcherTimer? usbLivePollTimer;

    private void ResetUsbInventory()
    {
        usbLivePollTimer?.Stop();
        usbRequestVersion++;
        usbInventoryContext = -1; refreshingUsb = false;
        usbAutoRefreshAttemptContext = -1;
        UsbDevices.ItemsSource = null;
        UsbHint.Text = UiText.T("「デバイス一覧を更新」で、この VM の USB 登録と接続状態を確認します。");
    }

    private void EnsureUsbLivePoll()
    {
        if (selfTest || closing || UsbPage is null || !UsbPage.IsVisible || usbInventoryContext != contextVersion || IsUiBusy || refreshingUsb) {
            usbLivePollTimer?.Stop();
            return;
        }
        bool hasLiveApple = UsbDevices.ItemsSource is IEnumerable<UsbEntry> entries &&
            entries.Any(e => e.IsAppleDevice && (e.Active || e.Selected || e.HasError || e.CanResetIphone));
        if (!hasLiveApple) {
            usbLivePollTimer?.Stop();
            return;
        }
        if (usbLivePollTimer is null) {
            usbLivePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            usbLivePollTimer.Tick += async (_, _) => await PollAppleUsbAsync();
        }
        if (!usbLivePollTimer.IsEnabled) usbLivePollTimer.Start();
    }

    private async Task PollAppleUsbAsync()
    {
        if (usbLivePollBusy || selfTest || closing || IsUiBusy || refreshingUsb || UsbPage is null || !UsbPage.IsVisible || usbInventoryContext != contextVersion || !HasEngine || !HasVm)
            return;
        usbLivePollBusy = true;
        long version = contextVersion;
        string? selectedId = (UsbDevices.SelectedItem as UsbEntry)?.Id;
        try {
            string root = System.IO.Path.GetFullPath(Root.Text);
            string json = await Services.NativeUsbService.ListAppleUsbRowsJsonAsync(root, VmDirectory, statusCancellation.Token);
            if (version != contextVersion || closing || IsUiBusy || refreshingUsb || usbInventoryContext != contextVersion) return;
            using var doc = JsonDocument.Parse(json);
            var updatedApple = doc.RootElement.EnumerateArray().Select(ParseUsbJsonEntry).ToArray();
            var existing = (UsbDevices.ItemsSource as IEnumerable<UsbEntry>)?.ToArray() ?? [];
            var existingNonApple = existing.Where(e => !e.IsAppleDevice).ToArray();
            var oldApple = existing.Where(e => e.IsAppleDevice).ToArray();
            if (oldApple.SequenceEqual(updatedApple)) { EnsureUsbLivePoll(); return; }
            var merged = existingNonApple.Concat(updatedApple).ToArray();
            UsbDevices.ItemsSource = merged;
            UsbDevices.SelectedItem = merged.FirstOrDefault(x => x.Id == selectedId);
            UpdateUsbControls();
            EnsureUsbLivePoll();
        } catch {
            // Keep transient poll quiet; manual RefreshUsb shows errors if needed.
        } finally {
            usbLivePollBusy = false;
        }
    }

    private void UpdateUsbControls()
    {
        if (UsbRefreshButton is null) return;
        bool current = usbInventoryContext == contextVersion;
        bool available = current && !refreshingUsb && !IsUiBusy;
        var selected = UsbDevices.SelectedItem as UsbEntry;
        bool iphonePending = selected is { Active: true, Attached: false, IsAppleDevice: true };
        UsbRefreshButton.IsEnabled = !IsUiBusy && !refreshingUsb && CanOperate("ListUsb");
        InstallUsbDkButton.IsEnabled = !IsUiBusy && !refreshingUsb && CanOperate("InstallUsbDk");
        UsbDevices.IsEnabled = available;
        UsbAttachButton.IsEnabled = available && !iphonePending && CanOperate("AttachUsb") && selected is { Present: true, Attached: false };
        UsbDetachButton.IsEnabled = available && CanOperate("DetachUsb") && selected is not null && (selected.Selected || selected.Active);
        if (UsbResetButton is not null) {
            bool showReset = selected is { CanResetIphone: true };
            UsbResetButton.Visibility = showReset ? Visibility.Visible : Visibility.Collapsed;
            UsbResetButton.IsEnabled = available && showReset && CanOperate("ResetIphoneUsb");
        }
        UsbAttachButton.Content = iphonePending ? UiText.T("接続処理中") : selected is { Selected: true } or { Active: true } ? UiText.T("再接続") : UiText.T("接続して自動接続に登録");
        UsbSelectionHint.Text = refreshingUsb ? UiText.T("USB の一覧を確認しています…") : !current
            ? UiText.T("一覧を更新してから操作してください。") : selected is null
                ? UiText.T("操作する USB デバイスを一覧から選択してください。") : selected.Attached
                    ? UiText.T("VM に接続中です。解除すると自動接続の登録も削除します。") : selected.Selected || selected.Active
                        ? selected.Present ? UiText.T("接続待ちです。「再接続」で転送をやり直せます。") : UiText.T("この機器は接続待ちです。登録の解除は VM の停止中でもできます。")
                        : selected.IsAppleDevice
                            ? UiText.T("iPhone は署名済みドライバーへ一時的に切り替えます。管理者確認後、再接続が必要な場合があります。解除時は元のドライバーへ戻します。")
                            : UiText.T("接続すると、この VM の次回起動時にも同じ USB ポートから自動接続します。");
        if (iphonePending) UsbSelectionHint.Text = UiText.T("iPhone の接続・復元処理中です。抜き差し待ちの場合は USB を再接続してください。やり直す場合は、一度切断してください。");
        if (selected is { CleanErrorText.Length: > 0 }) UsbSelectionHint.Text += "\n" + selected.CleanErrorText;
        EnsureUsbLivePoll();

        string Mode(int index) => index == 1 ? "UsbDk" : "WinUSB";
        if (!loadedHardware || committedHardware is null) {
            UsbBackendHint.Text = UiText.T("VM の保存済みの転送方式を確認しています。");
            return;
        }
        string saved = Mode(committedHardware.UsbBackend);
        string active = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("usb_backend_active", out var backend) && backend.ValueKind == JsonValueKind.String
            ? backend.GetString() switch { "usbdk" => "UsbDk", "winusb" => "WinUSB", _ => UiText.T("未確認") } : UiText.T("未確認");
        UsbBackendHint.Text = UiText.Format($"保存済み: {saved}") + (currentState is "stopped" or "unprepared" ? "。" : UiText.Format($" ／ 実行中: {active}。")) +
            (UsbBackend.SelectedIndex != committedHardware.UsbBackend ? UiText.Format($" 未保存の変更: {Mode(UsbBackend.SelectedIndex)}。") : "") +
            UiText.T("\n方式を変更するには USB の登録を解除し、VM を完全終了して変更を保存します。UsbDk は導入後にここで選択してください。再起動後、機器を選び直します。");
        UsbBackendHint.Text += UiText.T("\niPhone の通常・復旧・DFU 接続には WinUSB 方式を使用します。");
        if (active == UiText.T("未確認") && currentState is not ("stopped" or "unprepared"))
            UsbBackendHint.Text += UiText.T(" 現在の方式は VM の完全終了と再起動後に確認できます。");
        if ((saved == "UsbDk" || active == "UsbDk" || UsbBackend.SelectedIndex == 1) && latest.ValueKind == JsonValueKind.Object &&
            latest.TryGetProperty("usb_usbdk_installed", out var installed) && installed.ValueKind == JsonValueKind.False)
            UsbBackendHint.Text += UiText.T("\nUsbDk の導入を確認できません。VM を完全終了して「UsbDk を導入」を実行してください。");
        if (usbDriverRestartPending)
            UsbBackendHint.Text += UiText.T("\nUsbDk の適用には Windows の再起動が必要です。作業を保存してから Windows を再起動してください。");
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

    private void UsbPageVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ScheduleUsbAutoRefresh();
        EnsureUsbLivePoll();
    }

    private async void InstallUsbDk(object sender, RoutedEventArgs e) => await InstallUsbDkCore();
    private async Task<bool> InstallUsbDkCore()
    {
        if (IsUiBusy || refreshingUsb) return false;
        if (!await Operate("InstallUsbDk", elevate: true)) {
            UsbHint.Text = UiText.T("UsbDk の導入結果を確認できません。表示されたエラーを確認し、再試行してください。");
            return false;
        }
        if (usbDriverInstallResult is not { } result) {
            ShowError(UiText.T("UsbDk の導入結果を確認できません。必要条件を再確認してください。"));
            return false;
        }
        usbDriverRestartPending |= result.RestartRequired;
        UsbHint.Text = usbDriverRestartPending
            ? UiText.T("UsbDk の導入は完了しました。適用には Windows の再起動が必要です。作業を保存して Windows を再起動し、転送方式で UsbDk を選んで保存してください。")
            : UiText.T("UsbDk を導入しました。転送方式で UsbDk を選んで保存し、VM を完全終了してから起動してください。");
        UpdatePowerControls();
        if (result.RestartRequired && !selfTest)
            MessageBox.Show(this, UsbHint.Text, UiText.T("Windows の再起動が必要です"), MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private static UsbEntry ParseUsbJsonEntry(JsonElement x)
    {
        var entry = new UsbEntry(
            x.GetProperty("id").GetString() ?? throw new JsonException(UiText.T("USB の識別情報がありません。")),
            x.GetProperty("label").GetString() ?? UiText.T("USB デバイス"),
            x.GetProperty("present").GetBoolean(),
            x.GetProperty("selected").GetBoolean(),
            x.TryGetProperty("active", out var active) && active.GetBoolean(),
            x.TryGetProperty("attached", out var attached) && attached.GetBoolean(),
            x.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "");
        if (entry.ErrorText.Length > 2048) throw new JsonException(UiText.T("USB エラー情報が長すぎます。"));
        return entry;
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
        UsbHint.Text = UiText.T("USB の登録と接続状態を確認しています…");
        UpdatePowerControls();
        try {
            using var list = JsonDocument.Parse(await Execute("ListUsb", [], cancellation: cancellation));
            if (version != contextVersion || request != usbRequestVersion || closing || cancellation.IsCancellationRequested) return false;
            var devices = list.RootElement.EnumerateArray().Select(ParseUsbJsonEntry).ToArray();
            UsbDevices.ItemsSource = devices;
            UsbDevices.SelectedItem = devices.FirstOrDefault(x => x.Id == selectedId);
            usbInventoryContext = version;
            UsbHint.Text = currentState == "stopped"
                ? UiText.T("停止中も Windows に接続した USB と登録済みの未接続機器を確認できます。接続は VM の起動後、登録の解除は停止中も行えます。")
                : devices.Length == 0 ? UiText.T("現在の方式では USB を取得できていません。機器の接続・ドライバー・Windows 側で使用中のアプリを確認し、一覧を更新してください。")
                    : UiText.T("登録はこの VM に保存されます。別の USB ポートへ移した機器は、古い登録を解除して選び直してください。");
            return true;
        } catch (Exception error) {
            if (version == contextVersion && request == usbRequestVersion && !closing) {
                UsbDevices.ItemsSource = null;
                UsbHint.Text = error is OperationCanceledException
                    ? UiText.T("USB の確認を中断しました。一覧を更新して再試行してください。")
                    : UiText.T("USB の一覧を取得できません。「デバイス一覧を更新」で再試行してください。\n") + Services.BackendErrorFormatter.CleanMessage(error.Message);
            }
            return false;
        } finally {
            if (request == usbRequestVersion) {
                refreshingUsb = false;
                UpdatePowerControls();
                EnsureUsbLivePoll();
            }
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

    private async void ResetIphoneUsb(object sender, RoutedEventArgs e) => await ResetIphoneUsbCore();
    internal async Task<bool> ResetIphoneUsbCore()
    {
        if (IsUiBusy || refreshingUsb || !RequireAction("ResetIphoneUsb")) return false;
        if (usbInventoryContext != contextVersion || UsbDevices.SelectedItem is not UsbEntry { CanResetIphone: true } selected) return false;
        if (!selfTest && !Confirm(UiText.T("この iPhone / iPad の保留中の復元エラー状態をリセットしますか？\n（現在も一時ドライバーが適用されている場合は拒否されます）")))
            return false;
        var entry = selectedEntry;
        bool success = await Operate("ResetIphoneUsb", ["-UsbId", selected.Id]);
        if (selectedEntry == entry && !closing && CanOperate("ListUsb")) await RefreshUsbCore();
        return success;
    }
}
