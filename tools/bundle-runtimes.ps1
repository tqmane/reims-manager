#Requires -Version 7.2
param([Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$ArchiveDirectory)
$ErrorActionPreference='Stop'
$lock=Get-Content -Raw (Join-Path $PSScriptRoot 'runtimes.json') | ConvertFrom-Json
if($lock.schema -ne 1){throw 'Unknown runtime manifest schema.'}
foreach($runtime in $lock.runtimes){
    if($runtime.name -notin @('pwsh','python') -or $runtime.file -ne [IO.Path]::GetFileName($runtime.file)){throw 'Invalid runtime entry.'}
    $archive=Join-Path $ArchiveDirectory $runtime.file
    if((Get-FileHash -LiteralPath $archive).Hash -ne $runtime.sha256){throw "Runtime archive mismatch: $($runtime.name)"}
}
foreach($runtime in $lock.runtimes){
    $destination=Join-Path $OutputDirectory "runtimes/$($runtime.name)"
    if(Test-Path -LiteralPath $destination){throw 'Use an output folder without an existing runtime bundle.'}
    Expand-Archive -LiteralPath (Join-Path $ArchiveDirectory $runtime.file) -DestinationPath $destination
    $executable=Join-Path $destination $(if($runtime.name -eq 'pwsh'){'pwsh.exe'}else{'python.exe'})
    $signature=Get-AuthenticodeSignature -LiteralPath $executable
    if($signature.Status -ne 'Valid'){throw "Invalid runtime signature: $($runtime.name)"}
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'runtimes.json') -Destination $OutputDirectory
'Bundled verified runtime archives.'
