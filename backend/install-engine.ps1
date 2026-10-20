#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination, [string]$Archive, [string]$Sha256)
# GUI pipes use UTF-8 regardless of the Windows console code page.
$OutputEncoding=[Text.UTF8Encoding]::new($false)
[Console]::InputEncoding=$OutputEncoding
[Console]::OutputEncoding=$OutputEncoding
. "$PSScriptRoot/engine-package.ps1"
$pin=Get-ReimsEngineLock
$localArchive=[bool]$Archive
if ($Archive) {
    $Archive=[IO.Path]::GetFullPath($Archive)
    $Sha256=Get-ReimsArchiveDigest $Archive $Sha256
}
$Destination=[IO.Path]::GetFullPath($Destination).TrimEnd('\','/')
if (!$Destination -or $Destination -eq [IO.Path]::GetPathRoot($Destination).TrimEnd('\','/') -or (Test-Path -LiteralPath $Destination)) { throw 'Choose a new engine folder; existing files are never replaced.' }
$parent=Split-Path $Destination
for ($ancestor=$parent; $ancestor; $ancestor=Split-Path $ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Choose a destination without directory links.' }
}
[void][IO.Directory]::CreateDirectory($parent)
$work=Join-Path $parent ('.reims-install-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$archiveLock=$null
try {
    if (!$Archive) {
        Write-Output '配布済みエンジンを確認しています…'
        $asset=Resolve-ReimsReleaseAsset $pin
        $Archive=Join-Path $work $pin.asset
        Write-Output 'エンジンとネイティブ DLL をダウンロードしています…'
        try { Receive-ReimsReleaseAsset $asset $Archive }
        catch { throw "対応エンジンを取得・検証できません。未公開、ネットワーク障害、または ZIP の不一致です。$($_.Exception.Message)" }
    }
    # Keep the verified ZIP immutable until extraction finishes.
    $archiveLock=[IO.File]::Open($Archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    if ($localArchive -and (Get-FileHash -LiteralPath $Archive).Hash -ne $Sha256) { throw 'Engine archive SHA256 mismatch; download the ZIP and matching sidecar again.' }
    Write-Output 'ファイルとソースの固定版を検証しています…'
    $stage=Join-Path $work 'engine'
    Expand-ReimsPackage $Archive $stage
    $archiveLock.Dispose(); $archiveLock=$null
    [void](Read-ReimsEngine $stage -VerifyFiles)
    Write-Output 'QEMU・WHPX・Reims・ディスクツールの起動を確認しています…'
    Test-ReimsNativeEngine $stage
    if (Test-Path -LiteralPath $Destination) { throw 'Destination appeared during installation; nothing was replaced.' }
    # Windows can retain an executable's image lock briefly after a probe exits.
    for ($attempt=0; ; $attempt++) {
        try { [IO.Directory]::Move($stage,$Destination); break }
        catch [IO.IOException], [UnauthorizedAccessException] {
            if ($attempt -ge 9 -or (Test-Path -LiteralPath $Destination)) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Output "導入完了: $Destination"
} finally {
    if ($archiveLock) { $archiveLock.Dispose() }
    # Only this invocation's random sibling staging directory is removed.
    if ((Split-Path $work) -eq $parent -and (Split-Path $work -Leaf) -match '^\.reims-install-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
