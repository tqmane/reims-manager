using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReimsManager.Services;

internal static partial class NativeVmControlService
{
    [GeneratedRegex(@"^[a-z_][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex GuestAccountRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex GuestHostRegex();

    [GeneratedRegex(@"^(1280x720|1920x1080|2560x1440|3840x2160)$", RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    internal static async Task<string> PauseAsync(string vmDirectory, CancellationToken cancellation = default)
    {
        var (_, alive, port) = ReadLiveRuntime(vmDirectory);
        if (!alive) throw new InvalidOperationException("The recorded VM is no longer running.");
        using var _ = await QmpClient.ExecuteAsync(port, "stop", null, cancellation).ConfigureAwait(false);
        return "";
    }

    internal static async Task<string> ResumeAsync(string vmDirectory, CancellationToken cancellation = default)
    {
        var (_, alive, port) = ReadLiveRuntime(vmDirectory);
        if (!alive) throw new InvalidOperationException("The recorded VM is no longer running.");
        string status = await QmpClient.QueryStatusAsync(port, cancellation).ConfigureAwait(false);
        string cmd = status == "suspended" ? "system_wakeup" : "cont";
        using var _ = await QmpClient.ExecuteAsync(port, cmd, null, cancellation).ConfigureAwait(false);
        return "";
    }

    internal static async Task<string> PowerOffAsync(string vmDirectory, CancellationToken cancellation = default)
    {
        await InvokeGuestControlAsync(vmDirectory, "-h", null, null, cancellation).ConfigureAwait(false);
        return await StopVmAsync(vmDirectory, exitQemu: false, force: false, cancellation).ConfigureAwait(false);
    }

    internal static Task<string> RestartAsync(string vmDirectory, CancellationToken cancellation = default) =>
        InvokeGuestControlAsync(vmDirectory, "-r", null, null, cancellation);

    internal static async Task<string> ExitAsync(string vmDirectory, CancellationToken cancellation = default)
    {
        var (_, alive, port) = ReadLiveRuntime(vmDirectory);
        if (alive) {
            string state = await QmpClient.QueryStatusAsync(port, cancellation).ConfigureAwait(false);
            if (state != "shutdown")
                await InvokeGuestControlAsync(vmDirectory, "-h", null, null, cancellation).ConfigureAwait(false);
        }
        return await StopVmAsync(vmDirectory, exitQemu: true, force: false, cancellation).ConfigureAwait(false);
    }

    internal static Task<string> ForceExitAsync(string vmDirectory, CancellationToken cancellation = default) =>
        StopVmAsync(vmDirectory, exitQemu: true, force: true, cancellation);

    internal static Task<string> SetHostCursorAsync(string vmDirectory, bool hidden, CancellationToken cancellation = default) =>
        Task.Run(() => {
            cancellation.ThrowIfCancellationRequested();
            string identity = AtomicFileStore.VmMutexIdentity(vmDirectory);
            return AtomicFileStore.WithNamedMutex(
                $"Local\\ReimsManager.HostCursor.{identity}",
                5000,
                "Another host cursor operation is in progress for this VM.",
                () => SetHostCursorCore(vmDirectory, hidden, cancellation));
        }, cancellation);

    internal static Task<string> SetResolutionAsync(string vmDirectory, string resolution, CancellationToken cancellation = default)
    {
        if (!ResolutionRegex().IsMatch(resolution))
            throw new InvalidDataException($"Invalid resolution: {resolution}");
        return Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => {
                AssertVmStopped(vmDirectory);
                var config = ReadVmConfig(vmDirectory);
                config["resolution"] = resolution;
                AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, vmDirectory, vmDirectory);
                return "解像度を保存しました。次回起動時に適用します。";
            }), cancellation);
    }

    internal static async Task<string> ApplyResolutionAsync(string vmDirectory, string resolution, CancellationToken cancellation = default)
    {
        if (!ResolutionRegex().IsMatch(resolution))
            throw new InvalidDataException($"Invalid resolution: {resolution}");
        string jsPath = NativeStatusService.ResolveScriptAssetPath("guest-display.js");
        string js = File.ReadAllText(jsPath, Encoding.UTF8);
        _ = ReadVmConfig(vmDirectory);

        string command = $"/usr/bin/osascript -l JavaScript - {resolution} <<'REIMS_DISPLAY'\n{js}\nREIMS_DISPLAY";
        string result = await InvokeGuestControlAsync(vmDirectory, "", command, null, cancellation).ConfigureAwait(false);
        using (var doc = JsonDocument.Parse(result)) {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("display_applied", out var applied) ||
                applied.ValueKind != JsonValueKind.String ||
                applied.GetString() != resolution)
                throw new InvalidOperationException("macOS の解像度変更を確認できませんでした。起動時の設定は変更していません。");
        }

        try {
            AtomicFileStore.WithNamedMutex(
                $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
                5000,
                "Another VM operation is in progress. Wait for it to finish, then retry.",
                () => {
                    var config = ReadVmConfig(vmDirectory);
                    config["resolution"] = resolution;
                    AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, vmDirectory, vmDirectory);
                });
        } catch (Exception ex) {
            throw new InvalidOperationException($"macOS の解像度は変更済みですが、次回起動用の設定を保存できませんでした。再試行してください。{ex.Message}");
        }
        return result;
    }

    internal static bool CanHandleSetHardwareNatively(string vmDirectory, string iso, bool preserveNetwork, string network, string physicalAdapter)
    {
        if (!string.IsNullOrEmpty(iso)) return false;
        if (preserveNetwork) return true;
        if (network != "nat" || !string.IsNullOrEmpty(physicalAdapter)) return false;
        string configPath = Path.Combine(vmDirectory, "state", "config.json");
        if (!File.Exists(configPath)) return false;
        var config = AtomicFileStore.ReadJsonNode(configPath).AsObject();
        string? fwd = config["network"]?["forwarder"]?.GetValue<string>();
        return fwd is not ("macnat" or "macnat-ipv4");
    }

    internal static Task<string> SetHardwareSimpleAsync(
        string vmDirectory, int cpus, int memoryMiB, string resolution, string audio, bool microphone, string usbBackend,
        bool withInstaller, bool preserveNetwork, string guestHost, CancellationToken cancellation = default)
    {
        if (cpus is < 1 or > 64) throw new InvalidDataException("Invalid CPU count.");
        if (memoryMiB is < 4096 or > 131072 || memoryMiB % 4 != 0) throw new InvalidDataException("メモリは 4 MiB 単位で指定してください。");
        if (!ResolutionRegex().IsMatch(resolution)) throw new InvalidDataException("Invalid resolution.");
        if (audio is not ("none" or "dsound")) throw new InvalidDataException("Invalid audio backend.");
        if (usbBackend is not ("winusb" or "usbdk")) throw new InvalidDataException("Invalid USB backend.");
        if (withInstaller) throw new InvalidOperationException("インストーラーを接続する場合は ISO を選択してください。");
        if (!preserveNetwork && !string.IsNullOrEmpty(guestHost) && !GuestHostRegex().IsMatch(guestHost))
            throw new InvalidDataException("Invalid guest address.");

        return Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => {
                AssertVmStopped(vmDirectory);
                var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (!GlobalMemoryStatusEx(ref mem)) throw new InvalidOperationException("Cannot query host memory.");
                if (cpus > Environment.ProcessorCount || (ulong)memoryMiB * 1024UL * 1024UL >= mem.TotalPhys)
                    throw new InvalidOperationException("ホストの CPU / メモリ容量以内で設定してください。");

                var config = ReadVmConfig(vmDirectory);
                string currentUsb = config["usb_backend"]?.GetValue<string>() ?? "winusb";
                if (usbBackend != currentUsb && config["usb_devices"] is JsonArray usbArr && usbArr.Count > 0)
                    throw new InvalidOperationException("USB の転送方式を変更する前に、登録済み USB の自動接続を解除してください。変更を保存して VM を起動した後、新しい方式の一覧から選び直してください。");

                config["iso"] = "";
                config["cpus"] = cpus;
                config["memory_mib"] = memoryMiB;
                config["resolution"] = resolution;
                config["audio"] = audio;
                config["microphone"] = microphone;
                config["usb_backend"] = usbBackend;
                config["connect_installer"] = false;
                if (!preserveNetwork) {
                    config["network"] = new JsonObject {
                        ["mode"] = "nat",
                        ["adapter"] = "",
                        ["guest_host"] = guestHost
                    };
                }

                AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, vmDirectory, vmDirectory);
                return "仮想マシン設定を保存しました。次回起動に適用します。";
            }), cancellation);
    }

    private static string SetHostCursorCore(string vmDirectory, bool hidden, CancellationToken cancellation)
    {
        var snapshot = ReadHostCursorSnapshot(vmDirectory);
        if (hidden && !snapshot.Valid)
            throw new InvalidDataException("Invalid typed host cursor policy. Use ShowHostCursor to back it up and reset it first.");

        var (runtime, live, port) = ReadLiveRuntime(vmDirectory);
        string state = live ? QmpClient.QueryStatusAsync(port, cancellation).GetAwaiter().GetResult() : "stopped";
        if (hidden && state != "running")
            throw new InvalidOperationException("HideHostCursor requires a running VM.");
        if (!hidden && state is not ("running" or "paused" or "shutdown" or "prelaunch" or "suspended" or "stopped"))
            throw new InvalidOperationException($"Cannot change host cursor policy while QEMU is {state}.");

        bool? previousLive = null;
        bool attemptedQmp = false;
        try {
            if (live) {
                try { previousLive = QmpClient.QomGetBoolAsync(port, "/machine/peripheral/gfx", "hide-host-cursor", cancellation).GetAwaiter().GetResult(); }
                catch (Exception ex) {
                    throw new InvalidOperationException($"This engine lacks usable live host cursor control. Update the engine and use an owned Reims native window. {ex.Message}");
                }
                attemptedQmp = true;
                QmpClient.QomSetBoolAsync(port, "/machine/peripheral/gfx", "hide-host-cursor", hidden, cancellation).GetAwaiter().GetResult();
                if (QmpClient.QomGetBoolAsync(port, "/machine/peripheral/gfx", "hide-host-cursor", cancellation).GetAwaiter().GetResult() != hidden)
                    throw new IOException("QEMU did not confirm the requested host cursor policy.");
            }

            AssertHostCursorSnapshotUnchanged(vmDirectory, snapshot);
            if (!snapshot.Valid && snapshot.Bytes is not null) {
                string backup = Path.Combine(vmDirectory, "state", $"host-cursor.recovery-{Guid.NewGuid():N}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                using var s = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                s.Write(snapshot.Bytes, 0, snapshot.Bytes.Length);
                s.Flush(true);
            }

            var obj = new JsonObject { ["schema"] = 1, ["host_cursor_hidden"] = hidden };
            AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "host-cursor.json"), obj, vmDirectory, vmDirectory);
        } catch (Exception ex) {
            if (attemptedQmp && previousLive is bool prev && IsRuntimeAlive(runtime)) {
                try {
                    QmpClient.QomSetBoolAsync(port, "/machine/peripheral/gfx", "hide-host-cursor", prev, cancellation).GetAwaiter().GetResult();
                } catch (Exception rbEx) {
                    throw new InvalidOperationException($"{ex.Message} Live host cursor rollback also failed: {rbEx.Message}");
                }
            }
            throw;
        }

        return hidden
            ? "ホストカーソルを隠す設定を QEMU と保存値に反映しました。UI キューで処理されます。"
            : "ホストカーソルの非表示設定を解除しました。";
    }

    private sealed record HostCursorSnapshot(bool Exists, byte[]? Bytes, string Hash, bool Valid);

    private static HostCursorSnapshot ReadHostCursorSnapshot(string vmDirectory)
    {
        string path = Path.Combine(vmDirectory, "state", "host-cursor.json");
        if (!File.Exists(path)) return new HostCursorSnapshot(false, null, "missing", true);
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Attributes.HasFlag(FileAttributes.Directory))
            throw new InvalidDataException("Host cursor policy must be an ordinary VM-owned file.");
        if (info.Length > 65536)
            throw new InvalidDataException("Host cursor policy is too large. Move state/host-cursor.json aside in this VM folder, then use ShowHostCursor.");
        byte[] bytes = File.ReadAllBytes(path);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        bool valid = false;
        try {
            var obj = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))?.AsObject();
            valid = obj is not null && obj.Count == 2 &&
                obj.TryGetPropertyValue("schema", out var sc) && sc is JsonValue scV && scV.TryGetValue<int>(out int sNum) && sNum == 1 &&
                obj.TryGetPropertyValue("host_cursor_hidden", out var hv) && hv is JsonValue hV && hV.TryGetValue<bool>(out _);
        } catch {
            valid = false;
        }
        return new HostCursorSnapshot(true, bytes, hash, valid);
    }

    private static void AssertHostCursorSnapshotUnchanged(string vmDirectory, HostCursorSnapshot expected)
    {
        string path = Path.Combine(vmDirectory, "state", "host-cursor.json");
        if (!expected.Exists) {
            if (File.Exists(path)) throw new IOException("Host cursor policy changed concurrently.");
            return;
        }
        if (!File.Exists(path)) throw new IOException("Host cursor policy was removed concurrently.");
        var current = ReadHostCursorSnapshot(vmDirectory);
        if (!string.Equals(current.Hash, expected.Hash, StringComparison.Ordinal))
            throw new IOException("Host cursor policy changed concurrently.");
    }

    private static async Task<string> StopVmAsync(string vmDirectory, bool exitQemu, bool force, CancellationToken cancellation)
    {
        var (runtime, alive, port) = ReadLiveRuntime(vmDirectory);
        if (!alive || runtime is null) {
            StopWifiForwarderIfPresent(vmDirectory);
            return "The recorded VM is stopped.";
        }

        int pid = runtime["pid"]?.GetValue<int>() ?? 0;
        long ticks = runtime["start_ticks"]?.GetValue<long>() ?? 0;

        try {
            bool quitRequested = runtime["diagnostic_only"]?.GetValue<bool>() ?? false;
            if (quitRequested) {
                using var _ = await QmpClient.ExecuteAsync(port, "quit", null, cancellation).ConfigureAwait(false);
            } else if (await QmpClient.QueryStatusAsync(port, cancellation).ConfigureAwait(false) != "shutdown") {
                using var _ = await QmpClient.ExecuteAsync(port, "system_powerdown", null, cancellation).ConfigureAwait(false);
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            do {
                if (!IsRuntimeAlive(runtime)) {
                    StopWifiForwarderIfPresent(vmDirectory);
                    return "QEMU stopped.";
                }
                if (!quitRequested && await QmpClient.QueryStatusAsync(port, cancellation).ConfigureAwait(false) == "shutdown") {
                    if (!exitQemu)
                        return $"Guest powered off; QEMU PID {pid} retained. Run start.ps1 to power on.";
                    using var _ = await QmpClient.ExecuteAsync(port, "quit", null, cancellation).ConfigureAwait(false);
                    quitRequested = true;
                }
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            } while (DateTime.UtcNow < deadline);

            throw new TimeoutException("Guest has not shut down. Use stop.ps1 -Force only to discard unsaved work and exit QEMU.");
        } catch {
            if (!force) throw;
        }

        try {
            using var _ = await QmpClient.ExecuteAsync(port, "quit", null, cancellation).ConfigureAwait(false);
        } catch { }
        await Task.Delay(500, cancellation).ConfigureAwait(false);

        if (IsRuntimeAlive(runtime)) {
            try {
                using var proc = Process.GetProcessById(pid);
                if (proc.StartTime.ToUniversalTime().Ticks == ticks) proc.Kill(entireProcessTree: true);
            } catch (ArgumentException) { }
        }
        StopWifiForwarderIfPresent(vmDirectory);
        return "Powered off the recorded VM.";
    }

    private static void StopWifiForwarderIfPresent(string vmDirectory)
    {
        string folder = Path.Combine(vmDirectory, "state", "wifi-forwarder");
        string rtPath = Path.Combine(folder, "runtime.json");
        if (!File.Exists(rtPath)) return;

        var rt = AtomicFileStore.ReadJsonNode(rtPath, 65536).AsObject();
        int pid = rt["pid"]?.GetValue<int>() ?? 0;
        long ticks = rt["start_ticks"]?.GetValue<long>() ?? 0;
        string exe = rt["executable"]?.GetValue<string>() ?? "";
        string session = rt["session"]?.GetValue<string>() ?? "";

        if (ProcessIdentity.IsAlive(pid, ticks, exe)) {
            AtomicFileStore.WriteTextAtomicCore(Path.Combine(folder, "stop"), "stop");
            using var proc = Process.GetProcessById(pid);
            if (!proc.WaitForExit(10000))
                throw new TimeoutException("Owned forwarder did not confirm cleanup; inspect before changing bindings. It was not forcibly killed.");
            string stPath = Path.Combine(folder, "status.json");
            var status = File.Exists(stPath) ? AtomicFileStore.ReadJsonNode(stPath, 65536).AsObject() : null;
            if (status is null || status["session"]?.GetValue<string>() != session || status["state"]?.GetValue<string>() != "stopped")
                throw new InvalidOperationException("Forwarder capture cleanup was not confirmed.");
            return;
        }

        string lastPath = Path.Combine(folder, "status.json");
        var last = File.Exists(lastPath) ? AtomicFileStore.ReadJsonNode(lastPath, 65536).AsObject() : null;
        bool cleanStopped = last is not null && last["session"]?.GetValue<string>() == session &&
            (last["state"]?.GetValue<string>() == "stopped" ||
             (last["state"]?.GetValue<string>() == "error" && (last["cleanup_confirmed"]?.GetValue<bool>() ?? false)));
        if (cleanStopped) return;

        // Previous-boot archive fallback delegates to wifi-macnat.ps1's recovery verifier.
        string script = NativeStatusService.ResolveScriptAssetPath("start.ps1");
        var psi = new ProcessStartInfo(NativeUsbService.FindPwshExecutable()) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"$env:REIMS_VM_ROOT_OVERRIDE='{vmDirectory.Replace("'", "''")}'; . '{script.Replace("'", "''")}' -FunctionsOnly; Stop-ReimsWifiForwarder");
        using var child = Process.Start(psi) ?? throw new IOException("Cannot start Wi-Fi forwarder cleanup.");
        child.WaitForExit(15000);
        if (child.ExitCode != 0)
            throw new IOException(child.StandardError.ReadToEnd().Trim());
    }

    internal static async Task<string> InvokeGuestControlAsync(string vmDirectory, string operation, string? command, string? inputJson, CancellationToken cancellation)
    {
        var (_, alive, port) = ReadLiveRuntime(vmDirectory);
        if (!alive) throw new InvalidOperationException("VM は停止しています。");
        string qmpState = await QmpClient.QueryStatusAsync(port, cancellation).ConfigureAwait(false);
        if (qmpState == "shutdown" && operation == "-h") return "macOS は電源オフ済みです。";
        if (qmpState != "running") throw new InvalidOperationException($"VM は {qmpState} です。電源操作の前に再開してください。");

        string guestPath = Path.Combine(vmDirectory, "state", "guest.json");
        string setupPath = Path.Combine(vmDirectory, "state", "setup.json");
        JsonObject? guest = File.Exists(guestPath) ? AtomicFileStore.ReadJsonNode(guestPath).AsObject() : null;
        JsonObject? setup = guest is null && File.Exists(setupPath) ? AtomicFileStore.ReadJsonNode(setupPath).AsObject() : null;
        string? account = guest?["user"]?.GetValue<string>() ?? setup?["account_name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(account))
            throw new InvalidOperationException("macOS で ReimsTools の setup.command を実行し、接続タブでペアリングしてください。");
        if (!GuestAccountRegex().IsMatch(account))
            throw new InvalidDataException("Invalid guest account name.");

        string keyPath = Path.Combine(vmDirectory, "state", "ssh", "id_ed25519");
        string knownPath = Path.Combine(vmDirectory, "state", "known_hosts");
        if (!File.Exists(keyPath) || !File.Exists(knownPath))
            throw new InvalidOperationException("SSH キーとホスト鍵が未設定です。");

        var config = ReadVmConfig(vmDirectory);
        var (host, sshPort) = await ResolveGuestEndpointAsync(config, cancellation).ConfigureAwait(false);

        string sshExe = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        var info = new ProcessStartInfo(sshExe) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = inputJson is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-F");
        info.ArgumentList.Add("NUL");
        info.ArgumentList.Add(inputJson is not null ? "-T" : "-n");
        foreach (string opt in new[] {
            "ConnectTimeout=5", "BatchMode=yes", "IdentitiesOnly=yes", "StrictHostKeyChecking=yes",
            $"UserKnownHostsFile=\"{knownPath}\"", "KexAlgorithms=curve25519-sha256"
        }) {
            info.ArgumentList.Add("-o");
            info.ArgumentList.Add(opt);
        }
        string? alias = guest?["host_key_alias"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(alias)) {
            info.ArgumentList.Add("-o");
            info.ArgumentList.Add($"HostKeyAlias={alias}");
        }
        info.ArgumentList.Add("-p");
        info.ArgumentList.Add(sshPort.ToString());
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(keyPath);
        info.ArgumentList.Add($"{account}@{host}");
        info.ArgumentList.Add(!string.IsNullOrEmpty(command) ? command : $"sudo -n /sbin/shutdown {operation} now");

        using var process = Process.Start(info) ?? throw new IOException("ssh.exe を起動できませんでした。");
        var outTask = process.StandardOutput.ReadToEndAsync(cancellation);
        var errTask = process.StandardError.ReadToEndAsync(cancellation);
        if (inputJson is not null) {
            await process.StandardInput.WriteAsync(inputJson.AsMemory(), cancellation).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);
        string combined = string.Join(" ", new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));

        if (!string.IsNullOrEmpty(command)) {
            if (process.ExitCode != 0) throw new IOException($"macOS の操作に失敗しました ({process.ExitCode}): {combined}");
            return stdout.Trim();
        }
        if (process.ExitCode != 0 && !string.Equals(operation, "-r", StringComparison.Ordinal))
            throw new IOException($"macOS の操作に失敗しました ({process.ExitCode}): {combined}");
        return "";
    }

    private static async Task<(string Host, int Port)> ResolveGuestEndpointAsync(JsonObject config, CancellationToken cancellation)
    {
        var net = config["network"]?.AsObject();
        string mode = net?["mode"]?.GetValue<string>() ?? "nat";
        if (mode == "nat") return ("127.0.0.1", config["ssh_port"]?.GetValue<int>() ?? 2222);

        string guestHost = net?["guest_host"]?.GetValue<string>() ?? "";
        if (!GuestHostRegex().IsMatch(guestHost))
            throw new InvalidOperationException("ブリッジの macOS IP アドレスを設定してください。");

        string? forwarder = net?["forwarder"]?.GetValue<string>();
        string? tapStr = net?["tap_id"]?.GetValue<string>();
        if (mode == "bridge" && !IPAddress.TryParse(guestHost, out _) &&
            forwarder is "macnat" or "macnat-ipv4" &&
            Guid.TryParse(tapStr, out var tapId) && tapId != Guid.Empty) {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .SingleOrDefault(ni => Guid.TryParse(ni.Id, out var id) && id == tapId)
                ?? throw new InvalidOperationException("設定された TAP インターフェースを一意に確認できません。");
            int scopeIdx = adapter.GetIPProperties().GetIPv6Properties()?.Index ?? 0;
            if (scopeIdx <= 0)
                throw new InvalidOperationException("設定された TAP の IPv6 インターフェース番号を確認できません。");
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            dnsCts.CancelAfter(TimeSpan.FromSeconds(5));
            var addresses = await Dns.GetHostAddressesAsync(guestHost, dnsCts.Token).ConfigureAwait(false);
            var matched = addresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal && a.ScopeId == scopeIdx)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matched.Length > 1)
                throw new InvalidOperationException("設定された TAP に複数の macOS リンクローカルアドレスがあります。接続先を一意に確認できません。");
            if (matched.Length == 1) guestHost = matched[0];
        }
        return (guestHost, 22);
    }

    private static void AssertVmStopped(string vmDirectory)
    {
        var (_, alive, _) = ReadLiveRuntime(vmDirectory);
        if (alive) throw new InvalidOperationException("Stop this VM with stop.ps1 -Exit before changing its disks or firmware.");
    }

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
}
