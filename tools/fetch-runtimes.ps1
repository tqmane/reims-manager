#Requires -Version 7.2
$ErrorActionPreference='Stop'
$cache=Join-Path (Split-Path $PSScriptRoot) '.work/downloads'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$lock=Get-Content -Raw (Join-Path $PSScriptRoot 'runtimes.json') | ConvertFrom-Json
if($lock.schema -ne 1){throw 'Unknown runtime manifest schema.'}
foreach($runtime in $lock.runtimes){
    $uri=[Uri]$runtime.url
    if($uri.Scheme -ne 'https' -or $uri.Host -notin @('github.com','www.python.org') -or
       $runtime.file -ne [IO.Path]::GetFileName($uri.AbsolutePath) -or $runtime.sha256 -notmatch '^[a-f0-9]{64}$'){
        throw 'Invalid runtime download entry.'
    }
    $archive=Join-Path $cache $runtime.file
    if(!(Test-Path -LiteralPath $archive)){
        $partial="$archive.partial"
        Invoke-WebRequest -Uri $uri.AbsoluteUri -OutFile $partial
        if((Get-FileHash -LiteralPath $partial).Hash -ne $runtime.sha256){throw "Download hash mismatch: $($runtime.name)"}
        Move-Item -LiteralPath $partial -Destination $archive
    }
    if((Get-FileHash -LiteralPath $archive).Hash -ne $runtime.sha256){throw "Cached archive hash mismatch: $($runtime.name)"}
    "Verified archive: $($runtime.file)"
}
