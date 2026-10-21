# Exercise actual dispatch/elevated file handoff in a disposable script fixture.
$ErrorActionPreference='Stop'
$work=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp/wifi-actions-"+[guid]::NewGuid().ToString('N'))
$stub=Join-Path $work 'backend';$selected=Join-Path $work 'selected VM'
[void][IO.Directory]::CreateDirectory($stub)
Copy-Item "$PSScriptRoot/../manager-backend.ps1","$PSScriptRoot/../network-uplinks.ps1","$PSScriptRoot/../manager-ports.ps1" $stub
[IO.File]::WriteAllText((Join-Path $stub 'wifi-macnat.ps1'),@'
function Get-ReimsWifiStatus { @{schema=1;kind='none';configured=$false;ready=$false} }
'@)
[IO.File]::WriteAllText((Join-Path $stub 'start.ps1'),@'
param([switch]$FunctionsOnly)
$VmRoot=$env:REIMS_VM_ROOT_OVERRIDE
if(!$VmRoot){throw 'No-VM action sourced VM environment'}
function Enter-ReimsVmMutation { $null }
function Assert-ReimsVmStopped {}
function Write-ReimsJson($Path,$Value){
    if(Test-Path (Join-Path $VmRoot 'deny-result')){throw 'Fixture result write failure'}
    [void][IO.Directory]::CreateDirectory((Split-Path $Path))
    [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json))
}
function Prepare-ReimsWifi($EngineRoot,$TapId,$UplinkId,$GuestHost,$Consent){
    if(!$Consent -or $TapId -or $UplinkId -ne '11111111-1111-4111-8111-111111111111'){throw 'Prepare dispatch/consent mismatch'}
    $failure=Join-Path $VmRoot 'fail-prepare'
    if(Test-Path -LiteralPath $failure){
        [IO.File]::WriteAllText((Join-Path $VmRoot 'partial-state'),'changed')
        throw [IO.IOException]::new([IO.File]::ReadAllText($failure))
    }
    @{schema=1;prepared=$true;configured=$true;restart_required=$false}
}
'@)
[IO.File]::WriteAllText((Join-Path $stub 'wifi-driver.ps1'),@'
function Install-ReimsWifiDriver($Root,$Consent){
    if(!$Consent){throw 'Explicit terms consent required'}
    @{schema=1;installed=$true;reused=$false;restart_required=$false;terms_id='winpkfilter-3.6.2.1-personal-evaluation';certificate_imported=$false;forced_reboot=$false}
}
'@)
try{
    $global=& "$stub/manager-backend.ps1" -Root "$work/no-engine" -Action GetWifiMacNat
    if(($global | ConvertFrom-Json).schema -ne 1 -or (Test-Path "$work/no-engine")){throw 'Global status required VM/engine writes'}
    $driver=& "$stub/manager-backend.ps1" -Root "$work/no-engine" -VmDirectory $selected -Action InstallWifiDriver -AcceptPacketFilterTerms
    $driverPath=Join-Path $selected 'state/wifi-driver-install-result.json'
    if((Get-Content -Raw -LiteralPath $driverPath|ConvertFrom-Json).installed -ne $true){throw 'Driver result not saved'}
    $prepareParameters=@{Root="$work/no-engine";VmDirectory=$selected;Action='PrepareWifi';PhysicalAdapter='11111111-1111-4111-8111-111111111111';AcceptPacketFilterTerms=$true}
    $prepare=& "$stub/manager-backend.ps1" @prepareParameters
    $path=Join-Path $selected 'state/wifi-prepare-result.json'
    if((Get-Content -Raw -LiteralPath $path | ConvertFrom-Json).configured -ne $true -or !($prepare|ConvertFrom-Json).prepared){throw 'Prepare result not saved/echoed'}
    foreach($message in @('Fixture prepare failure: 選択した Wi-Fi が利用できません。',(('x'*2047)+[char]::ConvertFromUtf32(0x1f6a7)+'tail'))){
        [IO.File]::WriteAllText((Join-Path $selected 'fail-prepare'),$message)
        [IO.File]::SetLastWriteTimeUtc($path,[DateTime]::UtcNow.AddMinutes(-1))
        $started=[DateTime]::UtcNow
        $output=@(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$stub/manager-backend.ps1" -Root "$work/no-engine" -VmDirectory $selected -Action PrepareWifi -PhysicalAdapter '11111111-1111-4111-8111-111111111111' -AcceptPacketFilterTerms 2>&1)
        if($LASTEXITCODE -eq 0){throw 'Failed prepare dispatch returned success'}
        $failed=Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        if($failed.schema -ne 1 -or $failed.prepared -isnot [bool] -or $failed.prepared -or $failed.configured -isnot [bool] -or $failed.configured -or $failed.restart_required -isnot [bool] -or $failed.restart_required -or
            $failed.error -isnot [string] -or !$failed.error -or $failed.error.Length -gt 2048 -or [IO.File]::GetLastWriteTimeUtc($path) -lt $started){throw 'Fresh bounded prepare failure receipt was not written'}
        if($message.Length -le 2048 -and ($failed.error -ne $message -or ($output -join "`n") -notlike '*Fixture prepare failure*')){throw 'Original prepare exception was replaced'}
        if(!(Test-Path -LiteralPath (Join-Path $selected 'partial-state'))){throw 'Partial host state was incorrectly treated as rollback'}
    }
    Remove-Item -LiteralPath (Join-Path $selected 'fail-prepare')
    $null=& "$stub/manager-backend.ps1" @prepareParameters
    $before=[IO.File]::ReadAllText($path)
    [IO.File]::WriteAllText((Join-Path $selected 'deny-result'),'fixture')
    $output=@(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$stub/manager-backend.ps1" -Root "$work/no-engine" -VmDirectory $selected -Action PrepareWifi -PhysicalAdapter '11111111-1111-4111-8111-111111111111' -AcceptPacketFilterTerms 2>&1)
    if($LASTEXITCODE -eq 0 -or [IO.File]::ReadAllText($path) -ne $before){throw 'Failed elevated handoff claimed success or replaced old result'}
    'PASS Wi-Fi global no-VM dispatch, consent, GUID, fixed UAC results, fresh bounded errors, original throw, partial-state preservation and nonzero write failure'
}finally{
    $prefix=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if($work.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $work -Recurse -Force}
}
