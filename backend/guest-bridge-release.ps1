# Loading the release contract has no VM or filesystem side effects.
. "$PSScriptRoot/release-asset.ps1"
function Get-ReimsQualifiedGuestBridgeLock {
    $pin=Get-Content -Raw -LiteralPath "$PSScriptRoot/guest-bridge-lock.json" | ConvertFrom-Json -AsHashtable
    if (!$pin.release_ready -or $pin.scope -ne 'generic-metal' -or $pin.runtime_validation -ne 'abi-checked' -or !@($pin.qualified_kernels).Count) {
        throw 'Guest bridge is still a candidate; setup cannot be completed with an unqualified addon.'
    }
    return $pin
}

function Get-ReimsGuestBridgeRelease($Pin) {
    Resolve-ReimsReleaseAsset $Pin -CheckAvailability
}
