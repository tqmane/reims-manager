# Process-local settings; nothing is written to the system environment.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = if ($env:REIMS_PROJECT_ROOT_OVERRIDE) {
    [IO.Path]::GetFullPath($env:REIMS_PROJECT_ROOT_OVERRIDE)
} else {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
$MsysRoot = if ($env:REIMS_MSYS2_ROOT) { $env:REIMS_MSYS2_ROOT } else { Join-Path $env:SystemDrive 'msys64' }
$VmRoot = if ($env:REIMS_VM_ROOT_OVERRIDE) { [IO.Path]::GetFullPath($env:REIMS_VM_ROOT_OVERRIDE) } else { Join-Path $ProjectRoot 'vm' }
if ($VmRoot.TrimEnd('\','/') -eq [IO.Path]::GetPathRoot($VmRoot).TrimEnd('\','/')) { throw 'Select a VM folder, not a drive root.' }
$PortableEngine=Test-Path -LiteralPath (Join-Path $ProjectRoot 'reims-engine.json')
if ($PortableEngine) {
    $enginePrefix=$ProjectRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if (!$env:REIMS_VM_ROOT_OVERRIDE -or $VmRoot -eq $ProjectRoot -or $VmRoot.StartsWith($enginePrefix,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Select an explicit VM data directory outside the immutable engine package.'
    }
}
$ReimsSupportRoot=Join-Path $VmRoot 'support'
$ReimsWorkRoot=if ($PortableEngine) { $ReimsSupportRoot } else { Join-Path $ProjectRoot '.work' }

foreach ($directory in @('tmp', 'cache', 'cargo', 'rustup', 'target', 'downloads', 'diagnostics')) {
    New-Item -ItemType Directory -Path (Join-Path $ReimsWorkRoot $directory) -Force | Out-Null
}
$env:TEMP = Join-Path $ReimsWorkRoot 'tmp'
$env:TMP = $env:TEMP
$env:TMPDIR = $env:TEMP
$env:CARGO_HOME = Join-Path $ReimsWorkRoot 'cargo'
$env:RUSTUP_HOME = Join-Path $ReimsWorkRoot 'rustup'
$env:CARGO_TARGET_DIR = Join-Path $ReimsWorkRoot 'target'
$env:XDG_CACHE_HOME = Join-Path $ReimsWorkRoot 'cache'
$env:PIP_CACHE_DIR = Join-Path $env:XDG_CACHE_HOME 'pip'
$env:CCACHE_DIR = Join-Path $env:XDG_CACHE_HOME 'ccache'
$env:PYTHONUTF8 = '1'
$env:RUSTUP_TOOLCHAIN = '1.98.1-x86_64-pc-windows-gnu'
$env:CARGO_BUILD_JOBS = '4'
$env:CMAKE_BUILD_PARALLEL_LEVEL = '4'
$env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = Join-Path $MsysRoot 'ucrt64\bin\clang.exe'
$env:CC = Join-Path $MsysRoot 'ucrt64\bin\clang.exe'
$env:CXX = Join-Path $MsysRoot 'ucrt64\bin\clang++.exe'
$env:CMAKE_GENERATOR = 'Ninja'
$env:RC = Join-Path $MsysRoot 'ucrt64\bin\llvm-windres.exe'
$env:CMAKE_TOOLCHAIN_FILE = Join-Path $PSScriptRoot 'ucrt64.cmake'
$env:PATH = if ($PortableEngine) {
    # A portable engine must never pick up a developer's MSYS2 DLLs.
    (Join-Path $ProjectRoot 'bin') + [IO.Path]::PathSeparator + "$env:SystemRoot/System32;$env:SystemRoot;$env:SystemRoot/System32/OpenSSH"
} else { (@(
    (Join-Path $MsysRoot 'ucrt64\bin'),
    (Join-Path $env:CARGO_HOME 'bin'),
    (Join-Path $env:RUSTUP_HOME "toolchains\$env:RUSTUP_TOOLCHAIN\bin"),
    (Join-Path $env:ProgramFiles 'Git\cmd'),
    (Join-Path $MsysRoot 'usr\bin'),
    $env:PATH
) -join [IO.Path]::PathSeparator) }

function Invoke-ReimsMsys {
    param([Parameter(Mandatory)][string]$Command)
    if ($PortableEngine) { throw 'Build tools require a source checkout, not an installed engine package.' }
    $env:MSYSTEM = 'UCRT64'
    $env:CHERE_INVOKING = '1'
    $env:MSYS2_PATH_TYPE = 'inherit'
    $env:REIMS_MSYS_PROJECT_ROOT = $ProjectRoot
    $setup = 'reims_root="$(cygpath -u "$REIMS_MSYS_PROJECT_ROOT")"; export TEMP="$reims_root/.work/tmp" TMP="$reims_root/.work/tmp" TMPDIR="$reims_root/.work/tmp" XDG_CACHE_HOME="$reims_root/.work/cache" PIP_CACHE_DIR="$reims_root/.work/cache/pip" CARGO_HOME="$reims_root/.work/cargo" RUSTUP_HOME="$reims_root/.work/rustup"; '
    Push-Location $ProjectRoot
    try {
        & (Join-Path $MsysRoot 'usr\bin\bash.exe') -lc ($setup + $Command)
        if ($LASTEXITCODE -ne 0) { throw "MSYS2 command failed ($LASTEXITCODE): $Command" }
    } finally { Pop-Location }
}
