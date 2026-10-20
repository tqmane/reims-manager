#Requires -Version 7.2
# Authored USB inventory and QMP replies only. No host device, driver, or VM changes.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../usb.ps1"
$stoppedProbe=(Get-Command Get-ReimsStoppedUsbInventory).ScriptBlock
$ProjectRoot=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$VmRoot=Join-Path $ProjectRoot ('.work/tmp/usb-reliability-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $VmRoot 'state'))
$configPath=Join-Path $VmRoot 'state/config.json'
$script:checks=0

# Use the production atomic writer, with an explicit failure boundary around it.
$tokens=$null; $parseErrors=$null
$source=[Management.Automation.Language.Parser]::ParseFile("$PSScriptRoot/../start.ps1",[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'Cannot parse start.ps1' }
foreach ($name in @('Assert-ReimsArtifactPath','Write-ReimsJson')) {
    $definition=$source.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    },$false)
    if (!$definition) { throw "Missing production function: $name" }
    $text=$definition.Extent.Text
    if ($name -eq 'Write-ReimsJson') { $text=$text -replace '^function Write-ReimsJson\b','function Write-ReimsJsonFile' }
    . ([scriptblock]::Create($text))
}

function Assert($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Reject([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch {
        Assert ($_.Exception.Message -match $Pattern) "Expected '$Pattern'; got '$($_.Exception.Message)'"
        return
    }
    throw "Expected failure: $Pattern"
}
function Reset-Fixture {
    $script:fixture=@{
        Running=$true; Objects=@{}; Pending=@{}; Events=[Collections.Generic.List[string]]::new()
        Inventory=@'
  Bus 2, Addr 12, Port 11.2, Speed 12 Mb/s
    Class 00: USB device 0fd9:0080
'@
        AutoAttach=$true; DeleteDelay=0; DeleteFails=$false; AddFails=$false; WriteFails=$false
        DescriptionThrows=$false; RemoveDuringGet=$false; GetFails=$false; Adds=0; Deletes=0; Queries=0; Sleeps=0
    }
    Write-ReimsJsonFile $configPath @{schema=1; usb_devices=@(); resolution='1920x1080'; fixture_marker='retain'}
}
function Write-ReimsJson([string]$Path, $Value) {
    if ($script:fixture.WriteFails) { throw 'Authored settings save failure' }
    $script:fixture.Events.Add('save')
    Write-ReimsJsonFile $Path $Value
}
function Get-ReimsVmConfig { Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json }
function Get-ReimsRuntime { @{pid=1} }
function Get-ReimsVmProcess($Runtime) { $script:fixture.Running }
function Get-ReimsStoppedUsbInventory($Config) { $script:fixture.Inventory }
function Get-PnpDevice {
    [pscustomobject]@{InstanceId='USB\VID_0FD9&PID_0080\fixture';FriendlyName='Fallback name'}
}
function Get-PnpDeviceProperty {
    [CmdletBinding()]
    param([string[]]$InstanceId, [string]$KeyName)
    if ($script:fixture.DescriptionThrows) { throw 'Authored missing optional PnP property' }
    foreach ($id in $InstanceId) { [pscustomobject]@{InstanceId=$id;Data='Fixture USB'} }
}
function Get-ReimsProtectedUsbIds {
    return ,[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
}
function Start-Sleep([int]$Milliseconds) { $script:fixture.Sleeps++ }
function Invoke-ReimsQmp($Runtime, [string]$Command, [hashtable]$Arguments=@{}) {
    $script:fixture.Queries++
    if (!$script:fixture.Running) { throw 'Unexpected QMP call while stopped' }
    switch ($Command) {
        human-monitor-command { return $script:fixture.Inventory }
        qom-list {
            foreach ($id in @($script:fixture.Pending.Keys)) {
                if ($script:fixture.Pending[$id] -eq 0) {
                    $script:fixture.Objects.Remove($id)
                    $script:fixture.Pending.Remove($id)
                    $script:fixture.Events.Add('removed')
                } else { $script:fixture.Pending[$id]-- }
            }
            foreach ($id in $script:fixture.Objects.Keys) { [pscustomobject]@{name=$id;type='child<usb-host>'} }
        }
        qom-get {
            $id=$Arguments.path.Split('/')[-1]
            if ($script:fixture.RemoveDuringGet) {
                $script:fixture.RemoveDuringGet=$false
                $script:fixture.Objects.Remove($id)
                throw 'Device disappeared during listing'
            }
            if ($script:fixture.GetFails) { throw 'Authored QOM read failure' }
            if ($Arguments.property -ne 'attached' -or !$script:fixture.Objects.ContainsKey($id)) { throw 'Unknown USB property' }
            return [bool]$script:fixture.Objects[$id]
        }
        device_add {
            if ($script:fixture.AddFails) { throw 'Authored USB add failure' }
            if ($script:fixture.Objects.ContainsKey($Arguments.id)) { throw 'Duplicate live USB ID' }
            $script:fixture.Objects[$Arguments.id]=$script:fixture.AutoAttach
            $script:fixture.Adds++
            $script:fixture.Events.Add('added')
        }
        device_del {
            if ($script:fixture.DeleteFails) { throw 'Authored USB delete failure' }
            if (!$script:fixture.Objects.ContainsKey($Arguments.id)) { throw 'Unknown live USB ID' }
            $script:fixture.Pending[$Arguments.id]=$script:fixture.DeleteDelay
            $script:fixture.Deletes++
            $script:fixture.Events.Add('delete-requested')
        }
        default { throw "Unexpected QMP: $Command" }
    }
}

$id='hostusb-2-11_2-0fd90080'
try {
    Reset-Fixture
    $script:fixture.DescriptionThrows=$true
    $rows=@(Get-ReimsUsbDevices)
    Assert ($rows.Count -eq 1 -and $rows[0].label -match 'Fallback name') 'Missing optional description must not hide available USB devices'
    Assert ($rows[0].present -and !$rows[0].selected -and !$rows[0].active -and !$rows[0].attached) 'Inventory must not imply guest attachment'
    Reject { Get-ReimsPnpProperties @('fixture') DEVPKEY_Device_Parent } 'missing optional PnP property'

    # QEMU reports successful realization even if libusb cannot open a device.
    $script:fixture.AutoAttach=$false
    $result=Set-ReimsUsbSelection $id $true
    Assert ($result -match '接続は未完了' -and $script:fixture.Sleeps -eq 11) 'Unattached watchers must report pending after a bounded wait'
    $rows=@(Get-ReimsUsbDevices)
    Assert ($rows[0].selected -and $rows[0].active -and !$rows[0].attached) 'Saved and attached states must be separate'
    Assert (@(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 1) 'Pending automatic connection must survive a Manager restart'
    Assert ((Get-ReimsVmConfig).fixture_marker -eq 'retain') 'USB saves must preserve unrelated VM configuration'

    # The pinned QEMU stops auto-open attempts after three errors. A user retry
    # must recreate its watcher without requiring unplugging or a VM restart.
    $script:fixture.AutoAttach=$true
    Set-ReimsUsbSelection $id $true | Out-Null
    Assert ($script:fixture.Adds -eq 2 -and $script:fixture.Deletes -eq 1) 'Retry must reset a disconnected live watcher'
    Assert ((@(Get-ReimsUsbDevices))[0].attached) 'Successful retry must report actual guest attachment'
    Assert (@(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 1) 'Retry must not duplicate saved selection'
    Set-ReimsUsbSelection $id $true | Out-Null
    Assert ($script:fixture.Adds -eq 2 -and $script:fixture.Deletes -eq 1) 'Already attached retry must be idempotent'

    $script:fixture.Objects.Clear()
    Set-ReimsUsbSelection $id $true | Out-Null
    Assert ($script:fixture.Adds -eq 3 -and $script:fixture.Objects[$id]) 'A saved selection with a missing watcher must reconnect'
    $script:fixture.Inventory=''
    $row=@(Get-ReimsUsbDevices)[0]
    Assert ($row.selected -and !$row.present -and $row.attached -and $row.label -match '接続中') 'A redirected device missing from host inventory must remain visibly connected'

    $script:fixture.DeleteDelay=3
    $script:fixture.Events.Clear()
    Set-ReimsUsbSelection $id $false | Out-Null
    Assert (($script:fixture.Events -join ',') -eq 'delete-requested,removed,save') 'Unregister must wait for QEMU deletion before saving'
    Assert (!$script:fixture.Objects.Count -and @(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 0) 'Completed removal must clear both live and saved state'

    Reset-Fixture
    Set-ReimsUsbSelection $id $true | Out-Null
    $script:fixture.DeleteDelay=100
    $script:fixture.Events.Clear()
    $before=$script:fixture.Queries
    Reject { Set-ReimsUsbSelection $id $false } '切断完了を確認できません'
    Assert ($script:fixture.Objects.ContainsKey($id) -and @(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 1) 'Incomplete deletion must not lose saved registration'
    Assert (!($script:fixture.Events -contains 'save') -and $script:fixture.Queries-$before -le 24) 'Deletion verification must be bounded and must not commit prematurely'

    Reset-Fixture
    $script:fixture.WriteFails=$true
    Reject { Set-ReimsUsbSelection $id $true } 'Authored settings save failure'
    Assert (!$script:fixture.Objects.Count -and @(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 0) 'Failed registration save must undo the new live watcher'

    # If both save and rollback fail, surface the actual partial outcome and keep
    # the orphan discoverable so a later Disconnect can repair it.
    $script:fixture.DeleteFails=$true
    Reject { Set-ReimsUsbSelection $id $true } '保存: Authored settings save failure / 解除: Authored USB delete failure'
    $script:fixture.Inventory=''
    $row=@(Get-ReimsUsbDevices)[0]
    Assert (!$row.selected -and $row.active -and $row.attached -and !$row.present) 'An unsaved live orphan must remain visible after unplug'
    $script:fixture.DeleteFails=$false
    Set-ReimsUsbSelection $id $false | Out-Null
    Assert (!$script:fixture.Objects.Count) 'An unsaved live orphan must be removable without writing settings'

    Reset-Fixture
    Set-ReimsUsbSelection $id $true | Out-Null
    $script:fixture.WriteFails=$true
    Reject { Set-ReimsUsbSelection $id $false } '自動接続を復元しました: Authored settings save failure'
    Assert ($script:fixture.Objects.ContainsKey($id) -and @(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 1) 'Failed removal save must restore the live watcher and preserve registration'
    $script:fixture.AddFails=$true
    Reject { Set-ReimsUsbSelection $id $false } '保存: Authored settings save failure / 復元: Authored USB add failure'
    Assert (!$script:fixture.Objects.ContainsKey($id) -and @(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 1) 'Failed compensation must retain saved state for retry'
    $script:fixture.WriteFails=$false; $script:fixture.AddFails=$false
    Set-ReimsUsbSelection $id $true | Out-Null
    Assert ($script:fixture.Objects.ContainsKey($id)) 'A partial removal must be repairable with Retry'

    $script:fixture.RemoveDuringGet=$true
    $row=@(Get-ReimsUsbDevices)[0]
    Assert ($row.selected -and !$row.active -and !$row.attached) 'Concurrent deletion during list must not show a stale connected state'
    Set-ReimsUsbSelection $id $true | Out-Null
    $script:fixture.GetFails=$true
    Reject { Get-ReimsUsbDevices } 'Authored QOM read failure'
    $script:fixture.GetFails=$false

    # libusb's WinUSB and UsbDk backends assign different bus/port numbers.
    $config=Get-ReimsVmConfig
    Assert-ReimsUsbBackendChange $config winusb
    Reject { Assert-ReimsUsbBackendChange $config usbdk } '登録済み USB の自動接続を解除'
    $config | Add-Member -Force usb_backend usbdk
    Assert-ReimsUsbBackendChange $config usbdk
    Reject { Assert-ReimsUsbBackendChange $config winusb } '登録済み USB の自動接続を解除'
    $config.usb_devices=@()
    Assert-ReimsUsbBackendChange $config winusb
    Assert-ReimsUsbBackendChange $config usbdk

    $script:fixture.Running=$false
    $before=$script:fixture.Queries
    $row=@(Get-ReimsUsbDevices)[0]
    Assert ($row.selected -and !$row.active -and !$row.attached) 'Stopped VM must still list its saved selection'
    Reject { Set-ReimsUsbSelection $id $true } 'VM を起動してください'
    Set-ReimsUsbSelection $id $false | Out-Null
    Assert (@(Get-ReimsUsbSelections (Get-ReimsVmConfig)).Count -eq 0 -and $script:fixture.Queries -eq $before) 'Stopped unregister must not contact QMP'

    Reset-Fixture
    $script:fixture.Running=$false;$script:fixture.DescriptionThrows=$true
    $rows=@(Get-ReimsUsbDevices)
    Assert ($rows.Count -eq 1 -and $rows[0].present -and !$rows[0].selected -and $rows[0].label -match 'Fallback name') 'Stopped host inventory lost optional PnP fallback'
    Assert ($script:fixture.Queries -eq 0) 'Stopped inventory queried a recorded VM'

    # The real bounded stdio protocol runs against an authored, device-free
    # executable. It never invokes QEMU, USB enumeration, drivers or a VM.
    $probeDirectory=Join-Path $VmRoot 'probe'
    [void][IO.Directory]::CreateDirectory($probeDirectory)
    $builder=@'
param([string]$Output)
$ErrorActionPreference='Stop'
Add-Type -OutputAssembly $Output -OutputType ConsoleApplication -TypeDefinition @"
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
public static class UsbProbeFixture {
    public static void Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);
        using(var self=Process.GetCurrentProcess()) {
            File.WriteAllText(Environment.GetEnvironmentVariable("REIMS_USB_FIXTURE_PID"),self.Id+" "+self.StartTime.ToUniversalTime().Ticks);
        }
        if(String.Join("|",args)!="-machine|none|-nodefaults|-display|none|-S|-monitor|none|-qmp|stdio") { Environment.Exit(9); }
        var expected=Environment.GetEnvironmentVariable("REIMS_USB_FIXTURE_BACKEND");
        var actual=Environment.GetEnvironmentVariable("REIMS_USB_BACKEND")??"absent";
        if(actual!=expected) { Environment.Exit(10); }
        var mode=Environment.GetEnvironmentVariable("REIMS_USB_FIXTURE_MODE");
        if(mode=="timeout") { Thread.Sleep(30000);return; }
        if(mode=="early-exit") { Console.Error.WriteLine("fixture native dependency failure");Environment.Exit(7); }
        if(mode=="stderr-size") { Console.Error.Write(new String('e',20000));Console.Error.Flush();Thread.Sleep(30000);return; }
        if(mode=="line-size") { Console.WriteLine(new String('x',270000));Console.Out.Flush();Thread.Sleep(30000);return; }
        Console.WriteLine(mode=="bad-json"?"{not-json}":"{\"QMP\":{}}");Console.Out.Flush();
        if(Console.ReadLine().IndexOf("qmp_capabilities",StringComparison.Ordinal)<0) { Environment.Exit(11); }
        if(mode=="qmp-error") { Console.WriteLine("{\"id\":\"usb-caps\",\"error\":{\"class\":\"GenericError\",\"desc\":\"fixture failure\"}}");Console.Out.Flush();Thread.Sleep(30000);return; }
        Console.WriteLine("{\"event\":\"FIXTURE_EVENT\"}");
        Console.WriteLine("{\"id\":\"usb-caps\",\"return\":{}}");Console.Out.Flush();
        var request=Console.ReadLine();
        if(!request.Contains("human-monitor-command")||!request.Contains("info usbhost")) { Environment.Exit(12); }
        Console.WriteLine(mode=="result-type"?"{\"id\":\"usb-inventory\",\"return\":42}":"{\"id\":\"usb-inventory\",\"return\":\"日本語 Fixture USB\"}");Console.Out.Flush();
        var quit=Console.ReadLine();
        if(!quit.Contains("quit")) { Environment.Exit(13); }
        if(mode=="exit-timeout") { Thread.Sleep(30000); }
    }
}
"@
'@
    $buildScript=Join-Path $probeDirectory 'build.ps1';$probeExe=Join-Path $probeDirectory 'fixture.exe'
    [IO.File]::WriteAllText($buildScript,$builder,[Text.UTF8Encoding]::new($true))
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $buildScript -Output $probeExe
    if($LASTEXITCODE){throw 'Authored USB protocol fixture failed to compile.'}
    function Get-ReimsTool([string]$Name){if($Name -cne 'qemu-system-x86_64.exe'){throw 'Wrong probe tool'};return $probeExe}
    $variables=@('REIMS_USB_BACKEND','REIMS_USB_FIXTURE_PID','REIMS_USB_FIXTURE_MODE','REIMS_USB_FIXTURE_BACKEND')
    $environmentBefore=@{};foreach($name in $variables){$environmentBefore[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
    try {
        $env:REIMS_USB_FIXTURE_PID=Join-Path $probeDirectory 'pid.txt'
        $env:REIMS_USB_FIXTURE_MODE='success'
        $env:REIMS_USB_BACKEND='parent-environment-must-stay'
        foreach($backend in @('winusb','usbdk')){
            $env:REIMS_USB_FIXTURE_BACKEND=if($backend -eq 'usbdk'){'usbdk'}else{'absent'}
            $text=& $stoppedProbe ([pscustomobject]@{usb_backend=$backend}) -TimeoutMilliseconds 3000
            Assert ($text -ceq '日本語 Fixture USB') 'Offline QMP UTF8 text was not preserved'
            Assert ($env:REIMS_USB_BACKEND -ceq 'parent-environment-must-stay') 'Probe mutated the parent USB backend'
        }
        $env:REIMS_USB_FIXTURE_BACKEND='absent'
        Assert ((& $stoppedProbe ([pscustomobject]@{}) -TimeoutMilliseconds 3000) -ceq '日本語 Fixture USB') 'Missing saved backend did not preserve WinUSB default'
        foreach($case in @(@('timeout','timed out'),@('early-exit','closed QMP'),@('stderr-size','stderr exceeds'),@('line-size','line exceeds'),@('bad-json','JSON'),@('qmp-error','fixture failure'),@('result-type','not text'),@('exit-timeout','timed out during exit'))){
            $env:REIMS_USB_FIXTURE_MODE=$case[0]
            $watch=[Diagnostics.Stopwatch]::StartNew()
            Reject { & $stoppedProbe ([pscustomobject]@{usb_backend='winusb'}) -TimeoutMilliseconds 1200 } $case[1]
            Assert ($watch.Elapsed.TotalSeconds -lt 5) 'Offline probe failure was not globally bounded'
            $identity=([IO.File]::ReadAllText($env:REIMS_USB_FIXTURE_PID)).Split(' ')
            $owned=Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
            if($owned){try{Assert ($owned.StartTime.ToUniversalTime().Ticks -ne [long]$identity[1] -or $owned.HasExited) 'Owned inventory process survived failure'}finally{$owned.Dispose()}}
            else{Assert $true 'Owned inventory process exited'}
        }
        Reject { & $stoppedProbe ([pscustomobject]@{usb_backend=@('usbdk')}) } 'Invalid saved USB backend'
        Reject { & $stoppedProbe ([pscustomobject]@{usb_backend='unsupported'}) } 'Invalid saved USB backend'
    } finally {
        foreach($name in $variables){[Environment]::SetEnvironmentVariable($name,$environmentBefore[$name],'Process')}
    }

    "$script:checks USB reliability checks passed."
} finally {
    $allowed=[IO.Path]::GetFullPath((Join-Path $ProjectRoot '.work/tmp'))+[IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($VmRoot).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $VmRoot -Recurse -Force
    }
}
