# Functions only. Tests substitute inventory/signature/native operations.
function Get-ReimsInstalledTaps {
    $devices=@(Get-CimInstance Win32_NetworkAdapter -ErrorAction Stop)
    foreach ($adapter in @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object InterfaceDescription -Match 'TAP-Windows|TAP-Win32|TAP.*Adapter')) {
        $guid=([guid]$adapter.InterfaceGuid).ToString('D')
        $device=@($devices | Where-Object { $_.GUID -and ([guid]$_.GUID).ToString('D') -eq $guid })
        if ($device.Count -ne 1) { throw 'Cannot identify an existing TAP device safely.' }
        @{adapter_id=$guid;adapter_name=[string]$adapter.Name;pnp_instance_id=[string]$device[0].PNPDeviceID}
    }
}

function Get-ReimsTapPnpIds {
    foreach ($device in @(Get-PnpDevice -Class Net -ErrorAction Stop | Where-Object InstanceId -Match '^ROOT\\NET\\[0-9A-F]+$')) {
        $property=Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_HardwareIds -ErrorAction Stop
        if (@($property.Data) -contains 'tap0901') { [string]$device.InstanceId }
    }
}

function Get-ReimsDevconVersion([string]$Path) { return (Get-Item -LiteralPath $Path).VersionInfo }

function Assert-ReimsTapPackage([string]$Directory) {
    foreach ($name in @('devcon.exe','tap0901.cat','tap0901.sys')) {
        $path=Join-Path $Directory $name
        $signature=Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
            throw "Expected a valid Microsoft signature: $name"
        }
    }
    $version=Get-ReimsDevconVersion (Join-Path $Directory 'devcon.exe')
    if ($version.OriginalFilename -ine 'devcon.exe' -or $version.CompanyName -ne 'Microsoft Corporation') { throw 'Unexpected signed tool; expected Microsoft Device Console.' }
    $inf=Get-Content -LiteralPath (Join-Path $Directory 'OemVista.inf') -Raw
    if ($inf -notmatch '(?im)^\s*CatalogFile\s*=\s*tap0901\.cat\s*$' -or
        $inf -notmatch '(?im)^\s*DriverVer\s*=\s*[^,\r\n]+,\s*9\.27\.0\.0\s*$') { throw 'Unexpected TAP INF/catalog version.' }
    # Windows validates INF membership in the signed catalog during installation.
}

function Invoke-ReimsDevcon([string]$Executable,[string[]]$Arguments) {
    $output=@(& $Executable @Arguments 2>&1 | ForEach-Object ToString)
    return @{exit_code=$LASTEXITCODE;output=($output -join "`n")}
}

function Save-ReimsTapRecord([string]$Path,$Record) {
    $Record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$Path.new" -Encoding utf8NoBOM
    Move-Item -LiteralPath "$Path.new" -Destination $Path -Force
}

function Set-ReimsCreatedTapName($Adapter) {
    $name='Reims TAP '+([guid]$Adapter.adapter_id).ToString('N').Substring(0,8)
    $nic=@(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { ([guid]$_.InterfaceGuid).ToString('D') -eq $Adapter.adapter_id })
    if ($nic.Count -ne 1) { throw 'Cannot uniquely identify the created adapter for naming.' }
    Rename-NetAdapter -InputObject $nic[0] -NewName $name -Confirm:$false -ErrorAction Stop
    $Adapter.adapter_name=$name
}

function Install-ReimsDedicatedTap([string]$CacheDirectory,[string]$Archive,[string]$Adapter) {
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'The portable Reims engine requires Windows x64.' }
    $existing=@(Get-ReimsInstalledTaps)
    $recordPath=Join-Path $CacheDirectory 'tap-adapter.json'
    $owned=$null
    if (Test-Path -LiteralPath $recordPath) { $owned=Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json }
    $selected=@()
    if ($Adapter) {
        $selected=@($existing | Where-Object { $_.adapter_id -eq $Adapter.Trim('{}') -or $_.adapter_name -eq $Adapter })
        if ($selected.Count -ne 1) { throw 'Select an existing dedicated TAP by name or interface GUID.' }
    } elseif ($owned) {
        $selected=@($existing | Where-Object { (!$owned.adapter_id -or $_.adapter_id -eq $owned.adapter_id) -and $_.pnp_instance_id -eq $owned.pnp_instance_id })
    }
    if ($selected.Count -eq 1) {
        $result=@{}+$selected[0]
        $result+=@{schema=1;created=$false;reused=$true;owned=[bool]($owned -and (!$owned.adapter_id -or $owned.adapter_id -eq $result.adapter_id) -and $owned.pnp_instance_id -eq $result.pnp_instance_id);restart_required=$false;bridge_created=$false}
        if ($result.owned -and $owned.restart_required -and !$owned.adapter_id) {
            Set-ReimsCreatedTapName $result
            Save-ReimsTapRecord $recordPath $result
        }
        return $result
    }
    $before=@(Get-ReimsTapPnpIds)
    if ($owned -and $owned.restart_required -and $owned.pnp_instance_id -in $before) {
        return @{schema=1;created=$false;reused=$true;owned=$true;adapter_id=$null;adapter_name=$null;pnp_instance_id=$owned.pnp_instance_id;restart_required=$true;bridge_created=$false}
    }
    # devcon install may update all matching hardware IDs, not just its new node.
    # Never run it over another application's TAP driver/device.
    if ($existing.Count -or $before.Count) { throw 'Existing TAP/VPN devices were preserved. Explicitly select a dedicated TAP to reuse; no driver/device was changed.' }
    $pin=Get-Content -LiteralPath "$PSScriptRoot/tap-lock.json" -Raw | ConvertFrom-Json
    if ($Archive -and [IO.Path]::GetExtension($Archive) -ine '.zip') { throw 'Select the official 9.27.0 dist.win10 ZIP; legacy EXE installers are not used.' }
    New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
    if (!$Archive) { $Archive=Join-Path $CacheDirectory 'dist.win10-9.27.0.zip'; Get-ReimsPinnedDownload $pin.url $pin.sha256 $Archive }
    $work=Join-Path $CacheDirectory ('tap-install-'+[guid]::NewGuid().ToString('N'))
    $createdId=$null;$nativeSucceeded=$false;$lock=$null
    $driverLocks=[Collections.Generic.List[IO.FileStream]]::new()
    try {
        $lock=[IO.File]::Open([IO.Path]::GetFullPath($Archive),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        if ((Get-FileHash -LiteralPath $Archive).Hash -ne $pin.sha256) { throw 'Official TAP ZIP differs from the pinned release.' }
        Expand-ReimsPackage $Archive $work
        $driver=Join-Path $work $pin.directory
        foreach ($name in @('devcon.exe','tap0901.cat','tap0901.sys','OemVista.inf')) {
            $driverLocks.Add([IO.File]::Open((Join-Path $driver $name),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
        }
        Assert-ReimsTapPackage $driver
        if (@(Get-ReimsInstalledTaps).Count -or @(Get-ReimsTapPnpIds).Count) { throw 'A TAP/VPN device appeared during download/verification; no installer was run.' }
        $native=Invoke-ReimsDevcon (Join-Path $driver 'devcon.exe') @('install',(Join-Path $driver 'OemVista.inf'),'tap0901')
        $native.output | Set-Content -LiteralPath (Join-Path $CacheDirectory 'last-install.log') -Encoding utf8NoBOM
        if ($native.exit_code -notin @(0,1)) { throw "Official TAP installation failed ($($native.exit_code)); inspect last-install.log. No unknown device was removed." }
        $nativeSucceeded=$true
        $deadline=[DateTime]::UtcNow.AddSeconds(10)
        do {
            $new=@(Get-ReimsTapPnpIds | Where-Object { $_ -notin $before })
            if ($new.Count -or [DateTime]::UtcNow -ge $deadline) { break }
            Start-Sleep -Milliseconds 100
        } while ($true)
        if ($new.Count -ne 1 -or $new[0] -notmatch '^ROOT\\NET\\[0-9A-F]+$') { throw 'Cannot uniquely identify the created TAP device; inspect Windows Device Manager before retrying.' }
        $createdId=$new[0]
        do {
            $adapters=@(Get-ReimsInstalledTaps | Where-Object pnp_instance_id -EQ $createdId)
            if ($adapters.Count -or $native.exit_code -eq 1 -or [DateTime]::UtcNow -ge $deadline) { break }
            Start-Sleep -Milliseconds 100
        } while ($true)
        if ($adapters.Count -gt 1 -or (!$adapters.Count -and $native.exit_code -ne 1)) { throw 'New TAP adapter was not uniquely enumerated.' }
        # QEMU's Windows TAP name lookup is ANSI; only our new adapter is renamed.
        if ($adapters.Count) { Set-ReimsCreatedTapName $adapters[0] }
        $record=@{schema=1;created=$true;reused=$false;owned=$true;pnp_instance_id=$createdId
            adapter_id=$(if($adapters.Count){$adapters[0].adapter_id}else{$null})
            adapter_name=$(if($adapters.Count){$adapters[0].adapter_name}else{$null})
            restart_required=($native.exit_code -eq 1);bridge_created=$false
            manual_removal="pnputil /remove-device $createdId"}
        Save-ReimsTapRecord $recordPath $record
        return $record
    } catch {
        if ($nativeSucceeded -and $createdId) {
            $removed=Invoke-ReimsDevcon (Join-Path $work "$($pin.directory)/devcon.exe") @('remove',"@$createdId")
            if ($removed.exit_code -notin @(0,1)) { throw "TAP setup and owned-device rollback failed. Remove only device $createdId in Device Manager; see last-install.log." }
        }
        throw
    } finally {
        foreach ($driverLock in $driverLocks) { $driverLock.Dispose() }
        if ($lock) { $lock.Dispose() }
        $allowed=[IO.Path]::GetFullPath($CacheDirectory).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
        if ([IO.Path]::GetFullPath($work).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force }
    }
}
