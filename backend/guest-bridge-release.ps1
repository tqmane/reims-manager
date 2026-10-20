# Loading the release contract has no VM or filesystem side effects.
. "$PSScriptRoot/release-asset.ps1"
function Test-ReimsGuestBridgeEngine($Pin,$Engine) {
    $engineId=$null
    if($Engine -is [Collections.IDictionary]){$engineId=$Engine['id']}
    elseif($Engine -and $Engine.PSObject.Properties['id']){$engineId=$Engine.id}
    if($Pin -isnot [Collections.IDictionary] -or $engineId -isnot [string]){return $false}
    $required=$Pin['required_engine_id']
    if($required -isnot [string] -or $required -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$'){return $false}
    # Manager-only compatibility does not alter the immutable addon manifest.
    # Older locks without this field continue to require the original engine.
    $ids=@($required)
    if($Pin.Contains('compatible_engine_ids')){
        $ids=$Pin['compatible_engine_ids']
        if($ids -isnot [Array] -or !$ids.Count){return $false}
    }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($id in $ids){
        if($id -isnot [string] -or $id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or !$seen.Add($id)){return $false}
    }
    if($ids -cnotcontains $required){return $false}
    return $ids -ccontains $engineId
}

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
