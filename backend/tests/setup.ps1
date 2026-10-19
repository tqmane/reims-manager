# Orchestration fixtures only. Stub scripts never run native tools or touch a VM.
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")) ('setup-'+[guid]::NewGuid().ToString('N'))
$scripts=Join-Path $root 'backend'
New-Item -ItemType Directory -Force -Path $scripts | Out-Null
Copy-Item "$PSScriptRoot/../setup.ps1","$PSScriptRoot/../guest-bridge-release.ps1","$PSScriptRoot/../guest-bridge-lock.json","$PSScriptRoot/../release-asset.ps1" $scripts
function Write-ReimsJson($Path,$Value) { New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null; $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
function Get-ReimsVmConfig { Get-Content "$VmRoot/state/config.json" -Raw | ConvertFrom-Json }
function Assert-ReimsVmStopped {}
function Get-NewVmPorts { @(4444,2222) }
function Invoke-WebRequest {
    if ($script:unavailable) { throw 'Unpublished fixture' }
    @{StatusCode=200}
}
try {
    $oc=@'
param([switch]$Rebuild,[switch]$GenerateIdentity)
if ($GenerateIdentity) {
    if (!(Test-Path "$VmRoot/state/config.json")) { throw 'Identity requires the saved VM MAC/config' }
    Add-Content -LiteralPath $generationLog -Value 'generated'
    Write-ReimsJson "$VmRoot/state/opencore-identity.json" @{uuid='authored-fixed-identity'}
}
Write-ReimsJson "$VmRoot/state/opencore.json" @{image='before-efi'}
'@
    [IO.File]::WriteAllText("$scripts/prepare-opencore.ps1",$oc)
    [IO.File]::WriteAllText("$scripts/prepare-vm.ps1",@'
param($Iso,$DiskGiB,$QmpPort,$SshPort)
if (Test-Path "$VmRoot/state/config.json") { $c=Get-ReimsVmConfig; $c.iso=$Iso }
else { $c=@{iso=$Iso;disk_gib=$DiskGiB;cpus=8;memory_mib=8192;opencore='before-efi';firmware_vars='retained-nvram';mac='authored-mac'} }
Write-ReimsJson "$VmRoot/state/config.json" $c
'@)
    [IO.File]::WriteAllText("$scripts/prepare-tools.ps1",'[IO.File]::WriteAllText((Join-Path $VmRoot "tools-ready"),"authored tools")')
    [IO.File]::WriteAllText("$scripts/prepare-guest-bridge.ps1",@'
param($Mode)
$c=Get-ReimsVmConfig; $c.opencore='qualified-addon-efi'; Write-ReimsJson "$VmRoot/state/config.json" $c
Write-ReimsJson "$VmRoot/state/opencore.json" @{image='qualified-addon-efi'}
if ($bridgeFailure) { throw 'Addon fixture failure' }
'@)
    . "$scripts/setup.ps1"
    $script:unavailable=$false; $bridgeFailure=$false; $generationLog=Join-Path $root 'identity-generations.txt'
    $script:ReimsEngine=@{id='engine-b8b4486c-7100064d-2'}
    $VmRoot=Join-Path $root '日本語 VM with spaces'
    $iso=Join-Path $root 'authored.iso'; [IO.File]::WriteAllText($iso,'fixture, not an installer')
    $script:unavailable=$true
    $caught=$false;try { Invoke-ReimsVmSetup $iso 300 4 4096 | Out-Null } catch { $caught=$true }
    if (!$caught -or (Test-Path $VmRoot)) { throw 'Unpublished addon did not fail before VM preparation' }
    $script:unavailable=$false
    Invoke-ReimsVmSetup $iso 300 4 4096 | Out-Null
    $c=Get-ReimsVmConfig; $marker=Get-Content "$VmRoot/state/setup-preparation.json" -Raw | ConvertFrom-Json
    if ($c.cpus -ne 4 -or $c.memory_mib -ne 4096 -or $c.disk_gib -ne 300 -or $c.opencore -ne 'qualified-addon-efi' -or !$marker.complete -or !(Test-Path "$VmRoot/tools-ready")) { throw 'Setup did not complete the requested hardware/addon/tools plan' }
    if ($c.connect_installer -isnot [bool] -or !$c.connect_installer) { throw 'Fresh setup did not save its required installer preference' }
    $c.connect_installer=$false
    Write-ReimsJson "$VmRoot/state/config.json" $c
    Invoke-ReimsVmSetup $iso 300 4 4096 | Out-Null
    if ((Get-ReimsVmConfig).connect_installer -ne $false) { throw 'Repeated setup reset the saved installer preference' }
    $hashes=@{}; foreach($name in @('config.json','opencore.json','setup-preparation.json','opencore-identity.json')) { $hashes[$name]=(Get-FileHash "$VmRoot/state/$name").Hash }
    $bridgeFailure=$true
    $caught=$false;try { Invoke-ReimsVmSetup $iso 300 2 6144 | Out-Null } catch { $caught=$true }
    foreach ($name in $hashes.Keys) { if ((Get-FileHash "$VmRoot/state/$name").Hash -ne $hashes[$name]) { throw "Rollback changed $name" } }
    if (!$caught -or @(Get-Content $generationLog).Count -ne 1) { throw 'Existing setup failure regenerated identity or was swallowed' }
    Remove-Item -LiteralPath "$VmRoot/state/opencore.json"
    $caught=$false;try { Invoke-ReimsVmSetup $iso 300 4 4096 | Out-Null } catch { if($_.Exception.Message -notmatch 'missing its OpenCore manifest'){throw};$caught=$true }
    if (!$caught -or (Get-FileHash "$VmRoot/state/config.json").Hash -ne $hashes['config.json']) { throw 'Missing existing manifest silently regenerated firmware' }
    $VmRoot=Join-Path $root 'failed fresh VM'
    $caught=$false;try { Invoke-ReimsVmSetup $iso 300 4 4096 | Out-Null } catch { $caught=$true }
    if (!$caught -or (Test-Path "$VmRoot/state/config.json") -or (Test-Path "$VmRoot/state/setup-preparation.json")) { throw 'Incomplete fresh VM remained selected' }
    if (!(Test-Path "$VmRoot/state/opencore-identity.json")) { throw 'Fresh identity was not retained for retry' }
    'PASS: setup hardware plan, addon/tools completion, unavailable release, identity preservation, existing/fresh rollback'
} finally {
    $allowed=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if ($root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
