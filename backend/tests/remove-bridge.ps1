# Synthetic VM files/inventory/native calls only. No host adapter is queried or changed.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../network-uplinks.ps1"
$allowed=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
$root=Join-Path $allowed ('remove-bridge-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$tap='11111111-1111-4111-8111-111111111111';$uplink='22222222-2222-4222-8222-222222222222'
$bridge='33333333-3333-4333-8333-333333333333';$foreign='44444444-4444-4444-8444-444444444444'
$fixture=@{cases=0}
function Assert-ReimsVmStopped { if ($fixture.running) { throw 'Fixture VM is running' } }
function Get-ReimsHostBridgeIds { return $fixture.bridges }
function Get-ReimsNetworkInventory {
    return @{tap_adapters=@(@{id=$tap;name='Owned TAP'});uplinks=@(@{id=$uplink;name='Authored Wi-Fi'})}
}
function Invoke-ReimsNetworkCommand($Arguments) {
    $fixture.commands+=,($Arguments -join ' ')
    if (($Arguments -join ' ') -ne "bridge destroy {$bridge}") { throw 'Attempt to change an unowned interface/bridge' }
    if ($fixture.concurrentConfig) { [IO.File]::WriteAllText($fixture.configPath,'{"schema":1,"network":{"mode":"nat"},"user_change":"keep"}') }
    if ($fixture.destroyFailure) { throw 'Native destroy fixture failure' }
    if (!$fixture.stickyDestroy) { $fixture.bridges=@() }
    if ($fixture.foreignAfterDestroy) { $fixture.bridges=@($foreign) }
}
function Write-ReimsJson($Path,$Value) {
    if ($Path -eq $fixture.configPath -and $fixture.configFailure) { throw 'Config write fixture failure' }
    if ($Path -eq $fixture.recordPath -and $fixture.recordFailure) { throw 'Record write fixture failure' }
    [void][IO.Directory]::CreateDirectory((Split-Path $Path))
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 20))
}
function New-Case {
    $fixture.cases++
    $script:VmRoot=Join-Path $root ([string]$fixture.cases)
    [void][IO.Directory]::CreateDirectory((Join-Path $VmRoot 'state'))
    $fixture.running=$false;$fixture.bridges=@($bridge);$fixture.commands=@()
    $fixture.destroyFailure=$false;$fixture.configFailure=$false;$fixture.recordFailure=$false
    $fixture.stickyDestroy=$false;$fixture.foreignAfterDestroy=$false;$fixture.concurrentConfig=$false
    $fixture.configPath=Join-Path $VmRoot 'state/config.json';$fixture.recordPath=Join-Path $VmRoot 'state/host-bridge.json'
    $config=@{schema=1;mac='52:54:00:12:34:56';network=@{mode='bridge';adapter='Owned TAP';physical_adapter_id=$uplink;guest_host='guest.invalid'}}
    Write-ReimsJson $fixture.configPath $config
    # Preserve exact original bytes, including a BOM and formatting, on rollback.
    [IO.File]::WriteAllText($fixture.configPath,([char]0xfeff)+[IO.File]::ReadAllText($fixture.configPath)+"`r`n")
    Write-ReimsJson $fixture.recordPath @{schema=1;bridge_id=$bridge;tap_id=$tap;tap_name='Owned TAP';physical_adapter_id=$uplink;physical_adapter_name='Authored Wi-Fi';connectivity='unverified'}
    $fixture.originalHash=(Get-FileHash -LiteralPath $fixture.configPath).Hash
    $fixture.originalRecordHash=(Get-FileHash -LiteralPath $fixture.recordPath).Hash
}
function Reject([scriptblock]$Operation,[string]$Pattern) {
    $caught=$false;try { & $Operation | Out-Null } catch { if($_.Exception.Message -notmatch $Pattern){throw};$caught=$true }
    if(!$caught){throw "Expected refusal: $Pattern"}
}
function Assert-Unchanged {
    if ($fixture.commands.Count -or (Get-FileHash -LiteralPath $fixture.configPath).Hash -ne $fixture.originalHash) { throw 'Refusal changed configuration or host networking' }
}
try {
    New-Case;$fixture.running=$true
    Reject { Remove-ReimsHostBridge } 'running';Assert-Unchanged
    New-Case;$fixture.bridges=@($foreign)
    Reject { Remove-ReimsHostBridge } 'unknown or ambiguous';Assert-Unchanged
    New-Case;$fixture.bridges=@($bridge,$foreign)
    Reject { Remove-ReimsHostBridge } 'unknown or ambiguous';Assert-Unchanged
    New-Case;$fixture.bridges=@()
    Reject { Remove-ReimsHostBridge } 'unknown or ambiguous';Assert-Unchanged
    New-Case
    $bad=Get-Content -LiteralPath $fixture.recordPath -Raw | ConvertFrom-Json -AsHashtable;$bad.bridge_id='invalid';Write-ReimsJson $fixture.recordPath $bad
    Reject { Remove-ReimsHostBridge } 'invalid interface GUID';Assert-Unchanged
    New-Case
    $bad=Get-Content -LiteralPath $fixture.recordPath -Raw | ConvertFrom-Json -AsHashtable;$bad.tap_id=$bad.bridge_id;Write-ReimsJson $fixture.recordPath $bad
    Reject { Remove-ReimsHostBridge } 'ambiguous';Assert-Unchanged
    New-Case
    $bad=Get-Content -LiteralPath $fixture.recordPath -Raw | ConvertFrom-Json -AsHashtable;$bad.state='removed';Write-ReimsJson $fixture.recordPath $bad
    Reject { Remove-ReimsHostBridge } 'already removed';Assert-Unchanged
    New-Case
    $bad=Get-Content -LiteralPath $fixture.recordPath -Raw | ConvertFrom-Json -AsHashtable;$bad.tap_id=$foreign;Write-ReimsJson $fixture.recordPath $bad
    Reject { Remove-ReimsHostBridge } 'TAP/uplink identity';Assert-Unchanged
    New-Case
    $bad=Get-Content -LiteralPath $fixture.configPath -Raw | ConvertFrom-Json -AsHashtable;$bad.network.adapter='Unrelated VPN TAP';Write-ReimsJson $fixture.configPath $bad
    $fixture.originalHash=(Get-FileHash -LiteralPath $fixture.configPath).Hash
    Reject { Remove-ReimsHostBridge } 'does not match';Assert-Unchanged
    New-Case;$fixture.configFailure=$true
    Reject { Remove-ReimsHostBridge } 'Config write fixture';Assert-Unchanged
    New-Case;$fixture.destroyFailure=$true
    Reject { Remove-ReimsHostBridge } 'Native destroy fixture'
    if ((Get-FileHash -LiteralPath $fixture.configPath).Hash -ne $fixture.originalHash -or
        (Get-FileHash -LiteralPath $fixture.recordPath).Hash -ne $fixture.originalRecordHash -or $fixture.bridges[0] -ne $bridge) { throw 'Native failure did not restore exact configuration bytes' }
    New-Case;$fixture.stickyDestroy=$true
    Reject { Remove-ReimsHostBridge } 'empty topology'
    if ((Get-FileHash -LiteralPath $fixture.configPath).Hash -ne $fixture.originalHash) { throw 'A still-present bridge was reported removed' }
    New-Case;$fixture.recordFailure=$true
    Reject { Remove-ReimsHostBridge } 'Record write fixture'
    if ($fixture.bridges.Count -or (Get-Content -LiteralPath $fixture.configPath -Raw | ConvertFrom-Json).network.mode -ne 'nat') { throw 'Metadata failure restored a config pointing at a removed bridge' }
    New-Case;$fixture.foreignAfterDestroy=$true
    Reject { Remove-ReimsHostBridge } 'empty topology'
    if ($fixture.bridges[0] -ne $foreign -or $fixture.commands.Count -ne 1 -or (Get-Content -LiteralPath $fixture.configPath -Raw | ConvertFrom-Json).network.mode -ne 'nat') { throw 'Ambiguous post-state changed an unrelated bridge' }
    New-Case;$fixture.destroyFailure=$true;$fixture.concurrentConfig=$true
    Reject { Remove-ReimsHostBridge } 'modified_elsewhere'
    if ((Get-Content -LiteralPath $fixture.configPath -Raw | ConvertFrom-Json).user_change -ne 'keep') { throw 'Rollback overwrote another writer' }
    New-Case
    $removed=Remove-ReimsHostBridge
    $config=Get-Content -LiteralPath $fixture.configPath -Raw | ConvertFrom-Json
    $audit=Join-Path $VmRoot $removed.removal_audit
    if ($fixture.bridges.Count -or $fixture.commands.Count -ne 1 -or $config.network.mode -ne 'nat' -or $config.network.adapter -ne '' -or
        $config.mac -ne '52:54:00:12:34:56' -or $removed.state -ne 'removed' -or $removed.connectivity -ne 'unverified' -or
        (Get-FileHash -LiteralPath (Join-Path $audit 'config.json')).Hash -ne $fixture.originalHash -or
        (Get-FileHash -LiteralPath (Join-Path $audit 'host-bridge.json')).Hash -ne $fixture.originalRecordHash -or
        (Get-Content -LiteralPath (Join-Path $audit 'transaction.json') -Raw | ConvertFrom-Json).phase -ne 'complete') { throw 'Owned removal/NAT/audit transaction failed' }
    Reject { Get-ReimsSavedNetwork ([pscustomobject]@{network=[pscustomobject]@{mode='nat'}}) 'bridge' 'Owned TAP' '' $uplink } 'was removed'
    New-Case
    $nat=Get-Content -LiteralPath $fixture.configPath -Raw | ConvertFrom-Json -AsHashtable;$nat.network=@{mode='nat';adapter='';guest_host=''};Write-ReimsJson $fixture.configPath $nat
    if ((Remove-ReimsHostBridge).state -ne 'removed') { throw 'A created but not yet applied bridge could not be removed' }
    "PASS: $($fixture.cases) synthetic RemoveBridge cases; ownership, topology, exact rollback, concurrent data, NAT and retained audit; no host NIC calls"
} finally {
    if ([IO.Path]::GetFullPath($root).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $root -Leaf) -match '^remove-bridge-[a-f0-9]{32}$') { Remove-Item -LiteralPath $root -Recurse -Force }
}
