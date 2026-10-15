#Requires -Version 7.2
. "$PSScriptRoot/../engine-package.ps1"
$test=Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../.work")) ('native-timeout-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $test | Out-Null
try {
    # Compile a small .NET Framework fixture using Windows' built-in compiler.
    $builder=@'
param([string]$Output)
$ErrorActionPreference='Stop'
Add-Type -OutputAssembly $Output -OutputType ConsoleApplication -TypeDefinition @"
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
public static class ProbeFixture {
    public static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "--child") { Thread.Sleep(30000); return; }
        using (var self = Process.GetCurrentProcess()) {
            File.WriteAllText("owned-pids.txt", self.Id + " " + self.StartTime.ToUniversalTime().Ticks + Environment.NewLine);
            var info = new ProcessStartInfo(self.MainModule.FileName, "--child") { UseShellExecute=false, CreateNoWindow=true };
            using (var child = Process.Start(info)) {
                File.AppendAllText("owned-pids.txt", child.Id + " " + child.StartTime.ToUniversalTime().Ticks + Environment.NewLine);
                Thread.Sleep(30000);
            }
        }
    }
}
"@
'@
    $builder | Set-Content -LiteralPath "$test/build.ps1" -Encoding utf8NoBOM
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$test/build.ps1" -Output "$test/fixture.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Timeout fixture compilation failed.' }
    function Read-ReimsEngine { @{paths=@{qemu='fixture.exe'}} }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $caught=$false
    try { Test-ReimsNativeEngine $test -TimeoutMilliseconds 2000 }
    catch { if ($_.Exception.Message -notmatch 'timed out and its owned process tree was terminated') { throw }; $caught=$true }
    if (!$caught -or $watch.Elapsed.TotalSeconds -gt 15) { throw 'Native timeout was not bounded.' }
    $owned=@(Get-Content -LiteralPath "$test/owned-pids.txt")
    if ($owned.Count -ne 2) { throw 'Fixture did not start both parent and descendant.' }
    foreach ($line in $owned) {
        $identity=$line.Split(' ')
        $child=Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
        if ($child -and $child.StartTime.ToUniversalTime().Ticks -eq [long]$identity[1]) {
            try { if (!$child.WaitForExit(5000)) { throw 'An owned fixture process survived the timeout.' } }
            finally { $child.Dispose() }
        }
    }
    "PASS: native timeout terminated its owned parent and descendant in $([Math]::Round($watch.Elapsed.TotalSeconds,1))s"
} finally {
    # On a failed assertion, clean up only this fixture's recorded process identities.
    if (Test-Path -LiteralPath "$test/owned-pids.txt") {
        foreach ($line in Get-Content -LiteralPath "$test/owned-pids.txt") {
            $identity=$line.Split(' ')
            $child=Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
            if ($child) {
                try {
                    if ($child.StartTime.ToUniversalTime().Ticks -eq [long]$identity[1] -and !$child.HasExited) { $child.Kill($true); [void]$child.WaitForExit(5000) }
                } finally { $child.Dispose() }
            }
        }
    }
    $root=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work")+[IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($test).StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $test -Recurse -Force }
}
