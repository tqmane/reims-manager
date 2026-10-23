# Synthetic archive validation. No VM or Apple files are used.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../arm64-compat.ps1')
Add-Type -AssemblyName System.IO.Compression
$directory = Join-Path ([IO.Path]::GetTempPath()) ('reims-arm64-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $directory | Out-Null
try {
    function New-TestZip([string]$Path,[string[]]$Names) {
        $zip = [IO.Compression.ZipFile]::Open($Path,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $Names) {
                $entry = $zip.CreateEntry($name)
                $stream = $entry.Open()
                try {
                    $bytes = [Text.Encoding]::UTF8.GetBytes('fixture')
                    $stream.Write($bytes,0,$bytes.Length)
                } finally { $stream.Dispose() }
            }
        } finally { $zip.Dispose() }
    }
    $runtime = Join-Path $directory 'runtime.zip'
    $requiredRuntime = @('maclator','gpu/libmclbridge.dylib','gpu/libmclmetal.dylib',
        'SOURCE-COMMIT','SHA256SUMS')
    New-TestZip $runtime $requiredRuntime
    $checked = Assert-ReimsArm64Archive $runtime 'runtime'
    if ($checked.Count -ne $requiredRuntime.Count) { throw 'Unexpected runtime entry count' }

    $sysroot = Join-Path $directory 'sysroot.zip'
    New-TestZip $sysroot @('dyld','dyld_shared_cache_arm64e','dyld_shared_cache_arm64e.01',
        'sysroot-manifest.json','SHA256SUMS')
    [void](Assert-ReimsArm64Archive $sysroot 'sysroot')

    $bad = Join-Path $directory 'bad.zip'
    New-TestZip $bad ($requiredRuntime + '../outside')
    try { [void](Assert-ReimsArm64Archive $bad 'runtime'); throw 'Path traversal accepted' }
    catch { if ($_.Exception.Message -eq 'Path traversal accepted') { throw } }

    $unexpected = Join-Path $directory 'unexpected.zip'
    New-TestZip $unexpected ($requiredRuntime + 'extra.sh')
    try { [void](Assert-ReimsArm64Archive $unexpected 'runtime'); throw 'Unexpected file accepted' }
    catch { if ($_.Exception.Message -eq 'Unexpected file accepted') { throw } }

    $missing = Join-Path $directory 'missing.zip'
    New-TestZip $missing @('dyld','dyld_shared_cache_arm64e','SHA256SUMS')
    try { [void](Assert-ReimsArm64Archive $missing 'sysroot'); throw 'Incomplete sysroot accepted' }
    catch { if ($_.Exception.Message -eq 'Incomplete sysroot accepted') { throw } }
    'ARM64 ZIP fixtures passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
