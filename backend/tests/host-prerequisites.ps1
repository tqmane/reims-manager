# Authored host facts only; no registry, driver, GPU or VM is queried.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../host-prerequisites.ps1"
$root=Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")) ('prerequisites-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path "$root/engine/bin","$root/runtimes/python" | Out-Null
function Get-ReimsHostFacts { return $script:facts }
function Read-ReimsEngine($Directory,[switch]$VerifyFiles) {
    if (!$VerifyFiles) { throw 'Engine integrity verification was skipped' }
    return @{files=@{'bin/vulkan-1.dll'=('0'*64)}}
}
function Invoke-RestMethod {
    if ($script:unavailable) { throw 'Fixture offline' }
    $pin=Get-ReimsQualifiedGuestBridgeLock
    return @{draft=$false;prerelease=$false;tag_name=$pin.id;assets=@(@{name=$pin.asset;digest=('sha256:'+('a'*64));browser_download_url="https://github.com/$($pin.release_repository)/releases/download/$($pin.id)/$($pin.asset)"})}
}
try {
    [IO.File]::WriteAllText("$root/engine/bin/vulkan-1.dll",'authored fixture')
    [IO.File]::WriteAllText("$root/runtimes/python/python.exe",'not executable')
    $script:unavailable=$false
    $script:facts=@{feature_enabled=$true;whpx_ready=$true;firmware_virtualization=$true;restart_required=$false;vulkan_icd=$true;openssh=$true;usbdk=$false;available_memory_mib=16384;logical_cpus=8}
    $report=Get-ReimsPrerequisites "$root/engine" "$root/runtimes" 4 4096
    if (!$report.ready -or !$report.plan.start_ready -or $report.plan.cpus -ne 4 -or $report.plan.memory_mib -ne 4096) { throw 'Ready plan was lost' }
    if (($report.checks | Where-Object id -EQ usb).required -ne $false) { throw 'USB blocks default NAT' }
    $script:facts.available_memory_mib=1024
    $report=Get-ReimsPrerequisites "$root/engine" "$root/runtimes" 4 4096
    if (!$report.ready -or $report.plan.start_ready) { throw 'Low RAM must allow creation but prevent auto-start' }
    $script:facts.whpx_ready=$false
    $report=Get-ReimsPrerequisites "$root/engine" "$root/runtimes"
    if ($report.ready -or !$report.restart_required) { throw 'WHPX restart guidance missing' }
    $script:facts.feature_enabled=$false
    $report=Get-ReimsPrerequisites "$root/engine" "$root/runtimes"
    if (($report.checks | Where-Object id -EQ whpx).action -ne 'EnableWhpx') { throw 'Feature action missing' }
    $script:facts.whpx_ready=$true; $script:facts.feature_enabled=$true
    $script:unavailable=$true
    $report=Get-ReimsPrerequisites "$root/engine" "$root/runtimes"
    if ($report.ready -or ($report.checks | Where-Object id -EQ guest_addon).ready) { throw 'Unpublished addon was accepted' }
    # Exercise the real backend entrypoint with a trap for accidental VM environment loading.
    $stub=Join-Path $root 'backend'; New-Item -ItemType Directory $stub | Out-Null
    Copy-Item "$PSScriptRoot/../manager-backend.ps1" $stub
    [IO.File]::WriteAllText("$stub/start.ps1","throw 'VM environment was loaded'")
    [IO.File]::WriteAllText("$stub/host-prerequisites.ps1",'function Get-ReimsPrerequisites { @{ready=$true;restart_required=$false;checks=@();plan=@{cpus=4;memory_mib=4096;available_memory_mib=1024;start_ready=$false}} }')
    $json=& "$stub/manager-backend.ps1" -Root "$root/engine" -ToolsRoot "$root/runtimes" -Action GetPrerequisites
    if (!($json | ConvertFrom-Json).ready -or (Test-Path "$root/engine/.work") -or (Test-Path "$root/engine/vm")) { throw 'Global prerequisites mutated the engine or required a VM' }
    [IO.File]::WriteAllText("$stub/install-usbdk.ps1",'param($CacheDirectory) if (!$CacheDirectory) { throw "Missing user cache" }; "guided-usb-fixture"')
    if ((& "$stub/manager-backend.ps1" -Root "$root/engine" -Action InstallUsbDk) -ne 'guided-usb-fixture') { throw 'Global USB action required a VM' }
    'PASS: prerequisite readiness, memory plan, optional USB, unpublished addon, global no-VM boundary'
} finally {
    $allowed=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if ($root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
