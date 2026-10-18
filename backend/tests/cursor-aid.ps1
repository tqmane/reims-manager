#Requires -Version 7.2
# Real helper/function bodies with synthetic payloads and a function replacing ssh.exe.
$ErrorActionPreference='Stop'
$work=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp/cursor-aid-"+[guid]::NewGuid().ToString('N'))
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

    # Extract the actual private function; replace only its fixed executable for an offline argv/stdin spy.
    $ast=[scriptblock]::Create((Get-Content -Raw "$PSScriptRoot/../manager-backend.ps1")).Ast
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
    Invoke-GuestControl '-r' | Out-Null
    Assert-True ('-n' -cin $script:SshArguments -and $script:SshArguments[-1] -ceq 'sudo -n /sbin/shutdown -r now') 'Restart caller changed'
    $count=$script:SSHCalls
    foreach($state in @('paused','shutdown')){$script:State=$state;Reject {Invoke-GuestControl -Command 'fixed' -InputJson '{}'}}
    $script:State='running';$script:Active=$false
    Reject {Invoke-GuestControl -Command 'fixed' -InputJson '{}'}
    Assert-True ($script:SSHCalls -eq $count) 'Non-running VM reached SSH'
    $script:Active=$true;$script:SshExit=9
    Reject {Invoke-GuestControl -Command 'fixed' -InputJson '{}'}
    'PASS cursor aid bundle validation, typed results, strict SSH alias/path, stdin/legacy -n, runtime guards and native SSH failure; no guest connection.'
} finally {
    $env:REIMS_MANAGER_TOOLS_ROOT=$oldTools
    $prefix=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if($work.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $work -Recurse -Force}
}
