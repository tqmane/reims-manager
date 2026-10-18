# Fixed public tag/asset downloads need neither the release API nor credentials.
function Resolve-ReimsReleaseAsset([Collections.IDictionary]$Pin, [switch]$CheckAvailability) {
    if ($Pin.release_repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        $Pin.id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or
        $Pin.asset -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$') { throw 'Invalid immutable release pin.' }
    $url="https://github.com/$($Pin.release_repository)/releases/download/$($Pin.id)/$($Pin.asset)"
    $bytes=0
    if ($Pin.Contains('bytes')) {
        $bytes=[long]$Pin.bytes
        if ($bytes -le 0) { throw 'Invalid pinned release size.' }
    }
    if ($CheckAvailability) {
        try { [void](Invoke-WebRequest $url -Method Head -TimeoutSec 30) }
        catch { throw "Pinned asset $($Pin.id)/$($Pin.asset) is not available. Check the network or wait for publication; setup is not ready." }
    }
    return @{url=$url;bytes=$bytes}
}

function Receive-ReimsReleaseAsset($Asset,[string]$Destination) {
    if (!(Test-Path -LiteralPath $Destination)) {
        $partial="$Destination.$([guid]::NewGuid().ToString('N')).partial"
        try {
            Invoke-WebRequest $Asset.url -OutFile $partial -TimeoutSec 1800
            if ($Asset.bytes -gt 0 -and (Get-Item -LiteralPath $partial).Length -ne $Asset.bytes) { throw 'Release download size mismatch.' }
            Move-Item -LiteralPath $partial -Destination $Destination
        } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    }
    if ($Asset.bytes -gt 0 -and (Get-Item -LiteralPath $Destination).Length -ne $Asset.bytes) { throw 'Cached release size mismatch.' }
}
