#Requires -Version 7.2
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('reims-iphone-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$script:checks=0
function Assert($Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Pattern){$message='';try{&$Action|Out-Null}catch{$message=$_.Exception.Message};Assert ($message -match $Pattern) "Expected $Pattern; actual: $message"}
. "$PSScriptRoot/../iphone-usb.ps1"
Assert (!$('ReimsUsbDriverBinding.Native' -as [type])) 'Import must not initialize native binding'
. "$PSScriptRoot/../iphone-usb-session.ps1"
. "$PSScriptRoot/../usb.ps1"
$realStatus=(Get-Command Get-ReimsIphoneUsbStatus).ScriptBlock
$realRootGuard=(Get-Command Assert-ReimsIphoneUsbSessionRoot).ScriptBlock
$realContext=(Get-Command Get-ReimsIphoneUsbContext).ScriptBlock
$realArrival=(Get-Command Get-ReimsIphoneUsbArrival).ScriptBlock
$normal='USB\VID_05AC&PID_12A8\AUTHORED-PHONE'
$recovery='USB\VID_05AC&PID_1281\AUTHORED-RECOVERY'
$profile=@{id=(Get-ReimsIphoneUsbId $normal);initial_instance=$normal;ecid='000000000000beef';enabled=$true;session_id=('a'*32)}
$runtime=@{pid=123;start_ticks=456;executable='D:\authored-engine\bin\qemu.exe';qmp_port=4455;usb_backend='winusb'}
$snapshot=@{schema=1;profile=$profile;runtime=$runtime;vm_root=$root;caller_sid='S-1-5-21-1-2-3-1001'}
$ProjectRoot=$root;$VmRoot=$root
$config=[pscustomobject]@{usb_devices=@();iphone_usb=[pscustomobject]$profile;usb_backend='winusb'}
$script:candidates=@([pscustomobject]@{InstanceId=$normal})
$script:status=$null;$script:elevations=@();$script:capability=$true;$script:alive=$true;$script:normalWrites=[Collections.Generic.List[string]]::new()
function Get-ReimsIphoneUsbCandidates {$script:candidates}
function Get-ReimsIphoneUsbStatus($Profile){$script:status}
function Get-ReimsProtectedUsbIds {,[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)}
function Get-ReimsVmProcess($Runtime){if($script:alive){[pscustomobject]@{Id=$Runtime.pid}}}
function Assert-ReimsUsbSafe($Selections){if($script:fixture){$script:fixture.safeChecks++;if($script:fixture.blocked){throw 'Fixture protected USB'}}}
function Get-ReimsIphoneUsbContext($Runtime){if($Runtime.usb_backend -cne 'winusb' -or !$script:capability){throw 'Fixture unsupported engine'};@{package=@{infPath=(Join-Path $root 'libusb0.inf')};python='unused';native_library='unused'}}
function Get-ReimsIphoneUsbIdentity($Instance,$Context){if($script:fixture){$script:fixture.events.Add('identity');if($script:fixture.scenario -ceq 'identity-gap' -and $script:fixture.tick -eq 0){$script:fixture.present[$Instance]=$false;throw 'Fixture device disappeared'};if($script:fixture.scenario -ceq 'cancel-identity'){Write-ReimsJson (Get-ReimsIphoneUsbAdvisoryStopPath $snapshot.vm_root $snapshot.profile.session_id) @{stop=$true}};return @{ecid=$script:fixture.ecid}};@{ecid=$profile.ecid;udid='00008120-000000000000BEEF'}}
function Start-ReimsIphoneUsbElevation($Arguments){
    if($Arguments[0] -ceq '-StopSession' -and !(Test-Path -LiteralPath (Get-ReimsIphoneUsbAdvisoryStopPath $VmRoot $Arguments[2]))){throw 'Fixture stop elevated before advisory cancellation'}
    $script:elevations+=,@($Arguments);[pscustomobject]@{Id=321}
}
function ConvertTo-ReimsWindowsArgument($Value){'"'+$Value+'"'}
function Write-ReimsJson($Path,$Value){$script:normalWrites.Add($Path);[void][IO.Directory]::CreateDirectory((Split-Path $Path));[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 16))}
function Get-ReimsVmConfig {$script:config}
function Get-ReimsRuntime {$runtime}
function Get-ReimsIphoneUsbSessionRoot($Session){Join-Path $root $Session}
$script:fixture=$null
try {
    $ipad='USB\VID_05AC&PID_12AB\AUTHORED-IPAD'
    $ipadProfile=@{id=(Get-ReimsIphoneUsbId $ipad);initial_instance=$ipad;ecid='000000000000cafe';enabled=$true;session_id=('b'*32)}
    Assert (Test-ReimsIphoneUsbInstance $ipad) 'iPad normal parent is routed to dedicated Apple transfer'
    Assert (!(Test-ReimsIphoneUsbInstance ($ipad.Replace('12AB','12AB&MI_01')))) 'iPad interface is not a physical parent'
    $multi=[pscustomobject]@{usb_devices=@();iphone_usb=[pscustomobject]$profile.Clone();iphone_usb_devices=@([pscustomobject]$ipadProfile)}
    Assert (@(Get-ReimsIphoneUsbProfiles $multi).Count -eq 2 -and (Get-ReimsIphoneUsbProfile $multi $ipadProfile.id).ecid -ceq $ipadProfile.ecid) 'Both Apple profiles are retained and selected by exact ID'
    foreach($field in @('id','ecid','session_id')) {
        $duplicate=$ipadProfile.Clone();$duplicate[$field]=$profile[$field]
        Reject {Get-ReimsIphoneUsbProfiles ([pscustomobject]@{iphone_usb=[pscustomobject]$profile;iphone_usb_devices=@([pscustomobject]$duplicate)})} '無効|重複'
    }
    Reject {Get-ReimsIphoneUsbProfiles ([pscustomobject]@{iphone_usb_devices='bad'})} '無効'
    & {
        Set-StrictMode -Version Latest
        $empty=[pscustomobject]@{}
        Assert (@(Get-ReimsIphoneUsbProfiles $empty).Count -eq 0) 'Missing Apple settings remain compatible in strict production mode'
        Set-ReimsIphoneUsbProfile $empty $profile.Clone()
        Set-ReimsIphoneUsbProfile $empty $ipadProfile.Clone()
        Assert (@(Get-ReimsIphoneUsbProfiles $empty).Count -eq 2) 'First and additional registrations are safe in strict production mode'
        $onlyAdditional=[pscustomobject]@{iphone_usb_devices=@([pscustomobject]$ipadProfile.Clone())}
        Set-ReimsIphoneUsbProfile $onlyAdditional $profile.Clone()
        Assert (@(Get-ReimsIphoneUsbProfiles $onlyAdditional).Count -eq 2) 'Collection-only configurations do not require an absent legacy field'
    }
    $excluded=$snapshot.Clone();$excluded.other_instances=@($ipad)
    Assert (@(Get-ReimsIphoneUsbOtherInstances $excluded)[0] -ceq $ipad) 'Another registered Apple instance is excluded before identity or binding'
    $excluded.other_instances=@($normal)
    Reject {Get-ReimsIphoneUsbOtherInstances $excluded} 'Invalid excluded'
    $excluded.other_instances=@('USB\VID_05AC&PID_12AB\*')
    Reject {Get-ReimsIphoneUsbOtherInstances $excluded} 'Invalid excluded'
    $oneStatus=(Get-Command Get-ReimsIphoneUsbStatus).ScriptBlock
    $oneIdentity=(Get-Command Get-ReimsIphoneUsbIdentity).ScriptBlock
    $oneConfig=$script:config
    try {
        $script:config=[pscustomobject]@{usb_devices=@();iphone_usb=[pscustomobject]$profile.Clone();usb_backend='winusb'}
        $script:candidates=@([pscustomobject]@{InstanceId=$normal},[pscustomobject]@{InstanceId=$ipad})
        $script:primaryBefore=$script:config.iphone_usb|ConvertTo-Json -Depth 8 -Compress
        $script:multiStatus=@{};$script:multiStatus[$profile.id]=@{state='connected';active=$true;instance=$normal;pid=321;qemu_pid=$runtime.pid;qemu_start_ticks=$runtime.start_ticks}
        $script:multiIdentityCalls=[Collections.Generic.List[string]]::new()
        function Get-ReimsIphoneUsbStatus($Profile){$script:multiStatus[$Profile.id]}
        function Get-ReimsIphoneUsbIdentity($Instance,$Context){$script:multiIdentityCalls.Add($Instance);if($Instance -ine $ipad){throw 'Existing phone must not be identified again'};@{ecid=$ipadProfile.ecid;udid='00008120-000000000000CAFE'}}
        Set-ReimsIphoneUsbSelection $ipadProfile.id $true|Out-Null
        $newProfile=Get-ReimsIphoneUsbProfile $script:config $ipadProfile.id
        Assert (($script:config.iphone_usb|ConvertTo-Json -Depth 8 -Compress) -ceq $script:primaryBefore -and $script:elevations.Count -eq 1) 'Adding iPad keeps the active primary profile byte-equivalent and elevates only the new worker'
        Assert ($newProfile.ecid -ceq $ipadProfile.ecid -and $newProfile.session_id -cne $profile.session_id -and $script:multiIdentityCalls.Count -eq 1) 'Second device has its own exact identity and session'
        $written=Get-Content -LiteralPath (Join-Path $VmRoot "state/iphone-$($newProfile.session_id).json") -Raw|ConvertFrom-Json
        Assert ($written.other_instances -contains $normal -and $written.profile.id -ceq $ipadProfile.id) 'New worker excludes the live first phone in its immutable snapshot'
        $script:multiStatus[$ipadProfile.id]=@{state='connected';active=$true;instance=$ipad;pid=322;qemu_pid=$runtime.pid;qemu_start_ticks=$runtime.start_ticks}
        $rows=@(Get-ReimsIphoneUsbRows $script:config)
        Assert ($rows.Count -eq 2 -and @($rows|Where-Object attached).Count -eq 2 -and @($rows|Where-Object label -Match '^iPad').Count -eq 1) 'Both connected rows retain separate status and the iPad label'
        $beforeElevations=$script:elevations.Count
        Start-ReimsIphoneUsbAutoConnect $script:config $runtime
        Assert ($script:elevations.Count -eq $beforeElevations) 'Starting healthy registered profiles does not restart either worker'
        Set-ReimsIphoneUsbSelection $ipadProfile.id $false|Out-Null
        Assert (($script:config.iphone_usb|ConvertTo-Json -Depth 8 -Compress) -ceq $script:primaryBefore -and !(Get-ReimsIphoneUsbProfile $script:config $ipadProfile.id).enabled) 'Disconnecting iPad leaves the first phone enabled and unchanged'
        Assert (!(Test-Path -LiteralPath (Get-ReimsIphoneUsbAdvisoryStopPath $VmRoot $profile.session_id)) -and (Test-Path -LiteralPath (Get-ReimsIphoneUsbAdvisoryStopPath $VmRoot $newProfile.session_id))) 'Only the selected device receives a cancellation marker'
    } finally {
        Set-Item Function:Get-ReimsIphoneUsbStatus $oneStatus
        Set-Item Function:Get-ReimsIphoneUsbIdentity $oneIdentity
        $script:config=$oneConfig;$script:status=$null;$script:candidates=@([pscustomobject]@{InstanceId=$normal});$script:elevations=@();$script:normalWrites.Clear()
    }
    Assert ((Get-ReimsIphoneUsbId $normal) -ceq (Get-ReimsIphoneUsbId $normal.ToLowerInvariant())) 'Initial PnP ID hashes canonically'
    Assert ((Get-ReimsIphoneUsbId $normal) -cmatch '^hostiphone-[0-9a-f]{64}$') 'Phone row has stable distinct prefix'
    $rows=@(Get-ReimsIphoneUsbRows $config)
    Assert ($rows.Count -eq 1 -and $rows[0].selected -and $rows[0].bus -eq 0 -and !$rows[0].attached) 'Phone rows retain existing typed USB shape'
    $script:status=@{state='connected';active=$true;instance=$recovery};$script:candidates+=@([pscustomobject]@{InstanceId=$recovery})
    $rows=@(Get-ReimsIphoneUsbRows $config)
    Assert (@($rows.id|Sort-Object -Unique).Count -eq $rows.Count) 'Multiple visible modes cannot create duplicate UI row IDs'
    $script:status=@{state='error';active=$true;instance=$normal;error='Authored protected driver failure'}
    $rows=@(Get-ReimsIphoneUsbRows $config)
    Assert ($rows[0].label -match 'エラー' -and $rows[0].error -ceq $script:status.error -and !$rows[1].PSObject.Properties['error']) 'Only the owned present row exposes its protected error'
    $script:candidates=@()
    $rows=@(Get-ReimsIphoneUsbRows $config)
    Assert ($rows.Count -eq 1 -and !$rows[0].present -and $rows[0].label -match 'エラー' -and $rows[0].error -ceq $script:status.error) 'Unplugged saved phone retains the same protected error'
    $script:status=$null;$script:candidates=@([pscustomobject]@{InstanceId=$normal})
    $config.usb_devices=@([pscustomobject]@{vendor='05ac';product='12a8'})
    Assert (@(Get-ReimsIphoneUsbRows $config).Count -eq 0) 'Legacy selected phone suppresses the second path'
    Reject {Set-ReimsIphoneUsbSelection $profile.id $true} '旧 USB'
    Assert (!$script:elevations.Count) 'Legacy conflict cannot request UAC'
    $config.usb_devices=@();$script:candidates=@()
    Assert (@(Get-ReimsIphoneUsbRows $config)[0].present -eq $false) 'Unplugged saved phone stays removable'
    $bad=$profile.Clone();$bad.id='hostiphone-bad'
    Reject {Get-ReimsIphoneUsbProfile ([pscustomobject]@{iphone_usb=$bad})} '無効'
    foreach($key in @('id','ecid','initial_instance','session_id')) {
        $bad=$profile.Clone();$bad[$key]=@($bad[$key])
        Reject {Get-ReimsIphoneUsbProfile ([pscustomobject]@{iphone_usb=$bad})} '無効'
    }
    $bad=$profile.Clone();$bad.udid=@('00008120-000000000000BEEF')
    Reject {Get-ReimsIphoneUsbProfile ([pscustomobject]@{iphone_usb=$bad})} '無効'
    $temporaryRoot=Get-ReimsIphoneUsbTemporaryRoot 'D:\authored-engine' $profile.session_id
    Assert ($temporaryRoot -ceq "D:\.cache\reims-iphone-usb-$($profile.session_id)") 'Elevated temporary/cache root follows the engine volume outside ProgramData and VM data'
    Reject {Get-ReimsIphoneUsbTemporaryRoot 'relative-engine' $profile.session_id} 'temporary session path'
    $args=Get-ReimsIphoneUsbArgument $profile $recovery
    Assert ($args['host-instance-id'] -ceq $recovery -and $args['guest-reset'] -eq $true -and $args['guest-resets-all'] -eq $false -and
        !$args.ContainsKey('hostbus') -and !$args.ContainsKey('hostport')) 'Exact iPhone QMP path forwards addressed resets and keeps initial proxy enumeration virtual'
    $generic=Get-ReimsUsbArgument ([pscustomobject]@{id='fixture';bus=1;port='1';vendor='05ac';product='12a8'})
    Assert ($generic['guest-reset'] -eq $false) 'Generic USB reset behavior stays unchanged'
    $script:capability=$false
    Reject {Start-ReimsIphoneUsbAutoConnect $config $runtime} 'unsupported'
    Assert (!$script:elevations.Count) 'Unsupported engine cannot elevate or mutate'
    $script:capability=$true
    $mockContext=(Get-Command Get-ReimsIphoneUsbContext).ScriptBlock
    Set-Item Function:Get-ReimsIphoneUsbContext $realContext
    $old=$runtime.Clone();$old.usb_backend='usbdk'
    Reject {Get-ReimsIphoneUsbContext $old} 'WinUSB'
    function Invoke-ReimsQmp { @([pscustomobject]@{name='guest-reset'}) }
    Reject {Get-ReimsIphoneUsbContext $runtime} '未対応'
    Set-Item Function:Get-ReimsIphoneUsbContext $mockContext
    Start-ReimsIphoneUsbAutoConnect $config $runtime
    Assert ($script:elevations.Count -eq 1 -and $config.iphone_usb.session_id -cne ('a'*32)) 'Normal process saves session GUID before elevation'
    $saved=Get-Content -Raw -LiteralPath (Join-Path $root 'state/config.json')|ConvertFrom-Json
    Assert ($saved.iphone_usb.session_id -ceq $config.iphone_usb.session_id) 'Persisted profile owns the launched session'
    $advisory=Get-ReimsIphoneUsbAdvisoryStopPath $VmRoot $config.iphone_usb.session_id
    Assert (!(Test-Path -LiteralPath (Get-ReimsIphoneUsbSessionRoot $config.iphone_usb.session_id))) 'Delayed start fixture has no protected root yet'
    $writeCount=$script:normalWrites.Count
    Set-ReimsIphoneUsbSelection $config.iphone_usb.id $false|Out-Null
    Assert (!$config.iphone_usb.enabled -and $script:elevations.Count -eq 1 -and (Test-Path -LiteralPath $advisory)) 'Detach before worker root creation cancels the delayed start without another UAC'
    Assert ($script:normalWrites[$writeCount] -ceq $advisory -and $script:normalWrites[$writeCount+1] -ceq (Join-Path $root 'state/config.json')) 'Advisory cancellation is committed before the disabled profile'
    [void][IO.Directory]::CreateDirectory((Get-ReimsIphoneUsbSessionRoot $config.iphone_usb.session_id))
    $script:status=@{state='connected';active=$true;pid=321;start_ticks=654;executable='D:\authored-worker.exe'}
    Set-ReimsIphoneUsbSelection $config.iphone_usb.id $false|Out-Null
    Assert ($script:elevations.Count -eq 1) 'Verified live resident observes the advisory without additional UAC'
    $script:status=$null
    Set-ReimsIphoneUsbSelection $config.iphone_usb.id $false|Out-Null
    Assert ($script:elevations.Count -eq 2 -and $script:elevations[1][0] -ceq '-StopSession') 'Existing dead protected session still requests elevated restoration resume'
    $config.iphone_usb.enabled=$true
    $script:status=@{state='restoring';active=$true;qemu_pid=$runtime.pid;qemu_start_ticks=$runtime.start_ticks;pid=321}
    Reject {Start-ReimsIphoneUsbAutoConnect $config $runtime} '復元が未完了'
    $script:status.state='connected'
    Start-ReimsIphoneUsbAutoConnect $config $runtime
    Assert ($script:elevations.Count -eq 2) 'Live connection is idempotent, but restoring sessions cannot be recaptured'
    $script:status=$null
    $file=Join-Path $root 'authored-snapshot.json'
    Write-ReimsIphoneUsbSessionJson $file @{schema=1;value='authored'}
    $hash=(Get-FileHash -LiteralPath $file).Hash
    Assert ((Read-ReimsIphoneUsbSessionJson $file -ExpectedHash $hash).value -ceq 'authored') 'Snapshot is hashed from the exact parsed bytes'
    Reject {Read-ReimsIphoneUsbSessionJson $file -ExpectedHash ('0'*64)} 'input changed'

    # Remaining cases run the production resident loop with fake driver/PnP/QMP calls.
    function Publish-ReimsIphoneUsbStatus($Snapshot,$Root,$State,$Instance='',$ErrorText=''){$script:fixture.states.Add($State);$script:fixture.lastError=$ErrorText}
    function Get-ReimsIphoneUsbArrival($Instance){
        $f=$script:fixture
        if($f.tick -eq 0 -and ($f.scenario -ceq 'arrival-gap' -or ($f.scenario -ceq 'arrival-after-bind' -and $f.events.Contains('bind')))){return 0L}
        $f.arrival
    }
    function Get-PnpDeviceProperty($InstanceId,$KeyName){
        $f=$script:fixture
        if($f.metadataDenied){throw [UnauthorizedAccessException]::new('Fixture metadata access denied')}
        if($f.metadataMissing -or ($f.scenario -ceq 'stack-gap' -and $f.tick -eq 0)){return [pscustomobject]@{Type='Empty'}}
        if($KeyName -ceq 'DEVPKEY_Device_LastArrivalDate'){return [pscustomobject]@{Data=[DateTime]::new($f.arrival,[DateTimeKind]::Utc)}}
        [pscustomobject]@{Data=@("\Driver\$($f.stacks[$InstanceId])")}
    }
    function Get-ReimsUsbDriverBindingOperations {
        @{
            Read={param($id)$m=$script:fixture.models[$id].Clone();$m.Present=$script:fixture.present[$id];if($script:fixture.scenario -ceq 'read-gap' -and $script:fixture.tick -eq 1){$m.Present=$false};$m}
            Stage={param($inf)$script:fixture.events.Add('stage');$inf}
            Install={param($id,$wanted,$expected)
                $capture=$wanted.Service -ceq 'libusb0';$script:fixture.events.Add($(if($capture){'bind'}else{'restore'}))
                if(!$capture){$script:fixture.restoreTick=$script:fixture.tick}
                if(!(Test-ReimsUsbDriverBindingModel $script:fixture.models[$id] $expected)){throw 'Fixture foreign state'}
                $script:fixture.models[$id]=$wanted.Clone()
                if(!$capture -or !$script:fixture.reboot){$script:fixture.stacks[$id]=$wanted.Service}
                if($capture -and $script:fixture.scenario -ceq 'cancel-after-bind'){Write-ReimsJson (Get-ReimsIphoneUsbAdvisoryStopPath $snapshot.vm_root $snapshot.profile.session_id) @{stop=$true}}
                [bool]($capture -and $script:fixture.reboot)
            }
        }
    }
    function Invoke-ReimsQmp($Runtime,$Command,$Arguments) {
        $f=$script:fixture;$f.events.Add($Command)
        if($f.qmpFail -and $f.objects.Count){throw 'Fixture QMP stalled'}
        switch($Command){
            'query-status' {[pscustomobject]@{status=$f.vmStatus}}
            'qom-list' {foreach($id in $f.objects.Keys){[pscustomobject]@{name=$id}}}
            'qom-get' {$f.objects[$profile.id]['host-instance-id']}
            'device_add' {$f.objects[$Arguments.id]=$Arguments;$f.added.Add($Arguments);$f.arrivals.Add([long](Read-ReimsIphoneUsbSessionJson (Join-Path $f.root 'private/leases.json')).qmp_arrival)}
            default {throw "Unexpected fixture QMP command $Command"}
        }
    }
    function Remove-ReimsUsbObject($Runtime,$Id){$script:fixture.events.Add('remove');$script:fixture.objects.Remove($Id)}
    function Wait-ReimsUsbAttachment($Runtime,$Id){if($script:fixture.qmpFail){throw 'Fixture QMP stalled'};$true}
    function Get-ReimsIphoneUsbCandidates {foreach($id in $script:fixture.candidates){if($script:fixture.present[$id]){[pscustomobject]@{InstanceId=$id}}}}
    function Get-ReimsVmProcess($Runtime){$script:fixture.tick -lt $script:fixture.exitTick}
    function Start-Sleep {
        param($Seconds,$Milliseconds)
        $f=$script:fixture;$f.tick++
        if($f.tick -gt 10){throw 'Fixture bounded stop'}
        if($f.scenario -eq 'normal' -and $f.tick -eq 1){Write-ReimsIphoneUsbSessionJson (Join-Path $f.root 'stop') @{stop=$true}}
        if($f.scenario -eq 'mode' -and $f.tick -eq 1){$f.present[$normal]=$false;$f.candidates=@($recovery)}
        if($f.scenario -eq 'same-id' -and $f.tick -eq 1){$f.arrival=22L}
        if($f.scenario -eq 'unplug-replug' -and $f.tick -eq 1){$f.present[$normal]=$false}
        if($f.scenario -eq 'unplug-replug' -and $f.tick -eq 2){$f.present[$normal]=$true;$f.arrival=33L}
        if($f.scenario -eq 'shutdown' -and $f.tick -eq 1){$f.vmStatus='shutdown'}
        if($f.scenario -eq 'paused' -and $f.tick -eq 1){$f.vmStatus='paused'}
        if($f.scenario -eq 'guard-once' -and $f.tick -eq 1){$f.blocked=$true}
        if($f.scenario -in @('replug','qmp') -and $f.tick -eq 1){$f.arrival=[DateTime]::UtcNow.AddSeconds(1).Ticks;$f.stacks[$normal]='libusb0'}
        if($f.scenario -eq 'qmp' -and $f.objects.Count){$f.qmpFail=$true}
        if($f.scenario -eq 'foreign-qmp' -and $f.tick -eq 1){$f.objects[$profile.id]['host-instance-id']='USB\VID_05AC&PID_12A8\FOREIGN';Write-ReimsIphoneUsbSessionJson (Join-Path $f.root 'stop') @{stop=$true}}
        if($f.scenario -eq 'mode' -and $f.tick -eq 4){$f.present[$normal]=$true}
        if($f.scenario -eq 'absent' -and $f.tick -eq 1){$f.present[$normal]=$true}
        if($f.scenario -eq 'identity-gap' -and $f.tick -eq 1){$f.present[$normal]=$true}
    }
    function New-Fixture($Name) {
        $folder=Join-Path $root $Name;[void][IO.Directory]::CreateDirectory((Join-Path $folder 'private/receipts'))
        $snapshot.vm_root=Join-Path $root "$Name-caller-vm";[void][IO.Directory]::CreateDirectory($snapshot.vm_root)
        $models=@{};foreach($id in @($normal,$recovery)){$models[$id]=@{Present=$true;Problem=0;InfPath=(Join-Path $root 'authored-original.inf');InfHash=('a'*64);
            Section='AuthoredInstall';SectionExtension='.NT';Provider='Authored Driver';Version='1.2.3.4';ClassGuid='{36FC9E60-C465-11CF-8056-444553540000}';Service='usbccgp';MatchingId='USB\COMPOSITE'}}
        $script:fixture=@{root=$folder;tick=0;exitTick=2;models=$models;present=@{$normal=$true;$recovery=$true};stacks=@{$normal='usbccgp';$recovery='usbccgp'};
            events=[Collections.Generic.List[string]]::new();states=[Collections.Generic.List[string]]::new();objects=@{};added=[Collections.Generic.List[object]]::new();
            arrivals=[Collections.Generic.List[long]]::new();candidates=@($normal);arrival=11L;reboot=$false;qmpFail=$false;blocked=$false;safeChecks=0;scenario=$Name;ecid=$profile.ecid;lastError='';restoreTick=-1;vmStatus='running';metadataMissing=$false;metadataDenied=$false}
        $folder
    }
    $folder=New-Fixture 'metadata';$fixture.metadataMissing=$true
    Assert ((&$realArrival $normal) -eq 0 -and !(Test-ReimsIphoneUsbLiveModel $normal $fixture.models[$normal])) 'Missing PnP Data is unconfirmed, never an arrival or service-stack success'
    $fixture.metadataMissing=$false
    Assert ((&$realArrival $normal) -eq 11 -and (Test-ReimsIphoneUsbLiveModel $normal $fixture.models[$normal])) 'Actual metadata helpers accept the subsequent complete typed values'
    $fixture.metadataDenied=$true
    Reject {&$realArrival $normal} 'access denied'
    Reject {Test-ReimsIphoneUsbLiveModel $normal $fixture.models[$normal]} 'access denied'
    foreach($case in @('arrival-gap','arrival-after-bind','stack-gap')){
        $folder=New-Fixture $case;$fixture.exitTick=3
        Invoke-ReimsIphoneUsbSession $snapshot $folder
        Assert (!$fixture.lastError -and $fixture.added.Count -eq 1 -and @($fixture.events|Where-Object {$_ -ceq 'bind'}).Count -eq 1 -and $fixture.states.Contains('connected')) 'A metadata gap retries and reconnects without ending the session or rebinding'
        Assert ($fixture.states[0] -cin @('waiting','replug-waiting') -and $fixture.restoreTick -eq 3) 'No QMP attachment or restoration occurs while metadata is unconfirmed'
    }
    foreach($case in @('read-gap','identity-gap')){
        $folder=New-Fixture $case;$fixture.exitTick=4
        Invoke-ReimsIphoneUsbSession $snapshot $folder
        Assert (!$fixture.lastError -and $fixture.states.Contains('waiting') -and $fixture.states.Contains('connected') -and $fixture.restoreTick -eq 4) 'Disappearance after enumeration or during identity waits instead of becoming a foreign driver failure'
        Assert (@($fixture.events|Where-Object {$_ -ceq 'bind'}).Count -eq 1) 'A disappearing candidate cannot bind again or lose its existing receipt'
    }
    $folder=New-Fixture 'acl';$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $script:fixtureAcl=[Security.AccessControl.DirectorySecurity]::new();$fixtureAcl.SetAccessRuleProtection($true,$false)
    $fixtureAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($identity in @('S-1-5-32-544','S-1-5-18')){$fixtureAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($identity),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    $fixtureAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    function Get-Acl($LiteralPath){$script:fixtureAcl}
    &$realRootGuard $folder $sid
    Assert $true 'Protected root accepts Admin/System owners with caller read-only access'
    $fixtureAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'Write','Allow'))
    Reject {&$realRootGuard $folder $sid} 'permissions'
    $fixtureAcl.RemoveAccessRuleAll([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'Write','Allow'))
    $fixtureAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    $statusRoot=Get-ReimsIphoneUsbSessionRoot $profile.session_id;[void][IO.Directory]::CreateDirectory($statusRoot)
    Assert ((&$realStatus $profile).state -ceq 'restore-pending') 'Missing status in an existing protected root cannot abandon recovery authority'
    Write-ReimsIphoneUsbSessionJson (Join-Path $statusRoot 'status.json') @{schema=1;session_id=$profile.session_id;id=$profile.id;ecid=$profile.ecid;
        state='connected';active=$true;instance=$normal;pid=321;start_ticks=456;executable='D:\authored-worker.exe'}
    $fixture.exitTick=0
    $orphan=&$realStatus $profile
    Assert (!$orphan.active -and $orphan.state -ceq 'restore-pending') 'A dead worker cannot leave a stale attached-success row'
    $snapshot.vm_root=Join-Path $root 'cancel-before-root-caller-vm'
    Write-ReimsJson (Get-ReimsIphoneUsbAdvisoryStopPath $snapshot.vm_root $snapshot.profile.session_id) @{stop=$true}
    Assert (!(Test-Path -LiteralPath (Join-Path $root 'cancel-before-root'))) 'Cancellation marker exists before the delayed protected worker root'
    $folder=New-Fixture 'cancel-before-root'
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert (!$fixture.events.Contains('identity') -and !$fixture.events.Contains('bind') -and !$fixture.added.Count -and $fixture.states[-1] -ceq 'restored') 'Delayed start after cancellation performs no identity/capture and finishes restore-only'
    $folder=New-Fixture 'cancel-identity'
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.events.Contains('identity') -and !$fixture.events.Contains('bind') -and !$fixture.added.Count -and $fixture.states[-1] -ceq 'restored') 'Cancellation during identity prevents both binding and QMP add'
    $folder=New-Fixture 'cancel-after-bind'
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.events.Contains('bind') -and $fixture.events.Contains('restore') -and !$fixture.added.Count -and !$fixture.events.Contains('remove') -and $fixture.states[-1] -ceq 'restored') 'Cancellation after binding restores without issuing QMP add or claiming an unissued object'
    $folder=New-Fixture 'skip-other-registered-device'
    $fixture.candidates=@($normal,$recovery)
    $isolated=$snapshot.Clone();$isolated.other_instances=@($recovery)
    Invoke-ReimsIphoneUsbSession $isolated $folder
    Assert ($fixture.added.Count -eq 1 -and $fixture.added[0]['host-instance-id'] -ceq $normal -and
        @($fixture.events|Where-Object {$_ -ceq 'identity'}).Count -eq 1 -and $fixture.models[$recovery].Service -ceq 'usbccgp') 'Resident loop never identifies, binds or adds the other registered Apple device'
    $folder=New-Fixture 'normal'
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.events.Contains('bind') -and $fixture.events.Contains('remove') -and $fixture.events.Contains('restore') -and $fixture.states[-1] -ceq 'restored') "Connect/remove/restore order failed: events=$($fixture.events -join ','); states=$($fixture.states -join ','); error=$($fixture.lastError)"
    Assert ($fixture.events.IndexOf('remove') -lt $fixture.events.IndexOf('restore')) 'Driver restoration never precedes confirmed QMP removal'
    Assert ($fixture.added.Count -eq 1 -and $fixture.added[0]['guest-reset'] -and !$fixture.added[0]['guest-resets-all']) 'Production loop preserves real addressed resets without cycling initial enumeration'
    $folder=New-Fixture 'guard-once';$fixture.exitTick=3
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.safeChecks -eq 1 -and $fixture.added.Count -eq 1 -and !$fixture.states.Contains('qmp-wait-exit') -and $fixture.restoreTick -eq 3) 'Connected polling does not repeat the expensive protected-device check or stop on its later transient failure'
    $folder=New-Fixture 'same-id';$fixture.exitTick=3
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.added.Count -eq 2 -and $fixture.added[0]['host-instance-id'] -ceq $fixture.added[1]['host-instance-id'] -and
        @($fixture.events|Where-Object {$_ -ceq 'bind'}).Count -eq 1 -and $fixture.safeChecks -eq 1) 'Same-instance physical re-enumeration re-adds QMP without an unnecessary driver bind or protected-device check'
    Assert ($fixture.events.IndexOf('device_add') -lt $fixture.events.IndexOf('remove') -and $fixture.events.IndexOf('remove') -lt $fixture.events.LastIndexOf('device_add') -and
        @($fixture.events|Where-Object {$_ -ceq 'remove'}).Count -eq 1 -and $fixture.arrivals[0] -eq 11 -and $fixture.arrivals[1] -eq 22) 'Confirmed owned removal precedes recreation and each QMP arrival is persisted before add'
    $folder=New-Fixture 'unplug-replug';$fixture.exitTick=4
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.states.Contains('waiting') -and $fixture.added.Count -eq 2 -and !$fixture.lastError -and $fixture.restoreTick -eq 4) 'A real absence between unplug and replug keeps the session waiting, then reconnects without an array error or early restoration'
    Assert (@($fixture.events|Where-Object {$_ -ceq 'bind'}).Count -eq 1 -and $fixture.events.IndexOf('remove') -lt $fixture.events.LastIndexOf('device_add')) 'A reappearing exact instance reuses its owned binding only after removing the previous QMP device'
    $folder=New-Fixture 'absent-start';$fixture.present[$normal]=$false
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.states.Contains('waiting') -and !$fixture.lastError -and !$fixture.added.Count -and !$fixture.events.Contains('bind')) 'Absence before the first binding is a waiting state, not a failed or fabricated capture'
    $folder=New-Fixture 'shutdown';$fixture.exitTick=5
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.tick -eq 1 -and $fixture.events.Contains('remove') -and $fixture.events.Contains('restore') -and $snapshot.profile.enabled) 'Guest power off restores the phone while retained QEMU stays alive and selection remains enabled'
    $folder=New-Fixture 'paused';$fixture.exitTick=3
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.tick -eq 3 -and !$fixture.events.Contains('remove') -and $fixture.states[-1] -ceq 'restored') 'Paused QEMU retains capture until the process actually exits'
    $folder=New-Fixture 'replug';$fixture.reboot=$true;$fixture.exitTick=3
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.states[0] -ceq 'replug-waiting' -and $fixture.states.Contains('connected')) 'Historical reboot request resumes only after a new arrival and actual target stack'
    $receipt=Read-ReimsUsbDriverBindingReceipt (Join-Path $folder "private/receipts/$($profile.id).json")
    Assert ($receipt.phase -ceq 'restored' -and $receipt.needs_reboot -and $receipt.live_verification.service_stack -ceq 'usbccgp') 'Live original-model/stack proof resolves per-device restore without clearing historical reboot request'
    foreach($case in @('stale-arrival','old-stack')) {
        $folder=New-Fixture $case;$fixture.reboot=$true
        if($case -ceq 'stale-arrival'){$fixture.stacks[$normal]='libusb0'}else{$fixture.arrival=[DateTime]::UtcNow.AddSeconds(1).Ticks}
        Invoke-ReimsIphoneUsbSession $snapshot $folder
        Assert (!$fixture.added.Count -and !$fixture.states.Contains('connected')) 'A new arrival and actual target stack are independently necessary'
    }
    $folder=New-Fixture 'mode'
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.added.Count -eq 2 -and $fixture.added[1]['host-instance-id'] -ceq $recovery -and $fixture.safeChecks -eq 2) 'Every new mode checks protected devices before binding the exact ECID-matched instance'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $folder 'private/receipts') -File).Count -eq 2 -and $fixture.states.Contains('restore-pending')) 'All mode receipts survive disappearance and wait for restoration arrival'
    Assert (@($fixture.events|Where-Object {$_ -ceq 'restore'}).Count -eq 2) 'Every owned mode is restored; no new capture during restore-only wait'
    $folder=New-Fixture 'qmp';$fixture.exitTick=4
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert ($fixture.states.Contains('qmp-wait-exit') -and $fixture.restoreTick -ge 4) 'Ambiguous/stalled QMP waits for VM exit before restoration'
    $folder=New-Fixture 'foreign-qmp';$fixture.exitTick=4
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert (!$fixture.events.Contains('remove') -and $fixture.states.Contains('qmp-wait-exit') -and $fixture.restoreTick -ge 4) 'A foreign QMP object cannot be deleted or restore a potentially open driver early'
    $folder=New-Fixture 'unowned-qmp';$fixture.objects[$profile.id]=@{'host-instance-id'=$normal}
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert (!$fixture.events.Contains('bind') -and !$fixture.events.Contains('remove')) 'Preexisting unowned QMP IDs refuse before any binding'
    $folder=New-Fixture 'ambiguous';$fixture.candidates=@($normal,$recovery)
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert (!$fixture.events.Contains('bind') -and !$fixture.added.Count) 'More than one matching ECID refuses capture'
    $folder=New-Fixture 'different-ecid';$fixture.ecid='000000000000cafe'
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert (!$fixture.events.Contains('bind') -and !$fixture.added.Count) 'A different actual ECID cannot be captured'
    $folder=New-Fixture 'protected';$fixture.blocked=$true
    Invoke-ReimsIphoneUsbSession $snapshot $folder
    Assert (!$fixture.events.Contains('bind')) 'Protected host USB is rejected before binding'
    $folder=New-Fixture 'resume-restore';$fixture.exitTick=0
    $receiptPath=Join-Path $folder "private/receipts/$($profile.id).json"
    [void](Set-ReimsUsbDriverBinding -InstanceId $normal -ReceiptPath $receiptPath -PackageInf (Join-Path $root 'libusb0.inf'))
    $fixture.events.Clear()
    Write-ReimsIphoneUsbSessionJson (Join-Path $folder 'private/leases.json') @{qmp_instance=$normal;instances=@{}}
    Invoke-ReimsIphoneUsbSession $snapshot $folder -RestoreOnly
    Assert ($fixture.events.Contains('restore') -and !$fixture.events.Contains('identity') -and !$fixture.events.Contains('bind') -and $fixture.states[-1] -ceq 'restored') 'Explicit detach resumes protected recovery after process loss without a new capture'
    $folder=New-Fixture 'resume-unknown-intent';$fixture.exitTick=3
    $receiptPath=Join-Path $folder "private/receipts/$($profile.id).json"
    [void](Set-ReimsUsbDriverBinding -InstanceId $normal -ReceiptPath $receiptPath -PackageInf (Join-Path $root 'libusb0.inf'))
    $fixture.events.Clear()
    Write-ReimsIphoneUsbSessionJson (Join-Path $folder 'private/leases.json') @{qmp_instance=$normal;instances=@{}}
    Assert (!$fixture.objects.Count) 'Dead-worker recovery fixture has an unconfirmed add intent but no current QMP object'
    Invoke-ReimsIphoneUsbSession $snapshot $folder -RestoreOnly
    Assert ($fixture.states.Contains('qmp-wait-exit') -and $fixture.restoreTick -eq 3 -and !$fixture.events.Contains('qom-list') -and !$fixture.events.Contains('remove')) 'Unknown persisted QMP intent prevents restoration until VM exit even when enumeration would be empty'
    # Run the same bootstrap as the elevated entry, not replacements for its functions.
    $temporaryBefore=$env:TEMP
    Import-ReimsIphoneUsbQmpFunctions
    Assert ((Get-Command Get-ReimsVmProcess).Parameters.ContainsKey('Runtime') -and (Get-Command Invoke-ReimsQmp).Parameters.ContainsKey('Runtime') -and
        (Get-Command Invoke-ReimsQmp).Parameters.ContainsKey('Command') -and (Get-Command Invoke-ReimsQmp).Parameters.ContainsKey('Arguments')) 'Actual worker bootstrap preserves full house function signatures beyond the import call'
    $script:bootstrapCalls=0;$script:bootstrapProcess=[pscustomobject]@{Id=$runtime.pid;ProcessName=[IO.Path]::GetFileNameWithoutExtension($runtime.executable);StartTime=[DateTime]::new([long]$runtime.start_ticks,[DateTimeKind]::Utc);Path=$runtime.executable}
    function Get-Process { [CmdletBinding()]param([int]$Id);$script:bootstrapCalls++;$script:bootstrapProcess }
    Assert ((Get-ReimsVmProcess $runtime).Id -eq $runtime.pid) 'Imported production identity helper runs with authored process metadata only'
    $script:bootstrapProcess=$null
    Reject {Invoke-ReimsQmp $runtime 'query-status'} 'no longer running'
    Assert ($script:bootstrapCalls -eq 2 -and $env:TEMP -ceq $temporaryBefore) 'Imported QMP resolves its actual helper and refuses before network or environment setup'
    Assert (!$('ReimsUsbDriverBinding.Native' -as [type])) 'All native driver and identity operations stayed mocked'
    "iPhone session CPU fixture: $checks checks passed; no UAC, DLL, driver, device, VM or GPU operation invoked."
} finally {
    $resolved=[IO.Path]::GetFullPath($root);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+'\'
    if(!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture cleanup escaped its own temporary root.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
