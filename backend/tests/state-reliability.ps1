#Requires -Version 7.2
# Authored files and the current test process only; no QEMU, guest, or host changes.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$work = [IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp/state-reliability-" + [guid]::NewGuid().ToString('N'))
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
    $prefix = [IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp") + [IO.Path]::DirectorySeparatorChar
    if ($work.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
