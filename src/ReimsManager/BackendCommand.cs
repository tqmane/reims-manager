using System.Diagnostics;
using System.IO;
using System.Text;

namespace ReimsManager;

internal static class BackendCommand
{
    internal static async Task<string> Run(ProcessStartInfo info, string? input, bool readOnly, CancellationToken cancellation = default)
    {
        using var deadline = readOnly ? CancellationTokenSource.CreateLinkedTokenSource(cancellation) : new CancellationTokenSource();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        // Only read-only helper processes may be cancelled. Never kill a disk copy or QEMU to meet a UI timeout.
        if (readOnly) deadline.CancelAfter(TimeSpan.FromSeconds(30));
        if (readOnly) cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException("管理プロセスを起動できませんでした。");
        using var kill = deadline.Token.Register(() => {
            if (!readOnly) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        Task<string> stdout = info.RedirectStandardOutput ? Capture(process.StandardOutput, capture.Token) : Task.FromResult("");
        Task<string> stderr = info.RedirectStandardError ? Capture(process.StandardError, capture.Token) : Task.FromResult("");
        try {
            if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            // A detached QEMU may retain inherited pipe handles after its launcher exits.
            // Finish draining the launcher's buffered output, without waiting for QEMU's EOF.
            capture.CancelAfter(TimeSpan.FromSeconds(2));
            string output = await stdout.ConfigureAwait(false), error = await stderr.ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? $"操作に失敗しました ({process.ExitCode})。" : error.Trim());
            return output.Trim();
        } finally {
            capture.Cancel();
            // Observe both read tasks even on cancellation and drain redirected pipes concurrently.
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (deadline.IsCancellationRequested) { }
        }
    }

    private static async Task<string> Capture(StreamReader reader, CancellationToken token)
    {
        const int maximum = 1024 * 1024;
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        bool truncated = false;
        int count;
        try {
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0) {
                int available = maximum - result.Length;
                if (available < count) truncated = true;
                if (available > 0) result.Append(buffer, 0, Math.Min(available, count));
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        if (truncated) throw new InvalidDataException("管理プロセスの出力が上限を超えました。診断ログを確認してください。");
        return result.ToString();
    }
}
