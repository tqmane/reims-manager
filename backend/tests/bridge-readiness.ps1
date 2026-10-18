# Synthetic IP/binding/native/time providers only. No NIC or real wait is used.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../network-uplinks.ps1"
$allowed=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
$caseRoot=Join-Path $allowed ('bridge-readiness-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
$testUplinkId='11111111-1111-4111-8111-111111111111';$testTapId='22222222-2222-4222-8222-222222222222'
$testBridgeId='33333333-3333-4333-8333-333333333333';$testForeignId='44444444-4444-4444-8444-444444444444'
$fixture=@{cases=0}
function Assert-ReimsVmStopped { if($fixture.running){throw 'Fixture VM running'} }
function Get-ReimsNetworkInventory { @{uplinks=@(@{id=$testUplinkId;name='Selected';connected=$true});tap_adapters=@(@{id=$testTapId;name='Owned TAP'})} }
function Get-ReimsHostBridgeIds { if($fixture.created){$testBridgeId} }
function Get-NetAdapter {
    [pscustomobject]@{InterfaceGuid=$testUplinkId;InterfaceIndex=1;Status='Up'}
    [pscustomobject]@{InterfaceGuid=$testTapId;InterfaceIndex=2;Status='Up'}
    [pscustomobject]@{InterfaceGuid=$testForeignId;InterfaceIndex=9;Status='Up'}
    if($fixture.created -and !$fixture.missingBridge){[pscustomobject]@{InterfaceGuid=$testBridgeId;InterfaceIndex=3;Status='Up'}}
}
function Get-NetIPConfiguration($InterfaceIndex) {
    $fixture.ipReads+=,$InterfaceIndex
    if($InterfaceIndex -eq 1){
        if($fixture.beforeFailure){throw 'Fixture preselected IP query failure'}
        $addresses=$fixture.beforeAddresses;$gateway=$fixture.beforeGateway
    } elseif($InterfaceIndex -eq 3){
        if($fixture.postFailure){throw 'Fixture bridge IP query failure'}
        $addresses=$(if($fixture.clock -ge $fixture.readyAfter){$fixture.afterAddresses}else{@('169.254.1.2')})
        $gateway=$fixture.afterGateway
    } else {throw 'Read attempted on unrelated/physical wired NIC'}
    [pscustomobject]@{IPv4Address=@($addresses | ForEach-Object {[pscustomobject]@{IPAddress=$_}});IPv4DefaultGateway=@(if($gateway){[pscustomobject]@{NextHop=$gateway}})}
}
function Get-NetAdapterBinding {
    foreach($id in @($testTapId,$testUplinkId)+$(if($fixture.foreignMember){@($testForeignId)}else{@()})){
        [pscustomobject]@{ComponentID='ms_implat';Enabled=$true;InstanceID="{$id}::ms_implat"}
    }
}
function Invoke-ReimsNetworkCommand($Arguments) {
    if($Arguments[1] -eq 'show'){
        foreach($id in @($testTapId,$testUplinkId)+$(if($fixture.foreignMember){@($testForeignId)}else{@()})){"1 {$id} Ignored friendly name Yes Yes disabled"}
        return
    }
    $fixture.commands+=,($Arguments -join ' ')
    if(($Arguments -join ' ') -eq "bridge create {$testTapId} {$testUplinkId}"){$fixture.created=$true;return}
    if(($Arguments -join ' ') -eq "bridge destroy {$testBridgeId}"){
        if($fixture.destroyFailure){throw 'Fixture destroy failure'}
        $fixture.created=$false;return
    }
    throw 'Unexpected network mutation/renew/reset/metric/ping'
}
function Get-ReimsNetworkTime { [DateTime]::new(2026,1,1,0,0,0,[DateTimeKind]::Utc).AddMilliseconds($fixture.clock) }
function Start-Sleep($Milliseconds) { if($Milliseconds -ne 500){throw 'Unexpected polling interval'};$fixture.clock+=500 }
function Write-ReimsJson($Path,$Value) {
    if($fixture.writeFailure){throw 'Fixture record write failure'}
    if($Value.host_ipv4_before.usable -and $fixture.clock -lt $fixture.readyAfter){throw 'Record saved before lease readiness'}
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 10))
}
function New-Case {
    $fixture.cases++;$script:VmRoot=Join-Path $caseRoot ([string]$fixture.cases)
    [void][IO.Directory]::CreateDirectory((Join-Path $VmRoot 'state'))
    $fixture.running=$false;$fixture.created=$false;$fixture.commands=@();$fixture.ipReads=@();$fixture.clock=0;$fixture.readyAfter=0
    $fixture.beforeAddresses=@('192.0.2.2');$fixture.beforeGateway='192.0.2.1'
    $fixture.afterAddresses=@('198.51.100.2');$fixture.afterGateway='198.51.100.1'
    $fixture.beforeFailure=$false;$fixture.postFailure=$false;$fixture.missingBridge=$false;$fixture.foreignMember=$false
    $fixture.destroyFailure=$false;$fixture.writeFailure=$false
    $fixture.config=Join-Path $VmRoot 'state/config.json';$fixture.record=Join-Path $VmRoot 'state/host-bridge.json'
    [IO.File]::WriteAllText($fixture.config,'{"schema":1,"network":{"mode":"nat","adapter":""}}')
    [IO.File]::WriteAllText($fixture.record,'{"schema":1,"state":"removed","retained":"old"}')
    $fixture.originalConfig=[IO.File]::ReadAllText($fixture.config);$fixture.originalRecord=[IO.File]::ReadAllText($fixture.record)
}
function Reject([scriptblock]$Action,[string]$Pattern) {
    $caught=$false;try{& $Action | Out-Null}catch{if($_.Exception.Message -notmatch $Pattern){throw};$caught=$true}
    if(!$caught){throw "Expected refusal: $Pattern"}
    if([IO.File]::ReadAllText($fixture.config) -ne $fixture.originalConfig -or [IO.File]::ReadAllText($fixture.record) -ne $fixture.originalRecord){throw 'Failure changed NAT or published an unready record'}
}
try {
    foreach($bad in @('169.254.1.2','127.0.0.1','0.0.0.0','224.0.0.1','255.255.255.255','::1','bad')){if(Test-ReimsUsableIpv4 $bad){throw 'Invalid IPv4 accepted'}}
    New-Case;$fixture.running=$true
    Reject {New-ReimsHostBridge 'Owned TAP' $testUplinkId} 'VM running'
    if($fixture.commands.Count){throw 'Running VM reached native create'}
    New-Case;$fixture.beforeFailure=$true
    Reject {New-ReimsHostBridge 'Owned TAP' $testUplinkId} 'preselected IP query'
    if($fixture.commands.Count){throw 'Failed preflight reached native create'}
    New-Case
    $record=New-ReimsHostBridge 'Owned TAP' $testUplinkId
    if($record.host_ipv4_readiness -ne 'recovered' -or $fixture.clock -ne 0 -or !$record.host_ipv4_before.usable){throw 'Immediate readiness failed'}
    New-Case;$fixture.readyAfter=1500
    $record=New-ReimsHostBridge 'Owned TAP' $testUplinkId
    if($fixture.clock -ne 1500 -or $record.connectivity -ne 'unverified'){throw 'Delayed readiness or no-Internet-claim failed'}
    foreach($problem in @('lla','gateway','missing','query')){
        New-Case
        switch($problem){lla{$fixture.afterAddresses=@('169.254.1.2')};gateway{$fixture.afterGateway=''};missing{$fixture.missingBridge=$true};query{$fixture.postFailure=$true}}
        Reject {New-ReimsHostBridge 'Owned TAP' $testUplinkId} 'within 30 seconds'
        if($fixture.created -or $fixture.clock -ne 30000 -or $fixture.commands[-1] -ne "bridge destroy {$testBridgeId}"){throw 'Bounded lease failure did not roll back only its bridge'}
    }
    New-Case;$fixture.beforeGateway=''
    if((New-ReimsHostBridge 'Owned TAP' $testUplinkId).host_ipv4_readiness -ne 'not_required' -or $fixture.clock){throw 'Previously unusable uplink incorrectly required lease migration'}
    New-Case;$fixture.afterGateway='';$fixture.foreignMember=$true
    Reject {New-ReimsHostBridge 'Owned TAP' $testUplinkId} 'safe owned rollback'
    if(!$fixture.created -or @($fixture.commands | Where-Object {$_ -like 'bridge destroy*'}).Count){throw 'Foreign NIC was disconnected during cleanup'}
    New-Case;$fixture.afterGateway='';$fixture.destroyFailure=$true
    Reject {New-ReimsHostBridge 'Owned TAP' $testUplinkId} 'Fixture destroy failure'
    if(!$fixture.created){throw 'Rollback failure was reported as removal'}
    New-Case;$fixture.writeFailure=$true
    Reject {New-ReimsHostBridge 'Owned TAP' $testUplinkId} 'record write failure'
    if($fixture.created){throw 'Write failure did not clean up owned bridge'}
    "PASS: $($fixture.cases) synthetic CreateBridge readiness cases; 30s/500ms, lease validation, owned/member-safe rollback, NAT preservation; no host calls/waits"
} finally {
    if([IO.Path]::GetFullPath($caseRoot).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $caseRoot -Recurse -Force}
}
