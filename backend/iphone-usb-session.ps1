# No device/driver operation runs when dot-sourced.
param([string]$InputPath,[string]$InputHash,[string]$SessionId,[switch]$StopSession,[string]$CallerSid)
. "$PSScriptRoot/iphone-usb-package.ps1"

function Get-ReimsIphoneUsbSessionRoot([string]$Session) {
    if($Session -cnotmatch '^[0-9a-f]{32}$'){throw 'Invalid iPhone session ID.'}
    Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "ReimsIphoneUsb-$Session"
}

function Get-ReimsIphoneUsbTemporaryRoot([string]$EngineRoot,[string]$Session) {
    if(![IO.Path]::IsPathFullyQualified($EngineRoot) -or $Session -cnotmatch '^[0-9a-f]{32}$'){throw 'Invalid protected temporary session path.'}
    Join-Path ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($EngineRoot))) ".cache/reims-iphone-usb-$Session"
}

function Assert-ReimsIphoneUsbSessionRoot([string]$Root,[string]$Sid) {
    [void](Assert-ReimsIphoneUsbOrdinaryPath $Root)
    $acl=Get-Acl -LiteralPath $Root
    $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if($owner -cnotin @('S-1-5-32-544','S-1-5-18') -or !$acl.AreAccessRulesProtected){throw 'Untrusted iPhone session owner/ACL.'}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if($rule.IsInherited -or $rule.AccessControlType -ne 'Allow' -or $rule.IdentityReference.Value -cnotin @('S-1-5-32-544','S-1-5-18',$Sid) -or
            ($rule.IdentityReference.Value -ceq $Sid -and ([long]$rule.FileSystemRights -band 0xD0156))){throw 'Untrusted iPhone session permissions.'}
    }
}

function New-ReimsIphoneUsbSessionRoot([string]$Root,[string]$Sid) {
    [void][Security.Principal.SecurityIdentifier]::new($Sid)
    [void](Assert-ReimsIphoneUsbOrdinaryPath $Root -AllowMissing)
    if(Test-Path -LiteralPath $Root){throw 'iPhone session directory already exists.'}
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $admins=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544');$acl.SetOwner($admins)
    foreach($identity in @($admins,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($Sid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Root),$acl)
    Assert-ReimsIphoneUsbSessionRoot $Root $Sid
    $private=Join-Path $Root 'private';$acl.RemoveAccessRuleAll([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($Sid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($private),$acl)
    [void][IO.Directory]::CreateDirectory((Join-Path $private 'receipts'))
}

function Write-ReimsIphoneUsbSessionJson([string]$Path,$Value) {
    [void](Assert-ReimsIphoneUsbOrdinaryPath $Path -AllowMissing)
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 16 -WarningAction Stop))
        $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
        [IO.File]::Move($temporary,$Path,$true)
    } finally {if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}}
}

function Read-ReimsIphoneUsbSessionJson([string]$Path,[string]$ExpectedHash='') {
    [void](Assert-ReimsIphoneUsbOrdinaryPath $Path)
    $file=Get-Item -LiteralPath $Path
    if($file.PSIsContainer -or $file.Length -gt 65536){throw 'Invalid bounded iPhone session file.'}
    $bytes=[IO.File]::ReadAllBytes($Path)
    if($bytes.Length -gt 65536 -or ($ExpectedHash -and [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ine $ExpectedHash)){throw 'iPhone session input changed or exceeds its limit.'}
    [Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json -AsHashtable -Depth 16
}

function Get-ReimsIphoneUsbStatus($Profile) {
    $root=Get-ReimsIphoneUsbSessionRoot $Profile.session_id
    if(!(Test-Path -LiteralPath $root)){return}
    Assert-ReimsIphoneUsbSessionRoot $root ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    $file=Join-Path $root 'status.json'
    if(!(Test-Path -LiteralPath $file)){return @{schema=1;session_id=$Profile.session_id;id=$Profile.id;ecid=$Profile.ecid;state='restore-pending';active=$false;instance=''}}
    $status=Read-ReimsIphoneUsbSessionJson $file
    if($status.schema -ne 1 -or $status.session_id -cne $Profile.session_id -or $status.id -cne $Profile.id -or $status.ecid -cne $Profile.ecid -or
        $status.active -isnot [bool] -or $status.state -cnotin @('waiting','connected','replug-waiting','restoring','restore-pending','restored','error','qmp-wait-exit')){throw 'Invalid protected iPhone status.'}
    if($status.active -and !(Get-ReimsVmProcess $status)){$status.active=$false;$status.state='restore-pending'}
    $status
}

function Get-ReimsIphoneUsbArrival([string]$Instance) {
    $property=@(Get-PnpDeviceProperty -InstanceId $Instance -KeyName DEVPKEY_Device_LastArrivalDate -ErrorAction Stop)
    if(!$property.Count -or ($property.Count -eq 1 -and !$property[0].PSObject.Properties['Data'])){return 0L}
    if($property.Count -ne 1){throw 'Ambiguous iPhone arrival time.'}
    $value=$property[0].Data
    if($value -isnot [DateTime]){throw 'Cannot verify iPhone arrival time.'}
    $value.ToUniversalTime().Ticks
}

function Test-ReimsIphoneUsbLiveModel([string]$Instance,$Model) {
    $operations=Get-ReimsUsbDriverBindingOperations;$actual=& $operations.Read $Instance
    if(!(Test-ReimsUsbDriverBindingModel $actual $Model) -or $actual.Problem -ne 0){return $false}
    $property=@(Get-PnpDeviceProperty -InstanceId $Instance -KeyName DEVPKEY_Device_Stack -ErrorAction Stop)
    if(!$property.Count -or ($property.Count -eq 1 -and !$property[0].PSObject.Properties['Data'])){return $false}
    if($property.Count -ne 1){throw 'Ambiguous iPhone service stack.'}
    $stack=@($property[0].Data)
    [bool]($stack | Where-Object {$_ -imatch ('(^|\\)'+[regex]::Escape($Model.Service)+'$')})
}

function Publish-ReimsIphoneUsbStatus($Snapshot,[string]$Root,[string]$State,[string]$Instance='',[string]$ErrorText='') {
    if($ErrorText.Length -gt 2048){$ErrorText=$ErrorText.Substring(0,2048)}
    $self=Get-Process -Id $PID
    Write-ReimsIphoneUsbSessionJson (Join-Path $Root 'status.json') @{schema=1;session_id=$Snapshot.profile.session_id;id=$Snapshot.profile.id;ecid=$Snapshot.profile.ecid;
        state=$State;active=($State -cne 'restored');instance=$Instance;error=$ErrorText;pid=$PID;start_ticks=$self.StartTime.ToUniversalTime().Ticks;executable=$self.Path;
        qemu_pid=$Snapshot.runtime.pid;qemu_start_ticks=$Snapshot.runtime.start_ticks}
}

function Restore-ReimsIphoneUsbLeases([string]$Root) {
    $complete=$true
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $Root 'private/receipts') -Filter '*.json' -File) {
        $receipt=Read-ReimsUsbDriverBindingReceipt $file.FullName
        if($file.BaseName -cne (Get-ReimsIphoneUsbId $receipt.instance_id)){throw 'Foreign iPhone receipt filename.'}
        if($receipt.phase -ceq 'restored' -and $receipt.ContainsKey('live_verification')){continue}
        $result=Restore-ReimsUsbDriverBinding -InstanceId $receipt.instance_id -ReceiptPath $file.FullName
        if(($result.restored -or $result.state -ceq 'restore-reboot-required') -and (Test-ReimsIphoneUsbLiveModel $receipt.instance_id $receipt.original)) {
            $receipt=Read-ReimsUsbDriverBindingReceipt $file.FullName
            $receipt.phase='restored';$receipt.live_verification=@{original_model=$true;problem=0;service_stack=$receipt.original.Service;verified_utc=[DateTime]::UtcNow.ToString('o')}
            Write-ReimsUsbDriverBindingReceipt $file.FullName $receipt
        } else {
            $complete=$false
            $receipt=Read-ReimsUsbDriverBindingReceipt $file.FullName
            if($receipt.phase -ceq 'restored'){$receipt.phase='restore-pending';Write-ReimsUsbDriverBindingReceipt $file.FullName $receipt}
        }
    }
    $complete
}

function Remove-ReimsIphoneUsbQmp($Snapshot,[string]$Root,$Lease) {
    if(!$Lease.qmp_instance){return}
    if(Get-ReimsVmProcess $Snapshot.runtime) {
        $objects=Invoke-ReimsQmp $Snapshot.runtime 'qom-list' @{path='/machine/peripheral'}
        if($objects | Where-Object name -CEQ $Snapshot.profile.id) {
            $instance=Invoke-ReimsQmp $Snapshot.runtime 'qom-get' @{path="/machine/peripheral/$($Snapshot.profile.id)";property='host-instance-id'}
            if($instance -is [Array] -and $instance.Count -eq 1){$instance=$instance[0]}
            if($instance -isnot [string] -or $instance -ine $Lease.qmp_instance){throw 'QEMU iPhone object ownership is ambiguous.'}
            Remove-ReimsUsbObject $Snapshot.runtime $Snapshot.profile.id
        }
    }
    $Lease.qmp_instance='';$Lease.qmp_arrival=0L;Write-ReimsIphoneUsbSessionJson (Join-Path $Root 'private/leases.json') $Lease
}

function Test-ReimsIphoneUsbStopRequested($Snapshot,[string]$Root) {
    # Never parse or execute caller-controlled data; existence only cancels our session.
    (Test-Path -LiteralPath (Join-Path $Root 'stop')) -or
        (Test-Path -LiteralPath (Get-ReimsIphoneUsbAdvisoryStopPath $Snapshot.vm_root $Snapshot.profile.session_id))
}

function Get-ReimsIphoneUsbOtherInstances($Snapshot) {
    if(!$Snapshot.ContainsKey('other_instances')){return}
    if($Snapshot.other_instances -isnot [Array] -or $Snapshot.other_instances.Count -gt 128){throw 'Invalid excluded Apple USB instances.'}
    foreach($instance in $Snapshot.other_instances) {
        if($instance -isnot [string] -or !(Test-ReimsIphoneUsbInstance $instance) -or $instance -ieq $Snapshot.profile.initial_instance){throw 'Invalid excluded Apple USB instance.'}
        $instance
    }
}

function Invoke-ReimsIphoneUsbSession($Snapshot,[string]$Root,[switch]$RestoreOnly) {
    $otherInstances=@(Get-ReimsIphoneUsbOtherInstances $Snapshot)
    $mutex=[Threading.Mutex]::new($false,"Global\ReimsManager.IphoneUsb.$($Snapshot.profile.ecid)");$held=$false
    try {
        try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true}
        if(!$held){throw 'この iPhone は別の接続セッションが使用中です。'}
        $leasePath=Join-Path $Root 'private/leases.json'
        $lease=if(Test-Path -LiteralPath $leasePath){Read-ReimsIphoneUsbSessionJson $leasePath}else{@{qmp_instance='';qmp_arrival=0L;instances=@{}}}
        if(!$lease.ContainsKey('qmp_arrival')){$lease.qmp_arrival=0L}
        $stopping=[bool]$RestoreOnly -or (Test-ReimsIphoneUsbStopRequested $Snapshot $Root)
        # A dead client's add can still be queued even when qom-list is empty.
        $waitExit=[bool]$RestoreOnly -and [bool]$lease.qmp_instance -and [bool](Get-ReimsVmProcess $Snapshot.runtime);$errorText=''
        $context=if(!$stopping -and (Get-ReimsVmProcess $Snapshot.runtime)){Get-ReimsIphoneUsbContext $Snapshot.runtime}
        while($true) {
            $alive=[bool](Get-ReimsVmProcess $Snapshot.runtime)
            if(!$alive -or (Test-ReimsIphoneUsbStopRequested $Snapshot $Root)){$stopping=$true}
            if($waitExit -and $alive){Publish-ReimsIphoneUsbStatus $Snapshot $Root 'qmp-wait-exit' '' $errorText;Start-Sleep -Seconds 2;continue}
            try {
                if($alive){
                    $vmStatus=Invoke-ReimsQmp $Snapshot.runtime 'query-status'
                    if($vmStatus.status -isnot [string]){throw 'Invalid QEMU power state.'}
                    if($vmStatus.status -ceq 'shutdown'){$stopping=$true}
                }
                if($stopping) {
                    Remove-ReimsIphoneUsbQmp $Snapshot $Root $lease
                    Publish-ReimsIphoneUsbStatus $Snapshot $Root 'restoring' '' $errorText
                    if(Restore-ReimsIphoneUsbLeases $Root){Publish-ReimsIphoneUsbStatus $Snapshot $Root 'restored' '' $errorText;return}
                    Publish-ReimsIphoneUsbStatus $Snapshot $Root 'restore-pending' '' $errorText
                } else {
                    $candidates=@(Get-ReimsIphoneUsbCandidates);$matches=@();$identityFailure=''
                    foreach($candidate in $candidates) {
                        $id=$candidate.InstanceId;$key=Get-ReimsIphoneUsbId $id
                        if($otherInstances -icontains $id){continue}
                        $receiptPath=Join-Path $Root "private/receipts/$key.json"
                        $receipt=if(Test-Path -LiteralPath $receiptPath){Read-ReimsUsbDriverBindingReceipt $receiptPath}
                        if($receipt -and $lease.instances.ContainsKey($key) -and $receipt.phase -cne 'restored') {
                            $operations=Get-ReimsUsbDriverBindingOperations;$actual=& $operations.Read $id
                            if(!$actual.Present){continue}
                            if((Test-ReimsUsbDriverBindingModel $actual $receipt.target) -or (Test-ReimsUsbDriverBindingModel $actual $receipt.original) -or
                                (Test-ReimsUsbDriverBindingRecovery $actual $receipt)){$matches+=@{instance=$id;receipt=$receipt;path=$receiptPath};continue}
                            try{Write-ReimsIphoneUsbSessionJson (Join-Path $Root 'private/driver-state-failure.json') @{instance=$id;actual=$actual;original=$receipt.original;target=$receipt.target;phase=$receipt.phase;utc=[DateTime]::UtcNow.ToString('o')}}catch{}
                            throw 'An owned iPhone mode now has a foreign driver; restoration authority was retained.'
                        }
                        try{$identity=Get-ReimsIphoneUsbIdentity $id $context}catch{
                            $failure=$_
                            if(!(Get-ReimsIphoneUsbCandidates | Where-Object InstanceId -IEQ $id)){continue}
                            if($id -ieq $Snapshot.profile.initial_instance){throw $failure};$identityFailure=$failure.Exception.Message;continue
                        }
                        if($identity.ecid -ceq $Snapshot.profile.ecid){$matches+=@{instance=$id;receipt=$receipt;path=$receiptPath}}
                    }
                    if($matches.Count -gt 1){throw '同じ ECID の iPhone が複数あります。接続しません。'}
                    # PowerShell gives -and/-or equal precedence; group before indexing.
                    if(!$matches.Count -or ($lease.qmp_instance -and $lease.qmp_instance -ine $matches[0].instance)){Remove-ReimsIphoneUsbQmp $Snapshot $Root $lease}
                    if(!$matches.Count){Publish-ReimsIphoneUsbStatus $Snapshot $Root 'waiting' '' $identityFailure;Start-Sleep -Seconds 2;continue}
                    $selected=$matches[0];$id=$selected.instance;$key=Get-ReimsIphoneUsbId $id
                    $product=([regex]::Match($id,'(?i)PID_([0-9a-f]{4})')).Groups[1].Value
                    $arrival=Get-ReimsIphoneUsbArrival $id
                    if($arrival -le 0){Remove-ReimsIphoneUsbQmp $Snapshot $Root $lease;Publish-ReimsIphoneUsbStatus $Snapshot $Root 'waiting' $id;Start-Sleep -Seconds 2;continue}
                    if($lease.qmp_instance -ieq $id -and $lease.qmp_arrival -ne $arrival){Remove-ReimsIphoneUsbQmp $Snapshot $Root $lease}
                    if(!$selected.receipt -or $selected.receipt.phase -ceq 'restored') {
                        $objects=Invoke-ReimsQmp $Snapshot.runtime 'qom-list' @{path='/machine/peripheral'}
                        if($objects | Where-Object name -CEQ $Snapshot.profile.id){throw 'An unowned QEMU iPhone object already exists.'}
                        $lease.instances[$key]=@{binding_ticks=[DateTime]::UtcNow.Ticks;instance=$id}
                        Write-ReimsIphoneUsbSessionJson $leasePath $lease
                        Assert-ReimsUsbSafe @(@{vendor='05ac';product=$product})
                        if(Test-ReimsIphoneUsbStopRequested $Snapshot $Root){$stopping=$true;continue}
                        [void](Set-ReimsUsbDriverBinding -InstanceId $id -ReceiptPath $selected.path -PackageInf $context.package.infPath)
                        $selected.receipt=Read-ReimsUsbDriverBindingReceipt $selected.path
                    }
                    $ready=Test-ReimsIphoneUsbLiveModel $id $selected.receipt.target
                    $arrival=Get-ReimsIphoneUsbArrival $id
                    if($arrival -le 0 -or ($selected.receipt.needs_reboot -and $arrival -le $lease.instances[$key].binding_ticks)){$ready=$false}
                    if(!$ready){Publish-ReimsIphoneUsbStatus $Snapshot $Root 'replug-waiting' $id;Start-Sleep -Seconds 2;continue}
                    if(!$lease.qmp_instance) {
                        $objects=Invoke-ReimsQmp $Snapshot.runtime 'qom-list' @{path='/machine/peripheral'}
                        if($objects | Where-Object name -CEQ $Snapshot.profile.id){throw 'An unowned QEMU iPhone object already exists.'}
                        $lease.qmp_instance=$id;$lease.qmp_arrival=$arrival;Write-ReimsIphoneUsbSessionJson $leasePath $lease
                        if(Test-ReimsIphoneUsbStopRequested $Snapshot $Root){
                            $lease.qmp_instance='';$lease.qmp_arrival=0L;Write-ReimsIphoneUsbSessionJson $leasePath $lease
                            $stopping=$true;continue
                        }
                        Invoke-ReimsQmp $Snapshot.runtime 'device_add' (Get-ReimsIphoneUsbArgument $Snapshot.profile $id)|Out-Null
                    }
                    $attached=Wait-ReimsUsbAttachment $Snapshot.runtime $Snapshot.profile.id
                    Publish-ReimsIphoneUsbStatus $Snapshot $Root $(if($attached){'connected'}else{'waiting'}) $id
                }
            } catch {
                $errorText=$_.Exception.Message;$stopping=$true
                if($lease.qmp_instance -and $alive){$waitExit=$true}
                Publish-ReimsIphoneUsbStatus $Snapshot $Root $(if($waitExit){'qmp-wait-exit'}else{'error'}) '' $errorText
            }
            Start-Sleep -Seconds 2
        }
    } finally {if($held){$mutex.ReleaseMutex()};$mutex.Dispose()}
}

function Import-ReimsIphoneUsbQmpFunctions {
    # Import full declarations: Body alone omits parameters declared after the name.
    # Never execute start.ps1/environment.ps1 or their caller-tree setup code.
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile("$PSScriptRoot/start.ps1",[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Cannot parse the installed QMP helpers.'}
    foreach($name in @('Get-ReimsVmProcess','Invoke-ReimsQmp')) {
        $definitions=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)|Where-Object Name -CEQ $name)
        if($definitions.Count -ne 1){throw 'Missing or ambiguous installed QMP helper.'}
        $text=$definitions[0].Extent.Text -replace ('^function\s+'+[regex]::Escape($name)+'\b'),"function script:$name"
        . ([scriptblock]::Create($text))
    }
}

function Start-ReimsIphoneUsbSessionWorker([string]$SnapshotPath,[string]$Hash,[string]$Session,[switch]$Stop,[string]$Sid) {
    if(!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required.'}
    . "$PSScriptRoot/iphone-usb-package.ps1"
    . "$PSScriptRoot/iphone-usb.ps1"
    . "$PSScriptRoot/usb.ps1"
    Import-ReimsIphoneUsbQmpFunctions
    $root=Get-ReimsIphoneUsbSessionRoot $Session
    if($Stop) {
        # shortcut: after Windows restarts, explicit Detach resumes this protected
        # recovery ledger; no system service or automatic elevated startup is installed.
        Assert-ReimsIphoneUsbSessionRoot $root $Sid
        $snapshot=Read-ReimsIphoneUsbSessionJson (Join-Path $root 'private/snapshot.json')
        if($snapshot.caller_sid -cne $Sid -or $snapshot.profile.session_id -cne $Session){throw 'Foreign iPhone stop session.'}
        Write-ReimsIphoneUsbSessionJson (Join-Path $root 'stop') @{stop=$true}
    } else {
        if($Hash -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid snapshot hash.'}
        $snapshot=Read-ReimsIphoneUsbSessionJson $SnapshotPath -ExpectedHash $Hash
        $profile=Get-ReimsIphoneUsbProfile ([pscustomobject]@{iphone_usb=$snapshot.profile})
        if($snapshot.schema -isnot [int] -and $snapshot.schema -isnot [long] -or $snapshot.schema -ne 1 -or $profile.session_id -cne $Session -or !$profile.enabled -or $snapshot.caller_sid -cnotmatch '^S-1-5-21-[0-9-]+$'){throw 'Invalid typed iPhone session input.'}
        foreach($path in @($snapshot.engine_root,$snapshot.vm_root,$snapshot.tools_root,$snapshot.python)){if(![IO.Path]::IsPathFullyQualified($path)){throw 'Session paths must be absolute.'}}
        if($snapshot.runtime.pid -isnot [long] -and $snapshot.runtime.pid -isnot [int] -or $snapshot.runtime.pid -le 0 -or
            ($snapshot.runtime.start_ticks -isnot [int] -and $snapshot.runtime.start_ticks -isnot [long]) -or $snapshot.runtime.start_ticks -le 0 -or ![IO.Path]::IsPathFullyQualified($snapshot.runtime.executable) -or
            ($snapshot.runtime.qmp_port -isnot [int] -and $snapshot.runtime.qmp_port -isnot [long]) -or
            $snapshot.runtime.qmp_port -lt 1024 -or $snapshot.runtime.qmp_port -gt 65535 -or $snapshot.runtime.usb_backend -cne 'winusb'){throw 'Invalid typed VM runtime.'}
        New-ReimsIphoneUsbSessionRoot $root $snapshot.caller_sid
        Write-ReimsIphoneUsbSessionJson (Join-Path $root 'private/snapshot.json') $snapshot
    }
    $ProjectRoot=$snapshot.engine_root;$VmRoot=$snapshot.vm_root;$env:REIMS_MANAGER_TOOLS_ROOT=$snapshot.tools_root
    $temporaryRoot=Get-ReimsIphoneUsbTemporaryRoot $ProjectRoot $Session
    if(Test-Path -LiteralPath $temporaryRoot){Assert-ReimsIphoneUsbSessionRoot $temporaryRoot $snapshot.caller_sid}else{New-ReimsIphoneUsbSessionRoot $temporaryRoot $snapshot.caller_sid}
    # Keep the sparse protected task cache for explicit owner cleanup; never use
    # the caller's VM tree or ProgramData for elevated temporary/cache writes.
    $env:TEMP=$env:TMP=$env:TMPDIR=Join-Path $temporaryRoot 'private/tmp'
    $env:XDG_CACHE_HOME=Join-Path $temporaryRoot 'private/cache';$env:DOTNET_CLI_HOME=Join-Path $env:XDG_CACHE_HOME 'dotnet-home'
    $env:NUGET_PACKAGES=Join-Path $env:XDG_CACHE_HOME 'nuget';$env:NUGET_HTTP_CACHE_PATH=Join-Path $env:XDG_CACHE_HOME 'nuget-http'
    [void][IO.Directory]::CreateDirectory($env:TEMP)
    if($Stop) {
        $statusPath=Join-Path $root 'status.json'
        if(Test-Path -LiteralPath $statusPath){$status=Read-ReimsIphoneUsbSessionJson $statusPath;if(Get-ReimsVmProcess $status){return}}
    }
    Invoke-ReimsIphoneUsbSession $snapshot $root -RestoreOnly:$Stop
}

if($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference='Stop'
    Start-ReimsIphoneUsbSessionWorker $InputPath $InputHash $SessionId -Stop:$StopSession -Sid $CallerSid
}
