using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ReimsManager;

public partial class MainWindow
{
    private bool useFixtureConsole;

    // Runs only under --self-test against a newly created child of this executable.
    // This is not a QEMU/macOS, physical USB, GPU or guest-input acceptance test.
    internal async Task CheckNativeConsoleLifecycle(string folder)
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
            await CheckFixtureKeyboard(window);
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
            if (((IKeyboardInputSink)ConsoleView).TabInto(new TraversalRequest(FocusNavigationDirection.First)))
                throw new Exception("A detached console still accepts keyboard focus");
            if (FixtureMessage(window, App.ConsoleFixtureFocus, 0, 0) != 1) throw new Exception("Pop-out fixture could not focus its own window");
            await CheckFixtureKeyMessages(window);
            PrepareStartConsole(); UpdatePowerControls(); await ArrangeCheck(); UpdateConsole(); await ArrangeCheck();
            if (consoleDetached || !ConsoleView.Attached || FixtureGetParent(window) != host)
                throw new Exception("Start did not restore the default embedded console");
            await CheckFixtureKeyboard(window);
            ConsoleView.Detach();
            latest = JsonSerializer.SerializeToElement(new {
                state = "running", runtime = new { pid = int.MaxValue, start_ticks = 0L, executable = Environment.ProcessPath! }
            });
            UpdateConsole();
            if (consoleDetached) throw new Exception("A transient attach failure disabled automatic retries");
            latest = JsonSerializer.SerializeToElement(new {
                state = "running", runtime = new { pid = child.Id, start_ticks = child.StartTime.ToUniversalTime().Ticks, executable = Environment.ProcessPath! }
            });
            UpdateConsole(); await ArrangeCheck();
            if (!ConsoleView.Attached || FixtureGetParent(window) != host) throw new Exception("Polling did not recover the existing console");
            DockConsole(this, new RoutedEventArgs()); await ArrangeCheck();
            if (!ConsoleView.Attached || FixtureGetParent(window) != host) throw new Exception("Explicit re-docking failed");
            await CheckFixtureKeyboard(window);
            await CheckControlApi(child, window, folder);
            File.WriteAllText(Path.Combine(folder, "native-keyboard-result.json"), JsonSerializer.Serialize(new {
                tab_entry_native_focus = true, wrapper_focus_forwarded = true, mouse_entry_native_focus = true,
                native_key_down_up_and_system_messages = true, passive_attach_resize_keeps_focus = true,
                hidden_and_detached_reject_focus = true, popout_and_redock_keyboard = true,
                owned_window_messages_only = true, no_global_input_cu_uia_or_guest = true
            }));
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

    private async Task CheckFixtureKeyboard(nint window)
    {
        nint manager = new WindowInteropHelper(this).Handle;
        nint sentry = FixtureCreateWindow(0, "static", "Owned keyboard focus sentinel", 0x50000000,
            0, 0, 1, 1, manager, 0, 0, 0);
        if (sentry == 0) throw new Exception("Keyboard focus sentinel could not be created");
        try {
            FixtureSetFocus(sentry);
            var runtime = latest.GetProperty("runtime");
            ConsoleView.Attach(runtime.GetProperty("pid").GetInt32(), runtime.GetProperty("start_ticks").GetInt64(), runtime.GetProperty("executable").GetString()!);
            ConsoleView.UpdateWindowPos();
            if (FixtureGetFocus() != sentry) throw new Exception("Passive attach/resize stole focus from a Manager control");
            if (!((IKeyboardInputSink)ConsoleView).TabInto(new TraversalRequest(FocusNavigationDirection.First)))
                throw new Exception("Tab entry did not focus the embedded console");
            await CheckFixtureKeyMessages(window);

            FixtureSetFocus(sentry); FixtureSetFocus(ConsoleView.Handle);
            RequireFixtureFocus(window);
            FixtureSetFocus(sentry);
            FixtureMessage(ConsoleView.Handle, 0x0021, manager, (nint)((0x0201 << 16) | 1));
            RequireFixtureFocus(window);
            FixtureSetFocus(sentry);
            FixtureMessage(ConsoleView.Handle, 0x0210, 0x0201, 0);
            RequireFixtureFocus(window);

            FixtureSetFocus(sentry);
            OpenSettings(this, new RoutedEventArgs()); await ArrangeCheck();
            if (((IKeyboardInputSink)ConsoleView).TabInto(new TraversalRequest(FocusNavigationDirection.First)))
                throw new Exception("A hidden console accepted keyboard focus");
            if (!TryCloseSettings()) throw new Exception("Keyboard fixture settings could not close");
            await ArrangeCheck(); UpdateConsole(); await ArrangeCheck();
            if (!((IKeyboardInputSink)ConsoleView).TabInto(new TraversalRequest(FocusNavigationDirection.Last)))
                throw new Exception("Returning from settings did not restore keyboard entry");
            await CheckFixtureKeyMessages(window);
        } finally { FixtureDestroyWindow(sentry); }
    }

    private static nint RequireFixtureFocus(nint window)
    {
        uint thread = FixtureWindowThread(window, out _);
        var focus = new FixtureGuiThread { Size = (uint)Marshal.SizeOf<FixtureGuiThread>() };
        if (!FixtureGetGuiThreadInfo(thread, ref focus) ||
            focus.Focus != window && !FixtureIsChild(window, focus.Focus))
            throw new Exception("The OS keyboard focus is not inside the owned console process");
        return focus.Focus;
    }

    private static async Task CheckFixtureKeyMessages(nint window)
    {
        nint focus = RequireFixtureFocus(window);
        foreach (int message in new[] { 0x0100, 0x0101, 0x0104, 0x0105 }) {
            const int key = 0x87; // F24 is consumed only by the authored native fixture.
            nint before = FixtureMessage(window, App.ConsoleFixtureKeys, message, key);
            long flags = message is 0x0101 or 0x0105 ? 0xc0000001L : 1;
            if (message is 0x0104 or 0x0105) flags |= 0x20000000;
            if (!FixturePostMessage(focus, message, key, (nint)flags)) throw new Exception("Owned key message could not be queued");
            var clock = Stopwatch.StartNew();
            while (FixtureMessage(window, App.ConsoleFixtureKeys, message, key) == before) {
                if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Focused native child did not receive a key message");
                await Task.Delay(10);
            }
        }
    }

    private static nint FixtureMessage(nint window, int message, nint wparam, nint lparam)
    {
        if (FixtureSendMessageTimeout(window, message, wparam, lparam, 0x0002, 2000, out nint result) == 0)
            throw new Exception("Owned native fixture message timed out");
        return result;
    }

    private static void CheckFixtureSize(nint host, nint child)
    {
        if (!FixtureGetClientRect(host, out var h) || !FixtureGetClientRect(child, out var c) ||
            h.Right <= 0 || h.Bottom <= 0 || Math.Abs(h.Right - c.Right) > 2 || Math.Abs(h.Bottom - c.Bottom) > 2)
            throw new Exception("Native console is not resized to the host viewport");
    }
    [StructLayout(LayoutKind.Sequential)] private struct FixtureRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FixtureGuiThread {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public FixtureRect CaretRect;
    }
    [DllImport("user32.dll", EntryPoint = "GetParent")] private static extern nint FixtureGetParent(nint window);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool FixtureIsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] private static extern bool FixtureGetClientRect(nint window, out FixtureRect rectangle);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint FixtureWindowThread(nint window, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo")] private static extern bool FixtureGetGuiThreadInfo(uint thread, ref FixtureGuiThread info);
    [DllImport("user32.dll", EntryPoint = "GetFocus")] private static extern nint FixtureGetFocus();
    [DllImport("user32.dll", EntryPoint = "SetFocus")] private static extern nint FixtureSetFocus(nint window);
    [DllImport("user32.dll", EntryPoint = "IsChild")] private static extern bool FixtureIsChild(nint parent, nint child);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool FixturePostMessage(nint window, int message, nint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint FixtureSendMessageTimeout(nint window, int message, nint wparam, nint lparam, uint flags, uint milliseconds, out nint result);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint FixtureCreateWindow(int extended, string cls, string name, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow")] private static extern bool FixtureDestroyWindow(nint window);
}
