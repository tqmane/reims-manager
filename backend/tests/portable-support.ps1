#Requires -Version 7.2
$ErrorActionPreference='Stop'
$parent=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")
$test=Join-Path $parent ('portable-support-'+[guid]::NewGuid().ToString('N'))
$engine=Join-Path $test 'engine';$data=Join-Path $test '日本語 VM data'
New-Item -ItemType Directory -Force -Path $engine | Out-Null
[IO.File]::WriteAllText((Join-Path $engine 'reims-engine.json'),'{}')
$env:REIMS_PROJECT_ROOT_OVERRIDE=$engine
$env:REIMS_VM_ROOT_OVERRIDE=$data
$scriptPath=[IO.Path]::GetFullPath("$PSScriptRoot/../environment.ps1")
try {
    . $scriptPath
    foreach($name in @('TEMP','TMP','CARGO_HOME','RUSTUP_HOME','CARGO_TARGET_DIR','XDG_CACHE_HOME')){
        $value=[Environment]::GetEnvironmentVariable($name,'Process')
        if(!$value.StartsWith((Join-Path $data 'support')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Portable $name left VM support"}
    }
    [IO.File]::WriteAllText((Join-Path $env:TEMP 'fixture.txt'),'runtime scratch')
    if(@(Get-ChildItem -LiteralPath $engine -Recurse -File).Count -ne 1){throw 'Runtime wrote into engine package'}
    foreach($invalid in @('', $engine, (Join-Path $engine 'nested-vm'))){
        $env:REIMS_VM_ROOT_OVERRIDE=$invalid
        $caught=$false
        try {. $scriptPath} catch {if($_.Exception.Message -notmatch 'outside the immutable engine'){throw};$caught=$true}
        if(!$caught){throw 'Portable engine accepted an implicit or nested VM directory'}
    }
    'PASS: portable runtime support stays outside engine; implicit/nested VM paths rejected'
} finally {
    $allowed=$parent+[IO.Path]::DirectorySeparatorChar
    if([IO.Path]::GetFullPath($test).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $test -Recurse -Force}
}
