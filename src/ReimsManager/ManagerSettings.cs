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

    internal static string DataDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("REIMS_MANAGER_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reims", "Manager"));
    internal static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    private string originalEngine = "", originalQemu = "";
    private string? originalSelection;

    private void Remember()
    {
        originalEngine = EngineRoot; originalQemu = QemuExecutable; originalSelection = SelectedVm;
    }

    private static ManagerSettings Read(string file)
    {
        if (new FileInfo(file).Length > 1024 * 1024) throw new JsonException("Settings exceed 1 MiB");
        var settings = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(file)) ?? throw new JsonException("Empty settings");
        settings.EngineRoot ??= ""; settings.QemuExecutable ??= ""; settings.VirtualMachines ??= [];
        settings.VirtualMachines = settings.VirtualMachines.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.Remember();
        return settings;
    }

    internal static ManagerSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(SettingsFile)) return new();
        try {
            return Read(SettingsFile);
        } catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) {
            warning = "アプリ設定を読み込めません。元のファイルは保持しています。「登録・保存先」から設定を取り込めます。";
            try {
                var backup = Read(SettingsFile + ".backup.json");
                warning = "アプリ設定のバックアップを復元しました。読み込めなかった元のファイルは保持しています。";
                return backup;
            } catch (Exception backupError) when (backupError is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }
    }

    internal void Import(string file)
    {
        var imported = Read(Path.GetFullPath(file));
        if (EngineRoot.Length == 0) { EngineRoot = imported.EngineRoot; QemuExecutable = imported.QemuExecutable; }
        SelectedVm ??= imported.SelectedVm;
        VirtualMachines = VirtualMachines.Concat(imported.VirtualMachines).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
    }

    internal void Save()
    {
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
            bool unreadable = false;
            if (File.Exists(SettingsFile)) {
                try { latest = Read(SettingsFile); }
                catch (JsonException) {
                    unreadable = true;
                    File.Copy(SettingsFile, SettingsFile + ".unreadable-" + Guid.NewGuid().ToString("N") + ".json", false);
                }
            }
            // Merge registrations and only the fields this instance changed since loading.
            bool engineChanged = EngineRoot != originalEngine || QemuExecutable != originalQemu;
            var merged = new ManagerSettings {
                EngineRoot = !engineChanged && latest is not null ? latest.EngineRoot : EngineRoot,
                QemuExecutable = !engineChanged && latest is not null ? latest.QemuExecutable : QemuExecutable,
                SelectedVm = SelectedVm == originalSelection && latest is not null ? latest.SelectedVm : SelectedVm,
                VirtualMachines = (latest?.VirtualMachines ?? []).Concat(VirtualMachines).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, merged, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(SettingsFile)) File.Replace(temporary, SettingsFile, unreadable ? null : SettingsFile + ".backup.json");
            else File.Move(temporary, SettingsFile);
            VirtualMachines = merged.VirtualMachines;
            Remember();
        } finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
