using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ReimsManager.Services;

internal static class NativeToolRunner
{
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromSeconds(1800) };

    internal sealed record ToolOutput(int ExitCode, string StdOut, string StdErr);

    internal static bool IsPortableEngine(string engineRoot) =>
        File.Exists(Path.Combine(engineRoot, "reims-engine.json"));

    internal static string ResolveEngineTool(string engineRoot, string keyOrExe)
    {
        string manifestPath = Path.Combine(engineRoot, "reims-engine.json");
        if (File.Exists(manifestPath)) {
            string pathKey = keyOrExe switch {
                "qemu-system-x86_64.exe" or "qemu" => "qemu",
                "qemu-img.exe" or "qemu_img" => "qemu_img",
                "qemu-io.exe" or "qemu_io" => "qemu_io",
                "7z.exe" or "seven_zip" => "seven_zip",
                _ => keyOrExe
            };
            var doc = AtomicFileStore.ReadJsonNode(manifestPath, 4 * 1024 * 1024).AsObject();
            var paths = doc["paths"]?.AsObject() ?? throw new InvalidDataException("Missing engine paths.");
            string rel = paths[pathKey]?.GetValue<string>() ?? throw new InvalidDataException($"Engine component is missing: {pathKey}");
            string full = NativeStatusService.JoinPackagePath(engineRoot, rel);
            if (!File.Exists(full)) throw new FileNotFoundException($"Engine tool missing: {rel}", full);
            return full;
        }

        string exeName = keyOrExe switch {
            "qemu" => "qemu-system-x86_64.exe",
            "qemu_img" => "qemu-img.exe",
            "qemu_io" => "qemu-io.exe",
            "seven_zip" => "7z.exe",
            _ => keyOrExe
        };
        string built = Path.Combine(engineRoot, "build", "qemu", exeName);
        if (exeName.StartsWith("qemu-", StringComparison.OrdinalIgnoreCase) && File.Exists(built))
            return built;
        string? msysRoot = Environment.GetEnvironmentVariable("REIMS_MSYS2_ROOT");
        if (string.IsNullOrWhiteSpace(msysRoot))
            msysRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "msys64");
        var candidates = new List<string> {
            Path.Combine(msysRoot, "ucrt64", "bin", exeName),
            Path.Combine(msysRoot, "mingw64", "bin", exeName)
        };
        if (exeName.Equals("7z.exe", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"));
        foreach (string candidate in candidates) {
            if (File.Exists(candidate)) return candidate;
        }
        return exeName;
    }

    internal static string ResolvePythonExecutable(string engineRoot, string toolsRoot)
    {
        string bundled = Path.Combine(toolsRoot, "python", "python.exe");
        if (File.Exists(bundled)) return bundled;
        if (IsPortableEngine(engineRoot))
            throw new FileNotFoundException("Bundled Python is missing. Re-extract the complete Manager package.", bundled);
        string? msysRoot = Environment.GetEnvironmentVariable("REIMS_MSYS2_ROOT");
        if (string.IsNullOrWhiteSpace(msysRoot))
            msysRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "msys64");
        string msysPy = Path.Combine(msysRoot, "ucrt64", "bin", "python.exe");
        return File.Exists(msysPy) ? msysPy : "python.exe";
    }

    internal static string ResolveOpenSshKeyscan()
    {
        string sys = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keyscan.exe");
        if (File.Exists(sys)) return sys;
        string? msysRoot = Environment.GetEnvironmentVariable("REIMS_MSYS2_ROOT");
        if (string.IsNullOrWhiteSpace(msysRoot))
            msysRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "msys64");
        string msys = Path.Combine(msysRoot, "usr", "bin", "ssh-keyscan.exe");
        if (File.Exists(msys)) return msys;
        return "ssh-keyscan.exe";
    }

    internal static async Task<ToolOutput> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        string? stdinText = null,
        bool isolatedSystemPath = false,
        int timeoutMs = 180_000,
        CancellationToken cancellation = default)
    {
        var info = new ProcessStartInfo(executable) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinText is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            info.WorkingDirectory = workingDirectory;
        if (stdinText is not null)
            info.StandardInputEncoding = new UTF8Encoding(false);
        if (isolatedSystemPath) {
            string sys32 = Environment.SystemDirectory;
            string win = Path.GetDirectoryName(sys32)!;
            info.Environment["PATH"] = $"{sys32};{win}";
        }
        foreach (string arg in arguments) info.ArgumentList.Add(arg);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeoutMs);
        using var process = Process.Start(info) ?? throw new IOException($"Cannot start native tool: {Path.GetFileName(executable)}");
        using var killReg = cts.Token.Register(() => {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        Task<string> outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        Task<string> errTask = process.StandardError.ReadToEndAsync(cts.Token);
        if (stdinText is not null) {
            await process.StandardInput.WriteAsync(stdinText.AsMemory(), cts.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);
        if (process.ExitCode == -1073741515)
            throw new IOException($"Native DLL dependency is missing for {executable}. Reinstall the complete engine package.");
        return new ToolOutput(process.ExitCode, stdout, stderr);
    }

    internal static async Task<string> RunCheckedAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        string? stdinText = null,
        bool isolatedSystemPath = false,
        int timeoutMs = 180_000,
        CancellationToken cancellation = default)
    {
        var result = await RunAsync(executable, arguments, workingDirectory, stdinText, isolatedSystemPath, timeoutMs, cancellation).ConfigureAwait(false);
        if (result.ExitCode != 0) {
            string msg = string.Join(" ", new[] { result.StdOut.Trim(), result.StdErr.Trim() }.Where(s => s.Length > 0));
            throw new IOException($"{Path.GetFileName(executable)} failed ({result.ExitCode}): {msg}".Trim());
        }
        return result.StdOut;
    }

    internal static async Task DownloadVerifiedAsync(
        string url,
        string? expectedSha256,
        long expectedBytes,
        string destination,
        Action<string, double?>? progress = null,
        CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("An HTTPS release URL is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (!File.Exists(destination)) {
            string partial = $"{destination}.{Guid.NewGuid():N}.partial";
            try {
                using var response = await DownloadClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength ?? (expectedBytes > 0 ? expectedBytes : null);
                using (var src = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
                using (var dst = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)) {
                    byte[] buffer = new byte[81920];
                    long copied = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0) {
                        await dst.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                        copied += read;
                        double? pct = total > 0 ? Math.Min(100.0, copied * 100.0 / total.Value) : null;
                        progress?.Invoke($"{copied / (1024.0 * 1024.0):0.1} MB" + (total > 0 ? $" / {total.Value / (1024.0 * 1024.0):0.1} MB" : ""), pct);
                    }
                    await dst.FlushAsync(cancellation).ConfigureAwait(false);
                }
                if (expectedBytes > 0 && new FileInfo(partial).Length != expectedBytes)
                    throw new InvalidDataException("Release download size mismatch.");
                if (!string.IsNullOrEmpty(expectedSha256) &&
                    !string.Equals(ComputeSha256Lower(partial), expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Download SHA256 mismatch.");
                File.Move(partial, destination, overwrite: false);
            } finally {
                if (File.Exists(partial)) {
                    try { File.Delete(partial); } catch { }
                }
            }
        }

        if (expectedBytes > 0 && new FileInfo(destination).Length != expectedBytes)
            throw new InvalidDataException("Cached release size mismatch.");
        if (!string.IsNullOrEmpty(expectedSha256) &&
            !string.Equals(ComputeSha256Lower(destination), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cached download SHA256 mismatch.");
    }

    internal static void ExpandZipSafely(string archivePath, string destinationDir, CancellationToken cancellation = default)
    {
        if (Directory.Exists(destinationDir)) throw new IOException("Extraction directory already exists.");
        using var zip = ZipFile.OpenRead(archivePath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        const long maxTotalBytes = 4L * 1024 * 1024 * 1024;
        foreach (var entry in zip.Entries) {
            cancellation.ThrowIfCancellationRequested();
            string name = entry.FullName.TrimEnd('/');
            _ = NativeStatusService.JoinPackagePath(destinationDir, name);
            bool isSymlink = ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
            if (!seen.Add(name) || isSymlink)
                throw new InvalidDataException($"Duplicate or symlink ZIP entry: {name}");
            if (entry.Length < 0)
                throw new InvalidDataException($"Invalid ZIP entry size: {name}");
            total += entry.Length;
            if (total > maxTotalBytes || seen.Count > 20000)
                throw new InvalidDataException("Package exceeds the extraction limit.");
        }
        Directory.CreateDirectory(destinationDir);
        foreach (var entry in zip.Entries) {
            cancellation.ThrowIfCancellationRequested();
            string target = NativeStatusService.JoinPackagePath(destinationDir, entry.FullName.TrimEnd('/'));
            if (entry.FullName.EndsWith('/')) {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    internal static string ComputeSha256Lower(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static string ComputeSha256Upper(string path) => ComputeSha256Lower(path).ToUpperInvariant();
}
