#Requires -Version 7.2
. "$PSScriptRoot/../engine-package.ps1"
$test=Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../..")) ('.work/engine-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $test | Out-Null
$script:checks=0
function Reject([scriptblock]$Action,[string]$Description) {
    $rejected=$false
    try { & $Action | Out-Null } catch { $rejected=$true }
    if (!$rejected) { throw "Accepted invalid input: $Description" }
    $script:checks++
}
try {
    foreach ($name in @('../outside','/root','C:/escape','a/../b','a\b','bin/a:stream','bin/CON.dll','bin/trailing.','bin//x')) {
        Reject { Join-ReimsPackagePath $test $name } $name
    }
    if ((Join-ReimsPackagePath $test '日本語 path/file.txt') -ne (Join-Path $test '日本語 path/file.txt')) { throw 'Unicode path changed.' }; $script:checks++
    $zip=Join-Path $test 'bad.zip'
    $stream=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try { [void]$stream.CreateEntry('../escape.txt') } finally { $stream.Dispose() }
    Reject { Expand-ReimsPackage $zip (Join-Path $test 'bad-output') } 'ZIP traversal'
    if (Test-Path (Join-Path $test 'bad-output')) { throw 'ZIP extraction began before validating all entries.' }
    $duplicate=Join-Path $test 'duplicate.zip'
    $stream=[IO.Compression.ZipFile]::Open($duplicate,[IO.Compression.ZipArchiveMode]::Create)
    try { [void]$stream.CreateEntry('FILE.txt'); [void]$stream.CreateEntry('file.txt') } finally { $stream.Dispose() }
    Reject { Expand-ReimsPackage $duplicate (Join-Path $test 'duplicate-output') } 'case-insensitive duplicate'
    $pin=Get-ReimsEngineLock
    # Fixture archive data is deliberately authored here, never taken from a guest.
    $fixture=Join-Path $test '日本語 engine with spaces'; New-Item -ItemType Directory -Path $fixture | Out-Null
    $paths=@{qemu='bin/qemu-system-x86_64.exe';qemu_img='bin/qemu-img.exe';qemu_io='bin/qemu-io.exe';seven_zip='bin/7z.exe';firmware='share/qemu';firmware_code='share/qemu/edk2-x86_64-code.fd';firmware_vars='share/qemu/edk2-i386-vars.fd';rom='share/reims/ReimsVgpu.rom';opencore_archive='downloads/OpenCore.zip'}
    $files=@{}
    foreach ($key in $paths.Keys | Where-Object { $_ -ne 'firmware' }) {
        $path=Join-ReimsPackagePath $fixture $paths[$key]
        New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
        [IO.File]::WriteAllText($path,"fixture $key")
        $files[$paths[$key]]=(Get-FileHash $path).Hash.ToLower()
    }
    $pin.opencore.sha256=$files[$paths.opencore_archive]
    function Get-ReimsEngineLock { $pin }
    $manifest=@{schema=1;id=$pin.id;sources=$pin.sources;paths=$paths;files=$files}
    function Save-Fixture { $manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $fixture 'reims-engine.json') -Encoding utf8NoBOM }
    Save-Fixture
    [void](Read-ReimsEngine $fixture -VerifyFiles); $script:checks++
    $manifest.sources.qemu.commit='0'*40; Save-Fixture
    # Separate the expected pin from the manifest's source hashtable.
    $expected=Get-Content -Raw "$PSScriptRoot/../engine-lock.json" | ConvertFrom-Json -AsHashtable
    $expected.opencore.sha256=$files[$paths.opencore_archive]
    function Get-ReimsEngineLock { $expected }
    Reject { Read-ReimsEngine $fixture -VerifyFiles } 'wrong source revision'
    $manifest.sources=$expected.sources; Save-Fixture
    $rom=Join-ReimsPackagePath $fixture $paths.rom
    Add-Content -LiteralPath $rom 'changed'
    Reject { Read-ReimsEngine $fixture -VerifyFiles } 'changed ROM'
    [IO.File]::WriteAllText($rom,'fixture rom')
    $missing=Join-ReimsPackagePath $fixture $paths.qemu_img
    Remove-Item -LiteralPath $missing
    Reject { Read-ReimsEngine $fixture } 'missing disk tool'
    [IO.File]::WriteAllText($missing,'fixture qemu_img')
    [IO.File]::WriteAllText((Join-Path $fixture 'unlisted.dll'),'extra')
    Reject { Read-ReimsEngine $fixture -VerifyFiles } 'unlisted DLL'
    Remove-Item -LiteralPath (Join-Path $fixture 'unlisted.dll')
    $env:REIMS_PROJECT_ROOT_OVERRIDE=$fixture
    $env:REIMS_VM_ROOT_OVERRIDE=Join-Path $test 'VM folder'
    $backend=Join-Path $test 'backend'; New-Item -ItemType Directory -Path $backend | Out-Null
    Get-ChildItem "$PSScriptRoot/.." -File | Copy-Item -Destination $backend
    $expected | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $backend 'engine-lock.json') -Encoding utf8NoBOM
    . "$backend/start.ps1" -FunctionsOnly
    if ((Get-ReimsTool 'qemu-img.exe') -ne $missing -or (Get-ReimsEnginePath 'rom' '') -ne $rom) { throw 'Package tool resolution bypassed manifest.' }; $script:checks++
    if ($env:PATH -match 'msys') { throw 'Portable environment inherited MSYS2.' }; $script:checks++
    $config=[pscustomobject]@{schema=1;cpus=4;memory_mib=4096;resolution='1920x1080';rom=$rom;firmware_code=(Join-ReimsPackagePath $fixture $paths.firmware_code);firmware_vars=(Join-Path $test 'vars.fd');opencore=(Join-Path $test 'OpenCore.img');active_disk=(Join-Path $test 'disk.qcow2');mac='52:54:00:12:34:56';qmp_port=4444;ssh_port=2222;cpu='Haswell-noTSX';iso='';media_kind='disk'}
    $arguments=Get-ReimsQemuArguments $config $test -WithoutInstaller
    if ($arguments[[array]::IndexOf($arguments,'-L')+1] -ne (Join-ReimsPackagePath $fixture $paths.firmware).Replace('\','/')) { throw 'QEMU -L did not use package firmware.' }; $script:checks++
    Reject { & "$PSScriptRoot/../install-engine.ps1" -Destination $fixture -Archive $zip } 'existing destination'
    if (!(Test-Path -LiteralPath $rom)) { throw 'Existing engine modified on failed install.' }
    "PASS: $script:checks engine package checks (no VM started)"
} finally {
    $root=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work")+[IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($test).StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $test -Recurse -Force }
}
