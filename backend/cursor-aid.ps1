# Fixed Manager code and a bundled authored executable; no caller-controlled paths or commands.
function Invoke-ReimsCursorAid([ValidateSet('enable','disable','move-main','move-extended')][string]$Operation) {
    $request = @{schema=1;operation=$Operation}
    if ($Operation -ne 'disable') {
        if (!$env:REIMS_MANAGER_TOOLS_ROOT) { throw 'Cursor aid is missing. Re-extract the complete Manager package.' }
        $directory = Join-Path $env:REIMS_MANAGER_TOOLS_ROOT 'cursor-aid'
        $manifestPath = Join-Path $directory 'manifest.json'
        $binaryPath = Join-Path $directory 'ReimsCursorAid'
        if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf) -or !(Test-Path -LiteralPath $binaryPath -PathType Leaf)) { throw 'The bundled cursor aid is missing. Re-extract the complete Manager package.' }
        if ((Get-Item -LiteralPath $manifestPath).Length -gt 4096) { throw 'Invalid cursor aid manifest size.' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        if ($manifest -isnot [Collections.IDictionary] -or ($manifest.schema -isnot [long] -and $manifest.schema -isnot [int])) { throw 'Invalid cursor aid schema type.' }
        if ($manifest.schema -ne 1 -or $manifest.executable -isnot [string] -or $manifest.executable -cne 'ReimsCursorAid' -or
            $manifest.sha256 -isnot [string] -or $manifest.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $manifest.revision -isnot [string] -or $manifest.revision -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid cursor aid manifest.' }
        $length = (Get-Item -LiteralPath $binaryPath).Length
        if ($length -lt 1 -or $length -gt 20MB) { throw 'Invalid cursor aid executable size.' }
        $bytes = [IO.File]::ReadAllBytes($binaryPath)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($hash -cne $manifest.sha256) { throw 'Bundled cursor aid does not match its manifest. Re-extract the complete Manager package.' }
        $request.manifest = $manifest
        $request.payload = [Convert]::ToBase64String($bytes)
    }
    $installer = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'guest-cursor-aid.py')))
    # Base64 uses no shell metacharacters. Request data travels only over stdin.
    $command = "/usr/bin/env python3 -c 'import base64;exec(compile(base64.b64decode(`"$installer`"),`"reims-cursor-aid`",`"exec`"))'"
    $json = Invoke-GuestControl -Command $command -InputJson ($request | ConvertTo-Json -Depth 4 -Compress)
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 4096) { throw 'Unexpected cursor aid result size.' }
    $result = $json | ConvertFrom-Json -AsHashtable
    if ($result -isnot [Collections.IDictionary] -or ($result.schema -isnot [long] -and $result.schema -isnot [int]) -or $result.schema -ne 1 -or $result.enabled -isnot [bool] -or $result.enabled -ne ($Operation -ne 'disable') -or
        ($result.enabled -and ($result.pid -isnot [long] -and $result.pid -isnot [int] -or $result.pid -le 0)) -or
        (!$result.enabled -and $null -ne $result.pid)) { throw 'Cursor aid did not confirm the requested state.' }
    if ($Operation -like 'move-*') {
        $target = if ($Operation -eq 'move-main') { 'main' } else { 'extended' }
        $move = $result.move
        if ($move -isnot [Collections.IDictionary] -or ($move.schema -isnot [long] -and $move.schema -isnot [int]) -or $move.schema -ne 1 -or
            $move.action -cne 'move-to-display' -or $move.target -cne $target -or $move.error -ne $null -or
            $move.ok -isnot [bool] -or !$move.ok -or ($move.cgError -isnot [long] -and $move.cgError -isnot [int]) -or $move.cgError -ne 0) { throw 'Cursor movement was not confirmed; cursor aid remains enabled.' }
        if ($target -eq 'main') { 'カーソルをメイン画面へ移動しました。' } else { 'カーソルを拡張画面へ移動しました。' }
    }
    elseif ($result.enabled) { 'カーソル補助を有効にしました。macOS への次回ログイン時も起動します。' }
    else { 'カーソル補助を無効にしました。' }
}
