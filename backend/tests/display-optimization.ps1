#Requires -Version 7.2
$ErrorActionPreference='Stop'
function Assert-True($Value,$Message){if(!$Value){throw $Message}}
function Reject([scriptblock]$Call){$failed=$false;try{& $Call|Out-Null}catch{$failed=$true};Assert-True $failed 'Expected display protocol rejection'}
$script:Calls=0
function Invoke-GuestControl($Command,$InputJson){
    $script:Calls++
    Assert-True ($Command -match "^/usr/bin/env python3 -c 'import base64;exec\(compile\(base64.b64decode\(`"([A-Za-z0-9+/=]+)`"\)") 'Unexpected fixed display command'
    $source=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    Assert-True ($source -ceq [IO.File]::ReadAllText("$PSScriptRoot/../guest-display.py")) 'Display source did not come from installed Manager script'
    $script:Request=$InputJson|ConvertFrom-Json -AsHashtable
    Assert-True ($script:Request.Count -eq 2 -and $script:Request.schema -eq 1 -and $script:Request.operation -in @('lighten','restore')) 'Request is not a fixed display operation'
    if($script:FailSsh){throw 'fixture SSH failure'}
    $script:Reply
}
. "$PSScriptRoot/../display-optimization.ps1"
$script:Reply='{"schema":1,"operation":"lighten","applied":true,"main_unchanged":true,"display_id":20}'
Invoke-ReimsDisplayOptimization lighten | Out-Null
Assert-True ($script:Request.operation -ceq 'lighten') 'Lighten dispatch mismatch'
$script:Reply='{"schema":1,"operation":"restore","applied":false,"main_unchanged":true,"display_id":20}'
Invoke-ReimsDisplayOptimization restore | Out-Null
Assert-True ($script:Request.operation -ceq 'restore') 'Restore dispatch mismatch'
foreach($json in @(
    '{"schema":true,"operation":"lighten","applied":true,"main_unchanged":true,"display_id":20}',
    '{"schema":1,"operation":"lighten","applied":"true","main_unchanged":true,"display_id":20}',
    '{"schema":1,"operation":"lighten","applied":true,"main_unchanged":false,"display_id":20}',
    '{"schema":1,"operation":"restore","applied":false,"main_unchanged":true,"display_id":20}',
    '{"schema":1,"operation":"lighten","applied":true,"main_unchanged":true,"display_id":true}',
    '{"schema":1,"operation":"lighten","applied":true,"main_unchanged":true,"display_id":0}',
    '{"schema":1,"operation":"lighten","applied":true,"main_unchanged":true,"display_id":4294967296}',
    ('x'*4097)
)){$script:Reply=$json;Reject {Invoke-ReimsDisplayOptimization lighten}}
$script:FailSsh=$true
Reject {Invoke-ReimsDisplayOptimization lighten}
$before=$script:Calls
Reject {Invoke-ReimsDisplayOptimization 'arbitrary'}
Assert-True ($script:Calls -eq $before) 'Invalid operation reached SSH'
'PASS display fixed source/operation JSON, bounded typed replies, failed main-invariance proof, invalid IDs and SSH failure. No guest connection.'
