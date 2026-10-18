#Requires -Version 7.2
[CmdletBinding()]
param([string]$Installer,[string]$Adapter,[string]$CacheDirectory)
$ErrorActionPreference='Stop'
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'TAP ドライバーの導入には Windows の管理者権限が必要です。' }
if (!$CacheDirectory) { $CacheDirectory=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'ReimsManager/Drivers/tap-windows-9.27.0' }
. "$PSScriptRoot/engine-package.ps1"
. "$PSScriptRoot/tap-installer.ps1"
$mutex=[Threading.Mutex]::new($false,'Global\ReimsManager.TapInstall')
$held=$false
try {
    try { $held=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held=$true }
    if (!$held) { throw 'Another Reims TAP installation is in progress.' }
    Install-ReimsDedicatedTap $CacheDirectory $Installer $Adapter | ConvertTo-Json -Depth 5 -Compress
} finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
