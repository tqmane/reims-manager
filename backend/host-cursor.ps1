# Host viewer policy is separate from VM hardware config to avoid lost USB/config updates.
function Read-ReimsHostCursorSnapshot {
    $path=Join-Path $VmRoot 'state/host-cursor.json'
    if (!(Test-Path -LiteralPath $path)) { return @{exists=$false;bytes=$null;hash='missing';valid=$true;hidden=$false} }
    $file=Get-Item -LiteralPath $path
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint -or $file.PSIsContainer) { throw 'Host cursor policy must be an ordinary VM-owned file.' }
    if ($file.Length -gt 65536) { throw 'Host cursor policy is too large. Move state/host-cursor.json aside in this VM folder, then use ShowHostCursor.' }
    $bytes=[IO.File]::ReadAllBytes($path)
    $snapshot=@{exists=$true;bytes=$bytes;hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));valid=$false;hidden=$false}
    try {
        $value=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json -AsHashtable -ErrorAction Stop
        if ($value -is [Collections.IDictionary] -and $value.Count -eq 2 -and $value.Contains('schema') -and $value.Contains('host_cursor_hidden') -and
            ($value.schema -is [int] -or $value.schema -is [long]) -and $value.schema -eq 1 -and $value.host_cursor_hidden -is [bool]) {
            $snapshot.valid=$true;$snapshot.hidden=$value.host_cursor_hidden
        }
    } catch { }
    return $snapshot
}

function Get-ReimsHostCursorHidden {
    $snapshot=Read-ReimsHostCursorSnapshot
    if (!$snapshot.valid) { throw 'Invalid typed host cursor policy in state/host-cursor.json. ShowHostCursor can back it up and reset it.' }
    return [bool]$snapshot.hidden
}

function Remove-ReimsHostCursorLaunchPolicy([string[]]$Arguments) {
    for($i=0;$i -lt $Arguments.Count;$i++) {
        if ($Arguments[$i] -eq '-device' -and $i+1 -lt $Arguments.Count -and $Arguments[$i+1].StartsWith('{')) {
            $device=$Arguments[$i+1]|ConvertFrom-Json -AsHashtable
            if ($device['driver'] -eq 'apple-gfx-pci' -and $device['id'] -eq 'gfx') {
                if ($device.Contains('hide-host-cursor')) {
                    if ($device['hide-host-cursor'] -isnot [bool]) { throw 'Invalid host cursor bool in recorded launch arguments.' }
                    $device.Remove('hide-host-cursor')
                }
                $Arguments[$i]
                $device|ConvertTo-Json -Depth 4 -Compress
                $i++;continue
            }
        }
        $Arguments[$i]
    }
}

function Assert-ReimsHostCursorUnchanged($Before) {
    $current=Read-ReimsHostCursorSnapshot
    if ($current.hash -cne $Before.hash -or $current.exists -ne $Before.exists) { throw 'Host cursor policy changed concurrently; refusing to overwrite it.' }
}

function Write-ReimsHostCursorPolicy([bool]$Hidden, $Before) {
    $path=Join-Path $VmRoot 'state/host-cursor.json'
    $temporary=Join-Path $VmRoot ('state/.host-cursor-'+[guid]::NewGuid().ToString('N')+'.json')
    try {
        # Use the existing atomic JSON writer on a unique staging path.
        Write-ReimsJson $temporary @{schema=1;host_cursor_hidden=$Hidden}
        Assert-ReimsHostCursorUnchanged $Before
        [IO.File]::Move($temporary,$path,$true)
    } finally {
        foreach($owned in @($temporary,"$temporary.new")) {
            if (Test-Path -LiteralPath $owned) {
                $item=Get-Item -LiteralPath $owned
                if (!$item.PSIsContainer -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Remove-Item -LiteralPath $owned -Force }
            }
        }
    }
}

function Get-ReimsLiveHostCursor($Runtime) {
    $value=Invoke-ReimsQmp $Runtime 'qom-get' @{path='/machine/peripheral/gfx';property='hide-host-cursor'}
    # Invoke-ReimsQmp protects array results from pipeline enumeration.
    if ($value -is [Array] -and $value.Count -eq 1) { $value=$value[0] }
    if ($value -isnot [bool]) { throw 'QEMU returned a non-boolean host cursor policy.' }
    return $value
}

function Set-ReimsLiveHostCursor($Runtime,[bool]$Hidden) {
    Invoke-ReimsQmp $Runtime 'qom-set' @{path='/machine/peripheral/gfx';property='hide-host-cursor';value=$Hidden} | Out-Null
    if ((Get-ReimsLiveHostCursor $Runtime) -ne $Hidden) { throw 'QEMU did not confirm the requested host cursor policy.' }
}

function Set-ReimsHostCursor([bool]$Hidden) {
    $canonical=[IO.Path]::GetFullPath($VmRoot).TrimEnd('\','/').ToUpperInvariant()
    $identity=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    $mutex=[Threading.Mutex]::new($false,"Local\ReimsManager.HostCursor.$identity")
    $locked=$false
    try {
        try { $locked=$mutex.WaitOne(5000) } catch [Threading.AbandonedMutexException] { $locked=$true }
        if (!$locked) { throw 'Another host cursor operation is in progress for this VM.' }
        $snapshot=Read-ReimsHostCursorSnapshot
        if ($Hidden -and !$snapshot.valid) { throw 'Invalid typed host cursor policy. Use ShowHostCursor to back it up and reset it first.' }
        $runtime=Get-ReimsRuntime
        $live=$runtime -and (Get-ReimsVmProcess $runtime)
        $state=if($live){(Invoke-ReimsQmp $runtime 'query-status').status}else{'stopped'}
        if ($Hidden -and $state -ne 'running') { throw 'HideHostCursor requires a running VM.' }
        if (!$Hidden -and $state -notin @('running','paused','shutdown','prelaunch','suspended','stopped')) { throw "Cannot change host cursor policy while QEMU is $state." }
        $previous=$null;$attempted=$false
        try {
            if ($live) {
                try { $previous=Get-ReimsLiveHostCursor $runtime }
                catch { throw "This engine lacks usable live host cursor control. Update the engine and use an owned Reims native window. $($_.Exception.Message)" }
                $attempted=$true
                try { Set-ReimsLiveHostCursor $runtime $Hidden }
                catch { throw "QEMU refused host cursor control; a supported engine and live Reims-owned native window are required. $($_.Exception.Message)" }
            }
            Assert-ReimsHostCursorUnchanged $snapshot
            if (!$snapshot.valid) {
                $backup=Join-Path $VmRoot ('state/host-cursor.recovery-'+[guid]::NewGuid().ToString('N')+'.json')
                $stream=[IO.File]::Open($backup,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try { $stream.Write($snapshot.bytes,0,$snapshot.bytes.Length);$stream.Flush($true) } finally { $stream.Dispose() }
            }
            Write-ReimsHostCursorPolicy $Hidden $snapshot
        } catch {
            $failure=$_.Exception.Message
            if ($attempted -and $previous -ne $Hidden) {
                try {
                    if ((Get-ReimsLiveHostCursor $runtime) -eq $Hidden) { Set-ReimsLiveHostCursor $runtime $previous }
                } catch { $failure += " Rollback unconfirmed: $($_.Exception.Message)" }
            }
            throw $failure
        }
        return @{schema=1;host_cursor_hidden=$Hidden;live_qmp=[bool]$live}
    } finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
