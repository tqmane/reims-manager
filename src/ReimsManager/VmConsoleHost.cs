using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReimsManager;

public sealed class VmConsoleHost : HwndHost
{
    private const int StyleIndex = -16, ExStyleIndex = -20, OwnerIndex = -8;
    private const long ChildStyle = 0x40000000, PopupStyle = 0x80000000, VisibleStyle = 0x10000000;
    private const long AppWindow = 0x40000, ToolWindow = 0x80, NoActivate = 0x08000000, Topmost = 8;
    private const uint Passive = 0x0010 | 0x0004 | 0x0200; // NOACTIVATE, NOZORDER, NOOWNERZORDER
    private nint guest, ownerRoot;
    private Process? runtime;
    private Snapshot? saved;
    private HwndSource? ownerSource;
    private bool syncQueued, ownerClaimed;
    private string? syncFailure;
    private RectNative? lastBounds, lastClip;

    public VmConsoleHost()
    {
        IsVisibleChanged += (_, _) => QueueSync();
        IsEnabledChanged += (_, _) => QueueSync();
    }

    public bool Attached
    {
        get {
            if (guest == 0 || runtime is null || runtime.HasExited || !IsWindow(guest)) return false;
            GetWindowThreadProcessId(guest, out uint pid);
            return pid == runtime.Id;
        }
    }

    internal bool Attach(int pid, long startTicks, string executable)
    {
        if (Handle == 0 || ownerRoot == 0) return false;
        if (syncFailure is not null) throw new InvalidOperationException(syncFailure);
        if (Attached && runtime!.Id == pid && runtime.StartTime.ToUniversalTime().Ticks == startTicks &&
            string.Equals(runtime.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) {
            Synchronize(); return true;
        }
        Detach();
        var process = Process.GetProcessById(pid);
        try {
            // Retain the actual process handle rather than trusting a reused PID during layout.
            _ = process.SafeHandle;
            if (process.StartTime.ToUniversalTime().Ticks != startTicks ||
                !string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return false;
            nint candidate = 0;
            EnumWindows((window, _) => {
                GetWindowThreadProcessId(window, out uint windowPid);
                if (windowPid == pid && IsWindowVisible(window) && GetAncestor(window, 2) == window &&
                    ((long)GetWindowLongPtr(window, StyleIndex) & ChildStyle) == 0) { candidate = window; return false; }
                return true;
            }, 0);
            if (candidate == 0) return false;
            var snapshot = Snapshot.Capture(candidate);
            guest = candidate; runtime = process; saved = snapshot;
            try {
                if (GetWindow(guest, 4) != snapshot.Owner) throw new InvalidOperationException("VM 画面の所有者が変更されました。");
                // SDL remains top-level. The static HwndHost is only a physical viewport anchor.
                Position(guest, 0, default, Passive | 0x0002 | 0x0001 | 0x0080);
                WriteLong(guest, StyleIndex, (nint)(((long)snapshot.Style & ~0x71CF0000L) | PopupStyle));
                WriteLong(guest, ExStyleIndex, (nint)(((long)snapshot.ExStyle & ~(AppWindow | NoActivate | Topmost | 0x301L)) | ToolWindow));
                if (GetWindow(guest, 4) != snapshot.Owner) throw new InvalidOperationException("VM 画面の所有者が変更されました。");
                WriteLong(guest, OwnerIndex, ownerRoot);
                ownerClaimed = true;
                bool wasTopmost = ((long)snapshot.ExStyle & Topmost) != 0;
                Position(guest, wasTopmost ? -2 : 0, snapshot.Bounds,
                    0x0010 | 0x0200 | 0x0020 | (wasTopmost ? 0u : 0x0004u));
                Synchronize();
                return true;
            } catch (Exception error) {
                try { Detach(); }
                catch (Exception restore) {
                    syncFailure = "VM 画面の接続と復元に失敗しました。" + error.Message + " / " + restore.Message;
                    throw new InvalidOperationException(syncFailure, error);
                }
                throw;
            }
        } finally { if (!ReferenceEquals(runtime, process)) process.Dispose(); }
    }

    internal void Detach()
    {
        // Keep the snapshot on failure: accepted Closing must cancel rather than destroy an
        // owned guest HWND whose ownership could not be restored.
        if (Attached && saved is not null) {
            nint expectedOwner = ownerClaimed ? ownerRoot : saved.Owner;
            if (GetWindow(guest, 4) != expectedOwner)
                throw new InvalidOperationException("VM 画面の所有者が変更されたため、元の状態を上書きできません。");
            saved.Restore(guest);
        }
        saved?.Dispose(); saved = null;
        runtime?.Dispose(); runtime = null;
        guest = 0; ownerClaimed = false; lastBounds = lastClip = null; syncFailure = null;
    }

    private void Synchronize()
    {
        if (!Attached || saved is null || Handle == 0) return;
        if (GetAncestor(guest, 2) != guest || ((long)ReadLong(guest, StyleIndex) & ChildStyle) != 0 ||
            GetWindow(guest, 4) != ownerRoot) throw new InvalidOperationException("VM 画面の所有関係が変更されました。");
        if (!IsVisible || !IsEnabled || !IsWindowVisible(ownerRoot) || !IsWindowEnabled(ownerRoot) || IsIconic(ownerRoot)) {
            HideGuest(); return;
        }
        RectNative bounds = ScreenClientRect(Handle), clip = Intersect(bounds, ScreenClientRect(ownerRoot));
        // WPF ancestors no longer clip a top-level HWND automatically.
        for (DependencyObject? ancestor = this; ancestor is Visual visual; ancestor = VisualTreeHelper.GetParent(ancestor)) {
            if (ancestor is UIElement element) {
                if (element.ClipToBounds) clip = Intersect(clip, ScreenRect(visual, new Rect(element.RenderSize)));
                if (element.Clip is { } shape) {
                    if (shape.Bounds.IsEmpty) { HideGuest(); return; }
                    clip = Intersect(clip, ScreenRect(visual, shape.Bounds));
                }
            }
        }
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top || clip.Right <= clip.Left || clip.Bottom <= clip.Top) {
            HideGuest(); return;
        }
        var region = new RectNative { Left = clip.Left - bounds.Left, Top = clip.Top - bounds.Top,
            Right = clip.Right - bounds.Left, Bottom = clip.Bottom - bounds.Top };
        if (lastClip is null || !lastClip.Value.Equals(region)) {
            nint handle = CreateRectRgn(region.Left, region.Top, region.Right, region.Bottom);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            ApplyRegion(guest, handle);
            lastClip = region;
        }
        if (!GetWindowRect(guest, out var actual)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (lastBounds is null || !lastBounds.Value.Equals(bounds) || !actual.Equals(bounds) || !IsWindowVisible(guest)) {
            Position(guest, 0, bounds, Passive | 0x0040);
            lastBounds = bounds;
        }
    }

    private void HideGuest()
    {
        if (Attached && IsWindowVisible(guest)) Position(guest, 0, default, Passive | 0x0002 | 0x0001 | 0x0080);
    }

    private void QueueSync()
    {
        if (!Attached || syncQueued || Dispatcher.HasShutdownStarted) return;
        syncQueued = true;
        _ = Dispatcher.InvokeAsync(() => { syncQueued = false; TrySynchronize(); }, DispatcherPriority.Loaded);
    }

    private void TrySynchronize()
    {
        try { Synchronize(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) {
            string message = "VM 画面の配置を維持できませんでした。別ウィンドウ表示を使用してください。" + error.Message;
            try { Detach(); }
            catch (Exception restore) { message += " 復元にも失敗しました: " + restore.Message; }
            syncFailure = message; // Existing normal status/Attach reports this through UI feedback.
        }
    }

    private nint OwnerMessage(nint window, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message is 0x000a or 0x0018 && wparam == 0 || message == 0x0005 && wparam == 1)
            TrySynchronize(); // Native modal disable/hide/minimize must hide the popup immediately.
        if (message is 0x0003 or 0x0005 or 0x0047 or 0x02e0 or 0x000a or 0x0018) QueueSync();
        return 0;
    }

    private bool FocusGuest()
    {
        if (!Attached || !IsVisible || !IsEnabled || !IsWindowVisible(guest) || !IsWindowEnabled(guest) ||
            !IsWindowEnabled(ownerRoot) || GetWindow(guest, 4) != ownerRoot) return false;
        // Explicit Tab/click entry only. Ownership may join input queues; no isolation claim.
        if (!SetForegroundWindow(guest)) return false;
        var info = new GuiThread { Size = (uint)Marshal.SizeOf<GuiThread>() };
        uint thread = GetWindowThreadProcessId(guest, out _);
        return GetForegroundWindow() == guest && GetGUIThreadInfo(thread, ref info) &&
            (info.Focus == guest || IsChild(guest, info.Focus));
    }

    protected override bool TabIntoCore(TraversalRequest request) => FocusGuest();
    protected override nint WndProc(nint window, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message == 0x0021 && FocusGuest()) { handled = true; return 1; } // explicit WM_MOUSEACTIVATE
        return base.WndProc(window, message, wparam, lparam, ref handled);
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        nint anchor = CreateWindowEx(0, "static", "Reims VM console", 0x56000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (anchor == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        ownerRoot = GetAncestor(parent.Handle, 2);
        ownerSource = HwndSource.FromHwnd(ownerRoot);
        if (ownerSource is null) { DestroyWindow(anchor); throw new InvalidOperationException("Manager のウィンドウが見つかりません。"); }
        ownerSource.AddHook(OwnerMessage);
        return new(this, anchor);
    }

    protected override void DestroyWindowCore(HandleRef window)
    {
        Detach();
        if (ownerSource is { IsDisposed: false }) ownerSource.RemoveHook(OwnerMessage);
        ownerSource = null; ownerRoot = 0;
        DestroyWindow(window.Handle);
    }
    protected override void OnWindowPositionChanged(Rect rectangle) { base.OnWindowPositionChanged(rectangle); TrySynchronize(); }

    private sealed class Snapshot : IDisposable
    {
        public nint Style, ExStyle, Owner, Region;
        public RectNative Bounds;
        public PlacementNative Placement;
        public static Snapshot Capture(nint window)
        {
            var state = new Snapshot { Style = ReadLong(window, StyleIndex), ExStyle = ReadLong(window, ExStyleIndex), Owner = GetWindow(window, 4) };
            if (!GetWindowRect(window, out state.Bounds)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            state.Placement.Length = (uint)Marshal.SizeOf<PlacementNative>();
            if (!GetWindowPlacement(window, ref state.Placement)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            state.Region = CreateRectRgn(0, 0, 0, 0);
            if (state.Region == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (GetWindowRgn(window, state.Region) == 0) { DeleteObject(state.Region); state.Region = 0; }
            return state;
        }
        public void Restore(nint window)
        {
            WriteLong(window, StyleIndex, (nint)((long)Style & ~VisibleStyle)); WriteLong(window, ExStyleIndex, ExStyle);
            nint copy = 0;
            if (Region != 0) {
                copy = CreateRectRgn(0, 0, 0, 0);
                if (copy == 0 || CombineRgn(copy, Region, 0, 5) == 0) {
                    if (copy != 0) DeleteObject(copy);
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "VM 画面の領域を復元できませんでした。");
                }
            }
            ApplyRegion(window, copy);
            bool topmost = ((long)ExStyle & Topmost) != 0;
            uint flags = 0x0010 | 0x0200 | 0x0020 | (((long)Style & VisibleStyle) != 0 ? 0x0040u : 0x0080u);
            Position(window, topmost ? -1 : 0, Bounds, flags | (topmost ? 0u : 0x0004u));
            var placement = Placement;
            // Restore the saved normal rectangle while preserving the restored min/max state
            // without activating a background window. GetWindowPlacement normalizes showCmd.
            placement.ShowCmd = ((long)Style & VisibleStyle) != 0 ? 8u : 0u; // SW_SHOWNA / SW_HIDE
            if (!SetWindowPlacement(window, ref placement)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            WriteLong(window, OwnerIndex, Owner); // Release our owner lease only after restoration succeeded.
        }
        public void Dispose() { if (Region != 0) DeleteObject(Region); Region = 0; }
    }

    private static void ApplyRegion(nint window, nint region)
    {
        if (SetWindowRgn(window, region, true) != 0) return; // Successful transfer belongs to User32.
        int error = Marshal.GetLastPInvokeError(); if (region != 0) DeleteObject(region);
        throw new Win32Exception(error, "VM 画面の表示領域を設定できませんでした。");
    }
    private static nint ReadLong(nint window, int index)
    {
        Marshal.SetLastPInvokeError(0); nint value = GetWindowLongPtr(window, index);
        if (value == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return value;
    }
    private static void WriteLong(nint window, int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(window, index, value) == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    private static void Position(nint window, nint after, RectNative bounds, uint flags)
    {
        if (!SetWindowPos(window, after, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, flags))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "VM 画面の配置を変更できませんでした。");
    }
    private static RectNative ScreenClientRect(nint window)
    {
        if (!GetClientRect(window, out var rect)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var origin = new PointNative();
        if (!ClientToScreen(window, ref origin)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new RectNative { Left = origin.X, Top = origin.Y, Right = origin.X + rect.Right, Bottom = origin.Y + rect.Bottom };
    }
    private static RectNative ScreenRect(Visual visual, Rect rect)
    {
        Point a = visual.PointToScreen(rect.TopLeft), b = visual.PointToScreen(rect.BottomRight);
        return new RectNative { Left = (int)Math.Ceiling(a.X), Top = (int)Math.Ceiling(a.Y), Right = (int)Math.Floor(b.X), Bottom = (int)Math.Floor(b.Y) };
    }
    private static RectNative Intersect(RectNative a, RectNative b) => new() {
        Left = Math.Max(a.Left, b.Left), Top = Math.Max(a.Top, b.Top), Right = Math.Min(a.Right, b.Right), Bottom = Math.Min(a.Bottom, b.Bottom)
    };

    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PlacementNative {
        public uint Length, Flags, ShowCmd; public PointNative MinPosition, MaxPosition; public RectNative NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThread {
        public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public RectNative CaretRect;
    }
    private delegate bool EnumCallback(nint window, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThread info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out RectNative bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint window, out RectNative bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint window, ref PointNative point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint window, ref PlacementNative placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint window, ref PlacementNative placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int extended, string cls, string name, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
}
