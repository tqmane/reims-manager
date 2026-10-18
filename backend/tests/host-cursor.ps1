#Requires -Version 7.2
# No actual process, QMP socket, native window, VM or hardware is used.
$ErrorActionPreference='Stop'
$work=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp/host-cursor-"+[guid]::NewGuid().ToString('N'))
$VmRoot=Join-Path $work 'Virtual Machines/fixture'
[void][IO.Directory]::CreateDirectory((Join-Path $VmRoot 'state'))
$path=Join-Path $VmRoot 'state/host-cursor.json'
$script:PolicyPath=$path
$configPath=Join-Path $VmRoot 'state/config.json'
[IO.File]::WriteAllText($configPath,'{"schema":1,"usb_devices":[{"id":"keep"}],"network":{"mode":"nat"}}')
$configBefore=[IO.File]::ReadAllText($configPath)
function Assert-True($Value,$Message){if(!$Value){throw $Message}}
function Reject([scriptblock]$Call,[string]$Expected){
    $message=$null;try{& $Call | Out-Null}catch{$message=$_.Exception.Message}
    if(!$message -or ($Expected -and $message -notmatch $Expected)){throw "Expected '$Expected' rejection, got '$message'"}
}
function Get-ReimsRuntime { @{pid=123} }
function Get-ReimsVmProcess($Runtime) { $script:Alive }
function Invoke-ReimsQmp($Runtime,$Command,$Arguments) {
    $script:QmpCalls++
    if($Command -eq 'query-status'){return @{status=$script:State}}
    Assert-True ($Arguments.path -ceq '/machine/peripheral/gfx' -and $Arguments.property -ceq 'hide-host-cursor') 'Wrong QOM target'
    if($script:Unsupported){throw 'Property not found'}
    if($Command -eq 'qom-get'){
        if($script:BadType){return 'false'}
        return ,$script:Property
    }
    if($Command -ne 'qom-set' -or $Arguments.value -isnot [bool]){throw 'Invalid typed QMP request'}
    $script:SetCalls++
    if($script:RollbackFailure -and $Arguments.value -eq $false){throw 'Fixture rollback refused'}
    if($script:Refused){throw 'No owned window'}
    if(!$script:Frozen){$script:Property=$Arguments.value}
    return @{}
}
function Write-ReimsJson($Path,$Value) {
    [void][IO.Directory]::CreateDirectory((Split-Path $Path))
    if($script:WriteFailure){[IO.File]::WriteAllText($Path,'partial fixture');throw 'Fixture write failure'}
    [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Compress))
    if($script:ForeignWrite){[IO.File]::WriteAllText($script:PolicyPath,'{"schema":1,"host_cursor_hidden":false}');$script:ForeignWrite=$false}
}
. "$PSScriptRoot/../host-cursor.ps1"
function Reset-Fixture {
    $script:Alive=$true;$script:State='running';$script:Property=$false
    $script:Unsupported=$false;$script:BadType=$false;$script:Refused=$false;$script:Frozen=$false
    $script:WriteFailure=$false;$script:ForeignWrite=$false;$script:QmpCalls=0;$script:SetCalls=0
    $script:RollbackFailure=$false
    if(Test-Path $path){Remove-Item -LiteralPath $path -Force}
}
try {
    Reset-Fixture
    Assert-True (!(Get-ReimsHostCursorHidden)) 'Missing policy is not false'
    $script:Unsupported=$true
    Reject {Set-ReimsHostCursor $true} 'Update the engine'
    Assert-True (!(Test-Path $path) -and $script:SetCalls -eq 0) 'Old engine created a fake saved state'

    Reset-Fixture
    Set-ReimsHostCursor $true | Out-Null
    Assert-True ((Get-ReimsHostCursorHidden) -and $script:Property) 'Hide did not verify/save true'
    foreach($state in @('running','paused','shutdown','prelaunch','suspended')){
        $script:State=$state
        Set-ReimsHostCursor $false | Out-Null
        Assert-True (!(Get-ReimsHostCursorHidden) -and !$script:Property) 'Show did not verify/save false'
    }
    Reject {Set-ReimsHostCursor $true} 'requires a running'

    Reset-Fixture
    $script:Refused=$true
    Reject {Set-ReimsHostCursor $true} 'live Reims-owned'
    Assert-True (!(Test-Path $path) -and !$script:Property) 'Refused window policy persisted'

    Reset-Fixture
    $script:BadType=$true
    Reject {Set-ReimsHostCursor $true} 'non-boolean'
    Assert-True (!(Test-Path $path)) 'Malformed live bool was saved'

    Reset-Fixture
    $script:WriteFailure=$true
    Reject {Set-ReimsHostCursor $true} 'Fixture write failure'
    Assert-True (!$script:Property -and !(Test-Path $path) -and $script:SetCalls -eq 2) 'Failed persistence did not restore only owned live policy'
    Assert-True (@(Get-ChildItem (Split-Path $path) -Filter '.host-cursor-*').Count -eq 0) 'Owned staging file leaked'

    Reset-Fixture
    $script:WriteFailure=$true;$script:RollbackFailure=$true
    Reject {Set-ReimsHostCursor $true} 'Rollback unconfirmed'
    Assert-True ($script:Property -and !(Test-Path $path)) 'Failed rollback was falsely saved or claimed restored'

    Reset-Fixture
    $script:ForeignWrite=$true
    Reject {Set-ReimsHostCursor $true} 'changed concurrently'
    Assert-True (!$script:Property -and !(Get-ReimsHostCursorHidden)) 'Concurrent policy was clobbered or live policy not restored'

    Reset-Fixture
    $script:Frozen=$true
    Reject {Set-ReimsHostCursor $true} 'did not confirm'
    Assert-True (!(Test-Path $path)) 'Failed readback saved a policy'

    Reset-Fixture
    $script:Alive=$false
    $malformed=[Text.Encoding]::UTF8.GetBytes('{"schema":1,"host_cursor_hidden":"false"}')
    [IO.File]::WriteAllBytes($path,$malformed)
    Reject {Get-ReimsHostCursorHidden} 'Invalid typed'
    Set-ReimsHostCursor $false | Out-Null
    Assert-True (!(Get-ReimsHostCursorHidden) -and $script:QmpCalls -eq 0) 'Stopped Show contacted old QEMU or failed to recover'
    $backup=@(Get-ChildItem (Split-Path $path) -Filter 'host-cursor.recovery-*.json')
    Assert-True ($backup.Count -eq 1 -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($backup[0].FullName)) -ceq [Convert]::ToBase64String($malformed)) 'Recovery backup was not byte exact'

    # Exercise the real launch argument function with unrelated platform helpers stubbed.
    $ast=[scriptblock]::Create((Get-Content -Raw "$PSScriptRoot/../start.ps1")).Ast
    $function=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ReimsQemuArguments'},$true)
    . ([scriptblock]::Create($function.Extent.Text))
    function ConvertTo-ReimsQemuPath($Path){$Path.Replace('\','/')}
    function Get-ReimsEnginePath { 'fixture-firmware' }
    function Get-ReimsNetworkArgument { 'user,id=net0' }
    function Get-ReimsUsbSelections { @() }
    $config=[pscustomobject]@{firmware_code='code';firmware_vars='vars';opencore='opencore';active_disk='disk';resolution='1920x1080';rom='rom';cpu='fixture';cpus=2;memory_mib=4096;mac='52:54:00:00:00:01';qmp_port=4444}
    function Gfx-Argument {
        $arguments=Get-ReimsQemuArguments $config $work -WithoutInstaller
        ($arguments|Where-Object{$_ -like '{"driver":"apple-gfx-pci"*'}|ConvertFrom-Json -AsHashtable)
    }
    Reset-Fixture
    Assert-True (!(Gfx-Argument).Contains('hide-host-cursor')) 'Missing default added unsupported boot property'
    [IO.File]::WriteAllText($path,'{"schema":1,"host_cursor_hidden":false}')
    Assert-True (!(Gfx-Argument).Contains('hide-host-cursor')) 'False added unsupported boot property'
    [IO.File]::WriteAllText($path,'{"schema":1,"host_cursor_hidden":true}')
    Assert-True ((Gfx-Argument)['hide-host-cursor'] -is [bool] -and (Gfx-Argument)['hide-host-cursor']) 'Opt-in property is not a true JSON bool'
    [IO.File]::WriteAllText($path,'{"schema":1,"host_cursor_hidden":"true"}')
    Reject {Gfx-Argument} 'Invalid typed'
    $recorded=@('-device','{"driver":"apple-gfx-pci","id":"gfx","resolution":"fixture","hide-host-cursor":true}','-m','4096M')
    $normalized=@(Remove-ReimsHostCursorLaunchPolicy $recorded)
    Assert-True ($normalized.Count -eq 4 -and $normalized[1] -ceq '{"driver":"apple-gfx-pci","id":"gfx","resolution":"fixture"}' -and $normalized[3] -ceq '4096M') 'Retained power-on normalization changed a non-policy argument'
    Assert-True ((@(Remove-ReimsHostCursorLaunchPolicy $normalized)|ConvertTo-Json -Compress) -ceq ($normalized|ConvertTo-Json -Compress)) 'Mutable launch-policy normalization is not stable'
    Reject {Remove-ReimsHostCursorLaunchPolicy @('-device','{"driver":"apple-gfx-pci","id":"gfx","hide-host-cursor":"true"}')} 'Invalid host cursor bool'
    Assert-True ([IO.File]::ReadAllText($configPath) -ceq $configBefore) 'Hardware/USB config changed'
    'PASS host cursor unsupported/refusal/typed QOM, live verification, owned rollback, concurrent policy guard, stopped malformed recovery and byte backup, unchanged hardware config, default/opt-in/invalid launch arguments. No real QMP/VM/GPU.'
} finally {
    $prefix=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if($work.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $work -Recurse -Force}
}
