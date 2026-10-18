#Requires -Version 7.2
param([Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$ArchiveDirectory)
$ErrorActionPreference='Stop'
$lock=Get-Content -Raw (Join-Path $PSScriptRoot 'runtimes.json') | ConvertFrom-Json
if($lock.schema -ne 1){throw 'Unknown runtime manifest schema.'}
foreach($runtime in $lock.runtimes){
    if($runtime.name -notin @('pwsh','python','cursor-aid') -or $runtime.file -ne [IO.Path]::GetFileName($runtime.file)){throw 'Invalid runtime entry.'}
    $archive=Join-Path $ArchiveDirectory $runtime.file
    if((Get-FileHash -LiteralPath $archive).Hash -ne $runtime.sha256){throw "Runtime archive mismatch: $($runtime.name)"}
}
foreach($runtime in $lock.runtimes){
    $destination=Join-Path $OutputDirectory "runtimes/$($runtime.name)"
    if(Test-Path -LiteralPath $destination){throw 'Use an output folder without an existing runtime bundle.'}
    Expand-Archive -LiteralPath (Join-Path $ArchiveDirectory $runtime.file) -DestinationPath $destination
    if($runtime.name -eq 'cursor-aid'){
        $manifestFile=Get-Item -LiteralPath (Join-Path $destination 'manifest.json')
        if($manifestFile.Length -gt 65536){throw 'Cursor helper manifest exceeds its size limit.'}
        $manifest=Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
        if(($manifest.schema -isnot [long] -and $manifest.schema -isnot [int]) -or $manifest.schema -ne 1 -or
           $manifest.executable -isnot [string] -or $manifest.executable -cne 'ReimsCursorAid' -or
           $manifest.sha256 -isnot [string] -or $manifest.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
           $manifest.revision -isnot [string] -or $manifest.revision -cnotmatch '^[a-f0-9]{40}$'){
            throw 'Invalid cursor helper manifest.'
        }
        if((Get-FileHash -LiteralPath (Join-Path $destination 'ReimsCursorAid')).Hash -ne $manifest.sha256){throw 'Cursor helper payload hash mismatch.'}
        # The authored macOS helper is pinned by archive and payload hashes, not Windows Authenticode.
        continue
    }
    $executable=Join-Path $destination $(if($runtime.name -eq 'pwsh'){'pwsh.exe'}else{'python.exe'})
    $signature=Get-AuthenticodeSignature -LiteralPath $executable
    if($signature.Status -ne 'Valid'){throw "Invalid runtime signature: $($runtime.name)"}
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'runtimes.json') -Destination $OutputDirectory
'Bundled verified runtime archives.'
