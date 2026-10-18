#Requires -Version 7.2
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../opencore-backup.ps1"
$parent=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")
$test=Join-Path $parent ('opencore-backup-'+[guid]::NewGuid().ToString('N'))
$vm=Join-Path $test '日本語 VM data'
New-Item -ItemType Directory -Force -Path $vm | Out-Null
try {
    $source=Join-Path $vm 'config.plist'
    [IO.File]::WriteAllText($source,'authored identity fixture')
    $original=(Get-FileHash $source).Hash
    $first=Backup-ReimsOpenCoreConfig $vm $source 'identity'
    if(![IO.Path]::GetFullPath($first).StartsWith([IO.Path]::GetFullPath((Join-Path $vm 'backups/opencore'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Backup left the selected VM data directory'}
    [IO.File]::WriteAllText($source,'next authored identity fixture')
    $second=Backup-ReimsOpenCoreConfig $vm $source 'identity'
    if($first -eq $second -or (Get-FileHash $first).Hash -ne $original){throw 'A later operation overwrote the prior backup'}
    Copy-Item -LiteralPath $first -Destination $source -Force
    if((Get-FileHash $source).Hash -ne $original){throw 'Backup could not restore the configuration'}
    $caught=$false
    try { Backup-ReimsOpenCoreConfig $vm $source '../escape' | Out-Null } catch { $caught=$true }
    if(!$caught){throw 'Invalid backup reason accepted'}
    'PASS: VM-owned Unicode backup path, unique history, restoration and traversal rejection'
} finally {
    if([IO.Path]::GetFullPath($test).StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $test -Recurse -Force}
}
