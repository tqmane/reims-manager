using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ReimsManager.Services;

internal static partial class ProcessIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low, High; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] path, ref uint length);

    internal static bool IsAlive(int pid, long expectedStartTicks, string expectedExecutable)
    {
        if (!TryGetVerifiedProcess(pid, expectedStartTicks, expectedExecutable, out var process)) return false;
        process?.Dispose();
        return true;
    }

    internal static bool TryGetVerifiedProcess(int pid, long expectedStartTicks, string expectedExecutable, out Process? verified)
    {
        verified = null;
        if (pid <= 0 || expectedStartTicks <= 0 || string.IsNullOrWhiteSpace(expectedExecutable)) return false;
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }

        try {
            if (process.HasExited) { process.Dispose(); return false; }
            string expectedName = Path.GetFileNameWithoutExtension(expectedExecutable);
            if (!string.IsNullOrWhiteSpace(process.ProcessName) &&
                !string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase)) {
                process.Dispose();
                return false;
            }
            long actualTicks = process.StartTime.ToUniversalTime().Ticks;
            if (actualTicks != expectedStartTicks) {
                process.Dispose();
                return false;
            }
            string? actualExe = null;
            try { actualExe = process.MainModule?.FileName; }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { process.Dispose(); return false; }

            if (string.IsNullOrWhiteSpace(actualExe)) {
                actualExe = QueryLimitedImagePath(pid, expectedStartTicks);
                if (actualExe is null) { process.Dispose(); return false; }
            }
            if (!string.Equals(actualExe, expectedExecutable, StringComparison.OrdinalIgnoreCase)) {
                process.Dispose();
                return false;
            }
            verified = process;
            return true;
        } catch (InvalidOperationException) {
            process.Dispose();
            return false;
        }
    }

    private static string? QueryLimitedImagePath(int pid, long expectedTicks)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, checked((uint)pid));
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!GetProcessTimes(handle, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        long fileTime = ((long)created.High << 32) | created.Low;
        if (DateTime.FromFileTimeUtc(fileTime).Ticks != expectedTicks) return null;
        var buffer = new char[32768];
        uint len = (uint)buffer.Length;
        if (!QueryFullProcessImageNameW(handle, 0, buffer, ref len))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return len == 0 ? null : new string(buffer, 0, (int)len);
    }
}
