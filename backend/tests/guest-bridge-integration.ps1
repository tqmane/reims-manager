#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EngineRoot,[Parameter(Mandatory)][string]$ToolsRoot)
$ErrorActionPreference='Stop'
$env:REIMS_PROJECT_ROOT_OVERRIDE=[IO.Path]::GetFullPath($EngineRoot)
$env:REIMS_MANAGER_TOOLS_ROOT=[IO.Path]::GetFullPath($ToolsRoot)
$fixture=Join-Path $env:REIMS_PROJECT_ROOT_OVERRIDE ('.work/tmp/guest-bridge-fixture-'+[guid]::NewGuid().ToString('N'))
$env:REIMS_VM_ROOT_OVERRIDE=$fixture
$sourceScripts=Split-Path $PSScriptRoot
. "$sourceScripts/start.ps1" -FunctionsOnly
$lock=$null
try {
    # A future qualified package is simulated only in this private backend copy.
    $scripts=Join-Path $fixture 'backend'; New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    Get-ChildItem -LiteralPath $sourceScripts -File | Copy-Item -Destination $scripts
    Copy-Item -LiteralPath (Join-Path $sourceScripts 'opencore') -Destination $scripts -Recurse
    & "$scripts/prepare-opencore.ps1" -Rebuild | Out-Null
    $ocPath=Join-Path $fixture 'state/opencore.json'; $statePath=Join-Path $fixture 'state/config.json'
    $oc=Get-Content $ocPath -Raw | ConvertFrom-Json
    $state=@{schema=1;base_disk=(Join-Path $fixture 'base.qcow2');active_disk=(Join-Path $fixture 'current.qcow2');
        opencore=$oc.image;firmware_code=$oc.firmware_code;firmware_vars=$oc.firmware_vars_template;
        rom=(Get-ReimsEnginePath 'rom' '.work/target/dist-vulkan/share/reims-vgpu/ReimsVgpu.rom');qmp_port=4444;ssh_port=2222}
    Write-ReimsJson $statePath $state
    $python=Get-ReimsTool 'python.exe'
    $package=Join-Path $fixture 'authored-package'
    $makeFixture=@'
import json,pathlib,runpy,sys
helpers=runpy.run_path(sys.argv[1],run_name='fixture_helpers')
helpers['package'](pathlib.Path(sys.argv[2]))
pathlib.Path(sys.argv[3]).write_text(json.dumps(helpers['READY']),encoding='utf-8')
'@
    $makeFixture | & $python -X utf8 - "$PSScriptRoot/guest-bridge.py" $package "$scripts/guest-bridge-lock.json"
    if($LASTEXITCODE -ne 0){throw 'Authored package fixture failed'}
    $archive=Join-Path $fixture 'ReimsGuestBridge.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($package,$archive)
    $digest=(Get-FileHash $archive).Hash
    $originalImage=$oc.image; $imageHash=(Get-FileHash $originalImage).Hash
    $originalPlist=[IO.File]::ReadAllBytes($oc.config)
    $identityCode='import hashlib,plistlib,sys; c=plistlib.load(open(sys.argv[1],"rb")); print(hashlib.sha256(plistlib.dumps(c["PlatformInfo"])).hexdigest())'
    $identity=& $python -X utf8 -c $identityCode $oc.config
    $oldConfig=(Get-FileHash $statePath).Hash; $oldOc=(Get-FileHash $ocPath).Hash
    $fixturePinPath=Join-Path $scripts 'guest-bridge-lock.json'
    $fixturePinText=[IO.File]::ReadAllText($fixturePinPath)
    $wrongEngine=$fixturePinText | ConvertFrom-Json -AsHashtable
    $wrongEngine.required_engine_id='fixture-incompatible-engine'
    $wrongEngine | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $fixturePinPath -Encoding utf8NoBOM
    $rejected=$false
    try { & "$scripts/prepare-guest-bridge.ps1" -Archive $archive -Sha256 $digest | Out-Null }
    catch { if($_.Exception.Message -notmatch 'matching portable engine'){throw}; $rejected=$true }
    finally { [IO.File]::WriteAllText($fixturePinPath,$fixturePinText) }
    if(!$rejected -or (Get-FileHash $statePath).Hash -ne $oldConfig -or (Get-FileHash $ocPath).Hash -ne $oldOc){throw 'Incompatible host engine was not rejected safely'}
    # Invalid candidate validation must leave both selected images/metadata untouched.
    & $python -X utf8 -c 'import pathlib,plistlib,sys; p=pathlib.Path(sys.argv[1]); c=plistlib.loads(p.read_bytes()); c["Misc"]["Security"]["FixtureInvalidKey"]=True; p.write_bytes(plistlib.dumps(c))' $oc.config
    $rejected=$false
    try { & "$scripts/prepare-guest-bridge.ps1" -Archive $archive -Sha256 $digest | Out-Null } catch { $rejected=$true }
    if(!$rejected -or (Get-FileHash $statePath).Hash -ne $oldConfig -or (Get-FileHash $ocPath).Hash -ne $oldOc){throw 'Invalid candidate changed active metadata'}
    [IO.File]::WriteAllBytes($oc.config,$originalPlist)
    & "$scripts/prepare-guest-bridge.ps1" -Archive $archive -Sha256 $digest | Out-Null
    $enabled=Get-Content $ocPath -Raw | ConvertFrom-Json
    if($enabled.image -eq $originalImage -or (Get-FileHash $originalImage).Hash -ne $imageHash){throw 'Original EFI image was replaced'}
    if((& $python -X utf8 -c $identityCode $enabled.config) -ne $identity){throw 'Identity changed'}
    $check=@'
import plistlib,sys
c=plistlib.load(open(sys.argv[1],'rb'))
b=next(x for x in c['Kernel']['Add'] if x['BundlePath']=='ReimsMetalCompat.kext')
assert b['Enabled']==(sys.argv[2]=='enabled')
assert (b['MinKernel'],b['MaxKernel'])==('25.0.0','25.99.99')
assert c['NVRAM']['Add']['7C436110-AB2A-4BBB-A880-FE41995C9F82']['csr-active-config']==bytes(4)
'@
    $check | & $python -X utf8 - $enabled.config enabled
    if($LASTEXITCODE -ne 0){throw 'Enabled configuration check failed'}
    & "$scripts/prepare-guest-bridge.ps1" -Mode Disable | Out-Null
    $disabled=Get-Content $ocPath -Raw | ConvertFrom-Json
    $check | & $python -X utf8 - $disabled.config disabled
    if($LASTEXITCODE -ne 0){throw 'Disabled configuration check failed'}
    & "$scripts/prepare-opencore.ps1" -Rebuild | Out-Null
    $rebuilt=Get-Content $ocPath -Raw | ConvertFrom-Json
    if($rebuilt.image -ne $disabled.image -or (& $python -X utf8 -c $identityCode $rebuilt.config) -ne $identity){throw 'Rebuild discarded active EFI or identity'}
    # Deny metadata replacement to exercise switch rollback without touching a real VM.
    $oldConfig=(Get-FileHash $statePath).Hash; $oldOc=(Get-FileHash $ocPath).Hash
    $lock=[IO.File]::Open($statePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $rejected=$false
    try { & "$scripts/prepare-guest-bridge.ps1" -Archive $archive -Sha256 $digest | Out-Null } catch { $rejected=$true }
    $lock.Dispose(); $lock=$null
    if(!$rejected -or (Get-FileHash $statePath).Hash -ne $oldConfig -or (Get-FileHash $ocPath).Hash -ne $oldOc){throw 'Failed metadata switch was not rolled back'}
    if(Test-Path (Join-Path $fixture 'state/runtime.json')){throw 'Fixture unexpectedly recorded a VM process'}
    'PASS: authored addon validation, enable/disable, identity and bounds, active EFI rebuild, metadata rollback; no VM boot'
} finally {
    if($lock){$lock.Dispose()}
    $allowed=[IO.Path]::GetFullPath((Join-Path $env:REIMS_PROJECT_ROOT_OVERRIDE '.work/tmp'))+[IO.Path]::DirectorySeparatorChar
    if([IO.Path]::GetFullPath($fixture).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $fixture)){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
