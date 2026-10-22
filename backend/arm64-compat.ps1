# Reims ARM64 compatibility: guest-only application translation using pinned Maclator.
# Do not alter OpenCore, VM disks, system volumes or the GPU device.
function Assert-ReimsArm64Archive([string]$Archive,[ValidateSet('runtime','sysroot')][string]$Kind) {
    if (!(Test-Path -LiteralPath $Archive -PathType Leaf) -or [IO.Path]::GetExtension($Archive) -ne '.zip') {
        throw "GitHub Actions から取得した $Kind ZIP を選択してください。"
    }
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $total = [long]0
        if ($zip.Entries.Count -gt 128) { throw 'ZIP に予期しない数のエントリがあります。' }
        foreach ($entry in $zip.Entries) {
            $name = [string]$entry.FullName
            if (!$name -or $name.Contains('\') -or $name.StartsWith('/') -or $name.Contains(':') -or
                $name -match '(^|/)\.{1,2}(/|$)' -or $name -match '[\x00-\x1f]' -or !$seen.Add($name)) {
                throw "危険または重複した ZIP エントリ: $name"
            }
            # No symlinks: a symlink inside an otherwise safe zip can escape the install root.
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw "シンボリックリンクを含む ZIP は導入できません: $name"
            }
            if ($entry.Name.Length -eq 0) {
                if ($Kind -ne 'runtime' -or $name -ne 'gpu/') { throw "予期しないディレクトリ: $name" }
                continue
            }
            if ($Kind -eq 'runtime') {
                if ($name -cnotin @('maclator','gpu/libmclbridge.dylib','gpu/libmclmetal.dylib','SOURCE-COMMIT',
                    'LICENSE-MACLATOR','NOTICE-MACLATOR','SHA256SUMS')) {
                    throw "ランタイム ZIP に予期しないファイル: $name"
                }
            } elseif ($name -cnotin @('dyld','sysroot-manifest.json','SHA256SUMS') -and
                      $name -cnotmatch '^dyld_shared_cache_arm64e(?:\.[A-Za-z0-9_-]{1,40})?$') {
                throw "Apple sysroot ZIP に予期しないファイル: $name"
            }
            $total += [long]$entry.Length
            if ($total -gt 14GB) { throw '展開後の容量が上限を超えています。' }
        }
        $required = if ($Kind -eq 'runtime') {
            @('maclator','gpu/libmclbridge.dylib','gpu/libmclmetal.dylib','SOURCE-COMMIT','SHA256SUMS')
        } else {
            @('dyld','dyld_shared_cache_arm64e','sysroot-manifest.json','SHA256SUMS')
        }
        foreach ($name in $required) {
            if (!$seen.Contains($name)) { throw "ZIP に必要なファイルがありません: $name" }
        }
        if ($Kind -eq 'sysroot' -and @($seen | Where-Object { $_ -cmatch '^dyld_shared_cache_arm64e\.' }).Count -eq 0) {
            throw 'ARM64e の分割 dyld キャッシュがありません。'
        }
        return [pscustomobject]@{Count=$seen.Count;UncompressedSize=$total}
    } finally { $zip.Dispose() }
}

function Get-ReimsArm64ScpArguments {
    $paired = Join-Path $VmRoot 'state/guest.json'
    if (!(Test-Path -LiteralPath $paired)) { throw 'VM の SSH ホスト鍵を照合してペアリングしてください。' }
    $guest = Get-Content -LiteralPath $paired -Raw | ConvertFrom-Json
    $user = [string]$guest.user
    if ($user -cnotmatch '^[a-z_][a-z0-9_-]*$') { throw '保存された macOS アカウントが不正です。' }
    $state = Join-Path $VmRoot 'state'
    $key = Join-Path $state 'ssh/id_ed25519'
    $known = Join-Path $state 'known_hosts'
    if (!(Test-Path -LiteralPath $key) -or !(Test-Path -LiteralPath $known)) { throw 'SSH 鍵とホスト鍵がありません。' }
    $endpoint = Get-GuestEndpoint (Get-ReimsVmConfig)
    $hostName = [string]$endpoint.host
    if ($hostName -notmatch '^[A-Za-z0-9][A-Za-z0-9.:%-]*$') { throw '接続先が不正です。' }
    $scp = Join-Path $env:SystemRoot 'System32/OpenSSH/scp.exe'
    if (!(Test-Path -LiteralPath $scp)) { throw 'Windows OpenSSH の scp.exe がありません。' }
    $args = @('-F','NUL','-q','-O','-o','ConnectTimeout=5','-o','BatchMode=yes',
        '-o','IdentitiesOnly=yes','-o','StrictHostKeyChecking=yes',
        '-o',(Get-ReimsSshKnownHostsOption $known),'-o','KexAlgorithms=curve25519-sha256',
        '-P',[string]$endpoint.port,'-i',$key)
    if ($guest.PSObject.Properties['host_key_alias'] -and $guest.host_key_alias) {
        $args += @('-o', "HostKeyAlias=$($guest.host_key_alias)")
    }
    return @{Executable=$scp;Args=$args;Remote="$user@$hostName"}
}

function Copy-ReimsArm64Archive([hashtable]$Connection,[string]$Archive,[string]$RemoteName) {
    $target = $Connection.Remote + ':~/' + $RemoteName
    $response = @(& $Connection.Executable @($Connection.Args) $Archive $target 2>&1 | ForEach-Object ToString)
    if ($LASTEXITCODE -ne 0) {
        throw "macOS への転送に失敗しました ($LASTEXITCODE): $($response -join ' ')"
    }
}

function Invoke-ReimsArm64Compatibility([ValidateSet('status','install')][string]$Mode,
    [string]$RuntimeArchive = '',[string]$SysrootArchive = '') {
    # Verify QEMU process, state, SSH host key, and the VM selected by the Manager.
    if ($Mode -eq 'status') {
        $command = 'if test -x "$HOME/.local/share/reims-arm64/runtime/maclator" && test -f "$HOME/.local/share/reims-arm64/sysroot/dyld_shared_cache_arm64e" && test -x "$HOME/bin/reims-arm64"; then printf "installed: ~/bin/reims-arm64\n"; else printf "not installed\n"; fi'
        Invoke-GuestControl '' $command
        return
    }
    [void](Assert-ReimsArm64Archive $RuntimeArchive 'runtime')
    [void](Assert-ReimsArm64Archive $SysrootArchive 'sysroot')
    [void](Invoke-GuestControl '' 'test "$(uname -m)" = x86_64 && test -f /usr/lib/dyld')
    $transfer = 'reims-arm64-transfer-' + [guid]::NewGuid().ToString('N')
    $remote = '.' + $transfer
    $connection = Get-ReimsArm64ScpArguments
    try {
        [void](Invoke-GuestControl '' "umask 077; mkdir -m 700 -p ~/$remote")
        Copy-ReimsArm64Archive $connection $RuntimeArchive "$remote/runtime.zip"
        Copy-ReimsArm64Archive $connection $SysrootArchive "$remote/sysroot.zip"
        $install = Join-Path $PSScriptRoot 'arm64-guest-install.sh'
        if (!(Test-Path -LiteralPath $install)) { throw 'Reims ARM64 ゲストインストーラーが見つかりません。' }
        $script = Get-Content -LiteralPath $install -Raw -Encoding utf8
        Invoke-GuestControl '' ("sh -s -- " + $transfer) $script
    } finally {
        # Only remove the newly created UUID-named staging directory.
        try { [void](Invoke-GuestControl '' "rm -rf ~/$remote") } catch { }
    }
}
