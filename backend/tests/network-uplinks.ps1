# Synthetic inventory/netsh only. No host networking or VM is changed.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../network-uplinks.ps1"
$root=Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")) ('network-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
$VmRoot=$root
$fixture=@{bridges=@();commands=@();writeFailure=$false;createFailure=$false;unrelatedOnFailure=$false}
$wired='11111111-1111-4111-8111-111111111111';$wifi='22222222-2222-4222-8222-222222222222';$tap='33333333-3333-4333-8333-333333333333';$bridge='44444444-4444-4444-8444-444444444444'
function Get-NetAdapter {
    @(
        [pscustomobject]@{Name='Wired';InterfaceGuid=$wired;HardwareInterface=$true;InterfaceType=6;InterfaceDescription='Authored Ethernet';Status='Up'},
        [pscustomobject]@{Name='Wireless';InterfaceGuid=$wifi;HardwareInterface=$true;InterfaceType=71;InterfaceDescription='Authored WLAN';Status='Disconnected'},
        [pscustomobject]@{Name='Internal TAP';InterfaceGuid=$tap;HardwareInterface=$false;InterfaceType=53;InterfaceDescription='TAP-Windows Adapter';Status='Disconnected'},
        [pscustomobject]@{Name='VMware VMnet8';InterfaceGuid='55555555-5555-4555-8555-555555555555';HardwareInterface=$false;InterfaceType=6;InterfaceDescription='VMware Adapter';Status='Up'}
    )
}
function Invoke-ReimsNetworkCommand($Arguments) {
    $fixture.commands+=,($Arguments -join ' ')
    switch ($Arguments[1]) {
        list { return ($fixture.bridges -join "`n") }
        create {
            if ($fixture.createFailure) { if($fixture.unrelatedOnFailure){$fixture.bridges=@('66666666-6666-4666-8666-666666666666')}; throw 'Native create fixture failure' }
            $fixture.bridges=@($bridge);return 'Created fixture bridge'
        }
        destroy { $fixture.bridges=@($fixture.bridges | Where-Object { $_ -ne $Arguments[2] }); return 'Removed fixture bridge' }
        default { throw 'Unexpected native operation' }
    }
}
function Write-ReimsJson($Path,$Value) {
    if ($fixture.writeFailure) { throw 'Record write fixture failure' }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $Value | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}
function Reject([scriptblock]$Operation,[string]$Pattern) {
    $caught=$false;try { & $Operation | Out-Null } catch { if($_.Exception.Message -notmatch $Pattern){throw};$caught=$true }
    if(!$caught){throw "Expected refusal: $Pattern"}
}
try {
    $inventory=Get-ReimsNetworkInventory
    if ($inventory.uplinks.Count -ne 2 -or $inventory.tap_adapters.Count -ne 1 -or $inventory.uplinks[1].kind -ne 'wifi' -or $inventory.uplinks[1].connected) { throw 'Physical/virtual inventory classification failed' }
    Reject { Resolve-ReimsPhysicalUplink 'VMware VMnet8' } 'physical'
    Reject { New-ReimsHostBridge 'Internal TAP' $wifi } 'Connect the selected'
    $config=[pscustomobject]@{network=[pscustomobject]@{mode='nat';adapter='';guest_host=''}}
    if ((Get-ReimsSavedNetwork $config 'nat' '' '' $wired).mode -ne 'nat' -or $fixture.commands.Count) { throw 'Saving NAT changed host networking' }
    Reject { Get-ReimsSavedNetwork $config 'bridge' 'Internal TAP' '' $wired } 'Run CreateBridge'
    $record=New-ReimsHostBridge 'Internal TAP' $wired
    if ($record.connectivity -ne 'unverified' -or $record.physical_adapter_id -ne $wired -or $record.tap_id -ne $tap) { throw 'Bridge selection record wrong' }
    if ($fixture.commands -notcontains "bridge create $tap $wired") { throw 'Native bridge did not use selected interface GUIDs' }
    $saved=Get-ReimsSavedNetwork $config 'bridge' 'Internal TAP' 'guest.invalid' $wired
    if ($saved.adapter -ne 'Internal TAP' -or $saved.physical_adapter_id -ne $wired) { throw 'Physical name incorrectly became QEMU TAP ifname' }
    Reject { New-ReimsHostBridge 'Internal TAP' $wired } 'Existing Windows bridges'
    $fixture.bridges=@();$fixture.writeFailure=$true
    Reject { New-ReimsHostBridge 'Internal TAP' $wired } 'Record write fixture'
    if ($fixture.bridges.Count -or $fixture.commands -notcontains "bridge destroy $bridge") { throw 'Owned bridge rollback failed' }
    $fixture.writeFailure=$false;$fixture.createFailure=$true;$fixture.unrelatedOnFailure=$true
    $before=@($fixture.commands).Count
    Reject { New-ReimsHostBridge 'Internal TAP' $wired } 'Native create fixture'
    if (@($fixture.commands | Select-Object -Skip $before | Where-Object {$_ -like 'bridge destroy*'}).Count) { throw 'Failed create destroyed an unrelated bridge' }
    # The global action must not source VM environment scripts.
    $stub=Join-Path $root 'backend';New-Item -ItemType Directory $stub | Out-Null
    Copy-Item "$PSScriptRoot/../manager-backend.ps1","$PSScriptRoot/../network-uplinks.ps1" $stub
    [IO.File]::WriteAllText("$stub/start.ps1","throw 'VM environment loaded'")
    $json=& "$stub/manager-backend.ps1" -Root "$root/engine" -Action GetNetworkUplinks
    if (($json | ConvertFrom-Json).uplinks.Count -ne 2 -or (Test-Path "$root/engine/.work")) { throw 'Global inventory required a VM or engine writes' }
    'PASS: physical Wi-Fi/wired inventory, NAT preservation, GUID bridge selection, owned rollback, unrelated bridge protection, global read-only action'
} finally {
    $allowed=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if ($root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
