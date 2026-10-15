#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $repo ('.work/manager-ports-' + [guid]::NewGuid().ToString('N'))
$oldHome = $env:REIMS_MANAGER_HOME
try {
    $ProjectRoot = Join-Path $fixture 'engine'
    $env:REIMS_MANAGER_HOME = Join-Path $fixture 'preferences'
    New-Item -ItemType Directory -Force $ProjectRoot, $env:REIMS_MANAGER_HOME | Out-Null
    . "$PSScriptRoot/../manager-ports.ps1"
    $files = @()
    foreach ($i in 0..1) {
        $package = Join-Path $fixture "VM $i"
        $data = Join-Path $package 'machine.data/state'
        New-Item -ItemType Directory -Force $data | Out-Null
        $descriptor = Join-Path $package 'machine.reimsvm'
        @{schema=1;name="VM $i";directory='machine.data'} | ConvertTo-Json | Set-Content -LiteralPath $descriptor
        @{qmp_port=4444+2*$i;ssh_port=4445+2*$i} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'config.json')
        $files += $descriptor
    }
    # All VMs are stopped: none has a runtime or listening socket.
    @{VirtualMachines=@($files + (Join-Path $fixture 'unplugged.reimsvm'))} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:REIMS_MANAGER_HOME 'settings.json')
    $reserved = @(Get-ReimsReservedVmPorts)
    if (@(4444,4445,4446,4447 | Where-Object { $_ -notin $reserved }).Count) { throw 'Stopped VM ports were not reserved from application settings.' }
    $allocated = @(Get-NewVmPorts)
    if ($allocated.Count -ne 2 -or $allocated[0] -eq $allocated[1] -or @($allocated | Where-Object { $_ -in $reserved }).Count) { throw 'Port collision.' }
    '{broken' | Set-Content -LiteralPath (Join-Path $env:REIMS_MANAGER_HOME 'settings.json')
    $rejected = $false
    try { Get-NewVmPorts | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw 'An unreadable library must not silently allocate colliding ports.' }
    'PASS: application library, stopped VMs, missing descriptor, distinct available ports, corrupt settings'
} finally {
    $env:REIMS_MANAGER_HOME = $oldHome
    # Only this invocation's disposable fixture; never actual VM or settings directories.
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repo '.work')) + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
