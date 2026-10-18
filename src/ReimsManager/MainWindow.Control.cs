using System.Text.Json;
using System.Windows;

namespace ReimsManager;

public partial class MainWindow
{
    private JsonElement ControlSnapshot(bool ok = true, string? error = null, string? message = null)
    {
        int? runtimePid = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("runtime", out var runtime) &&
            runtime.ValueKind == JsonValueKind.Object && runtime.TryGetProperty("pid", out var pid) ? pid.GetInt32() : null;
        return JsonSerializer.SerializeToElement(new {
            schema = 1, ok, manager_pid = Environment.ProcessId, busy = IsUiBusy,
            selected_descriptor = selectedEntry?.File, state = currentState,
            consoleAttached = ConsoleView.Attached, consoleDetached, runtime_pid = runtimePid, error, message
        });
    }

    internal async Task<JsonElement> Control(string command)
    {
        if (!Dispatcher.CheckAccess()) return await Dispatcher.InvokeAsync(() => Control(command)).Task.Unwrap();
        if (closing) return ControlSnapshot(false, "closing", "Manager is closing.");
        if (command == "status") {
            if (!IsUiBusy) await RefreshState(full: false);
            return ControlSnapshot();
        }
        if (IsUiBusy) return ControlSnapshot(false, "busy", "Another operation is running.");
        // Automation cannot answer a modal discard/consent prompt on the user's behalf.
        if (hardwareDirty || identityDirty) return ControlSnapshot(false, "unsaved_changes", "Save or discard the pending settings first.");
        string? action = command switch {
            "start" => "Start", "poweroff" => "PowerOff", "restart" => "Restart", "exit" => "Exit",
            "pause" => "Pause", "resume" => "Resume", "dock-console" or "popout-console" => "Console", _ => null
        };
        if (action is null) return ControlSnapshot(false, "unsupported_command", "Unsupported control command.");
        if (!CanOperate(action)) return ControlSnapshot(false, "unavailable", "The selected VM is not ready for this operation.");
        if (command == "dock-console") { DockConsole(this, new RoutedEventArgs()); return ControlSnapshot(); }
        if (command == "popout-console") { PopOutConsole(this, new RoutedEventArgs()); return ControlSnapshot(); }
        bool success = await Operate(action);
        return ControlSnapshot(success, success ? null : "operation_failed", success ? null : ErrorNotice.Text);
    }
}
