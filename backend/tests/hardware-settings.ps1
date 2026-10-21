# Actual SetHardware dispatch, with authored host facts and network-call traps.
$ErrorActionPreference='Stop'
$temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
$root=Join-Path $temporary ('reims-hardware-settings-'+[guid]::NewGuid().ToString('N'))
$stub=Join-Path $root 'backend';$vm=Join-Path $root 'selected VM'
[void][IO.Directory]::CreateDirectory($stub)
[void][IO.Directory]::CreateDirectory((Join-Path $vm 'state'))
Copy-Item -LiteralPath "$PSScriptRoot/../manager-backend.ps1","$PSScriptRoot/../manager-ports.ps1" -Destination $stub
[IO.File]::WriteAllText((Join-Path $stub 'start.ps1'),@'
param([switch]$FunctionsOnly)
$VmRoot=$env:REIMS_VM_ROOT_OVERRIDE
function Enter-ReimsVmMutation { $null }
function Assert-ReimsVmStopped { if(Test-Path (Join-Path $VmRoot 'running')){throw 'Stop the VM first'} }
function Get-ReimsVmConfig { Get-Content -LiteralPath (Join-Path $VmRoot 'state/config.json') -Raw|ConvertFrom-Json }
function Assert-ReimsUsbBackendChange($Config,$Backend) { if($Backend -ne $Config.usb_backend){throw 'USB fixture backend mismatch'} }
function Get-CimInstance { [pscustomobject]@{NumberOfLogicalProcessors=8;TotalPhysicalMemory=64GB} }
function Get-ReimsTapAdapters {
    [IO.File]::AppendAllText((Join-Path $VmRoot 'network-calls.txt'),"tap`n")
    [pscustomobject]@{Name='fixture TAP'}
}
function Write-ReimsJson($Path,$Value) { [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 10)) }
'@)
[IO.File]::WriteAllText((Join-Path $stub 'network-uplinks.ps1'),@'
function Get-ReimsSavedNetwork($Config,$Mode,$Adapter,$GuestHost,$PhysicalAdapter) {
    [IO.File]::AppendAllText((Join-Path $VmRoot 'network-calls.txt'),"resolve`n")
    if($Mode -eq 'bridge'){throw 'Fixture requires network preparation'}
    @{mode=$Mode;adapter=$Adapter;guest_host=$GuestHost}
}
'@)
function Reject([scriptblock]$Operation,[string]$Pattern) {
    try { & $Operation|Out-Null } catch { if($_.Exception.Message -notmatch $Pattern){throw};return }
    throw "Expected rejection: $Pattern"
}
$configPath=Join-Path $vm 'state/config.json'
$base=@{Root=$root;VmDirectory=$vm;Action='SetHardware';Cpus=2;MemoryMiB=12288;Resolution='2560x1440';Audio='dsound';Microphone=$true}
try {
    foreach($network in @(
        @{mode='bridge';adapter='missing TAP';forwarder='macnat';physical_adapter_id='11111111-1111-4111-8111-111111111111';guest_host='';retained=@{opaque='unchanged'}},
        @{mode='bridge';adapter='missing TAP';forwarder='macnat-ipv4';guest_host='invalid saved host';retained=@{opaque='unchanged'}},
        @{mode='nat';adapter='';guest_host=''}
    )) {
        $config=@{schema=1;cpus=1;memory_mib=8192;resolution='1920x1080';iso='';usb_backend='winusb';network=$network}
        [IO.File]::WriteAllText($configPath,($config|ConvertTo-Json -Depth 10))
        $before=(Get-Content -LiteralPath $configPath -Raw|ConvertFrom-Json).network|ConvertTo-Json -Depth 10 -Compress
        & "$stub/manager-backend.ps1" @base -PreserveNetwork|Out-Null
        $saved=Get-Content -LiteralPath $configPath -Raw|ConvertFrom-Json
        if(($saved.network|ConvertTo-Json -Depth 10 -Compress) -cne $before -or
            $saved.cpus -ne 2 -or $saved.memory_mib -ne 12288 -or $saved.resolution -ne '2560x1440' -or
            $saved.audio -ne 'dsound' -or !$saved.microphone -or (Test-Path (Join-Path $vm 'network-calls.txt'))) {
            throw 'Hardware save changed or inspected the retained network, or lost hardware values'
        }
    }
    $legacy=Get-Content -LiteralPath $configPath -Raw|ConvertFrom-Json
    $legacy.PSObject.Properties.Remove('network')
    [IO.File]::WriteAllText($configPath,($legacy|ConvertTo-Json -Depth 10))
    & "$stub/manager-backend.ps1" @base -PreserveNetwork|Out-Null
    if((Get-Content -LiteralPath $configPath -Raw|ConvertFrom-Json).PSObject.Properties['network']){throw 'Hardware-only save manufactured a network for a legacy configuration'}
    $bytes=[IO.File]::ReadAllBytes($configPath)
    Reject { & "$stub/manager-backend.ps1" @base -PreserveNetwork -Network nat } 'cannot be combined'
    foreach($name in @('Adapter','PhysicalAdapter','GuestHost')) {
        $arguments=$base.Clone();$arguments[$name]='fixture'
        Reject { & "$stub/manager-backend.ps1" @arguments -PreserveNetwork } 'cannot be combined'
    }
    [IO.File]::WriteAllText((Join-Path $vm 'running'),'fixture')
    Reject { & "$stub/manager-backend.ps1" @base -PreserveNetwork } 'Stop the VM'
    Remove-Item -LiteralPath (Join-Path $vm 'running')
    $invalid=$base.Clone();$invalid.MemoryMiB=4097
    Reject { & "$stub/manager-backend.ps1" @invalid -PreserveNetwork } '4 MiB'
    $invalid=$base.Clone();$invalid.Cpus=9
    Reject { & "$stub/manager-backend.ps1" @invalid -PreserveNetwork } 'ホストの CPU'
    if([Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) -cne [Convert]::ToBase64String($bytes)){throw 'Rejected save changed the configuration'}
    Reject { & "$stub/manager-backend.ps1" @base -Network bridge -Adapter 'fixture TAP' } 'requires network preparation'
    if(!(Test-Path (Join-Path $vm 'network-calls.txt'))){throw 'Changed network bypassed preparation validation'}
    & "$stub/manager-backend.ps1" @base -Network nat|Out-Null
    if((Get-Content -LiteralPath $configPath -Raw|ConvertFrom-Json).network.mode -ne 'nat'){throw 'Legacy explicit network save changed behavior'}
    'PASS: hardware-only save preserves network without inspection; explicit network changes and non-network validation remain guarded.'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(!$resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'reims-hardware-settings-*'){throw 'Unexpected fixture cleanup path'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
