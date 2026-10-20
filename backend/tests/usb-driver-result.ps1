#Requires -Version 7.2
# Authored installer/principal/MSI fixtures only. No elevation, network, or driver changes.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$work=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp/usb-driver-result-"+[guid]::NewGuid().ToString('N'))
$scripts=Join-Path $work 'backend'; $cache=Join-Path $work 'installer cache'
$caller=Join-Path $work 'caller profile/日本語 support'; $selected=Join-Path $work 'selected VM'
foreach ($path in @($scripts,$caller,$selected)) { [void][IO.Directory]::CreateDirectory($path) }
$fixture=@{Administrator=$true;ExitCode=0;HashValid=$true;SignatureValid=$true;PublisherValid=$true;Msis=0;Downloads=0;AdminChecks=0;VmChecks=0;VmRunning=$false;EnvLoads=0;ExpectedReceipt='';ReceiptLocked=$false;Dispatch=[Collections.Generic.List[object]]::new()}
$script:checks=0
function Assert($Condition,[string]$Message) { if (!$Condition) { throw $Message }; $script:checks++ }
function Reject([scriptblock]$Action,[string]$Pattern) {
    try { & $Action | Out-Null } catch {
        Assert ($_.Exception.Message -match $Pattern) "Expected '$Pattern'; got '$($_.Exception.Message)'"
        return
    }
    throw "Expected failure: $Pattern"
}
function New-Receipt([string]$Name) {
    $path=Join-Path $caller "$Name.json"
    [IO.File]::WriteAllBytes($path,[byte[]]@())
    return $path
}
function Get-FixtureUsbPrincipal {
    $fixture.AdminChecks++
    $principal=[pscustomobject]@{Administrator=$fixture.Administrator}
    $principal | Add-Member ScriptMethod IsInRole { param($Role) return $this.Administrator }
    return $principal
}
function Invoke-WebRequest($Uri,$OutFile) {
    $fixture.Downloads++
    if ($Uri -ne 'https://github.com/daynix/UsbDk/releases/download/v1.00-22/UsbDk_1.0.22_x64.msi') { throw 'Unexpected USB installer source' }
    [IO.File]::WriteAllText($OutFile,'authored MSI fixture; never executed')
}
function Get-FileHash($Path) {
    @{Hash=$(if ($fixture.HashValid) {'91F6F695E1E13C656024E6D3B55620BF08D8835EF05EE0496935BA6BB62466A5'} else {'authored wrong hash'})}
}
function Get-AuthenticodeSignature($Path) {
    @{Status=$(if ($fixture.SignatureValid) {'Valid'} else {'NotSigned'});SignerCertificate=@{Subject=$(if ($fixture.PublisherValid) {'CN=Fixture, O=Red Hat, Inc.'} else {'CN=Wrong publisher'})}}
}
function Start-Process($FilePath,$ArgumentList,$WindowStyle,[switch]$PassThru,[switch]$Wait) {
    if ($FilePath -ne "$env:SystemRoot/System32/msiexec.exe" -or $ArgumentList -notmatch '/qn /norestart' -or !$PassThru -or !$Wait) { throw 'Unexpected MSI invocation' }
    $fixture.Msis++
    if ($fixture.ExpectedReceipt) {
        $opened=$null
        try { $opened=[IO.File]::Open($fixture.ExpectedReceipt,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
        catch [IO.IOException] { $fixture.ReceiptLocked=$true }
        finally { if ($opened) { $opened.Dispose() } }
        Assert $fixture.ReceiptLocked 'Receipt must remain locked during MSI execution'
    }
    [pscustomobject]@{ExitCode=$fixture.ExitCode}
}

try {
    # Replace only the platform principal construction; execute the complete
    # production validation, installer checks, MSI result handling and receipt I/O.
    $source=[IO.File]::ReadAllText("$PSScriptRoot/../install-usbdk.ps1")
    $ast=[scriptblock]::Create($source).Ast
    $principal=@($ast.FindAll({param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$principal'
    },$true))
    Assert ($principal.Count -eq 1 -and $principal[0].Extent.Text -match 'WindowsPrincipal') 'Expected one production Windows principal construction'
    $source=$source.Replace($principal[0].Extent.Text,'$principal = Get-FixtureUsbPrincipal')
    [IO.File]::WriteAllText((Join-Path $scripts 'install-usbdk.ps1'),$source)
    [IO.File]::WriteAllText((Join-Path $scripts 'start.ps1'),@'
param([switch]$FunctionsOnly)
$fixture.EnvLoads++
$ReimsWorkRoot=Join-Path $fixture.Selected 'support'
function Enter-ReimsVmMutation { $null }
function Assert-ReimsVmStopped {
    $fixture.VmChecks++
    if ($fixture.VmRunning) { throw 'Fixture VM is still running' }
}
'@)
    $fixture.Selected=$selected

    $wrongExtension=Join-Path $caller 'receipt.txt'; [IO.File]::WriteAllBytes($wrongExtension,[byte[]]@())
    $nonempty=New-Receipt 'nonempty'; [IO.File]::WriteAllText($nonempty,'retain original')
    $directory=Join-Path $caller 'directory.json'; [void][IO.Directory]::CreateDirectory($directory)
    foreach ($path in @('', 'relative.json', (Join-Path $caller 'missing.json'), $wrongExtension, $nonempty, $directory)) {
        Reject { & "$scripts/install-usbdk.ps1" -CacheDirectory $cache -ResultPath $path } 'USB 導入結果の保存先'
    }
    Assert ($fixture.Msis -eq 0 -and $fixture.Downloads -eq 0 -and $fixture.AdminChecks -eq 0) 'Invalid receipt must fail before any elevated installer work'
    Assert ([IO.File]::ReadAllText($nonempty) -eq 'retain original') 'Nonempty receipt must not be overwritten'
    $junction=Join-Path $work 'redirected parent'
    New-Item -ItemType Junction -Path $junction -Target $caller | Out-Null
    $linkedReceipt=New-Receipt 'linked-parent'
    Reject { & "$scripts/install-usbdk.ps1" -CacheDirectory $cache -ResultPath (Join-Path $junction 'linked-parent.json') } 'リンクではない'
    Assert ((Get-Item -LiteralPath $linkedReceipt).Length -eq 0 -and $fixture.Msis -eq 0) 'Reparse parent must not receive a result or launch MSI'

    $locked=New-Receipt 'locked'
    $lock=[IO.File]::Open($locked,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { Reject { & "$scripts/install-usbdk.ps1" -CacheDirectory $cache -ResultPath $locked } '' }
    finally { $lock.Dispose() }
    Assert ($fixture.Msis -eq 0 -and $fixture.Downloads -eq 0) 'Caller-held receipt must not start installation'

    foreach ($code in @(0,3010)) {
        $path=New-Receipt "success-$code"
        $fixture.ExitCode=$code; $fixture.ExpectedReceipt=$path; $fixture.ReceiptLocked=$false
        $text=& "$scripts/install-usbdk.ps1" -CacheDirectory $cache -ResultPath $path
        $receipt=Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        Assert ($receipt.schema -eq 1 -and $receipt.installed -and $receipt.restart_required -eq ($code -eq 3010) -and $receipt.exit_code -eq $code) 'MSI success/reboot result must reach the exact caller receipt'
        Assert ([IO.File]::ReadAllBytes($path)[0] -eq 123) 'Receipt must be UTF-8 without BOM'
        Assert ($text -match $(if ($code -eq 3010) {'Windows を再起動'} else {'UsbDk を選んで保存'})) 'Existing CLI result text must remain available'
        # The caller can reopen/read/remove its receipt after the elevated script exits.
        $read=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $read.Dispose()
    }

    $path=New-Receipt 'failed-msi'; $fixture.ExpectedReceipt=$path; $fixture.ReceiptLocked=$false; $fixture.ExitCode=1603
    Reject { & "$scripts/install-usbdk.ps1" -CacheDirectory $cache -ResultPath $path } '失敗しました \(1603\)'
    Assert ((Get-Item -LiteralPath $path).Length -eq 0) 'Failed MSI must not publish installed=true'
    $fixture.ExpectedReceipt=''; $fixture.ExitCode=0
    foreach ($guard in @('Administrator','HashValid','SignatureValid','PublisherValid')) {
        $path=New-Receipt "guard-$guard"; $before=$fixture.Msis
        $fixture[$guard]=$false
        Reject { & "$scripts/install-usbdk.ps1" -CacheDirectory $cache -ResultPath $path } $(if ($guard -eq 'Administrator') {'管理者権限'} elseif ($guard -eq 'HashValid') {'検証済みファイル'} else {'署名を確認できません'})
        $fixture[$guard]=$true
        Assert ($fixture.Msis -eq $before -and (Get-Item -LiteralPath $path).Length -eq 0) 'Privilege/hash/signature failure must not start MSI or publish success'
    }

    $path=New-Receipt 'selected-vm'; $fixture.ExpectedReceipt=$path; $fixture.ReceiptLocked=$false
    & "$scripts/install-usbdk.ps1" -ResultPath $path | Out-Null
    Assert ($fixture.VmChecks -eq 1 -and (Get-Content -Raw -LiteralPath $path | ConvertFrom-Json).installed) 'Selected-VM install must retain the stopped-VM guard and caller receipt'
    $fixture.VmRunning=$true; $path=New-Receipt 'running-vm'; $before=$fixture.Msis
    Reject { & "$scripts/install-usbdk.ps1" -ResultPath $path } 'Fixture VM is still running'
    Assert ($fixture.Msis -eq $before -and (Get-Item -LiteralPath $path).Length -eq 0) 'Running VM must block installation before MSI'
    $fixture.VmRunning=$false; $fixture.ExpectedReceipt=''; $fixture.ExitCode=3010
    $text=& "$scripts/install-usbdk.ps1" -CacheDirectory $cache
    Assert ($text -match 'Windows を再起動') 'Legacy CLI without a receipt must still report required reboot'

    # Verify both actual Manager dispatch paths preserve the supplied caller path;
    # this spy does not touch the elevated account's LocalAppData cache.
    Copy-Item -LiteralPath "$PSScriptRoot/../manager-backend.ps1" -Destination $scripts
    foreach ($name in @('network-uplinks.ps1','manager-ports.ps1')) { [IO.File]::WriteAllText((Join-Path $scripts $name),'# fixture: no host access') }
    [IO.File]::WriteAllText((Join-Path $scripts 'install-usbdk.ps1'),@'
param([string]$CacheDirectory,[string]$ResultPath)
$fixture.Dispatch.Add([pscustomobject]@{cache=$CacheDirectory;result=$ResultPath;receipt_supplied=$PSBoundParameters.ContainsKey('ResultPath')})
'@)
    $path=New-Receipt 'dispatch-global'; $before=$fixture.EnvLoads
    & "$scripts/manager-backend.ps1" -Root "$work/no-engine" -Action InstallUsbDk -UsbResultPath $path
    $dispatch=$fixture.Dispatch[-1]
    Assert ($dispatch.receipt_supplied -and $dispatch.result -ceq $path -and $dispatch.cache -and $fixture.EnvLoads -eq $before) 'Global dispatch must forward caller receipt without requiring a VM'
    $path=New-Receipt 'dispatch-selected'
    & "$scripts/manager-backend.ps1" -Root "$work/no-engine" -VmDirectory $selected -Action InstallUsbDk -UsbResultPath $path
    $dispatch=$fixture.Dispatch[-1]
    Assert ($dispatch.receipt_supplied -and $dispatch.result -ceq $path -and !$dispatch.cache) 'Selected dispatch must forward caller receipt without bypassing VM installer context'
    & "$scripts/manager-backend.ps1" -Root "$work/no-engine" -Action InstallUsbDk
    Assert (!$fixture.Dispatch[-1].receipt_supplied) 'Legacy global dispatch must omit the new optional parameter'
    & "$scripts/manager-backend.ps1" -Root "$work/no-engine" -VmDirectory $selected -Action InstallUsbDk
    Assert (!$fixture.Dispatch[-1].receipt_supplied) 'Legacy selected dispatch must omit the new optional parameter'
    "$script:checks USB driver receipt checks passed."
} finally {
    $prefix=[IO.Path]::GetFullPath("$PSScriptRoot/../../.work/tmp")+[IO.Path]::DirectorySeparatorChar
    if ($work.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
