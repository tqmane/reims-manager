#Requires -Version 7.2
[CmdletBinding()]
param([string]$NdisApiSource,[string]$GslSource,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin=Get-Content -LiteralPath (Join-Path $repo 'backend/wifi-macnat-lock.json') -Raw | ConvertFrom-Json
$work=Join-Path $repo '.work/wifi-macnat'
[void][IO.Directory]::CreateDirectory($work)
function Get-Source($Pin,[string]$Name) {
    $destination=Join-Path $work ($Name+'-'+$Pin.commit)
    if(!(Test-Path -LiteralPath $destination)) {
        $zip=Join-Path $work ($Name+'.zip')
        $repository=([uri]$Pin.repository).AbsolutePath.Trim('/')
        Invoke-WebRequest -Uri "https://codeload.github.com/$repository/zip/$($Pin.commit)" -OutFile $zip
        $unpack=Join-Path $work ([guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $zip -DestinationPath $unpack
        $root=@(Get-ChildItem -LiteralPath $unpack -Directory)
        if($root.Count -ne 1){throw 'Unexpected pinned SDK archive layout.'}
        Move-Item -LiteralPath $root[0].FullName -Destination $destination
    }
    return $destination
}
if(!$NdisApiSource){$NdisApiSource=Get-Source $pin.sdk 'ndisapi'}
if(!$GslSource){$GslSource=Get-Source $pin.gsl 'gsl'}
$NdisApiSource=[IO.Path]::GetFullPath($NdisApiSource).Replace('\','/')
$GslSource=[IO.Path]::GetFullPath($GslSource).Replace('\','/')
if(!$OutputDirectory){$OutputDirectory=Join-Path $work 'build'}
$cmake=(Get-Command cmake -ErrorAction SilentlyContinue).Source
if(!$cmake){$cmake=(& "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -find '**/cmake.exe' | Select-Object -First 1)}
if(!$cmake){throw 'Visual Studio C++ CMake tools are required to build the helper.'}
& $cmake -S (Join-Path $repo 'native/wifi-macnat') -B $OutputDirectory -G 'Visual Studio 17 2022' -A x64 -T host=x64 `
    '-DCMAKE_TRY_COMPILE_CONFIGURATION=Release' "-DNDISAPI_SOURCE=$NdisApiSource" "-DGSL_SOURCE=$GslSource"
if($LASTEXITCODE){throw 'Native helper configuration failed.'}
& $cmake --build $OutputDirectory --config Release --parallel 1
if($LASTEXITCODE){throw 'Native helper build failed.'}
& (Join-Path (Split-Path $cmake) 'ctest.exe') --test-dir $OutputDirectory -C Release --output-on-failure
if($LASTEXITCODE){throw 'Packet CPU fixtures failed.'}
& (Join-Path $OutputDirectory 'Release/reims-wifi-macnat.exe') --self-test
if($LASTEXITCODE){throw 'Driver-free helper identity checks failed.'}
'Built native Wi-Fi helper and passed driver-free CPU fixtures.'
