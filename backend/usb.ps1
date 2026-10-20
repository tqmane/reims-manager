# USB selection is stored with the VM; QEMU owns reconnect polling.
function Get-ReimsUsbSelections($Config) {
    if (!$Config.PSObject.Properties['usb_devices']) { return }
    $ids = [Collections.Generic.HashSet[string]]::new()
    foreach ($device in @($Config.usb_devices)) {
        if ($device.bus -lt 1 -or $device.bus -gt 65535 -or
            $device.port -cnotmatch '^[1-9][0-9]*(\.[1-9][0-9]*){0,6}$' -or
            $device.vendor -cnotmatch '^[0-9a-f]{4}$' -or $device.vendor -eq '0000' -or
            $device.product -cnotmatch '^[0-9a-f]{4}$' -or $device.product -eq '0000') {
            throw 'Invalid saved USB selection.'
        }
        $expected = "hostusb-$($device.bus)-$($device.port.Replace('.','_'))-$($device.vendor)$($device.product)"
        if ($device.id -cne $expected -or !$ids.Add($expected)) { throw 'Invalid or duplicate USB selection ID.' }
        $device
    }
}

function Get-ReimsUsbArgument($Device) {
    # shortcut: selection follows a physical port, reselect after moving ports.
    return @{driver='usb-host'; id=$Device.id; bus='xhci.0'; hostbus=[int]$Device.bus;
        hostport=[string]$Device.port; vendorid=[Convert]::ToInt32($Device.vendor,16);
        productid=[Convert]::ToInt32($Device.product,16); 'guest-reset'=$false}
}

function Assert-ReimsUsbBackendChange($Config, [string]$Backend) {
    $current=if ($Config.PSObject.Properties['usb_backend']) { [string]$Config.usb_backend } else { 'winusb' }
    if ($Backend -ne $current -and @(Get-ReimsUsbSelections $Config).Count) {
        # libusb's Windows backends do not share physical bus/port numbering.
        throw 'USB の転送方式を変更する前に、登録済み USB の自動接続を解除してください。変更を保存して VM を起動した後、新しい方式の一覧から選び直してください。'
    }
}

function Remove-ReimsUsbLaunchArguments([string[]]$Arguments) {
    for ($i=0; $i -lt $Arguments.Count; $i++) {
        if ($Arguments[$i] -eq '-device' -and $i+1 -lt $Arguments.Count -and $Arguments[$i+1].StartsWith('{')) {
            $device=$Arguments[$i+1] | ConvertFrom-Json
            if ($device.driver -eq 'usb-host') { $i++; continue }
        }
        $Arguments[$i]
    }
}

function Get-ReimsPnpProperties([string[]]$Instances, [string]$Key, [switch]$Optional) {
    # WMI rejects large OR queries, so batch within its quota.
    for ($offset=0; $offset -lt $Instances.Count; $offset+=24) {
        try {
            Get-PnpDeviceProperty -InstanceId $Instances[$offset..([math]::Min($offset+23,$Instances.Count-1))] -KeyName $Key -ErrorAction $(if ($Optional) {'SilentlyContinue'} else {'Stop'})
        } catch { if (!$Optional) { throw } }
    }
}

function Get-ReimsProtectedUsbIds {
    $blocked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $storageRoots = @()
    $protected = @(Get-PnpDevice -PresentOnly -Class Keyboard,Mouse,Bluetooth -ErrorAction SilentlyContinue | ForEach-Object InstanceId)
    foreach ($drive in @($ProjectRoot,$VmRoot,$env:SystemRoot) | ForEach-Object { [IO.Path]::GetPathRoot($_).TrimEnd('\',':') } | Select-Object -Unique) {
        if ($drive -match '^[A-Za-z]$') {
            foreach ($partition in Get-Partition -DriveLetter $drive -ErrorAction Stop) {
                $disks = @(Get-CimInstance Win32_DiskDrive -Filter "Index=$($partition.DiskNumber)" -ErrorAction Stop)
                if (!$disks.Count) { throw 'Cannot identify the host storage device; USB forwarding was not changed.' }
                $protected += $disks | ForEach-Object PNPDeviceID
                if ((Get-Disk -Number $partition.DiskNumber -ErrorAction Stop).BusType -eq 'USB') { $storageRoots += $disks | ForEach-Object PNPDeviceID }
            }
        }
    }
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $parents = @{}
    for ($depth=0; $protected.Count -and $depth -lt 32; $depth++) {
        $pending = @($protected | Where-Object { $_ -and $visited.Add($_) })
        foreach ($instance in $pending) {
            if ($instance -match 'VID_([0-9A-F]{4})&PID_([0-9A-F]{4})') { [void]$blocked.Add("$($Matches[1]):$($Matches[2])") }
        }
        $properties = @(Get-ReimsPnpProperties $pending DEVPKEY_Device_Parent | Where-Object { $_.PSObject.Properties['Data'] })
        foreach ($property in $properties) { $parents[$property.InstanceId]=$property.Data }
        $protected = @($properties | ForEach-Object Data)
    }
    foreach ($instance in $storageRoots) {
        $identified=$false
        for ($depth=0; $instance -and $depth -lt 32; $depth++) {
            if ($instance -match 'VID_([0-9A-F]{4})&PID_([0-9A-F]{4})') { $identified=$true; break }
            $instance=$parents[$instance]
        }
        if (!$identified) { throw 'USB 保存先の識別情報を取得できません。転送は開始しません。' }
    }
    return ,$blocked
}

function Assert-ReimsUsbSafe($Selections) {
    if (!@($Selections).Count) { return }
    $blocked = Get-ReimsProtectedUsbIds
    foreach ($device in $Selections) {
        if ($blocked.Contains("$($device.vendor):$($device.product)")) {
            throw 'Windows の入力機器または VM の保存先ディスクは転送できません。USB の登録を解除してください。'
        }
    }
}

function ConvertFrom-ReimsUsbInventory([string]$Text, $Names, $Blocked) {
    foreach ($match in [regex]::Matches($Text, '(?im)^\s*Bus (\d+), Addr (\d+), Port ([0-9.]+),[^\r\n]*\r?\n\s*Class [0-9a-f]+: USB device ([0-9a-f]{4}):([0-9a-f]{4})')) {
        $bus=[int]$match.Groups[1].Value; $port=$match.Groups[3].Value
        $vendor=$match.Groups[4].Value.ToLowerInvariant(); $product=$match.Groups[5].Value.ToLowerInvariant()
        $key="${vendor}:${product}"
        if ($Blocked.Contains($key)) { continue }
        [pscustomobject]@{id="hostusb-$bus-$($port.Replace('.','_'))-$vendor$product";
            bus=$bus; address=[int]$match.Groups[2].Value; port=$port; vendor=$vendor; product=$product;
            label="$key $($Names[$key]) — USB $bus / $port"; present=$true; selected=$false}
    }
}

function Get-ReimsUsbObjectStates($Runtime) {
    $states=@{}
    $objects=Invoke-ReimsQmp $Runtime qom-list @{path='/machine/peripheral'}
    foreach ($object in $objects) {
        if ($object.name -cnotmatch '^hostusb-[0-9]+-[0-9_]+-[0-9a-f]{8}$') { continue }
        try {
            # A realized usb-host object can be waiting for a device or have
            # exhausted its retries. Only USBDevice.attached confirms attachment.
            $attached=Invoke-ReimsQmp $Runtime qom-get @{path="/machine/peripheral/$($object.name)";property='attached'}
            if ($attached -isnot [bool]) { throw 'QEMU から USB の接続状態を取得できません。' }
            $states[$object.name]=[bool]$attached
        } catch {
            $failure=$_
            # Listing is read-only and can race a different Manager's removal.
            if (!(Get-ReimsVmProcess $Runtime)) { return @{} }
            $current=Invoke-ReimsQmp $Runtime qom-list @{path='/machine/peripheral'}
            if ($current | Where-Object name -EQ $object.name) { throw $failure }
        }
    }
    return $states
}

function Remove-ReimsUsbObject($Runtime, [string]$Id) {
    Invoke-ReimsQmp $Runtime device_del @{id=$Id} | Out-Null
    # device_del acknowledges a request; it does not guarantee that the object
    # has gone. Do not remove its saved registration or reuse its ID until then.
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        if (!(Get-ReimsVmProcess $Runtime)) { return }
        $objects=Invoke-ReimsQmp $Runtime qom-list @{path='/machine/peripheral'}
        if (!($objects | Where-Object name -EQ $Id)) { return }
        if ($attempt -lt 19) { Start-Sleep -Milliseconds 100 }
    }
    throw 'USB の切断完了を確認できません。登録は保持しています。一覧を更新してから再試行してください。'
}

function Wait-ReimsUsbAttachment($Runtime, [string]$Id) {
    # QEMU scans for reconnects every two seconds. Allow one scan, then report
    # pending honestly instead of turning a driver/open failure into success.
    for ($attempt=0; $attempt -lt 12; $attempt++) {
        if (!(Get-ReimsVmProcess $Runtime)) { return $false }
        $attached=Invoke-ReimsQmp $Runtime qom-get @{path="/machine/peripheral/$Id";property='attached'}
        if ($attached -isnot [bool]) { throw 'QEMU から USB の接続状態を取得できません。' }
        if ($attached) { return $true }
        if ($attempt -lt 11) { Start-Sleep -Milliseconds 250 }
    }
    return $false
}

function Get-ReimsStoppedUsbInventory($Config, [ValidateRange(100,15000)][int]$TimeoutMilliseconds=10000) {
    $backend='winusb'
    if($Config.PSObject.Properties['usb_backend']){$backend=$Config.usb_backend}
    if($backend -isnot [string] -or $backend -cnotin @('winusb','usbdk')){throw 'Invalid saved USB backend; select WinUSB or UsbDk in VM settings.'}
    $info=[Diagnostics.ProcessStartInfo]::new((Get-ReimsTool 'qemu-system-x86_64.exe'))
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.StandardOutputEncoding=[Text.UTF8Encoding]::new($false,$true)
    $info.StandardErrorEncoding=[Text.UTF8Encoding]::new($false,$true)
    $info.WorkingDirectory=Split-Path $info.FileName
    foreach($argument in @('-machine','none','-nodefaults','-display','none','-S','-monitor','none','-qmp','stdio')){$info.ArgumentList.Add($argument)}
    # Do not change this Manager process's backend or silently switch drivers.
    if($backend -eq 'usbdk'){$info.Environment['REIMS_USB_BACKEND']='usbdk'}else{[void]$info.Environment.Remove('REIMS_USB_BACKEND')}
    $clock=[Diagnostics.Stopwatch]::StartNew();$process=[Diagnostics.Process]::new();$process.StartInfo=$info
    $started=$false
    try{
        $started=$process.Start()
        if(!$started){throw 'Could not start the USB inventory probe.'}
        $io=@{pending='';total=0;errors='';outBuffer=[char[]]::new(4096);errBuffer=[char[]]::new(2048);outTask=$null;errTask=$null;errClosed=$false}
        # Both redirected streams are drained in bounded chunks. One deadline
        # includes greeting, replies and process exit, not a timeout per event.
        $readLine={
            while($true){
                $newline=$io.pending.IndexOf("`n")
                if($newline -gt 262144){throw 'USB inventory QMP line exceeds its size limit.'}
                if($newline -ge 0){$line=$io.pending.Substring(0,$newline);$io.pending=$io.pending.Substring($newline+1);return $line}
                if($io.pending.Length -gt 262144){throw 'USB inventory QMP line exceeds its size limit.'}
                $remaining=$TimeoutMilliseconds-[int]$clock.ElapsedMilliseconds
                if($remaining -le 0){throw 'USB inventory probe timed out.'}
                if(!$io.outTask){$io.outTask=$process.StandardOutput.ReadAsync($io.outBuffer,0,$io.outBuffer.Length)}
                if(!$io.errClosed -and !$io.errTask){$io.errTask=$process.StandardError.ReadAsync($io.errBuffer,0,$io.errBuffer.Length)}
                $tasks=[Collections.Generic.List[Threading.Tasks.Task]]::new();$tasks.Add($io.outTask)
                if($io.errTask){$tasks.Add($io.errTask)}
                $completed=[Threading.Tasks.Task]::WaitAny($tasks.ToArray(),$remaining)
                if($completed -lt 0){throw 'USB inventory probe timed out.'}
                if($completed -eq 1){
                    $count=$io.errTask.GetAwaiter().GetResult();$io.errTask=$null
                    if(!$count){$io.errClosed=$true}else{$io.errors+=[string]::new($io.errBuffer,0,$count)}
                    if($io.errors.Length -gt 16384){throw 'USB inventory probe stderr exceeds its size limit.'}
                    continue
                }
                $count=$io.outTask.GetAwaiter().GetResult();$io.outTask=$null
                if(!$count){throw "USB inventory probe closed QMP before completing. Required native DLLs or the selected USB backend may be unavailable. $($io.errors)"}
                $io.total+=$count
                if($io.total -gt 1048576){throw 'USB inventory QMP output exceeds its size limit.'}
                $io.pending+=[string]::new($io.outBuffer,0,$count)
            }
        }
        $greeting=(& $readLine)|ConvertFrom-Json -AsHashtable
        if($greeting -isnot [Collections.IDictionary] -or !$greeting.Contains('QMP')){throw 'USB inventory probe did not provide a QMP greeting.'}
        $text=$null
        foreach($request in @(@{execute='qmp_capabilities';id='usb-caps'},@{execute='human-monitor-command';arguments=@{'command-line'='info usbhost'};id='usb-inventory'})){
            $process.StandardInput.WriteLine(($request|ConvertTo-Json -Depth 4 -Compress));$process.StandardInput.Flush()
            do{
                $reply=(& $readLine)|ConvertFrom-Json -AsHashtable
                if($reply -isnot [Collections.IDictionary]){throw 'Invalid USB inventory QMP response.'}
            }while(!$reply.Contains('id') -or $reply.id -cne $request.id)
            if($reply.Contains('error')){throw "USB inventory QMP failed: $($reply.error|ConvertTo-Json -Compress)"}
            if(!$reply.Contains('return')){throw 'USB inventory QMP reply is missing its result.'}
            if($request.id -eq 'usb-inventory'){
                if($reply['return'] -isnot [string]){throw 'USB inventory QMP result is not text.'}
                $text=$reply['return']
            }
        }
        $process.StandardInput.WriteLine('{"execute":"quit","id":"usb-quit"}');$process.StandardInput.Flush()
        $remaining=[Math]::Max(0,$TimeoutMilliseconds-[int]$clock.ElapsedMilliseconds)
        if(!$process.WaitForExit($remaining)){throw 'USB inventory probe timed out during exit.'}
        if($process.ExitCode -ne 0){throw "USB inventory probe failed ($($process.ExitCode)); check native DLLs and the selected USB backend."}
        return $text
    }finally{
        try{
            if($started -and !$process.HasExited){
                try{$process.StandardInput.WriteLine('{"execute":"quit","id":"usb-cleanup"}');$process.StandardInput.Flush()}catch{}
                # This is solely the process we created, never a recorded/user VM.
                try{$process.Kill($true)}catch [InvalidOperationException]{if(!$process.HasExited){throw}}
                if(!$process.WaitForExit(2000)){throw 'Owned USB inventory probe did not exit after termination.'}
            }
        }finally{$process.Dispose()}
    }
}

function Get-ReimsUsbDevices {
    $config = Get-ReimsVmConfig
    $saved = @(Get-ReimsUsbSelections $config)
    $runtime = Get-ReimsRuntime
    $devices = @()
    $states=@{}
    $running=$runtime -and (Get-ReimsVmProcess $runtime)
    if ($running) {
        $text = Invoke-ReimsQmp $runtime human-monitor-command @{'command-line'='info usbhost'}
        $states=Get-ReimsUsbObjectStates $runtime
    } else {
        $text = Get-ReimsStoppedUsbInventory $config
    }
    if ($text -match 'not available|unknown command') { throw 'USB 転送に対応した QEMU へ更新してください。' }
    $names = @{}
    $pnp = @(Get-PnpDevice -PresentOnly | Where-Object InstanceId -Like 'USB\VID_*')
    $descriptions = @{}
    foreach ($property in Get-ReimsPnpProperties @($pnp | ForEach-Object InstanceId) DEVPKEY_Device_BusReportedDeviceDesc -Optional) {
        if ($property.PSObject.Properties['Data']) { $descriptions[$property.InstanceId]=$property.Data }
    }
    foreach ($device in $pnp) {
        if ($device.InstanceId -match 'VID_([0-9A-F]{4})&PID_([0-9A-F]{4})') {
            $key="$($Matches[1]):$($Matches[2])"
            $description=$descriptions[$device.InstanceId]
            $names[$key]=if ($description) { $description } else { $device.FriendlyName }
        }
    }
    $devices = @(ConvertFrom-ReimsUsbInventory $text $names (Get-ReimsProtectedUsbIds))
    foreach ($device in $saved) {
        $row = $devices | Where-Object id -EQ $device.id | Select-Object -First 1
        if ($row) { $row.selected=$true }
        else {
            $devices += [pscustomobject]@{id=$device.id;bus=$device.bus;address=0;port=$device.port;
                vendor=$device.vendor;product=$device.product;present=$false;selected=$true;
                label=$device.label}
        }
    }
    # A failed save plus failed rollback can leave a live, unsaved watcher.
    # Keep it visible and removable even if its physical device was unplugged.
    foreach ($id in $states.Keys) {
        if ($devices | Where-Object id -EQ $id) { continue }
        if ($id -cmatch '^hostusb-([1-9][0-9]*)-([1-9][0-9]*(?:_[1-9][0-9]*){0,6})-([0-9a-f]{4})([0-9a-f]{4})$') {
            $bus=[int]$Matches[1]; $port=$Matches[2].Replace('_','.'); $vendor=$Matches[3]; $product=$Matches[4]
            $devices += [pscustomobject]@{id=$id;bus=$bus;address=0;port=$port;vendor=$vendor;product=$product;
                present=$false;selected=$false;label="${vendor}:${product} — USB $bus / $port"}
        }
    }
    foreach ($device in $devices) {
        $active=$states.ContainsKey($device.id)
        $attached=$active -and $states[$device.id]
        $device | Add-Member -Force active ([bool]$active)
        $device | Add-Member -Force attached ([bool]$attached)
        if ($attached) { $device.label += $(if ($device.selected) {' ［接続中・自動接続］'} else {' ［接続中・未登録］'}) }
        elseif ($device.selected -or $active) {
            $device.label += $(if (!$running) {' ［登録済み・VM 起動待ち］'}
                elseif ($device.present) {' ［未接続・再接続できます］'}
                elseif ($active) {' ［自動接続・接続待ち］'}
                else {' ［登録済み・再接続が必要］'})
        }
    }
    return $devices
}

function Set-ReimsUsbSelection([string]$Id, [bool]$Connect) {
    $config=Get-ReimsVmConfig
    $saved=@(Get-ReimsUsbSelections $config)
    $existing=$saved | Where-Object id -EQ $Id | Select-Object -First 1
    $runtime=Get-ReimsRuntime
    $running=$runtime -and (Get-ReimsVmProcess $runtime)
    $states=if ($running) { Get-ReimsUsbObjectStates $runtime } else { @{} }
    $active=$states.ContainsKey($Id)
    if ($Connect) {
        if (!$running) { throw 'USB を接続するには VM を起動してください。' }
        if ($existing -and $active -and $states[$Id]) { 'この USB は VM に接続され、自動接続にも登録済みです。'; return }
        $device=Get-ReimsUsbDevices | Where-Object { $_.id -eq $Id -and ($_.present -or $_.attached) } | Select-Object -First 1
        if (!$device) { throw '選択した USB がありません。一覧を更新してください。' }
        Assert-ReimsUsbSafe @($device)
        $entry=if ($existing) { $existing } else {
            [pscustomobject]@{id=$device.id;bus=$device.bus;port=$device.port;vendor=$device.vendor;product=$device.product;
                label=($device.label -replace ' ［[^］]+］$','')}
        }
        $added=$false
        if (!$active -or !$states[$Id]) {
            # usb-host stops retrying after three open failures. Explicit Retry
            # replaces the disconnected watcher so fixing its driver can recover.
            if ($active) { Remove-ReimsUsbObject $runtime $Id }
            Invoke-ReimsQmp $runtime device_add (Get-ReimsUsbArgument $entry) | Out-Null
            $added=$true
        }
        if (!$existing) {
            $config | Add-Member -Force usb_devices @($saved + $entry)
            try { Write-ReimsJson (Join-Path $VmRoot 'state/config.json') $config }
            catch {
                $saveFailure=$_
                if ($added) {
                    try { Remove-ReimsUsbObject $runtime $Id }
                    catch { throw "自動接続の保存に失敗し、USB の接続解除も完了していません。一覧を更新して解除してください。保存: $($saveFailure.Exception.Message) / 解除: $($_.Exception.Message)" }
                }
                throw $saveFailure
            }
        }
        try { $attached=Wait-ReimsUsbAttachment $runtime $Id }
        catch { "自動接続の登録は保存済みですが、USB の接続状態を確認できません。一覧を更新してください: $($_.Exception.Message)"; return }
        if ($attached) { 'USB を VM に接続し、自動接続に登録しました。同じ USB ポートへの再接続と VM の次回起動にも適用します。' }
        else { '自動接続の登録は保存済みですが、ゲストへの接続は未完了です。転送方式とドライバーを確認し、一覧の「再接続」を試してください。' }
    } else {
        if (!$existing -and !$active) { throw 'この USB は登録されていません。' }
        if ($active) { Remove-ReimsUsbObject $runtime $Id }
        if ($existing) {
            $config | Add-Member -Force usb_devices @($saved | Where-Object id -NE $Id)
            try { Write-ReimsJson (Join-Path $VmRoot 'state/config.json') $config }
            catch {
                $saveFailure=$_
                if ($active -and (Get-ReimsVmProcess $runtime)) {
                    try { Invoke-ReimsQmp $runtime device_add (Get-ReimsUsbArgument $existing) | Out-Null }
                    catch { throw "USB は切断しましたが、登録解除を保存できず、自動接続も復元できません。登録は残っています。一覧を更新して再試行してください。保存: $($saveFailure.Exception.Message) / 復元: $($_.Exception.Message)" }
                    throw "USB の登録解除を保存できないため、自動接続を復元しました: $($saveFailure.Exception.Message)"
                }
                throw $saveFailure
            }
        }
        'USB の自動接続を解除しました。VM からの切断を確認しました。'
    }
}
