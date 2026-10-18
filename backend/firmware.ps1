# Select immutable code/template inputs; never operate on a VM's live NVRAM.
function Resolve-ReimsFirmwarePair($Recorded, [string]$FirmwareDirectory, [string[]]$DefaultDirectories) {
    if ($Recorded) {
        foreach ($pair in @(@('firmware_code','code_sha256'), @('firmware_vars_template','vars_sha256'))) {
            if (!$Recorded.PSObject.Properties[$pair[0]] -or !$Recorded.PSObject.Properties[$pair[1]]) {
                throw 'The recorded OVMF pair has incomplete metadata. Restore its code/template paths and hashes before rebuilding.'
            }
            $path=$Recorded.($pair[0]); $expected=$Recorded.($pair[1])
            if ($expected -notmatch '\A[a-fA-F0-9]{64}\z' -or !(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path).Hash -ne $expected) {
                throw 'The recorded OVMF pair is missing or changed. Restore its matching code/template and manifest; engine firmware cannot safely replace it during a configuration rebuild.'
            }
        }
        $source=if ($Recorded.PSObject.Properties['firmware_source'] -and $Recorded.firmware_source) { $Recorded.firmware_source } else { Split-Path $Recorded.firmware_code }
        $result=[pscustomobject]@{code=$Recorded.firmware_code;vars=$Recorded.firmware_vars_template;source=$source}
        if (!$FirmwareDirectory) { return $result }
    }
    if (!$FirmwareDirectory) {
        $FirmwareDirectory=$DefaultDirectories | Where-Object {
            (Test-Path -LiteralPath (Join-Path $_ 'edk2-x86_64-code.fd') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $_ 'edk2-i386-vars.fd') -PathType Leaf)
        } | Select-Object -First 1
    }
    if (!$FirmwareDirectory) { throw 'Select a firmware directory containing a matched x86_64 OVMF code/variables template pair.' }
    $code=Join-Path $FirmwareDirectory 'edk2-x86_64-code.fd'
    $vars=Join-Path $FirmwareDirectory 'edk2-i386-vars.fd'
    foreach ($path in @($code,$vars)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Incomplete OVMF pair: $path" }
    }
    if ($Recorded) {
        if ((Get-FileHash -LiteralPath $code).Hash -ne $Recorded.code_sha256 -or (Get-FileHash -LiteralPath $vars).Hash -ne $Recorded.vars_sha256) {
            throw 'The selected OVMF code/template differs from this VM''s recorded pair. An existing NVRAM requires a separately validated firmware migration; a config/kext rebuild will not replace either file.'
        }
        $result.source=$FirmwareDirectory
        return $result
    }
    return [pscustomobject]@{code=$code;vars=$vars;source=$FirmwareDirectory}
}
