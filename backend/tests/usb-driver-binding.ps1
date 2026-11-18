#Requires -Version 7.2
$ErrorActionPreference='Stop'
$nativeBefore='ReimsUsbDriverBinding.Native' -as [type]
. "$PSScriptRoot/../usb-driver-binding.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('reims-usb-binding-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script:checks=0
function Assert($Condition,[string]$Message) { if(!$Condition) { throw $Message }; $script:checks++ }
function Reject([scriptblock]$Action,[string]$Pattern) {
    $message=''; try { & $Action | Out-Null } catch { $message=$_.Exception.Message }
    Assert ($message -match $Pattern) "Expected refusal: $Pattern; actual: $message"
}
function New-Fixture([string]$Name) {
    $folder=Join-Path $root $Name; New-Item -ItemType Directory -Path $folder | Out-Null
    $original=@{Present=$true;Problem=0;InfPath=(Join-Path $folder 'original-apple.inf');InfHash=('a'*64);
        Section='AuthoredComposite';SectionExtension='.NT';Provider='Authored Apple Driver';Version='1.2.3.4';
        ClassGuid='{36FC9E60-C465-11CF-8056-444553540000}';Service='usbccgp';MatchingId='USB\COMPOSITE';
        DriverKey='{36FC9E60-C465-11CF-8056-444553540000}\0538';ReadState='bound'}
    $state=@{Id='USB\VID_05AC&PID_12A8\AUTHORED-PHONE';Path=(Join-Path $folder 'receipt.json');
        PackageInf=(Join-Path $folder 'libusb0.inf');PublishedInf=(Join-Path $folder 'oem-libusb0.inf');
        Current=($original|ConvertTo-Json|ConvertFrom-Json -AsHashtable);Original=$original;
        Calls=[Collections.Generic.List[string]]::new();StageFail=$false;ChangeFail=$false;RollbackFail=$false;
        Reboot=$false;ForeignStage=$false;PartialFail=$false;Unhealthy=$false;MissingDriver=$false;IncompleteDriver=$false;ForeignIncomplete=$false;ReadDenied=$false}
    $read={ param($id)
        if($id -cne $state.Id) { throw 'Fixture wrong instance' }
        $state.Calls.Add('read')
        if($state.ReadDenied -and $state.Calls.Contains('bind')) { throw [UnauthorizedAccessException]::new('Fixture metadata read denied') }
        $state.Current | ConvertTo-Json | ConvertFrom-Json -AsHashtable
    }.GetNewClosure()
    $stage={ param($inf)
        $saved=Get-Content -Raw -LiteralPath $state.Path | ConvertFrom-Json -AsHashtable
        if($saved.phase -ne 'prepared' -or $saved.mutation_attempted -or $saved.original.InfHash -ne $state.Original.InfHash) { throw 'Receipt was not durable before staging' }
        $state.Calls.Add('stage')
        if($state.StageFail) { throw 'Fixture staging failed' }
        if($state.ForeignStage) { $state.Current.Service='ForeignService';$state.Current.InfHash='b'*64 }
        $state.PublishedInf
    }.GetNewClosure()
    $install={ param($id,$wanted,$expected)
        if($id -cne $state.Id) { throw 'Fixture wrong instance' }
        $saved=Get-Content -Raw -LiteralPath $state.Path | ConvertFrom-Json -AsHashtable
        if($saved.phase -notin @('binding','restoring') -or !$saved.mutation_attempted) { throw 'Receipt was not durable before selection' }
        if($state.Current.InfHash -ne $expected.InfHash -or $state.Current.Service -ne $expected.Service -or $state.Current.ClassGuid -ne $expected.ClassGuid) { throw 'Fixture expected-driver guard failed' }
        $capture=$wanted.Service -eq 'libusb0';$state.Calls.Add($(if($capture){'bind'}else{'restore'}))
        if(!$capture -and $state.RollbackFail) { throw 'Fixture rollback failed' }
        if($capture -and $state.PartialFail) { $state.Current.ClassGuid=$wanted.ClassGuid;throw 'Fixture class selection failed' }
        $state.Current=$wanted | ConvertTo-Json | ConvertFrom-Json -AsHashtable
        if($capture -and ($state.MissingDriver -or $state.IncompleteDriver)) {
            $state.Current.ReadState=$(if($state.MissingDriver){'unbound'}else{'incomplete'})
            $state.Current.DriverKey=$(if($state.MissingDriver){$null}else{$wanted.ClassGuid+'\0042'})
            foreach($field in @('InfPath','InfHash','Section','SectionExtension','Provider','Version','MatchingId','Service')) { $state.Current[$field]=$null }
            if($state.ForeignIncomplete) { $state.Current.Service='ForeignService' }
            $state.Current.Problem=28
            throw 'Fixture install lost driver metadata'
        }
        if($capture -and $state.ChangeFail) { throw 'Fixture change failed after mutation' }
        if($capture -and $state.Unhealthy) { $state.Current.Problem=31 }
        [bool]$state.Reboot
    }.GetNewClosure()
    $state.Operations=@{Read=$read;Stage=$stage;Install=$install};$state
}
function Bind($Fixture) { Set-ReimsUsbDriverBinding -InstanceId $Fixture.Id -ReceiptPath $Fixture.Path -PackageInf $Fixture.PackageInf -Operations $Fixture.Operations }
function Restore($Fixture) { Restore-ReimsUsbDriverBinding -InstanceId $Fixture.Id -ReceiptPath $Fixture.Path -Operations $Fixture.Operations }
try {
    Assert (($null -ne ('ReimsUsbDriverBinding.Native' -as [type])) -eq ($null -ne $nativeBefore)) 'Import must not initialize native definitions or query devices'
    Initialize-ReimsUsbDriverBindingNative
    [ReimsUsbDriverBinding.Native]::CheckLayout()
    Assert $true 'Native C# compiles and 64-bit SetupAPI structures match; no native operation invoked'
    Assert ([ReimsUsbDriverBinding.Native]::ClassDriverFlagsEx -eq (0x08000000 -bor 0x00000800)) 'Class lists must retain distinct hardware-ID nodes and include excluded pinned drivers'

    $f=New-Fixture 'success';$result=Bind $f
    Assert ($result.bound -and $result.state -eq 'bound' -and !$result.needs_reboot) 'Binding result must confirm success only after readback'
    $saved=Read-ReimsUsbDriverBindingReceipt $f.Path
    Assert ($saved.original.Section -eq $f.Original.Section -and $saved.original.MatchingId -eq $f.Original.MatchingId -and $saved.target.InfPath -eq $f.PublishedInf) 'Exact original model and staged INF survive receipt serialization'
    $again=Bind $f
    Assert ($again.bound -and @($f.Calls|Where-Object {$_ -eq 'bind'}).Count -eq 1) 'Already-owned binding is idempotent'
    Assert ((Restore $f).restored -and (Test-ReimsUsbDriverBindingModel $f.Current $f.Original)) 'Restore must install the exact original model'
    Assert ((Restore $f).restored -and @($f.Calls|Where-Object {$_ -eq 'restore'}).Count -eq 1) 'Already-restored binding is idempotent'
    $f.Current=$saved.target
    Reject { Restore $f } 'Completed receipt'
    Assert (@($f.Calls|Where-Object {$_ -eq 'restore'}).Count -eq 1) 'Completed receipt must not reclaim a later identical libusb0 binding'

    $f=New-Fixture 'stage-fail';$f.StageFail=$true
    Reject { Bind $f } 'staging failed'
    Assert ((Test-ReimsUsbDriverBindingModel $f.Current $f.Original) -and !$f.Calls.Contains('bind')) 'Staging failure cannot change any device driver'
    Assert ((Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'failed') 'Staging failure retains a readable recovery record'
    Assert ((Restore $f).restored -and !$f.Calls.Contains('restore')) 'No-change failure needs no native restore'

    $f=New-Fixture 'change-fail';$f.ChangeFail=$true
    Reject { Bind $f } 'change failed after mutation'
    Assert ((Test-ReimsUsbDriverBindingModel $f.Current $f.Original) -and $f.Calls.Contains('restore')) 'Partial binding failure restores the original driver'
    Assert ((Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'restored') 'Confirmed rollback is retained, not reported as capture success'

    $f=New-Fixture 'class-partial';$f.PartialFail=$true
    Reject { Bind $f } 'class selection failed'
    Assert ((Test-ReimsUsbDriverBindingModel $f.Current $f.Original) -and $f.Calls.Contains('restore')) 'Class-only native selection failure restores the original class and model'

    foreach($kind in @('MissingDriver','IncompleteDriver')) {
        $f=New-Fixture $kind;$f[$kind]=$true
        Reject { Bind $f } 'lost driver metadata'
        Assert ((Test-ReimsUsbDriverBindingModel $f.Current $f.Original) -and $f.Calls.Contains('restore') -and
            (Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'restored') 'Proven missing/incomplete driver state must restore the exact original model'
    }
    $f=New-Fixture 'foreign-incomplete';$f.IncompleteDriver=$f.ForeignIncomplete=$true
    Reject { Bind $f } 'foreign'
    Assert (!$f.Calls.Contains('restore') -and (Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'rollback-failed') 'Partial metadata identifying a foreign service must not be overwritten'
    $f=New-Fixture 'read-denied';$f.ChangeFail=$f.ReadDenied=$true
    Reject { Bind $f } 'metadata read denied'
    Assert (!$f.Calls.Contains('restore') -and (Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'rollback-failed') 'Access-denied reads must not become no-driver recovery authority'

    $f=New-Fixture 'reboot';$f.Reboot=$true;$result=Bind $f
    Assert (!$result.bound -and $result.needs_reboot -and $result.state -eq 'reboot-required') 'Reboot-required is neither success nor an automatic reboot'
    Assert ((Read-ReimsUsbDriverBindingReceipt $f.Path).needs_reboot) 'Reboot requirement remains durable'
    $f.Reboot=$false;$result=Restore $f
    Assert (!$result.restored -and $result.needs_reboot -and $result.state -eq 'restore-reboot-required') 'Pending reboot must not silently become a verified restoration'

    $f=New-Fixture 'rollback-fail';$f.ChangeFail=$f.RollbackFail=$true
    Reject { Bind $f } 'rollback failed'
    Assert ((Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'rollback-failed') 'Rollback failure must keep original receipt for recovery'

    $f=New-Fixture 'foreign-stage';$f.ForeignStage=$true
    Reject { Bind $f } 'changed while staging'
    Assert (!$f.Calls.Contains('bind') -and $f.Current.Service -eq 'ForeignService') 'Foreign staging-time driver changes are not overwritten'
    Reject { Restore $f } 'never changed'
    Assert (!$f.Calls.Contains('restore')) 'Non-mutating receipt has no authority over a later driver'

    $f=New-Fixture 'foreign-restore';Bind $f | Out-Null;$f.Current.Service='ForeignService'
    Reject { Restore $f } 'foreign'
    Assert (!$f.Calls.Contains('restore') -and (Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'bound') 'Foreign restore refusal retains receipt without clobbering driver'

    # Windows Update / Apple driver upgrade to a newer version of the same provider+service must be accepted as already restored without downgrading.
    $f=New-Fixture 'updated-original';Bind $f | Out-Null
    $f.Current=$f.Original.Clone()
    $f.Current.InfPath=Join-Path (Split-Path $f.Original.InfPath) 'updated-apple.inf'
    $f.Current.InfHash='c'*64
    $f.Current.Version='552.0.0.0'
    $result=Restore $f
    Assert ($result.restored -and $result.state -eq 'restored' -and !$f.Calls.Contains('restore') -and (Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'restored' -and $f.Current.Version -eq '552.0.0.0') 'Updated same-provider original driver must complete restoration without downgrading'

    # Foreign provider with same service name must still be rejected.
    $f=New-Fixture 'foreign-provider';Bind $f | Out-Null
    $f.Current=$f.Original.Clone()
    $f.Current.Provider='ThirdPartyVendor'
    $f.Current.InfHash='d'*64
    Reject { Restore $f } 'foreign'
    Assert (!$f.Calls.Contains('restore') -and (Read-ReimsUsbDriverBindingReceipt $f.Path).phase -eq 'bound') 'Foreign provider replacement must still be refused'

    $f=New-Fixture 'absent';Bind $f | Out-Null;$target=$f.Current;$f.Current=@{Present=$false}
    $result=Restore $f
    Assert (!$result.restored -and $result.state -eq 'restore-pending' -and !$f.Calls.Contains('restore')) 'Disconnected device restoration remains explicitly pending'
    $f.Current=$target
    Assert ((Restore $f).restored) 'Exact instance reappearance can finish original-driver restoration'

    $f=New-Fixture 'unhealthy';$f.Unhealthy=$true
    Reject { Bind $f } 'not confirmed healthy'
    Assert ((Test-ReimsUsbDriverBindingModel $f.Current $f.Original)) 'Unhealthy target binding cannot be reported captured'

    $f=New-Fixture 'unhealthy-original';$f.Current.Problem=31
    Reject { Bind $f } 'Original driver is not healthy'
    Assert (!$f.Calls.Contains('stage') -and !(Test-Path $f.Path)) 'Unhealthy original driver is refused before staging or creating a lease'
    $f=New-Fixture 'owned-later-unhealthy';Bind $f | Out-Null;$f.Current.Problem=31
    Reject { Bind $f } 'Owned driver binding is not healthy'
    Assert (@($f.Calls|Where-Object {$_ -eq 'bind'}).Count -eq 1) 'A later device problem is not reported as another successful capture'

    $f=New-Fixture 'multiple'
    Reject { Set-ReimsUsbDriverBinding -InstanceId @($f.Id,'USB\VID_05AC&PID_12A8\OTHER') -ReceiptPath $f.Path -PackageInf $f.PackageInf -Operations $f.Operations } 'Exactly one'
    Reject { Set-ReimsUsbDriverBinding -InstanceId 'USB\VID_05AC&PID_12A8&MI_01\CHILD' -ReceiptPath $f.Path -PackageInf $f.PackageInf -Operations $f.Operations } 'Exactly one'
    Reject { Set-ReimsUsbDriverBinding -InstanceId 'USB\VID_05AC&PID_12A8\*' -ReceiptPath $f.Path -PackageInf $f.PackageInf -Operations $f.Operations } 'Exactly one'
    Assert (!$f.Calls.Count -and !(Test-Path $f.Path)) 'Multi-device/interface/wildcard requests refuse before operations or receipts'
    $f=New-Fixture 'tampered-receipt';Bind $f | Out-Null
    $record=Read-ReimsUsbDriverBindingReceipt $f.Path;$record.target.Version='9.0.0.0'
    [IO.File]::WriteAllText($f.Path,($record|ConvertTo-Json -Depth 16))
    Reject { Restore $f } 'pinned signed driver model'
    Assert (!$f.Calls.Contains('restore')) 'Tampered target cannot reach driver restoration'
    foreach($name in @('libusb0.inf','libusb0.cat','amd64/libusb0.sys','amd64/libusb0.dll','x86/libusb0_x86.dll')) {
        $file=Join-Path (Split-Path $f.PackageInf) $name
        New-Item -ItemType Directory -Force -Path (Split-Path $file) | Out-Null
        [IO.File]::WriteAllText($file,'authored invalid package')
    }
    Reject { Assert-ReimsUsbDriverBindingPackage $f.PackageInf } 'pin mismatch'
    $f=New-Fixture 'missing-package';$missing=$false
    try { Assert-ReimsUsbDriverBindingPackage $f.PackageInf }
    catch [System.Management.Automation.ItemNotFoundException] { $missing=$true }
    Assert ($missing -and !$f.Calls.Count) 'Missing pinned files must refuse before any native staging'
    "USB driver binding CPU fixture: $script:checks checks passed; no device, driver, VM or GPU operation invoked."
} finally {
    if(Test-Path -LiteralPath $root) {
        $resolved=[IO.Path]::GetFullPath($root)
        $temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
        if(!$resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture cleanup escaped its temporary workspace.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
