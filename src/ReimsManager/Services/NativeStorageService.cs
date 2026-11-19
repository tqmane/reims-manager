using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ReimsManager.Services;

internal static partial class NativeStorageService
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotNameRegex();

    [GeneratedRegex(@"reims-vgpu: fatal|panic|WHPX.*fail|VK_ERROR|assertion", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FailureLogLineRegex();

    [LibraryImport("WinHvPlatform.dll")]
    private static partial int WHvGetCapability(uint code, out ulong value, uint size, out uint written);

    internal static async Task<string> CreateOrRestoreSnapshotAsync(
        string engineRoot, string vmDirectory, string name, bool restore, CancellationToken cancellation = default)
    {
        if (!SnapshotNameRegex().IsMatch(name))
            throw new InvalidDataException("Invalid snapshot name.");

        string snapshotsDir = Path.Combine(vmDirectory, "snapshots");
        string snapDir = Path.Combine(snapshotsDir, name);
        string manifestPath = Path.Combine(snapDir, "snapshot.json");
        string nextDisk = Path.Combine(vmDirectory, "disks", $"{Guid.NewGuid()}.qcow2");
        string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");

        string precheckDisk;
        JsonObject? existingSnap = null;
        string? varsTarget = null;
        JsonObject? newSnapshotObj = null;

        await Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => {
                AtomicFileStore.AssertVmStopped(vmDirectory);
                var config = AtomicFileStore.ReadVmConfig(vmDirectory);
                string activeDisk = AtomicFileStore.AssertArtifactPath(config["active_disk"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                string fwCode = AtomicFileStore.AssertArtifactPath(config["firmware_code"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                string fwVars = AtomicFileStore.AssertArtifactPath(config["firmware_vars"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                string ocImg = AtomicFileStore.AssertArtifactPath(config["opencore"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);

                if (!restore) {
                    if (Directory.Exists(snapDir)) throw new IOException($"Snapshot already exists: {name}");
                    precheckDisk = activeDisk;
                } else {
                    if (!File.Exists(manifestPath)) throw new FileNotFoundException($"Snapshot not found: {name}", manifestPath);
                    existingSnap = AtomicFileStore.ReadJsonNode(manifestPath).AsObject();
                    if (existingSnap["schema"]?.GetValue<int>() != 1) throw new InvalidDataException("Unknown snapshot schema.");
                    precheckDisk = AtomicFileStore.AssertArtifactPath(existingSnap["disk"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                    _ = AtomicFileStore.AssertArtifactPath(existingSnap["firmware_vars"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                    if (!string.Equals(NativeToolRunner.ComputeSha256Upper(fwCode), existingSnap["firmware_code_sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(NativeToolRunner.ComputeSha256Upper(ocImg), existingSnap["opencore_sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Firmware/OpenCore changed since this snapshot. Restore with the original firmware pair.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(nextDisk)!);
                NativeToolRunner.RunCheckedAsync(qemuImg, ["check", "-q", precheckDisk], cancellation: cancellation).GetAwaiter().GetResult();
                NativeToolRunner.RunCheckedAsync(qemuImg, ["create", "-f", "qcow2", "-F", "qcow2", "-b", precheckDisk, nextDisk], cancellation: cancellation).GetAwaiter().GetResult();

                if (!restore) {
                    Directory.CreateDirectory(snapDir);
                    varsTarget = Path.Combine(snapDir, "OVMF_VARS.fd");
                    File.Copy(fwVars, varsTarget, overwrite: false);
                    newSnapshotObj = new JsonObject {
                        ["schema"] = 1,
                        ["name"] = name,
                        ["created_utc"] = DateTime.UtcNow.ToString("O"),
                        ["disk"] = activeDisk,
                        ["firmware_vars"] = varsTarget,
                        ["firmware_code_sha256"] = NativeToolRunner.ComputeSha256Upper(fwCode),
                        ["opencore_sha256"] = NativeToolRunner.ComputeSha256Upper(ocImg)
                    };
                } else {
                    varsTarget = Path.Combine(vmDirectory, "firmware", $"OVMF_VARS-{Guid.NewGuid()}.fd");
                    Directory.CreateDirectory(Path.GetDirectoryName(varsTarget)!);
                    File.Copy(existingSnap!["firmware_vars"]!.GetValue<string>(), varsTarget, overwrite: false);
                    config["firmware_vars"] = varsTarget;
                }

                config["active_disk"] = nextDisk;
                AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, engineRoot, vmDirectory);
                if (newSnapshotObj is not null)
                    AtomicFileStore.WriteJsonAtomic(manifestPath, newSnapshotObj, engineRoot, vmDirectory);
            }), cancellation).ConfigureAwait(false);

        string op = restore ? "Restore" : "Create";
        return $"{op} snapshot '{name}'; next boot writes {nextDisk}. Previous disks are retained.";
    }

    internal static Task<string> ExpandDiskAsync(string engineRoot, string vmDirectory, int diskGiB, CancellationToken cancellation = default)
    {
        if (diskGiB is < 64 or > 2048) throw new InvalidDataException("Disk capacity must be 64-2048 GiB.");
        string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");
        return Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => {
                AtomicFileStore.AssertVmStopped(vmDirectory);
                var config = AtomicFileStore.ReadVmConfig(vmDirectory);
                string active = AtomicFileStore.AssertArtifactPath(config["active_disk"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                string infoJson = NativeToolRunner.RunCheckedAsync(qemuImg, ["info", "--output=json", active], cancellation: cancellation).GetAwaiter().GetResult();
                using var infoDoc = JsonDocument.Parse(infoJson);
                long currentVirtualSize = infoDoc.RootElement.GetProperty("virtual-size").GetInt64();
                if ((long)diskGiB * 1024L * 1024L * 1024L <= currentVirtualSize)
                    throw new InvalidOperationException("現在より大きな容量を指定してください。縮小は行いません。");

                string next = Path.Combine(vmDirectory, "disks", $"{Guid.NewGuid()}.qcow2");
                NativeToolRunner.RunCheckedAsync(qemuImg, ["check", "-q", active], cancellation: cancellation).GetAwaiter().GetResult();
                NativeToolRunner.RunCheckedAsync(qemuImg, ["create", "-f", "qcow2", "-F", "qcow2", "-b", active, next], cancellation: cancellation).GetAwaiter().GetResult();
                NativeToolRunner.RunCheckedAsync(qemuImg, ["resize", next, $"{diskGiB}G"], cancellation: cancellation).GetAwaiter().GetResult();
                config["active_disk"] = next;
                config["disk_gib"] = diskGiB;
                AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, engineRoot, vmDirectory);
                return "仮想ディスクを拡張しました。macOS の Disk Utility でも APFS 領域を拡張してください。以前のディスクは保持しています。";
            }), cancellation);
    }

    internal static Task<string> OptimizeDiskAsync(string engineRoot, string vmDirectory, CancellationToken cancellation = default)
    {
        string qemuImg = NativeToolRunner.ResolveEngineTool(engineRoot, "qemu_img");
        return Task.Run(() => AtomicFileStore.WithNamedMutex(
            $"Local\\ReimsManager.VmMutation.{AtomicFileStore.VmMutexIdentity(vmDirectory)}",
            5000,
            "Another VM operation is in progress. Wait for it to finish, then retry.",
            () => {
                AtomicFileStore.AssertVmStopped(vmDirectory);
                var config = AtomicFileStore.ReadVmConfig(vmDirectory);
                string active = AtomicFileStore.AssertArtifactPath(config["active_disk"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                string baseDisk = AtomicFileStore.AssertArtifactPath(config["base_disk"]?.GetValue<string>() ?? "", engineRoot, vmDirectory);
                string next = Path.Combine(vmDirectory, "disks", $"{Guid.NewGuid()}.qcow2");
                string partial = next + ".partial";
                try {
                    NativeToolRunner.RunCheckedAsync(qemuImg, ["convert", "-f", "qcow2", "-O", "qcow2", active, partial], timeoutMs: 600_000, cancellation: cancellation).GetAwaiter().GetResult();
                    NativeToolRunner.RunCheckedAsync(qemuImg, ["rebase", "-f", "qcow2", "-b", baseDisk, "-F", "qcow2", partial], timeoutMs: 600_000, cancellation: cancellation).GetAwaiter().GetResult();
                    NativeToolRunner.RunCheckedAsync(qemuImg, ["check", "-q", partial], timeoutMs: 300_000, cancellation: cancellation).GetAwaiter().GetResult();
                    NativeToolRunner.RunCheckedAsync(qemuImg, ["compare", "-f", "qcow2", "-F", "qcow2", active, partial], timeoutMs: 600_000, cancellation: cancellation).GetAwaiter().GetResult();
                    File.Move(partial, next, overwrite: false);
                } finally {
                    if (File.Exists(partial)) try { File.Delete(partial); } catch { }
                }
                config["active_disk"] = next;
                AtomicFileStore.WriteJsonAtomic(Path.Combine(vmDirectory, "state", "config.json"), config, engineRoot, vmDirectory);
                return "ディスク差分をまとめ、内容の一致を検証しました。以前のディスクとスナップショットは保持されています。";
            }), cancellation);
    }

    internal static async Task<string> CollectLogsAsync(string engineRoot, string vmDirectory, bool screenshot = true, bool zip = false, CancellationToken cancellation = default)
    {
        bool hasVm = !string.IsNullOrWhiteSpace(vmDirectory);
        string workRoot = NativeToolRunner.IsPortableEngine(engineRoot)
            ? (hasVm ? Path.Combine(vmDirectory, "support") : Path.Combine(ManagerSettings.DataDirectory, "support"))
            : Path.Combine(engineRoot, ".work");
        string dest = Path.Combine(workRoot, "diagnostics", $"vm-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(dest);

        bool whpxReady = false;
        try {
            whpxReady = WHvGetCapability(0, out ulong cap, 8, out uint written) >= 0 && written >= 4 && (cap & 0xFFFFFFFF) != 0;
        } catch { whpxReady = false; }

        string? cpuName = null;
        using (var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            cpuName = cpuKey?.GetValue("ProcessorNameString") as string;

        var gpus = new JsonArray();
        try {
            using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}", writable: false);
            if (video is not null) {
                foreach (string sub in video.GetSubKeyNames()) {
                    try {
                        using var k = video.OpenSubKey(sub, writable: false);
                        string? desc = k?.GetValue("DriverDesc") as string;
                        string? ver = k?.GetValue("DriverVersion") as string;
                        if (!string.IsNullOrWhiteSpace(desc))
                            gpus.Add(new JsonObject { ["Name"] = desc, ["DriverVersion"] = ver });
                    } catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        } catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        var hostReport = new JsonObject {
            ["collected_utc"] = DateTime.UtcNow.ToString("O"),
            ["runtime"] = $".NET {Environment.Version}",
            ["whpx"] = new JsonObject { ["ready"] = whpxReady },
            ["os"] = new JsonObject { ["Version"] = Environment.OSVersion.VersionString },
            ["cpu"] = new JsonObject { ["Name"] = cpuName?.Trim(), ["LogicalProcessors"] = Environment.ProcessorCount },
            ["gpu"] = gpus
        };
        AtomicFileStore.WriteJsonAtomic(Path.Combine(dest, "host.json"), hostReport, workRoot, workRoot);

        foreach (string name in new[] { "config.json", "media.json", "opencore.json", "runtime.json" }) {
            if (!hasVm) break;
            string src = Path.Combine(vmDirectory, "state", name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dest, name), overwrite: true);
        }

        var (runtime, alive, qmpPort) = hasVm ? AtomicFileStore.ReadLiveRuntime(vmDirectory) : (null, false, 0);
        if (runtime?["run_directory"]?.GetValue<string>() is { Length: > 0 } runDir) {
            string safeRunDir = AtomicFileStore.AssertArtifactPath(runDir, engineRoot, vmDirectory);
            foreach (string name in new[] { "stdout.log", "stderr.log", "serial.log", "command.json", "qmp-status.json" }) {
                string src = Path.Combine(safeRunDir, name);
                if (!File.Exists(src)) continue;
                using var inStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var outStream = File.Create(Path.Combine(dest, name));
                await inStream.CopyToAsync(outStream, cancellation).ConfigureAwait(false);
            }
            if (alive) {
                try {
                    using var stDoc = await QmpClient.ExecuteAsync(qmpPort, "query-status", null, cancellation).ConfigureAwait(false);
                    AtomicFileStore.WriteTextAtomicCore(Path.Combine(dest, "qmp-status.json"), stDoc.RootElement.GetRawText());
                    using var blkDoc = await QmpClient.ExecuteAsync(qmpPort, "query-block", null, cancellation).ConfigureAwait(false);
                    AtomicFileStore.WriteTextAtomicCore(Path.Combine(dest, "qmp-block.json"), blkDoc.RootElement.GetRawText());
                    if (screenshot) {
                        using var _ = await QmpClient.ExecuteAsync(qmpPort, "screendump", new {
                            filename = Path.Combine(dest, "guest.ppm"),
                            device = "gfx",
                            head = 0
                        }, cancellation).ConfigureAwait(false);
                    }
                } catch (Exception ex) {
                    File.WriteAllText(Path.Combine(dest, "qmp-error.txt"), ex.Message, AtomicFileStore.Utf8NoBom);
                }
            } else if (screenshot) {
                File.WriteAllText(Path.Combine(dest, "qmp-error.txt"), "VM is stopped; no guest screendump collected.");
            }
        } else if (screenshot) {
            File.WriteAllText(Path.Combine(dest, "qmp-error.txt"), "No VM runtime recorded; no guest screendump collected.");
        }

        string stderrCopy = Path.Combine(dest, "stderr.log");
        if (File.Exists(stderrCopy)) {
            var failures = File.ReadLines(stderrCopy).Where(l => FailureLogLineRegex().IsMatch(l)).ToArray();
            if (failures.Length > 0)
                File.WriteAllLines(Path.Combine(dest, "failures.txt"), failures, AtomicFileStore.Utf8NoBom);
        }

        if (zip) ZipFile.CreateFromDirectory(dest, dest + ".zip");
        return $"Local diagnostics: {dest}. Review logs and host paths before sharing.";
    }
}
