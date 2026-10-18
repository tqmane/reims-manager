#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../firmware.ps1"
$parent=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")
$fixture=Join-Path $parent ('firmware-pair-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
$checks=0
function Reject([scriptblock]$Action,[string]$Pattern) {
    $caught=$false
    try { & $Action | Out-Null } catch { if($_.Exception.Message -notmatch $Pattern){throw}; $caught=$true }
    if(!$caught){throw 'An unsafe firmware change was accepted'}
    $script:checks++
}
try {
    $old=Join-Path $fixture 'old';$new=Join-Path $fixture 'new';$same=Join-Path $fixture 'same';$partial=Join-Path $fixture 'partial'
    New-Item -ItemType Directory -Path $old,$new,$same,$partial | Out-Null
    foreach($name in @('edk2-x86_64-code.fd','edk2-i386-vars.fd')){
        [IO.File]::WriteAllText((Join-Path $old $name),"old matched fixture $name")
        [IO.File]::WriteAllText((Join-Path $new $name),"new matched fixture $name")
        Copy-Item -LiteralPath (Join-Path $old $name) -Destination $same
    }
    Copy-Item -LiteralPath (Join-Path $new 'edk2-x86_64-code.fd') -Destination $partial
    $record=[pscustomobject]@{firmware_code=(Join-Path $old 'edk2-x86_64-code.fd');firmware_vars_template=(Join-Path $old 'edk2-i386-vars.fd');firmware_source='original-package'}
    $record | Add-Member code_sha256 (Get-FileHash $record.firmware_code).Hash
    $record | Add-Member vars_sha256 (Get-FileHash $record.firmware_vars_template).Hash
    $live=Join-Path $fixture 'live-nvram.fd';[IO.File]::WriteAllText($live,'existing live NVRAM')
    $before=(Get-FileHash $live).Hash
    $selected=Resolve-ReimsFirmwarePair $record '' @($new)
    if($selected.code -ne $record.firmware_code -or $selected.vars -ne $record.firmware_vars_template -or $selected.source -ne 'original-package'){throw 'Engine upgrade replaced recorded pair'}; $checks++
    $selected=Resolve-ReimsFirmwarePair $null '' @($partial,$new)
    if($selected.code -ne (Join-Path $new 'edk2-x86_64-code.fd')){throw 'New VM did not select a complete engine pair'}; $checks++
    Reject { Resolve-ReimsFirmwarePair $record $new @() } 'separately validated firmware migration'
    $selected=Resolve-ReimsFirmwarePair $record $same @()
    if($selected.code -ne $record.firmware_code -or $selected.vars -ne $record.firmware_vars_template){throw 'Identical explicit pair changed recorded paths'}; $checks++
    [IO.File]::WriteAllText((Join-Path $same 'edk2-i386-vars.fd'),'different template')
    Reject { Resolve-ReimsFirmwarePair $record $same @() } 'separately validated firmware migration'
    Reject { Resolve-ReimsFirmwarePair $null $partial @() } 'Incomplete OVMF pair'
    [IO.File]::WriteAllText($record.firmware_code,'changed recorded code')
    Reject { Resolve-ReimsFirmwarePair $record '' @($new) } 'recorded OVMF pair is missing or changed'
    $record.PSObject.Properties.Remove('code_sha256')
    Reject { Resolve-ReimsFirmwarePair $record '' @($new) } 'incomplete metadata'
    if((Get-FileHash $live).Hash -ne $before){throw 'Live NVRAM was changed'}; $checks++
    "PASS: $checks recorded/new OVMF pair checks; no VM boot"
} finally {
    $prefix=$parent.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if([IO.Path]::GetFullPath($fixture).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
