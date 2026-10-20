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

    internal async Task CheckOnlyNativeConsole(string folder)
    {
        if (!selfTest) throw new InvalidOperationException("Fixture access only");
        ConfigureEngine(folder, Environment.ProcessPath!);
        machines.Add(new VmEntry("Owned native console fixture", Path.Combine(folder, "native.reimsvm"), Path.Combine(folder, "native.data")));
        Library.ItemsSource = machines; Library.SelectedIndex = 0;
        await CheckNativeConsoleLifecycle(folder);
    }

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
            using var original = FixtureWindowSnapshot.Capture(window);
            useFixtureConsole = true;
            latest = JsonSerializer.SerializeToElement(new {
                state = "running", runtime = new { pid = child.Id, start_ticks = child.StartTime.ToUniversalTime().Ticks, executable = Environment.ProcessPath! }
            });
            currentState = "running"; State.Text = "実行中"; Tabs.SelectedIndex = 0;
            presentation.ShowConsole(); UpdatePowerControls(); await ArrangeCheck(); UpdateConsole(); await ArrangeCheck();
            nint host = ConsoleView.Handle, manager = new WindowInteropHelper(this).Handle;
            if (host == 0 || !ConsoleView.Attached) throw new Exception("Fixture console did not attach");
            RequireOwnedPopup(window, manager);
            CheckFixtureSize(host, window);
            await CheckOwnedPopupGeometry(window, host);
            await CheckPopupOcclusion(window);
            await CheckFixtureKeyboard(window);
            for (int i = 0; i < 3; i++) {
                OpenSettings(this, new RoutedEventArgs()); await ArrangeCheck();
                if (ConsoleView.Handle != host || !IsOwnedPopup(window, manager) || FixtureIsWindowVisible(window))
                    throw new Exception("Settings reparented the console or let it cover the editor");
                if (!TryCloseSettings()) throw new Exception("Fixture settings could not close");
                await ArrangeCheck(); UpdateConsole(); await ArrangeCheck();
                if (ConsoleView.Handle != host || !IsOwnedPopup(window, manager) || !FixtureIsWindowVisible(window))
                    throw new Exception("Returning from settings lost the embedded console");
                CheckFixtureSize(host, window);
                ShowLibrary(this, new RoutedEventArgs()); await ArrangeCheck();
                if (!IsOwnedPopup(window, manager)) throw new Exception("Library navigation detached the console");
                DockConsole(this, new RoutedEventArgs()); await ArrangeCheck();
            }
            // A temporary status failure must still restore an already attached display.
            OpenSettings(this, new RoutedEventArgs()); currentState = "unknown"; UpdatePowerControls();
            if (!TryCloseSettings()) throw new Exception("Unknown-state settings could not close");
            await ArrangeCheck();
            if (!IsOwnedPopup(window, manager) || !FixtureIsWindowVisible(window)) throw new Exception("Unknown status hid an already validated console");
            currentState = "running"; UpdatePowerControls();
            PopOutConsole(this, new RoutedEventArgs()); await ArrangeCheck();
            var popoutClock = Stopwatch.StartNew();
            while (!FixtureIsWindowVisible(window)) {
                if (popoutClock.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Explicit pop-out did not show the native window");
                await Task.Delay(10);
            }
            if (ConsoleView.Attached) throw new Exception("Explicit pop-out did not detach the native window");
            original.RequireRestored(window);
            await CheckAuthoredSnapshot(window, child, original);
            await CheckAcceptedClose(window, child);
            original.RequireRestored(window);
            if (!FixtureSetForeground(window)) throw new Exception("Pop-out activation was denied");
            if (((IKeyboardInputSink)ConsoleView).TabInto(new TraversalRequest(FocusNavigationDirection.First)))
                throw new Exception("A detached console still accepts keyboard focus");
            if (FixtureMessage(window, App.ConsoleFixtureFocus, 0, 0) != 1) throw new Exception("Pop-out fixture could not focus its own window");
            await CheckFixtureKeyMessages(window);
            PrepareStartConsole(); UpdatePowerControls(); await ArrangeCheck(); UpdateConsole(); await ArrangeCheck();
            if (consoleDetached || !ConsoleView.Attached || !IsOwnedPopup(window, manager))
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
            if (!ConsoleView.Attached || !IsOwnedPopup(window, manager)) throw new Exception("Polling did not recover the existing console");
            DockConsole(this, new RoutedEventArgs()); await ArrangeCheck();
            if (!ConsoleView.Attached || !IsOwnedPopup(window, manager)) throw new Exception("Explicit re-docking failed");
            await CheckFixtureKeyboard(window);
            await CheckControlApi(child, window, folder);
            File.WriteAllText(Path.Combine(folder, "native-keyboard-result.json"), JsonSerializer.Serialize(new {
                tab_entry_native_focus = true, passive_wrapper_focus_not_forwarded = true, mouse_entry_native_focus = true,
                top_level_owned_popup = true, actual_guest_foreground = true, accepted_close_preserves_guest = true,
                original_owner_style_exstyle_region_rect_restored = true, native_modal_and_minimize_hidden = true,
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

    private static bool IsOwnedPopup(nint window, nint manager) => FixtureGetAncestor(window, 2) == window &&
        FixtureGetWindow(window, 4) == manager && ((long)FixtureReadLong(window, -16) & 0x40000000L) == 0;

    private static void RequireOwnedPopup(nint window, nint manager)
    {
        long style = (long)FixtureReadLong(window, -16), exStyle = (long)FixtureReadLong(window, -20);
        if (!IsOwnedPopup(window, manager) || (style & 0x80000000L) == 0 || (style & 0x00CF0000L) != 0 || (exStyle & 0x80) == 0 ||
            (exStyle & (0x40000 | 0x08000000 | 8)) != 0)
            throw new Exception("Console is not a normal activatable, non-topmost owned top-level popup");
    }

    private async Task CheckOwnedPopupGeometry(nint window, nint host)
    {
        double left = Left, top = Top, width = Width, height = Height;
        var state = WindowState;
        nint manager = new WindowInteropHelper(this).Handle;
        try {
            WindowState = WindowState.Normal; Left = -32; Top = top + 23; Width = width + 20; Height = height + 16;
            await ArrangeCheck(); CheckFixtureSize(host, window); RequireOwnedPopup(window, manager);
            FixtureEnableWindow(manager, false); await ArrangeCheck();
            if (FixtureIsWindowVisible(window)) throw new Exception("Native modal disable did not hide the console");
            FixtureEnableWindow(manager, true); await ArrangeCheck();
            if (!FixtureIsWindowVisible(window)) throw new Exception("Native modal re-enable did not restore the console");
            WindowState = WindowState.Minimized; await ArrangeCheck();
            if (FixtureIsWindowVisible(window)) throw new Exception("Minimizing the owner left the console visible");
            WindowState = WindowState.Normal; await ArrangeCheck(); CheckFixtureSize(host, window);
        } finally {
            FixtureEnableWindow(manager, true); WindowState = WindowState.Normal;
            Left = left; Top = top; Width = width; Height = height; WindowState = state;
            await ArrangeCheck();
        }
    }

    private async Task CheckAuthoredSnapshot(nint window, Process child, FixtureWindowSnapshot original)
    {
        var visibility = ConsoleView.Visibility;
        nint owner = FixtureCreateWindow(0, "static", "Owned fixture original owner", unchecked((int)0x80000000), 0, 0, 1, 1, 0, 0, 0, 0);
        if (owner == 0) throw new Exception("Original owner fixture could not be created");
        try {
            ConsoleView.Visibility = Visibility.Visible; await ArrangeCheck();
            FixtureWriteLong(window, -8, owner);
            nint region = FixtureCreateRectRgn(4, 4, original.Bounds.Right - original.Bounds.Left - 4, original.Bounds.Bottom - original.Bounds.Top - 4);
            if (region == 0) throw new Exception("Original region fixture could not be created");
            if (FixtureSetWindowRgn(window, region, true) == 0) { FixtureDeleteObject(region); throw new Exception("Original fixture region could not be set"); }
            using var authored = FixtureWindowSnapshot.Capture(window);
            if (!ConsoleView.Attach(child.Id, child.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!)) throw new Exception("Authored snapshot could not attach");
            await ArrangeCheck(); RequireOwnedPopup(window, new WindowInteropHelper(this).Handle);
            ConsoleView.Detach(); await ArrangeCheck(); authored.RequireRestored(window);
            FixtureShowWindow(window, 3); // Authored fixture setup: maximize before hosting.
            var clock = Stopwatch.StartNew();
            while (!FixtureIsZoomed(window)) {
                if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Fixture did not maximize");
                await Task.Delay(10);
            }
            using var maximized = FixtureWindowSnapshot.Capture(window);
            if (maximized.Placement.ShowCmd != 3 || !maximized.Placement.NormalPosition.Equals(authored.Placement.NormalPosition))
                throw new Exception("Maximized fixture lost its original normal placement");
            nint manager = new WindowInteropHelper(this).Handle;
            if (!FixtureSetForeground(manager)) throw new Exception("Manager fixture activation was denied");
            if (!ConsoleView.Attach(child.Id, child.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!)) throw new Exception("Maximized fixture could not attach");
            await ArrangeCheck(); CheckFixtureSize(ConsoleView.Handle, window);
            ConsoleView.Detach(); await ArrangeCheck(); maximized.RequireRestored(window);
            if (FixtureGetForeground() != manager) throw new Exception("Restoring maximized placement stole foreground");
            FixtureShowWindow(window, 4); // Restore without activation and inspect the old normal rectangle.
            await Task.Delay(50); await ArrangeCheck(); authored.RequireRestored(window);
        } finally {
            ConsoleView.Detach(); original.Restore(window);
            if (FixtureGetWindow(window, 4) == owner) throw new Exception("Original owner still owns the fixture before destruction");
            FixtureDestroyWindow(owner);
            ConsoleView.Visibility = visibility;
        }
    }

    private async Task CheckPopupOcclusion(nint window)
    {
        if (!FixtureGetWindowRect(window, out var rect)) throw new Exception("Fixture bounds could not be read");
        // WindowFromPoint deliberately skips static text controls; use an opaque native hit target.
        nint sentry = FixtureCreateWindow(0, "button", "Owned unrelated foreground fixture", unchecked((int)0x90000000),
            rect.Left + 12, rect.Top + 12, 80, 60, 0, 0, 0, 0);
        if (sentry == 0) throw new Exception("Foreground occlusion fixture could not be created");
        double width = Width;
        try {
            if (!FixtureSetForeground(sentry)) throw new Exception("Occlusion fixture activation was denied");
            RequireUncovered("before synchronization");
            Width += 8; await ArrangeCheck();
            var state = latest.GetProperty("runtime");
            ConsoleView.Attach(state.GetProperty("pid").GetInt32(), state.GetProperty("start_ticks").GetInt64(), state.GetProperty("executable").GetString()!);
            ConsoleView.UpdateWindowPos();
            RequireUncovered("after synchronization");
        } finally { Width = width; FixtureDestroyWindow(sentry); await ArrangeCheck(); }

        void RequireUncovered(string stage)
        {
            nint foreground = FixtureGetForeground();
            var point = new FixturePoint { X = rect.Left + 20, Y = rect.Top + 20 };
            nint hit = FixtureWindowFromPoint(point);
            if (foreground != sentry || hit != sentry)
                throw new Exception("Passive viewport synchronization covered or activated above another foreground window: " +
                    JsonSerializer.Serialize(new { stage, point = new[] { point.X, point.Y },
                        foreground = FixtureWindowDetails(foreground), hit = FixtureWindowDetails(hit),
                        sentry = FixtureWindowDetails(sentry), guest = FixtureWindowDetails(window),
                        manager = FixtureWindowDetails(new WindowInteropHelper(this).Handle) }));
        }
    }

    private static object FixtureWindowDetails(nint window)
    {
        var name = new System.Text.StringBuilder(256);
        FixtureGetClassName(window, name, name.Capacity);
        bool hasBounds = FixtureGetWindowRect(window, out var bounds);
        return new { hwnd = (long)window, @class = name.ToString(), owner = (long)FixtureGetWindow(window, 4),
            visible = FixtureIsWindowVisible(window),
            bounds = hasBounds ? new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom } : null };
    }

    private async Task CheckAcceptedClose(nint window, Process child)
    {
        var closingWindow = new MainWindow(true) { ShowActivated = false };
        try {
            closingWindow.selectedEntry = selectedEntry;
            closingWindow.useFixtureConsole = true; closingWindow.latest = latest;
            closingWindow.currentState = "running"; closingWindow.presentation.ShowConsole();
            closingWindow.UpdatePowerControls(); closingWindow.Show(); await closingWindow.ArrangeCheck();
            closingWindow.UpdateConsole(); await closingWindow.ArrangeCheck();
            if (!closingWindow.ConsoleView.Attached) throw new Exception("Closing fixture could not attach");
            nint manager = new WindowInteropHelper(closingWindow).Handle;
            RequireOwnedPopup(window, manager);
            nint foreign = FixtureCreateWindow(0, "static", "Owned fixture changed owner", unchecked((int)0x80000000), 0, 0, 1, 1, 0, 0, 0, 0);
            if (foreign == 0) throw new Exception("Changed owner fixture could not be created");
            try {
                FixtureWriteLong(window, -8, foreign);
                using var changed = FixtureWindowSnapshot.Capture(window);
                closingWindow.Close(); await ArrangeCheck();
                if (!closingWindow.IsVisible || child.HasExited) throw new Exception("Close was not cancelled after losing ownership");
                changed.RequireRestored(window); // No blind snapshot restoration over a foreign owner.
                FixtureWriteLong(window, -8, manager);
            } finally {
                if (FixtureIsWindow(window) && FixtureGetWindow(window, 4) == foreign) FixtureWriteLong(window, -8, manager);
                FixtureDestroyWindow(foreign);
            }
            closingWindow.Close(); await ArrangeCheck();
            if (closingWindow.IsVisible || child.HasExited || !FixtureIsWindow(window) || FixtureGetWindow(window, 4) != 0)
                throw new Exception("Accepted Manager close did not detach before destroying its owner HWND");
        } finally {
            closingWindow.ConsoleView.Detach();
            if (closingWindow.IsVisible) closingWindow.Close();
        }
    }

    private sealed class FixtureWindowSnapshot : IDisposable
    {
        public nint Style, ExStyle, Owner, Region;
        public FixtureRect Bounds;
        public FixturePlacement Placement;
        public static FixtureWindowSnapshot Capture(nint window)
        {
            var state = new FixtureWindowSnapshot { Style = FixtureReadLong(window, -16), ExStyle = FixtureReadLong(window, -20), Owner = FixtureGetWindow(window, 4) };
            if (!FixtureGetWindowRect(window, out state.Bounds)) throw new Exception("Fixture bounds could not be read");
            state.Placement.Length = (uint)Marshal.SizeOf<FixturePlacement>();
            if (!FixtureGetWindowPlacement(window, ref state.Placement)) throw new Exception("Fixture placement could not be read");
            state.Region = FixtureCreateRectRgn(0, 0, 0, 0);
            if (state.Region == 0) throw new Exception("Fixture region could not be allocated");
            if (FixtureGetWindowRgn(window, state.Region) == 0) { FixtureDeleteObject(state.Region); state.Region = 0; }
            return state;
        }
        public void RequireRestored(nint window)
        {
            if (FixtureGetAncestor(window, 2) != window || FixtureGetWindow(window, 4) != Owner ||
                FixtureReadLong(window, -16) != Style || FixtureReadLong(window, -20) != ExStyle ||
                !FixtureGetWindowRect(window, out var bounds) || !bounds.Equals(Bounds))
                throw new Exception("Pop-out did not restore the exact original owner/style/exstyle/rectangle");
            var placement = new FixturePlacement { Length = (uint)Marshal.SizeOf<FixturePlacement>() };
            if (!FixtureGetWindowPlacement(window, ref placement) || placement.ShowCmd != Placement.ShowCmd ||
                !placement.NormalPosition.Equals(Placement.NormalPosition)) throw new Exception("Original normal placement/show state was not restored");
            nint region = FixtureCreateRectRgn(0, 0, 0, 0);
            if (region == 0) throw new Exception("Fixture comparison region could not be allocated");
            try {
                int kind = FixtureGetWindowRgn(window, region);
                if (Region == 0 ? kind != 0 : kind == 0 || !FixtureEqualRgn(Region, region))
                    throw new Exception("Pop-out did not restore the original window region");
            } finally { FixtureDeleteObject(region); }
        }
        public void Restore(nint window)
        {
            FixtureWriteLong(window, -8, Owner); FixtureWriteLong(window, -16, Style); FixtureWriteLong(window, -20, ExStyle);
            nint copy = 0;
            if (Region != 0) {
                copy = FixtureCreateRectRgn(0, 0, 0, 0);
                if (copy == 0 || FixtureCombineRgn(copy, Region, 0, 5) == 0) { if (copy != 0) FixtureDeleteObject(copy); throw new Exception("Fixture region copy failed"); }
            }
            if (FixtureSetWindowRgn(window, copy, true) == 0) { if (copy != 0) FixtureDeleteObject(copy); throw new Exception("Fixture region restoration failed"); }
            if (!FixtureSetWindowPos(window, 0, Bounds.Left, Bounds.Top, Bounds.Right - Bounds.Left, Bounds.Bottom - Bounds.Top, 0x0234))
                throw new Exception("Fixture rectangle restoration failed");
            var placement = Placement; placement.ShowCmd = 8;
            if (!FixtureSetWindowPlacement(window, ref placement)) throw new Exception("Fixture placement restoration failed");
        }
        public void Dispose() { if (Region != 0) FixtureDeleteObject(Region); Region = 0; }
    }

    private static void FixtureWriteLong(nint window, int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        if (FixtureSetLong(window, index, value) == 0 && Marshal.GetLastPInvokeError() != 0) throw new Exception("Fixture window state update failed");
    }

    private async Task CheckFixtureKeyboard(nint window)
    {
        nint manager = new WindowInteropHelper(this).Handle;
        nint sentry = FixtureCreateWindow(0, "static", "Owned keyboard focus sentinel", 0x50000000,
            0, 0, 1, 1, manager, 0, 0, 0);
        if (sentry == 0) throw new Exception("Keyboard focus sentinel could not be created");
        try {
            if (!FixtureSetForeground(manager)) throw new Exception("Manager fixture activation was denied");
            FixtureSetFocus(sentry);
            var runtime = latest.GetProperty("runtime");
            ConsoleView.Attach(runtime.GetProperty("pid").GetInt32(), runtime.GetProperty("start_ticks").GetInt64(), runtime.GetProperty("executable").GetString()!);
            ConsoleView.UpdateWindowPos();
            if (FixtureGetFocus() != sentry || FixtureGetForeground() != manager) throw new Exception("Passive attach/resize stole focus from a Manager control");
            if (!FixtureGetWindowRect(window, out var beforeResize) ||
                !FixtureSetWindowPos(window, 0, beforeResize.Left + 11, beforeResize.Top + 9,
                    beforeResize.Right - beforeResize.Left - 30, beforeResize.Bottom - beforeResize.Top - 20, 0x0214))
                throw new Exception("External SDL-size fixture could not resize");
            ConsoleView.Attach(runtime.GetProperty("pid").GetInt32(), runtime.GetProperty("start_ticks").GetInt64(), runtime.GetProperty("executable").GetString()!);
            CheckFixtureSize(ConsoleView.Handle, window);
            if (FixtureGetForeground() != manager || FixtureGetFocus() != sentry) throw new Exception("External resize recovery stole focus");
            if (!((IKeyboardInputSink)ConsoleView).TabInto(new TraversalRequest(FocusNavigationDirection.First)))
                throw new Exception("Tab entry did not focus the embedded console: " + JsonSerializer.Serialize(new {
                    visible = ConsoleView.IsVisible, enabled = ConsoleView.IsEnabled,
                    manager = FixtureFocusState(manager), guest = FixtureFocusState(window)
                }));
            await CheckFixtureKeyMessages(window);

            if (!FixtureSetForeground(manager)) throw new Exception("Manager fixture activation was denied");
            FixtureSetFocus(sentry); FixtureSetFocus(ConsoleView.Handle);
            if (FixtureGetForeground() != manager) throw new Exception("Programmatic anchor focus activated the guest");
            FixtureSetFocus(sentry);
            FixtureMessage(ConsoleView.Handle, 0x0021, manager, (nint)((0x0201 << 16) | 1));
            RequireFixtureFocus(window);
            if (!FixtureSetForeground(window)) throw new Exception("Top-level console activation was denied");
            RequireFixtureFocus(window);

            if (!FixtureSetForeground(manager)) throw new Exception("Manager fixture activation was denied");
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
        if (FixtureGetForeground() != window || !FixtureGetGuiThreadInfo(thread, ref focus) ||
            focus.Focus != window && !FixtureIsChild(window, focus.Focus))
            throw new Exception("The OS keyboard focus is not inside the owned console process");
        return focus.Focus;
    }

    private static object FixtureFocusState(nint window)
    {
        uint thread = FixtureWindowThread(window, out uint pid);
        var focus = new FixtureGuiThread { Size = (uint)Marshal.SizeOf<FixtureGuiThread>() };
        Marshal.SetLastPInvokeError(0);
        bool success = FixtureGetGuiThreadInfo(thread, ref focus);
        return new { thread, pid, success, error = Marshal.GetLastPInvokeError(),
            focus = (long)focus.Focus, active = (long)focus.Active, capture = (long)focus.Capture };
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
        var point = new FixturePoint();
        if (!FixtureClientToScreen(host, ref point) || !FixtureGetWindowRect(child, out var bounds) ||
            bounds.Left != point.X || bounds.Top != point.Y)
            throw new Exception("Top-level console does not follow the physical screen origin");
    }
    [StructLayout(LayoutKind.Sequential)] private struct FixturePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct FixturePlacement {
        public uint Length, Flags, ShowCmd; public FixturePoint MinPosition, MaxPosition; public FixtureRect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FixtureRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FixtureGuiThread {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public FixtureRect CaretRect;
    }
    [DllImport("user32.dll", EntryPoint = "GetAncestor")] private static extern nint FixtureGetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindow")] private static extern nint FixtureGetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint FixtureReadLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint FixtureSetLong(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool FixtureGetWindowRect(nint window, out FixtureRect rectangle);
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")] private static extern bool FixtureClientToScreen(nint window, ref FixturePoint point);
    [DllImport("user32.dll", EntryPoint = "EnableWindow")] private static extern bool FixtureEnableWindow(nint window, bool enabled);
    [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool FixtureIsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint FixtureGetForeground();
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern nint FixtureWindowFromPoint(FixturePoint point);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int FixtureGetClassName(nint window, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool FixtureSetForeground(nint window);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool FixtureSetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowPlacement")] private static extern bool FixtureGetWindowPlacement(nint window, ref FixturePlacement placement);
    [DllImport("user32.dll", EntryPoint = "SetWindowPlacement")] private static extern bool FixtureSetWindowPlacement(nint window, ref FixturePlacement placement);
    [DllImport("user32.dll", EntryPoint = "ShowWindowAsync")] private static extern bool FixtureShowWindow(nint window, int command);
    [DllImport("user32.dll", EntryPoint = "IsZoomed")] private static extern bool FixtureIsZoomed(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowRgn")] private static extern int FixtureGetWindowRgn(nint window, nint region);
    [DllImport("user32.dll", EntryPoint = "SetWindowRgn")] private static extern int FixtureSetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn")] private static extern nint FixtureCreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", EntryPoint = "CombineRgn")] private static extern int FixtureCombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll", EntryPoint = "EqualRgn")] private static extern bool FixtureEqualRgn(nint first, nint second);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool FixtureDeleteObject(nint value);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool FixtureIsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] private static extern bool FixtureGetClientRect(nint window, out FixtureRect rectangle);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint FixtureWindowThread(nint window, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo", SetLastError = true)] private static extern bool FixtureGetGuiThreadInfo(uint thread, ref FixtureGuiThread info);
    [DllImport("user32.dll", EntryPoint = "GetFocus")] private static extern nint FixtureGetFocus();
    [DllImport("user32.dll", EntryPoint = "SetFocus")] private static extern nint FixtureSetFocus(nint window);
    [DllImport("user32.dll", EntryPoint = "IsChild")] private static extern bool FixtureIsChild(nint parent, nint child);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool FixturePostMessage(nint window, int message, nint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint FixtureSendMessageTimeout(nint window, int message, nint wparam, nint lparam, uint flags, uint milliseconds, out nint result);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint FixtureCreateWindow(int extended, string cls, string name, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow")] private static extern bool FixtureDestroyWindow(nint window);
}
