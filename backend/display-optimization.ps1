function Invoke-ReimsDisplayOptimization([ValidateSet('lighten','restore')][string]$Operation) {
    $source = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'guest-display.py'))
    if ($source.Length -gt 20KB) { throw 'Display helper exceeds the bounded SSH command size.' }
    $encoded = [Convert]::ToBase64String($source)
    $command = "/usr/bin/env python3 -c 'import base64;exec(compile(base64.b64decode(`"$encoded`"),`"reims-display`",`"exec`"))'"
    $json = Invoke-GuestControl -Command $command -InputJson (@{schema=1;operation=$Operation}|ConvertTo-Json -Compress)
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 4096) { throw 'Unexpected display result size.' }
    $result = $json | ConvertFrom-Json -AsHashtable
    if ($result -isnot [Collections.IDictionary] -or ($result.schema -isnot [long] -and $result.schema -isnot [int]) -or $result.schema -ne 1 -or
        $result.operation -cne $Operation -or $result.applied -isnot [bool] -or $result.applied -ne ($Operation -eq 'lighten') -or
        $result.main_unchanged -isnot [bool] -or !$result.main_unchanged -or
        ($result.display_id -isnot [long] -and $result.display_id -isnot [int]) -or $result.display_id -le 0 -or $result.display_id -gt [uint32]::MaxValue) { throw 'Display optimization did not confirm the requested state.' }
    if ($Operation -eq 'lighten') { '拡張画面をセッション限定で軽量化しました。メイン画面は変更していません。' }
    else { '管理済みの拡張画面を元のモードへ戻しました。' }
}
