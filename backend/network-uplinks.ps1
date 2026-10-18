# Network inventory is global/read-only. Host changes require explicit CreateBridge.
function Get-ReimsNetworkInventory {
    $adapters=@(Get-NetAdapter -ErrorAction Stop)
    $uplinks=@(foreach ($adapter in $adapters | Where-Object HardwareInterface) {
        @{id=([guid]$adapter.InterfaceGuid).ToString('D');name=[string]$adapter.Name
          description=[string]$adapter.InterfaceDescription;status=[string]$adapter.Status
          connected=([string]$adapter.Status -eq 'Up')
          kind=$(switch ([int]$adapter.InterfaceType) {71 {'wifi'};6 {'ethernet'};default {'other'}})
          bridge_support='unverified'}
    })
    $taps=@(foreach ($adapter in $adapters | Where-Object InterfaceDescription -Match 'TAP-Windows|TAP-Win32|TAP.*Adapter') {
        @{id=([guid]$adapter.InterfaceGuid).ToString('D');name=[string]$adapter.Name
          description=[string]$adapter.InterfaceDescription;status=[string]$adapter.Status}
    })
    return @{schema=1;uplinks=$uplinks;tap_adapters=$taps}
}

function Resolve-ReimsPhysicalUplink([string]$Selection) {
    $matches=@((Get-ReimsNetworkInventory).uplinks | Where-Object { $_.id -eq $Selection.Trim('{}') -or $_.name -eq $Selection })
    if ($matches.Count -ne 1) { throw 'Select an existing physical Wi-Fi or wired adapter by its uplink ID.' }
    return $matches[0]
}

function Invoke-ReimsNetworkCommand([string[]]$Arguments) {
    $output=@(& "$env:SystemRoot/System32/netsh.exe" @Arguments 2>&1 | ForEach-Object ToString)
    if ($LASTEXITCODE -ne 0) { throw "Windows bridge command failed ($LASTEXITCODE): $($output -join ' ')" }
    return $output
}

function Get-ReimsHostBridgeIds {
    $text=(Invoke-ReimsNetworkCommand @('bridge','list')) -join "`n"
    # Native list identifies bridges by GUID; no localized headings are parsed.
    return @([regex]::Matches($text,'(?i)\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b') | ForEach-Object { ([guid]$_.Value).ToString('D') } | Select-Object -Unique)
}

function New-ReimsHostBridge([string]$TapName,[string]$PhysicalSelection) {
    $uplink=Resolve-ReimsPhysicalUplink $PhysicalSelection
    if (!$uplink.connected) { throw 'Connect the selected physical adapter before creating a bridge.' }
    $taps=@((Get-ReimsNetworkInventory).tap_adapters | Where-Object name -EQ $TapName)
    if ($taps.Count -ne 1) { throw 'Select the dedicated internal TAP adapter; no physical NIC is passed to QEMU.' }
    $before=@(Get-ReimsHostBridgeIds)
    if ($before.Count) { throw 'Existing Windows bridges are preserved. Inspect them before creating a new Reims bridge.' }
    $created=@()
    $createSucceeded=$false
    try {
        [void](Invoke-ReimsNetworkCommand @('bridge','create',([guid]$taps[0].id).ToString('B'),([guid]$uplink.id).ToString('B')))
        $createSucceeded=$true
        $created=@(Get-ReimsHostBridgeIds | Where-Object { $_ -notin $before })
        if ($created.Count -ne 1) { throw 'Cannot uniquely identify the new bridge; no unknown bridge will be deleted.' }
        $record=@{schema=1;bridge_id=$created[0];tap_id=$taps[0].id;tap_name=$taps[0].name
            physical_adapter_id=$uplink.id;physical_adapter_name=$uplink.name;connectivity='unverified'}
        Write-ReimsJson (Join-Path $VmRoot 'state/host-bridge.json') $record
        return $record
    } catch {
        $failure=$_
        if ($createSucceeded -and !$created.Count) { $created=@(Get-ReimsHostBridgeIds | Where-Object { $_ -notin $before }) }
        if ($created.Count -eq 1) {
            try { [void](Invoke-ReimsNetworkCommand @('bridge','destroy',([guid]$created[0]).ToString('B'))) }
            catch { throw "Bridge setup failed and rollback failed. Inspect Windows network settings. Setup: $($failure.Exception.Message); rollback: $($_.Exception.Message)" }
        }
        throw $failure
    }
}

function Get-ReimsSavedNetwork($Config,[string]$Mode,[string]$TapName,[string]$GuestHost,[string]$PhysicalSelection) {
    $network=@{mode=$Mode;adapter=$TapName;guest_host=$GuestHost}
    if ($PhysicalSelection) {
        $uplink=Resolve-ReimsPhysicalUplink $PhysicalSelection
        if ($Mode -eq 'bridge') {
            $path=Join-Path $VmRoot 'state/host-bridge.json'
            if (!(Test-Path -LiteralPath $path)) { throw 'Run CreateBridge for the selected physical uplink before saving bridged mode. NAT was not changed.' }
            $bridge=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($bridge.PSObject.Properties['state'] -and $bridge.state -eq 'removed') { throw 'Run CreateBridge again; this owned bridge record was removed.' }
            $tap=@((Get-ReimsNetworkInventory).tap_adapters | Where-Object { $_.name -eq $TapName -and $_.id -eq $bridge.tap_id })
            if ($bridge.physical_adapter_id -ne $uplink.id -or $tap.Count -ne 1 -or $bridge.bridge_id -notin @(Get-ReimsHostBridgeIds)) {
                throw 'The recorded bridge does not match this uplink/TAP selection. Existing network settings were preserved.'
            }
        }
        $network.physical_adapter_id=$uplink.id; $network.physical_adapter_name=$uplink.name
    } elseif ($Config.PSObject.Properties['network'] -and $Config.network.PSObject.Properties['physical_adapter_id'] -and $Config.network.adapter -eq $TapName) {
        $network.physical_adapter_id=$Config.network.physical_adapter_id
        $network.physical_adapter_name=$Config.network.physical_adapter_name
    }
    return $network
}

function Remove-ReimsHostBridge {
    Assert-ReimsVmStopped
    $configPath=Join-Path $VmRoot 'state/config.json'
    $recordPath=Join-Path $VmRoot 'state/host-bridge.json'
    if (!(Test-Path -LiteralPath $configPath -PathType Leaf) -or !(Test-Path -LiteralPath $recordPath -PathType Leaf)) { throw 'A VM configuration and its owned bridge record are required.' }
    $configBytes=[IO.File]::ReadAllBytes($configPath);$recordBytes=[IO.File]::ReadAllBytes($recordPath)
    $config=[Text.Encoding]::UTF8.GetString($configBytes).TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable
    $record=[Text.Encoding]::UTF8.GetString($recordBytes).TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable
    if ($config.schema -ne 1 -or $record.schema -ne 1 -or $record['state'] -eq 'removed') { throw 'The active owned bridge metadata is invalid or already removed.' }
    foreach ($key in @('bridge_id','tap_id','physical_adapter_id')) {
        $id=[guid]::Empty
        if (![guid]::TryParse([string]$record[$key],[ref]$id) -or $id -eq [guid]::Empty) { throw 'The owned bridge record has an invalid interface GUID.' }
        $record[$key]=$id.ToString('D')
    }
    if (@(@($record.bridge_id,$record.tap_id,$record.physical_adapter_id) | Select-Object -Unique).Count -ne 3) { throw 'The owned bridge interface GUIDs are ambiguous.' }
    $bridges=@(Get-ReimsHostBridgeIds)
    if ($bridges.Count -ne 1 -or $bridges[0] -ne $record.bridge_id) { throw 'The live bridge topology is unknown or ambiguous; no bridge was changed.' }
    $inventory=Get-ReimsNetworkInventory
    if (@($inventory.tap_adapters | Where-Object { $_.id -eq $record.tap_id -and $_.name -eq $record.tap_name }).Count -ne 1 -or
        @($inventory.uplinks | Where-Object id -EQ $record.physical_adapter_id).Count -ne 1) { throw 'The owned TAP/uplink identity no longer matches; no bridge was changed.' }
    if ($config.network.mode -notin @('nat','bridge') -or ($config.network.mode -eq 'bridge' -and
        ($config.network.adapter -ne $record.tap_name -or $config.network.physical_adapter_id -ne $record.physical_adapter_id))) { throw 'The VM network does not match its owned bridge record.' }
    $originalHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($configBytes))
    $recordHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($recordBytes))
    $auditDirectory=Join-Path $VmRoot ('backups/network-remove-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($auditDirectory)
    [IO.File]::WriteAllBytes((Join-Path $auditDirectory 'config.json'),$configBytes)
    [IO.File]::WriteAllBytes((Join-Path $auditDirectory 'host-bridge.json'),$recordBytes)
    $audit=@{schema=1;operation='RemoveBridge';bridge_id=$record.bridge_id;phase='prepared';started_utc=[DateTime]::UtcNow.ToString('o')}
    $auditPath=Join-Path $auditDirectory 'transaction.json'
    Write-ReimsJson $auditPath $audit
    $natHash=$null
    try {
        Assert-ReimsVmStopped
        $live=@(Get-ReimsHostBridgeIds)
        if ($live.Count -ne 1 -or $live[0] -ne $record.bridge_id -or (Get-FileHash -LiteralPath $configPath).Hash -ne $originalHash -or
            (Get-FileHash -LiteralPath $recordPath).Hash -ne $recordHash) { throw 'Bridge metadata/configuration changed during preparation.' }
        # Commit a usable NAT configuration before the host bridge can disappear.
        $config.network=@{mode='nat';adapter='';guest_host=''}
        Write-ReimsJson $configPath $config
        $natHash=(Get-FileHash -LiteralPath $configPath).Hash
        [void](Invoke-ReimsNetworkCommand @('bridge','destroy',([guid]$record.bridge_id).ToString('B')))
        if (@(Get-ReimsHostBridgeIds).Count) { throw 'Bridge removal did not leave the expected empty topology.' }
        $record.state='removed';$record.removed_utc=[DateTime]::UtcNow.ToString('o')
        $record.removal_audit=[IO.Path]::GetRelativePath($VmRoot,$auditDirectory).Replace('\','/')
        Write-ReimsJson $recordPath $record
        $audit.phase='complete';$audit.configuration='nat';$audit.finished_utc=[DateTime]::UtcNow.ToString('o')
        Write-ReimsJson $auditPath $audit
        return $record
    } catch {
        $failure=$_.Exception.Message
        $audit.phase='manual_recovery_required';$audit.configuration='unknown';$audit.error=$failure
        try {
            $currentHash=(Get-FileHash -LiteralPath $configPath).Hash
            if ($currentHash -eq $originalHash) { $audit.configuration='unchanged' }
            elseif ($natHash -and $currentHash -eq $natHash) {
                $audit.configuration='nat'
                $remaining=@(Get-ReimsHostBridgeIds)
                if ($remaining.Count -eq 1 -and $remaining[0] -eq $record.bridge_id) {
                    $temporary=Join-Path (Split-Path $configPath) ('.network-restore-'+[guid]::NewGuid().ToString('N')+'.tmp')
                    try {
                        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                        try { $stream.Write($configBytes,0,$configBytes.Length);$stream.Flush($true) } finally { $stream.Dispose() }
                        [IO.File]::Move($temporary,$configPath,$true)
                    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
                    $audit.configuration='restored';$audit.phase='native_failed_config_restored'
                }
            } else { $audit.configuration='modified_elsewhere' }
        } catch { $failure+=" Recovery inspection failed: $($_.Exception.Message)" }
        $audit.finished_utc=[DateTime]::UtcNow.ToString('o')
        try { Write-ReimsJson $auditPath $audit } catch { $failure+=" Audit update failed: $($_.Exception.Message)" }
        throw "RemoveBridge failed ($($audit.configuration)); inspect $auditDirectory. $failure"
    }
}
