# Shared package contract; loading this file has no filesystem side effects.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ReimsEngineLock { Get-Content -Raw -LiteralPath "$PSScriptRoot/engine-lock.json" | ConvertFrom-Json -AsHashtable }

function Join-ReimsPackagePath([string]$Directory, [string]$Relative) {
    if (!$Relative -or $Relative.Contains('\') -or $Relative -match '[:\x00-\x1f]' -or
        $Relative.StartsWith('/') -or @($Relative.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[. ]$|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count) {
        throw "Invalid package path: $Relative"
    }
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    if (!$path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes its directory.' }
    return $path
}

function Read-ReimsEngine([string]$Directory, [switch]$VerifyFiles) {
    $path = Join-Path $Directory 'reims-engine.json'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    if ((Get-Item -LiteralPath $path).Length -gt 4MB) { throw 'Engine manifest is too large.' }
    $m = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
    $pin = Get-ReimsEngineLock
    if ($m.schema -ne 1 -or $m.id -ne $pin.id) { throw 'Unsupported engine package. Use the engine version paired with this manager.' }
    foreach ($source in $pin.sources.Keys) {
        if ($m.sources[$source].commit -ne $pin.sources[$source].commit -or $m.sources[$source].repository -ne $pin.sources[$source].repository) { throw "Unexpected source revision: $source" }
    }
    foreach ($key in @('qemu','qemu_img','qemu_io','seven_zip','firmware_code','firmware_vars','rom','opencore_archive')) {
        $file = Join-ReimsPackagePath $Directory $m.paths[$key]
        if (!$m.files.ContainsKey($m.paths[$key]) -or !(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Engine component is missing: $key" }
    }
    [void](Join-ReimsPackagePath $Directory $m.paths.firmware)
    foreach ($name in $m.files.Keys) {
        $file = Join-ReimsPackagePath $Directory $name
        if ($m.files[$name] -notmatch '^[a-f0-9]{64}$') { throw "Invalid SHA256: $name" }
        if ($VerifyFiles) {
            if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file).Hash -ne $m.files[$name]) { throw "Engine file missing or changed: $name" }
        }
    }
    if ($m.files[$m.paths.opencore_archive] -ne $pin.opencore.sha256) { throw 'OpenCore archive does not match the official pinned release.' }
    if ($VerifyFiles) {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File) {
            $name = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\','/')
            if ($name -ne 'reims-engine.json' -and !$m.files.ContainsKey($name)) { throw "Unlisted package file: $name" }
        }
    }
    return $m
}

function Expand-ReimsPackage([string]$Archive, [string]$Directory) {
    if (Test-Path -LiteralPath $Directory) { throw 'Extraction directory already exists.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total = 0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.TrimEnd('/')
            [void](Join-ReimsPackagePath $Directory $name)
            if (!$names.Add($name) -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw "Duplicate or symlink ZIP entry: $name" }
            $total += $entry.Length
            if ($total -gt 4GB -or $names.Count -gt 20000) { throw 'Package exceeds the extraction limit.' }
        }
        [void][IO.Directory]::CreateDirectory($Directory)
        foreach ($entry in $zip.Entries) {
            $path = Join-ReimsPackagePath $Directory $entry.FullName.TrimEnd('/')
            if ($entry.FullName.EndsWith('/')) { [void][IO.Directory]::CreateDirectory($path); continue }
            [void][IO.Directory]::CreateDirectory((Split-Path $path))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
        }
    } finally { $zip.Dispose() }
}

function Get-ReimsPinnedDownload([string]$Url, [string]$Sha256, [string]$Destination) {
    if ($Sha256 -notmatch '^[a-f0-9]{64}$' -or !([uri]$Url).IsAbsoluteUri -or ([uri]$Url).Scheme -ne 'https') { throw 'A HTTPS URL and SHA256 are required.' }
    if (!(Test-Path -LiteralPath $Destination)) {
        $partial = "$Destination.$([guid]::NewGuid().ToString('N')).partial"
        try {
            Invoke-WebRequest $Url -OutFile $partial -TimeoutSec 1800
            if ((Get-FileHash -LiteralPath $partial).Hash -ne $Sha256) { throw 'Download SHA256 mismatch.' }
            Move-Item -LiteralPath $partial -Destination $Destination
        } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    }
    if ((Get-FileHash -LiteralPath $Destination).Hash -ne $Sha256) { throw 'Cached download SHA256 mismatch.' }
}

function Test-ReimsNativeEngine([string]$Directory) {
    $m = Read-ReimsEngine $Directory
    if (!$m) { throw 'Engine manifest not found.' }
    foreach ($probe in @(@('qemu','--version','QEMU'), @('qemu_img','--version','qemu-img'), @('qemu_io','--version','qemu-io'), @('seven_zip','i','7-Zip'), @('qemu','-accel','whpx'), @('qemu','-device','apple-gfx-pci'))) {
        $info = [Diagnostics.ProcessStartInfo]::new((Join-ReimsPackagePath $Directory $m.paths[$probe[0]]))
        $info.UseShellExecute=$false; $info.CreateNoWindow=$true
        $info.WorkingDirectory=$Directory
        $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
        # DLLs must resolve beside the executable or from Windows, never from MSYS2/PATH.
        $info.Environment['PATH']="$env:SystemRoot/System32;$env:SystemRoot"
        $info.ArgumentList.Add($probe[1]); if ($probe[1] -in @('-device','-accel')) { $info.ArgumentList.Add('help') }
        $process=[Diagnostics.Process]::Start($info)
        try {
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(30000)) { throw "Native tool timed out: $($probe[0])" }
            $output=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0 -or $output -notmatch [regex]::Escape($probe[2])) {
                throw "Native engine check failed: $($probe[0]) ($($process.ExitCode)). Required DLLs or Reims/WHPX support may be missing. $output"
            }
        } finally { $process.Dispose() }
    }
}
