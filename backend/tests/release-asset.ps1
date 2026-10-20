# No network/native/GPU/VM. Every API call fails, while fixed public assets work.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../engine-package.ps1"
$root=Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")) ('release-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
$fixtureState=@{apiCalls=0;missing=$false;changed=$false;payload=[Text.Encoding]::UTF8.GetBytes('authored download fixture')}
function Invoke-RestMethod { $fixtureState.apiCalls++; throw '403 rate limit fixture' }
function Invoke-WebRequest($Uri,$Method,$OutFile,$TimeoutSec) {
    if ($fixtureState.missing) { throw '404 unpublished fixture' }
    if ($Method -eq 'Head') { return @{StatusCode=200} }
    [IO.File]::WriteAllBytes($OutFile,$(if ($fixtureState.changed) {[byte[]]@(0,1,2)} else {$fixtureState.payload}))
}
function Reject([scriptblock]$Operation,[string]$Pattern) {
    $caught=$false
    try { & $Operation | Out-Null } catch { if ($_.Exception.Message -notmatch $Pattern) {throw};$caught=$true }
    if (!$caught) { throw "Expected rejection: $Pattern" }
}
try {
    $lock=Get-ReimsEngineLock
    if ($lock.bytes -ne 96767891) { throw 'Published engine size pin differs' }
    $pin=@{release_repository='tqmane/reims-manager';id='fixture-release-1';asset='Fixture.zip';bytes=$fixtureState.payload.Length}
    $asset=Resolve-ReimsReleaseAsset $pin -CheckAvailability
    if ($asset.url -ne 'https://github.com/tqmane/reims-manager/releases/download/fixture-release-1/Fixture.zip' -or $fixtureState.apiCalls) { throw 'Fixed asset resolution used API' }
    $download=Join-Path $root 'download.zip'
    Receive-ReimsReleaseAsset $asset $download
    if ((Get-Item $download).Length -ne $asset.bytes) { throw 'Direct download incomplete' }
    $fixtureState.changed=$true
    Reject { Receive-ReimsReleaseAsset $asset "$root/rejected.zip" } 'size mismatch'
    if (Test-Path "$root/rejected.zip") { throw 'Truncated archive was published' }
    $fixtureState.changed=$false; $fixtureState.missing=$true
    Reject { Resolve-ReimsReleaseAsset $pin -CheckAvailability } 'not available'
    $fixtureState.missing=$false
    $bad=$pin.Clone(); $bad.id='../wrong'
    Reject { Resolve-ReimsReleaseAsset $bad } 'Invalid immutable release pin'
    # Actual online installer path must reach extraction without API or ZIP SHA.
    $stub=Join-Path $root 'backend'; New-Item -ItemType Directory $stub | Out-Null
    Copy-Item "$PSScriptRoot/../install-engine.ps1" $stub
    $pin | ConvertTo-Json | Set-Content "$stub/fixture-pin.json" -Encoding utf8NoBOM
    $helpers=[IO.Path]::GetFullPath("$PSScriptRoot/../engine-package.ps1").Replace("'","''")
    $spies=@'
function Get-ReimsEngineLock { Get-Content "$PSScriptRoot/fixture-pin.json" -Raw | ConvertFrom-Json -AsHashtable }
function Expand-ReimsPackage { throw 'Fixture extraction reached after direct download' }
function Test-ReimsNativeEngine { throw 'Native execution must not occur' }
'@
    ". '$helpers'`n$spies" | Set-Content "$stub/engine-package.ps1" -Encoding utf8NoBOM
    Reject { & "$stub/install-engine.ps1" -Destination "$root/new-engine" } 'Fixture extraction reached after direct download'
    if ($fixtureState.apiCalls -or (Test-Path "$root/new-engine")) { throw 'Installer used API or published an incomplete destination' }
    'PASS: fixed direct installer survives API 403; size/unpublished/path errors; no API, token or new ZIP SHA requirement'
} finally {
    $allowed=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if ($root.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
