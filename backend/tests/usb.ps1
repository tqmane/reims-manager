#Requires -Version 7.2
. "$PSScriptRoot/../start.ps1" -FunctionsOnly
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('reims-usb-test-'+[guid]::NewGuid())
$VmRoot=$testRoot
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:checks=0
function Assert($Condition, $Message) { if (!$Condition) { throw $Message }; $script:checks++ }
function Reject([scriptblock]$Action) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Assert $failed 'Expected USB refusal' }
$script:inventory=@'
  Bus 2, Addr 12, Port 11.2, Speed 12 Mb/s
    Class 00: USB device 0fd9:0080
  Bus 2, Addr 18, Port 17, Speed 5000 Mb/s
    Class 00: USB device 0bda:9210
  Bus 6, Addr 2, Port 1, Speed 12 Mb/s
    Class 00: USB device 046d:c539
'@
$script:objects=@{}
$script:running=$true
function Get-ReimsVmConfig { Get-Content -Raw (Join-Path $VmRoot 'state/config.json') | ConvertFrom-Json }
function Get-ReimsRuntime { @{pid=1} }
function Get-ReimsVmProcess($Runtime) { $script:running }
function Get-ReimsStoppedUsbInventory($Config) { $script:inventory }
function Get-PnpDevice { }
function Get-ReimsProtectedUsbIds {
    $blocked=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$blocked.Add('0BDA:9210'); [void]$blocked.Add('046D:C539'); return ,$blocked
}
function Invoke-ReimsQmp($Runtime,$Command,$Arguments) {
    switch ($Command) {
        human-monitor-command { $script:inventory }
        device_add { $script:objects[$Arguments.id]=$Arguments }
        device_del { $script:objects.Remove($Arguments.id) }
        qom-list { foreach ($key in $script:objects.Keys) { [pscustomobject]@{name=$key} } }
        qom-get {
            $id=$Arguments.path.Split('/')[-1]
            if ($Arguments.property -ne 'attached' -or !$script:objects.ContainsKey($id)) { throw 'Unknown USB property' }
            $true
        }
        default { throw "Unexpected QMP: $Command" }
    }
}
try {
    $parent=[pscustomobject]@{InstanceId='USB\VID_1234&PID_5678\authored-parent';FriendlyName='USB Composite Device';Class='USB'}
    $mtp=[pscustomobject]@{InstanceId='USB\VID_1234&PID_5678&MI_00\authored-mtp';FriendlyName='Fixture Phone Model';Class='WPD'}
    $adb=[pscustomobject]@{InstanceId='USB\VID_1234&PID_5678&MI_01\authored-adb';FriendlyName='ADB Interface';Class='USBDevice'}
    $descriptions=@{ $parent.InstanceId='Fixture Product'; $mtp.InstanceId='MTP'; $adb.InstanceId='ADB Interface' }
    foreach ($order in @(@($parent,$mtp,$adb),@($adb,$mtp,$parent))) {
        $names=Get-ReimsUsbProductNames $order $descriptions
        Assert ($names['1234:5678'] -eq 'Fixture Product') 'Physical product must outrank child interfaces regardless of enumeration order'
    }
    $descriptions[$parent.InstanceId]='Android'
    Assert ((Get-ReimsUsbProductNames @($parent,$mtp,$adb) $descriptions)['1234:5678'] -eq 'Fixture Phone Model') 'Actual WPD model must outrank generic Android and ADB names'
    $parent.FriendlyName='Physical Fixture Model'
    Assert ((Get-ReimsUsbProductNames @($adb,$parent) @{})['1234:5678'] -eq 'Physical Fixture Model') 'Physical friendly name survives missing bus properties'
    $parent.FriendlyName='USB Composite Device'
    Assert (!(Get-ReimsUsbProductNames @($parent,$adb) $descriptions).ContainsKey('1234:5678')) 'Generic-only metadata must not imply a model name'
    Assert (!(Get-ReimsUsbProductNames @() @{}).Count) 'Missing metadata must preserve inventory without inventing a name'
    Assert ((Get-ReimsUsbProductNames @($mtp,$adb) $descriptions)['1234:5678'] -eq 'Fixture Phone Model') 'Interface model remains useful when the parent is absent'
    $second=[pscustomobject]@{InstanceId='USB\VID_1234&PID_5678\authored-second';FriendlyName='Second Fixture Model';Class='USB'}
    $descriptions[$parent.InstanceId]='First Fixture Model'
    Assert (!(Get-ReimsUsbProductNames @($parent,$second,$mtp,$adb) $descriptions).ContainsKey('1234:5678')) 'Conflicting physical models sharing VID/PID must not be assigned arbitrarily to ports'
    $second.FriendlyName='First Fixture Model'
    Assert ((Get-ReimsUsbProductNames @($second,$adb,$parent) $descriptions)['1234:5678'] -eq 'First Fixture Model') 'A shared confirmed model can name multiple identical devices'
    $descriptions[$parent.InstanceId]='Fixture Product _SN:AUTHORED-NOT-A-DEVICE-SERIAL'
    Assert ((Get-ReimsUsbProductNames @($parent,$adb) $descriptions)['1234:5678'] -eq 'Fixture Product') 'Product labels must not persist an appended bus serial'

    Write-ReimsJson (Join-Path $VmRoot 'state/config.json') @{schema=1}
    $rows=@(Get-ReimsUsbDevices)
    Assert ($rows.Count -eq 1 -and $rows[0].port -eq '11.2') 'Host storage and input must be excluded'
    $id=$rows[0].id
    Set-ReimsUsbSelection $id $true | Out-Null
    $selected=@(Get-ReimsUsbSelections (Get-ReimsVmConfig))
    Assert ($selected.Count -eq 1 -and $script:objects.Count -eq 1) 'Only the selection is saved and attached'
    $argument=Get-ReimsUsbArgument $selected[0]
    Assert (!$argument.ContainsKey('hostaddr') -and $argument.hostport -eq '11.2' -and $argument.vendorid -eq 0xfd9 -and $argument.productid -eq 0x80) 'Reconnect must not use the transient address'
    Set-ReimsUsbSelection $id $true | Out-Null
    Assert (@(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 1) 'Repeated selection is idempotent'
    $script:inventory=$script:inventory.Replace('Addr 12','Addr 45')
    $rows=@(Get-ReimsUsbDevices)
    Assert ($rows[0].id -eq $id -and $rows[0].selected -and $rows[0].address -eq 45) 'Replug keeps selection despite changed address'
    $script:inventory=''
    $rows=@(Get-ReimsUsbDevices)
    Assert ($rows.Count -eq 1 -and !$rows[0].present -and $rows[0].selected) 'Unplugged selection remains removable'
    Set-ReimsUsbSelection $id $false | Out-Null
    Assert (@(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 0 -and !$script:objects.Count) 'Detach removes persisted and live watcher'
    $bad=[pscustomobject]@{id='hostusb-2-17-0bda9210';bus=2;port='17';vendor='0bda';product='9210';label='host storage'}
    Reject { Assert-ReimsUsbSafe @($bad) }
    $bad.port='17,hostaddr=18'
    Reject { Get-ReimsUsbSelections ([pscustomobject]@{usb_devices=@($bad)}) }
    $bad.port='17'; $bad.vendor='0000'
    Reject { Get-ReimsUsbSelections ([pscustomobject]@{usb_devices=@($bad)}) }
    $bad.vendor='0bda'
    Reject { Get-ReimsUsbSelections ([pscustomobject]@{usb_devices=@($bad,$bad)}) }
    $script:running=$false
    $script:inventory="  Bus 2, Addr 12, Port 11.2, Speed 12 Mb/s`n    Class 00: USB device 0fd9:0080`n  Bus 6, Addr 2, Port 1, Speed 12 Mb/s`n    Class 00: USB device 046d:c539"
    Write-ReimsJson (Join-Path $VmRoot 'state/config.json') @{schema=1}
    $stopped=@(Get-ReimsUsbDevices)
    Assert ($stopped.Count -eq 1 -and $stopped[0].present -and !$stopped[0].active -and !$stopped[0].attached) 'Stopped inventory must show eligible physical devices, excluding protected input'
    Reject { Set-ReimsUsbSelection $id $true }
    $script:inventory=''
    Write-ReimsJson (Join-Path $VmRoot 'state/config.json') @{schema=1;usb_devices=$selected}
    Assert (@(Get-ReimsUsbDevices).Count -eq 1) 'Stopped VM lists saved devices'
    Set-ReimsUsbSelection $id $false | Out-Null
    Assert (@(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 0) 'Stopped VM permits unregistering absent devices'
    "$script:checks USB checks passed."
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    $allowed=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
