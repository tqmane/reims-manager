#Requires -Version 7.2
$ErrorActionPreference='Stop'
$temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root=Join-Path $temporary ('reims-cursor-bundle-'+[guid]::NewGuid().ToString('N'))
try {
    foreach($case in @('valid','payload','manifest-path','boolean-schema','archive')){
        $directory=Join-Path $root $case
        $tools=Join-Path $directory 'tools';$payload=Join-Path $directory 'payload';$archives=Join-Path $directory 'archives'
        [void][IO.Directory]::CreateDirectory($tools);[void][IO.Directory]::CreateDirectory($payload);[void][IO.Directory]::CreateDirectory($archives)
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bundle-runtimes.ps1') -Destination $tools
        # An authored inert fixture is never executed or shipped.
        [IO.File]::WriteAllText((Join-Path $payload 'ReimsCursorAid'),'Reims cursor bundle fixture')
        $manifest=@{schema=1;executable='ReimsCursorAid';sha256=(Get-FileHash (Join-Path $payload 'ReimsCursorAid')).Hash.ToLowerInvariant();revision=('a'*40)}
        if($case -eq 'payload'){$manifest.sha256='0'*64}
        if($case -eq 'manifest-path'){$manifest.executable='../ReimsCursorAid'}
        if($case -eq 'boolean-schema'){$manifest.schema=$true}
        $manifest|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8NoBOM
        $archive=Join-Path $archives 'cursor-fixture.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($payload,$archive)
        $hash=(Get-FileHash $archive).Hash.ToLowerInvariant()
        if($case -eq 'archive'){$hash='0'*64}
        @{schema=1;runtimes=@(@{name='cursor-aid';file='cursor-fixture.zip';sha256=$hash})}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $tools 'runtimes.json') -Encoding utf8NoBOM
        $failed=$false
        try { & (Join-Path $tools 'bundle-runtimes.ps1') -OutputDirectory (Join-Path $directory 'output') -ArchiveDirectory $archives | Out-Null }
        catch { $failed=$true }
        if($failed -ne ($case -ne 'valid')){throw "Cursor bundle fixture failed: $case"}
    }
    'Cursor bundle fixtures passed (5 cases).'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(!$resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'reims-cursor-bundle-*'){throw 'Unexpected fixture cleanup target'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
