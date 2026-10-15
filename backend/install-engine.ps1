#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination, [string]$Archive, [string]$Sha256)
. "$PSScriptRoot/engine-package.ps1"
$pin=Get-ReimsEngineLock
$Destination=[IO.Path]::GetFullPath($Destination).TrimEnd('\','/')
if (!$Destination -or $Destination -eq [IO.Path]::GetPathRoot($Destination).TrimEnd('\','/') -or (Test-Path -LiteralPath $Destination)) { throw 'Choose a new engine folder; existing files are never replaced.' }
$parent=Split-Path $Destination
for ($ancestor=$parent; $ancestor; $ancestor=Split-Path $ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Choose a destination without directory links.' }
}
[void][IO.Directory]::CreateDirectory($parent)
$work=Join-Path $parent ('.reims-install-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
try {
    if (!$Archive) {
        Write-Output '配布済みエンジンを確認しています…'
        $api="https://api.github.com/repos/$($pin.release_repository)/releases/tags/$($pin.id)"
        try { $release=Invoke-RestMethod $api -Headers @{'User-Agent'='ReimsManager'} -TimeoutSec 30 }
        catch { throw "対応エンジンのリリースを取得できません。ネットワークを確認するか、リポジトリの Windows engine package CI 成果物 ZIP と SHA256 を選択してください。$api" }
        if ($release.draft -or $release.prerelease -or $release.tag_name -ne $pin.id) { throw 'The matching engine has not been released.' }
        $assets=@($release.assets | Where-Object name -EQ $pin.asset)
        if ($assets.Count -ne 1 -or $assets[0].digest -notmatch '^sha256:([a-f0-9]{64})$') { throw 'The engine release has no unique SHA256-verified asset.' }
        $Sha256=$Matches[1]
        $expected="https://github.com/$($pin.release_repository)/releases/download/$($pin.id)/$($pin.asset)"
        if ($assets[0].browser_download_url -ne $expected) { throw 'Unexpected release asset URL.' }
        $Archive=Join-Path $work $pin.asset
        Write-Output 'エンジンとネイティブ DLL をダウンロードしています…'
        Get-ReimsPinnedDownload $expected $Sha256 $Archive
    }
    if (!$Sha256 -and (Test-Path -LiteralPath "$Archive.sha256")) { $Sha256=(Get-Content -Raw -LiteralPath "$Archive.sha256").Trim().Split(' ')[0] }
    if ($Sha256 -and ($Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $Archive).Hash -ne $Sha256)) { throw 'Engine archive is damaged; download it again.' }
    Write-Output 'ファイルとソースの固定版を検証しています…'
    $stage=Join-Path $work 'engine'
    Expand-ReimsPackage $Archive $stage
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
    # Only this invocation's random sibling staging directory is removed.
    if ((Split-Path $work) -eq $parent -and (Split-Path $work -Leaf) -match '^\.reims-install-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
