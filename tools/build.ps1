#Requires -Version 7.2
[CmdletBinding()]
param([string]$Dotnet='dotnet')
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceCommit=(& git -C $repoRoot rev-parse --verify HEAD) -join ''
if($LASTEXITCODE -ne 0){throw 'Commit the source before creating a release package.'}
$sourceChanges=(& git -C $repoRoot status --porcelain --untracked-files=all -- src backend tools .github README.md NOTICE.md LICENSES) -join ''
if($LASTEXITCODE -ne 0 -or $sourceChanges){throw 'Review and commit source changes before packaging.'}
$work=Join-Path $repoRoot '.work'
$output=Join-Path $work ('package-'+[guid]::NewGuid().ToString('N'))
$artifacts=Join-Path $repoRoot 'artifacts'
$checks=Join-Path $work ('checks-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $output,$checks,$artifacts,(Join-Path $work 'tmp') | Out-Null
$env:TEMP=Join-Path $work 'tmp';$env:TMP=$env:TEMP
$env:DOTNET_CLI_HOME=Join-Path $work 'dotnet-home'
$env:NUGET_PACKAGES=Join-Path $work 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:MSBUILDDISABLENODEREUSE='1'
& $Dotnet publish (Join-Path $repoRoot 'src/ReimsManager/ReimsManager.csproj') -c Release -r win-x64 --self-contained true -o $output -m:1 `
    "-p:BaseIntermediateOutputPath=$(Join-Path $work 'obj')/" "-p:BaseOutputPath=$(Join-Path $work 'bin')/" `
    '-p:UseSharedCompilation=false' "-p:PathMap=$repoRoot=/_/reims-manager"
if($LASTEXITCODE -ne 0){throw 'Manager build failed.'}
& (Join-Path $PSScriptRoot 'bundle-runtimes.ps1') -OutputDirectory $output -ArchiveDirectory (Join-Path $work 'downloads')
$env:REIMS_MANAGER_HOME=Join-Path $checks 'settings'
$test=Start-Process -FilePath (Join-Path $output 'ReimsManager.exe') -ArgumentList '--self-test',('"'+$checks+'"') -WindowStyle Hidden -PassThru -Wait
if($test.ExitCode -ne 0){throw "Manager checks failed; see $checks"}
foreach($file in Get-ChildItem (Join-Path $repoRoot 'backend') -Recurse -Filter '*.ps1'){
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "PowerShell syntax error: $($file.Name)"}
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'),(Join-Path $repoRoot 'NOTICE.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSES') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $checks 'result.json') -Destination (Join-Path $repoRoot 'artifacts/self-test.json') -Force
@{publish=$output;checks=$checks} | ConvertTo-Json | Set-Content (Join-Path $work 'last-build.json') -Encoding utf8NoBOM
$finalCommit=(& git -C $repoRoot rev-parse HEAD) -join ''
$sourceChanges=(& git -C $repoRoot status --porcelain --untracked-files=all -- src backend tools .github README.md NOTICE.md LICENSES) -join ''
if($LASTEXITCODE -ne 0 -or $finalCommit -ne $sourceCommit -or $sourceChanges){throw 'Source changed during the package build.'}
@{schema=1;revision=$sourceCommit;self_checks='passed';built_utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $output 'build-manifest.json') -Encoding utf8NoBOM
$zip=Join-Path $artifacts 'ReimsManager-win-x64.zip'
$partial=Join-Path $artifacts ([guid]::NewGuid().ToString('N')+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($output,$partial,[IO.Compression.CompressionLevel]::Optimal,$false)
Move-Item -LiteralPath $partial -Destination $zip -Force
(Get-FileHash -LiteralPath $zip).Hash.ToLower()+'  ReimsManager-win-x64.zip' | Set-Content -LiteralPath "$zip.sha256" -Encoding utf8NoBOM
"Built and checked: $zip"
