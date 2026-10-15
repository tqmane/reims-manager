using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace ReimsManager;

public partial class MainWindow
{
    private bool useFixtureConsole;

    // Runs only under --self-test against a newly created child of this executable.
    // This is not a QEMU/macOS, physical USB, GPU or guest-input acceptance test.
    internal async Task CheckNativeConsoleLifecycle()
    {
        if (!selfTest) throw new InvalidOperationException("Fixture access only");
        var savedStatus = latest;
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--console-fixture");
        using var child = Process.Start(info) ?? throw new Exception("Native console fixture did not start");
        try {
            var clock = Stopwatch.StartNew();
            nint window = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(10)) {
                child.Refresh();
                if (child.HasExited) throw new Exception("Native console fixture exited before showing a window");
                window = child.MainWindowHandle;
                if (window != 0) break;
                await Task.Delay(50);
            }
            if (window == 0) throw new Exception("Native console fixture window timed out");
            useFixtureConsole = true;
            latest = JsonSerializer.SerializeToElement(new {
                state = "running", runtime = new { pid = child.Id, start_ticks = child.StartTime.ToUniversalTime().Ticks, executable = Environment.ProcessPath! }
            });
            currentState = "running"; State.Text = "実行中"; Tabs.SelectedIndex = 0;
            presentation.ShowConsole(); UpdatePowerControls(); await ArrangeCheck(); UpdateConsole(); await ArrangeCheck();
            nint host = ConsoleView.Handle;
            if (host == 0 || !ConsoleView.Attached || FixtureGetParent(window) != host) throw new Exception("Fixture console did not attach");
            CheckFixtureSize(host, window);
            for (int i = 0; i < 3; i++) {
                OpenSettings(this, new RoutedEventArgs()); await ArrangeCheck();
                if (ConsoleView.Handle != host || FixtureGetParent(window) != host || FixtureIsWindowVisible(window))
                    throw new Exception("Settings reparented the console or let it cover the editor");
                if (!TryCloseSettings()) throw new Exception("Fixture settings could not close");
                await ArrangeCheck(); UpdateConsole(); await ArrangeCheck();
                if (ConsoleView.Handle != host || FixtureGetParent(window) != host || !FixtureIsWindowVisible(window))
                    throw new Exception("Returning from settings lost the embedded console");
                CheckFixtureSize(host, window);
                ShowLibrary(this, new RoutedEventArgs()); await ArrangeCheck();
                if (FixtureGetParent(window) != host) throw new Exception("Library navigation detached the console");
                DockConsole(this, new RoutedEventArgs()); await ArrangeCheck();
            }
            // A temporary status failure must still restore an already attached display.
            OpenSettings(this, new RoutedEventArgs()); currentState = "unknown"; UpdatePowerControls();
            if (!TryCloseSettings()) throw new Exception("Unknown-state settings could not close");
            await ArrangeCheck();
            if (FixtureGetParent(window) != host || !FixtureIsWindowVisible(window)) throw new Exception("Unknown status hid an already validated console");
            currentState = "running"; UpdatePowerControls();
            PopOutConsole(this, new RoutedEventArgs()); await ArrangeCheck();
            if (ConsoleView.Attached || FixtureGetParent(window) != 0) throw new Exception("Explicit pop-out did not restore the native window");
            DockConsole(this, new RoutedEventArgs()); await ArrangeCheck();
            if (!ConsoleView.Attached || FixtureGetParent(window) != host) throw new Exception("Explicit re-docking failed");
        } finally {
            // Detach first, so destroying a test host never destroys an owned guest window.
            ConsoleView.Detach(); useFixtureConsole = false; consoleDetached = false;
            latest = savedStatus; currentState = "stopped"; Tabs.SelectedIndex = 0;
            ConsoleView.Visibility = Visibility.Hidden; ConsolePlaceholder.Visibility = Visibility.Visible;
            UpdatePowerControls();
            if (!child.HasExited) {
                child.Refresh(); child.CloseMainWindow();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await child.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            }
        }
    }

    private static void CheckFixtureSize(nint host, nint child)
    {
        if (!FixtureGetClientRect(host, out var h) || !FixtureGetClientRect(child, out var c) ||
            h.Right <= 0 || h.Bottom <= 0 || Math.Abs(h.Right - c.Right) > 2 || Math.Abs(h.Bottom - c.Bottom) > 2)
            throw new Exception("Native console is not resized to the host viewport");
    }
    [StructLayout(LayoutKind.Sequential)] private struct FixtureRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetParent")] private static extern nint FixtureGetParent(nint window);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool FixtureIsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] private static extern bool FixtureGetClientRect(nint window, out FixtureRect rectangle);
}
