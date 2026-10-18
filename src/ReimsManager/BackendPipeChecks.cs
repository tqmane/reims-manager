using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal static class BackendPipeChecks
{
    internal static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);
        string script = Path.Combine(folder, "backend-pipe-fixture.ps1");
        File.WriteAllText(script, """
            param([string]$Root,[string]$Pwsh,[int]$Result=0,[int]$Delay=0)
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            $child=Start-Process -FilePath $Pwsh -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','"Start-Sleep -Seconds 30"' -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Root 'child-out.txt') -RedirectStandardError (Join-Path $Root 'child-err.txt') -PassThru
            $self=Get-Process -Id $PID
            $metadata=@{child=$child.Id;child_start=$child.StartTime.ToUniversalTime().Ticks;backend=$PID;backend_start=$self.StartTime.ToUniversalTime().Ticks}|ConvertTo-Json
            [IO.File]::WriteAllText((Join-Path $Root 'processes.tmp'),$metadata)
            [IO.File]::Move((Join-Path $Root 'processes.tmp'),(Join-Path $Root 'processes.json'))
            if($Delay){Start-Sleep -Milliseconds $Delay}
            [Console]::Out.Write('日本語 backend complete')
            [Console]::Error.Write('expected backend diagnostic')
            exit $Result
            """, new UTF8Encoding(false));
        await Check("success", 0, false, false);
        await Check("failure", 7, false, false);
        await Check("cancel-after-exit", 0, true, true);
        await Check("mutating-cancellation", 0, false, true);
        File.WriteAllText(Path.Combine(folder, "backend-pipe-result.json"), JsonSerializer.Serialize(new {
            launcher_exit_separate_from_eof = true, captured_unicode_output = true, nonzero_exit_diagnostic = true,
            cancellation_after_launcher_exit_preserves_child = true, mutating_cancellation_does_not_finish_early = true,
            detached_children_survive_helper_disposal = true
        }));

        async Task Check(string name, int exit, bool readOnly, bool cancel)
        {
            string root = Path.Combine(folder, name); Directory.CreateDirectory(root);
            string pwsh = Path.Combine(AppContext.BaseDirectory, "runtimes/pwsh/pwsh.exe");
            var info = new ProcessStartInfo(pwsh) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            int delay = name == "mutating-cancellation" ? 700 : 0;
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Root", root, "-Pwsh", pwsh,
                "-Result", exit.ToString(), "-Delay", delay.ToString() }) info.ArgumentList.Add(arg);
            using var cancellation = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();
            Task<string> run = BackendCommand.Run(info, null, readOnly, cancellation.Token);
            Process? child = null, backend = null;
            long childStart = 0, backendStart = 0;
            try {
                string metadata = Path.Combine(root, "processes.json");
                while (!File.Exists(metadata)) {
                    if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Pipe fixture did not publish its owned PIDs");
                    await Task.Delay(20).ConfigureAwait(false);
                }
                using var json = JsonDocument.Parse(File.ReadAllText(metadata));
                childStart = json.RootElement.GetProperty("child_start").GetInt64();
                backendStart = json.RootElement.GetProperty("backend_start").GetInt64();
                child = Process.GetProcessById(json.RootElement.GetProperty("child").GetInt32());
                if (child.StartTime.ToUniversalTime().Ticks != childStart) throw new Exception("Fixture child PID changed");
                try { backend = Process.GetProcessById(json.RootElement.GetProperty("backend").GetInt32()); }
                catch (ArgumentException) { }
                if (backend is not null && backend.StartTime.ToUniversalTime().Ticks != backendStart) throw new Exception("Fixture launcher PID changed");
                if (cancel && readOnly) {
                    while (backend is not null && !backend.HasExited) {
                        if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Launcher did not exit before cancellation");
                        await Task.Delay(20).ConfigureAwait(false);
                    }
                    cancellation.Cancel();
                } else if (cancel) cancellation.Cancel();
                try {
                    string output = await run.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                    if (exit != 0 || readOnly && cancel || output != "日本語 backend complete") throw new Exception("Launcher output/status was lost");
                } catch (IOException error) when (exit == 7 && error.Message.Contains("expected backend diagnostic")) { }
                catch (OperationCanceledException) when (readOnly && cancel) { }
                if (backend is not null && !backend.HasExited) throw new Exception("A mutating operation returned before its launcher completed");
                if (child.HasExited) throw new Exception("A detached child was killed when its launcher exited/cancelled");
                if (delay > 0 && clock.ElapsedMilliseconds < delay) throw new Exception("A mutating operation returned before its launcher completed");
            } finally {
                // Only the PIDs created by this fixture, with matching creation times, are cleaned up.
                if (child is not null && !child.HasExited && child.StartTime.ToUniversalTime().Ticks == childStart) {
                    child.Kill(); await child.WaitForExitAsync().ConfigureAwait(false);
                }
                if (backend is not null && !backend.HasExited && backend.StartTime.ToUniversalTime().Ticks == backendStart) {
                    backend.Kill(); await backend.WaitForExitAsync().ConfigureAwait(false);
                }
                try { await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                child?.Dispose(); backend?.Dispose();
            }
        }
    }
}
