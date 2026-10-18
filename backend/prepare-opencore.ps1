#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.8',
    [string]$QemuImg,
    [string]$FirmwareDirectory,
    [switch]$GenerateIdentity,
    [string]$IdentityFile,
    [ValidateSet('Keep','Enable','Disable')][string]$AppleAccountCompatibility = 'Keep',
    [switch]$ApplyBootProfile,
    [switch]$Rebuild
)
. "$PSScriptRoot/start.ps1" -FunctionsOnly
Assert-ReimsVmStopped
if ($GenerateIdentity -and !$Rebuild) { throw 'Use -GenerateIdentity with -Rebuild.' }
if ($IdentityFile -and (!$Rebuild -or $GenerateIdentity)) { throw 'Use -IdentityFile with -Rebuild, without -GenerateIdentity.' }
if ($AppleAccountCompatibility -ne 'Keep' -and !$Rebuild) { throw 'Use -AppleAccountCompatibility with -Rebuild.' }
if ($ApplyBootProfile -and !$Rebuild) { throw 'Use -ApplyBootProfile with -Rebuild.' }
$downloads = Join-Path $ProjectRoot '.work/downloads'
$pin = (Get-ReimsEngineLock).opencore
if ($Version -ne $pin.version) { throw 'Use the OpenCore version qualified with this manager.' }
$expectedHash = $pin.sha256; $url = $pin.url
$archive = if ($script:ReimsEngine) { Get-ReimsEnginePath 'opencore_archive' '' } else { Join-Path $downloads $pin.file }
Get-ReimsPinnedDownload $url $expectedHash $archive
$package = Join-Path $downloads "OpenCore-$Version-$($expectedHash.Substring(0,12))"
$validatedArchive = "$package.sha256"
if (!(Test-Path -LiteralPath $validatedArchive)) {
    if (Test-Path -LiteralPath $package) { throw 'Unverified OpenCore extraction already exists; use a new engine folder.' }
    Expand-ReimsPackage $archive $package
    $expectedHash | Set-Content -LiteralPath $validatedArchive -Encoding ascii
}
if ((Get-Content -Raw -LiteralPath $validatedArchive).Trim() -ne $expectedHash) { throw 'OpenCore extraction belongs to a different release.' }
$QemuImg = Get-ReimsTool 'qemu-img.exe' $QemuImg
$python = Get-ReimsTool 'python.exe'
$destination = Join-Path $VmRoot "firmware/opencore-$Version"
$efiRoot = Join-Path $destination 'root'
$image = Join-Path $destination 'OpenCore.img'
$manifestPath = Join-Path $VmRoot 'state/opencore.json'
if (Test-Path -LiteralPath $manifestPath) {
    $active = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($active.version -ne $Version) { throw 'Keep the installed OpenCore version when rebuilding an existing VM.' }
    $activeConfig = Assert-ReimsArtifactPath $active.config
    $efiRoot = Split-Path (Split-Path (Split-Path $activeConfig))
    if ([IO.Path]::GetFullPath((Join-Path $efiRoot 'EFI/OC/config.plist')) -ne $activeConfig) { throw 'Unexpected active OpenCore configuration layout.' }
    $destination = Split-Path $efiRoot
    $image = Assert-ReimsArtifactPath $active.image
}
New-Item -ItemType Directory -Force -Path (Join-Path $efiRoot 'EFI/BOOT'), (Join-Path $efiRoot 'EFI/OC/Drivers') | Out-Null
foreach ($relative in @('BOOT/BOOTx64.efi', 'OC/OpenCore.efi', 'OC/Drivers/OpenRuntime.efi', 'OC/Drivers/OpenPartitionDxe.efi', 'OC/Drivers/OpenHfsPlus.efi')) {
    Copy-Item -LiteralPath (Join-Path $package "X64/EFI/$relative") -Destination (Join-Path $efiRoot "EFI/$relative") -Force
}
$plist = Join-Path $efiRoot 'EFI/OC/config.plist'
if (!(Test-Path -LiteralPath $plist)) {
    & $python -X utf8 "$PSScriptRoot/opencore/base.py" (Join-Path $package 'Docs/Sample.plist') $plist
    if ($LASTEXITCODE -ne 0) { throw 'OpenCore configuration generation failed.' }
}
if ($GenerateIdentity) {
    $vmConfig = Get-ReimsVmConfig
    $model = & $python -X utf8 -c 'import plistlib,sys; print(plistlib.load(open(sys.argv[1],"rb"))["PlatformInfo"]["Generic"]["SystemProductName"])' $plist
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the installed Mac model.' }
    $pairs = (Invoke-ReimsTool (Join-Path $package 'Utilities/macserial/macserial.exe') @('-m', $model, '-n', '1')) -join "`n"
    $match = [regex]::Match($pairs, '^([A-Z0-9]{12})\s*\|\s*([A-Z0-9]{17})$', [Text.RegularExpressions.RegexOptions]::Multiline)
    if (!$match.Success) { throw 'macserial did not return a serial/MLB pair.' }
    $identityPath = Join-Path $VmRoot 'state/opencore-identity.json'
    Write-ReimsJson $identityPath @{
        model = $model; serial = $match.Groups[1].Value
        mlb = $match.Groups[2].Value; mac = $vmConfig.mac
    }
    $backup = Join-Path $ProjectRoot ('.work/diagnostics/opencore-before-identity-' + (Get-Date -Format 'yyyyMMdd-HHmmss-ffff') + '.plist')
    Copy-Item -LiteralPath $plist -Destination $backup
    $script = @'
import json, pathlib, plistlib, sys, uuid
p, manifest = map(pathlib.Path, sys.argv[1:])
c = plistlib.loads(p.read_bytes())
i = json.loads(manifest.read_text(encoding='utf-8-sig'))
g = c['PlatformInfo']['Generic']
assert g['SystemProductName'] == i['model'], 'Keep the installed Mac model'
uuid.UUID(g['SystemUUID'])
rom = bytes.fromhex(i['mac'].replace(':', ''))
assert len(rom) == 6 and not rom[0] & 1, 'ROM must be a unicast VM MAC'
g.update(SystemSerialNumber=i['serial'], MLB=i['mlb'], ROM=rom)
c['PlatformInfo'].update(Automatic=True, UpdateDataHub=True, UpdateNVRAM=True, UpdateSMBIOS=True)
i.update(uuid=g['SystemUUID'], rom=rom.hex())
p.write_bytes(plistlib.dumps(c, sort_keys=False))
manifest.write_text(json.dumps(i, indent=2) + '\n', encoding='utf-8')
'@
    $script | & $python -X utf8 - $plist $identityPath
    if ($LASTEXITCODE -ne 0) { throw 'OpenCore identity update failed; the previous plist is saved in diagnostics.' }
    "Generated a VM-specific identity; preserved SystemUUID. Backup: $backup"
}
$validate = Join-Path $package 'Utilities/ocvalidate/ocvalidate.exe'
if ($ApplyBootProfile) {
    $backup = Join-Path $ProjectRoot ('.work/diagnostics/opencore-before-boot-profile-' + [guid]::NewGuid().ToString('N') + '.plist')
    Copy-Item -LiteralPath $plist -Destination $backup
    $candidate = "$backup.candidate"
    & $python -X utf8 "$PSScriptRoot/opencore/boot-policy.py" $plist $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Boot profile update failed; the original config is unchanged.' }
    Invoke-ReimsTool $validate @($candidate) | Out-Null
    Move-Item -LiteralPath $candidate -Destination $plist -Force
    "起動項目を APFS / HFS に限定しました。変更前の設定: $backup"
}
if ($IdentityFile) {
    [void](Assert-ReimsArtifactPath $IdentityFile)
    $backup = Join-Path $ProjectRoot ('.work/diagnostics/opencore-before-edit-' + (Get-Date -Format 'yyyyMMdd-HHmmss-ffff') + '.plist')
    Copy-Item -LiteralPath $plist -Destination $backup
    $candidate = "$backup.candidate"
    $identityEdit = @'
import json, pathlib, plistlib, re, sys, uuid
source, destination, identity = map(pathlib.Path, sys.argv[1:])
c = plistlib.loads(source.read_bytes())
i = json.loads(identity.read_text(encoding='utf-8-sig'))
assert set(i) == {'model','serial','mlb','uuid','rom'}, 'Pass all five identity fields'
assert all(isinstance(v,str) for v in i.values()), 'Identity fields must be strings'
i = {k:v.strip() for k,v in i.items()}
assert re.fullmatch(r'(?:iMac|MacPro|Macmini|MacBookPro|MacBookAir|MacBook)\d+,\d+', i['model']), 'Invalid Mac model'
assert re.fullmatch(r'[A-Z0-9]{10,12}', i['serial']), 'Serial must be 10 to 12 uppercase letters/digits'
assert re.fullmatch(r'[A-Z0-9]{17}', i['mlb']), 'MLB must be 17 uppercase letters/digits'
u = uuid.UUID(i['uuid']); assert u.int, 'UUID cannot be zero'
assert re.fullmatch(r'[0-9A-Fa-f]{12}', i['rom']), 'ROM must be 12 hex digits'
rom = bytes.fromhex(i['rom']); assert any(rom) and not rom[0] & 1, 'ROM must be a nonzero unicast address'
c['PlatformInfo']['Generic'].update(SystemProductName=i['model'],SystemSerialNumber=i['serial'],
    MLB=i['mlb'],SystemUUID=str(u).upper(),ROM=rom)
c['PlatformInfo'].update(Automatic=True,UpdateDataHub=True,UpdateNVRAM=True,UpdateSMBIOS=True)
destination.write_bytes(plistlib.dumps(c,sort_keys=False))
i.update(uuid=str(u).upper(),rom=rom.hex()); identity.write_text(json.dumps(i),encoding='utf-8')
'@
    $identityEdit | & $python -X utf8 - $plist $candidate $IdentityFile
    if ($LASTEXITCODE -ne 0) { throw '識別情報の形式を確認してください。元の設定は保持されています。' }
    $edited = Get-Content -Raw -LiteralPath $IdentityFile | ConvertFrom-Json
    $models = (Invoke-ReimsTool (Join-Path $package 'Utilities/macserial/macserial.exe') @('-l')) -join "`n"
    if ($models -notmatch ('(?m)^\s*Model:\s*' + [regex]::Escape($edited.model) + '\s*$')) { throw 'OpenCore が認識する Mac モデルを指定してください。' }
    Invoke-ReimsTool $validate @($candidate) | Out-Null
    Move-Item -LiteralPath $candidate -Destination $plist -Force
    Copy-Item -LiteralPath $IdentityFile -Destination (Join-Path $VmRoot 'state/opencore-identity.json') -Force
    "識別情報を保存しました。変更前の設定: $backup"
}
if ($AppleAccountCompatibility -ne 'Keep') {
    $backup = Join-Path $ProjectRoot ('.work/diagnostics/opencore-before-account-' + (Get-Date -Format 'yyyyMMdd-HHmmss-ffff') + '.plist')
    Copy-Item -LiteralPath $plist -Destination $backup
    $candidate = "$backup.candidate"
    & $python -X utf8 "$PSScriptRoot/opencore/apple-account.py" $plist $candidate $AppleAccountCompatibility
    if ($LASTEXITCODE -ne 0) { throw 'Apple Account compatibility update failed; the original config is unchanged.' }
    Invoke-ReimsTool $validate @($candidate) | Out-Null
    Move-Item -LiteralPath $candidate -Destination $plist -Force
    "Apple Account compatibility: $AppleAccountCompatibility (Darwin 25.4.0 only). Backup: $backup"
}
$validation = Invoke-ReimsTool $validate @($plist)
$validation | Set-Content -LiteralPath (Join-Path $destination 'ocvalidate.log') -Encoding utf8NoBOM
$configHash = (Get-FileHash -LiteralPath $plist).Hash
if (!$Rebuild -and (Test-Path -LiteralPath $image) -and (Test-Path -LiteralPath $manifestPath)) {
    $previous = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($previous.image -eq $image -and ($previous.config_sha256 -ne $configHash -or
        $previous.image_sha256 -ne (Get-FileHash -LiteralPath $image).Hash)) {
        throw 'OpenCore config or image changed. Use -Rebuild to validate and rebuild its EFI image.'
    }
}
if ($Rebuild -or !(Test-Path -LiteralPath $image)) {
    Invoke-ReimsTool $QemuImg @('convert', '-f', 'vvfat', '-O', 'raw', ('fat:' + $efiRoot.Replace('\', '/')), "$image.partial")
    $stream = [IO.File]::OpenRead("$image.partial")
    try {
        [void]$stream.Seek(510, [IO.SeekOrigin]::Begin)
        if ($stream.ReadByte() -ne 0x55 -or $stream.ReadByte() -ne 0xaa) { throw 'vvfat did not produce an MBR.' }
        [void]$stream.Seek(450, [IO.SeekOrigin]::Begin)
        # Keep the FAT16 partition type; macOS cannot probe this filesystem tagged as 0xEF.
        if ($stream.ReadByte() -notin @(0x06, 0x0e)) { throw 'Expected vvfat FAT16 partition type.' }
    } finally { $stream.Dispose() }
    Move-Item -LiteralPath "$image.partial" -Destination $image -Force
}
if (!$FirmwareDirectory) {
    $candidates = if ($script:ReimsEngine) { @(Get-ReimsEnginePath 'firmware' '') } else { @((Join-Path $MsysRoot 'ucrt64/share/qemu'), (Join-Path $MsysRoot 'mingw64/share/qemu'), (Join-Path $ProjectRoot 'build/qemu/pc-bios')) }
    $FirmwareDirectory = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'edk2-x86_64-code.fd') } | Select-Object -First 1
}
if (!$FirmwareDirectory) { throw 'Pass -FirmwareDirectory from a native QEMU package with x86_64 OVMF code and variables.' }
$codeSource = Join-Path $FirmwareDirectory 'edk2-x86_64-code.fd'
$varsSource = Join-Path $FirmwareDirectory 'edk2-i386-vars.fd'
foreach ($source in @($codeSource, $varsSource)) {
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Incomplete OVMF pair: $source" }
}
$firmware = Join-Path $VmRoot 'firmware'
$code = Join-Path $firmware 'OVMF_CODE.fd'
$varsTemplate = Join-Path $firmware 'OVMF_VARS.template.fd'
foreach ($pair in @(@($codeSource, $code), @($varsSource, $varsTemplate))) {
    if (Test-Path -LiteralPath $pair[1]) {
        if ((Get-FileHash -LiteralPath $pair[0]).Hash -ne (Get-FileHash -LiteralPath $pair[1]).Hash) {
            throw "Existing firmware differs from this package: $($pair[1]). Keep a matched code/variables pair."
        }
    } else { Copy-Item -LiteralPath $pair[0] -Destination $pair[1] }
}
$manifest = [ordered]@{
    schema = 1; version = $Version; release_url = $url; release_sha256 = $expectedHash
    image = $image; image_sha256 = (Get-FileHash -LiteralPath $image).Hash
    config = $plist; config_sha256 = $configHash
    ocvalidate = $validate; firmware_code = $code; firmware_vars_template = $varsTemplate
    code_sha256 = (Get-FileHash -LiteralPath $code).Hash; vars_sha256 = (Get-FileHash -LiteralPath $varsTemplate).Hash
    firmware_source = $FirmwareDirectory; validated = $true
}
Write-ReimsJson $manifestPath $manifest
$manifest | ConvertTo-Json -Depth 5
