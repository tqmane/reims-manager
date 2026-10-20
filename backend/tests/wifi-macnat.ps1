# All network/driver/process providers are synthetic. No driver or VM is opened.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../wifi-macnat.ps1"
. "$PSScriptRoot/../wifi-driver.ps1"
. "$PSScriptRoot/../network-uplinks.ps1"
$routerReaderScript=${function:Get-ReimsWifiRouterDiscovery}
$testRoot=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp/wifi-"+[guid]::NewGuid().ToString('N'))
$VmRoot=$testRoot
[void][IO.Directory]::CreateDirectory((Join-Path $VmRoot 'state'))
$tapId='11111111-1111-4111-8111-111111111111';$uplinkId='22222222-2222-4222-8222-222222222222'
$fixture=@{ownedTap=$tapId;running=$false;foreign=$false;driver='running';kind='wifi';bindings=@{ms_tcpip=$true;ms_tcpip6=$false};writes=0;badWrite=$false;changes=@();cases=0;captureBusy=$false;signature='Valid';badCatalog=$false;nativeCalls=@()}
$configPath=Join-Path $VmRoot 'state/config.json'
function Reset {
    $fixture.running=$false;$fixture.foreign=$false;$fixture.driver='running';$fixture.kind='wifi';$fixture.captureBusy=$false
    $fixture.bindings=@{ms_tcpip=$true;ms_tcpip6=$false};$fixture.changes=@();$fixture.writes=0;$fixture.badWrite=$false
    $fixture.router=@{ActiveStore='Enabled';PersistentStore='ControlledByDHCP'};$fixture.routerWrites=@();$fixture.routerFailOnce=$false
    [IO.File]::WriteAllText($configPath,'{"schema":1,"network":{"mode":"nat","adapter":"","guest_host":""}}')
    foreach($path in @('state/wifi-forwarder.json','state/wifi-forwarder/runtime.json','state/wifi-forwarder/status.json')){
        $file=Join-Path $VmRoot $path;if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file}
    }
}
function Check($Condition,[string]$Message){if(!$Condition){throw $Message}}
function Reject([scriptblock]$Code,[string]$Pattern){$fixture.cases++;try{& $Code;throw 'Expected rejection'}catch{if($_.Exception.Message -notmatch $Pattern){throw}}}
function Assert-ReimsVmStopped {if($fixture.running){throw 'Fixture VM running'}}
function Assert-Administrator {}
function Stop-ReimsWifiForwarder {}
function Get-ReimsWifiDriverState {$fixture.driver}
function Get-ReimsWifiHelper {$PSHOME+'/pwsh.exe'}
function Assert-ReimsOwnedWifiTap($Id,$PnpId){
    if($fixture.foreign -or $Id -ne $tapId){throw 'Foreign TAP refused'}
    @{adapter_id=$tapId;adapter_name='Reims TAP 11111111';pnp_instance_id='ROOT\NET\0001'}
}
function Resolve-ReimsPhysicalUplink { @{id=$uplinkId;kind=$fixture.kind;connected=$true;name='Authored Wi-Fi'} }
function Get-ReimsWifiBindings($Id){
    if($Id -eq $uplinkId){return @{ms_tcpip=$true;ms_tcpip6=$true}}
    if($Id -ne $tapId){throw 'Unrelated binding read'}
    return $fixture.bindings.Clone()
}
function Assert-ReimsWifiCaptureAvailable {
    if($fixture.captureBusy){throw 'Fixture active foreign capture'}
}
function Set-ReimsWifiBinding($Id,$Component,$Enabled){
    if($Id -ne $tapId){throw 'Physical/foreign binding mutation attempted'}
    $fixture.bindings[$Component]=$Enabled;$fixture.changes+=,$Component
}
function Get-ReimsWifiRouterDiscovery($Id){if($Id -ne $fixture.ownedTap){throw 'Unrelated RouterDiscovery read'};return @{ActiveStore=$fixture.router.ActiveStore}}
function Set-ReimsWifiRouterDiscovery($Id,$Store,$Value){
    if($Id -ne $fixture.ownedTap){throw 'Physical/foreign RouterDiscovery mutation attempted'}
    if($Store -ne 'ActiveStore'){throw 'Persistent policy must remain untouched'}
    if($fixture.routerFailOnce){$fixture.routerFailOnce=$false;$fixture.router[$Store]=$Value;throw 'Fixture RouterDiscovery write failure'}
    $fixture.router[$Store]=$Value;$fixture.routerWrites+=,@($Store,$Value)
}
function Get-ReimsVmConfig {Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json}
function Write-ReimsJson($Path,$Value){
    $fixture.writes++
    if($fixture.badWrite -and $Path -eq $configPath){throw 'Fixture config write failure'}
    [void][IO.Directory]::CreateDirectory((Split-Path $Path))
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 10))
}
try{
    Reset
    $receipt=Set-ReimsWifiEnabled $tapId $uplinkId 'guest.local';$fixture.cases++
    Check (!$fixture.bindings.ms_tcpip -and $fixture.bindings.ms_tcpip6) 'TAP IPv4/IPv6 policy wrong'
    Check ($receipt.bindings_before.ms_tcpip -and !$receipt.bindings_before.ms_tcpip6) 'Previous IP bindings lost'
    Check ((Get-ReimsVmConfig).network.mode -eq 'bridge' -and (Get-ReimsVmConfig).network.forwarder -eq 'macnat') 'Mode not saved'
    Check ($receipt.ipv6_enabled -and $fixture.router.ActiveStore -eq 'Disabled' -and $fixture.router.PersistentStore -eq 'ControlledByDHCP') 'IPv6 protection changed persistent policy'
    Check ($receipt.router_discovery_store -eq 'ActiveStore' -and $receipt.router_discovery_before.ActiveStore -eq 'Enabled' -and !$receipt.router_discovery_before.ContainsKey('PersistentStore')) 'Effective RouterDiscovery scope/original lost'
    $receipt=Set-ReimsWifiEnabled $tapId $uplinkId 'guest.local';$fixture.cases++
    Check ($receipt.router_discovery_before.ActiveStore -eq 'Enabled' -and !$receipt.router_discovery_before.ContainsKey('PersistentStore')) 'Reapply overwrote original RouterDiscovery'
    $saved=Get-ReimsSavedNetwork (Get-ReimsVmConfig) 'bridge' $tapId 'guest.local' $uplinkId
    Check ($saved.mode -eq 'bridge' -and $saved.tap_id -eq $tapId) 'Hardware save loses Wi-Fi mode'
    Reject {Get-ReimsSavedNetwork (Get-ReimsVmConfig) 'nat' '' '' ''} 'Disable Wi-Fi'
    Reject {Get-ReimsWifiSavedNetwork (Get-ReimsVmConfig) $tapId '33333333-3333-4333-8333-333333333333' ''} 'before saving'
    [void](Disable-ReimsWifiMacNat);$fixture.cases++
    Check ($fixture.bindings.ms_tcpip -and $fixture.bindings.ms_tcpip6) 'Disable did not restore exact original bindings'
    Check ($fixture.router.ActiveStore -eq 'Enabled' -and $fixture.router.PersistentStore -eq 'ControlledByDHCP') 'Disable did not restore effective policy/preserve persistence'
    Check ((Get-ReimsVmConfig).network.mode -eq 'nat') 'Disable did not restore NAT'
    foreach($case in @('running','foreign','captureBusy')){
        Reset;$fixture[$case]=$true
        Reject {Set-ReimsWifiEnabled $tapId $uplinkId ''} 'running|Foreign|foreign capture'
        Check ($fixture.writes -eq 0 -and !$fixture.changes.Count) 'Refusal mutated state/bindings'
    }
    Reset;$fixture.driver='unknown'
    Reject {Set-ReimsWifiEnabled $tapId $uplinkId ''} 'Install the optional'
    Check (!$fixture.changes.Count) 'Unknown driver mutated bindings'
    Reset;$fixture.kind='ethernet'
    Reject {Set-ReimsWifiEnabled $tapId $uplinkId ''} 'physical Wi-Fi'
    Reset;$fixture.badWrite=$true
    Reject {Set-ReimsWifiEnabled $tapId $uplinkId ''} 'config write failure'
    Check ($fixture.bindings.ms_tcpip -and !$fixture.bindings.ms_tcpip6) 'Failed save lost binding rollback'
    Check ((Get-ReimsVmConfig).network.mode -eq 'nat') 'Failed save changed NAT'
    Check ((Read-ReimsWifiJson (Join-Path $VmRoot 'state/wifi-forwarder.json')).state -eq 'error') 'Failure not recorded'
    $failedStatus=Get-ReimsWifiStatus $VmRoot
    Check ($failedStatus.enabled -and !$failedStatus.configured -and !$failedStatus.ready) 'Partial owned state hid Disable or claimed configured';$fixture.cases++
    Check ($fixture.router.ActiveStore -eq 'Enabled' -and $fixture.router.PersistentStore -eq 'ControlledByDHCP') 'Failed save lost RouterDiscovery rollback'
    Reset;$fixture.routerFailOnce=$true
    Reject {Set-ReimsWifiEnabled $tapId $uplinkId ''} 'RouterDiscovery write failure'
    Check ($fixture.router.ActiveStore -eq 'Enabled' -and $fixture.router.PersistentStore -eq 'ControlledByDHCP' -and $fixture.bindings.ms_tcpip) 'Partial policy write rollback failed'
    Reset;[void](Set-ReimsWifiEnabled $tapId $uplinkId '');$fixture.router.ActiveStore='ControlledByDHCP'
    $beforeWrites=$fixture.routerWrites.Count
    Reject {Set-ReimsWifiEnabled $tapId $uplinkId ''} 'outside Manager'
    Reject {Disable-ReimsWifiMacNat} 'ownership changed|outside Manager'
    Check ($fixture.routerWrites.Count -eq $beforeWrites) 'Foreign RouterDiscovery was overwritten'
    Reset;$fixture.router.PersistentStore=$null
    $nullPersistent=Set-ReimsWifiEnabled $tapId $uplinkId '';$fixture.cases++
    Check ($nullPersistent.ipv6_enabled -and $fixture.router.ActiveStore -eq 'Disabled' -and $null -eq $fixture.router.PersistentStore) 'Persistent NULL/default was guessed or overwritten'
    $fixture.router.ActiveStore='Enabled'
    Reject {Assert-ReimsWifiRouterDisabled $nullPersistent} 'outside Manager'
    $reapplied=Set-ReimsWifiEnabled $tapId $uplinkId '';$fixture.cases++
    Check ($fixture.router.ActiveStore -eq 'Disabled' -and $reapplied.router_discovery_before.ActiveStore -eq 'Enabled') 'Explicit reapply did not preserve known original effective policy'
    [void](Disable-ReimsWifiMacNat);$fixture.cases++
    Check ($fixture.router.ActiveStore -eq 'Enabled' -and $null -eq $fixture.router.PersistentStore) 'NULL/default persistence changed on Disable'
    Reset;$oldDual=Set-ReimsWifiEnabled $tapId $uplinkId '';[void]$oldDual.Remove('router_discovery_store')
    Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder.json') $oldDual
    $beforeWrites=$fixture.routerWrites.Count
    Reject {Set-ReimsWifiEnabled $tapId $uplinkId ''} 'Older dual-store'
    Reject {Disable-ReimsWifiMacNat} 'Older dual-store'
    Check ($fixture.routerWrites.Count -eq $beforeWrites) 'Unknown older dual-store receipt was changed'
    Reset;[void](Set-ReimsWifiEnabled $tapId $uplinkId '')
    $legacy=Read-ReimsWifiJson (Join-Path $VmRoot 'state/wifi-forwarder.json');$legacy.Remove('ipv6_enabled');$legacy.Remove('router_discovery_before')
    Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder.json') $legacy
    $fixture.router=@{ActiveStore='Enabled';PersistentStore='ControlledByDHCP'}
    $upgraded=Set-ReimsWifiEnabled $tapId $uplinkId '';$fixture.cases++
    Check ($upgraded.ipv6_enabled -and $upgraded.router_discovery_before.ActiveStore -eq 'Enabled') 'Legacy IPv4 record was not explicitly upgraded'
    Reset;[void](Set-ReimsWifiEnabled $tapId $uplinkId '')
    $fixture.bindings.ms_tcpip=$true
    Reject {Disable-ReimsWifiMacNat} 'changed outside'
    Reset;[void](Set-ReimsWifiEnabled $tapId $uplinkId '');$fixture.badWrite=$true
    Reject {Disable-ReimsWifiMacNat} 'config write failure'
    Check (!$fixture.bindings.ms_tcpip) 'Failed Disable lost IPv4 rollback'
    Check ($fixture.router.ActiveStore -eq 'Disabled' -and $fixture.router.PersistentStore -eq 'ControlledByDHCP') 'Failed Disable lost RA protection or changed persistence'
    $fixture.badWrite=$false;[void](Disable-ReimsWifiMacNat)
    Check ($fixture.bindings.ms_tcpip) 'Disable retry did not restore original IPv4'
    # One action prepares missing prerequisites; reboot paths never claim configured.
    $originalDriverInstaller=${function:Install-ReimsWifiDriver}
    function Install-ReimsWifiDriver($EngineRoot,$AcceptDriverTerms){
        if(!$AcceptDriverTerms){throw 'Fixture explicitly accept terms'}
        $fixture.driverInstalls++;$fixture.driver='running'
        @{schema=1;installed=$true;restart_required=$fixture.driverReboot}
    }
    function Install-ReimsWifiTap($Id){$fixture.tapInstalls++;@{schema=1;owned=$true;adapter_id=$fixture.ownedTap;restart_required=$fixture.tapReboot}}
    Reset;$fixture.driverInstalls=0;$fixture.tapInstalls=0;$fixture.driverReboot=$false;$fixture.tapReboot=$false
    $prepared=Prepare-ReimsWifi 'fixture-engine' '' $uplinkId 'guest.local' $false
    Check ($prepared.prepared -and $prepared.configured -and $fixture.driverInstalls -eq 0 -and $fixture.tapInstalls -eq 1) 'One action did not reuse installed driver/prepare owned TAP'
    $fixture.cases++
    Reset;$fixture.driver='not_installed';$fixture.driverReboot=$true;$fixture.driverInstalls=0;$fixture.tapInstalls=0
    $pending=Prepare-ReimsWifi 'fixture-engine' '' $uplinkId '' $true
    Check ($pending.restart_required -and !$pending.configured -and $fixture.tapInstalls -eq 0 -and !$fixture.changes.Count) 'Driver reboot path touched TAP/configuration'
    $fixture.cases++
    Reset;$fixture.driver='not_installed';$fixture.driverReboot=$false
    Reject {Prepare-ReimsWifi 'fixture-engine' '' $uplinkId '' $false} 'explicitly accept'
    Reset;$fixture.tapReboot=$true
    $pending=Prepare-ReimsWifi 'fixture-engine' '' $uplinkId '' $false
    Check ($pending.restart_required -and !$pending.prepared -and !$fixture.changes.Count) 'TAP pending reboot was hidden'
    $fixture.cases++
    Set-Item Function:Install-ReimsWifiDriver $originalDriverInstaller
    Reset
    $fixture.cases++
    $status=Get-ReimsWifiStatus ''
    Check (!$status.enabled -and $status.forwarder_state -eq 'stopped' -and $status.driver_state -eq 'running') 'Global status requires VM'
    Check ($status.terms.url -eq 'https://www.ntkernel.com/windows-packet-filter/download/') 'Terms URL drift'
    # Load the real runtime/status functions again, replacing process identity provider only.
    . "$PSScriptRoot/../wifi-macnat.ps1"
    function Assert-ReimsOwnedWifiTap($Id,$PnpId){if($Id -ne $fixture.ownedTap -or $PnpId -ne 'ROOT\NET\0001'){throw 'Unowned policy status'};@{adapter_id=$Id}}
    function Get-ReimsWifiRouterDiscovery { @{ActiveStore='Disabled'} }
    function Get-ReimsWifiDriverState {'running'}
    function Get-ReimsWifiHelper {$PSHOME+'/pwsh.exe'}
    $fixture.live=$true;$fixture.identityWrong=$false
    function Get-ReimsVmProcess($Runtime){if($fixture.identityWrong){throw 'Fixture PID reused'};if($fixture.live){[pscustomobject]@{Id=$Runtime.pid}}}
    function Get-ReimsRuntime {[pscustomobject]@{pid=77;start_ticks=12345}}
    $session='{33333333-3333-4333-8333-333333333333}'
    Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder.json') @{schema=1;state='enabled';tap_id=$tapId;pnp_instance_id='ROOT\NET\0001';uplink_id=$uplinkId;error='';ipv6_enabled=$true;router_discovery_store='ActiveStore';router_discovery_before=@{ActiveStore='Enabled'}}
    Write-ReimsJson $configPath @{schema=1;network=@{mode='bridge';forwarder='macnat';tap_id=$tapId;physical_adapter_id=$uplinkId}}
    Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder/runtime.json') @{schema=1;pid=88;start_ticks=54321;executable='fixture';session=$session;qemu_pid=77;qemu_start_ticks=12345}
    Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder/status.json') @{schema=1;state='ready';session=$session;helper_pid=88;qemu_pid=77;qemu_start_ticks=12345;error='';ipv6_enabled=$true}
    Check ((Get-ReimsWifiStatus $VmRoot).forwarder_state -eq 'ready') 'Exact live identity not ready';$fixture.cases++
    Check ((Get-ReimsWifiStatus $VmRoot).ready -and (Get-ReimsWifiStatus $VmRoot).configured -and (Get-ReimsWifiStatus $VmRoot).running) 'Unified forwarder flags not confirmed'
    Check ((Get-ReimsWifiStatus $VmRoot).ipv6_enabled -and !(Get-ReimsWifiStatus $VmRoot).ipv4_only) 'IPv6 status not typed/inverse';$fixture.cases++
    Write-ReimsJson $configPath @{schema=1;network=@{mode='bridge';forwarder='macnat-ipv4';tap_id=$tapId;physical_adapter_id=$uplinkId}}
    $legacyStatus=Get-ReimsWifiStatus $VmRoot
    Check (!$legacyStatus.configured -and !$legacyStatus.ready -and !$legacyStatus.ipv6_enabled -and $legacyStatus.ipv4_only -and $legacyStatus.enabled -and $legacyStatus.error -match 'Reapply') 'Legacy IPv4 was treated as safely IPv6 prepared';$fixture.cases++
    Write-ReimsJson $configPath @{schema=1;network=@{mode='bridge';forwarder='macnat';tap_id=$tapId;physical_adapter_id=$uplinkId}}
    $withoutFeature=Read-ReimsWifiJson (Join-Path $VmRoot 'state/wifi-forwarder/status.json');$withoutFeature.Remove('ipv6_enabled')
    Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder/status.json') $withoutFeature
    Check (!(Get-ReimsWifiStatus $VmRoot).ready -and (Get-ReimsWifiStatus $VmRoot).forwarder_state -eq 'unconfirmed') 'Older helper readiness was accepted as IPv6';$fixture.cases++
    $withoutFeature.ipv6_enabled='true';Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder/status.json') $withoutFeature
    Check (!(Get-ReimsWifiStatus $VmRoot).ready) 'String IPv6 native flag was accepted';$fixture.cases++
    $withoutFeature.ipv6_enabled=$true;Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder/status.json') $withoutFeature
    $fixture.identityWrong=$true
    Check ((Get-ReimsWifiStatus $VmRoot).forwarder_state -eq 'unconfirmed') 'PID reuse accepted';$fixture.cases++
    $fixture.identityWrong=$false;$fixture.live=$false
    Check ((Get-ReimsWifiStatus $VmRoot).forwarder_state -eq 'unconfirmed') 'Stale ready accepted';$fixture.cases++
    Reject {Stop-ReimsWifiForwarder} 'without confirmed'
    Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder/status.json') @{schema=1;state='stopped';session=$session;cleanup_confirmed=$true}
    Stop-ReimsWifiForwarder;$fixture.cases++
    # Reboot recovery uses only synthetic OS/process/adapter/inspection providers.
    $fixture.boot=[DateTime]::UtcNow.AddMinutes(-10).Ticks;$fixture.inspectCalls=0
    function Get-ReimsWifiBootTicks {$fixture.boot}
    function Get-ReimsWifiHelper {Join-Path $VmRoot 'authored-helper.exe'}
    function Get-ReimsVmProcess {if($fixture.helperLive){[pscustomobject]@{Id=88}}}
    function Get-Process {
        [CmdletBinding()]param([int]$Id)
        if($Id -ne 77){throw 'Unrelated process query'}
        if($fixture.qemuLive -or $fixture.pidReused){
            $ticks=if($fixture.qemuLive){$fixture.boot-2000}else{$fixture.boot+2000}
            $object=[pscustomobject]@{StartTime=[DateTime]::new($ticks,[DateTimeKind]::Utc)}
            $object|Add-Member ScriptMethod Dispose {};return $object
        }
    }
    function Get-ReimsWifiNic($Id){
        if($Id -eq $tapId){return @{InterfaceIndex=23}}
        if($Id -eq $uplinkId){return @{InterfaceIndex=11}}
        throw 'Foreign adapter queried'
    }
    function Get-ReimsWifiCaptureInspection($Tap,$Wifi){
        if($Tap -ne $tapId -or $Wifi -ne $uplinkId){throw 'Wrong inspection pair'}
        $fixture.inspectCalls++
        if($fixture.inspectFail){throw 'Fixture read-only inspect failed'}
        if($fixture.concurrent){[IO.File]::AppendAllText($configPath,' ')}
        return $fixture.inspection
    }
    function Reset-Reboot {
        $fixture.helperLive=$false;$fixture.qemuLive=$false;$fixture.pidReused=$false;$fixture.inspectFail=$false;$fixture.concurrent=$false;$fixture.foreign=$false
        $fixture.inspection=@{schema=1;adapters=@(
            @{role='tap';interface_index=23;ndis_mode=0;packet_filter=11;sdk_medium=0;system_medium=0;physical_medium=0},
            @{role='wifi';interface_index=11;ndis_mode=0;packet_filter=0;sdk_medium=16;system_medium=16;physical_medium=9})}
        Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder/runtime.json') @{schema=1;pid=88;start_ticks=($fixture.boot-1000);executable=(Get-ReimsWifiHelper);session=$session;qemu_pid=77;qemu_start_ticks=($fixture.boot-2000)}
        Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder/status.json') @{schema=1;state='ready';session=$session;helper_pid=88;qemu_pid=77;qemu_start_ticks=($fixture.boot-2000);cleanup_confirmed=$false;hardware_filters=@{tap=@{before=11;requested=43;read_back=43};wifi=@{before=0;requested=$null;read_back=0}};receive=@{strategy='native80211-socket-memberships';interface_index=11}}
        Write-ReimsJson (Join-Path $VmRoot 'state/wifi-forwarder.json') @{schema=1;state='enabled';tap_id=$tapId;uplink_id=$uplinkId;pnp_instance_id='ROOT\NET\0001'}
        Write-ReimsJson $configPath @{schema=1;network=@{mode='bridge';forwarder='macnat';tap_id=$tapId;physical_adapter_id=$uplinkId}}
    }
    function Change-RebootJson($Path,[scriptblock]$Change){$value=Read-ReimsWifiJson (Join-Path $VmRoot $Path);&$Change $value;Write-ReimsJson (Join-Path $VmRoot $Path) $value}
    Reset-Reboot
    $original=[IO.File]::ReadAllText((Join-Path $VmRoot 'state/wifi-forwarder/status.json'))
    $beforeChanges=$fixture.changes.Count;$beforeRouterWrites=$fixture.routerWrites.Count
    Stop-ReimsWifiForwarder
    Check (!(Test-Path (Join-Path $VmRoot 'state/wifi-forwarder/runtime.json'))) 'Recovered runtime was not archived';$fixture.cases++
    $archives=@(Get-ChildItem -LiteralPath (Join-Path $VmRoot 'state/wifi-forwarder-history') -Directory)
    Check ($archives.Count -eq 1) 'Reboot history missing'
    $archived=Join-Path $archives[0].FullName 'original/status.json'
    Check ([IO.File]::ReadAllText($archived) -ceq $original -and !(Read-ReimsWifiJson $archived).cleanup_confirmed) 'Old cleanup status was fabricated'
    $proof=Read-ReimsWifiJson (Join-Path $archives[0].FullName 'recovery.json')
    Check ($proof.boot_ticks -eq $fixture.boot -and $proof.evidence.Count -eq 4) 'Recovery proof missing boot/byte guards';$fixture.cases++
    Check ($fixture.changes.Count -eq $beforeChanges -and $fixture.routerWrites.Count -eq $beforeRouterWrites) 'Recovery changed IP bindings or RouterDiscovery'

    foreach($case in @('same-boot','old-qemu-live','helper-live','foreign-tap','nonobserve','missing-filter','changed-tap-filter','active-tap','active-wifi','wrong-index','duplicate-role','wrong-medium','string-mode','inspect-failure','concurrent-file','session-mismatch','string-pid','missing-status','oversize')){
        Reset-Reboot
        switch($case){
            same-boot {Change-RebootJson 'state/wifi-forwarder/runtime.json' {param($v)$v.start_ticks=$fixture.boot}}
            old-qemu-live {$fixture.qemuLive=$true}
            helper-live {$fixture.helperLive=$true}
            foreign-tap {$fixture.foreign=$true;function Assert-ReimsOwnedWifiTap {throw 'Foreign TAP'}}
            nonobserve {Change-RebootJson 'state/wifi-forwarder/status.json' {param($v)$v.hardware_filters.wifi.requested=4}}
            missing-filter {Change-RebootJson 'state/wifi-forwarder/status.json' {param($v)$v.hardware_filters.Remove('tap')}}
            changed-tap-filter {$fixture.inspection.adapters[0].packet_filter=43}
            active-tap {$fixture.inspection.adapters[0].ndis_mode=3}
            active-wifi {$fixture.inspection.adapters[1].ndis_mode=3}
            wrong-index {$fixture.inspection.adapters[1].interface_index=99}
            duplicate-role {$fixture.inspection.adapters[1].role='tap'}
            wrong-medium {$fixture.inspection.adapters[1].sdk_medium=0;$fixture.inspection.adapters[1].system_medium=0;$fixture.inspection.adapters[1].physical_medium=0}
            string-mode {$fixture.inspection.adapters[0].ndis_mode='0'}
            inspect-failure {$fixture.inspectFail=$true}
            concurrent-file {$fixture.concurrent=$true}
            session-mismatch {Change-RebootJson 'state/wifi-forwarder/status.json' {param($v)$v.session=[guid]::NewGuid().ToString('B')}}
            string-pid {Change-RebootJson 'state/wifi-forwarder/status.json' {param($v)$v.helper_pid='88'}}
            missing-status {Remove-Item -LiteralPath (Join-Path $VmRoot 'state/wifi-forwarder/status.json')}
            oversize {[IO.File]::WriteAllText((Join-Path $VmRoot 'state/wifi-forwarder/status.json'),[string]::new('x',65537))}
        }
        Reject {Archive-ReimsWifiPreviousBoot} '.'
        Check (Test-Path (Join-Path $VmRoot 'state/wifi-forwarder/runtime.json')) 'Refused recovery discarded old runtime'
        function Assert-ReimsOwnedWifiTap($Id,$PnpId){if($Id -ne $fixture.ownedTap -or $PnpId -ne 'ROOT\NET\0001'){throw 'Unowned policy status'};@{adapter_id=$Id}}
    }
    Reset-Reboot;$fixture.pidReused=$true
    # A fresh process with a reused PID is foreign; do not touch it or require
    # that every QEMU on the host is absent before starting a new owned VM.
    Archive-ReimsWifiPreviousBoot;$fixture.cases++
    Remove-Item Function:Get-Process
    . "$PSScriptRoot/../wifi-macnat.ps1"
    function Get-ReimsVmProcess { $null }
    # Exercise the actual binding identity reader, without any live CIM/NIC call.
    $fixture.wrongBinding=$false;$fixture.bridged=$false;$fixture.bindingWrites=@()
    function Get-NetAdapter {
        [pscustomobject]@{InterfaceGuid=$fixture.ownedTap;Name='Owned';InterfaceIndex=43}
        [pscustomobject]@{InterfaceGuid='44444444-4444-4444-8444-444444444444';Name='Foreign';InterfaceIndex=99}
    }
    function Get-NetAdapterBinding($Name){
        if($Name -ne 'Owned'){throw 'Unrelated binding query'}
        foreach($component in @('ms_tcpip','ms_tcpip6')){
            $id=if($fixture.wrongBinding){'44444444-4444-4444-8444-444444444444'}else{$fixture.ownedTap}
            [pscustomobject]@{ComponentID=$component;InstanceID="{$id}::$component";Enabled=$true}
        }
        if($fixture.bridged){[pscustomobject]@{ComponentID='ms_implat';InstanceID="{$($fixture.ownedTap)}::ms_implat";Enabled=$true}}
    }
    function Disable-NetAdapterBinding($Name,$ComponentID){$fixture.bindingWrites+=,@($Name,$ComponentID)}
    function Enable-NetAdapterBinding($Name,$ComponentID){$fixture.bindingWrites+=,@($Name,$ComponentID)}
    Check ((Get-ReimsWifiBindings $tapId).ms_tcpip) 'Valid exact binding identity rejected';$fixture.cases++
    $fixture.wrongBinding=$true
    Reject {Get-ReimsWifiBindings $tapId} 'identity is unknown'
    $fixture.wrongBinding=$false;$fixture.bridged=$true
    Reject {Get-ReimsWifiBindings $tapId} 'Windows bridge'
    $fixture.bridged=$false
    Set-ReimsWifiBinding $tapId ms_tcpip $false
    Check ($fixture.bindingWrites.Count -eq 1 -and $fixture.bindingWrites[0][0] -eq 'Owned') 'Binding setter used an unrelated adapter';$fixture.cases++
    Reject {Set-ReimsWifiBinding '55555555-5555-4555-8555-555555555555' ms_tcpip $false} 'missing or ambiguous'
    $fixture.sourcePolicy=@{ActiveStore=1;PersistentStore=$null};$fixture.policyCalls=@();$fixture.policyReads=@()
    function Get-NetIPInterface($InterfaceIndex,$AddressFamily,$PolicyStore){
        if($InterfaceIndex -ne 43 -or $AddressFamily -ne 'IPv6'){throw 'Foreign/family policy read'}
        $fixture.policyReads+=,$PolicyStore
        [pscustomobject]@{InterfaceIndex=43;RouterDiscovery=$fixture.sourcePolicy[$PolicyStore]}
    }
    function Set-NetIPInterface($InterfaceIndex,$AddressFamily,$PolicyStore,$RouterDiscovery){
        if($InterfaceIndex -ne 43 -or $AddressFamily -ne 'IPv6' -or $PolicyStore -ne 'ActiveStore'){throw 'Foreign/family/persistent policy mutation'}
        $fixture.policyCalls+=,@($PolicyStore,$RouterDiscovery)
    }
    $policy=& $routerReaderScript $tapId
    Check ($policy.ActiveStore -eq 'Enabled' -and !$policy.ContainsKey('PersistentStore') -and $fixture.policyReads -notcontains 'PersistentStore') 'Effective CIM ordinal not normalized or Persistent NULL read/guessed';$fixture.cases++
    $fixture.sourcePolicy.ActiveStore='Disabled';$policy=& $routerReaderScript $tapId
    Check ($policy.ActiveStore -eq 'Disabled') 'Typed RouterDiscovery enum rejected';$fixture.cases++
    $fixture.sourcePolicy.ActiveStore=9
    Reject {& $routerReaderScript $tapId} 'Unknown TAP IPv6'
    $fixture.sourcePolicy.ActiveStore=255
    Reject {& $routerReaderScript $tapId} 'Unknown TAP IPv6'
    Set-ReimsWifiRouterDiscovery $tapId ActiveStore Disabled
    Check ($fixture.policyCalls.Count -eq 1 -and $fixture.policyCalls[0][1] -eq 'Disabled') 'Selected-only RouterDiscovery write contract wrong';$fixture.cases++
    Reject {Set-ReimsWifiRouterDiscovery $tapId ActiveStore 'unknown'} 'Invalid RouterDiscovery'
    Reject {Set-ReimsWifiRouterDiscovery $tapId PersistentStore 'Disabled'} 'Invalid RouterDiscovery'
    # Signature/catalog-member failures must precede any netcfg install.
    $payload=Join-Path $VmRoot 'payload';[void][IO.Directory]::CreateDirectory($payload)
    [IO.File]::WriteAllText((Join-Path $payload 'ndisrd_lwf.inf'),"CatalogFile = ndisrd.cat`nDriverVer = 05/17/2025,3.6.2.1`n%ndisrd_Desc%=Install, nt_ndisrd`n")
    function Get-AuthenticodeSignature { @{Status=$fixture.signature;SignerCertificate=@{Subject='CN=Microsoft Windows Hardware Compatibility Publisher'}} }
    function Invoke-ReimsWifiNative($Program,$Arguments){$fixture.nativeCalls+=,($Arguments -join ' ');@{exit_code=$(if($fixture.badCatalog){1}else{0});output='fixture'}}
    $fixture.signature='NotSigned'
    Reject {Assert-ReimsWifiDriverPayload $payload 'fixture.exe'} 'WHCP'
    Check (!$fixture.nativeCalls.Count) 'Unsigned catalog reached native execution'
    $fixture.signature='Valid';$fixture.badCatalog=$true
    Reject {Assert-ReimsWifiDriverPayload $payload 'fixture.exe'} 'membership/trust'
    $fixture.badCatalog=$false;Assert-ReimsWifiDriverPayload $payload 'fixture.exe';$fixture.cases++
    Reject {Install-ReimsWifiDriver 'fixture-engine' $false} 'explicitly accept'
    $fixture.cases++
    $globalAction=Get-Content -Raw "$PSScriptRoot/../manager-backend.ps1"
    Check ($globalAction.IndexOf("if (`$Action -eq 'GetWifiMacNat' -and !`$VmDirectory)") -lt $globalAction.IndexOf('. "$PSScriptRoot/start.ps1"')) 'Global action loads VM environment'
    "PASS $($fixture.cases) Wi-Fi ownership, binding rollback, mode preservation, real-ready identity, stale PID/cleanup and catalog/consent CPU cases"
}finally{
    $allowed=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if($testRoot.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $testRoot -Recurse -Force}
}
