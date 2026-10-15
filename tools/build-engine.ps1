#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsysRoot)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. "$repo/backend/engine-package.ps1"
$pin=Get-ReimsEngineLock
$work=Join-Path $repo '.work/engine-build'
if (Test-Path -LiteralPath $work) { throw 'Use a fresh engine build directory.' }
New-Item -ItemType Directory -Path $work | Out-Null
$downloads=Join-Path $work 'downloads'; New-Item -ItemType Directory -Path $downloads | Out-Null
function Invoke-EngineGit([string[]]$Arguments) {
    & git.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Pinned source checkout failed.' }
}
function Checkout($Source, [string]$Destination) {
    Invoke-EngineGit -Arguments @('init',$Destination)
    Invoke-EngineGit -Arguments @('-C',$Destination,'config','core.autocrlf','false')
    Invoke-EngineGit -Arguments @('-C',$Destination,'remote','add','origin',$Source.repository)
    Invoke-EngineGit -Arguments @('-C',$Destination,'fetch','--depth=1','origin',$Source.commit)
    Invoke-EngineGit -Arguments @('-C',$Destination,'checkout','--detach','FETCH_HEAD')
    $actual=(& git -C $Destination rev-parse HEAD) -join ''
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Source.commit) { throw 'Source revision mismatch.' }
}
$source=Join-Path $work 'reims'
Checkout $pin.sources.reims $source
Checkout $pin.sources.qemu (Join-Path $source 'vendor/qemu')
$cargo=Get-Content -Raw (Join-Path $source 'Cargo.toml')
if (!$cargo.Contains($pin.sources.metal2vulkan.commit)) { throw 'Reims no longer pins the expected graphics runtime.' }
foreach ($item in @($pin.opencore, $pin.seven_zip)) { Get-ReimsPinnedDownload $item.url $item.sha256 (Join-Path $downloads $item.file) }
foreach ($name in $pin.firmware.files.Keys) {
    Get-ReimsPinnedDownload "https://raw.githubusercontent.com/qemu/qemu/$($pin.firmware.commit)/pc-bios/$name" $pin.firmware.files[$name] (Join-Path $downloads $name)
}
# Only the build runner needs 7-Zip. The downloaded installer is extracted, not installed.
$host7zip=Join-Path $env:ProgramFiles '7-Zip/7z.exe'
if (!(Test-Path -LiteralPath $host7zip)) { $host7zip=(Get-Command 7z.exe -ErrorAction Stop).Source }
$seven=Join-Path $work '7zip'
& $host7zip e '-y' "-o$seven" (Join-Path $downloads $pin.seven_zip.file) '7z.exe' '7z.dll' 'License.txt' 'readme.txt'
if ($LASTEXITCODE -ne 0) { throw '7-Zip extraction failed.' }
$rustup=(Get-Command rustup.exe -ErrorAction Stop).Source
$env:REIMS_PROJECT_ROOT_OVERRIDE=$source
$env:REIMS_MSYS2_ROOT=[IO.Path]::GetFullPath($MsysRoot)
. "$source/scripts/windows/environment.ps1"
& $rustup toolchain install $env:RUSTUP_TOOLCHAIN --profile minimal
if ($LASTEXITCODE -ne 0) { throw 'Rust toolchain installation failed.' }
Invoke-ReimsMsys 'pacman -Q > "$TEMP/../diagnostics/msys2-packages-current.txt"'
# The pinned xtask dist copies the committed C header; only header/check needs external cbindgen.
& "$source/scripts/windows/build.ps1" -Profile release
if ($LASTEXITCODE -ne 0) { throw 'Native engine build failed.' }
foreach ($tree in @(@($source,$pin.sources.reims.commit),@((Join-Path $source 'vendor/qemu'),$pin.sources.qemu.commit))) {
    $head=(& git -C $tree[0] rev-parse HEAD) -join ''
    if ($LASTEXITCODE -ne 0 -or $head -ne $tree[1]) { throw 'Source revision changed during the build.' }
    $changes=(& git -C $tree[0] status --porcelain --untracked-files=normal) -join ''
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Build source changed; refusing to publish mismatched binaries.' }
}
$package=Join-Path $work 'package'
$python=Join-Path $MsysRoot 'ucrt64/bin/python.exe'
& $python -X utf8 "$PSScriptRoot/package_engine.py" --source $source --msys $MsysRoot --downloads $downloads --sevenzip $seven --output $package
if ($LASTEXITCODE -ne 0) { throw 'Engine packaging failed.' }
[void](Read-ReimsEngine $package -VerifyFiles)
Test-ReimsNativeEngine $package
$artifacts=Join-Path $repo 'artifacts/engine'; New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$archive=Join-Path $artifacts $pin.asset
[IO.Compression.ZipFile]::CreateFromDirectory($package,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
(Get-FileHash -LiteralPath $archive).Hash.ToLower()+'  '+$pin.asset | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Copy-Item -LiteralPath "$package/reims-engine.json" -Destination $artifacts
Write-Output "Engine package ready: $archive"
