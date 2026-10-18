# Shared by the manager backend and isolated fixtures. No ports are changed on import.
function Get-ReimsRegisteredVmFiles {
    $homePath = if ($env:REIMS_MANAGER_HOME) { [IO.Path]::GetFullPath($env:REIMS_MANAGER_HOME) }
        else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Reims/Manager' }
    $settingsPath = Join-Path $homePath 'settings.json'
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        foreach ($file in $settings.VirtualMachines) { if ($file) { [string]$file } }
    }
    # Retain compatibility with a legacy engine-local library, without treating it as the source of truth.
    $legacyLibrary = Join-Path $ProjectRoot '.work/manager-library.json'
    if (Test-Path -LiteralPath $legacyLibrary -PathType Leaf) {
        foreach ($file in (Get-Content -LiteralPath $legacyLibrary -Raw | ConvertFrom-Json)) { if ($file) { [string]$file } }
    }
}

function Get-ReimsReservedVmPorts {
    $reserved = [Collections.Generic.HashSet[int]]::new()
    $configs = [Collections.Generic.List[string]]::new()
    $configs.Add((Join-Path $ProjectRoot 'vm/state/config.json'))
    foreach ($file in (Get-ReimsRegisteredVmFiles | Select-Object -Unique)) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $entry = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        if ($entry.schema -ne 1 -or !$entry.directory) { throw "登録 VM の形式を確認できません: $file" }
        $directory = [IO.Path]::GetFullPath([string]$entry.directory, (Split-Path ([IO.Path]::GetFullPath($file))))
        $configs.Add((Join-Path $directory 'state/config.json'))
    }
    foreach ($file in $configs) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $config = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        foreach ($key in @('qmp_port','ssh_port')) {
            $port = [int]$config.$key
            if ($port -lt 1024 -or $port -gt 65535) { throw "登録 VM のポートが不正です: $file" }
            [void]$reserved.Add($port)
        }
    }
    # Emit numbers, not a nested collection, so callers can build their own set consistently.
    foreach ($port in $reserved) { $port }
}

function Get-NewVmPorts {
    $reserved = [Collections.Generic.HashSet[int]]::new()
    foreach ($port in (Get-ReimsReservedVmPorts)) { [void]$reserved.Add([int]$port) }
    $result = @()
    for ($port = 4444; $port -lt 65000 -and $result.Count -lt 2; $port++) {
        if ($reserved.Contains($port)) { continue }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        try { $listener.Start(); $result += $port }
        catch [Net.Sockets.SocketException] { }
        finally { $listener.Stop() }
    }
    if ($result.Count -ne 2) { throw 'VM の接続ポートを確保できません。' }
    return $result
}
