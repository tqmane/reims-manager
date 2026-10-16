namespace ReimsManager;

/// <summary>
/// Presentation only: changing views must never start, stop or detach a VM.
/// Unknown samples retain the last known layout; a different VM never inherits it.
/// </summary>
internal sealed class WorkspacePresentation
{
    private string? vm;
    private bool live, managementRequested;

    internal bool Focused(bool settingsOpen, bool detached) => live && !managementRequested && !settingsOpen && !detached;

    internal void Observe(string? selectedVm, string state)
    {
        if (!string.Equals(vm, selectedVm, StringComparison.OrdinalIgnoreCase)) {
            vm = selectedVm;
            live = managementRequested = false;
        }
        if (vm is null) { live = managementRequested = false; return; }
        if (state is "running" or "paused" or "prelaunch" or "suspended") live = true;
        else if (state is "stopped" or "shutdown" or "unprepared") live = managementRequested = false;
        // A transient status failure, or an error while a VM still owns its display,
        // must not make the console jump around or reset an explicit Library choice.
    }

    internal void ShowManagement() => managementRequested = true;
    internal void ShowConsole() => managementRequested = false;
}
