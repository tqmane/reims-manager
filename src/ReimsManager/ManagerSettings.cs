using System.IO;
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
    private static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    internal static ManagerSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(SettingsFile)) return new();
        try {
            var settings = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(SettingsFile))
                ?? throw new JsonException("Empty settings");
            settings.EngineRoot ??= "";
            settings.QemuExecutable ??= "";
            settings.VirtualMachines ??= [];
            return settings;
        } catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) {
            warning = "アプリの設定を読み込めませんでした。VM のファイルから開き直せます。";
            return new();
        }
    }

    internal void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsFile, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
