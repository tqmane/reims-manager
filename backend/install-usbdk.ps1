#Requires -Version 7.2
[CmdletBinding()]
param([string]$CacheDirectory, [string]$ResultPath)
$ErrorActionPreference='Stop'
$resultStream=$null
try {
if ($PSBoundParameters.ContainsKey('ResultPath')) {
    # The unelevated caller owns this empty receipt. Keep its existing handle
    # open so another process cannot redirect or replace it while MSI runs.
    if ([string]::IsNullOrWhiteSpace($ResultPath) -or ![IO.Path]::IsPathFullyQualified($ResultPath) -or
        [IO.Path]::GetExtension($ResultPath) -ine '.json') {
        throw 'USB 導入結果の保存先には、既存の空の JSON ファイルを絶対パスで指定してください。'
    }
    $receipt=[IO.FileInfo]::new([IO.Path]::GetFullPath($ResultPath))
    if (!$receipt.Exists -or $receipt.Length -ne 0 -or
        ($receipt.Attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -or
        !$receipt.Directory.Exists -or ($receipt.Directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'USB 導入結果の保存先は、リンクではない既存の空の JSON ファイルである必要があります。'
    }
    $resultStream=[IO.File]::Open($receipt.FullName,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if ($resultStream.Length -ne 0) { throw 'USB 導入結果の保存先が空ではありません。導入は開始しません。' }
}
if ($CacheDirectory) {
    $ReimsWorkRoot=[IO.Path]::GetFullPath($CacheDirectory)
} else {
    . "$PSScriptRoot/start.ps1" -FunctionsOnly
    Assert-ReimsVmStopped
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'USB 転送ドライバーの導入には Windows の管理者権限が必要です。' }
New-Item -ItemType Directory -Force -Path (Join-Path $ReimsWorkRoot 'downloads'),(Join-Path $ReimsWorkRoot 'diagnostics') | Out-Null
$installer = Join-Path $ReimsWorkRoot 'downloads/UsbDk_1.0.22_x64.msi'
if (!(Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest 'https://github.com/daynix/UsbDk/releases/download/v1.00-22/UsbDk_1.0.22_x64.msi' -OutFile $installer
}
if ((Get-FileHash $installer).Hash -ne '91F6F695E1E13C656024E6D3B55620BF08D8835EF05EE0496935BA6BB62466A5') { throw 'UsbDk のダウンロードが検証済みファイルと一致しません。' }
$signature = Get-AuthenticodeSignature $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Red Hat, Inc\.') { throw 'UsbDk の署名を確認できません。' }
$log = Join-Path $ReimsWorkRoot 'diagnostics/usbdk-install.log'
$process = Start-Process -FilePath "$env:SystemRoot/System32/msiexec.exe" -ArgumentList "/i `"$installer`" /qn /norestart /L*v `"$log`"" -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -notin @(0,3010)) { throw "USB 転送ドライバーの導入に失敗しました ($($process.ExitCode))。ログ: $log" }
if ($resultStream) {
    $json=[ordered]@{schema=1;installed=$true;restart_required=($process.ExitCode -eq 3010);exit_code=[int]$process.ExitCode} | ConvertTo-Json -Compress
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes($json)
    $resultStream.Write($bytes,0,$bytes.Length)
    $resultStream.Flush($true)
}
if ($process.ExitCode -eq 3010) { '導入しました。USB 転送を使用する前に Windows を再起動してください。' }
else { 'USB 転送ドライバーを導入しました。VM の USB 転送方式で UsbDk を選んで保存し、VM を完全終了してから起動してください。' }
} finally { if ($resultStream) { $resultStream.Dispose() } }
