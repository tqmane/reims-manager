using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal static class OnboardingChecks
{
    internal static string Requirements(int cpus = 4, int memory = 4096, int? available = 8192, bool whpx = true, bool restart = false, bool ready = true) => JsonSerializer.Serialize(new {
        ready, restart_required = restart,
        checks = new object[] {
            new { id = "whpx", label = "Windows Hypervisor Platform", ready = whpx, message = "Synthetic prerequisite", action = "EnableWhpx" },
            new { id = "vulkan", label = "Vulkan driver", ready = true, message = "Synthetic ICD registration" },
            new { id = "openssh", label = "OpenSSH", ready = true, message = "Synthetic SSH tools" },
            new { id = "engine", label = "Pinned engine / firmware", ready = true, message = "Synthetic immutable inventory" },
            new { id = "runtimes", label = "Manager runtimes", ready = true, message = "Synthetic bundled tools" },
            new { id = "guest_addon", label = "Qualified addon", ready = true, message = "Synthetic release availability" },
            new { id = "usb", label = "USB (optional)", required = false, ready = false, message = "Synthetic optional item", action = "InstallUsbDk" },
            new { id = "capacity", label = "Memory / CPU plan", required = false, ready = available >= memory + 1024, message = "Synthetic current free memory" }
        },
        plan = new { cpus, memory_mib = memory, available_memory_mib = available, start_ready = true }
    });

    internal const string Network = """
        {"schema":1,"uplinks":[{"id":"AAAAAAAA-1111-4111-8111-111111111111","name":"Wireless","description":"Synthetic WLAN","kind":"wifi","status":"Up","connected":true,"bridge_support":"unverified"},{"id":"bbbbbbbb-2222-4222-8222-222222222222","name":"Wired LAN","description":"Synthetic Ethernet","kind":"ethernet","status":"Disconnected","connected":false,"bridge_support":"unverified"}],"tap_adapters":[{"id":"cccccccc-3333-4333-8333-333333333333","name":"Dedicated test TAP","description":"Synthetic TAP","status":"Disconnected"}]}
        """;

    internal static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (string state in new[] { "unknown", "running", "shutdown", "paused", "suspended", "prelaunch", "unprepared", "stopped" })
            if (VmActionPolicy.CanRun("RemoveBridge",state,true,true) != (state == "stopped") ||
                VmActionPolicy.CanRun("RemoveBridge",state,false,true) || VmActionPolicy.CanRun("RemoveBridge",state,true,false))
                throw new Exception("Bridge removal bypassed its fully stopped VM/engine policy.");
        var ready = SetupRequirements.Parse(Requirements());
        if (!ready.Ready || !ready.StartReady || ready.Checks.Single(value => value.Id == "usb").Required)
            throw new Exception("Optional USB blocked basic setup.");
        if (SetupRequirements.Parse(Requirements(whpx: false)).Ready || SetupRequirements.Parse(Requirements(ready: false)).Ready)
            throw new Exception("A missing required/default-required prerequisite was bypassed.");
        foreach (string json in new[] { Requirements(available: 2048), Requirements(available: null), Requirements(restart: true) }) {
            var value = SetupRequirements.Parse(json);
            if (!value.Ready || value.StartReady) throw new Exception("Low/unknown RAM or a pending restart was automatically started.");
        }
        try { SetupRequirements.Parse(Requirements(memory: 4097)); throw new Exception("Invalid memory plan was accepted."); }
        catch (InvalidDataException) { }
        var inventory = NetworkInventory.Parse(Network);
        if (inventory.Uplinks[0].Id != "aaaaaaaa-1111-4111-8111-111111111111" || !inventory.Uplinks[0].DisplayName.Contains("Wi-Fi") ||
            !inventory.Uplinks[1].DisplayName.Contains("有線 LAN") || inventory.Uplinks[1].Connected || inventory.Taps.Single() != "Dedicated test TAP")
            throw new Exception("Physical friendly names/GUIDs were confused with internal TAP choices.");
        string parent = Path.Combine(folder, "日本語 VM library");
        var planned = VmLibrary.PlanCreate(parent, "Example macOS");
        if (planned.Directory != Path.Combine(parent, "Example macOS") || Path.GetDirectoryName(planned.File) != planned.Directory || Directory.Exists(parent))
            throw new Exception("Planning a VM wrote data or used the working directory.");
        foreach (string name in new[] { "..", "CON", "folder/name", "LPT1.txt", "tail.", "NUL" }) {
            try { VmLibrary.PlanCreate(parent, name); throw new Exception("Invalid VM name was accepted: " + name); }
            catch (ArgumentException) { }
        }
        try { VmLibrary.PlanCreate("relative-folder", "Example"); throw new Exception("Relative VM destination was accepted."); }
        catch (ArgumentException) { }
        if (!Path.IsPathFullyQualified(VmLibrary.DefaultRoot) || !EngineSetupWindow.DefaultDestination.StartsWith(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reims", "Engines") + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new Exception("Product defaults depend on a checkout or working directory.");
        File.WriteAllText(Path.Combine(folder, "onboarding-result.json"), JsonSerializer.Serialize(new {
            optional_usb_nonblocking = true, missing_required_rejected = true, low_memory_creation_allowed = true,
            auto_start_capacity_guard = true, restart_defers_start = true, hardware_plan_validation = true,
            physical_guid_and_friendly_names = true, tap_choices_separate = true, vm_path_plan_no_writes = true,
            windows_reserved_names_rejected = true, ordinary_user_defaults = true
        }));
    }
}
