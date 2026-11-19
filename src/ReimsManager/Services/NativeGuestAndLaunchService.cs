using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ReimsManager.Services;

internal static partial class NativeGuestAndLaunchService
{
    [GeneratedRegex(@"^SHA256:[A-Za-z0-9+/]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex SshFingerprintRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low, High; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoW lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint hProcess, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    internal static async Task<string> PairAsync(string vmDirectory, string user, string fingerprint, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(user) || !Regex.IsMatch(user, @"^[a-z_][a-z0-9_-]*$") || !SshFingerprintRegex().IsMatch(fingerprint))
            throw new InvalidOperationException("macOS のユーザー名と setup.command が表示した SHA256 指紋を入力してください。");

        var config = AtomicFileStore.ReadVmConfig(vmDirectory);
        var (host, port) = await NativeVmControlService.ResolveGuestEndpointForActionAsync(config, cancellation).ConfigureAwait(false);
        string scanner = NativeToolRunner.ResolveOpenSshKeyscan();
        var scanOut = await NativeToolRunner.RunAsync(scanner, ["-T", "5", "-t", "ed25519", "-p", port.ToString(), host], timeoutMs: 15_000, cancellation: cancellation).ConfigureAwait(false);
        string[] matches = scanOut.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#') && l.Contains(" ssh-ed25519 ", StringComparison.Ordinal))
            .ToArray();
        if (scanOut.ExitCode != 0 || matches.Length != 1)
            throw new IOException("macOS SSH ホスト鍵を取得できませんでした。");

        string[] parts = matches[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte[] blob = Convert.FromBase64String(parts[2]);
        string actual = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        if (!string.Equals(actual, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("ホスト鍵の指紋が一致しません。保存しませんでした。");

        string knownPath = Path.Combine(vmDirectory, "state", "known_hosts");
        const string alias = "reims-macos";
        string entryLine = $"{alias} ssh-ed25519 {Convert.ToBase64String(blob)}";
        var kept = File.Exists(knownPath)
            ? File.ReadAllLines(knownPath).Where(l => !l.StartsWith(alias + " ", StringComparison.Ordinal)).ToList()
            : [];
        kept.Add(entryLine);
        AtomicFileStore.WriteTextAtomicCore(knownPath, string.Join(Environment.NewLine, kept) + Environment.NewLine);

        var guestObj = new JsonObject { ["user"] = user, ["host_key_alias"] = alias };
        AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "guest.json"), guestObj, vmDirectory, vmDirectory);
        return "SSH ホスト鍵を照合し、接続設定を保存しました。";
    }

    internal static async Task<string> InvokeCursorAidAsync(string toolsRoot, string vmDirectory, string operation, CancellationToken cancellation = default)
    {
        if (operation is not ("enable" or "disable" or "move-main" or "move-extended"))
            throw new ArgumentException($"Unsupported cursor aid operation: {operation}");

        var request = new JsonObject { ["schema"] = 1, ["operation"] = operation };
        if (operation != "disable") {
            string dir = Path.Combine(toolsRoot, "cursor-aid");
            string manifestPath = Path.Combine(dir, "manifest.json");
            string binaryPath = Path.Combine(dir, "ReimsCursorAid");
            if (!File.Exists(manifestPath) || !File.Exists(binaryPath))
                throw new FileNotFoundException("The bundled cursor aid is missing. Re-extract the complete Manager package.");
            var m = AtomicFileStore.ReadJsonNode(manifestPath, 4096).AsObject();
            string? exeName = m["executable"]?.GetValue<string>();
            string? sha = m["sha256"]?.GetValue<string>();
            string? rev = m["revision"]?.GetValue<string>();
            if ((m["schema"]?.GetValue<int>() ?? 0) != 1 || exeName != "ReimsCursorAid" ||
                sha is null || !Regex.IsMatch(sha, @"^[0-9a-f]{64}$") ||
                rev is null || !Regex.IsMatch(rev, @"^[0-9a-f]{40}$"))
                throw new InvalidDataException("Invalid cursor aid manifest.");
            var binInfo = new FileInfo(binaryPath);
            if (binInfo.Length is < 1 or > 20 * 1024 * 1024) throw new InvalidDataException("Invalid cursor aid executable size.");
            byte[] bytes = File.ReadAllBytes(binaryPath);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha, StringComparison.Ordinal))
                throw new InvalidDataException("Bundled cursor aid does not match its manifest.");
            request["manifest"] = m.DeepClone();
            request["payload"] = Convert.ToBase64String(bytes);
        }

        string scriptB64 = Convert.ToBase64String(File.ReadAllBytes(NativeStatusService.ResolveScriptAssetPath("guest-cursor-aid.py")));
        string command = $"/usr/bin/env python3 -c 'import base64;exec(compile(base64.b64decode(\"{scriptB64}\"),\"reims-cursor-aid\",\"exec\"))'";
        string json = await NativeVmControlService.InvokeGuestControlAsync(vmDirectory, "", command, request.ToJsonString(), cancellation).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(json) > 4096) throw new InvalidDataException("Unexpected cursor aid result size.");

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        bool expectedEnabled = operation != "disable";
        if (r.GetProperty("schema").GetInt32() != 1 || r.GetProperty("enabled").GetBoolean() != expectedEnabled)
            throw new InvalidOperationException("Cursor aid did not confirm the requested state.");
        if (expectedEnabled && r.GetProperty("pid").GetInt64() <= 0)
            throw new InvalidOperationException("Cursor aid did not return a valid PID.");
        if (!expectedEnabled && r.TryGetProperty("pid", out var pidProp) && pidProp.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("Cursor aid reported a running PID after disable.");

        if (operation.StartsWith("move-", StringComparison.Ordinal)) {
            string target = operation == "move-main" ? "main" : "extended";
            var mv = r.GetProperty("move");
            if (mv.GetProperty("schema").GetInt32() != 1 ||
                mv.GetProperty("action").GetString() != "move-to-display" ||
                mv.GetProperty("target").GetString() != target ||
                !mv.GetProperty("ok").GetBoolean() ||
                mv.GetProperty("cgError").GetInt64() != 0 ||
                (mv.TryGetProperty("error", out var errProp) && errProp.ValueKind != JsonValueKind.Null))
                throw new InvalidOperationException("Cursor movement was not confirmed; cursor aid remains enabled.");
            return target == "main" ? "カーソルをメイン画面へ移動しました。" : "カーソルを拡張画面へ移動しました。";
        }
        return expectedEnabled ? "カーソル補助を有効にしました。macOS への次回ログイン時も起動します。" : "カーソル補助を無効にしました。";
    }

    internal static async Task<string> InvokeDisplayOptimizationAsync(string vmDirectory, string operation, CancellationToken cancellation = default)
    {
        if (operation is not ("lighten" or "restore"))
            throw new ArgumentException($"Unsupported display optimization operation: {operation}");

        byte[] source = File.ReadAllBytes(NativeStatusService.ResolveScriptAssetPath("guest-display.py"));
        if (source.Length > 20 * 1024) throw new InvalidDataException("Display helper exceeds the bounded SSH command size.");
        string b64 = Convert.ToBase64String(source);
        string command = $"/usr/bin/env python3 -c 'import base64;exec(compile(base64.b64decode(\"{b64}\"),\"reims-display\",\"exec\"))'";
        string payload = JsonSerializer.Serialize(new { schema = 1, operation });
        string json = await NativeVmControlService.InvokeGuestControlAsync(vmDirectory, "", command, payload, cancellation).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(json) > 4096) throw new InvalidDataException("Unexpected display result size.");

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.GetProperty("schema").GetInt32() != 1 ||
            r.GetProperty("operation").GetString() != operation ||
            r.GetProperty("applied").GetBoolean() != (operation == "lighten") ||
            !r.GetProperty("main_unchanged").GetBoolean() ||
            r.GetProperty("display_id").GetUInt32() == 0)
            throw new InvalidOperationException("Display optimization did not confirm the requested state.");

        return operation == "lighten"
            ? "拡張画面をセッション限定で軽量化しました。メイン画面は変更していません。"
            : "管理済みの拡張画面を元のモードへ戻しました。";
    }

    internal static async Task<string> StartVmAsync(
        string engineRoot, string toolsRoot, string vmDirectory, string? explicitQemu,
        bool withInstaller, bool paused = false, bool headless = false, CancellationToken cancellation = default)
    {
        return await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            10_000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => StartVmCoreAsync(engineRoot, toolsRoot, vmDirectory, explicitQemu, withInstaller, paused, headless, cancellation).GetAwaiter().GetResult()), cancellation).ConfigureAwait(false);
    }

    private static async Task<string> StartVmCoreAsync(
        string engineRoot, string toolsRoot, string vmDirectory, string? explicitQemu,
        bool withInstaller, bool paused, bool headless, CancellationToken cancellation)
    {
        var config = AtomicFileStore.ReadVmConfig(vmDirectory);
        string usbBackend = config["usb_backend"]?.GetValue<string>() ?? "winusb";
        if (usbBackend is not ("winusb" or "usbdk")) throw new InvalidDataException("Invalid saved USB backend.");

        string qemuExe = !string.IsNullOrWhiteSpace(explicitQemu)
            ? Path.GetFullPath(explicitQemu)
            : NativeToolRunner.ResolveEngineTool(engineRoot, "qemu");
        if (NativeToolRunner.IsPortableEngine(engineRoot)) {
            string pinnedQemu = Path.GetFullPath(NativeToolRunner.ResolveEngineTool(engineRoot, "qemu"));
            if (!string.Equals(qemuExe, pinnedQemu, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Selected QEMU does not belong to the installed engine package.");
        }

        string runDir = Path.Combine(vmDirectory, "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        bool wifiMacNat = config["network"]?["mode"]?.GetValue<string>() == "bridge" &&
                          config["network"]?["forwarder"]?.GetValue<string>() is "macnat" or "macnat-ipv4";
        if (wifiMacNat) {
            var wifiStatus = NativeStatusService.GetWifiStatusNode(vmDirectory, config, null);
            if (!(wifiStatus["configured"]?.GetValue<bool>() ?? false) || !(wifiStatus["ipv6_enabled"]?.GetValue<bool>() ?? false))
                throw new InvalidOperationException("Reapply Wi-Fi preparation for IPv6 before starting this VM.");
        }

        bool hideCursor = NativeStatusService.ReadHostCursorHidden(vmDirectory);
        List<string> qemuArgs = BuildQemuArguments(engineRoot, vmDirectory, config, runDir, paused || wifiMacNat, withoutInstaller: !withInstaller, hideHostCursor: hideCursor);

        var (existingRt, alive, existingQmpPort) = AtomicFileStore.ReadLiveRuntime(vmDirectory);
        if (alive && existingRt is not null) {
            string state = await QmpClient.QueryStatusAsync(existingQmpPort, cancellation).ConfigureAwait(false);
            if (state != "shutdown")
                throw new InvalidOperationException($"VM is {state}. Start only powers on a shut-down VM.");
            string prevRunDir = AtomicFileStore.AssertArtifactPath(existingRt["run_directory"]!.GetValue<string>(), engineRoot, vmDirectory);
            var prevCmd = AtomicFileStore.ReadJsonNode(Path.Combine(prevRunDir, "command.json")).AsObject();
            string[] prevArgs = StripVariableLaunchFlags(prevCmd["arguments"]!.AsArray().Select(n => n!.GetValue<string>()));
            string[] expectedArgs = StripVariableLaunchFlags(BuildQemuArguments(engineRoot, vmDirectory, config, prevRunDir, false, !withInstaller, hideCursor));

            var savedUsbIds = ReadSavedUsbIds(config).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var livePeripherals = (await QmpClient.QomListAsync(existingQmpPort, "/machine/peripheral", cancellation).ConfigureAwait(false))
                .Select(p => p.Name).Where(n => n.StartsWith("hostusb-", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!savedUsbIds.SequenceEqual(livePeripherals, StringComparer.Ordinal))
                throw new InvalidOperationException("USB selections differ from the retained VM. Exit QEMU before starting.");
            if (!string.Equals(existingRt["executable"]?.GetValue<string>(), qemuExe, StringComparison.OrdinalIgnoreCase) ||
                (prevCmd["headless"]?.GetValue<bool>() ?? false) != headless ||
                !prevArgs.SequenceEqual(expectedArgs, StringComparer.Ordinal))
                throw new InvalidOperationException("Launch settings changed. Exit QEMU before starting with different disks, firmware, devices or options.");

            using (var _ = await QmpClient.ExecuteAsync(existingQmpPort, "system_reset", null, cancellation).ConfigureAwait(false)) { }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true) {
                string curState = await QmpClient.QueryStatusAsync(existingQmpPort, cancellation).ConfigureAwait(false);
                if (curState == "prelaunch") break;
                if (curState != "shutdown" || DateTime.UtcNow > deadline)
                    throw new TimeoutException($"VM reset did not reach prelaunch: {curState}.");
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
            if (wifiMacNat) await StartWifiForwarderAsync(vmDirectory, config, existingRt, cancellation).ConfigureAwait(false);
            if (!paused) {
                using var _ = await QmpClient.ExecuteAsync(existingQmpPort, "cont", null, cancellation).ConfigureAwait(false);
            }
            string appleWarn = TryStartIphoneUsbAutoConnect(engineRoot, toolsRoot, vmDirectory, config, existingRt, cancellation);
            string msg = $"Powered on the existing QEMU process, PID {existingRt["pid"]!.GetValue<int>()}. Logs: {prevRunDir}";
            return string.IsNullOrEmpty(appleWarn) ? msg : msg + "\n" + appleWarn;
        }

        // Cold launch checks
        AtomicFileStore.AssertVmStopped(vmDirectory);
        _ = NativePnpUsb.GetProtectedUsbIds(engineRoot, vmDirectory);
        string prereqJson = await NativeStatusService.GetPrerequisitesJsonAsync(engineRoot, toolsRoot, config["cpus"]!.GetValue<int>(), config["memory_mib"]!.GetValue<int>(), cancellation).ConfigureAwait(false);
        bool whpxReady = JsonDocument.Parse(prereqJson).RootElement.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == "whpx").GetProperty("ready").GetBoolean();
        if (!whpxReady)
            throw new InvalidOperationException("WHPX unavailable. Enable Windows Hypervisor Platform and reboot.");

        string activeDisk = AtomicFileStore.AssertArtifactPath(config["active_disk"]!.GetValue<string>(), engineRoot, vmDirectory);
        string baseDisk = AtomicFileStore.AssertArtifactPath(config["base_disk"]!.GetValue<string>(), engineRoot, vmDirectory);
        foreach (string key in new[] { "active_disk", "opencore", "firmware_code", "firmware_vars", "rom" }) {
            string p = AtomicFileStore.AssertArtifactPath(config[key]!.GetValue<string>(), engineRoot, vmDirectory);
            if (!File.Exists(p)) throw new FileNotFoundException($"Missing VM input: {p}", p);
        }
        if (withInstaller && !File.Exists(config["iso"]?.GetValue<string>() ?? ""))
            throw new FileNotFoundException("Installer is missing.");

        string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");
        using (var chainDoc = JsonDocument.Parse(await NativeToolRunner.RunCheckedAsync(qemuImg, ["info", "--backing-chain", "--output=json", activeDisk], cancellation: cancellation).ConfigureAwait(false))) {
            var arr = chainDoc.RootElement.EnumerateArray().ToArray();
            if (arr.Length < 2 || arr[0].GetProperty("format").GetString() != "qcow2" ||
                !string.Equals(Path.GetFullPath(arr[^1].GetProperty("filename").GetString() ?? ""), Path.GetFullPath(baseDisk), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Active disk is not an overlay of this VM base disk.");
        }

        string accels = await NativeToolRunner.RunCheckedAsync(qemuExe, ["-accel", "help"], Path.GetDirectoryName(qemuExe), cancellation: cancellation).ConfigureAwait(false);
        if (!accels.Split(['\r', '\n']).Any(l => l.Trim() == "whpx"))
            throw new InvalidOperationException("This QEMU was not built with WHPX.");
        string gfxProps = await NativeToolRunner.RunCheckedAsync(qemuExe, ["-device", "apple-gfx-pci,help"], Path.GetDirectoryName(qemuExe), cancellation: cancellation).ConfigureAwait(false);
        if (!gfxProps.Contains("resolution", StringComparison.Ordinal))
            throw new InvalidOperationException("This QEMU lacks the reims apple-gfx-pci ABI.");
        if (hideCursor && !gfxProps.Contains("hide-host-cursor", StringComparison.Ordinal))
            throw new InvalidOperationException("This engine lacks host cursor control.");

        string netMode = config["network"]?["mode"]?.GetValue<string>() ?? "nat";
        if (netMode == "bridge") {
            string tap = config["network"]?["adapter"]?.GetValue<string>() ?? "";
            if (!NativeHostNetwork.GetNetworkInventory().Taps.Contains(tap, StringComparer.Ordinal))
                throw new InvalidOperationException("The configured TAP adapter is missing. Install TAP-Windows6, create a Windows bridge, or switch to NAT.");
        }

        int qmpPort = config["qmp_port"]!.GetValue<int>();
        int sshPort = config["ssh_port"]!.GetValue<int>();
        foreach (int p in netMode == "nat" ? new[] { qmpPort, sshPort } : [qmpPort]) {
            var listener = new TcpListener(IPAddress.Loopback, p);
            try { listener.Start(); } finally { listener.Stop(); }
        }

        Directory.CreateDirectory(runDir);
        var launchJson = new JsonObject {
            ["executable"] = qemuExe,
            ["arguments"] = new JsonArray(qemuArgs.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            ["headless"] = headless,
            ["diagnostic_only"] = false,
            ["usb_backend"] = usbBackend
        };
        AtomicFileStore.WriteJsonAtomic(Path.Combine(runDir, "command.json"), launchJson, engineRoot, vmDirectory);

        var (pid, ticks) = StartDetachedQemuProcess(
            qemuExe, qemuArgs, engineRoot,
            Path.Combine(runDir, "stdout.log"),
            Path.Combine(runDir, "stderr.log"),
            usbBackend, headless);

        var rtObj = new JsonObject {
            ["pid"] = pid,
            ["start_ticks"] = ticks,
            ["executable"] = qemuExe,
            ["qmp_port"] = qmpPort,
            ["run_directory"] = runDir,
            ["disk"] = activeDisk,
            ["diagnostic_only"] = false,
            ["usb_backend"] = usbBackend
        };
        AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "runtime.json"), rtObj, engineRoot, vmDirectory);

        try {
            var qmpDeadline = DateTime.UtcNow.AddSeconds(20);
            while (true) {
                if (!ProcessIdentity.IsAlive(pid, ticks, qemuExe))
                    throw new IOException(ReadVmStartFailure(runDir, config["memory_mib"]!.GetValue<int>()));
                try {
                    using var stDoc = await QmpClient.ExecuteAsync(qmpPort, "query-status", null, cancellation).ConfigureAwait(false);
                    AtomicFileStore.WriteTextAtomicCore(Path.Combine(runDir, "qmp-status.json"), stDoc.RootElement.GetRawText());
                    break;
                } catch {
                    if (DateTime.UtcNow > qmpDeadline) throw;
                    await Task.Delay(250, cancellation).ConfigureAwait(false);
                }
            }
            if (wifiMacNat) {
                await StartWifiForwarderAsync(vmDirectory, config, rtObj, cancellation).ConfigureAwait(false);
                if (!paused) {
                    using var _ = await QmpClient.ExecuteAsync(qmpPort, "cont", null, cancellation).ConfigureAwait(false);
                }
            }
        } catch {
            if (wifiMacNat) { try { NativeVmControlService.ForceExitAsync(vmDirectory, CancellationToken.None).GetAwaiter().GetResult(); } catch { } }
            if (ProcessIdentity.IsAlive(pid, ticks, qemuExe)) {
                try { using var pr = System.Diagnostics.Process.GetProcessById(pid); pr.Kill(true); } catch { }
            }
            throw;
        }

        string appleWarning = TryStartIphoneUsbAutoConnect(engineRoot, toolsRoot, vmDirectory, config, rtObj, cancellation);
        string startedMsg = $"Started PID {pid}, QMP 127.0.0.1:{qmpPort}, SSH 127.0.0.1:{sshPort}. Logs: {runDir}";
        return string.IsNullOrEmpty(appleWarning) ? startedMsg : startedMsg + "\n" + appleWarning;
    }

    private static (int Pid, long StartTicks) StartDetachedQemuProcess(
        string exe, List<string> args, string workingDir, string stdoutPath, string stderrPath, string usbBackend, bool headless)
    {
        var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
        nint saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
        Marshal.StructureToPtr(sa, saPtr, false);

        using var outFs = new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var errFs = new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        SetHandleInformation(outFs.SafeFileHandle, 1 /* HANDLE_FLAG_INHERIT */, 1);
        SetHandleInformation(errFs.SafeFileHandle, 1 /* HANDLE_FLAG_INHERIT */, 1);

        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        if (!env.ContainsKey("REIMS_VGPU_LOG") || string.IsNullOrWhiteSpace(env["REIMS_VGPU_LOG"]))
            env["REIMS_VGPU_LOG"] = "info";
        env["REIMS_VGPU_STATS"] = "1";
        if (usbBackend == "usbdk") env["REIMS_USB_BACKEND"] = "usbdk";
        else env.Remove("REIMS_USB_BACKEND");
        if (headless) env["REIMS_VGPU_WINDOW"] = "0";
        else env.Remove("REIMS_VGPU_WINDOW");

        var envSb = new StringBuilder();
        foreach (var kv in env) envSb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        envSb.Append('\0');
        byte[] envBytes = Encoding.Unicode.GetBytes(envSb.ToString());
        nint envPtr = Marshal.AllocHGlobal(envBytes.Length);
        Marshal.Copy(envBytes, 0, envPtr, envBytes.Length);

        try {
            var si = new StartupInfoW {
                cb = Marshal.SizeOf<StartupInfoW>(),
                dwFlags = 0x00000001 /* STARTF_USESHOWWINDOW */ | 0x00000100 /* STARTF_USESTDHANDLES */,
                wShowWindow = 0 /* SW_HIDE */,
                hStdOutput = outFs.SafeFileHandle.DangerousGetHandle(),
                hStdError = errFs.SafeFileHandle.DangerousGetHandle(),
                hStdInput = IntPtr.Zero
            };
            string cmdLine = QuoteWindowsArg(exe) + " " + string.Join(' ', args.Select(QuoteWindowsArg));
            if (!CreateProcessW(exe, cmdLine, saPtr, IntPtr.Zero, true, 0x08000400 /* CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT */, envPtr, workingDir, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to launch QEMU.");
            try {
                if (!GetProcessTimes(pi.hProcess, out var created, out _, out _, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                long ticks = DateTime.FromFileTimeUtc((long)(((ulong)created.High << 32) | created.Low)).Ticks;
                return (pi.dwProcessId, ticks);
            } finally {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        } finally {
            Marshal.FreeHGlobal(saPtr);
            Marshal.FreeHGlobal(envPtr);
        }
    }

    private static async Task StartWifiForwarderAsync(string vmDirectory, JsonObject config, JsonObject qemuRuntime, CancellationToken cancellation)
    {
        // Fast native launch for running macnat; previous-boot dead-forwarder cleanup delegates to wifi-macnat.ps1.
        string helper = Path.Combine(AppContext.BaseDirectory, "runtimes", "wifi-macnat", "reims-wifi-macnat.exe");
        string folder = Path.Combine(vmDirectory, "state", "wifi-forwarder");
        string rtPath = Path.Combine(folder, "runtime.json");
        if (File.Exists(rtPath)) {
            var prev = AtomicFileStore.ReadJsonNode(rtPath).AsObject();
            bool cleanStopped = false;
            string stPath = Path.Combine(folder, "status.json");
            if (File.Exists(stPath)) {
                var st = AtomicFileStore.ReadJsonNode(stPath).AsObject();
                cleanStopped = st["session"]?.GetValue<string>() == prev["session"]?.GetValue<string>() && st["state"]?.GetValue<string>() == "stopped";
            }
            if (!cleanStopped) {
                string script = NativeStatusService.ResolveScriptAssetPath("start.ps1");
                await NativeToolRunner.RunCheckedAsync(NativeUsbService.FindPwshExecutable(), [
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                    $"$env:REIMS_VM_ROOT_OVERRIDE='{vmDirectory.Replace("'", "''")}'; . '{script.Replace("'", "''")}' -FunctionsOnly; Start-ReimsWifiForwarder (Get-ReimsVmConfig) (Get-ReimsRuntime)"
                ], cancellation: cancellation).ConfigureAwait(false);
                return;
            }
        }

        if (!File.Exists(helper)) throw new FileNotFoundException("Bundled native Wi-Fi helper is missing.", helper);
        var wifiStatus = NativeStatusService.GetWifiStatusNode(vmDirectory, config, null);
        if (!(wifiStatus["configured"]?.GetValue<bool>() ?? false) || !(wifiStatus["ipv6_enabled"]?.GetValue<bool>() ?? false))
            throw new InvalidOperationException("Wi-Fi configuration/ownership does not match.");
        var rec = AtomicFileStore.ReadJsonNode(Path.Combine(vmDirectory, "state", "wifi-forwarder.json")).AsObject();
        Guid tapId = Guid.Parse(rec["tap_id"]!.GetValue<string>());
        Guid uplinkId = Guid.Parse(rec["uplink_id"]!.GetValue<string>());
        string mac = config["mac"]!.GetValue<string>();
        int qPid = qemuRuntime["pid"]!.GetValue<int>();
        long qTicks = qemuRuntime["start_ticks"]!.GetValue<long>();

        Directory.CreateDirectory(folder);
        string stopFile = Path.Combine(folder, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        string session = Guid.NewGuid().ToString("B");
        // Native forwarder expects: tapGuid, uplinkGuid, mac, qPid, qTicks, folder, session
        var fwArgs = new[] { tapId.ToString("B"), uplinkId.ToString("B"), mac, qPid.ToString(), qTicks.ToString(), folder, session };
        var (fwdPid, fwdTicks) = StartDetachedQemuProcess(helper, [.. fwArgs], folder, Path.Combine(folder, "stdout.log"), Path.Combine(folder, "stderr.log"), "winusb", true);
        var rt = new JsonObject {
            ["schema"] = 1, ["pid"] = fwdPid, ["start_ticks"] = fwdTicks, ["executable"] = helper,
            ["session"] = session, ["qemu_pid"] = qPid, ["qemu_start_ticks"] = qTicks
        };
        AtomicFileStore.WriteJsonAtomic(rtPath, rt, vmDirectory, vmDirectory);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        try {
            while (DateTime.UtcNow < deadline) {
                string statusPath = Path.Combine(folder, "status.json");
                if (File.Exists(statusPath)) {
                    var st = AtomicFileStore.ReadJsonNode(statusPath, 65536).AsObject();
                    if (st["session"]?.GetValue<string>() == session) {
                        if (st["state"]?.GetValue<string>() == "error")
                            throw new InvalidOperationException(st["error"]?.GetValue<string>() ?? "Forwarder error.");
                        if (st["state"]?.GetValue<string>() == "ready" &&
                            st["helper_pid"]?.GetValue<int>() == fwdPid &&
                            st["qemu_pid"]?.GetValue<int>() == qPid &&
                            st["qemu_start_ticks"]?.GetValue<long>() == qTicks) {
                            if (st["ipv6_enabled"]?.GetValue<bool>() != true)
                                throw new InvalidOperationException("Installed helper has no confirmed IPv6 readiness receipt.");
                            return;
                        }
                    }
                }
                if (!ProcessIdentity.IsAlive(fwdPid, fwdTicks, helper))
                    throw new InvalidOperationException("Forwarder exited before ready; inspect its logs.");
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
            throw new TimeoutException("Forwarder readiness timed out.");
        } catch {
            AtomicFileStore.WriteTextAtomicCore(stopFile, "stop");
            throw;
        }
    }

    private static string TryStartIphoneUsbAutoConnect(
        string engineRoot, string toolsRoot, string vmDirectory, JsonObject config, JsonObject runtime, CancellationToken cancellation)
    {
        try {
            bool hasPrimary = config["iphone_usb"] is JsonObject p && (p["enabled"]?.GetValue<bool>() ?? false);
            bool hasExtra = config["iphone_usb_devices"] is JsonArray arr && arr.Any(n => n is JsonObject o && (o["enabled"]?.GetValue<bool>() ?? false));
            if (!hasPrimary && !hasExtra) return "";

            var list = new List<string>();
            if (hasPrimary && config["iphone_usb"] is JsonObject pri) list.Add(pri["id"]!.GetValue<string>());
            if (config["iphone_usb_devices"] is JsonArray arr2) {
                foreach (var n in arr2)
                    if (n is JsonObject o && (o["enabled"]?.GetValue<bool>() ?? false)) list.Add(o["id"]!.GetValue<string>());
            }
            foreach (string id in list)
                _ = NativeUsbService.AttachUsbAsync(engineRoot, toolsRoot, vmDirectory, id, cancellation).GetAwaiter().GetResult();
            return "";
        } catch (Exception ex) {
            return "VM は起動済みですが、iPhone の自動接続を開始できません: " + BackendErrorFormatter.CleanMessage(ex.Message);
        }
    }

    private static List<string> BuildQemuArguments(
        string engineRoot, string vmDirectory, JsonObject config, string runDir,
        bool paused, bool withoutInstaller, bool hideHostCursor)
    {
        static string QPath(string p) => p.Replace('\\', '/').Replace(",", ",,");
        string audio = config["audio"]?.GetValue<string>() ?? "none";
        if (audio is not ("none" or "dsound")) throw new InvalidDataException("Unknown Windows audio backend.");

        string resolution = config["resolution"]?.GetValue<string>() ?? "1920x1080";
        var modes = new[] { $"{resolution}@60", "1280x720@60", "1920x1080@60", "2560x1440@60", "3840x2160@60" }
            .Distinct(StringComparer.Ordinal).ToArray();
        var gfx = new JsonObject {
            ["driver"] = "apple-gfx-pci",
            ["id"] = "gfx",
            ["bus"] = "pcie.0",
            ["addr"] = "0x4",
            ["displays"] = 1,
            ["resolution"] = resolution,
            ["romfile"] = config["rom"]!.GetValue<string>().Replace('\\', '/'),
            ["display-modes"] = new JsonArray(modes.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())
        };
        if (hideHostCursor) gfx["hide-host-cursor"] = true;

        string netMode = config["network"]?["mode"]?.GetValue<string>() ?? "nat";
        int sshPort = config["ssh_port"]!.GetValue<int>();
        string netArg = netMode switch {
            "nat" => $"user,id=net0,ipv6=off,hostfwd=tcp:127.0.0.1:{sshPort}-:22",
            "bridge" => "tap,id=net0,ifname=" + (config["network"]?["adapter"]?.GetValue<string>() ?? "").Replace(",", ",,"),
            _ => throw new InvalidDataException("Unknown network mode.")
        };

        string fwDir = NativeToolRunner.IsPortableEngine(engineRoot)
            ? NativeStatusService.JoinPackagePath(engineRoot, AtomicFileStore.ReadJsonNode(Path.Combine(engineRoot, "reims-engine.json"), 4 * 1024 * 1024)["paths"]!["firmware"]!.GetValue<string>())
            : Path.Combine(engineRoot, "vendor", "qemu", "pc-bios");

        int cpus = config["cpus"]!.GetValue<int>();
        int mem = config["memory_mib"]!.GetValue<int>();
        string cpu = config["cpu"]!.GetValue<string>();
        string mac = config["mac"]!.GetValue<string>();
        int qmpPort = config["qmp_port"]!.GetValue<int>();

        var args = new List<string> {
            "-L", QPath(fwDir),
            "-name", "reims-windows", "-machine", "q35", "-accel", "whpx,hyperv=off",
            "-cpu", cpu, "-smp", $"{cpus},sockets=1,cores={cpus},threads=1", "-m", $"{mem}M",
            "-global", "ICH9-LPC.acpi-pci-hotplug-with-bridge-support=off",
            "-global", "ICH9-LPC.disable_s3=1", "-global", "ICH9-LPC.disable_s4=1",
            "-device", "pcie-root-port,bus=pcie.0,addr=0x1,slot=1,x-speed=16,x-width=32",
            "-device", "isa-applesmc,osk=ourhardworkbythesewordsguardedpleasedontsteal(c)AppleComputerInc",
            "-drive", $"if=pflash,format=raw,readonly=on,file={QPath(config["firmware_code"]!.GetValue<string>())}",
            "-drive", $"if=pflash,format=raw,file={QPath(config["firmware_vars"]!.GetValue<string>())}",
            "-smbios", "type=2",
            "-device", "ich9-ahci,id=sata,addr=0x2",
            "-drive", $"id=opencore,if=none,format=raw,snapshot=on,file={QPath(config["opencore"]!.GetValue<string>())}",
            "-device", "ide-hd,bus=sata.2,drive=opencore,bootindex=1",
            "-drive", $"id=disk,if=none,format=qcow2,file={QPath(config["active_disk"]!.GetValue<string>())}",
            "-device", "ide-hd,bus=sata.3,drive=disk,rotation_rate=1",
            "-netdev", netArg,
            "-device", $"virtio-net-pci,netdev=net0,id=net0,mac={mac},addr=0x3", "-vga", "none",
            "-device", gfx.ToJsonString(),
            "-device", "qemu-xhci,id=xhci,addr=0x5,p2=8,p3=6,conditional-intr-mapping=on",
            "-device", "usb-kbd,id=kbd,bus=xhci.0",
            "-device", "usb-tablet,id=tablet,bus=xhci.0",
            "-audiodev", $"{audio},id=snd0,in.voices=0",
            "-device", "usb-audio,id=audio,audiodev=snd0,bus=xhci.0",
            "-display", "none", "-monitor", "none",
            "-serial", $"file:{QPath(Path.Combine(runDir, "serial.log"))}",
            "-msg", "timestamp=on",
            "-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off"
        };

        if (config["microphone"]?.GetValue<bool>() ?? false) {
            args.AddRange(["-audiodev", "dsound,id=mic0,in.voices=1", "-device", "usb-audio,id=microphone,input=on,audiodev=mic0,bus=xhci.0"]);
        }
        if (config["usb_devices"] is JsonArray usbArr) {
            foreach (var n in usbArr) {
                var u = n!.AsObject();
                var uObj = new JsonObject {
                    ["driver"] = "usb-host",
                    ["id"] = u["id"]!.GetValue<string>(),
                    ["bus"] = "xhci.0",
                    ["hostbus"] = u["bus"]!.GetValue<int>(),
                    ["hostport"] = u["port"]!.GetValue<string>(),
                    ["vendorid"] = Convert.ToInt32(u["vendor"]!.GetValue<string>(), 16),
                    ["productid"] = Convert.ToInt32(u["product"]!.GetValue<string>(), 16),
                    ["guest-reset"] = false
                };
                args.Add("-device");
                args.Add(uObj.ToJsonString());
            }
        }
        if (!withoutInstaller) {
            string kind = config["media_kind"]?.GetValue<string>() ?? "cdrom";
            string prot = kind == "disk" ? "snapshot=on" : "media=cdrom,readonly=on";
            args.AddRange([
                "-drive", $"id=installer,if=none,format=raw,{prot},file={QPath(config["iso"]!.GetValue<string>())}",
                "-device", kind == "disk" ? "ide-hd,bus=sata.4,drive=installer" : "ide-cd,bus=sata.4,drive=installer"
            ]);
        }
        string toolsImg = Path.Combine(vmDirectory, "tools", "ReimsTools.img");
        if (File.Exists(toolsImg))
            args.AddRange(["-drive", $"id=tools,if=none,format=raw,snapshot=on,file={QPath(toolsImg)}", "-device", "ide-hd,id=tools,bus=sata.5,drive=tools"]);
        args.Add("-no-shutdown");
        if (paused) args.Add("-S");
        return args;
    }

    private static string[] StripVariableLaunchFlags(IEnumerable<string> args)
    {
        var list = args.ToArray();
        var clean = new List<string>(list.Length);
        for (int i = 0; i < list.Length; i++) {
            if (list[i] == "-S") continue;
            if (list[i] == "-device" && i + 1 < list.Length && list[i + 1].StartsWith('{')) {
                var obj = JsonNode.Parse(list[i + 1])?.AsObject();
                if (obj?["driver"]?.GetValue<string>() == "usb-host") { i++; continue; }
                if (obj?["driver"]?.GetValue<string>() == "apple-gfx-pci" && obj["id"]?.GetValue<string>() == "gfx") {
                    obj.Remove("hide-host-cursor");
                    clean.Add("-device");
                    clean.Add(obj.ToJsonString());
                    i++;
                    continue;
                }
            }
            clean.Add(list[i]);
        }
        return [.. clean];
    }

    private static IEnumerable<string> ReadSavedUsbIds(JsonObject config) =>
        config["usb_devices"] is JsonArray arr ? arr.Select(x => x!["id"]!.GetValue<string>()) : [];

    private static string ReadVmStartFailure(string runDir, int memoryMiB)
    {
        string log = Path.Combine(runDir, "stderr.log");
        if (File.Exists(log)) {
            foreach (string line in File.ReadLines(log).Take(20)) {
                if (Regex.IsMatch(line, @"Cannot set up .+ of guest memory"))
                    return $"VM 用の {memoryMiB} MiB のメモリを Windows が確保できませんでした。他のアプリを閉じて再試行してください。VM のメモリ設定は変更していません。ログ: {log}";
            }
        }
        return $"QEMU exited. Read {log}";
    }

    private static string QuoteWindowsArg(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (!value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (char c in value) {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            if (bs > 0) { sb.Append('\\', bs); bs = 0; }
            sb.Append(c);
        }
        if (bs > 0) sb.Append('\\', bs * 2);
        return sb.Append('"').ToString();
    }
}
