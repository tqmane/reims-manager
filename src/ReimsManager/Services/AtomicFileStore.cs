using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReimsManager.Services;

internal static class AtomicFileStore
{
    internal static readonly UTF8Encoding Utf8NoBom = new(false);

    internal static string AssertArtifactPath(string path, string projectRoot, string vmRoot)
    {
        string full = Path.GetFullPath(path);
        string projPrefix = Path.GetFullPath(projectRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string vmPrefix = Path.GetFullPath(vmRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(projPrefix, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(vmPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact must stay under the project: {full}");
        return full;
    }

    internal static void AssertVmOwnedPath(string path, string vmRoot)
    {
        string full = Path.GetFullPath(path);
        string vmPrefix = Path.GetFullPath(vmRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(vmPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"VM input must belong to the selected VM folder: {full}");
    }

    internal static string ReadBoundedText(string path, int maxBytes = 1024 * 1024)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Required file not found.", path);
        if (info.Length > maxBytes) throw new InvalidDataException($"File exceeds size limit ({maxBytes} bytes): {Path.GetFileName(path)}");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > maxBytes) throw new InvalidDataException($"File exceeds size limit ({maxBytes} bytes): {Path.GetFileName(path)}");
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    internal static JsonNode ReadJsonNode(string path, int maxBytes = 1024 * 1024) =>
        JsonNode.Parse(ReadBoundedText(path, maxBytes)) ?? throw new InvalidDataException($"Empty JSON in {Path.GetFileName(path)}");

    internal static void WriteJsonAtomic(string path, JsonNode value, string projectRoot, string vmRoot)
    {
        string target = AssertArtifactPath(path, projectRoot, vmRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string json = value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteTextAtomicCore(target, json);
    }

    internal static void WriteVmJsonAtomic(string path, object value, string projectRoot, string vmRoot)
    {
        string target = AssertArtifactPath(path, projectRoot, vmRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        WriteTextAtomicCore(target, json);
    }

    internal static void WriteTextAtomicCore(string fullPath, string content)
    {
        byte[] bytes = Utf8NoBom.GetBytes(content);
        string temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        } finally {
            if (File.Exists(temporary)) {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    internal static string VmMutexIdentity(string vmRoot)
    {
        string canonical = Path.GetFullPath(vmRoot).TrimEnd('\\', '/').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static T WithNamedMutex<T>(string mutexName, int timeoutMs, string busyMessage, Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        bool locked = false;
        try {
            try { locked = mutex.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException(busyMessage);
            return action();
        } finally {
            if (locked) mutex.ReleaseMutex();
        }
    }

    internal static void WithNamedMutex(string mutexName, int timeoutMs, string busyMessage, Action action)
    {
        WithNamedMutex(mutexName, timeoutMs, busyMessage, () => { action(); return true; });
    }

    internal static JsonObject ReadVmConfig(string vmDirectory)
    {
        string path = Path.Combine(vmDirectory, "state", "config.json");
        if (!File.Exists(path)) throw new InvalidOperationException("先に VM のセットアップを完了してください。");
        var doc = ReadJsonNode(path).AsObject();
        if (doc["schema"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Unknown VM configuration schema.");
        return doc;
    }

    internal static (JsonObject? Runtime, bool Alive, int QmpPort) ReadLiveRuntime(string vmDirectory)
    {
        string path = Path.Combine(vmDirectory, "state", "runtime.json");
        if (!File.Exists(path)) return (null, false, 0);
        var rt = ReadJsonNode(path).AsObject();
        int pid = rt["pid"]?.GetValue<int>() ?? 0;
        long ticks = rt["start_ticks"]?.GetValue<long>() ?? 0;
        string exe = rt["executable"]?.GetValue<string>() ?? "";
        bool alive = ProcessIdentity.IsAlive(pid, ticks, exe);
        return (rt, alive, rt["qmp_port"]?.GetValue<int>() ?? 0);
    }

    internal static void AssertVmStopped(string vmDirectory)
    {
        var (_, alive, _) = ReadLiveRuntime(vmDirectory);
        if (alive) throw new InvalidOperationException("Stop this VM with Exit before changing its disks or firmware.");
    }

    internal static void AssertNoReparsePointsInTree(string rootPath)
    {
        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Reparse points are not permitted: {rootPath}");
        if (!Directory.Exists(rootPath)) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories)) {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Reparse points are not permitted: {entry}");
        }
    }
}
