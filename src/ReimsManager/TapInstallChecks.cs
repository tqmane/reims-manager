using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal static class TapInstallChecks
{
    internal static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder,"tap-install-result.json");
        const string valid = """
            {"schema":1,"created":true,"reused":false,"owned":true,"adapter_id":"11111111-1111-4111-8111-111111111111","adapter_name":"Reims TAP 11111111","pnp_instance_id":"ROOT\\NET\\0001","restart_required":false,"bridge_created":false}
            """;
        File.WriteAllText(path,valid);
        var result=TapInstallResult.Read(path,DateTime.UtcNow.AddSeconds(-10));
        if (!result.Owned || result.RestartRequired || result.AdapterName != "Reims TAP 11111111") throw new Exception("Fresh TAP result was not read.");
        Reject(()=>TapInstallResult.Read(path,DateTime.UtcNow.AddMinutes(1)));
        foreach (string json in new[] {
            valid.Replace("\"schema\":1","\"schema\":2"),valid.Replace("\"bridge_created\":false","\"bridge_created\":true"),
            valid.Replace("\"restart_required\":false","\"restart_required\":\"false\""),valid.Replace("\"owned\":true","\"owned\":false"),
            valid.Replace("\"reused\":false","\"reused\":true"),valid.Replace("11111111-1111-4111-8111-111111111111","invalid-guid"),
            valid.Replace("\"adapter_name\":\"Reims TAP 11111111\"","\"adapter_name\":null"),
            valid.Replace("Reims TAP 11111111","Reims TAP\\n11111111")
        }) { File.WriteAllText(path,json);Reject(()=>TapInstallResult.Read(path,DateTime.MinValue)); }
        File.WriteAllText(path,valid.Replace("\"adapter_id\":\"11111111-1111-4111-8111-111111111111\"","\"adapter_id\":null")
            .Replace("\"adapter_name\":\"Reims TAP 11111111\"","\"adapter_name\":null").Replace("\"restart_required\":false","\"restart_required\":true"));
        result=TapInstallResult.Read(path,DateTime.MinValue);
        if (!result.RestartRequired || result.AdapterId is not null) throw new Exception("Pending reboot result was treated as a ready adapter.");
        File.WriteAllText(path,new string(' ',65537));Reject(()=>TapInstallResult.Read(path,DateTime.MinValue));
        File.WriteAllText(Path.Combine(folder,"tap-result-check.json"),"{\"fresh_success_only\":true,\"bounded_64k\":true,\"schema_and_fields_validated\":true,\"pending_restart_supported\":true}");
    }

    private static void Reject(Action read)
    {
        try { read();throw new Exception("Invalid/stale TAP result was accepted."); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or KeyNotFoundException or JsonException) { }
    }
}
