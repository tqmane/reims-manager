# Importing this module defines functions only. Elevation belongs to its isolated worker.
function Get-ReimsIphoneUsbId([string]$Instance) {
    'hostiphone-'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Instance.ToUpperInvariant()))).ToLowerInvariant()
}

function Get-ReimsIphoneUsbAdvisoryStopPath([string]$Directory,[string]$Session) {
    if(![IO.Path]::IsPathFullyQualified($Directory) -or $Session -cnotmatch '^[0-9a-f]{32}$'){throw 'Invalid iPhone cancellation path.'}
    Join-Path ([IO.Path]::GetFullPath($Directory)) "state/iphone-$Session.stop"
}

function Test-ReimsIphoneUsbInstance([string]$Instance) {
    $Instance -imatch '^USB\\VID_05AC&PID_(12A8|12AB|128[0-3]|122[0-7])(?:&REV_[0-9A-F]{4})?\\[^\\/\x00-\x20*?]{1,200}$'
}

function Test-ReimsIphoneUsbLegacy($Config) {
    $Config.PSObject.Properties['usb_devices'] -and @($Config.usb_devices | Where-Object { $_.vendor -ieq '05ac' -and $_.product -imatch '^(12a8|12ab|128[0-3]|122[0-7])$' }).Count -gt 0
}

function Get-ReimsIphoneUsbProfile($Config,[string]$Id='') {
    if($Id){return Get-ReimsIphoneUsbProfiles $Config | Where-Object id -CEQ $Id}
    if(!$Config.PSObject.Properties['iphone_usb'] -or !$Config.iphone_usb) { return }
    $p=$Config.iphone_usb | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
    if($p -isnot [Collections.IDictionary]){throw 'iPhone の保存済み設定が無効です。'}
    foreach($key in @('id','ecid','initial_instance','session_id')){if($p[$key] -isnot [string]){throw 'iPhone の保存済み設定が無効です。'}}
    if(@($p.Keys | Where-Object {$_ -cnotin @('id','ecid','udid','initial_instance','enabled','session_id')}).Count -or
        !(Test-ReimsIphoneUsbInstance $p.initial_instance) -or $p.id -cne (Get-ReimsIphoneUsbId $p.initial_instance) -or
        $p.ecid -cnotmatch '^[0-9a-f]{16}$' -or $p.ecid -eq ('0'*16) -or $p.enabled -isnot [bool] -or
        $p.session_id -cnotmatch '^[0-9a-f]{32}$' -or ($p.Contains('udid') -and ($p.udid -isnot [string] -or $p.udid -cnotmatch '^[0-9A-Fa-f-]{16,64}$'))) { throw 'iPhone の保存済み設定が無効です。' }
    $p
}

function Get-ReimsIphoneUsbProfiles($Config) {
    $profiles=@(Get-ReimsIphoneUsbProfile $Config)
    if($Config.PSObject.Properties['iphone_usb_devices']) {
        if($Config.iphone_usb_devices -isnot [Array] -or $Config.iphone_usb_devices.Count -gt 64){throw 'Apple USB の登録一覧が無効です。'}
        foreach($value in $Config.iphone_usb_devices) {
            $profile=Get-ReimsIphoneUsbProfile ([pscustomobject]@{iphone_usb=$value})
            if(!$profile){throw 'Apple USB の登録一覧が無効です。'}
            $profiles+=,$profile
        }
    }
    if($profiles.Count -gt 64){throw 'Apple USB の登録数が上限を超えました。'}
    foreach($field in @('id','ecid','session_id')) {
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($profile in $profiles){if(!$seen.Add($profile[$field])){throw 'Apple USB の登録が重複しています。'}}
    }
    $profiles
}

function Set-ReimsIphoneUsbProfile($Config,$Profile) {
    $profiles=@(Get-ReimsIphoneUsbProfiles $Config)
    $primary=Get-ReimsIphoneUsbProfile $Config
    if(($primary -and $primary.id -ceq $Profile.id) -or !$profiles.Count) {
        $candidate=[pscustomobject]@{iphone_usb=[pscustomobject]$Profile;iphone_usb_devices=@($profiles | Where-Object id -CNE $Profile.id | ForEach-Object {[pscustomobject]$_})}
        [void](Get-ReimsIphoneUsbProfiles $candidate)
        $Config | Add-Member -Force iphone_usb ([pscustomobject]$Profile)
    } else {
        $additional=@($profiles | Where-Object {$_.id -cne $Profile.id -and (!$primary -or $_.id -cne $primary.id)} | ForEach-Object {[pscustomobject]$_}) + @([pscustomobject]$Profile)
        $candidate=[pscustomobject]@{iphone_usb=$(if($primary){[pscustomobject]$primary}else{$null});iphone_usb_devices=$additional}
        [void](Get-ReimsIphoneUsbProfiles $candidate)
        $Config | Add-Member -Force iphone_usb_devices $additional
    }
}

function Get-ReimsIphoneUsbCandidates {
    $rows=@(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { Test-ReimsIphoneUsbInstance $_.InstanceId })
    if($rows.Count -gt 64) { throw 'iPhone USB の候補数が上限を超えました。' }
    $rows
}

function Get-ReimsIphoneUsbRows($Config,$Blocked=$null) {
    if(Test-ReimsIphoneUsbLegacy $Config) { return }
    if(!(Get-Command Get-ReimsIphoneUsbStatus -ErrorAction SilentlyContinue)){. "$PSScriptRoot/iphone-usb-session.ps1"}
    $profiles=@(Get-ReimsIphoneUsbProfiles $Config)
    $statuses=@{};foreach($profile in $profiles){$statuses[$profile.id]=Get-ReimsIphoneUsbStatus $profile}
    if($null -eq $Blocked){$Blocked=Get-ReimsProtectedUsbIds}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $emitted=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($device in Get-ReimsIphoneUsbCandidates) {
        $product=([regex]::Match($device.InstanceId,'(?i)PID_([0-9a-f]{4})')).Groups[1].Value.ToLowerInvariant()
        if($blocked.Contains("05ac:$product")) { continue }
        $matching=@($profiles | Where-Object {$device.InstanceId -ieq $_.initial_instance -or
            ($statuses[$_.id] -and $device.InstanceId -ieq $statuses[$_.id].instance)})
        if($matching.Count -gt 1){throw 'Apple USB の登録と接続状態が重複しています。'}
        $profile=if($matching.Count){$matching[0]}else{$null}
        $owned=$profile -and $seen.Add($profile.id)
        $status=if($owned){$statuses[$profile.id]}else{$null}
        $id=if($owned){$profile.id}else{Get-ReimsIphoneUsbId $device.InstanceId}
        if(!$emitted.Add($id)){continue}
        $attached=[bool]($owned -and $status -and $status.state -ceq 'connected')
        $initialProduct=if($owned){([regex]::Match($profile.initial_instance,'(?i)PID_([0-9a-f]{4})')).Groups[1].Value.ToLowerInvariant()}else{$product}
        $kind=switch($initialProduct){'12ab'{'iPad'} '12a8'{'iPhone'} default{'iPhone/iPad'}}
        $label="$kind ($product)"
        if($owned -and $status){$label+=' ［'+$(switch($status.state){'connected'{'接続中'} 'replug-waiting'{'抜き差し待ち'} 'restored'{'復元済み'} 'restore-pending'{'元ドライバーの復元待ち'} 'qmp-wait-exit'{'VM 完全終了後に復元'} 'error'{'エラー・復元の確認が必要'} default{'接続・復元処理中'}})+'］'}
        $row=[pscustomobject]@{id=$id;label=$label;present=$true;selected=[bool]($owned -and $profile.enabled);
            active=[bool]($owned -and $status -and $status.active);attached=$attached;vendor='05ac';product=$product;bus=0;address=0;port=''}
        if($owned){$row|Add-Member error $(if($status -and $status.ContainsKey('error')){[string]$status.error}else{''})}
        $row
    }
    foreach($profile in $profiles) {
        if($seen.Contains($profile.id)){continue}
        $status=$statuses[$profile.id]
        $kind=if($profile.initial_instance -imatch 'PID_12AB'){'iPad'}else{'iPhone/iPad'}
        [pscustomobject]@{id=$profile.id;label=$kind+$(if($status -and $status.state -ceq 'error'){' ［エラー・復元の確認が必要］'}else{' ［再接続・復元待ち］'});present=$false;selected=$profile.enabled;
            error=$(if($status -and $status.ContainsKey('error')){[string]$status.error}else{''});
            active=[bool]($status -and $status.active);attached=$false;vendor='05ac';product=([regex]::Match($profile.initial_instance,'(?i)PID_([0-9a-f]{4})')).Groups[1].Value.ToLowerInvariant();bus=0;address=0;port=''}
    }
}

function Get-ReimsIphoneUsbContext($Runtime) {
    if(!$Runtime -or $Runtime.usb_backend -cne 'winusb' -or !(Get-ReimsVmProcess $Runtime)) { throw 'iPhone 転送には WinUSB 方式で VM を起動してください。USB 設定を変更し、VM を完全終了して起動し直してください。' }
    try{$properties=Invoke-ReimsQmp $Runtime 'device-list-properties' @{typename='usb-host'}}catch{throw 'このエンジンでは iPhone の個別転送を確認できません。エンジンを更新し、VM を完全終了して起動し直してください。'}
    if(!($properties | Where-Object name -CEQ 'host-instance-id')) { throw 'このエンジンは iPhone の個別転送に未対応です。エンジンを更新して VM を起動し直してください。' }
    $engine=Read-ReimsEngine $ProjectRoot -VerifyFiles
    if(!$engine -or $Runtime.executable -ine (Join-ReimsPackagePath $ProjectRoot $engine.paths.qemu)) { throw '実行中 VM と検証済みエンジンが一致しません。' }
    $dlls=@($engine.files.Keys | Where-Object {[IO.Path]::GetFileName($_) -ceq 'libusb-1.0.dll'})
    if($dlls.Count -ne 1) { throw '検証済みエンジンに iPhone 用 USB ライブラリーがありません。' }
    $package=Get-ReimsIphoneUsbDriverPackage (Join-Path $env:REIMS_MANAGER_TOOLS_ROOT 'iphone-usb-driver')
    $python=Assert-ReimsIphoneUsbOrdinaryPath (Join-Path $env:REIMS_MANAGER_TOOLS_ROOT 'python/python.exe')
    if(!(Test-Path -LiteralPath $python -PathType Leaf)){throw '同梱 Python がありません。Manager の完全なパッケージを再展開してください。'}
    @{native_library=(Join-ReimsPackagePath $ProjectRoot $dlls[0]);python=$python;package=$package}
}

function Get-ReimsIphoneUsbIdentity([string]$Instance,$Context) {
    $info=[Diagnostics.ProcessStartInfo]::new($Context.python)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($arg in @('-B','-X','utf8',"$PSScriptRoot/iphone-usb-identity.py")){$info.ArgumentList.Add($arg)}
    $child=[Diagnostics.Process]::Start($info)
    try {
        $output=$child.StandardOutput.ReadToEndAsync();$errorText=$child.StandardError.ReadToEndAsync()
        $child.StandardInput.WriteLine((@{instance_id=$Instance;native_library=$Context.native_library}|ConvertTo-Json -Compress));$child.StandardInput.Close()
        if(!$child.WaitForExit(30000)){$child.Kill();[void]$child.WaitForExit(5000);throw 'iPhone の本人確認がタイムアウトしました。'}
        $text=$output.GetAwaiter().GetResult();$failure=$errorText.GetAwaiter().GetResult()
        if($text.Length -gt 4096 -or $failure.Length -gt 8192 -or $child.ExitCode -ne 0){throw '元ドライバーで iPhone の ECID を確認できません。ロック解除・信頼設定、または復旧モード用ドライバーを確認してください。'}
        $value=$text|ConvertFrom-Json -AsHashtable
        if($value.schema -ne 1 -or $value.instance_id -ine $Instance -or $value.ecid -cnotmatch '^[0-9a-f]{16}$' -or $value.ecid -eq ('0'*16)){throw 'iPhone の本人確認結果が無効です。'}
        $value
    } finally {$child.Dispose()}
}

function Start-ReimsIphoneUsbElevation([string[]]$Arguments) {
    $args=@('-NoProfile','-File',"$PSScriptRoot/iphone-usb-session.ps1")+$Arguments
    Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -Verb RunAs -WindowStyle Hidden -ArgumentList (($args | ForEach-Object {ConvertTo-ReimsWindowsArgument $_}) -join ' ') -PassThru
}

function Start-ReimsIphoneUsbAutoConnect($Config,$Runtime,[string]$Id='') {
    if(!(Get-Command Get-ReimsIphoneUsbStatus -ErrorAction SilentlyContinue)){. "$PSScriptRoot/iphone-usb-session.ps1"}
    $profiles=@(Get-ReimsIphoneUsbProfiles $Config | Where-Object {$_.enabled -and (!$Id -or $_.id -ceq $Id)})
    if(!$profiles.Count){return}
    if(Test-ReimsIphoneUsbLegacy $Config){throw '旧 USB 一覧の iPhone 登録を解除してから、iPhone 専用接続を選んでください。'}
    $context=$null;$failures=[Collections.Generic.List[string]]::new()
    foreach($profile in $profiles) {
        try {
            $status=Get-ReimsIphoneUsbStatus $profile
            if($status -and $status.active -and $status.state -cin @('waiting','connected','replug-waiting') -and
                $status.qemu_pid -eq $Runtime.pid -and $status.qemu_start_ticks -eq $Runtime.start_ticks -and (Get-ReimsVmProcess $status)){continue}
            if($status -and $status.state -cne 'restored'){throw '前回の iPhone の復元が未完了です。先に「切断」で復元を再開してください。'}
            if(!$context){$context=Get-ReimsIphoneUsbContext $Runtime}
            $otherInstances=@(foreach($other in Get-ReimsIphoneUsbProfiles $Config) {
                if($other.id -ceq $profile.id){continue}
                $other.initial_instance
                $otherStatus=Get-ReimsIphoneUsbStatus $other
                if($otherStatus -and $otherStatus.instance){$otherStatus.instance}
            })
            $profile.session_id=[guid]::NewGuid().ToString('N')
            Set-ReimsIphoneUsbProfile $Config $profile
            Write-ReimsJson (Join-Path $VmRoot 'state/config.json') $Config
            $snapshot=@{schema=1;profile=$profile;runtime=$Runtime;engine_root=$ProjectRoot;vm_root=$VmRoot;tools_root=$env:REIMS_MANAGER_TOOLS_ROOT;
                caller_sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;python=$context.python;other_instances=@($otherInstances|Sort-Object -Unique)}
            $path=Join-Path $VmRoot "state/iphone-$($profile.session_id).json"
            Write-ReimsJson $path $snapshot
            $hash=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
            [void](Start-ReimsIphoneUsbElevation @('-InputPath',$path,'-InputHash',$hash,'-SessionId',$profile.session_id))
        } catch {$failures.Add($_.Exception.Message)}
    }
    if($failures.Count){throw ($failures -join "`n")}
}

function Set-ReimsIphoneUsbSelection([string]$Id,[bool]$Connect) {
    if(!(Get-Command Get-ReimsIphoneUsbStatus -ErrorAction SilentlyContinue)){. "$PSScriptRoot/iphone-usb-session.ps1"}
    $config=Get-ReimsVmConfig;$profile=Get-ReimsIphoneUsbProfile $config $Id
    if($Connect) {
        if(Test-ReimsIphoneUsbLegacy $config){throw '旧 USB 一覧の iPhone 登録を解除してから、iPhone 専用接続を選んでください。'}
        $runtime=Get-ReimsRuntime;$context=Get-ReimsIphoneUsbContext $runtime
        if(!$profile) {
            $matches=@(Get-ReimsIphoneUsbCandidates | Where-Object {(Get-ReimsIphoneUsbId $_.InstanceId) -ceq $Id})
            if($matches.Count -ne 1){throw '選択した iPhone が見つからないか、一意ではありません。'}
            $instance=$matches[0].InstanceId;$product=([regex]::Match($instance,'(?i)PID_([0-9a-f]{4})')).Groups[1].Value
            Assert-ReimsUsbSafe @(@{vendor='05ac';product=$product})
            $identity=Get-ReimsIphoneUsbIdentity $instance $context
            $profile=Get-ReimsIphoneUsbProfiles $config | Where-Object ecid -CEQ $identity.ecid
            if(!$profile){
                $profile=@{id=$Id;initial_instance=$instance;ecid=$identity.ecid;enabled=$true;session_id=[guid]::NewGuid().ToString('N')}
                if($identity.Contains('udid')){$profile.udid=$identity.udid}
            }
        }
        $profile.enabled=$true;Set-ReimsIphoneUsbProfile $config $profile
        Start-ReimsIphoneUsbAutoConnect $config $runtime $profile.id
        'iPhone の接続を登録しました。ドライバー切替の確認画面を許可してください。再接続待ちの場合は抜き差ししてください。'
    } else {
        if(!$profile -or $profile.id -cne $Id){throw 'この iPhone は登録されていません。'}
        # This advisory can only revoke this session, including a delayed UAC start.
        Write-ReimsJson (Get-ReimsIphoneUsbAdvisoryStopPath $VmRoot $profile.session_id) @{session_id=$profile.session_id;stop=$true}
        $status=Get-ReimsIphoneUsbStatus $profile
        $resident=$status -and $status.ContainsKey('pid') -and (Get-ReimsVmProcess $status)
        if(!$resident -and (Test-Path -LiteralPath (Get-ReimsIphoneUsbSessionRoot $profile.session_id))){
            [void](Start-ReimsIphoneUsbElevation @('-StopSession','-SessionId',$profile.session_id,'-CallerSid',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
        }
        $profile.enabled=$false;Set-ReimsIphoneUsbProfile $config $profile
        Write-ReimsJson (Join-Path $VmRoot 'state/config.json') $config
        'iPhone の自動接続を解除し、元ドライバーへの復元を要求しました。未接続のモードがある場合は再接続後に復元します。'
    }
}

function Get-ReimsIphoneUsbArgument($Profile,[string]$Instance) {
    if(!(Test-ReimsIphoneUsbInstance $Instance)){throw 'Invalid exact iPhone instance.'}
    $product=([regex]::Match($Instance,'(?i)PID_([0-9a-f]{4})')).Groups[1].Value
    @{driver='usb-host';id=$Profile.id;bus='xhci.0';'host-instance-id'=$Instance;vendorid=0x05ac;
        productid=[Convert]::ToInt32($product,16);'guest-reset'=$true;'guest-resets-all'=$false}
}
