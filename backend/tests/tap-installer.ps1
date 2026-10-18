# Authored ZIP/signature/inventory/native fixtures only. No host driver is installed.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../engine-package.ps1"
$root=Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")) ('tap-'+[guid]::NewGuid().ToString('N'))
$stub=Join-Path $root 'backend';$payload=Join-Path $root 'payload/dist.win10/amd64'
New-Item -ItemType Directory -Force -Path $stub,$payload | Out-Null
Copy-Item "$PSScriptRoot/../tap-installer.ps1" $stub
. "$stub/tap-installer.ps1"
$fixture=@{existing=@();created=$false;exit_code=0;ready=$true;commands=@();renames=@();badSignature='';badTool=$false;badRecord=$false;downloads=0;foreignDuringVerify=$false}
$pnp='ROOT\NET\0001';$guid='11111111-1111-4111-8111-111111111111'
$newAdapter=@{adapter_id=$guid;adapter_name='日本語の新規 TAP';pnp_instance_id=$pnp}
function Get-ReimsInstalledTaps {
    $fixture.existing
    if ($fixture.created -and $fixture.ready) { $newAdapter }
}
function Get-ReimsTapPnpIds {
    if ($fixture.created) { $pnp }
    foreach ($item in $fixture.existing) { $item.pnp_instance_id }
}
function Get-AuthenticodeSignature($LiteralPath) {
    if ($fixture.foreignDuringVerify) { $fixture.existing=@($newAdapter) }
    @{Status=$(if([IO.Path]::GetFileName($LiteralPath) -eq $fixture.badSignature){'NotSigned'}else{'Valid'});SignerCertificate=@{Subject='CN=Authored fixture, O=Microsoft Corporation, C=US'}}
}
function Get-ReimsDevconVersion {
    @{OriginalFilename=$(if($fixture.badTool){'unrelated.exe'}else{'DevCon.exe'});CompanyName='Microsoft Corporation'}
}
function Invoke-ReimsDevcon($Executable,$Arguments) {
    $fixture.commands+=,($Arguments -join ' ')
    if ($Arguments[0] -eq 'install') { $fixture.created=$true;return @{exit_code=$fixture.exit_code;output='authored install fixture'} }
    if ($Arguments[0] -eq 'remove' -and $Arguments[1] -eq "@$pnp") { $fixture.created=$false;return @{exit_code=0;output='authored removal fixture'} }
    throw 'Attempted unknown/wildcard device removal'
}
function Invoke-WebRequest($Uri,$OutFile,$TimeoutSec) { $fixture.downloads++;Copy-Item -LiteralPath $fixture.templateArchive -Destination $OutFile }
function Save-ReimsTapRecord($Path,$Record) {
    if ($fixture.badRecord) { throw 'Fixture record write failure' }
    $Record | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}
function Get-NetAdapter {
    foreach($item in $fixture.existing) { [pscustomobject]@{InterfaceGuid=$item.adapter_id;Name=$item.adapter_name;PnPDeviceID=$item.pnp_instance_id} }
    if ($fixture.created -and $fixture.ready) { [pscustomobject]@{InterfaceGuid=$guid;Name=$newAdapter.adapter_name;PnPDeviceID=$pnp} }
}
function Rename-NetAdapter($InputObject,$NewName,[switch]$Confirm,$ErrorAction) {
    if (!$fixture.created -or $InputObject.PnPDeviceID -ne $pnp -or $InputObject.InterfaceGuid -ne $guid) {throw 'Attempted to rename an unowned adapter'}
    if ($NewName -cne 'Reims TAP 11111111') {throw 'Created TAP name is not stable ASCII'}
    $fixture.renames+=,$InputObject.InterfaceGuid
    $newAdapter.adapter_name=$NewName
}
function Reject([scriptblock]$Action,[string]$Pattern) {
    $caught=$false;try { & $Action | Out-Null } catch {if($_.Exception.Message -notmatch $Pattern){throw};$caught=$true}
    if (!$caught) {throw "Expected refusal: $Pattern"}
}
try {
    foreach($name in @('devcon.exe','tap0901.cat','tap0901.sys')) { [IO.File]::WriteAllText((Join-Path $payload $name),'authored bytes, not executable') }
    [IO.File]::WriteAllText((Join-Path $payload 'OemVista.inf'),"CatalogFile = tap0901.cat`nDriverVer = 02/27/2024,9.27.0.0`n")
    $archive=Join-Path $root 'fixture.zip';[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $root 'payload'),$archive)
    $fixture.templateArchive=$archive
    $pin=Get-Content "$PSScriptRoot/../tap-lock.json" -Raw | ConvertFrom-Json
    if ($pin.sha256 -ne '36e2609b7ceefedcb978ce5c48caf9e0e5af83423717c4e2e3c1d7ebca8f62a5') {throw 'Official TAP pin changed'}
    $pin.sha256=(Get-FileHash $archive).Hash.ToLowerInvariant()
    $pin | ConvertTo-Json | Set-Content "$stub/tap-lock.json" -Encoding utf8NoBOM
    foreach($bad in @('devcon.exe','tap0901.cat','tap0901.sys')) {
        $fixture.badSignature=$bad
        Reject { Install-ReimsDedicatedTap "$root/bad-signature-$bad" $archive '' } 'valid Microsoft signature'
    }
    $fixture.badSignature='';$fixture.badTool=$true
    Reject { Install-ReimsDedicatedTap "$root/wrong-signed-tool" $archive '' } 'Expected Microsoft Device Console'
    $fixture.badTool=$false
    $wrong=Join-Path $root 'wrong.zip';[IO.File]::WriteAllText($wrong,'not the pinned archive')
    Reject { Install-ReimsDedicatedTap "$root/wrong-archive" $wrong '' } 'pinned release'
    Reject { Install-ReimsDedicatedTap "$root/legacy-exe" "$root/old.exe" '' } 'legacy EXE'
    if ($fixture.commands.Count) {throw 'Untrusted package reached native execution'}
    $fixture.existing=@(@{adapter_id='22222222-2222-4222-8222-222222222222';adapter_name='Other VPN TAP';pnp_instance_id='ROOT\NET\0022'})
    Reject { Install-ReimsDedicatedTap "$root/protected-vpn" '' '' } 'Existing TAP/VPN devices were preserved'
    $reused=Install-ReimsDedicatedTap "$root/explicit-reuse" '' 'Other VPN TAP'
    if (!$reused.reused -or $reused.owned -or $reused.created -or $fixture.commands.Count -or $fixture.downloads -or $fixture.renames.Count) {throw 'Explicit reuse changed an existing VPN'}
    $fixture.existing=@()
    $fixture.foreignDuringVerify=$true
    Reject { Install-ReimsDedicatedTap "$root/device-appeared" $archive '' } 'appeared during download'
    $fixture.foreignDuringVerify=$false;$fixture.existing=@()
    if ($fixture.commands.Count) {throw 'Concurrent TAP appearance reached installer'}
    $fresh=Install-ReimsDedicatedTap "$root/fresh" '' ''
    if (!$fresh.created -or !$fresh.owned -or $fresh.bridge_created -or $fresh.adapter_id -ne $guid -or $fresh.pnp_instance_id -ne $pnp -or $fixture.downloads -ne 1 -or $fresh.adapter_name -ne 'Reims TAP 11111111') {throw 'Automatic dedicated creation record/name incorrect'}
    $again=Install-ReimsDedicatedTap "$root/fresh" '' ''
    if (!$again.reused -or !$again.owned -or $fixture.commands.Count -ne 1) {throw 'Owned TAP reuse reinstalled driver'}
    $fixture.created=$false;$fixture.commands=@();$fixture.badRecord=$true
    Reject { Install-ReimsDedicatedTap "$root/rollback" $archive '' } 'Fixture record write failure'
    if ($fixture.created -or $fixture.commands.Count -ne 2 -or $fixture.commands[1] -ne "remove @$pnp") {throw 'Rollback did not remove only its owned PnP ID'}
    $fixture.badRecord=$false;$fixture.exit_code=2;$fixture.commands=@()
    Reject { Install-ReimsDedicatedTap "$root/native-failed" $archive '' } 'installation failed'
    if ($fixture.commands.Count -ne 1) {throw 'Failed native create removed an unproven device'}
    $fixture.created=$false;$fixture.exit_code=1;$fixture.ready=$false;$fixture.commands=@()
    $pending=Install-ReimsDedicatedTap "$root/reboot" $archive ''
    if (!$pending.restart_required -or $pending.adapter_id -or !$pending.owned) {throw 'Required reboot was hidden'}
    $pendingAgain=Install-ReimsDedicatedTap "$root/reboot" '' ''
    if (!$pendingAgain.restart_required -or !$pendingAgain.reused -or $fixture.commands.Count -ne 1) {throw 'Pending reboot repeated installer'}
    $fixture.ready=$true
    $ready=Install-ReimsDedicatedTap "$root/reboot" '' ''
    if ($ready.restart_required -or !$ready.owned -or $ready.adapter_id -ne $guid -or $fixture.commands.Count -ne 1) {throw 'Post-reboot owned device was not reused'}
    Copy-Item "$PSScriptRoot/../manager-backend.ps1" $stub
    [IO.File]::WriteAllText("$stub/start.ps1","throw 'VM environment unexpectedly loaded'")
    [IO.File]::WriteAllText("$stub/install-tap.ps1",'param($Installer,$Adapter) if ($Installer) {throw "Unexpected file dialog argument"}; ''{"schema":1,"created":false,"reused":true,"owned":false,"restart_required":false,"bridge_created":false}''')
    $json=& "$stub/manager-backend.ps1" -Root "$root/nonexistent-engine" -Action InstallTap
    if (!($json | ConvertFrom-Json).reused -or (Test-Path "$root/nonexistent-engine")) {throw 'Automatic InstallTap required a VM or engine writes'}
    'PASS: pinned ZIP/signature/tool trust, VPN preservation, created-only ASCII naming, reuse, appearance race, exact-PnP rollback, reboot reporting, global no-VM action; no driver installed'
} finally {
    $allowed=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if ($root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
