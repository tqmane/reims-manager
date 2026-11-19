using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReimsManager.Services;

internal static partial class NativeEngineService
{
    private const string UsbDkMsiUrl = "https://github.com/daynix/UsbDk/releases/download/v1.00-22/UsbDk_1.0.22_x64.msi";
    private const string UsbDkExpectedSha256 = "91f6f695e1e13c656024e6d3b55620bf08d8835ef05ee0496935ba6bb62466a5";

    [GeneratedRegex(@"\A([a-fA-F0-9]{64})(?:[ \t]+\*?([^\r\n]+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SidecarSha256Regex();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct, dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public nint pPolicyCallbackData, pSIPClientData, pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public string? pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public nint pSignatureSettings;
    }

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid pgActionId, ref WinTrustData pWVTData);

    internal static async Task InstallEngineAsync(
        string destination,
        string? localArchive,
        string? explicitSha256,
        Action<string, double?>? progress = null,
        CancellationToken cancellation = default)
    {
        var pin = AtomicFileStore.ReadJsonNode(NativeStatusService.ResolveScriptAssetPath("engine-lock.json"), 65536).AsObject();
        destination = Path.GetFullPath(destination).TrimEnd('\\', '/');
        string root = Path.GetPathRoot(destination)?.TrimEnd('\\', '/') ?? "";
        if (string.IsNullOrEmpty(destination) || destination == root || Path.Exists(destination))
            throw new IOException("Choose a new engine folder; existing files are never replaced.");

        string parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("Invalid destination folder.");
        for (string? ancestor = parent; !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor)) {
            if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Choose a destination without directory links.");
        }

        string? expectedLocalHash = null;
        string? archiveFull = null;
        if (!string.IsNullOrWhiteSpace(localArchive)) {
            archiveFull = Path.GetFullPath(localArchive);
            expectedLocalHash = ResolveArchiveDigest(archiveFull, explicitSha256);
        }

        Directory.CreateDirectory(parent);
        string work = Path.Combine(parent, $".reims-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        FileStream? archiveLock = null;
        try {
            if (archiveFull is null) {
                progress?.Invoke(UiText.T("配布済みエンジンを確認しています…"), 2);
                var (url, _, asset, bytes) = NativeStatusService.ResolveReleaseAssetInfo(pin);
                string? archiveSha = pin["sha256"]?.GetValue<string>();
                archiveFull = Path.Combine(work, asset);
                progress?.Invoke(UiText.T("エンジンとネイティブ DLL をダウンロードしています…"), 5);
                try {
                    await NativeToolRunner.DownloadVerifiedAsync(url, archiveSha, bytes, archiveFull, (msg, pct) => {
                        progress?.Invoke($"{UiText.T("ダウンロード中")}: {msg}", pct is double p ? 5 + p * 0.65 : null);
                    }, cancellation).ConfigureAwait(false);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    throw new IOException($"対応エンジンを取得・検証できません。未公開、ネットワーク障害、または ZIP の不一致です。{ex.Message}", ex);
                }
            }

            archiveLock = new FileStream(archiveFull, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (expectedLocalHash is not null && !string.Equals(NativeToolRunner.ComputeSha256Lower(archiveFull), expectedLocalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Engine archive SHA256 mismatch; download the ZIP and matching sidecar again.");

            progress?.Invoke(UiText.T("ファイルとソースの固定版を検証しています…"), 72);
            string stage = Path.Combine(work, "engine");
            NativeToolRunner.ExpandZipSafely(archiveFull, stage, cancellation);
            archiveLock.Dispose();
            archiveLock = null;

            NativeStatusService.VerifyEnginePackage(stage);
            progress?.Invoke(UiText.T("QEMU・WHPX・Reims・ディスクツールの起動を確認しています…"), 88);
            await TestNativeEngineAsync(stage, cancellation).ConfigureAwait(false);

            if (Path.Exists(destination))
                throw new IOException("Destination appeared during installation; nothing was replaced.");

            for (int attempt = 0; ; attempt++) {
                try {
                    Directory.Move(stage, destination);
                    break;
                } catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 9 && !Path.Exists(destination)) {
                    await Task.Delay(500, cancellation).ConfigureAwait(false);
                }
            }
            progress?.Invoke(UiText.Format($"導入完了: {destination}"), 100);
        } finally {
            archiveLock?.Dispose();
            if (Directory.Exists(work)) {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }
    }

    internal static async Task<string> EnableWhpxElevatedAsync(CancellationToken cancellation = default)
    {
        string dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        var info = new ProcessStartInfo(dism) {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "/Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart"
        };
        try {
            using var proc = Process.Start(info) ?? throw new IOException("dism.exe を起動できませんでした。");
            await proc.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (proc.ExitCode is not (0 or 3010))
                throw new IOException($"Windows Hypervisor Platform の有効化に失敗しました ({proc.ExitCode})。");
            return "作業を保存して Windows を再起動してから、VM を起動してください。";
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            throw new InvalidOperationException("管理者権限の確認（UAC）がキャンセルされました。");
        }
    }

    internal static async Task<string> InstallUsbDkAsync(string vmDirectory, bool hasVm, CancellationToken cancellation = default)
    {
        if (hasVm && !string.IsNullOrWhiteSpace(vmDirectory))
            AtomicFileStore.AssertVmStopped(vmDirectory);

        string supportRoot = Path.Combine(ManagerSettings.DataDirectory, "support");
        string downloads = Path.Combine(supportRoot, "downloads");
        string diagnostics = Path.Combine(supportRoot, "diagnostics");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(diagnostics);

        string installer = Path.Combine(downloads, "UsbDk_1.0.22_x64.msi");
        await NativeToolRunner.DownloadVerifiedAsync(UsbDkMsiUrl, UsbDkExpectedSha256, 0, installer, null, cancellation).ConfigureAwait(false);
        VerifyAuthenticodeRedHat(installer);

        string log = Path.Combine(diagnostics, "usbdk-install.log");
        string msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        var info = new ProcessStartInfo(msiexec) {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"/i \"{installer}\" /qn /norestart /L*v \"{log}\""
        };
        try {
            using var proc = Process.Start(info) ?? throw new IOException("msiexec.exe を起動できませんでした。");
            await proc.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (proc.ExitCode is not (0 or 3010))
                throw new IOException($"USB 転送ドライバーの導入に失敗しました ({proc.ExitCode})。ログ: {log}");
            return JsonSerializer.Serialize(new {
                schema = 1,
                installed = true,
                restart_required = proc.ExitCode == 3010,
                exit_code = proc.ExitCode
            });
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            throw new InvalidOperationException("管理者権限の確認（UAC）がキャンセルされました。");
        }
    }

    private static string ResolveArchiveDigest(string archivePath, string? explicitSha256)
    {
        if (string.IsNullOrWhiteSpace(explicitSha256)) {
            string sidecar = archivePath + ".sha256";
            if (!File.Exists(sidecar))
                throw new FileNotFoundException("An explicit SHA256 or the release/CI .sha256 sidecar is required before extracting a local engine ZIP.");
            var info = new FileInfo(sidecar);
            if (info.Length > 4096) throw new InvalidDataException("Invalid SHA256 sidecar: file is too large.");
            string text = File.ReadAllText(sidecar).Trim();
            var m = SidecarSha256Regex().Match(text);
            if (!m.Success || (m.Groups[2].Success && m.Groups[2].Value != Path.GetFileName(archivePath)))
                throw new InvalidDataException("Invalid SHA256 sidecar: expected one digest for this ZIP.");
            return m.Groups[1].Value;
        }
        if (!Regex.IsMatch(explicitSha256, @"\A[a-fA-F0-9]{64}\z"))
            throw new InvalidDataException("Invalid explicit SHA256: expected 64 hexadecimal characters.");
        return explicitSha256;
    }

    private static async Task TestNativeEngineAsync(string directory, CancellationToken cancellation)
    {
        var probes = new (string Key, string[] Args, string Expected)[] {
            ("qemu", ["--version"], "QEMU"),
            ("qemu_img", ["--version"], "qemu-img"),
            ("qemu_io", ["--version"], "qemu-io"),
            ("seven_zip", ["i"], "7-Zip"),
            ("qemu", ["-accel", "help"], "whpx"),
            ("qemu", ["-device", "help"], "apple-gfx-pci")
        };
        foreach (var (key, args, expected) in probes) {
            string tool = NativeToolRunner.ResolveEngineTool(directory, key);
            var res = await NativeToolRunner.RunAsync(tool, args, directory, null, isolatedSystemPath: true, timeoutMs: 30_000, cancellation).ConfigureAwait(false);
            string combined = res.StdOut + res.StdErr;
            if (res.ExitCode != 0 || !combined.Contains(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Native engine check failed: {key} ({res.ExitCode}). Required DLLs or Reims/WHPX support may be missing. {combined}");
        }
    }

    private static void VerifyAuthenticodeRedHat(string filePath)
    {
        var actionGuid = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = filePath };
        nint filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var trustData = new WinTrustData {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (fast & offline compatible once cached)
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1 // WTD_STATEACTION_VERIFY
            };
            int status = WinVerifyTrust(IntPtr.Zero, ref actionGuid, ref trustData);
            trustData.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(IntPtr.Zero, ref actionGuid, ref trustData);
            if (status != 0)
                throw new InvalidDataException($"UsbDk の署名を確認できません (WinVerifyTrust=0x{status:X8})。");
        } finally {
            Marshal.FreeHGlobal(filePtr);
        }

#pragma warning disable SYSLIB0057 // Authenticode MSI signer cert extraction requires PE/CMS parser.
        using var cert = X509CertificateLoader.LoadCertificate(X509Certificate.CreateFromSignedFile(filePath).GetRawCertData());
#pragma warning restore SYSLIB0057
        if (!cert.Subject.Contains("Red Hat, Inc.", StringComparison.Ordinal))
            throw new InvalidDataException("UsbDk の署名者が Red Hat, Inc. ではありません。");
    }
}
