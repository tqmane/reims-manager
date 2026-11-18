using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ReimsManager.Services;

internal static class ActionRouter
{
    internal static async Task<string> ExecuteAsync(
        string action,
        string[] arguments,
        string engineRoot,
        string vmDirectory,
        bool hasVm,
        string qemuExecutable,
        bool elevate = false,
        string? input = null,
        Action<string>? logWarning = null,
        CancellationToken cancellation = default)
    {
        string toolsRoot = Path.Combine(AppContext.BaseDirectory, "runtimes");

        if (!elevate) {
            Task<string>? nativeTask = TryRunNativeAsync(action, arguments, engineRoot, vmDirectory, toolsRoot, cancellation);
            if (nativeTask is not null)
                return await nativeTask.ConfigureAwait(false);
        }

        string backend = Path.Combine(AppContext.BaseDirectory, "scripts", "manager-backend.ps1");
        if (!File.Exists(backend)) backend = Path.Combine(engineRoot, "scripts", "windows", "manager-backend.ps1");
        if (!File.Exists(backend)) throw new FileNotFoundException(UiText.T("管理スクリプトが見つかりません。Reims のプロジェクトを選択してください。"));

        string pwsh = NativeUsbService.FindPwshExecutable();
        if (!File.Exists(pwsh)) {
            string programFilesPwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            if (File.Exists(programFilesPwsh)) pwsh = programFilesPwsh;
            else throw new FileNotFoundException(UiText.T("PowerShell 7 をインストールしてください。"));
        }

        var info = new ProcessStartInfo(pwsh) {
            UseShellExecute = elevate,
            CreateNoWindow = true,
            WorkingDirectory = engineRoot
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", backend, "-Root", engineRoot, "-Action", action }.Concat(arguments))
            info.ArgumentList.Add(arg);
        if (hasVm && action is not ("GetPrerequisites" or "GetNetworkUplinks" or "EnableWhpx")) {
            info.ArgumentList.Add("-VmDirectory");
            info.ArgumentList.Add(vmDirectory);
        }
        info.ArgumentList.Add("-Qemu");
        info.ArgumentList.Add(qemuExecutable);
        info.ArgumentList.Add("-ToolsRoot");
        info.ArgumentList.Add(toolsRoot);

        if (input is not null) {
            info.RedirectStandardInput = true;
            info.StandardInputEncoding = new UTF8Encoding(false);
        }
        if (elevate) {
            info.Verb = "runas";
            info.WindowStyle = ProcessWindowStyle.Hidden;
        } else {
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
        }

        bool readOnly = action is "Status" or "GetIdentity" or "ListUsb" or "GetPrerequisites" or "GetNetworkUplinks";
        string? usbResultPath = null;
        try {
            if (action == "InstallUsbDk") {
                string support = Path.Combine(ManagerSettings.DataDirectory, "support");
                Directory.CreateDirectory(support);
                usbResultPath = Path.Combine(support, $"usb-install-{Guid.NewGuid():N}.json");
                using (File.Open(usbResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                info.ArgumentList.Add("-UsbResultPath");
                info.ArgumentList.Add(usbResultPath);
            }
            string output = await BackendCommand.Run(info, input, readOnly, readOnly ? cancellation : CancellationToken.None).ConfigureAwait(false);
            if (usbResultPath is not null) {
                var installResult = UsbDriverInstallResult.Read(usbResultPath);
                return installResult.ToJson();
            }
            return output;
        } finally {
            if (usbResultPath is not null) {
                try { File.Delete(usbResultPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    logWarning?.Invoke(UiText.T("UsbDk の一時結果ファイルを削除できませんでした: ") + error.Message + "\n");
                }
            }
        }
    }

    private static Task<string>? TryRunNativeAsync(
        string action,
        string[] arguments,
        string engineRoot,
        string vmDirectory,
        string toolsRoot,
        CancellationToken cancellation)
    {
        switch (action) {
            case "Status":
                return NativeStatusService.GetStatusJsonAsync(engineRoot, vmDirectory, cancellation);
            case "GetNetworkUplinks":
                return Task.Run(NativeHostNetwork.GetNetworkInventoryJson, cancellation);
            case "GetPrerequisites":
                return NativeStatusService.GetPrerequisitesJsonAsync(
                    engineRoot,
                    toolsRoot,
                    GetIntArg(arguments, "-Cpus", 8),
                    GetIntArg(arguments, "-MemoryMiB", 8192),
                    cancellation);
            case "GetIdentity":
                return Task.Run(() => {
                    var id = NativeOpenCore.ReadIdentity(engineRoot, vmDirectory);
                    return JsonSerializer.Serialize(new { model = id.Model, serial = id.Serial, mlb = id.Mlb, uuid = id.Uuid, rom = id.Rom });
                }, cancellation);
            case "ListUsb":
                return NativeUsbService.ListUsbJsonAsync(engineRoot, vmDirectory, cancellation);
            case "AttachUsb":
                return NativeUsbService.AttachUsbAsync(engineRoot, toolsRoot, vmDirectory, GetArg(arguments, "-UsbId"), cancellation);
            case "DetachUsb":
                return NativeUsbService.DetachUsbAsync(vmDirectory, GetArg(arguments, "-UsbId"), cancellation);
            case "ResetIphoneUsb":
                return NativeUsbService.ResetIphoneUsbAsync(vmDirectory, GetArg(arguments, "-UsbId"), cancellation);
            case "Pause":
                return NativeVmControlService.PauseAsync(vmDirectory, cancellation);
            case "Resume":
                return NativeVmControlService.ResumeAsync(vmDirectory, cancellation);
            case "PowerOff":
                return NativeVmControlService.PowerOffAsync(vmDirectory, cancellation);
            case "Restart":
                return NativeVmControlService.RestartAsync(vmDirectory, cancellation);
            case "Exit":
                return NativeVmControlService.ExitAsync(vmDirectory, cancellation);
            case "ForceExit":
                return NativeVmControlService.ForceExitAsync(vmDirectory, cancellation);
            case "HideHostCursor":
                return NativeVmControlService.SetHostCursorAsync(vmDirectory, hidden: true, cancellation);
            case "ShowHostCursor":
                return NativeVmControlService.SetHostCursorAsync(vmDirectory, hidden: false, cancellation);
            case "SetResolution":
                return NativeVmControlService.SetResolutionAsync(vmDirectory, GetArg(arguments, "-Resolution", "1920x1080"), cancellation);
            case "ApplyResolution":
                return NativeVmControlService.ApplyResolutionAsync(vmDirectory, GetArg(arguments, "-Resolution", "1920x1080"), cancellation);
            case "SetHardware": {
                string iso = GetArg(arguments, "-Iso");
                bool preserveNet = HasSwitch(arguments, "-PreserveNetwork");
                string network = GetArg(arguments, "-Network", "nat");
                string physicalAdapter = GetArg(arguments, "-PhysicalAdapter");
                if (!NativeVmControlService.CanHandleSetHardwareNatively(vmDirectory, iso, preserveNet, network, physicalAdapter))
                    return null;
                return NativeVmControlService.SetHardwareSimpleAsync(
                    vmDirectory,
                    GetIntArg(arguments, "-Cpus", 8),
                    GetIntArg(arguments, "-MemoryMiB", 8192),
                    GetArg(arguments, "-Resolution", "1920x1080"),
                    GetArg(arguments, "-Audio", "none"),
                    HasSwitch(arguments, "-Microphone"),
                    GetArg(arguments, "-UsbBackend", "winusb"),
                    HasSwitch(arguments, "-WithInstaller"),
                    preserveNet,
                    GetArg(arguments, "-GuestHost"),
                    cancellation);
            }
            default:
                return null;
        }
    }

    private static string GetArg(string[] args, string flag, string defaultValue = "")
    {
        for (int i = 0; i + 1 < args.Length; i++) {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return defaultValue;
    }

    private static int GetIntArg(string[] args, string flag, int defaultValue)
    {
        string raw = GetArg(args, flag);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) ? val : defaultValue;
    }

    private static bool HasSwitch(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}
