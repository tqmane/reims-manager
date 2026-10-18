using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal sealed record VmEntry(string Name, string File, string Directory, string? Problem = null)
{
    public bool IsAvailable => Problem is null;
    public string Subtitle => IsAvailable ? Directory : "見つからない / 読み込めない · " + Directory;
    public override string ToString() => Name;
}

internal static class VmLibrary
{
    internal static VmEntry Registered(string file)
    {
        try { return Open(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException) {
            string name = file, directory = "";
            try { name = Path.GetFileNameWithoutExtension(file); directory = Path.GetDirectoryName(Path.GetFullPath(file))!; }
            catch (ArgumentException) { }
            return new(name, file, directory, error.Message);
        }
    }

    private static VmEntry Validate(string file, string name, string directory)
    {
        file = Path.GetFullPath(file);
        string package = Path.GetDirectoryName(file)!;
        directory = Path.GetFullPath(directory, package);
        if (!string.Equals(directory, package, StringComparison.OrdinalIgnoreCase) &&
            !directory.StartsWith(package.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VM のデータは VM ファイルと同じフォルダー内に置いてください。");
        if (string.IsNullOrWhiteSpace(name) || directory.TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(directory)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidDataException("VM の名前または保存先が不正です。");
        return new(name, file, directory);
    }

    internal static VmEntry Open(string file)
    {
        file = Path.GetFullPath(file);
        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var value = json.RootElement;
        if (value.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("この VM ファイルの形式には対応していません。");
        return Validate(file, value.GetProperty("name").GetString() ?? "", value.GetProperty("directory").GetString() ?? throw new InvalidDataException("VM のデータ保存先がありません。"));
    }

    internal static VmEntry Create(string file, string directory)
    {
        var entry = Validate(file, Path.GetFileNameWithoutExtension(file), directory);
        if (System.IO.File.Exists(entry.File)) throw new IOException("VM ファイルは既に存在します。");
        if (System.IO.File.Exists(entry.Directory) || System.IO.Directory.Exists(entry.Directory) && System.IO.Directory.EnumerateFileSystemEntries(entry.Directory).Any())
            throw new IOException("この保存先には既にデータがあります。別の VM 名を選択してください。");
        string parent = Path.GetDirectoryName(entry.File)!;
        string relative = Path.GetRelativePath(parent, entry.Directory);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, name = entry.Name, directory = relative });
        System.IO.Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, ".reims-create-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(content); stream.Flush(flushToDisk: true);
            }
            System.IO.File.Move(temporary, entry.File, overwrite: false);
        } finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
        return entry;
    }
}
