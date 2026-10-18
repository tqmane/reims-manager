# Download/extract data only. Never execute the MSI or import its certificate.
function Invoke-ReimsWifiNative([string]$Program,[string[]]$Arguments) {
    $output=@(& $Program @Arguments 2>&1 | ForEach-Object ToString)
    return @{exit_code=$LASTEXITCODE;output=($output -join "`n")}
}
function Assert-ReimsWifiDriverPayload([string]$Directory,[string]$Helper) {
    $catalog=Join-Path $Directory 'ndisrd.cat'
    $signature=Get-AuthenticodeSignature -LiteralPath $catalog
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Windows Hardware Compatibility Publisher'){
        throw 'Expected a valid Microsoft WHCP-signed driver catalog.'
    }
    $inf=Get-Content -Raw -LiteralPath (Join-Path $Directory 'ndisrd_lwf.inf')
    if($inf -notmatch '(?im)^CatalogFile\s*=\s*ndisrd\.cat\s*$' -or $inf -notmatch '(?im)^DriverVer\s*=\s*[^,\r\n]+,3\.6\.2\.1\s*$' -or
        $inf -notmatch '(?im)^%ndisrd_Desc%=Install, nt_ndisrd\s*$') {throw 'Unexpected driver INF identity/version.'}
    foreach($name in @('ndisrd_lwf.inf','ndisrd.sys')){
        $result=Invoke-ReimsWifiNative $Helper @('--verify-catalog',$catalog,(Join-Path $Directory $name))
        if($result.exit_code -ne 0){throw "Driver catalog membership/trust verification failed: $name"}
    }
}
function Install-ReimsWifiDriver([string]$EngineRoot,[bool]$AcceptDriverTerms) {
    if(!$AcceptDriverTerms){throw 'Read the official personal/academic/evaluation and commercial terms, then explicitly accept before installing.'}
    Assert-Administrator
    if([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64){throw 'Wi-Fi compatibility currently requires Windows x64.'}
    $pin=Get-Content -Raw -LiteralPath "$PSScriptRoot/wifi-macnat-lock.json" | ConvertFrom-Json
    $result=@{schema=1;installed=$false;reused=$false;restart_required=$false;terms_id=$pin.driver.terms_id;certificate_imported=$false;forced_reboot=$false}
    $helper=Get-ReimsWifiHelper
    if(!(Test-Path -LiteralPath $helper -PathType Leaf)){throw 'Re-extract the complete Manager package; native helper missing.'}
    $installed=Get-ReimsWifiDriverState
    if($installed -eq 'running'){$result.installed=$true;$result.reused=$true;return $result}
    if($installed -ne 'not_installed'){throw 'An existing packet-filter installation is preserved; resolve its state before retrying.'}
    . "$PSScriptRoot/engine-package.ps1"
    $engine=Read-ReimsEngine $EngineRoot -VerifyFiles
    if(!$engine){throw 'Select the installed portable engine to extract the official package.'}
    $seven=Join-ReimsPackagePath $EngineRoot $engine.paths.seven_zip
    $cache=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('ReimsManager/Drivers/wifi-'+$pin.driver.version)
    [void][IO.Directory]::CreateDirectory($cache)
    $attempt=Join-Path $cache ([guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($attempt)
    $archive=Join-Path $attempt 'driver.msi'
    Invoke-WebRequest -Uri $pin.driver.url -OutFile $archive
    $unpack=Join-Path $attempt 'unpacked'
    $extracted=Invoke-ReimsWifiNative $seven @('x','-y',"-o$unpack",$archive)
    if($extracted.exit_code){throw 'Cannot extract the official driver MSI as data.'}
    $cabinet=0
    foreach($cab in @(Get-ChildItem -LiteralPath $unpack -Recurse -Filter '*.cab')){
        if(++$cabinet -gt 8){throw 'Unexpected MSI cabinet layout.'}
        $expanded=Invoke-ReimsWifiNative $seven @('x','-y',('-o'+(Join-Path $unpack "cab-$cabinet")),$cab.FullName)
        if($expanded.exit_code){throw 'Driver cabinet extraction failed.'}
    }
    $payload=Join-Path $attempt 'payload';[void][IO.Directory]::CreateDirectory($payload)
    foreach($entry in @(@('INF','ndisrd_lwf.inf'),@('CAT','ndisrd.cat'),@('SYS','ndisrd.sys'))){
        $found=@(Get-ChildItem -LiteralPath $unpack -Recurse -File | Where-Object Name -EQ ('FILE_win10_x64_Drivers_'+$entry[0]))
        if($found.Count -ne 1){throw 'Official Windows 10/11 x64 payload is missing/ambiguous.'}
        Copy-Item -LiteralPath $found[0].FullName -Destination (Join-Path $payload $entry[1])
    }
    $locks=[Collections.Generic.List[IO.FileStream]]::new()
    try{
        foreach($name in @('ndisrd_lwf.inf','ndisrd.cat','ndisrd.sys')){$locks.Add([IO.File]::Open((Join-Path $payload $name),'Open','Read','Read'))}
        Assert-ReimsWifiDriverPayload $payload $helper
        if((Get-ReimsWifiDriverState) -ne 'not_installed'){throw 'Another driver installation appeared; no existing filter was changed.'}
        $native=Invoke-ReimsWifiNative "$env:SystemRoot/System32/netcfg.exe" @('-v','-l',(Join-Path $payload 'ndisrd_lwf.inf'),'-c','s','-i','nt_ndisrd')
        $native.output | Set-Content -LiteralPath (Join-Path $attempt 'install.log') -Encoding utf8NoBOM
        if($native.exit_code -notin @(0,3010)){throw "Official driver installation failed ($($native.exit_code)); inspect the private installation log before retrying."}
        $result.installed=$true;$result.restart_required=$native.exit_code -eq 3010
        $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cache 'installation.json') -Encoding utf8NoBOM
        return $result
    }finally{foreach($lock in $locks){$lock.Dispose()}}
}
