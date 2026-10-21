#Requires -Version 7.2
# Authored files, loopback protocol peers and test processes only; no QEMU or guest.
param([string]$QmpSource = "$PSScriptRoot/../start.ps1")
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$work = Join-Path ([IO.Path]::GetTempPath()) ('reims-state-reliability-' + [guid]::NewGuid().ToString('N'))
$ProjectRoot = $work
$VmRoot = Join-Path $work 'Virtual Machines/日本語 fixture'
$scripts = Join-Path $work 'backend'
$script:ReimsEngine = $null
[void][IO.Directory]::CreateDirectory($scripts)
[void][IO.Directory]::CreateDirectory((Join-Path $VmRoot 'state'))
$configPath = Join-Path $VmRoot 'state/config.json'
$runtimePath = Join-Path $VmRoot 'state/runtime.json'

function Assert-True($Value, [string]$Message) { if (!$Value) { throw $Message } }
function Reject([scriptblock]$Call, [string]$Pattern) {
    $message = $null
    try { & $Call | Out-Null } catch { $message = $_.Exception.Message }
    if (!$message -or ($Pattern -and $message -notmatch $Pattern)) { throw "Expected '$Pattern'; got '$message'" }
}

$start = [scriptblock]::Create((Get-Content -Raw "$PSScriptRoot/../start.ps1")).Ast
foreach ($name in @('Assert-ReimsArtifactPath', 'Write-ReimsJson', 'Enter-ReimsVmMutation', 'Get-ReimsVmConfig', 'Get-ReimsVmProcess', 'Get-ReimsRuntime', 'Assert-ReimsVmStopped', 'Invoke-ReimsQmp', 'Get-ReimsNetworkMode')) {
    $definition = $start.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
$qmpAst=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($QmpSource),[ref]$null,[ref]$null)
$qmpDefinition=$qmpAst.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-ReimsQmp'},$true)
if(!$qmpDefinition){throw 'Missing production Invoke-ReimsQmp fixture source.'}
. ([scriptblock]::Create($qmpDefinition.Extent.Text))
$realWriter = (Get-Command Write-ReimsJson).ScriptBlock
$current = Get-Process -Id $PID
$runtime = [pscustomobject]@{
    pid = $PID; start_ticks = $current.StartTime.ToUniversalTime().Ticks; executable = $current.Path
    qmp_port = 4444; run_directory = Join-Path $VmRoot 'logs/fixture'
}
$config = [pscustomobject]@{
    schema = 1; cpus = 6; memory_mib = 12288; resolution = '1920x1080'; disk_gib = 300
    base_disk = Join-Path $VmRoot 'disks/base.qcow2'; active_disk = Join-Path $VmRoot 'disks/current.qcow2'
    opencore = Join-Path $VmRoot 'firmware/opencore.img'; firmware_code = Join-Path $VmRoot 'firmware/code.fd'
    firmware_vars = Join-Path $VmRoot 'firmware/vars.fd'; rom = Join-Path $VmRoot 'firmware/ReimsVgpu.rom'
    qmp_port = 4444; ssh_port = 2222; iso = ''; audio = 'dsound'; microphone = $true; usb_backend = 'usbdk'
    connect_installer = $false; usb_devices = @([pscustomobject]@{ id = 'retained-usb' })
    extension_field = [pscustomobject]@{ retained = '未対応の設定も保持' }
}
try {
    Write-ReimsJson $configPath $config
    Assert-True ((Get-ReimsVmProcess $runtime).Id -eq $PID) 'A fully matching live identity was lost'
    $stale = $runtime.PSObject.Copy(); $stale.start_ticks--
    Write-ReimsJson $runtimePath $stale
    Assert-True ($null -eq (Get-ReimsVmProcess $stale)) 'A reused PID blocked restart'
    Assert-ReimsVmStopped
    Reject { Invoke-ReimsQmp $stale 'query-status' } 'no longer running'
    $stale = $runtime.PSObject.Copy(); $stale.executable += '.old'
    Assert-True ($null -eq (Get-ReimsVmProcess $stale)) 'A different executable was treated as this VM'
    # Missing permissions are not evidence that a matching process has stopped.
    function Get-Process {
        [CmdletBinding()]param([int]$Id)
        if ($script:LookupDenied) { Write-Error 'Authored access denied' -Category PermissionDenied; return }
        [pscustomobject]@{ Id = $Id; StartTime = $current.StartTime; Path = $null }
    }
    $script:LookupDenied = $false
    Reject { Get-ReimsVmProcess $runtime } 'Cannot verify the executable'
    $script:LookupDenied = $true
    Reject { Get-ReimsVmProcess $runtime } 'Cannot verify the recorded VM process'
    Remove-Item Function:Get-Process
    Remove-Item -LiteralPath $runtimePath

    # A stale/concurrent legacy staging file must not be consumed by a new save.
    [IO.File]::WriteAllText("$configPath.new", 'belongs to another pending writer')
    $config.cpus = 7
    Write-ReimsJson $configPath $config
    Assert-True ((Get-ReimsVmConfig).cpus -eq 7) 'A complete saved profile did not round trip'
    Assert-True ([IO.File]::ReadAllText("$configPath.new") -ceq 'belongs to another pending writer') 'A save consumed another writer staging file'
    $before = [IO.File]::ReadAllText($configPath)
    $deep = @{ leaf = 'must not be truncated' }
    for ($i = 0; $i -lt 25; $i++) { $deep = @{ child = $deep } }
    Reject { Write-ReimsJson $configPath $deep } 'depth'
    Assert-True ([IO.File]::ReadAllText($configPath) -ceq $before) 'Serialization failure replaced the last saved profile'
    $blocked = Join-Path $VmRoot 'state/blocked.json'
    [void][IO.Directory]::CreateDirectory($blocked)
    Reject { Write-ReimsJson $blocked @{ schema = 1 } } ''
    Assert-True (@(Get-ChildItem (Split-Path $configPath) -Filter '*.tmp').Count -eq 0) 'Failed atomic replacement leaked a staging file'
    if ([IO.Path]::DirectorySeparatorChar -eq '\') {
        $held = [IO.File]::Open($configPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { Reject { Write-ReimsJson $configPath @{ broken = $true } } '' }
        finally { $held.Dispose() }
        Assert-True ([IO.File]::ReadAllText($configPath) -ceq $before) 'Sharing violation destroyed the previous profile'
    }

    # A second Manager process cannot start/mutate this VM concurrently.
    $lockSource = $start.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Enter-ReimsVmMutation' }, $true).Extent.Text
    $lockReady = Join-Path $work 'lock-ready'; $lockRelease = Join-Path $work 'lock-release'
    $lockJob = Start-Job -ArgumentList $lockSource, $VmRoot, $lockReady, $lockRelease -ScriptBlock {
        param($Source, $Directory, $Ready, $Release)
        $ErrorActionPreference = 'Stop'; $VmRoot = $Directory
        . ([scriptblock]::Create($Source))
        $held = Enter-ReimsVmMutation
        try {
            [IO.File]::WriteAllText($Ready, 'locked')
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while (!(Test-Path -LiteralPath $Release) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        } finally { $held.ReleaseMutex(); $held.Dispose() }
    }
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (!(Test-Path -LiteralPath $lockReady)) {
            if ($lockJob.State -in @('Failed', 'Completed') -or [DateTime]::UtcNow -ge $deadline) { throw 'Mutation lock fixture failed to start' }
            Start-Sleep -Milliseconds 50
        }
        Reject { Enter-ReimsVmMutation -TimeoutMilliseconds 100 } 'Another VM operation'
        [IO.File]::WriteAllText($lockRelease, 'release')
        Receive-Job $lockJob -Wait -ErrorAction Stop | Out-Null
        $held = Enter-ReimsVmMutation -TimeoutMilliseconds 100
        $held.ReleaseMutex(); $held.Dispose()
    } finally {
        Stop-Job $lockJob -ErrorAction SilentlyContinue
        Remove-Job $lockJob -Force
    }

    # Two independent PowerShell clients use the production connection helper.
    # The server understands only authored QMP-shaped JSON on an ephemeral port.
    & {
        $jobs=[Collections.Generic.List[object]]::new()
        function Wait-FixtureFile([string]$Path) {
            $deadline=[DateTime]::UtcNow.AddSeconds(8)
            while(![IO.File]::Exists($Path)) {
                if([DateTime]::UtcNow -ge $deadline){throw "Loopback fixture did not become ready: $([IO.Path]::GetFileName($Path))"}
                Start-Sleep -Milliseconds 20
            }
        }
        function Complete-FixtureJob($Job) {
            if(!(Wait-Job $Job -Timeout 12)){throw 'Loopback fixture exceeded its deadline.'}
            Receive-Job $Job -ErrorAction Stop
        }
        function Start-QmpPeer([string]$Mode) {
            $directory=Join-Path $work ([guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($directory)
            $job=Start-Job -ArgumentList $directory,$Mode -ScriptBlock {
                param($Directory,$Mode)
                $ErrorActionPreference='Stop'
                $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
                $listener.Start(8)
                try {
                    [IO.File]::WriteAllText((Join-Path $Directory 'port'),[string]$listener.LocalEndpoint.Port)
                    $limit=if($Mode -eq 'serial'){2}else{1}
                    for($i=0;$i -lt $limit;$i++) {
                        $accept=$listener.AcceptTcpClientAsync()
                        if(!$accept.Wait(6000)){throw 'Fixture did not receive its expected client.'}
                        $client=$accept.Result
                        [IO.File]::AppendAllText((Join-Path $Directory 'accepted'),"accepted`n")
                        try {
                            $stream=$client.GetStream();$stream.ReadTimeout=5000;$stream.WriteTimeout=1000
                            $reader=[IO.StreamReader]::new($stream)
                            $writer=[IO.StreamWriter]::new($stream);$writer.AutoFlush=$true
                            if($Mode -eq 'bad-greeting'){$writer.WriteLine('{"fixture":true}');continue}
                            $writer.WriteLine('{"QMP":{"capabilities":[]}}')
                            $caps=$reader.ReadLine()|ConvertFrom-Json
                            if($caps.execute -cne 'qmp_capabilities'){throw 'Unexpected fixture handshake.'}
                            $writer.WriteLine((@{return=@{};id=$caps.id}|ConvertTo-Json -Compress))
                            $command=$reader.ReadLine()|ConvertFrom-Json
                            if($command.execute -cne 'query-status'){throw 'Unexpected fixture command.'}
                            [IO.File]::AppendAllText((Join-Path $Directory 'commands'),"command`n")
                            if($Mode -eq 'serial' -and $i -eq 0) {
                                [IO.File]::WriteAllText((Join-Path $Directory 'command-ready'),'ready')
                                $deadline=[DateTime]::UtcNow.AddSeconds(5)
                                while(![IO.File]::Exists((Join-Path $Directory 'reply-release'))) {
                                    if($listener.Pending()){throw 'Another client connected before the current lease was released.'}
                                    if([DateTime]::UtcNow -ge $deadline){throw 'Fixture reply release timed out.'}
                                    Start-Sleep -Milliseconds 20
                                }
                            }
                            if($Mode -eq 'command-error') {
                                $reply=@{error=@{class='GenericError';desc='authored command failure'};id=$command.id}
                            } else {$reply=@{return=@{status='running'};id=$command.id}}
                            $writer.WriteLine(($reply|ConvertTo-Json -Depth 4 -Compress))
                            # EOF must arrive before the next client holds the lease.
                            if($reader.ReadLine() -ne $null){throw 'Unexpected command replay on the same connection.'}
                        } finally {$client.Dispose()}
                    }
                    if($Mode -ne 'serial') {
                        $deadline=[DateTime]::UtcNow.AddMilliseconds(400)
                        do {
                            if($listener.Pending()){throw 'A failed greeting or command was replayed.'}
                            Start-Sleep -Milliseconds 20
                        } while([DateTime]::UtcNow -lt $deadline)
                    }
                } finally {$listener.Stop()}
            }
            $jobs.Add($job);Wait-FixtureFile (Join-Path $directory 'port')
            return @{job=$job;directory=$directory;port=[int][IO.File]::ReadAllText((Join-Path $directory 'port'))}
        }
        function Start-QmpClient($Peer,[string]$Name) {
            $identity=Join-Path $Peer.directory "$Name-identity"
            $checked=Join-Path $Peer.directory "$Name-checked"
            [IO.File]::WriteAllText($identity,'alive')
            $job=Start-Job -ArgumentList $qmpDefinition.Extent.Text,$Peer.port,$identity,$checked -ScriptBlock {
                param($Source,$Port,$Identity,$Checked)
                $ErrorActionPreference='Stop'
                . ([scriptblock]::Create($Source))
                function Get-ReimsVmProcess($Runtime) {
                    [IO.File]::WriteAllText($Checked,'checked')
                    return [IO.File]::ReadAllText($Identity) -ceq 'alive'
                }
                try {
                    $result=Invoke-ReimsQmp ([pscustomobject]@{qmp_port=$Port}) query-status
                    @{ok=$true;status=$result.status;error=''}
                } catch {@{ok=$false;status='';error=$_.Exception.Message}}
            }
            $jobs.Add($job)
            return @{job=$job;identity=$identity;checked=$checked}
        }
        function Assert-QmpLeaseReleased([int]$Port) {
            $mutex=[Threading.Mutex]::new($false,"Local\ReimsManager.Qmp.$Port")
            try {
                Assert-True ($mutex.WaitOne(100)) 'A failed QMP connection retained the shared lease'
                $mutex.ReleaseMutex()
            } finally {$mutex.Dispose()}
        }
        $lease=$null;$held=$false
        try {
            $peer=Start-QmpPeer serial
            $lease=[Threading.Mutex]::new($false,"Local\ReimsManager.Qmp.$($peer.port)")
            $held=$lease.WaitOne(100);Assert-True $held 'Could not hold fixture peer lease'
            $first=Start-QmpClient $peer first;$second=Start-QmpClient $peer second
            Wait-FixtureFile $first.checked;Wait-FixtureFile $second.checked
            Assert-True (![IO.File]::Exists((Join-Path $peer.directory 'accepted'))) 'A PowerShell client connected while a cooperating owner held the lease'
            $lease.ReleaseMutex();$held=$false
            Wait-FixtureFile (Join-Path $peer.directory 'command-ready')
            Start-Sleep -Milliseconds 150
            [IO.File]::WriteAllText((Join-Path $peer.directory 'reply-release'),'release')
            foreach($client in @($first,$second)) {
                $reply=Complete-FixtureJob $client.job
                Assert-True ($reply.ok -and $reply.status -eq 'running') 'Serialized QMP client did not receive its own reply'
            }
            Complete-FixtureJob $peer.job | Out-Null
            Assert-True ([IO.File]::ReadAllLines((Join-Path $peer.directory 'accepted')).Count -eq 2) 'The fixture did not see exactly two serial connections'
            Assert-QmpLeaseReleased $peer.port
            $lease.Dispose();$lease=$null

            foreach($mode in @('bad-greeting','command-error')) {
                $peer=Start-QmpPeer $mode
                $client=Start-QmpClient $peer $mode
                $reply=Complete-FixtureJob $client.job
                Assert-True (!$reply.ok -and $reply.error -match 'QMP greeting|authored command failure') 'A failed handshake/command was reported as success'
                Complete-FixtureJob $peer.job | Out-Null
                Assert-True ([IO.File]::ReadAllLines((Join-Path $peer.directory 'accepted')).Count -eq 1) 'QMP replayed a request after connecting'
                Assert-QmpLeaseReleased $peer.port
            }

            $peer=Start-QmpPeer identity
            $lease=[Threading.Mutex]::new($false,"Local\ReimsManager.Qmp.$($peer.port)")
            $held=$lease.WaitOne(100)
            $client=Start-QmpClient $peer identity
            Wait-FixtureFile $client.checked
            [IO.File]::WriteAllText($client.identity,'gone')
            $lease.ReleaseMutex();$held=$false
            $reply=Complete-FixtureJob $client.job
            Assert-True (!$reply.ok -and $reply.error -match 'no longer running') 'QMP did not recheck process identity after waiting for the lease'
            Assert-True (![IO.File]::Exists((Join-Path $peer.directory 'accepted'))) 'A stale VM identity reached the TCP server'
            Assert-QmpLeaseReleased $peer.port
            'PASS: shared QMP lease serializes two PowerShell clients; failed greeting/command is not replayed; identity is rechecked after waiting; leases are released.'
        } catch {
            foreach($job in $jobs) {
                Write-Warning "Loopback fixture job $($job.Id): $($job.State)"
                Receive-Job $job -Keep -ErrorAction Continue | Out-String | Write-Warning
            }
            throw
        } finally {
            if($held){$lease.ReleaseMutex()}
            if($lease){$lease.Dispose()}
            foreach($job in $jobs){Stop-Job $job -ErrorAction SilentlyContinue;Remove-Job $job -Force -ErrorAction SilentlyContinue}
        }
    }

    # Execute the actual action body with a guest response, not a second copy of its logic.
    $backend = [scriptblock]::Create((Get-Content -Raw "$PSScriptRoot/../manager-backend.ps1")).Ast
    $actions = $backend.Find({ param($node) $node -is [Management.Automation.Language.SwitchStatementAst] }, $true)
    $apply = $actions.Clauses | Where-Object { $_.Item1.Value -eq 'ApplyResolution' }
    # Extracted functions resolve script: state in each generated entrypoint.
    $scriptState = '$script:ReimsEngine = $null' + [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $scripts 'apply-resolution.ps1'), $scriptState + '& ' + $apply.Item2.Extent.Text)
    Copy-Item -LiteralPath "$PSScriptRoot/../guest-display.js" -Destination $scripts
    $Resolution = '2560x1440'
    # Share mock controls by reference across generated script scopes.
    $fixture = @{
        GuestReply = '{"display_applied":"2560x1440"}'
        GuestFailure = $false; GuestUpdatesUsb = $false; CommitFailure = $false
    }
    function Invoke-GuestControl($Command) {
        if ($fixture.GuestFailure) { throw 'Authored guest failure' }
        if ($fixture.GuestUpdatesUsb) {
            $updated = Get-ReimsVmConfig
            $updated.usb_devices = @([pscustomobject]@{ id = 'usb-saved-during-ssh' })
            & $realWriter $configPath $updated
        }
        $fixture.GuestReply
    }
    function Write-ReimsJson($Path, $Value) {
        if ($fixture.CommitFailure -and $Path -eq $configPath) { throw 'Authored persistence failure' }
        & $realWriter $Path $Value
    }
    $fixture.GuestFailure = $true
    Reject { & "$scripts/apply-resolution.ps1" } 'Authored guest failure'
    $fixture.GuestFailure = $false; $fixture.GuestReply = '{"display_applied":"1280x720"}'
    Reject { & "$scripts/apply-resolution.ps1" } '確認できません'
    Assert-True ([IO.File]::ReadAllText($configPath) -ceq $before) 'Unconfirmed display change modified boot preferences'
    $fixture.GuestReply = '{"display_applied":"2560x1440"}'; $fixture.CommitFailure = $true
    Reject { & "$scripts/apply-resolution.ps1" } '次回起動用の設定を保存できません'
    Assert-True ([IO.File]::ReadAllText($configPath) -ceq $before) 'Failed display persistence lost the old profile'
    $fixture.CommitFailure = $false; $fixture.GuestUpdatesUsb = $true
    & "$scripts/apply-resolution.ps1" | Out-Null
    $saved = Get-ReimsVmConfig
    Assert-True ($saved.resolution -eq $Resolution -and $saved.cpus -eq 7 -and $saved.audio -eq 'dsound' -and $saved.microphone) 'Live resolution did not persist independently of other hardware'
    Assert-True ($saved.usb_devices[0].id -eq 'usb-saved-during-ssh' -and $saved.extension_field.retained -eq $config.extension_field.retained) 'Resolution overwrote settings saved during SSH'

    # Status reports the saved next-boot preference separately from current media.
    $status = $backend.Find({ param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -eq '$Action -eq ''Status'''
    }, $true)
    [IO.File]::WriteAllText((Join-Path $scripts 'status.ps1'), $scriptState + '& ' + $status.Clauses[0].Item2.Extent.Text)
    Copy-Item -LiteralPath "$PSScriptRoot/../snapshot.ps1" -Destination $scripts
    [IO.File]::WriteAllText((Join-Path $scripts 'start.ps1'), 'param([switch]$FunctionsOnly)' + [Environment]::NewLine + $scriptState)
    function Get-NetAdapter { @() }
    function Get-ReimsTapAdapters { @() }
    function Get-ReimsHostCursorHidden { $false }
    function Get-ReimsWifiStatus { @{ schema = 1 } }
    function Invoke-ReimsQmp { @{ status = 'running' } }
    Write-ReimsJson $runtimePath $runtime
    Write-ReimsJson (Join-Path $runtime.run_directory 'command.json') @{ arguments = @('-drive', 'id=installer,if=none') }
    $reported = & "$scripts/status.ps1" | ConvertFrom-Json
    Assert-True ($reported.with_installer -and !$reported.connect_installer) 'Live attached media replaced saved false'
    Assert-True ($null -eq $reported.usb_backend_active -and $reported.usb_usbdk_installed -is [bool]) 'An older runtime claimed an unrecorded USB backend'
    $runtime | Add-Member -Force usb_backend 'winusb'; Write-ReimsJson $runtimePath $runtime
    $saved | Add-Member -Force usb_backend 'usbdk'
    $saved.connect_installer = $true; Write-ReimsJson $configPath $saved
    Write-ReimsJson (Join-Path $runtime.run_directory 'command.json') @{ arguments = @() }
    $reported = & "$scripts/status.ps1" | ConvertFrom-Json
    Assert-True (!$reported.with_installer -and $reported.connect_installer) 'Live detached media replaced saved true'
    Assert-True ($reported.usb_backend -eq 'usbdk' -and $reported.usb_backend_active -eq 'winusb') 'Saved USB backend replaced the running QEMU backend'
    Remove-Item -LiteralPath $runtimePath
    $reported = & "$scripts/status.ps1" | ConvertFrom-Json
    Assert-True ($null -eq $reported.usb_backend_active -and $reported.usb_backend -eq 'usbdk') 'A stopped VM reported an active USB backend'

    # A failed active-overlay save must not publish a checkpoint of a writable disk.
    foreach ($path in @($saved.active_disk, $saved.base_disk, $saved.firmware_vars, $saved.firmware_code, $saved.opencore)) {
        [void][IO.Directory]::CreateDirectory((Split-Path $path))
        [IO.File]::WriteAllText($path, 'authored fixture')
    }
    function Get-ReimsTool { 'fixture-qemu-img' }
    function Invoke-ReimsTool($Program, $Arguments) {
        if ($Arguments[0] -eq 'create') { [IO.File]::WriteAllText($Arguments[-1], 'authored overlay') }
        elseif ($Arguments[0] -ne 'check') { throw 'Unexpected fixture disk operation' }
    }
    $fixture.CommitFailure = $true
    Reject { & "$scripts/snapshot.ps1" -Action Create -Name failed } 'Authored persistence failure'
    Assert-True ((Get-ReimsVmConfig).active_disk -eq $saved.active_disk) 'Failed checkpoint commit changed the active disk'
    Assert-True (@(& "$scripts/snapshot.ps1" -Action List).Count -eq 0) 'An unfrozen checkpoint was exposed after save failure'
    Assert-True (!(Test-Path (Join-Path $VmRoot 'snapshots/failed/snapshot.json'))) 'Failed checkpoint published restore metadata'
    $fixture.CommitFailure = $false
    & "$scripts/snapshot.ps1" -Action Create -Name committed | Out-Null
    $checkpoint = @(& "$scripts/snapshot.ps1" -Action List)
    Assert-True ($checkpoint.Count -eq 1 -and $checkpoint[0].disk -eq $saved.active_disk -and (Get-ReimsVmConfig).active_disk -ne $checkpoint[0].disk) 'Committed checkpoint was not frozen before publication'
    'PASS: stale PID recovery and unknown-identity refusal; atomic profile saves; per-VM cross-process mutation exclusion; verified live resolution persistence; saved/current installer separation; failed snapshot commit visibility. No VM or guest was started.'
} finally {
    if (Test-Path Function:Get-Process) { Remove-Item Function:Get-Process }
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($work.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
