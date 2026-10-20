using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal static class BackendEncodingChecks
{
    // Exercise the shipped PowerShell entrypoint, its real prerequisite JSON
    // generator and the same redirected pipes/parser used by the setup window.
    // Only host facts, package verification and network availability are authored.
    internal static async Task Run(string folder)
    {
        string scripts = Path.Combine(folder, "scripts"), engine = Path.Combine(folder, "日本語 ソフト 表示 engine");
        string runtimes = Path.Combine(folder, "runtimes"), shipped = Path.Combine(AppContext.BaseDirectory, "scripts");
        Directory.CreateDirectory(scripts); Directory.CreateDirectory(Path.Combine(engine, "bin"));
        Directory.CreateDirectory(Path.Combine(runtimes, "python"));
        File.WriteAllText(Path.Combine(engine, "bin/vulkan-1.dll"), "Authored file; never loaded.");
        File.WriteAllText(Path.Combine(runtimes, "python/python.exe"), "Authored file; never executed.");
        File.Copy(Path.Combine(shipped, "manager-backend.ps1"), Path.Combine(scripts, "manager-backend.ps1"));
        File.Copy(Path.Combine(shipped, "install-engine.ps1"), Path.Combine(scripts, "install-engine.ps1"));
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        File.WriteAllText(Path.Combine(scripts, "host-prerequisites.ps1"),
            ". " + Quote(Path.Combine(shipped, "host-prerequisites.ps1")) + "\n" + """
            function Get-ReimsHostFacts {
                @{feature_enabled=$true;whpx_ready=$true;firmware_virtualization=$true;restart_required=$false;
                  vulkan_icd=$true;openssh=$true;usbdk=$false;available_memory_mib=16384;logical_cpus=8}
            }
            function Read-ReimsEngine($Directory,[switch]$VerifyFiles) {
                if (!$VerifyFiles) { throw 'Engine verification was skipped.' }
                @{files=@{'bin/vulkan-1.dll'=('0'*64)}}
            }
            function Get-ReimsQualifiedGuestBridgeLock { @{id=[Console]::In.ReadToEnd()} }
            function Get-ReimsGuestBridgeRelease($Pin) { @{StatusCode=200} }
            """, new UTF8Encoding(false));
        const string diagnostic = "日本語の機能・表示・ソフトを確認できません。";
        File.WriteAllText(Path.Combine(scripts, "network-uplinks.ps1"), "throw " + Quote(diagnostic), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(scripts, "engine-package.ps1"),
            "function Get-ReimsEngineLock { throw " + Quote(diagnostic) + " }", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(scripts, "start.ps1"), "throw 'The fixture must not load a VM.'");
        string wrapper = Path.Combine(folder, "legacy-console.ps1");
        File.WriteAllText(wrapper, """
            param([string]$Scripts,[string]$Root,[string]$ToolsRoot,[int]$CodePage,[string]$Mode)
            $ErrorActionPreference='Stop'
            $OutputEncoding=[Text.Encoding]::GetEncoding($CodePage)
            [Console]::InputEncoding=$OutputEncoding
            [Console]::OutputEncoding=$OutputEncoding
            if ($Mode -eq 'legacy') {
                . "$Scripts/host-prerequisites.ps1"
                Get-ReimsPrerequisites $Root $ToolsRoot 4 4096 | ConvertTo-Json -Depth 6 -Compress
            } elseif ($Mode -eq 'engine-error') {
                & "$Scripts/install-engine.ps1" -Destination (Join-Path $Root 'new-engine')
            } else {
                $action=if ($Mode -eq 'error') {'GetNetworkUplinks'} else {'GetPrerequisites'}
                & "$Scripts/manager-backend.ps1" -Root $Root -ToolsRoot $ToolsRoot -Action $action -Cpus 4 -MemoryMiB 4096
            }
            """, new UTF8Encoding(false));

        Task<string> Run(int codePage, string mode, string input)
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "runtimes/pwsh/pwsh.exe")) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", wrapper,
                "-Scripts", scripts, "-Root", engine, "-ToolsRoot", runtimes, "-CodePage", codePage.ToString(), "-Mode", mode })
                info.ArgumentList.Add(arg);
            return BackendCommand.Run(info, input, readOnly: true);
        }

        // Prove that the fixture catches the user's reported 0xEF error.
        string legacy = await Run(932, "legacy", "fixture");
        try { SetupRequirements.Parse(legacy); throw new Exception("The legacy CP932 pipe did not reproduce the reported JSON failure."); }
        catch (JsonException error) when (error.Message.Contains("0xEF", StringComparison.Ordinal)) { }

        const string payload = """C:\日本語\ソフト\表示 "機能" 😀""";
        foreach (int codePage in new[] { 932, 65001 }) {
            string json = await Run(codePage, "prerequisites", payload);
            var result = SetupRequirements.Parse(json);
            if (!result.Ready || !result.StartReady || result.Checks.Length != 8 ||
                result.Checks.Single(x => x.Id == "vulkan").Message != "Vulkan ICD 登録を検出しました。GPU の実行・性能検証はしていません。" ||
                result.Checks.Single(x => x.Id == "guest_addon").Message != $"資格確認済みの addon {payload} を取得できます。フレームペーシングには制限があります。" || json.Contains('\uFFFD'))
                throw new Exception("Prerequisite JSON, Unicode stdin, or a quoted Japanese path was corrupted by the console code page.");
        }
        foreach (string mode in new[] { "error", "engine-error" }) {
            try { await Run(932, mode, ""); throw new Exception("A failing backend was reported as successful."); }
            catch (IOException error) when (error.Message.Contains(diagnostic, StringComparison.Ordinal) && !error.Message.Contains('\uFFFD')) { }
        }
        File.WriteAllText(Path.Combine(folder, "backend-encoding-result.json"), JsonSerializer.Serialize(new {
            legacy_cp932_reproduces_0xef = true, cp932_prerequisites_roundtrip = true, utf8_prerequisites_roundtrip = true,
            real_prerequisite_messages_preserved = true, unicode_stdin_and_paths_preserved = true,
            manager_error_unicode_preserved = true, engine_error_unicode_preserved = true,
            no_host_probe_network_driver_or_vm_operations = true
        }));
    }
}
