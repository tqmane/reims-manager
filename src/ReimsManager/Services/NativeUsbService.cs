using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReimsManager.Services;

internal static partial class NativeUsbService
{
    private const int ForbiddenCallerRights = 0xD0156;

    [GeneratedRegex(@"^\s*Bus (\d+), Addr (\d+), Port ([0-9.]+),[^\r\n]*\r?\n\s*Class [0-9a-f]+: USB device ([0-9a-f]{4}):([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex QemuUsbHostRegex();

    [GeneratedRegex(@"^hostusb-([1-9][0-9]*)-([1-9][0-9]*(?:_[1-9][0-9]*){0,6})-([0-9a-f]{4})([0-9a-f]{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex HostUsbIdRegex();

    [GeneratedRegex(@"^hostusb-[0-9]+-[0-9_]+-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex LiveHostUsbIdRegex();

    [GeneratedRegex(@"^[1-9][0-9]*(\.[1-9][0-9]*){0,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsbPortRegex();

    [GeneratedRegex(@"^[0-9a-f]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex4Regex();

    [GeneratedRegex(@"^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex16Regex();

    [GeneratedRegex(@"^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex32Regex();

    [GeneratedRegex(@"^[0-9A-Fa-f-]{16,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UdidRegex();

    [GeneratedRegex(@"PID_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PidRegex();

    [GeneratedRegex(@"^(12a8|12ab|128[0-3]|122[0-7])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppleUsbProductRegex();

    internal sealed record SavedUsbDevice(string Id, int Bus, string Port, string Vendor, string Product, string Label);

    internal sealed record AppleProfile(string Id, string InitialInstance, string Ecid, bool Enabled, string SessionId, string? Udid);

    internal static async Task<string> ListUsbJsonAsync(string engineRoot, string vmDirectory, CancellationToken cancellation = default)
    {
        var (rows, _) = await BuildUsbRowsAsync(engineRoot, vmDirectory, cancellation).ConfigureAwait(false);
        return new JsonArray(rows.ToArray()).ToJsonString();
    }

    /// <summary>
    /// Reads live Apple USB rows using only PnP and protected ACL status files without spawning QEMU or PowerShell.
    /// </summary>
    internal static async Task<string> ListAppleUsbRowsJsonAsync(string engineRoot, string vmDirectory, CancellationToken cancellation = default)
    {
        return await Task.Run(() => {
            cancellation.ThrowIfCancellationRequested();
            string configPath = Path.Combine(vmDirectory, "state", "config.json");
            if (!File.Exists(configPath)) return "[]";
            var config = AtomicFileStore.ReadJsonNode(configPath).AsObject();
            if (config["schema"]?.GetValue<int>() != 1 || IsLegacyAppleRegistered(config)) return "[]";
            string callerSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            var blocked = NativePnpUsb.GetProtectedUsbIds(engineRoot, vmDirectory);
            var rows = GetAppleUsbRows(vmDirectory, ReadAppleProfiles(config), blocked, callerSid);
            return new JsonArray(rows.ToArray()).ToJsonString();
        }, cancellation).ConfigureAwait(false);
    }

    internal static async Task<string> AttachUsbAsync(string engineRoot, string toolsRoot, string vmDirectory, string usbId, CancellationToken cancellation = default)
    {
        if (usbId.StartsWith("hostiphone-", StringComparison.Ordinal))
            return await SetAppleUsbSelectionAsync(engineRoot, toolsRoot, vmDirectory, usbId, connect: true, cancellation).ConfigureAwait(false);

        return await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => AttachGenericUsb(engineRoot, vmDirectory, usbId, cancellation)), cancellation).ConfigureAwait(false);
    }

    internal static async Task<string> DetachUsbAsync(string vmDirectory, string usbId, CancellationToken cancellation = default)
    {
        if (usbId.StartsWith("hostiphone-", StringComparison.Ordinal))
            return SetAppleUsbSelectionOffOrReset(vmDirectory, usbId, resetOnly: false);

        return await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => DetachGenericUsb(vmDirectory, usbId, cancellation)), cancellation).ConfigureAwait(false);
    }

    internal static Task<string> ResetIphoneUsbAsync(string vmDirectory, string usbId, CancellationToken cancellation = default) =>
        Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => SetAppleUsbSelectionOffOrReset(vmDirectory, usbId, resetOnly: true)), cancellation);

    internal static string FindPwshExecutable()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", "pwsh", "pwsh.exe");
        if (File.Exists(bundled)) return bundled;
        string current = Path.Combine(AppContext.BaseDirectory, "pwsh.exe");
        return File.Exists(current) ? current : "pwsh.exe";
    }

    private static async Task<(List<JsonObject> Rows, HashSet<string> Blocked)> BuildUsbRowsAsync(string engineRoot, string vmDirectory, CancellationToken cancellation)
    {
        var config = ReadVmConfig(vmDirectory);
        var saved = ReadSavedUsbSelections(config);
        var savedIds = new HashSet<string>(saved.Select(s => s.Id), StringComparer.Ordinal);
        var (runtime, running, qmpPort) = ReadLiveRuntime(vmDirectory);

        string text = await QueryStoppedUsbInventoryAsync(engineRoot, config, cancellation).ConfigureAwait(false);
        if (text.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("USB 転送に対応した QEMU へ更新してください。");

        var states = running ? await ReadUsbObjectStatesAsync(runtime!, qmpPort, cancellation).ConfigureAwait(false)
                             : new Dictionary<string, bool>(StringComparer.Ordinal);
        var pnp = NativePnpUsb.GetPresentUsbDevices();
        var names = NativePnpUsb.BuildProductNames(pnp);
        var blocked = NativePnpUsb.GetProtectedUsbIds(engineRoot, vmDirectory);

        var rows = new List<JsonObject>();
        var presentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in QemuUsbHostRegex().Matches(text)) {
            int bus = int.Parse(m.Groups[1].Value);
            int addr = int.Parse(m.Groups[2].Value);
            string port = m.Groups[3].Value;
            string vendor = m.Groups[4].Value.ToLowerInvariant();
            string product = m.Groups[5].Value.ToLowerInvariant();
            string key = $"{vendor}:{product}";
            if (blocked.Contains(key)) continue;

            string id = $"hostusb-{bus}-{port.Replace('.', '_')}-{vendor}{product}";
            if (vendor == "05ac" && AppleUsbProductRegex().IsMatch(product) && !savedIds.Contains(id)) continue;

            string productName = names.TryGetValue(key, out string? n) ? n : "";
            string baseLabel = string.IsNullOrEmpty(productName) ? $"{key}  — USB {bus} / {port}" : $"{key} {productName} — USB {bus} / {port}";
            presentIds.Add(id);
            rows.Add(new JsonObject {
                ["id"] = id,
                ["bus"] = bus,
                ["address"] = addr,
                ["port"] = port,
                ["vendor"] = vendor,
                ["product"] = product,
                ["label"] = baseLabel,
                ["present"] = true,
                ["selected"] = savedIds.Contains(id)
            });
        }

        foreach (var entry in saved) {
            if (presentIds.Contains(entry.Id)) continue;
            presentIds.Add(entry.Id);
            rows.Add(new JsonObject {
                ["id"] = entry.Id,
                ["bus"] = entry.Bus,
                ["address"] = 0,
                ["port"] = entry.Port,
                ["vendor"] = entry.Vendor,
                ["product"] = entry.Product,
                ["label"] = entry.Label,
                ["present"] = false,
                ["selected"] = true
            });
        }

        foreach (string liveId in states.Keys) {
            if (presentIds.Contains(liveId)) continue;
            var m = HostUsbIdRegex().Match(liveId);
            if (!m.Success) continue;
            int bus = int.Parse(m.Groups[1].Value);
            string port = m.Groups[2].Value.Replace('_', '.');
            string vendor = m.Groups[3].Value;
            string product = m.Groups[4].Value;
            rows.Add(new JsonObject {
                ["id"] = liveId,
                ["bus"] = bus,
                ["address"] = 0,
                ["port"] = port,
                ["vendor"] = vendor,
                ["product"] = product,
                ["label"] = $"{vendor}:{product} — USB {bus} / {port}",
                ["present"] = false,
                ["selected"] = false
            });
        }

        foreach (var row in rows) {
            string id = row["id"]!.GetValue<string>();
            bool selected = row["selected"]!.GetValue<bool>();
            bool present = row["present"]!.GetValue<bool>();
            bool active = states.TryGetValue(id, out bool isAttached);
            bool attached = active && isAttached;
            row["active"] = active;
            row["attached"] = attached;
            string label = row["label"]!.GetValue<string>();
            if (attached) label += selected ? " ［接続中・自動接続］" : " ［接続中・未登録］";
            else if (selected || active) {
                label += !running ? " ［登録済み・VM 起動待ち］"
                       : present ? " ［未接続・再接続できます］"
                       : active ? " ［自動接続・接続待ち］"
                       : " ［登録済み・再接続が必要］";
            }
            row["label"] = label;
        }

        if (!IsLegacyAppleRegistered(config)) {
            string callerSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            foreach (var apple in GetAppleUsbRows(vmDirectory, ReadAppleProfiles(config), blocked, callerSid))
                rows.Add(apple);
        }

        return (rows, blocked);
    }

    private static string AttachGenericUsb(string engineRoot, string vmDirectory, string usbId, CancellationToken cancellation)
    {
        var config = ReadVmConfig(vmDirectory);
        var saved = ReadSavedUsbSelections(config);
        var existing = saved.FirstOrDefault(x => x.Id == usbId);
        var (runtime, running, qmpPort) = ReadLiveRuntime(vmDirectory);
        if (!running || runtime is null) throw new InvalidOperationException("USB を接続するには VM を起動してください。");

        var states = ReadUsbObjectStatesAsync(runtime, qmpPort, cancellation).GetAwaiter().GetResult();
        bool active = states.TryGetValue(usbId, out bool currentlyAttached);
        if (existing is not null && active && currentlyAttached)
            return "この USB は VM に接続され、自動接続にも登録済みです。";

        var (rows, blocked) = BuildUsbRowsAsync(engineRoot, vmDirectory, cancellation).GetAwaiter().GetResult();
        var device = rows.FirstOrDefault(r =>
            r["id"]?.GetValue<string>() == usbId &&
            ((r["present"]?.GetValue<bool>() ?? false) || (r["attached"]?.GetValue<bool>() ?? false)));
        if (device is null) throw new InvalidOperationException("選択した USB がありません。一覧を更新してください。");

        string vendor = device["vendor"]!.GetValue<string>();
        string product = device["product"]!.GetValue<string>();
        if (blocked.Contains($"{vendor}:{product}"))
            throw new InvalidOperationException("Windows の入力機器または VM の保存先ディスクは転送できません。USB の登録を解除してください。");

        string rawLabel = Regex.Replace(device["label"]!.GetValue<string>(), @" ［[^］]+］$", "");
        var entry = existing ?? new SavedUsbDevice(usbId, device["bus"]!.GetValue<int>(), device["port"]!.GetValue<string>(), vendor, product, rawLabel);

        bool added = false;
        if (!active || !currentlyAttached) {
            if (active) RemoveUsbObject(runtime, qmpPort, usbId, cancellation);
            QmpClient.ExecuteAsync(qmpPort, "device_add", BuildGenericQmpDeviceArg(entry), cancellation).GetAwaiter().GetResult().Dispose();
            added = true;
        }

        if (existing is null) {
            var nextList = new List<SavedUsbDevice>(saved) { entry };
            SetUsbSelectionsOnConfig(config, nextList);
            try {
                AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, vmDirectory, vmDirectory);
            } catch (Exception saveFailure) {
                if (added) {
                    try { RemoveUsbObject(runtime, qmpPort, usbId, cancellation); }
                    catch (Exception detachEx) {
                        throw new InvalidOperationException($"自動接続の保存に失敗し、USB の接続解除も完了していません。一覧を更新して解除してください。保存: {saveFailure.Message} / 解除: {detachEx.Message}");
                    }
                }
                throw;
            }
        }

        try {
            if (WaitUsbAttachment(runtime, qmpPort, usbId, cancellation))
                return "USB を VM に接続し、自動接続に登録しました。同じ USB ポートへの再接続と VM の次回起動にも適用します。";
            return "自動接続の登録は保存済みですが、ゲストへの接続は未完了です。転送方式とドライバーを確認し、一覧の「再接続」を試してください。";
        } catch (Exception ex) {
            return $"自動接続の登録は保存済みですが、USB の接続状態を確認できません。一覧を更新してください: {ex.Message}";
        }
    }

    private static string DetachGenericUsb(string vmDirectory, string usbId, CancellationToken cancellation)
    {
        var config = ReadVmConfig(vmDirectory);
        var saved = ReadSavedUsbSelections(config);
        var existing = saved.FirstOrDefault(x => x.Id == usbId);
        var (runtime, running, qmpPort) = ReadLiveRuntime(vmDirectory);
        var states = running && runtime is not null
            ? ReadUsbObjectStatesAsync(runtime, qmpPort, cancellation).GetAwaiter().GetResult()
            : new Dictionary<string, bool>(StringComparer.Ordinal);
        bool active = states.ContainsKey(usbId);
        if (existing is null && !active) throw new InvalidOperationException("この USB は登録されていません。");

        if (active && runtime is not null) RemoveUsbObject(runtime, qmpPort, usbId, cancellation);
        if (existing is not null) {
            SetUsbSelectionsOnConfig(config, saved.Where(x => x.Id != usbId).ToList());
            try {
                AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, vmDirectory, vmDirectory);
            } catch (Exception saveFailure) {
                if (active && IsRuntimeAlive(runtime)) {
                    try {
                        QmpClient.ExecuteAsync(qmpPort, "device_add", BuildGenericQmpDeviceArg(existing), cancellation).GetAwaiter().GetResult().Dispose();
                    } catch (Exception restoreEx) {
                        throw new InvalidOperationException($"USB は切断しましたが、登録解除を保存できず、自動接続も復元できません。登録は残っています。一覧を更新して再試行してください。保存: {saveFailure.Message} / 復元: {restoreEx.Message}");
                    }
                    throw new InvalidOperationException($"USB の登録解除を保存できないため、自動接続を復元しました: {saveFailure.Message}");
                }
                throw;
            }
        }
        return "USB の自動接続を解除しました。VM からの切断を確認しました。";
    }

    private static async Task<string> SetAppleUsbSelectionAsync(string engineRoot, string toolsRoot, string vmDirectory, string usbId, bool connect, CancellationToken cancellation)
    {
        if (!connect) return SetAppleUsbSelectionOffOrReset(vmDirectory, usbId, resetOnly: false);

        return await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => AttachAppleUsb(engineRoot, toolsRoot, vmDirectory, usbId, cancellation)), cancellation).ConfigureAwait(false);
    }

    private static string AttachAppleUsb(string engineRoot, string toolsRoot, string vmDirectory, string usbId, CancellationToken cancellation)
    {
        var config = ReadVmConfig(vmDirectory);
        if (IsLegacyAppleRegistered(config))
            throw new InvalidOperationException("旧 USB 一覧の iPhone 登録を解除してから、iPhone 専用接続を選んでください。");

        var (runtime, running, qmpPort) = ReadLiveRuntime(vmDirectory);
        string backend = runtime?["usb_backend"]?.GetValue<string>() ?? "";
        if (!running || runtime is null || backend != "winusb")
            throw new InvalidOperationException("iPhone 転送には WinUSB 方式で VM を起動してください。USB 設定を変更し、VM を完全終了して起動し直してください。");

        VerifyQemuHostInstanceSupport(qmpPort, cancellation);
        NativeStatusService.VerifyEnginePackage(engineRoot);

        string qemuPath = runtime["executable"]?.GetValue<string>() ?? "";
        string expectedQemu = NativeStatusService.ResolveEngineToolPath(engineRoot, "qemu", out string libusbRel);
        if (!string.Equals(Path.GetFullPath(qemuPath), Path.GetFullPath(expectedQemu), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("実行中 VM と検証済みエンジンが一致しません。");
        if (string.IsNullOrEmpty(libusbRel))
            throw new InvalidOperationException("検証済みエンジンに iPhone 用 USB ライブラリーがありません。");

        string libusbFull = NativeStatusService.JoinPackagePath(engineRoot, libusbRel);
        string driverPkgDir = Path.Combine(toolsRoot, "iphone-usb-driver");
        AssertOrdinaryPath(driverPkgDir, allowMissing: false);
        if (!File.Exists(Path.Combine(driverPkgDir, "manifest.json")))
            throw new InvalidOperationException("iPhone USB package directory is missing.");
        string pythonExe = Path.Combine(toolsRoot, "python", "python.exe");
        AssertOrdinaryPath(pythonExe, allowMissing: false);
        if (!File.Exists(pythonExe))
            throw new InvalidOperationException("同梱 Python がありません。Manager の完全なパッケージを再展開してください。");

        var profiles = ReadAppleProfiles(config);
        var profile = profiles.FirstOrDefault(p => p.Id == usbId);
        if (profile is null) {
            var candidates = NativePnpUsb.GetPresentAppleCandidates().Where(id => ComputeAppleUsbId(id) == usbId).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("選択した iPhone が見つからないか、一意ではありません。");
            string instance = candidates[0];
            string product = PidRegex().Match(instance).Groups[1].Value;
            var blocked = NativePnpUsb.GetProtectedUsbIds(engineRoot, vmDirectory);
            if (blocked.Contains($"05ac:{product}"))
                throw new InvalidOperationException("Windows の入力機器または VM の保存先ディスクは転送できません。USB の登録を解除してください。");

            var (ecid, udid) = ProbeAppleIdentity(pythonExe, libusbFull, instance);
            profile = profiles.FirstOrDefault(p => p.Ecid == ecid)
                ?? new AppleProfile(usbId, instance, ecid, true, Guid.NewGuid().ToString("N"), udid);
        }

        string callerSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var status = ReadAppleStatus(profile, callerSid);
        if (status is not null) {
            bool active = status["active"]?.GetValue<bool>() ?? false;
            string st = status["state"]?.GetValue<string>() ?? "";
            int qPid = status["qemu_pid"]?.GetValue<int>() ?? 0;
            long qTicks = status["qemu_start_ticks"]?.GetValue<long>() ?? 0;
            int rtPid = runtime["pid"]?.GetValue<int>() ?? 0;
            long rtTicks = runtime["start_ticks"]?.GetValue<long>() ?? 0;
            if (active && st is "waiting" or "connected" or "replug-waiting" && qPid == rtPid && qTicks == rtTicks &&
                ProcessIdentity.IsAlive(status["pid"]?.GetValue<int>() ?? 0, status["start_ticks"]?.GetValue<long>() ?? 0, status["executable"]?.GetValue<string>() ?? "")) {
                profile = profile with { Enabled = true };
                SaveAppleProfile(config, profile, vmDirectory);
                return "iPhone はすでに VM へ自動接続中です。";
            }
            if (st != "restored")
                throw new InvalidOperationException("前回の iPhone の復元が未完了です。先に「切断」または「復元リセット」で復旧してください。");
        }

        var otherInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in profiles) {
            if (other.Id == profile.Id) continue;
            otherInstances.Add(other.InitialInstance);
            var otherStatus = ReadAppleStatus(other, callerSid);
            string? inst = otherStatus?["instance"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(inst)) otherInstances.Add(inst);
        }

        var previousProfile = profile;
        var updatedProfile = profile with { Enabled = true, SessionId = Guid.NewGuid().ToString("N") };
        SaveAppleProfile(config, updatedProfile, vmDirectory);

        var profileJson = ToProfileJson(updatedProfile);
        var snapshot = new JsonObject {
            ["schema"] = 1,
            ["profile"] = profileJson,
            ["runtime"] = runtime.DeepClone(),
            ["engine_root"] = engineRoot,
            ["vm_root"] = vmDirectory,
            ["tools_root"] = toolsRoot,
            ["caller_sid"] = callerSid,
            ["python"] = pythonExe,
            ["other_instances"] = new JsonArray(otherInstances.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
        };
        string snapshotPath = Path.Combine(vmDirectory, "state", $"iphone-{updatedProfile.SessionId}.json");
        AtomicFileStore.WriteJsonAtomic(snapshotPath, snapshot, vmDirectory, vmDirectory);
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(snapshotPath)));

        try {
            StartElevatedIphoneSession(["-InputPath", snapshotPath, "-InputHash", hash, "-SessionId", updatedProfile.SessionId], waitForExitMs: 0);
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            try { File.Delete(snapshotPath); } catch { }
            if (profiles.Any(p => p.Id == previousProfile.Id))
                SaveAppleProfile(config, previousProfile, vmDirectory);
            throw new InvalidOperationException("管理者権限の確認（UAC）がキャンセルされたため、iPhone の接続を開始しませんでした。");
        }

        return "iPhone の接続を登録しました。ドライバー切替の確認画面を許可してください。再接続待ちの場合は抜き差ししてください。";
    }

    private static string SetAppleUsbSelectionOffOrReset(string vmDirectory, string usbId, bool resetOnly)
    {
        var config = ReadVmConfig(vmDirectory);
        var profile = ReadAppleProfiles(config).FirstOrDefault(p => p.Id == usbId)
            ?? throw new InvalidOperationException("この iPhone は登録されていません。");

        string stopPath = Path.Combine(vmDirectory, "state", $"iphone-{profile.SessionId}.stop");
        AtomicFileStore.WriteJsonAtomic(stopPath, new JsonObject {
            ["session_id"] = profile.SessionId,
            ["stop"] = true
        }, vmDirectory, vmDirectory);

        string callerSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var status = ReadAppleStatus(profile, callerSid);
        bool resident = status is not null &&
            ProcessIdentity.IsAlive(status["pid"]?.GetValue<int>() ?? 0, status["start_ticks"]?.GetValue<long>() ?? 0, status["executable"]?.GetValue<string>() ?? "");

        string sessionRoot = GetAppleSessionRoot(profile.SessionId);
        if (!resident && Directory.Exists(sessionRoot)) {
            string modeFlag = resetOnly ? "-ResetSession" : "-StopSession";
            try {
                // -ResetSession completes synchronously and deletes or updates status.json immediately.
                StartElevatedIphoneSession([modeFlag, "-SessionId", profile.SessionId, "-CallerSid", callerSid], waitForExitMs: resetOnly ? 12000 : 0);
            } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
                throw new InvalidOperationException("管理者権限の確認（UAC）がキャンセルされました。");
            }
        }

        SaveAppleProfile(config, profile with { Enabled = false }, vmDirectory);
        if (!resetOnly)
            return "iPhone の自動接続を解除し、元ドライバーへの復元を要求しました。未接続のモードがある場合は再接続後に復元します。";

        var afterStatus = ReadAppleStatus(profile, callerSid);
        string? remainingErr = afterStatus?["error"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(remainingErr))
            throw new InvalidOperationException(remainingErr);
        return "iPhone の古い復元セッション状態をリセットしました。再接続して「接続」をお試しください。";
    }

    private static Process? StartElevatedIphoneSession(string[] arguments, int waitForExitMs)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "scripts", "iphone-usb-session.ps1");
        if (!File.Exists(script))
            script = Path.Combine(AppContext.BaseDirectory, "backend", "iphone-usb-session.ps1");
        var allArgs = new[] { "-NoProfile", "-File", script }.Concat(arguments);
        var info = new ProcessStartInfo(FindPwshExecutable()) {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(' ', allArgs.Select(QuoteWindowsArgument))
        };
        var process = Process.Start(info);
        if (process is not null && waitForExitMs > 0) {
            using (process) {
                process.WaitForExit(waitForExitMs);
            }
            return null;
        }
        process?.Dispose();
        return null;
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (!value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value) {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(c);
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        return sb.Append('"').ToString();
    }

    private static (string Ecid, string? Udid) ProbeAppleIdentity(string pythonExe, string libusbPath, string instance)
    {
        string script = NativeStatusService.ResolveScriptAssetPath("iphone-usb-identity.py");
        var info = new ProcessStartInfo(pythonExe) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-B");
        info.ArgumentList.Add("-X");
        info.ArgumentList.Add("utf8");
        info.ArgumentList.Add(script);
        using var child = Process.Start(info) ?? throw new IOException("iPhone の本人確認プロセスを起動できませんでした。");
        var outTask = child.StandardOutput.ReadToEndAsync();
        var errTask = child.StandardError.ReadToEndAsync();
        string payload = JsonSerializer.Serialize(new { instance_id = instance, native_library = libusbPath });
        child.StandardInput.WriteLine(payload);
        child.StandardInput.Close();
        if (!child.WaitForExit(30000)) {
            try { child.Kill(true); } catch { }
            throw new TimeoutException("iPhone の本人確認がタイムアウトしました。");
        }
        string output = outTask.GetAwaiter().GetResult();
        string error = errTask.GetAwaiter().GetResult();
        if (output.Length > 4096 || error.Length > 8192 || child.ExitCode != 0)
            throw new InvalidOperationException("元ドライバーで iPhone の ECID を確認できません。ロック解除・信頼設定、または復旧モード用ドライバーを確認してください。");
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 ||
            !string.Equals(root.GetProperty("instance_id").GetString(), instance, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("iPhone の本人確認結果が無効です。");
        string ecid = root.GetProperty("ecid").GetString() ?? "";
        if (!LowerHex16Regex().IsMatch(ecid) || ecid == new string('0', 16))
            throw new InvalidDataException("iPhone の本人確認結果が無効です。");
        string? udid = root.TryGetProperty("udid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        return (ecid, udid);
    }

    private static List<JsonObject> GetAppleUsbRows(string vmDirectory, List<AppleProfile> profiles, HashSet<string> blocked, string callerSid)
    {
        var rows = new List<JsonObject>();
        var statuses = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        foreach (var profile in profiles) statuses[profile.Id] = ReadAppleStatus(profile, callerSid);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string instance in NativePnpUsb.GetPresentAppleCandidates()) {
            string product = PidRegex().Match(instance).Groups[1].Value.ToLowerInvariant();
            if (blocked.Contains($"05ac:{product}")) continue;

            var matching = profiles.Where(p =>
                string.Equals(instance, p.InitialInstance, StringComparison.OrdinalIgnoreCase) ||
                (statuses.TryGetValue(p.Id, out var st) && st is not null &&
                 string.Equals(instance, st["instance"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matching.Length > 1) throw new InvalidOperationException("Apple USB の登録と接続状態が重複しています。");

            var profile = matching.Length == 1 ? matching[0] : null;
            bool owned = profile is not null && seen.Add(profile.Id);
            var status = owned ? statuses[profile!.Id] : null;
            string id = owned ? profile!.Id : ComputeAppleUsbId(instance);
            if (!emitted.Add(id)) continue;

            string stateStr = status?["state"]?.GetValue<string>() ?? "";
            bool attached = owned && stateStr == "connected";
            string initialProduct = owned
                ? PidRegex().Match(profile!.InitialInstance).Groups[1].Value.ToLowerInvariant()
                : product;
            string kind = initialProduct switch { "12ab" => "iPad", "12a8" => "iPhone", _ => "iPhone/iPad" };
            string label = $"{kind} ({product})";
            if (owned && status is not null) {
                string badge = stateStr switch {
                    "connected" => "接続中",
                    "replug-waiting" => "抜き差し待ち",
                    "restored" => "復元済み",
                    "restore-pending" => "元ドライバーの復元待ち",
                    "qmp-wait-exit" => "VM 完全終了後に復元",
                    "error" => "エラー・復元の確認が必要",
                    _ => "接続・復元処理中"
                };
                label += $" ［{badge}］";
            }

            var row = new JsonObject {
                ["id"] = id,
                ["label"] = label,
                ["present"] = true,
                ["selected"] = owned && profile!.Enabled,
                ["active"] = owned && (status?["active"]?.GetValue<bool>() ?? false),
                ["attached"] = attached,
                ["vendor"] = "05ac",
                ["product"] = product,
                ["bus"] = 0,
                ["address"] = 0,
                ["port"] = ""
            };
            if (owned) row["error"] = status?["error"]?.GetValue<string>() ?? "";
            rows.Add(row);
        }

        foreach (var profile in profiles) {
            if (seen.Contains(profile.Id)) continue;
            var status = statuses[profile.Id];
            string stateStr = status?["state"]?.GetValue<string>() ?? "";
            string kind = profile.InitialInstance.Contains("PID_12AB", StringComparison.OrdinalIgnoreCase) ? "iPad" : "iPhone/iPad";
            string suffix = stateStr == "error" ? " ［エラー・復元の確認が必要］" : " ［再接続・復元待ち］";
            string prod = PidRegex().Match(profile.InitialInstance).Groups[1].Value.ToLowerInvariant();
            rows.Add(new JsonObject {
                ["id"] = profile.Id,
                ["label"] = kind + suffix,
                ["present"] = false,
                ["selected"] = profile.Enabled,
                ["error"] = status?["error"]?.GetValue<string>() ?? "",
                ["active"] = status?["active"]?.GetValue<bool>() ?? false,
                ["attached"] = false,
                ["vendor"] = "05ac",
                ["product"] = prod,
                ["bus"] = 0,
                ["address"] = 0,
                ["port"] = ""
            });
        }

        return rows;
    }

    private static JsonObject? ReadAppleStatus(AppleProfile profile, string callerSid)
    {
        string root = GetAppleSessionRoot(profile.SessionId);
        if (!Directory.Exists(root)) return null;
        AssertSessionRootAcl(root, callerSid);

        string file = Path.Combine(root, "status.json");
        if (!File.Exists(file)) {
            return new JsonObject {
                ["schema"] = 1,
                ["session_id"] = profile.SessionId,
                ["id"] = profile.Id,
                ["ecid"] = profile.Ecid,
                ["state"] = "restore-pending",
                ["active"] = false,
                ["instance"] = ""
            };
        }

        AssertOrdinaryPath(file, allowMissing: false);
        var status = AtomicFileStore.ReadJsonNode(file, 65536).AsObject();
        string st = status["state"]?.GetValue<string>() ?? "";
        if (status["schema"]?.GetValue<int>() != 1 ||
            status["session_id"]?.GetValue<string>() != profile.SessionId ||
            status["id"]?.GetValue<string>() != profile.Id ||
            status["ecid"]?.GetValue<string>() != profile.Ecid ||
            status["active"] is not JsonValue actVal || !actVal.TryGetValue<bool>(out bool active) ||
            st is not ("waiting" or "connected" or "replug-waiting" or "restoring" or "restore-pending" or "restored" or "error" or "qmp-wait-exit"))
            throw new InvalidDataException("Invalid protected iPhone status.");

        if (active) {
            int pid = status["pid"]?.GetValue<int>() ?? 0;
            long ticks = status["start_ticks"]?.GetValue<long>() ?? 0;
            string exe = status["executable"]?.GetValue<string>() ?? "";
            if (!ProcessIdentity.IsAlive(pid, ticks, exe)) {
                status["active"] = false;
                status["state"] = "restore-pending";
            }
        }
        return status;
    }

    private static string GetAppleSessionRoot(string sessionId)
    {
        if (!LowerHex32Regex().IsMatch(sessionId)) throw new InvalidDataException("Invalid iPhone session ID.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), $"ReimsIphoneUsb-{sessionId}");
    }

    private static void AssertOrdinaryPath(string path, bool allowMissing)
    {
        string full = Path.GetFullPath(path);
        var stack = new Stack<string>();
        string? cur = full;
        while (!string.IsNullOrEmpty(cur)) {
            stack.Push(cur);
            cur = Path.GetDirectoryName(cur);
        }
        while (stack.Count > 0) {
            string part = stack.Pop();
            if (File.Exists(part) || Directory.Exists(part)) {
                var attrs = File.GetAttributes(part);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Untrusted reparse point in path: {part}");
            } else if (!allowMissing && string.Equals(part, full, StringComparison.OrdinalIgnoreCase)) {
                throw new FileNotFoundException("Required file not found.", full);
            }
        }
    }

    private static void AssertSessionRootAcl(string root, string callerSid)
    {
        AssertOrdinaryPath(root, allowMissing: false);
        var acl = new DirectoryInfo(root).GetAccessControl();
        string owner = acl.GetOwner(typeof(SecurityIdentifier))?.Value ?? "";
        if ((owner != "S-1-5-32-544" && owner != "S-1-5-18") || !acl.AreAccessRulesProtected)
            throw new InvalidDataException("Untrusted iPhone session owner/ACL.");
        foreach (AuthorizationRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
            if (rule is not FileSystemAccessRule fsRule ||
                fsRule.IsInherited ||
                fsRule.AccessControlType != AccessControlType.Allow)
                throw new InvalidDataException("Untrusted iPhone session permissions.");
            string sid = fsRule.IdentityReference.Value;
            if (sid != "S-1-5-32-544" && sid != "S-1-5-18" && sid != callerSid)
                throw new InvalidDataException("Untrusted iPhone session permissions.");
            if (sid == callerSid && ((int)fsRule.FileSystemRights & ForbiddenCallerRights) != 0)
                throw new InvalidDataException("Untrusted iPhone session permissions.");
        }
    }

    private static async Task<string> QueryStoppedUsbInventoryAsync(string engineRoot, JsonObject config, CancellationToken cancellation)
    {
        string backend = config["usb_backend"]?.GetValue<string>() ?? "winusb";
        if (backend is not ("winusb" or "usbdk"))
            throw new InvalidDataException("Invalid saved USB backend; select WinUSB or UsbDk in VM settings.");
        string qemuExe = NativeStatusService.ResolveEngineToolPath(engineRoot, "qemu", out _);
        var info = new ProcessStartInfo(qemuExe) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
            WorkingDirectory = Path.GetDirectoryName(qemuExe)!
        };
        foreach (string arg in new[] { "-machine", "none", "-nodefaults", "-display", "none", "-S", "-monitor", "none", "-qmp", "stdio" })
            info.ArgumentList.Add(arg);
        if (backend == "usbdk") info.Environment["REIMS_USB_BACKEND"] = "usbdk";
        else info.Environment.Remove("REIMS_USB_BACKEND");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var process = Process.Start(info) ?? throw new IOException("Could not start the USB inventory probe.");
        using var kill = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

        var errTask = process.StandardError.ReadToEndAsync(token);
        string? greeting = await ReadBoundedQmpLineAsync(process.StandardOutput, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(greeting)) {
            string err = await errTask.ConfigureAwait(false);
            throw new IOException($"USB inventory probe closed QMP before completing. {err}".Trim());
        }
        using (var greetDoc = JsonDocument.Parse(greeting)) {
            if (!greetDoc.RootElement.TryGetProperty("QMP", out _))
                throw new InvalidDataException("USB inventory probe did not provide a QMP greeting.");
        }

        await process.StandardInput.WriteLineAsync("{\"execute\":\"qmp_capabilities\",\"id\":\"usb-caps\"}").ConfigureAwait(false);
        await WaitQmpReplyAsync(process.StandardOutput, "usb-caps", token).ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync("{\"execute\":\"human-monitor-command\",\"arguments\":{\"command-line\":\"info usbhost\"},\"id\":\"usb-inventory\"}").ConfigureAwait(false);
        string text = await WaitQmpReplyAsync(process.StandardOutput, "usb-inventory", token).ConfigureAwait(false);
        try {
            await process.StandardInput.WriteLineAsync("{\"execute\":\"quit\",\"id\":\"usb-quit\"}").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        } catch { }

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"USB inventory probe failed ({process.ExitCode}); check native DLLs and the selected USB backend.");
        return text;
    }

    private static async Task<string?> ReadBoundedQmpLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (line is not null && line.Length > 262144)
            throw new InvalidDataException("USB inventory QMP line exceeds its size limit.");
        return line;
    }

    private static async Task<string> WaitQmpReplyAsync(StreamReader reader, string requestId, CancellationToken token)
    {
        while (true) {
            string? line = await ReadBoundedQmpLineAsync(reader, token).ConfigureAwait(false);
            if (line is null) throw new EndOfStreamException("USB inventory probe closed QMP before completing.");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String || idProp.GetString() != requestId)
                continue;
            if (root.TryGetProperty("error", out var err))
                throw new IOException($"USB inventory QMP failed: {err.GetRawText()}");
            if (!root.TryGetProperty("return", out var ret))
                throw new InvalidDataException("USB inventory QMP reply is missing its result.");
            return ret.ValueKind == JsonValueKind.String ? ret.GetString() ?? "" : "";
        }
    }

    private static void VerifyQemuHostInstanceSupport(int qmpPort, CancellationToken cancellation)
    {
        try {
            using var doc = QmpClient.ExecuteAsync(qmpPort, "device-list-properties", new { typename = "usb-host" }, cancellation).GetAwaiter().GetResult();
            bool hasHostInstance = doc.RootElement.ValueKind == JsonValueKind.Array &&
                doc.RootElement.EnumerateArray().Any(x => x.TryGetProperty("name", out var n) && n.GetString() == "host-instance-id");
            if (!hasHostInstance)
                throw new InvalidOperationException("このエンジンは iPhone の個別転送に未対応です。エンジンを更新して VM を起動し直してください。");
        } catch (IOException) {
            throw new InvalidOperationException("このエンジンでは iPhone の個別転送を確認できません。エンジンを更新し、VM を完全終了して起動し直してください。");
        }
    }

    private static async Task<Dictionary<string, bool>> ReadUsbObjectStatesAsync(JsonObject runtime, int qmpPort, CancellationToken cancellation)
    {
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        var objects = await QmpClient.QomListAsync(qmpPort, "/machine/peripheral", cancellation).ConfigureAwait(false);
        foreach (var (name, _) in objects) {
            if (!LiveHostUsbIdRegex().IsMatch(name)) continue;
            try {
                bool attached = await QmpClient.QomGetBoolAsync(qmpPort, $"/machine/peripheral/{name}", "attached", cancellation).ConfigureAwait(false);
                states[name] = attached;
            } catch {
                if (!IsRuntimeAlive(runtime)) return new Dictionary<string, bool>(StringComparer.Ordinal);
                var current = await QmpClient.QomListAsync(qmpPort, "/machine/peripheral", cancellation).ConfigureAwait(false);
                if (current.Any(x => x.Name == name)) throw;
            }
        }
        return states;
    }

    private static void RemoveUsbObject(JsonObject runtime, int qmpPort, string id, CancellationToken cancellation)
    {
        QmpClient.ExecuteAsync(qmpPort, "device_del", new { id }, cancellation).GetAwaiter().GetResult().Dispose();
        for (int attempt = 0; attempt < 20; attempt++) {
            if (!IsRuntimeAlive(runtime)) return;
            var objects = QmpClient.QomListAsync(qmpPort, "/machine/peripheral", cancellation).GetAwaiter().GetResult();
            if (objects.All(o => o.Name != id)) return;
            if (attempt < 19) Thread.Sleep(100);
        }
        throw new InvalidOperationException("USB の切断完了を確認できません。登録は保持しています。一覧を更新してから再試行してください。");
    }

    private static bool WaitUsbAttachment(JsonObject runtime, int qmpPort, string id, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 12; attempt++) {
            if (!IsRuntimeAlive(runtime)) return false;
            if (QmpClient.QomGetBoolAsync(qmpPort, $"/machine/peripheral/{id}", "attached", cancellation).GetAwaiter().GetResult()) return true;
            if (attempt < 11) Thread.Sleep(250);
        }
        return false;
    }

    private static Dictionary<string, object?> BuildGenericQmpDeviceArg(SavedUsbDevice d) => new() {
        ["driver"] = "usb-host",
        ["id"] = d.Id,
        ["bus"] = "xhci.0",
        ["hostbus"] = d.Bus,
        ["hostport"] = d.Port,
        ["vendorid"] = Convert.ToInt32(d.Vendor, 16),
        ["productid"] = Convert.ToInt32(d.Product, 16),
        ["guest-reset"] = false
    };

    private static JsonObject ReadVmConfig(string vmDirectory)
    {
        string path = Path.Combine(vmDirectory, "state", "config.json");
        var doc = AtomicFileStore.ReadJsonNode(path).AsObject();
        if (doc["schema"]?.GetValue<int>() != 1) throw new InvalidDataException("Unknown VM configuration schema.");
        return doc;
    }

    private static (JsonObject? Runtime, bool Alive, int QmpPort) ReadLiveRuntime(string vmDirectory)
    {
        string path = Path.Combine(vmDirectory, "state", "runtime.json");
        if (!File.Exists(path)) return (null, false, 0);
        var rt = AtomicFileStore.ReadJsonNode(path).AsObject();
        bool alive = IsRuntimeAlive(rt);
        return (rt, alive, rt["qmp_port"]?.GetValue<int>() ?? 0);
    }

    private static bool IsRuntimeAlive(JsonObject? runtime)
    {
        if (runtime is null) return false;
        int pid = runtime["pid"]?.GetValue<int>() ?? 0;
        long ticks = runtime["start_ticks"]?.GetValue<long>() ?? 0;
        string exe = runtime["executable"]?.GetValue<string>() ?? "";
        return ProcessIdentity.IsAlive(pid, ticks, exe);
    }

    private static List<SavedUsbDevice> ReadSavedUsbSelections(JsonObject config)
    {
        var list = new List<SavedUsbDevice>();
        if (config["usb_devices"] is not JsonArray arr) return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in arr) {
            var obj = node?.AsObject() ?? throw new InvalidDataException("Invalid saved USB selection.");
            int bus = obj["bus"]?.GetValue<int>() ?? 0;
            string port = obj["port"]?.GetValue<string>() ?? "";
            string vendor = obj["vendor"]?.GetValue<string>() ?? "";
            string product = obj["product"]?.GetValue<string>() ?? "";
            string id = obj["id"]?.GetValue<string>() ?? "";
            string label = obj["label"]?.GetValue<string>() ?? "";
            if (bus is < 1 or > 65535 || !UsbPortRegex().IsMatch(port) ||
                !LowerHex4Regex().IsMatch(vendor) || vendor == "0000" ||
                !LowerHex4Regex().IsMatch(product) || product == "0000")
                throw new InvalidDataException("Invalid saved USB selection.");
            string expected = $"hostusb-{bus}-{port.Replace('.', '_')}-{vendor}{product}";
            if (id != expected || !seen.Add(expected))
                throw new InvalidDataException("Invalid or duplicate USB selection ID.");
            list.Add(new SavedUsbDevice(id, bus, port, vendor, product, label));
        }
        return list;
    }

    private static void SetUsbSelectionsOnConfig(JsonObject config, List<SavedUsbDevice> list)
    {
        var arr = new JsonArray();
        foreach (var d in list) {
            arr.Add(new JsonObject {
                ["id"] = d.Id,
                ["bus"] = d.Bus,
                ["port"] = d.Port,
                ["vendor"] = d.Vendor,
                ["product"] = d.Product,
                ["label"] = d.Label
            });
        }
        config["usb_devices"] = arr;
    }

    private static bool IsLegacyAppleRegistered(JsonObject config) =>
        config["usb_devices"] is JsonArray arr && arr.Any(n => {
            var o = n?.AsObject();
            string v = o?["vendor"]?.GetValue<string>() ?? "";
            string p = o?["product"]?.GetValue<string>() ?? "";
            return string.Equals(v, "05ac", StringComparison.OrdinalIgnoreCase) && AppleUsbProductRegex().IsMatch(p);
        });

    private static List<AppleProfile> ReadAppleProfiles(JsonObject config)
    {
        var profiles = new List<AppleProfile>();
        if (config["iphone_usb"] is JsonObject primary) profiles.Add(ParseAppleProfile(primary));
        if (config["iphone_usb_devices"] is JsonArray extra) {
            if (extra.Count > 64) throw new InvalidDataException("Apple USB の登録一覧が無効です。");
            foreach (var node in extra) {
                if (node is not JsonObject obj) throw new InvalidDataException("Apple USB の登録一覧が無効です。");
                profiles.Add(ParseAppleProfile(obj));
            }
        }
        if (profiles.Count > 64) throw new InvalidDataException("Apple USB の登録数が上限を超えました。");
        if (profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count ||
            profiles.Select(p => p.Ecid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count ||
            profiles.Select(p => p.SessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count)
            throw new InvalidDataException("Apple USB の登録が重複しています。");
        return profiles;
    }

    private static AppleProfile ParseAppleProfile(JsonObject obj)
    {
        var allowed = new HashSet<string>(["id", "ecid", "udid", "initial_instance", "enabled", "session_id"], StringComparer.Ordinal);
        if (obj.Any(kv => !allowed.Contains(kv.Key))) throw new InvalidDataException("iPhone の保存済み設定が無効です。");
        string id = obj["id"]?.GetValue<string>() ?? throw new InvalidDataException("iPhone の保存済み設定が無効です。");
        string initial = obj["initial_instance"]?.GetValue<string>() ?? throw new InvalidDataException("iPhone の保存済み設定が無効です。");
        string ecid = obj["ecid"]?.GetValue<string>() ?? throw new InvalidDataException("iPhone の保存済み設定が無効です。");
        string session = obj["session_id"]?.GetValue<string>() ?? throw new InvalidDataException("iPhone の保存済み設定が無効です。");
        bool enabled = obj["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out bool b) ? b : throw new InvalidDataException("iPhone の保存済み設定が無効です。");
        string? udid = obj.TryGetPropertyValue("udid", out var u) ? u?.GetValue<string>() : null;
        if (!NativePnpUsb.IsAppleCandidateInstance(initial) || id != ComputeAppleUsbId(initial) ||
            !LowerHex16Regex().IsMatch(ecid) || ecid == new string('0', 16) ||
            !LowerHex32Regex().IsMatch(session) || (udid is not null && !UdidRegex().IsMatch(udid)))
            throw new InvalidDataException("iPhone の保存済み設定が無効です。");
        return new AppleProfile(id, initial, ecid, enabled, session, udid);
    }

    private static void SaveAppleProfile(JsonObject config, AppleProfile profile, string vmDirectory)
    {
        var all = ReadAppleProfiles(config);
        var primary = config["iphone_usb"] is JsonObject pObj ? ParseAppleProfile(pObj) : null;
        if ((primary is not null && primary.Id == profile.Id) || all.Count == 0) {
            config["iphone_usb"] = ToProfileJson(profile);
        } else {
            var additional = new JsonArray();
            foreach (var item in all.Where(x => x.Id != profile.Id && (primary is null || x.Id != primary.Id)))
                additional.Add(ToProfileJson(item));
            additional.Add(ToProfileJson(profile));
            config["iphone_usb_devices"] = additional;
        }
        AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, vmDirectory, vmDirectory);
    }

    private static JsonObject ToProfileJson(AppleProfile p)
    {
        var obj = new JsonObject {
            ["id"] = p.Id,
            ["ecid"] = p.Ecid,
            ["initial_instance"] = p.InitialInstance,
            ["enabled"] = p.Enabled,
            ["session_id"] = p.SessionId
        };
        if (p.Udid is not null) obj["udid"] = p.Udid;
        return obj;
    }

    private static string ComputeAppleUsbId(string instance) =>
        "hostiphone-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instance.ToUpperInvariant())));
}
