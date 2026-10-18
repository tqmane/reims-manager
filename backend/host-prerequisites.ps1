# Read-only onboarding checks. Do not load environment.ps1: there may be no VM yet.
. "$PSScriptRoot/engine-package.ps1"
. "$PSScriptRoot/guest-bridge-release.ps1"

function Get-ReimsHostFacts {
    $feature=Get-CimInstance Win32_OptionalFeature -Filter "Name = 'HypervisorPlatform'" -ErrorAction Stop
    $processors=@(Get-CimInstance Win32_Processor -ErrorAction Stop)
    $os=Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $whpx=$false
    if (!("ReimsSetupWhpx" -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class ReimsSetupWhpx {
    [DllImport("WinHvPlatform.dll")]
    public static extern int WHvGetCapability(uint code, out ulong value, uint size, out uint written);
}
'@
    }
    try {
        [ulong]$capability=0; [uint32]$written=0
        $result=[ReimsSetupWhpx]::WHvGetCapability(0,[ref]$capability,8,[ref]$written)
        $whpx=$result -ge 0 -and $written -ge 4 -and ($capability -band 0xffffffff) -ne 0
    } catch { $whpx=$false }
    $icds=@()
    $drivers='HKLM:\SOFTWARE\Khronos\Vulkan\Drivers'
    if (Test-Path $drivers) {
        $key=Get-Item $drivers
        foreach ($name in $key.GetValueNames()) { if ($key.GetValue($name) -eq 0) { $icds+=$name } }
    }
    $display='HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
    foreach ($key in @(Get-ChildItem $display -ErrorAction SilentlyContinue)) {
        $value=Get-ItemProperty $key.PSPath -Name VulkanDriverName -ErrorAction SilentlyContinue
        if ($value) { $icds+=@($value.VulkanDriverName) }
    }
    $reboot=Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
    return @{
        feature_enabled=($feature.InstallState -eq 1); whpx_ready=$whpx
        firmware_virtualization=(@($processors | Where-Object {!$_.VirtualizationFirmwareEnabled}).Count -eq 0)
        restart_required=$reboot
        available_memory_mib=[int][math]::Floor($os.FreePhysicalMemory/1024)
        logical_cpus=[int](($processors | Measure-Object NumberOfLogicalProcessors -Sum).Sum)
        vulkan_icd=(@($icds | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -gt 0)
        openssh=(Test-Path -LiteralPath "$env:SystemRoot/System32/OpenSSH/ssh-keygen.exe" -PathType Leaf) -and (Test-Path -LiteralPath "$env:SystemRoot/System32/OpenSSH/ssh.exe" -PathType Leaf)
        usbdk=(Test-Path -LiteralPath "$env:SystemRoot/System32/drivers/UsbDk.sys" -PathType Leaf)
    }
}

function Get-ReimsPrerequisites([string]$Root, [string]$ToolsRoot, [int]$Cpus=8, [int]$MemoryMiB=8192) {
    if (!$ToolsRoot) { $ToolsRoot=Join-Path (Split-Path $PSScriptRoot) 'runtimes' }
    $checks=[Collections.Generic.List[object]]::new()
    try { $facts=Get-ReimsHostFacts }
    catch {
        return @{ready=$false;restart_required=$false;checks=@(@{id='host';label='Windows';ready=$false;message="Windows の前提条件を取得できません: $($_.Exception.Message)"});plan=@{cpus=$Cpus;memory_mib=$MemoryMiB;available_memory_mib=$null;start_ready=$false}}
    }
    $restart=[bool]$facts.restart_required -or ($facts.feature_enabled -and !$facts.whpx_ready -and $facts.firmware_virtualization)
    $message=if ($facts.whpx_ready) {'WHPX を利用できます。'}
        elseif (!$facts.firmware_virtualization) {'UEFI/BIOS の CPU 仮想化を有効にしてください。'}
        elseif (!$facts.feature_enabled) {'Windows Hypervisor Platform を管理者権限で有効にし、Windows を再起動してください。'}
        else {'WHPX がまだ利用できません。Windows を再起動し、再確認してください。'}
    $whpx=@{id='whpx';label='Windows Hypervisor Platform';ready=[bool]$facts.whpx_ready;message=$message}
    if (!$facts.feature_enabled) { $whpx.action='EnableWhpx' }
    $checks.Add($whpx)
    $checks.Add(@{id='vulkan';label='GPU Vulkan driver';ready=[bool]$facts.vulkan_icd;message=$(if ($facts.vulkan_icd) {'Vulkan ICD 登録を検出しました。GPU の実行・性能検証はしていません。'} else {'GPU メーカーの Vulkan 対応ドライバーをインストールしてください。'})})
    $checks.Add(@{id='openssh';label='OpenSSH Client';ready=[bool]$facts.openssh;message=$(if ($facts.openssh) {'SSH と鍵作成ツールを検出しました。'} else {'Windows のオプション機能で OpenSSH クライアントを追加してください。管理者権限が必要です。'})})
    try {
        $engine=Read-ReimsEngine $Root -VerifyFiles
        if (!$engine) { throw '先に対応エンジンをセットアップしてください。' }
        foreach ($relative in $engine.files.Keys) {
            if (!(Test-Path -LiteralPath (Join-ReimsPackagePath $Root $relative) -PathType Leaf)) { throw "エンジンの依存ファイルがありません: $relative" }
        }
        if (!$engine.files.ContainsKey('bin/vulkan-1.dll')) { throw 'エンジンに Vulkan loader がありません。' }
        $checks.Add(@{id='engine';label='Reims engine / firmware';ready=$true;message='固定版 QEMU・DLL・OpenCore・OVMF・GPU ROM を検出しました。'})
    } catch { $checks.Add(@{id='engine';label='Reims engine / firmware';ready=$false;message=$_.Exception.Message}) }
    $python=Test-Path -LiteralPath (Join-Path $ToolsRoot 'python/python.exe') -PathType Leaf
    $checks.Add(@{id='runtimes';label='Manager runtimes';ready=[bool]$python;message=$(if ($python) {'同梱 Python を検出しました。'} else {'Manager の完全な配布パッケージを再展開してください。同梱 Python がありません。'})})
    try {
        $pin=Get-ReimsQualifiedGuestBridgeLock
        [void](Get-ReimsGuestBridgeRelease $pin)
        $checks.Add(@{id='guest_addon';label='Metal / WebGL guest addon';ready=$true;message="資格確認済みの addon $($pin.id) を取得できます。フレームペーシングには制限があります。"})
    } catch { $checks.Add(@{id='guest_addon';label='Metal / WebGL guest addon';ready=$false;message=$_.Exception.Message}) }
    $checks.Add(@{id='usb';label='USB passthrough (optional)';required=$false;ready=[bool]$facts.usbdk;message=$(if ($facts.usbdk) {'UsbDk ドライバーを検出しました。選択した機器だけを転送します。'} else {'USB 転送を使う場合は UsbDk を管理者権限で導入してください。再起動が必要になる場合があります。'});action='InstallUsbDk'})
    $planFits=$facts.available_memory_mib -ge ($MemoryMiB+1024) -and $Cpus -le $facts.logical_cpus
    $checks.Add(@{id='capacity';label='VM memory / CPU plan';required=$false;ready=[bool]$planFits;message=$(if ($planFits) {"空き RAM $($facts.available_memory_mib) MiB。計画 $MemoryMiB MiB / $Cpus vCPU を起動できます。"} else {"空き RAM $($facts.available_memory_mib) MiB / ホスト $($facts.logical_cpus) 論理 CPU。計画 $MemoryMiB MiB / $Cpus vCPU は保存できますが、自動起動の前に RAM を確保するか割り当てを下げてください。ホスト用に 1024 MiB を残します。"})})
    $ready=@($checks | Where-Object { !$_.ready -and (!$_.ContainsKey('required') -or $_.required) }).Count -eq 0
    return @{ready=$ready;restart_required=$restart;checks=@($checks.ToArray());plan=@{cpus=$Cpus;memory_mib=$MemoryMiB;available_memory_mib=$facts.available_memory_mib;start_ready=($ready -and $planFits -and !$restart)}}
}
