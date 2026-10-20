#Requires -Version 7.2
# Real helper/function bodies with synthetic payloads and a function replacing ssh.exe.
$ErrorActionPreference='Stop'
$testParent=Join-Path ([IO.Path]::GetTempPath()) 'reims-cursor-aid-tests'
$work=Join-Path $testParent ([guid]::NewGuid().ToString('N'))
$oldTools=$env:REIMS_MANAGER_TOOLS_ROOT
[void][IO.Directory]::CreateDirectory($work)
function Assert-True($Value,[string]$Message){if(!$Value){throw $Message}}
function Reject([scriptblock]$Call){$failed=$false;try{& $Call | Out-Null}catch{$failed=$true};Assert-True $failed 'Expected cursor aid rejection'}
try {
    $env:REIMS_MANAGER_TOOLS_ROOT=Join-Path $work 'runtimes'
    $bundle=Join-Path $env:REIMS_MANAGER_TOOLS_ROOT 'cursor-aid'
    [void][IO.Directory]::CreateDirectory($bundle)
    $bytes=[Text.Encoding]::UTF8.GetBytes('authored fixture, never executed')
    [IO.File]::WriteAllBytes((Join-Path $bundle 'ReimsCursorAid'),$bytes)
    $manifest=@{schema=1;executable='ReimsCursorAid';sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant();revision=('a'*40)}
    $manifestPath=Join-Path $bundle 'manifest.json'
    function Save-Manifest { [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Compress)) }
    Save-Manifest
    $script:Calls=0;$script:Reply='{"schema":1,"enabled":true,"pid":123}'
    function Invoke-GuestControl($Command,$InputJson){
        $script:Calls++
        Assert-True ($Command -match "^/usr/bin/env python3 -c 'import base64;exec\(compile\(base64.b64decode\(`"([A-Za-z0-9+/=]+)`"\)") 'Unexpected guest command'
        $source=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
        Assert-True ($source -eq [IO.File]::ReadAllText("$PSScriptRoot/../guest-cursor-aid.py")) 'Installer is not the installed fixed source'
        $script:Request=$InputJson|ConvertFrom-Json -AsHashtable
        $script:Reply
    }
    . "$PSScriptRoot/../cursor-aid.ps1"
    Invoke-ReimsCursorAid enable | Out-Null
    Assert-True ($script:Calls -eq 1 -and $script:Request.operation -ceq 'enable') 'Enable request mismatch'
    Assert-True ([Convert]::FromBase64String($script:Request.payload).Length -eq $bytes.Length) 'Payload transport mismatch'
    foreach($field in @('schema','executable','sha256','revision')){
        $before=$manifest[$field]
        $manifest[$field]=switch($field){schema{$true} executable{'../foreign'} sha256{('A'*64)} revision{'wrong'}}
        Save-Manifest
        Reject {Invoke-ReimsCursorAid enable}
        $manifest[$field]=$before
    }
    Save-Manifest
    [IO.File]::WriteAllText((Join-Path $bundle 'ReimsCursorAid'),'tampered')
    Reject {Invoke-ReimsCursorAid enable}
    Assert-True ($script:Calls -eq 1) 'Invalid bundle reached SSH'
    [IO.File]::WriteAllBytes((Join-Path $bundle 'ReimsCursorAid'),$bytes)
    foreach($reply in @('{"schema":true,"enabled":true,"pid":123}','{"schema":1,"enabled":"true","pid":123}','{"schema":1,"enabled":true,"pid":0}','{"schema":1,"enabled":true,"pid":true}')){
        $script:Reply=$reply
        Reject {Invoke-ReimsCursorAid enable}
    }
    $script:Reply='{"schema":1,"enabled":false,"pid":null}'
    $env:REIMS_MANAGER_TOOLS_ROOT=''
    Invoke-ReimsCursorAid disable | Out-Null
    Assert-True ($script:Request.operation -ceq 'disable' -and !$script:Request.Contains('payload')) 'Disable required bundle or sent payload'

    $env:REIMS_MANAGER_TOOLS_ROOT=Join-Path $work 'runtimes'
    foreach($target in @('main','extended')){
        $script:Reply=@{schema=1;enabled=$true;pid=123;move=@{schema=1;action='move-to-display';target=$target;ok=$true;cgError=0;displayID=22;candidateCount=1;boundsCG=@{x=0;y=0;width=1600;height=900};positionCG=@{x=800;y=450};error=$null}}|ConvertTo-Json -Depth 5 -Compress
        Invoke-ReimsCursorAid "move-$target" | Out-Null
        Assert-True ($script:Request.operation -ceq "move-$target" -and $script:Request.Contains('manifest') -and $script:Request.Contains('payload')) 'Move skipped verified enable payload'
    }
    $script:Reply='{"schema":1,"enabled":true,"pid":123,"move":{"action":"move-to-display","target":"extended","ok":false,"cgError":0}}'
    Reject {Invoke-ReimsCursorAid move-extended}
    $script:Reply='{"schema":1,"enabled":true,"pid":123,"move":{"action":"move-to-display","target":"main","ok":true,"cgError":0}}'
    Reject {Invoke-ReimsCursorAid move-extended}

    # Extract the actual private function; replace only its fixed executable for an offline argv/stdin spy.
    $ast=[scriptblock]::Create((Get-Content -Raw "$PSScriptRoot/../manager-backend.ps1")).Ast
    foreach($name in @('Select-ReimsGuestScopedAddress','Get-GuestEndpoint')){
        $definition=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    function Fake-Interface([string]$Id,[int]$Index){
        $properties=[pscustomobject]@{FixtureIndex=$Index}
        $properties|Add-Member ScriptMethod GetIPv6Properties {[pscustomobject]@{Index=$this.FixtureIndex}}
        $nic=[pscustomobject]@{Id=$Id;FixtureProperties=$properties}
        $nic|Add-Member ScriptMethod GetIPProperties {$this.FixtureProperties}
        return $nic
    }
    $tapId=[guid]'11111111-1111-4111-8111-111111111111'
    $script:EndpointInterfaces=@((Fake-Interface $tapId.ToString('B') 73),(Fake-Interface '22222222-2222-4222-8222-222222222222' 74))
    $script:EndpointAddresses=@('192.0.2.10','2001:db8::1','fe80::42%74','fe80::42%73','FE80::42%73')|ForEach-Object {[Net.IPAddress]::Parse($_)}
    Assert-True ((Select-ReimsGuestScopedAddress $script:EndpointAddresses $tapId $script:EndpointInterfaces) -ceq 'fe80::42%73') 'Distinct same-TAP scope was not preferred'
    $foreignOnly=@([Net.IPAddress]::Parse('fe80::42%74'),[Net.IPAddress]::Parse('fe80::42'))
    Assert-True ($null -eq (Select-ReimsGuestScopedAddress $foreignOnly $tapId $script:EndpointInterfaces)) 'Foreign or unscoped link-local address was adopted'
    Reject {Select-ReimsGuestScopedAddress @([Net.IPAddress]::Parse('fe80::42%73'),[Net.IPAddress]::Parse('fe80::43%73')) $tapId $script:EndpointInterfaces}
    Reject {Select-ReimsGuestScopedAddress $script:EndpointAddresses $tapId @()}
    Reject {Select-ReimsGuestScopedAddress $script:EndpointAddresses $tapId @($script:EndpointInterfaces[0],$script:EndpointInterfaces[0])}
    Reject {Select-ReimsGuestScopedAddress $script:EndpointAddresses $tapId @((Fake-Interface $tapId.ToString('D') 0))}
    function Get-ReimsNetworkMode($Config){$Config.network.mode}
    $script:ScopedCalls=0
    function Resolve-ReimsGuestScopedAddress($Name,$Tap){
        $script:ScopedCalls++
        Assert-True ($Name -ceq 'fixture.invalid' -and $Tap -eq $tapId) 'Scoped resolver received another configured host/TAP'
        Select-ReimsGuestScopedAddress $script:EndpointAddresses $Tap $script:EndpointInterfaces
    }
    $endpointConfig=[pscustomobject]@{ssh_port=2222;network=[pscustomobject]@{mode='bridge';forwarder='macnat';tap_id=$tapId.ToString('B');guest_host='fixture.invalid'}}
    Assert-True ((Get-GuestEndpoint $endpointConfig).host -ceq 'fe80::42%73') 'macnat endpoint did not use the scoped result'
    $endpointConfig.network.forwarder='macnat-ipv4'
    Assert-True ((Get-GuestEndpoint $endpointConfig).host -ceq 'fe80::42%73') 'Legacy macnat endpoint policy differs'
    $savedAddresses=$script:EndpointAddresses;$script:EndpointAddresses=$foreignOnly
    Assert-True ((Get-GuestEndpoint $endpointConfig).host -ceq 'fixture.invalid') 'No matching LL did not retain configured host'
    $script:EndpointAddresses=$savedAddresses
    $before=$script:ScopedCalls
    $endpointConfig.network.forwarder='windows-bridge'
    Assert-True ((Get-GuestEndpoint $endpointConfig).host -ceq 'fixture.invalid') 'Wired host changed'
    $endpointConfig.network.forwarder='macnat';$endpointConfig.network.tap_id='invalid-guid'
    Assert-True ((Get-GuestEndpoint $endpointConfig).host -ceq 'fixture.invalid') 'Invalid TAP was used for scoped lookup'
    $endpointConfig.network.tap_id=@($tapId.ToString('B'))
    Assert-True ((Get-GuestEndpoint $endpointConfig).host -ceq 'fixture.invalid') 'Untyped TAP was used for scoped lookup'
    $endpointConfig.network.tap_id=$tapId.ToString('B')
    $endpointConfig.network.mode='unknown'
    Assert-True ((Get-GuestEndpoint $endpointConfig).host -ceq 'fixture.invalid') 'Non-bridge mode was used for scoped lookup'
    $endpointConfig.network.mode='bridge'
    foreach($literal in @('192.0.2.10','2001:db8::1','fe80::42')){
        $endpointConfig.network.guest_host=$literal
        Assert-True ((Get-GuestEndpoint $endpointConfig).host -ceq $literal) 'Literal endpoint was changed'
    }
    $endpointConfig.network.guest_host='fixture.invalid';$endpointConfig.network.mode='nat'
    $nat=Get-GuestEndpoint $endpointConfig
    Assert-True ($nat.host -ceq '127.0.0.1' -and $nat.port -eq 2222 -and $script:ScopedCalls -eq $before) 'NAT/wired/literal performed DNS or changed endpoint'
    $endpointConfig.network.mode='bridge'
    $body=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-GuestControl'},$true).Extent.Text
    $body=$body.Replace('$ssh = "$env:SystemRoot/System32/OpenSSH/ssh.exe"', '$ssh = ''Test-ReimsCursorSsh''')
    . ([scriptblock]::Create($body))
    $VmRoot=Join-Path $work 'Virtual Machines/日本語 # space'
    [void][IO.Directory]::CreateDirectory((Join-Path $VmRoot 'state/ssh'))
    [IO.File]::WriteAllText((Join-Path $VmRoot 'state/ssh/id_ed25519'),'synthetic key')
    [IO.File]::WriteAllText((Join-Path $VmRoot 'state/known_hosts'),'synthetic host')
    [IO.File]::WriteAllText((Join-Path $VmRoot 'state/guest.json'),'{"user":"fixture","host_key_alias":"reims-fixture"}')
    function Get-ReimsRuntime { @{pid=123} }
    function Get-ReimsVmProcess($Runtime) { $script:Active }
    function Invoke-ReimsQmp($Runtime,$Action) { @{status=$script:State} }
    function Get-ReimsVmConfig { @{ssh_port=2222} }
    function Get-GuestEndpoint($Config) { @{host='fixture.invalid';port=$Config.ssh_port} }
    function Get-ReimsSshKnownHostsOption($Path) { 'UserKnownHostsFile="'+$Path.Replace('\','/')+'"' }
    function Test-ReimsCursorSsh {
        $script:SshArguments=@($args);$script:SshInput=@($input);$script:SSHCalls++
        $global:LASTEXITCODE=$script:SshExit
        'fixture stdout'
    }
    $script:Active=$true;$script:State='running';$script:SshExit=0;$script:SSHCalls=0
    Invoke-GuestControl -Command 'fixed command' | Out-Null
    Assert-True ('-n' -cin $script:SshArguments -and '-T' -cnotin $script:SshArguments -and $script:SshInput.Count -eq 0) 'Existing command stdin behavior changed'
    Invoke-GuestControl -Command 'fixed command' -InputJson '{"typed":"日本語"}' | Out-Null
    Assert-True ('-n' -cnotin $script:SshArguments -and '-T' -cin $script:SshArguments -and $script:SshInput.Count -eq 1 -and $script:SshInput[0] -ceq '{"typed":"日本語"}') 'Bounded stdin did not reach SSH intact'
    foreach($required in @('StrictHostKeyChecking=yes','BatchMode=yes','IdentitiesOnly=yes','HostKeyAlias=reims-fixture','fixture@fixture.invalid')){Assert-True ($required -cin $script:SshArguments) 'Strict paired SSH configuration changed'}
    Assert-True (($script:SshArguments|Where-Object{$_ -like 'UserKnownHostsFile=*'}) -ceq (Get-ReimsSshKnownHostsOption (Join-Path $VmRoot 'state/known_hosts'))) 'Quoted host path changed'
    # Reuse the actual endpoint function in the strict SSH argv fixture too.
    $endpointBody=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-GuestEndpoint'},$true).Extent.Text
    . ([scriptblock]::Create($endpointBody))
    function Get-ReimsVmConfig {$endpointConfig}
    Invoke-GuestControl -Command 'fixed command' | Out-Null
    foreach($required in @('StrictHostKeyChecking=yes','BatchMode=yes','IdentitiesOnly=yes','HostKeyAlias=reims-fixture','fixture@fe80::42%73')){Assert-True ($required -cin $script:SshArguments) 'Scoped endpoint changed paired SSH authentication'}
    Invoke-GuestControl '-r' | Out-Null
    Assert-True ('-n' -cin $script:SshArguments -and $script:SshArguments[-1] -ceq 'sudo -n /sbin/shutdown -r now') 'Restart caller changed'
    $count=$script:SSHCalls
    foreach($state in @('paused','shutdown')){$script:State=$state;Reject {Invoke-GuestControl -Command 'fixed' -InputJson '{}'}}
    $script:State='running';$script:Active=$false
    Reject {Invoke-GuestControl -Command 'fixed' -InputJson '{}'}
    Assert-True ($script:SSHCalls -eq $count) 'Non-running VM reached SSH'
    $script:Active=$true;$script:SshExit=9
    Reject {Invoke-GuestControl -Command 'fixed' -InputJson '{}'}
    'PASS cursor aid payload/move, GUID+scope endpoint selection/deduplication/refusals/fallback, strict SSH alias/path, stdin/legacy -n, runtime guards and SSH failure; no DNS, NIC or guest query.'
} finally {
    $env:REIMS_MANAGER_TOOLS_ROOT=$oldTools
    $prefix=[IO.Path]::GetFullPath($testParent)+[IO.Path]::DirectorySeparatorChar
    if($work.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $work -Recurse -Force}
}
