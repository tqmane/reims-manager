#Requires -Version 7.2
[CmdletBinding()]
param([ValidateSet('Enable','Disable')][string]$Mode='Enable', [string]$Archive, [string]$Sha256)
. "$PSScriptRoot/start.ps1" -FunctionsOnly
Assert-ReimsVmStopped
[void](Get-ReimsVmConfig)
$pinPath=Join-Path $PSScriptRoot 'guest-bridge-lock.json'
$pin=Get-Content -Raw -LiteralPath $pinPath | ConvertFrom-Json
$statePath=Join-Path $VmRoot 'state/config.json'
$ocPath=Join-Path $VmRoot 'state/opencore.json'
$stateHash=(Get-FileHash -LiteralPath $statePath).Hash; $ocHash=(Get-FileHash -LiteralPath $ocPath).Hash
$state=Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json -AsHashtable
$oc=Get-Content -Raw -LiteralPath $ocPath | ConvertFrom-Json -AsHashtable
$vmPrefix=$VmRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
foreach ($path in @($oc.config,$oc.image)) {
    if (!(Assert-ReimsArtifactPath $path).StartsWith($vmPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'OpenCore files must belong to the selected VM.' }
}
if ([IO.Path]::GetFullPath($state.opencore) -ne [IO.Path]::GetFullPath($oc.image)) { throw 'VM and OpenCore manifests select different images; reconcile them before applying the bridge.' }
$sourceRoot=Split-Path (Split-Path (Split-Path $oc.config))
if ([IO.Path]::GetFullPath((Join-Path $sourceRoot 'EFI/OC/config.plist')) -ne [IO.Path]::GetFullPath($oc.config)) { throw 'Unexpected OpenCore layout.' }
foreach ($item in @((Get-Item -LiteralPath $sourceRoot)) + @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force)) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'EFI links are not supported; the original is unchanged.' }
}
[void](Assert-ReimsArtifactPath $oc.ocvalidate)
$python=Get-ReimsTool 'python.exe'
$patch=Join-Path $PSScriptRoot 'opencore/guest-bridge.py'
if ($Mode -eq 'Enable') {
    Invoke-ReimsTool $python @('-X','utf8',$patch,'check-release','.',$pinPath)
    if (!$script:ReimsEngine -or $script:ReimsEngine.id -ne $pin.required_engine_id) { throw 'Select the matching portable engine package before applying this guest bridge.' }
}
$work=Join-Path $ProjectRoot ('.work/tmp/guest-bridge-'+[guid]::NewGuid().ToString('N'))
$slot=Join-Path $VmRoot ('firmware/guest-bridge-'+[guid]::NewGuid().ToString('N'))
$keepSlot=$false; $archiveLock=$null
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $package=Join-Path $work 'package'
    if ($Mode -eq 'Enable') {
        if ($Archive) { $Archive=[IO.Path]::GetFullPath($Archive); $Sha256=Get-ReimsArchiveDigest $Archive $Sha256 }
        else {
            Write-Output 'WebGL 互換ドライバーの対応版を取得しています…'
            $release=Invoke-RestMethod "https://api.github.com/repos/$($pin.release_repository)/releases/tags/$($pin.id)" -Headers @{'User-Agent'='ReimsManager'} -TimeoutSec 30
            if ($release.draft -or $release.prerelease -or $release.tag_name -ne $pin.id) { throw 'The qualified guest bridge release is not available.' }
            $assets=@($release.assets | Where-Object name -EQ $pin.asset)
            if ($assets.Count -ne 1 -or $assets[0].digest -notmatch '^sha256:([a-f0-9]{64})$') { throw 'Guest bridge release has no unique SHA256 asset.' }
            $Sha256=$Matches[1]
            $url="https://github.com/$($pin.release_repository)/releases/download/$($pin.id)/$($pin.asset)"
            if ($assets[0].browser_download_url -ne $url) { throw 'Unexpected guest bridge URL.' }
            $Archive=Join-Path $ProjectRoot ".work/downloads/$($pin.id).zip"
            Get-ReimsPinnedDownload $url $Sha256 $Archive
        }
        $archiveLock=[IO.File]::Open($Archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        if ((Get-FileHash -LiteralPath $Archive).Hash -ne $Sha256) { throw 'Guest bridge archive SHA256 mismatch.' }
        Expand-ReimsPackage $Archive $package
        $archiveLock.Dispose(); $archiveLock=$null
        Invoke-ReimsTool $python @('-X','utf8',$patch,'verify',$package,$pinPath)
    }
    Write-Output '現在の EFI のコピーで設定を検証しています…'
    New-Item -ItemType Directory -Path $slot | Out-Null
    $root=Join-Path $slot 'root'
    Copy-Item -LiteralPath $sourceRoot -Destination $root -Recurse
    $patchArguments=@('-X','utf8',$patch,$Mode.ToLower(),$root,$pinPath)
    if ($Mode -eq 'Enable') { $patchArguments+=@('--package',$package) }
    Invoke-ReimsTool $python $patchArguments
    $plist=Join-Path $root 'EFI/OC/config.plist'
    Invoke-ReimsTool $oc.ocvalidate @($plist) | Out-Null
    $image=Join-Path $slot 'OpenCore.img'
    Invoke-ReimsTool (Get-ReimsTool 'qemu-img.exe') @('convert','-f','vvfat','-O','raw',('fat:'+$root.Replace('\','/')),$image) | Out-Null
    $stream=[IO.File]::OpenRead($image)
    try {
        [void]$stream.Seek(450,[IO.SeekOrigin]::Begin); $type=$stream.ReadByte()
        [void]$stream.Seek(510,[IO.SeekOrigin]::Begin)
        if ($type -notin @(0x06,0x0e) -or $stream.ReadByte() -ne 0x55 -or $stream.ReadByte() -ne 0xaa) { throw 'Expected a FAT16 OpenCore image.' }
    } finally { $stream.Dispose() }
    Assert-ReimsVmStopped
    if ((Get-FileHash -LiteralPath $statePath).Hash -ne $stateHash -or (Get-FileHash -LiteralPath $ocPath).Hash -ne $ocHash) { throw 'VM settings changed during preparation; the active image was not replaced.' }
    Copy-Item -LiteralPath $statePath -Destination (Join-Path $slot 'before-config.json')
    Copy-Item -LiteralPath $ocPath -Destination (Join-Path $slot 'before-opencore.json')
    $oc.config=$plist; $oc.config_sha256=(Get-FileHash -LiteralPath $plist).Hash
    $oc.image=$image; $oc.image_sha256=(Get-FileHash -LiteralPath $image).Hash
    $state.opencore=$image
    # Retain both images/backups even if a metadata write fails during the switch.
    $keepSlot=$true
    try {
        Write-ReimsJson $ocPath $oc
        Write-ReimsJson $statePath $state
    } catch {
        Copy-Item -LiteralPath (Join-Path $slot 'before-opencore.json') -Destination $ocPath -Force
        Copy-Item -LiteralPath (Join-Path $slot 'before-config.json') -Destination $statePath -Force
        throw
    }
    "Metal / WebGL 互換設定: $Mode / $($pin.abi_family)。元の EFI と識別情報を保持しました。"
} finally {
    if ($archiveLock) { $archiveLock.Dispose() }
    $workPrefix=[IO.Path]::GetFullPath((Join-Path $ProjectRoot '.work/tmp'))+[IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($work).StartsWith($workPrefix,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force }
    $slotPrefix=[IO.Path]::GetFullPath((Join-Path $VmRoot 'firmware'))+[IO.Path]::DirectorySeparatorChar
    if (!$keepSlot -and [IO.Path]::GetFullPath($slot).StartsWith($slotPrefix,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $slot)) { Remove-Item -LiteralPath $slot -Recurse -Force }
}
