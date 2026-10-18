using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ReimsManager;

internal static class ManagerControl
{
    internal const int MaximumMessage = 64 * 1024;
    internal static readonly string[] Commands = ["status", "start", "poweroff", "restart", "exit", "pause", "resume", "dock-console", "popout-console"];
    internal static string PipeName(int pid) => $"ReimsManager.{Process.GetCurrentProcess().SessionId}.{pid}";

    internal static JsonElement Failure(string code, string message, bool mayContinue = false) => JsonSerializer.SerializeToElement(new {
        schema = 1, ok = false, error = code, message, operation_may_continue = mayContinue
    });

    internal static async Task Write(Stream stream, JsonElement value, CancellationToken cancellation)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaximumMessage) throw new InvalidDataException("Control message exceeds 64 KiB.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, cancellation); await stream.WriteAsync(data, cancellation); await stream.FlushAsync(cancellation);
    }

    internal static async Task<JsonElement> Read(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessage) throw new InvalidDataException("Control message must be 1–65536 bytes.");
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, cancellation);
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
        return document.RootElement.Clone();
    }

    internal static async Task<JsonElement> Send(int pid, string command)
    {
        if (!Commands.Contains(command)) throw new ArgumentException("Unsupported control command.");
        using var target = Process.GetProcessById(pid);
        using var current = Process.GetCurrentProcess();
        if (target.SessionId != current.SessionId || target.ProcessName != current.ProcessName || pid == current.Id)
            throw new ArgumentException("Choose a Reims Manager process in this Windows session.");
        using var pipe = new NamedPipeClientStream(".", PipeName(pid), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connection = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(connection.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(command == "status" ? 35 : 240));
        await Write(pipe, JsonSerializer.SerializeToElement(new { schema = 1, command }), deadline.Token);
        // A client timeout never cancels a command already accepted by the Manager.
        var reply = await Read(pipe, deadline.Token);
        if (reply.ValueKind != JsonValueKind.Object || reply.GetProperty("schema").GetInt32() != 1 || reply.GetProperty("ok").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Unsupported control response.");
        return reply;
    }

    internal static async Task<int> RunClient(string[] arguments)
    {
        string? output = null;
        JsonElement reply;
        try {
            if (arguments.Length < 2 || !Commands.Contains(arguments[1])) throw new ArgumentException("Use --control status|start|poweroff|restart|exit|pause|resume|dock-console|popout-console [--pid PID] [--output file.json].");
            int? pid = null;
            for (int i = 2; i < arguments.Length; i++) {
                string option = arguments[i];
                if (++i >= arguments.Length) throw new ArgumentException("Missing control option value.");
                if (option == "--pid" && pid is null && int.TryParse(arguments[i], out int value) && value > 0) pid = value;
                else if (option == "--output" && output is null) output = Path.GetFullPath(arguments[i]);
                else throw new ArgumentException("Invalid control option.");
            }
            if (pid is null) {
                using var current = Process.GetCurrentProcess();
                var candidates = new List<int>();
                foreach (var process in Process.GetProcessesByName(current.ProcessName)) {
                    using (process) {
                        try { if (process.Id != current.Id && process.SessionId == current.SessionId) candidates.Add(process.Id); }
                        catch (InvalidOperationException) { }
                    }
                }
                if (candidates.Count != 1) throw new ArgumentException("Specify --pid: exactly one running Manager was not found.");
                pid = candidates[0];
            }
            reply = await Send(pid.Value, arguments[1]);
        } catch (Exception error) {
            reply = Failure(error is OperationCanceledException ? "timeout" : "control_error", error.Message,
                arguments.Length > 1 && arguments[1] != "status");
        }
        string json = reply.GetRawText();
        try { if (output is not null) File.WriteAllText(output, json + Environment.NewLine); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            reply = Failure("output_error", error.Message, arguments.Length > 1 && arguments[1] != "status");
            json = reply.GetRawText();
        }
        Console.WriteLine(json);
        return reply.GetProperty("ok").GetBoolean() ? 0 : 1;
    }
}

internal sealed class ManagerControlServer : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string, Task<JsonElement>> dispatch;
    private readonly Task listener;

    internal ManagerControlServer(Func<string, Task<JsonElement>> dispatch)
    {
        this.dispatch = dispatch;
        listener = Listen();
        if (listener.IsFaulted) listener.GetAwaiter().GetResult();
    }

    private async Task Listen()
    {
        var active = new List<Task>();
        try {
            while (!lifetime.IsCancellationRequested) {
                active.RemoveAll(task => task.IsCompleted);
                if (active.Count >= 4) { await Task.WhenAny(active).WaitAsync(lifetime.Token).ConfigureAwait(false); continue; }
                var pipe = new NamedPipeServerStream(ManagerControl.PipeName(Environment.ProcessId), PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false); }
                catch { pipe.Dispose(); throw; }
                active.Add(Handle(pipe));
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task Handle(NamedPipeServerStream pipe)
    {
        using (pipe) {
            try {
                JsonElement reply;
                try {
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid)) throw new UnauthorizedAccessException("Cannot verify the control client.");
                    using var client = Process.GetProcessById(checked((int)clientPid));
                    using var current = Process.GetCurrentProcess();
                    if (client.SessionId != current.SessionId) throw new UnauthorizedAccessException("Control is limited to the same Windows session.");
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                    var request = await ManagerControl.Read(pipe, readDeadline.Token).ConfigureAwait(false);
                    if (request.ValueKind != JsonValueKind.Object || request.GetProperty("schema").GetInt32() != 1 ||
                        request.EnumerateObject().Any(property => property.Name is not ("schema" or "command"))) throw new InvalidDataException("Unsupported control request.");
                    string command = request.GetProperty("command").GetString() ?? "";
                    if (!ManagerControl.Commands.Contains(command)) throw new InvalidDataException("Unsupported control command.");
                    reply = await dispatch(command).ConfigureAwait(false);
                } catch (Exception error) { reply = ManagerControl.Failure("request_error", error.Message); }
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                await ManagerControl.Write(pipe, reply, writeDeadline.Token);
            } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose() { lifetime.Cancel(); _ = listener; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
