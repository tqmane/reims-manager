using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace ReimsManager;

public partial class MainWindow
{
    // Present only in --self-test: no user engine, guest disk or QEMU is invoked.
    private Func<string, Task<string>>? controlFixtureBackend;

    private async Task CheckControlApi(Process child, nint childWindow, string folder)
    {
        if (!selfTest || !useFixtureConsole) throw new InvalidOperationException("Control fixture access only.");
        using var server = new ManagerControlServer(Control);
        string fixtureState = "stopped", blockedAction = "Start";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwarded = new List<string>();
        controlFixtureBackend = async action => {
            if (!Dispatcher.CheckAccess()) throw new Exception("Control bypassed the UI dispatcher.");
            if (action == "GetNetworkUplinks") return "{\"schema\":1,\"uplinks\":[],\"tap_adapters\":[]}";
            if (action == "Status") return JsonSerializer.Serialize(new {
                state = fixtureState, prepared = true, snapshots = Array.Empty<string>(), taps = Array.Empty<string>(), adapters = Array.Empty<string>(),
                runtime = fixtureState == "stopped" ? null : new { pid = child.Id, start_ticks = child.StartTime.ToUniversalTime().Ticks, executable = Environment.ProcessPath! },
                cpus = 4, memory_mib = 8192, disk_gib = 300, disk = "fixture", iso = "", with_installer = false,
                network = "nat", network_adapter = "", guest_host = "", audio = "none", microphone = false, usb_backend = "winusb", resolution = "1920x1080"
            });
            forwarded.Add(action);
            if (action == blockedAction) { entered.TrySetResult(); await release.Task; }
            fixtureState = action switch { "PowerOff" => "shutdown", "Exit" => "stopped", "Pause" => "paused", _ => "running" };
            return "Owned native control fixture completed.";
        };
        try {
            currentState = "stopped"; hardwareDirty = identityDirty = false; UpdatePowerControls();
            var start = ControlCli("start");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (start.IsCompleted) throw new Exception("CLI start finished before the real operation.");
            var during = await ControlCli("status");
            if (!during.GetProperty("busy").GetBoolean()) throw new Exception("Status could not observe a running operation.");
            var rejected = await ControlCli("pause");
            if (rejected.GetProperty("error").GetString() != "busy" || forwarded.Count != 1) throw new Exception("Concurrent mutation was accepted.");
            release.TrySetResult();
            var started = await start;
            if (!started.GetProperty("ok").GetBoolean() || started.GetProperty("busy").GetBoolean() ||
                started.GetProperty("state").GetString() != "running" || !started.GetProperty("consoleAttached").GetBoolean())
                throw new Exception("CLI start did not complete with the running embedded console.");
            var popped = await ControlCli("popout-console");
            if (!popped.GetProperty("consoleDetached").GetBoolean() || FixtureGetWindow(childWindow, 4) != 0 || FixtureGetAncestor(childWindow, 2) != childWindow) throw new Exception("CLI pop-out failed.");
            await ControlCli("dock-console");
            RequireOwnedPopup(childWindow, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            await ControlCli("pause"); await ControlCli("resume");

            // Disconnect after acceptance; the command must finish without the client.
            blockedAction = "Restart";
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var disconnected = new NamedPipeClientStream(".", ManagerControl.PipeName(Environment.ProcessId), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await disconnected.ConnectAsync(timeout.Token);
                await ManagerControl.Write(disconnected, JsonSerializer.SerializeToElement(new { schema = 1, command = "restart" }), timeout.Token);
                await entered.Task.WaitAsync(timeout.Token);
            }
            if (!busy) throw new Exception("Client disconnect cancelled the operation.");
            release.TrySetResult();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await ControlCli("status");
            if (busy || fixtureState != "running" || child.HasExited) throw new Exception("Disconnected operation did not finish or killed its display.");
            await ControlCli("poweroff"); await ControlCli("exit");
            if (!forwarded.SequenceEqual(new[] { "Start", "Pause", "Resume", "Restart", "PowerOff", "Exit" })) throw new Exception("Control action forwarding changed.");
            using (var invalid = new NamedPipeClientStream(".", ManagerControl.PipeName(Environment.ProcessId), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await invalid.ConnectAsync(timeout.Token);
                await ManagerControl.Write(invalid, JsonSerializer.SerializeToElement(new { schema = 1, command = "ForceExit", code = "never execute" }), timeout.Token);
                if ((await ManagerControl.Read(invalid, timeout.Token)).GetProperty("ok").GetBoolean() || forwarded.Count != 6)
                    throw new Exception("A non-whitelisted control request was executed.");
            }
            hardwareDirty = true;
            if ((await ControlCli("start")).GetProperty("error").GetString() != "unsaved_changes") throw new Exception("Automation discarded unsaved settings.");
            hardwareDirty = false; currentState = "unknown";
            if ((await ControlCli("start")).GetProperty("error").GetString() != "unavailable") throw new Exception("Automation bypassed VM policy.");

            foreach (int size in new[] { 0, -1, ManagerControl.MaximumMessage + 1 }) {
                byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, size);
                using var invalid = new MemoryStream(header);
                try { await ManagerControl.Read(invalid, CancellationToken.None); throw new Exception("Invalid frame size was accepted."); }
                catch (InvalidDataException) { }
            }
            File.WriteAllText(Path.Combine(folder, "control-result.json"), JsonSerializer.Serialize(new {
                real_cli_process = true, redirected_stdout_and_output_file = true, dispatcher_forwarding = true,
                start_waits_for_completion = true, concurrent_status = true, busy_mutation_rejected = true,
                native_console_dock_popout = true, disconnect_preserves_operation = true, all_power_actions_forwarded = true,
                unsaved_changes_preserved = true, vm_policy_preserved = true, message_bounds = true, commands_whitelisted = true
            }));
        } finally { release.TrySetResult(); controlFixtureBackend = null; hardwareDirty = identityDirty = false; }

        async Task<JsonElement> ControlCli(string command)
        {
            string output = Path.Combine(folder, "control-" + Guid.NewGuid().ToString("N") + " 日本語.json");
            var info = new ProcessStartInfo(Environment.ProcessPath!) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in new[] { "--control", command, "--pid", Environment.ProcessId.ToString(), "--output", output }) info.ArgumentList.Add(argument);
            using var client = Process.Start(info) ?? throw new Exception("Control CLI did not start.");
            var stdout = client.StandardOutput.ReadToEndAsync(); var stderr = client.StandardError.ReadToEndAsync();
            try { await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!client.HasExited) { client.Kill(); await client.WaitForExitAsync(); } }
            using var reply = JsonDocument.Parse(await stdout);
            var value = reply.RootElement.Clone();
            if ((await stderr).Length != 0 || File.ReadAllText(output).Trim() != value.GetRawText() ||
                (client.ExitCode == 0) != value.GetProperty("ok").GetBoolean()) throw new Exception("CLI JSON output/exit code failed.");
            return value;
        }
    }
}
