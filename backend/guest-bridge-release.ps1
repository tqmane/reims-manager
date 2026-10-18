# Loading the release contract has no VM or filesystem side effects.
function Get-ReimsQualifiedGuestBridgeLock {
    $pin=Get-Content -Raw -LiteralPath "$PSScriptRoot/guest-bridge-lock.json" | ConvertFrom-Json -AsHashtable
    if (!$pin.release_ready -or $pin.scope -ne 'generic-metal' -or $pin.runtime_validation -ne 'abi-checked' -or !@($pin.qualified_kernels).Count) {
        throw 'Guest bridge is still a candidate; setup cannot be completed with an unqualified addon.'
    }
    return $pin
}

function Get-ReimsGuestBridgeRelease($Pin) {
    $api="https://api.github.com/repos/$($Pin.release_repository)/releases/tags/$($Pin.id)"
    try { $release=Invoke-RestMethod $api -Headers @{'User-Agent'='ReimsManager'} -TimeoutSec 30 }
    catch { throw "Qualified guest addon is not available. Check the network or wait for release $($Pin.id); setup is not ready." }
    if ($release.draft -or $release.prerelease -or $release.tag_name -ne $Pin.id) { throw 'The qualified guest bridge release is not available.' }
    $assets=@($release.assets | Where-Object name -EQ $Pin.asset)
    if ($assets.Count -ne 1 -or $assets[0].digest -notmatch '^sha256:([a-f0-9]{64})$') { throw 'Guest bridge release has no unique SHA256 asset.' }
    $digest=$Matches[1]
    $url="https://github.com/$($Pin.release_repository)/releases/download/$($Pin.id)/$($Pin.asset)"
    if ($assets[0].browser_download_url -ne $url) { throw 'Unexpected guest bridge URL.' }
    return @{url=$url;sha256=$digest}
}
