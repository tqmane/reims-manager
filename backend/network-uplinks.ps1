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
