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
}
