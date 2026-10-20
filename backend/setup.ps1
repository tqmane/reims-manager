# Orchestration only; callers load start.ps1 and manager-ports.ps1 first.
function Invoke-ReimsVmSetup([string]$Iso, [int]$DiskGiB, [int]$Cpus=8, [int]$MemoryMiB=8192) {
    Assert-ReimsVmStopped
    if (!$Iso -or !(Test-Path -LiteralPath $Iso -PathType Leaf)) { throw 'macOS インストーラーを選択してください。' }
    . "$PSScriptRoot/guest-bridge-release.ps1"
    $pin=Get-ReimsQualifiedGuestBridgeLock
    if (!(Test-ReimsGuestBridgeEngine $pin $script:ReimsEngine)) { throw 'Install and select the matching portable engine before setup.' }
    # Fail before preparing disks/firmware when the required addon is unpublished.
    [void](Get-ReimsGuestBridgeRelease $pin)
    $existing=Test-Path -LiteralPath (Join-Path $VmRoot 'state/config.json')
    if ($existing -and !(Test-Path -LiteralPath (Join-Path $VmRoot 'state/opencore.json'))) {
        throw 'Existing VM is missing its OpenCore manifest. Restore its matched EFI/identity metadata before setup; it was not regenerated.'
    }
    $backup=Join-Path $VmRoot ('backups/setup-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $saved=@{}
    foreach ($name in @('config.json','opencore.json','setup-preparation.json')) {
        $path=Join-Path $VmRoot "state/$name"
        if (Test-Path -LiteralPath $path) {
            Copy-Item -LiteralPath $path -Destination (Join-Path $backup $name)
            $saved[$name]=$path
        }
    }
    try {
    if (!(Test-Path -LiteralPath (Join-Path $VmRoot 'state/opencore.json'))) {
        & "$PSScriptRoot/prepare-opencore.ps1" -Rebuild | Out-Null
    }
    $ports=Get-NewVmPorts
    & "$PSScriptRoot/prepare-vm.ps1" -Iso $Iso -DiskGiB $DiskGiB -QmpPort $ports[0] -SshPort $ports[1] | Out-Null
    # macserial identity generation takes its ROM from the newly saved VM MAC.
    if (!$existing -and !(Test-Path -LiteralPath (Join-Path $VmRoot 'state/opencore-identity.json'))) {
        & "$PSScriptRoot/prepare-opencore.ps1" -GenerateIdentity -Rebuild | Out-Null
    }
    $config=Get-ReimsVmConfig
    $config.cpus=$Cpus; $config.memory_mib=$MemoryMiB
    if (!$existing) { $config | Add-Member -Force connect_installer $true }
    Write-ReimsJson (Join-Path $VmRoot 'state/config.json') $config
    & "$PSScriptRoot/prepare-tools.ps1" | Out-Null
    & "$PSScriptRoot/prepare-guest-bridge.ps1" -Mode Enable
    Write-ReimsJson (Join-Path $VmRoot 'state/setup-preparation.json') @{
        schema=1;complete=$true;engine_id=$script:ReimsEngine.id;guest_addon_id=$pin.id
        macos_installation='user';prepared_utc=[DateTime]::UtcNow.ToString('o')
    }
    '準備完了。インストーラー付きで起動し、macOS のインストールと初回設定を行ってください。'
    'macOS 設定後、ReimsTools の setup.command を実行して SSH・共有機能をペアリングしてください。'
    foreach ($limit in $pin.limitations) { Write-Output $limit }
    } catch {
        foreach ($name in $saved.Keys) {
            $source=Join-Path $backup $name
            if (!(Test-Path -LiteralPath $saved[$name]) -or (Get-FileHash $saved[$name]).Hash -ne (Get-FileHash $source).Hash) {
                Copy-Item -LiteralPath $source -Destination $saved[$name] -Force
            }
        }
        # Fresh disks/EFI/identity remain for retry, but no incomplete VM is selected.
        foreach ($name in @('config.json','setup-preparation.json')) {
            $path=Join-Path $VmRoot "state/$name"
            if (!$saved.ContainsKey($name) -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Force }
        }
        throw
    }
}
