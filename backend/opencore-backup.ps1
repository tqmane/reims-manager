function Backup-ReimsOpenCoreConfig([string]$VmDirectory, [string]$Source,
    [ValidateSet('identity','boot-profile','edit','account','updates')][string]$Reason) {
    if (!(Test-Path -LiteralPath $Source -PathType Leaf)) { throw 'OpenCore configuration to back up was not found.' }
    $folder=Join-Path ([IO.Path]::GetFullPath($VmDirectory)) 'backups/opencore'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $backup=Join-Path $folder ("before-$Reason-"+[guid]::NewGuid().ToString('N')+'.plist')
    Copy-Item -LiteralPath $Source -Destination $backup
    return $backup
}
