# Functions only; querying a missing configuration creates no directories.
function Test-ReimsWifiNetwork($Config) {
    return [bool]($Config -and $Config.PSObject.Properties['network'] -and $Config.network.mode -eq 'bridge' -and
        $Config.network.PSObject.Properties['forwarder'] -and $Config.network.forwarder -eq 'macnat-ipv4')
}
function Get-ReimsWifiHelper {
    $root=if($env:REIMS_MANAGER_TOOLS_ROOT){$env:REIMS_MANAGER_TOOLS_ROOT}else{Join-Path (Split-Path $PSScriptRoot) 'runtimes'}
    return Join-Path $root 'wifi-macnat/reims-wifi-macnat.exe'
}
function Get-ReimsWifiDriverState {
    try {
        $driver=@(Get-CimInstance Win32_SystemDriver -Filter "Name='ndisrd'" -ErrorAction Stop)
        if(!$driver.Count){return 'not_installed'}
        if($driver.Count -eq 1 -and $driver[0].State -eq 'Running'){return 'running'}
    } catch {}
    return 'unknown'
}
function Read-ReimsWifiJson([string]$Path) {
    if(!(Test-Path -LiteralPath $Path -PathType Leaf)){return $null}
    if((Get-Item -LiteralPath $Path).Length -gt 64KB){throw 'Wi-Fi state exceeds 64 KiB.'}
    $value=Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -AsHashtable
    if($value.schema -ne 1){throw 'Unsupported Wi-Fi state schema.'}
    return $value
}
function Get-ReimsWifiStatus([string]$Directory) {
    $pin=Get-Content -LiteralPath "$PSScriptRoot/wifi-macnat-lock.json" -Raw | ConvertFrom-Json
    $result=@{schema=1;kind='none';installed=$false;configured=$false;running=$false;ready=$false;pid=$null;enabled=$false;helper_available=(Test-Path -LiteralPath (Get-ReimsWifiHelper) -PathType Leaf)
        driver_state=(Get-ReimsWifiDriverState);forwarder_state='stopped';error='';tap_id='';uplink_id='';ipv4_only=$true
        terms=@{id=$pin.driver.terms_id;url=$pin.driver.terms_url;license_url=$pin.driver.license_url;commercial_license_required=$true
            notice='Free for non-commercial personal/academic use and commercial evaluation. Commercial deployment/redistribution requires separate vendor licensing.'}}
    $result.installed=$result.helper_available -and $result.driver_state -eq 'running'
    if(!$Directory){return $result}
    try {
        $record=Read-ReimsWifiJson (Join-Path $Directory 'state/wifi-forwarder.json')
        if(!$record){return $result}
        $result.enabled=$record.state -eq 'enabled';$result.tap_id=$record.tap_id;$result.uplink_id=$record.uplink_id
        $result.error=[string]$record['error']
        $config=Read-ReimsWifiJson (Join-Path $Directory 'state/config.json')
        if($config -and $config.ContainsKey('network') -and $config.network['forwarder'] -eq 'macnat-ipv4'){
            $result.kind='macnat-ipv4'
            $result.configured=$result.enabled -and $config.network.mode -eq 'bridge' -and $config.network.tap_id -eq $record.tap_id -and $config.network.physical_adapter_id -eq $record.uplink_id
        }
        $native=Read-ReimsWifiJson (Join-Path $Directory 'state/wifi-forwarder/status.json')
        $runtime=Read-ReimsWifiJson (Join-Path $Directory 'state/wifi-forwarder/runtime.json')
        if($runtime){
            $process=Get-ReimsVmProcess ([pscustomobject]$runtime)
            if($process){
                $qemu=Get-ReimsRuntime
                $sameQemu=$qemu -and $qemu.pid -eq $runtime.qemu_pid -and $qemu.start_ticks -eq $runtime.qemu_start_ticks -and (Get-ReimsVmProcess $qemu)
                $result.running=[bool]$sameQemu;$result.pid=$runtime.pid
                if($sameQemu -and $native -and $native.session -eq $runtime.session -and $native.helper_pid -eq $runtime.pid -and
                    $native.qemu_pid -eq $runtime.qemu_pid -and $native.qemu_start_ticks -eq $runtime.qemu_start_ticks -and $native.state -in @('starting','ready','error')){
                    $result.forwarder_state=$native.state;$result.error=$native.error
                    $result.ready=$result.configured -and $native.state -eq 'ready'
                }else{$result.forwarder_state='unconfirmed'}
            }elseif($native -and $native.session -eq $runtime.session -and $native.state -eq 'error'){
                $result.forwarder_state='error';$result.error=$native.error
            }elseif($native -and $native.session -eq $runtime.session -and $native.state -eq 'stopped'){$result.forwarder_state='stopped'}
            else{$result.forwarder_state='unconfirmed';$result.error='Forwarder exited without confirmed cleanup.'}
        }
    } catch {$result.forwarder_state='unconfirmed';$result.error=$_.Exception.Message}
    return $result
}
function Get-ReimsWifiNic([string]$Id) {
    $guid=[guid]$Id
    if($guid -eq [guid]::Empty){throw 'Empty adapter identity.'}
    $matches=@(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {([guid]$_.InterfaceGuid) -eq $guid})
    if($matches.Count -ne 1){throw 'The selected adapter is missing or ambiguous.'}
    return $matches[0]
}
function Assert-ReimsOwnedWifiTap([string]$Id,[string]$PnpId='') {
    . "$PSScriptRoot/tap-installer.ps1"
    $path=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'ReimsManager/Drivers/tap-windows-9.27.0/tap-adapter.json'
    $owned=Read-ReimsWifiJson $path
    if(!$owned -or $owned.adapter_id -ne ([guid]$Id).ToString('D') -or $owned.pnp_instance_id -notmatch '^ROOT\\NET\\[0-9A-F]+$'){
        throw 'Select the dedicated TAP created by Manager; other VPN adapters are preserved.'
    }
    $live=@(Get-ReimsInstalledTaps | Where-Object { $_.adapter_id -eq $owned.adapter_id -and $_.pnp_instance_id -eq $owned.pnp_instance_id })
    if($live.Count -ne 1 -or ($PnpId -and $PnpId -ne $owned.pnp_instance_id) -or $live[0].adapter_name -notmatch '^Reims TAP [A-Za-z0-9 -]+$'){
        throw 'Dedicated TAP identity/name changed; no bindings were modified.'
    }
    return $live[0]
}
function Get-ReimsWifiBindings([string]$Id) {
    $nic=Get-ReimsWifiNic $Id
    $bindings=@(Get-NetAdapterBinding -Name $nic.Name -AllBindings -ErrorAction Stop)
    if(@($bindings | Where-Object {$_.ComponentID -eq 'ms_implat' -and $_.Enabled}).Count){throw 'A selected adapter already belongs to a Windows bridge.'}
    $result=@{}
    foreach($component in @('ms_tcpip','ms_tcpip6')){
        $found=@($bindings | Where-Object ComponentID -EQ $component)
        $expected=([guid]$Id).ToString('B')+'::'+$component
        if($found.Count -ne 1 -or $found[0].Enabled -isnot [bool] -or $found[0].InstanceID -ine $expected){throw 'IP binding state/identity is unknown.'}
        $result[$component]=$found[0].Enabled
    }
    return $result
}
function Assert-ReimsWifiCaptureAvailable([string]$TapId,[string]$UplinkId) {
    & (Get-ReimsWifiHelper) --check ([guid]$TapId).ToString('B') ([guid]$UplinkId).ToString('B')
    if($LASTEXITCODE){throw 'Selected adapters are missing, reserved, or have an unknown/active packet-filter mode; no bindings were changed.'}
}
function Set-ReimsWifiBinding([string]$Id,[string]$Component,[bool]$Enabled) {
    $nic=Get-ReimsWifiNic $Id
    if($Enabled){Enable-NetAdapterBinding -Name $nic.Name -ComponentID $Component -Confirm:$false -ErrorAction Stop | Out-Null}
    else{Disable-NetAdapterBinding -Name $nic.Name -ComponentID $Component -Confirm:$false -ErrorAction Stop | Out-Null}
}
function Set-ReimsWifiEnabled([string]$TapId,[string]$UplinkId,[string]$GuestHost) {
    Assert-ReimsVmStopped;Assert-Administrator
    Stop-ReimsWifiForwarder
    $tap=Assert-ReimsOwnedWifiTap $TapId
    $uplink=Resolve-ReimsPhysicalUplink $UplinkId
    if($uplink.kind -ne 'wifi' -or !$uplink.connected -or $tap.adapter_id -eq $uplink.id){throw 'Select a connected physical Wi-Fi adapter.'}
    if($GuestHost -and $GuestHost -notmatch '^[A-Za-z0-9][A-Za-z0-9.:-]*$'){throw 'Invalid guest address.'}
    if((Get-ReimsWifiDriverState) -ne 'running' -or !(Test-Path -LiteralPath (Get-ReimsWifiHelper))){throw 'Install the optional Wi-Fi driver and bundled helper first.'}
    [void](Get-ReimsWifiBindings $uplink.id)
    Assert-ReimsWifiCaptureAvailable $tap.adapter_id $uplink.id
    $before=Get-ReimsWifiBindings $tap.adapter_id
    $liveBefore=$before.Clone()
    $path=Join-Path $VmRoot 'state/wifi-forwarder.json';$previous=Read-ReimsWifiJson $path
    if($previous -and $previous.state -ne 'disabled'){
        if($previous.state -ne 'enabled' -or $previous.tap_id -ne $tap.adapter_id -or $previous.uplink_id -ne $uplink.id){throw 'Disable the previous Wi-Fi configuration before changing adapters.'}
        if($before.ms_tcpip -or !$before.ms_tcpip6){throw 'TAP bindings changed outside Manager; inspect before reapplying.'}
        $before=$previous.bindings_before
    }
    $record=@{schema=1;state='prepared';tap_id=$tap.adapter_id;tap_name=$tap.adapter_name;pnp_instance_id=$tap.pnp_instance_id;uplink_id=$uplink.id;bindings_before=$before;error=''}
    $config=Get-ReimsVmConfig
    Write-ReimsJson $path $record
    try {
        Assert-ReimsVmStopped
        Set-ReimsWifiBinding $tap.adapter_id ms_tcpip $false
        Set-ReimsWifiBinding $tap.adapter_id ms_tcpip6 $true
        $after=Get-ReimsWifiBindings $tap.adapter_id
        if($after.ms_tcpip -or !$after.ms_tcpip6){throw 'Dedicated TAP IP binding preparation failed.'}
        $record.state='enabled';Write-ReimsJson $path $record
        $config | Add-Member -Force network @{mode='bridge';forwarder='macnat-ipv4';adapter=$tap.adapter_name;tap_id=$tap.adapter_id;physical_adapter_id=$uplink.id;physical_adapter_name=$uplink.name;guest_host=$GuestHost}
        Write-ReimsJson (Join-Path $VmRoot 'state/config.json') $config
        return $record
    } catch {
        $record.state='error';$failure=$_.Exception.Message
        try{[void](Assert-ReimsOwnedWifiTap $record.tap_id $record.pnp_instance_id);foreach($key in @('ms_tcpip','ms_tcpip6')){Set-ReimsWifiBinding $record.tap_id $key $liveBefore[$key]}
            if($previous -and $previous.state -eq 'enabled'){$record=$previous}}
        catch{$failure+='; binding rollback requires inspection: '+$_.Exception.Message}
        $record.error=$failure
        Write-ReimsJson $path $record;throw $failure
    }
}
function Disable-ReimsWifiMacNat {
    Assert-ReimsVmStopped;Assert-Administrator
    Stop-ReimsWifiForwarder
    $path=Join-Path $VmRoot 'state/wifi-forwarder.json';$record=Read-ReimsWifiJson $path
    if(!$record -or $record.state -eq 'disabled'){throw 'No enabled owned Wi-Fi configuration.'}
    [void](Assert-ReimsOwnedWifiTap $record.tap_id $record.pnp_instance_id)
    $current=Get-ReimsWifiBindings $record.tap_id
    if($record.state -eq 'enabled' -and ($current.ms_tcpip -or !$current.ms_tcpip6)){throw 'TAP bindings changed outside Manager; inspect before restoring.'}
    Assert-ReimsWifiCaptureAvailable $record.tap_id $record.uplink_id
    foreach($key in @('ms_tcpip','ms_tcpip6')){if($record.bindings_before[$key] -isnot [bool]){throw 'Original binding state is invalid.'}}
    $config=Get-ReimsVmConfig
    $savedNat=$false
    try {
        # IPv6 remains enabled for link-local management; only host IPv4 is undone.
        Set-ReimsWifiBinding $record.tap_id ms_tcpip $record.bindings_before.ms_tcpip
        $config | Add-Member -Force network @{mode='nat';adapter='';guest_host=''}
        Write-ReimsJson (Join-Path $VmRoot 'state/config.json') $config
        $savedNat=$true
        $record.state='disabled';Write-ReimsJson $path $record
    }catch{
        $failure=$_.Exception.Message
        if(!$savedNat){
            try{[void](Assert-ReimsOwnedWifiTap $record.tap_id $record.pnp_instance_id);Set-ReimsWifiBinding $record.tap_id ms_tcpip $current.ms_tcpip}
            catch{$failure+='; IPv4 rollback requires inspection: '+$_.Exception.Message}
        }
        $record.state='error';$record.error=$failure;Write-ReimsJson $path $record
        throw $failure
    }
    return $record
}
function Install-ReimsWifiTap([string]$Id) {
    $json=(& "$PSScriptRoot/install-tap.ps1" -Adapter $Id) -join "`n"
    $tap=$json | ConvertFrom-Json -AsHashtable
    if($tap.schema -ne 1 -or $tap.owned -isnot [bool] -or !$tap.owned -or $tap.restart_required -isnot [bool]){
        throw 'A dedicated Manager-owned TAP is required; other VPN adapters were preserved.'
    }
    return $tap
}
function Get-ReimsWifiSavedNetwork($Config,[string]$Tap,[string]$Uplink,[string]$GuestHost) {
    $record=Read-ReimsWifiJson (Join-Path $VmRoot 'state/wifi-forwarder.json')
    if(!$record -or $record.state -ne 'enabled' -or $record.uplink_id -ne ([guid]$Uplink).ToString('D') -or
        ($Tap -ne $record.tap_name -and $Tap.Trim('{}') -ne $record.tap_id)){throw 'Enable this dedicated TAP/Wi-Fi pair before saving Wi-Fi mode.'}
    return @{mode='bridge';forwarder='macnat-ipv4';adapter=$record.tap_name;tap_id=$record.tap_id;physical_adapter_id=$record.uplink_id;guest_host=$GuestHost}
}
function Prepare-ReimsWifi([string]$EngineRoot,[string]$TapId,[string]$UplinkId,[string]$GuestHost,[bool]$AcceptDriverTerms) {
    Assert-ReimsVmStopped;Assert-Administrator
    $uplink=Resolve-ReimsPhysicalUplink $UplinkId
    if($uplink.kind -ne 'wifi' -or !$uplink.connected){throw 'Select a connected physical Wi-Fi adapter.'}
    if(!(Test-Path -LiteralPath (Get-ReimsWifiHelper) -PathType Leaf)){throw 'Bundled native Wi-Fi helper is missing.'}
    if($TapId){[void](Assert-ReimsOwnedWifiTap $TapId)}
    $driver=$null
    if((Get-ReimsWifiDriverState) -ne 'running'){$driver=Install-ReimsWifiDriver $EngineRoot $AcceptDriverTerms}
    $result=@{schema=1;prepared=$false;configured=$false;restart_required=$false}
    if($driver -and $driver.restart_required){$result.restart_required=$true;return $result}
    $tap=Install-ReimsWifiTap $TapId
    if($tap.restart_required){$result.restart_required=$true;return $result}
    if(!$tap.adapter_id){throw 'Dedicated TAP identity is not ready.'}
    [void](Set-ReimsWifiEnabled $tap.adapter_id $uplink.id $GuestHost)
    $result.prepared=$true;$result.configured=$true
    return $result
}
function Stop-ReimsWifiForwarder {
    $folder=Join-Path $VmRoot 'state/wifi-forwarder';$runtime=Read-ReimsWifiJson (Join-Path $folder 'runtime.json')
    if(!$runtime){return}
    $process=Get-ReimsVmProcess ([pscustomobject]$runtime)
    if(!$process){
        $last=Read-ReimsWifiJson (Join-Path $folder 'status.json')
        if(!$last -or $last.session -ne $runtime.session -or ($last.state -ne 'stopped' -and
            !($last.state -eq 'error' -and $last['cleanup_confirmed'] -eq $true))){throw 'Previous forwarder exited without confirmed capture cleanup; inspect before reusing adapters.'}
        return
    }
    [IO.File]::WriteAllText((Join-Path $folder 'stop'),'stop')
    if(!$process.WaitForExit(10000)){throw 'Owned forwarder did not confirm cleanup; inspect before changing bindings. It was not forcibly killed.'}
    $state=Read-ReimsWifiJson (Join-Path $folder 'status.json')
    if(!$state -or $state.session -ne $runtime.session -or $state.state -ne 'stopped'){throw 'Forwarder capture cleanup was not confirmed.'}
}
function Start-ReimsWifiForwarder($Config,$QemuRuntime) {
    Stop-ReimsWifiForwarder
    $record=Read-ReimsWifiJson (Join-Path $VmRoot 'state/wifi-forwarder.json')
    if(!$record -or $record.state -ne 'enabled' -or $Config.network.tap_id -ne $record.tap_id -or $Config.network.physical_adapter_id -ne $record.uplink_id){throw 'Wi-Fi configuration/ownership does not match.'}
    [void](Assert-ReimsOwnedWifiTap $record.tap_id $record.pnp_instance_id)
    $bindings=Get-ReimsWifiBindings $record.tap_id
    if($bindings.ms_tcpip -or !$bindings.ms_tcpip6){throw 'Dedicated TAP host IPv4 must be disabled and IPv6 retained.'}
    $uplink=Resolve-ReimsPhysicalUplink $record.uplink_id
    if($uplink.kind -ne 'wifi' -or !$uplink.connected){throw 'Selected Wi-Fi adapter is unavailable.'}
    [void](Get-ReimsWifiBindings $record.uplink_id)
    if(!(Get-ReimsVmProcess $QemuRuntime)){throw 'QEMU identity is no longer valid.'}
    $folder=Join-Path $VmRoot 'state/wifi-forwarder';[void][IO.Directory]::CreateDirectory($folder)
    $stop=Join-Path $folder 'stop';if(Test-Path -LiteralPath $stop){Remove-Item -LiteralPath $stop}
    $session=[guid]::NewGuid().ToString('B');$helper=Get-ReimsWifiHelper
    $arguments=@(([guid]$record.tap_id).ToString('B'),([guid]$record.uplink_id).ToString('B'),$Config.mac,[string]$QemuRuntime.pid,[string]$QemuRuntime.start_ticks,$folder,$session)
    $process=Start-Process -FilePath $helper -ArgumentList (($arguments | ForEach-Object {ConvertTo-ReimsWindowsArgument $_}) -join ' ') -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $folder 'stdout.log') -RedirectStandardError (Join-Path $folder 'stderr.log')
    $runtime=@{schema=1;pid=$process.Id;start_ticks=$process.StartTime.ToUniversalTime().Ticks;executable=$helper;session=$session;qemu_pid=$QemuRuntime.pid;qemu_start_ticks=$QemuRuntime.start_ticks}
    Write-ReimsJson (Join-Path $folder 'runtime.json') $runtime
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    try{
        do{
            $state=Read-ReimsWifiJson (Join-Path $folder 'status.json')
            if($state -and $state.session -eq $session){
                if($state.state -eq 'error'){throw $state.error}
                if($state.state -eq 'ready' -and $state.helper_pid -eq $runtime.pid -and $state.qemu_pid -eq $QemuRuntime.pid -and $state.qemu_start_ticks -eq $QemuRuntime.start_ticks){return}
            }
            if($process.HasExited){throw 'Forwarder exited before ready; inspect its fixed state/logs.'}
            Start-Sleep -Milliseconds 100
        }while([DateTime]::UtcNow -lt $deadline)
        throw 'Forwarder readiness timed out.'
    }catch{[IO.File]::WriteAllText($stop,'stop');[void]$process.WaitForExit(10000);throw}
}
