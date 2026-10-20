using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal sealed class ManagerSettings
{
    public string EngineRoot { get; set; } = "";
    public string QemuExecutable { get; set; } = "";
    public string? SelectedVm { get; set; }
    public List<string> VirtualMachines { get; set; } = [];
    public Dictionary<string, bool> ClipboardByVm { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static string DataDirectory {
        get {
            string? configured = Environment.GetEnvironmentVariable("REIMS_MANAGER_HOME");
            return Path.GetFullPath(!string.IsNullOrWhiteSpace(configured) ? configured :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reims", "Manager"));
        }
    }
    internal static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    private static string BackupFile => SettingsFile + ".backup.json";
    private const long MaximumBytes = 1024 * 1024;
    private string originalEngine = "", originalQemu = "";
    private string? originalSelection;
    private Dictionary<string, bool> originalClipboard = new(StringComparer.OrdinalIgnoreCase);
    internal string? LastSaveWarning { get; private set; }

    internal bool ClipboardEnabled(string vmFile) => ClipboardByVm.TryGetValue(vmFile, out bool enabled) && enabled;
    internal void SetClipboardEnabled(string vmFile, bool enabled) => ClipboardByVm[vmFile] = enabled;

    internal void SynchronizeFrom(ManagerSettings saved, bool adoptEngine)
    {
        if (adoptEngine) {
            EngineRoot = originalEngine = saved.EngineRoot;
            QemuExecutable = originalQemu = saved.QemuExecutable;
        }
        VirtualMachines = VirtualMachines.Concat(saved.VirtualMachines).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ClipboardByVm = new(saved.ClipboardByVm, StringComparer.OrdinalIgnoreCase);
        originalClipboard = new(ClipboardByVm, StringComparer.OrdinalIgnoreCase);
        // The active VM and its selection baseline belong to this window.
    }

    private void Remember()
    {
        originalEngine = EngineRoot; originalQemu = QemuExecutable; originalSelection = SelectedVm;
        originalClipboard = new(ClipboardByVm, StringComparer.OrdinalIgnoreCase);
    }

    private static ManagerSettings Read(string file)
    {
        if (new FileInfo(file).Length > MaximumBytes) throw new JsonException("Settings exceed 1 MiB");
        var settings = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(file)) ?? throw new JsonException("Empty settings");
        settings.EngineRoot ??= ""; settings.QemuExecutable ??= ""; settings.VirtualMachines ??= [];
        settings.VirtualMachines = settings.VirtualMachines.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var clipboard = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.ClipboardByVm ?? [])
            if (!string.IsNullOrWhiteSpace(pair.Key)) clipboard[pair.Key] = pair.Value;
        settings.ClipboardByVm = clipboard;
        settings.Remember();
        return settings;
    }

    internal static ManagerSettings Load(out string? warning)
    {
        warning = null;
        try {
            return Read(SettingsFile);
        } catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) {
            bool missing = error is FileNotFoundException or DirectoryNotFoundException;
            if (!missing) warning = "アプリ設定を読み込めません。元のファイルは保持しています。「登録・保存先」から設定を取り込めます。";
            try {
                var backup = Read(BackupFile);
                warning = missing ? "アプリ設定のファイルが見つからないため、バックアップから設定を復元しました。" :
                    "アプリ設定のバックアップを復元しました。読み込めなかった元のファイルは保持しています。";
                return backup;
            } catch (Exception backupError) when (backupError is IOException or JsonException or UnauthorizedAccessException) {
                if (missing && backupError is not (FileNotFoundException or DirectoryNotFoundException))
                    warning = "アプリ設定のファイルが見つからず、バックアップも読み込めません。バックアップは保持しています。";
                return new();
            }
        }
    }

    internal void Import(string file)
    {
        var imported = Read(Path.GetFullPath(file));
        // Do not change the active window until the imported settings reach disk.
        var candidate = new ManagerSettings {
            EngineRoot = EngineRoot.Length == 0 ? imported.EngineRoot : EngineRoot,
            QemuExecutable = EngineRoot.Length == 0 ? imported.QemuExecutable : QemuExecutable,
            SelectedVm = SelectedVm ?? imported.SelectedVm,
            VirtualMachines = VirtualMachines.Concat(imported.VirtualMachines).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ClipboardByVm = new(ClipboardByVm, StringComparer.OrdinalIgnoreCase),
            originalEngine = originalEngine, originalQemu = originalQemu, originalSelection = originalSelection,
            originalClipboard = new(originalClipboard, StringComparer.OrdinalIgnoreCase)
        };
        foreach (var pair in imported.ClipboardByVm) candidate.ClipboardByVm.TryAdd(pair.Key, pair.Value);
        candidate.Save();
        EngineRoot = candidate.EngineRoot; QemuExecutable = candidate.QemuExecutable; SelectedVm = candidate.SelectedVm;
        VirtualMachines = candidate.VirtualMachines; ClipboardByVm = candidate.ClipboardByVm;
        LastSaveWarning = candidate.LastSaveWarning;
        Remember();
    }

    private static void PreserveUnreadable(string file) => File.Copy(file, file + ".unreadable-" + Guid.NewGuid().ToString("N") + ".json", false);

    private static ManagerSettings? ReadBackupForSave()
    {
        try { return Read(BackupFile); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (JsonException) { PreserveUnreadable(BackupFile); return null; }
    }

    private static void SeedBackup(string source)
    {
        string temporary = BackupFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                input.CopyTo(output); output.Flush(flushToDisk: true);
            }
            File.Move(temporary, BackupFile, overwrite: true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void Save()
    {
        LastSaveWarning = null;
        Directory.CreateDirectory(DataDirectory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SettingsFile.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\ReimsManager.Settings." + key);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("別の Reims Manager が設定を保存しています。少し待って再試行してください。");
        string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            ManagerSettings? latest = null;
            bool primaryReadable = false;
            try { latest = Read(SettingsFile); primaryReadable = true; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) {
                latest = ReadBackupForSave();
            } catch (JsonException) {
                PreserveUnreadable(SettingsFile);
                latest = ReadBackupForSave();
            }
            // Merge registrations and only the fields this instance changed since loading.
            bool engineChanged = EngineRoot != originalEngine || QemuExecutable != originalQemu;
            var clipboard = new Dictionary<string, bool>(latest?.ClipboardByVm ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ClipboardByVm)
                if (latest is null || !originalClipboard.TryGetValue(pair.Key, out bool previous) || pair.Value != previous)
                    clipboard[pair.Key] = pair.Value;
            var merged = new ManagerSettings {
                EngineRoot = !engineChanged && latest is not null ? latest.EngineRoot : EngineRoot,
                QemuExecutable = !engineChanged && latest is not null ? latest.QemuExecutable : QemuExecutable,
                SelectedVm = SelectedVm == originalSelection && latest is not null ? latest.SelectedVm : SelectedVm,
                VirtualMachines = (latest?.VirtualMachines ?? []).Concat(VirtualMachines).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ClipboardByVm = clipboard
            };
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, merged, new JsonSerializerOptions { WriteIndented = true });
                if (stream.Length > MaximumBytes) throw new IOException("アプリ設定が保存可能なサイズを超えています。元の設定は保持しています。");
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(SettingsFile)) File.Replace(temporary, SettingsFile, primaryReadable ? BackupFile : null);
            else File.Move(temporary, SettingsFile);
            VirtualMachines = merged.VirtualMachines;
            ClipboardByVm = merged.ClipboardByVm;
            Remember();
            // A rejected primary write must never leave a recoverable candidate.
            // Seed the first backup only after the settings themselves are committed.
            if (!primaryReadable && latest is null) {
                try { SeedBackup(SettingsFile); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    LastSaveWarning = "アプリ設定は保存しましたが、復元用のバックアップを作成できませんでした。\n" + error.Message;
                }
            }
        } finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
