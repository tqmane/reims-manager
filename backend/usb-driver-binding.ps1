# Native calls are deferred until an explicit operation. Importing this file only defines functions.
function Initialize-ReimsUsbDriverBindingNative {
    if ('ReimsUsbDriverBinding.Native' -as [type]) {
        if(![ReimsUsbDriverBinding.Model].GetField('ReadState') -or ![ReimsUsbDriverBinding.Native].GetField('ClassDriverFlagsEx')) { throw 'Use a fresh PowerShell process for the updated native driver definitions.' }
        return
    }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
namespace ReimsUsbDriverBinding {
public sealed class Model {
    public string InfPath, InfHash, Section, SectionExtension, Provider, Version, ClassGuid, Service, MatchingId;
    public string ReadState, DriverKey;
    public bool Present;
    public uint Problem;
}
public static class Native {
    // Keep distinct hardware-ID models even when their INF descriptions and ranks match.
    public const uint ClassDriverFlagsEx = 0x08000800; // NO_CLASSLIST_NODE_MERGE | ALLOWEXCLUDEDDRVS
    [StructLayout(LayoutKind.Sequential)] struct Device { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Driver {
        public uint Size, Type; public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Provider;
        public FileTime Date; public ulong Version;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Parameters {
        public uint Size, Flags, FlagsEx; public IntPtr Parent, Handler, Context, Queue; public UIntPtr ClassReserved; public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string DriverPath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Detail {
        public uint Size; public FileTime Date; public uint CompatOffset, CompatLength; public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Section;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string InfPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Description;
        public ushort HardwareId;
    }
    static readonly IntPtr Invalid = new IntPtr(-1);
    [DllImport("setupapi.dll", SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr cls, IntPtr parent);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr parent, uint flags, ref Device device);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", SetLastError=true)] static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref Device device, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref Device device, uint property, out uint type, byte[] data, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, IntPtr device, ref Parameters parameters);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiSetDeviceInstallParamsW(IntPtr set, IntPtr device, ref Parameters parameters);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiBuildDriverInfoList(IntPtr set, IntPtr device, uint type);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref Device device, uint type, uint index, ref Driver driver);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDriverInfoDetailW(IntPtr set, ref Device device, ref Driver driver, IntPtr detail, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiSetSelectedDriverW(IntPtr set, ref Device device, ref Driver driver);
    [DllImport("newdev.dll", SetLastError=true)] static extern bool DiInstallDevice(IntPtr parent, IntPtr set, ref Device device, ref Driver driver, uint flags, out bool reboot);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupCopyOEMInfW(string source, string media, uint mediaType, uint style, StringBuilder destination, uint size, out uint required, IntPtr component);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SetupOpenInfFileW(string path, string cls, uint style, out uint errorLine);
    [DllImport("setupapi.dll")] static extern void SetupCloseInfFile(IntPtr inf);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetActualSectionToInstallW(IntPtr inf, string section, StringBuilder actual, uint size, out uint required, IntPtr extension);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint instance, uint flags);
    static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    static Device NewDevice() { return new Device { Size=(uint)Marshal.SizeOf<Device>() }; }
    static IntPtr NewSet() { var set=SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero); if (set==Invalid) throw new Win32Exception(); return set; }
    public static void CheckLayout() {
        if (IntPtr.Size!=8 || RuntimeInformation.OSArchitecture!=Architecture.X64 || Marshal.SizeOf<Device>()!=32 || Marshal.SizeOf<Driver>()!=1568 ||
            Marshal.SizeOf<Parameters>()!=584 || Marshal.SizeOf<Detail>()!=1584 ||
            Marshal.OffsetOf<Detail>("HardwareId").ToInt32()!=1576 || Marshal.OffsetOf<Parameters>("DriverPath").ToInt32()!=60)
            throw new InvalidOperationException("Unsupported native SetupAPI layout; a 64-bit host is required.");
    }
    static string Property(IntPtr set, ref Device device, uint property, RegistryKey hardware=null, string valueName=null) {
        var data=new byte[4096]; uint type, required;
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref device, property, out type, data, (uint)data.Length, out required)) {
            int error=Marshal.GetLastWin32Error();
            if (error==13 && hardware!=null && !hardware.GetValueNames().Contains(valueName,StringComparer.OrdinalIgnoreCase)) return null;
            throw new Win32Exception(error);
        }
        if (type!=1 || required<2 || required>data.Length || (required&1)!=0) throw new InvalidDataException("Invalid device property.");
        return Encoding.Unicode.GetString(data,0,(int)required).TrimEnd('\0');
    }
    static string RegistryString(RegistryKey key, string name, bool allowEmpty=false) {
        if (!key.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase)) return null;
        var value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (value==null || (!allowEmpty && String.IsNullOrWhiteSpace(value)) || value.Length>1024) throw new InvalidDataException("Invalid installed driver metadata: "+name);
        return value;
    }
    static string Hash(string path) { using (var file=File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    public static Model Read(string id) {
        CheckLayout(); var set=NewSet(); var device=NewDevice();
        try {
            if (!SetupDiOpenDeviceInfoW(set,id,IntPtr.Zero,0,ref device)) {
                int error=Marshal.GetLastWin32Error();
                if (error==unchecked((int)0xe000020b)) return new Model { Present=false };
                throw new Win32Exception(error);
            }
            uint status, problem; uint result=CM_Get_DevNode_Status(out status,out problem,device.DevInst,0);
            if (result==13) return new Model { Present=false };
            if (result!=0) throw new InvalidOperationException("Cannot confirm target device presence.");
            string cls=Property(set,ref device,8);
            var handle=SetupDiOpenDevRegKey(set,ref device,1,0,1,0x20019);
            if (handle==Invalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            string driverKey,service;
            using (var hardware=RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(handle,true))) {
                driverKey=Property(set,ref device,9,hardware,"Driver");
                service=Property(set,ref device,4,hardware,"Service");
            }
            var model=new Model { Present=true, Problem=problem, ReadState="unbound", ClassGuid=cls, Service=service, DriverKey=driverKey };
            if (driverKey==null) return model;
            if (!System.Text.RegularExpressions.Regex.IsMatch(driverKey,@"^\{[0-9a-fA-F-]{36}\}\\[0-9]{4}$")) throw new InvalidDataException("Invalid driver registry path.");
            model.ReadState="incomplete";
            using (var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+driverKey)) {
                if (key==null) return model;
                string inf=RegistryString(key,"InfPath");
                if (inf!=null) {
                    if (Path.GetFileName(inf)!=inf || !inf.EndsWith(".inf",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid installed INF name.");
                    model.InfPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"INF",inf);
                    try { model.InfHash=Hash(model.InfPath); } catch (FileNotFoundException) { }
                }
                model.Section=RegistryString(key,"InfSection");
                model.SectionExtension=RegistryString(key,"InfSectionExt",true) ?? "";
                model.Provider=RegistryString(key,"ProviderName"); model.Version=RegistryString(key,"DriverVersion");
                model.MatchingId=RegistryString(key,"MatchingDeviceId");
                if (model.InfHash!=null && model.Section!=null && model.Provider!=null && model.Version!=null && model.MatchingId!=null && service!=null) model.ReadState="bound";
                return model;
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }
    static bool Equal(string a,string b) { return String.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
    public static bool Same(Model a,Model b) {
        if (b!=null && (b.ReadState=="unbound" || b.ReadState=="incomplete")) {
            return a!=null && a.Present && b.Present && a.ReadState==b.ReadState && a.Problem==b.Problem &&
                Equal(a.DriverKey,b.DriverKey) && Equal(a.ClassGuid,b.ClassGuid) && Equal(a.Service,b.Service) &&
                Equal(a.InfPath,b.InfPath) && Equal(a.InfHash,b.InfHash) && Equal(a.Section,b.Section) &&
                Equal(a.SectionExtension,b.SectionExtension) && Equal(a.Provider,b.Provider) && Equal(a.Version,b.Version) && Equal(a.MatchingId,b.MatchingId);
        }
        return a!=null && b!=null && a.Present && b.Present && Equal(Path.GetFileName(a.InfPath),Path.GetFileName(b.InfPath)) &&
            Equal(a.InfHash,b.InfHash) && Equal(a.Section+a.SectionExtension,b.Section+b.SectionExtension) && Equal(a.Provider,b.Provider) &&
            Equal(a.Version,b.Version) && Equal(a.ClassGuid,b.ClassGuid) && Equal(a.Service,b.Service) && Equal(a.MatchingId,b.MatchingId);
    }
    public static string Stage(string inf) {
        var destination=new StringBuilder(260); uint required;
        Check(SetupCopyOEMInfW(inf,null,1,0,destination,(uint)destination.Capacity,out required,IntPtr.Zero));
        if (destination.Length==0) throw new InvalidDataException("Staging did not return the published INF.");
        return destination.ToString();
    }
    public static bool Install(string id, Model wanted, Model expected) {
        CheckLayout();
        if (!Equal(Hash(wanted.InfPath),wanted.InfHash)) throw new InvalidDataException("Driver INF changed before selection.");
        var set=NewSet(); var device=NewDevice(); IntPtr inf=Invalid;
        try {
            var parameters=new Parameters { Size=(uint)Marshal.SizeOf<Parameters>() };
            Check(SetupDiGetDeviceInstallParamsW(set,IntPtr.Zero,ref parameters));
            parameters.Flags|=0x10000; parameters.FlagsEx|=ClassDriverFlagsEx; parameters.DriverPath=wanted.InfPath;
            Check(SetupDiSetDeviceInstallParamsW(set,IntPtr.Zero,ref parameters));
            Check(SetupDiBuildDriverInfoList(set,IntPtr.Zero,1));
            Check(SetupDiOpenDeviceInfoW(set,id,IntPtr.Zero,2,ref device));
            uint line; inf=SetupOpenInfFileW(wanted.InfPath,null,2,out line); if (inf==Invalid) throw new Win32Exception();
            Driver selected=default(Driver); int matches=0; bool complete=false;
            for (uint index=0;index<4096;index++) {
                var driver=new Driver { Size=(uint)Marshal.SizeOf<Driver>() };
                if (!SetupDiEnumDriverInfoW(set,ref device,1,index,ref driver)) {
                    int error=Marshal.GetLastWin32Error(); if (error==259) { complete=true; break; } throw new Win32Exception(error);
                }
                string version=String.Format("{0}.{1}.{2}.{3}",driver.Version>>48,(driver.Version>>32)&65535,(driver.Version>>16)&65535,driver.Version&65535);
                if (!Equal(driver.Provider,wanted.Provider) || !Equal(version,wanted.Version)) continue;
                uint required; SetupDiGetDriverInfoDetailW(set,ref device,ref driver,IntPtr.Zero,0,out required);
                if (required<1584 || required>65536) throw new InvalidDataException("Invalid native driver detail size.");
                var buffer=Marshal.AllocHGlobal((int)required);
                try {
                    Marshal.WriteInt32(buffer,Marshal.SizeOf<Detail>());
                    Check(SetupDiGetDriverInfoDetailW(set,ref device,ref driver,buffer,required,out required));
                    var detail=Marshal.PtrToStructure<Detail>(buffer);
                    var actual=new StringBuilder(260); uint length;
                    Check(SetupDiGetActualSectionToInstallW(inf,detail.Section,actual,260,out length,IntPtr.Zero));
                    string ids=Marshal.PtrToStringUni(IntPtr.Add(buffer,1576),((int)required-1576)/2);
                    if (Equal(Hash(detail.InfPath),wanted.InfHash) && Equal(actual.ToString(),wanted.Section+wanted.SectionExtension) &&
                        ids.Split('\0').Any(value=>Equal(value,wanted.MatchingId))) { selected=driver; matches++; }
                } finally { Marshal.FreeHGlobal(buffer); }
            }
            if (!complete || matches!=1) throw new InvalidOperationException("Expected exactly one pinned driver model; refusing ambiguous/missing selection.");
            if (!Same(Read(id),expected)) throw new InvalidOperationException("Current device driver changed before native selection.");
            Check(SetupDiSetSelectedDriverW(set,ref device,ref selected));
            bool reboot; Check(DiInstallDevice(IntPtr.Zero,set,ref device,ref selected,0,out reboot));
            return reboot;
        } finally { if (inf!=Invalid) SetupCloseInfFile(inf); SetupDiDestroyDeviceInfoList(set); }
    }
}
}
'@
    [ReimsUsbDriverBinding.Native]::CheckLayout()
}

function Get-ReimsUsbDriverBindingOperations {
    @{
        Read={ param($id) Initialize-ReimsUsbDriverBindingNative; [ReimsUsbDriverBinding.Native]::Read($id) | ConvertTo-Json | ConvertFrom-Json -AsHashtable }
        Stage={ param($inf) Assert-ReimsUsbDriverBindingPackage $inf; Initialize-ReimsUsbDriverBindingNative; [ReimsUsbDriverBinding.Native]::Stage($inf) }
        Install={ param($id,$wanted,$expected)
            Initialize-ReimsUsbDriverBindingNative
            $models=foreach($value in @($wanted,$expected)) {
                $model=[ReimsUsbDriverBinding.Model]::new()
                foreach($key in $value.Keys) { if($model.PSObject.Properties[$key]) { $model.$key=$value[$key] } }
                $model
            }
            [ReimsUsbDriverBinding.Native]::Install($id,$models[0],$models[1])
        }
    }
}

function Assert-ReimsUsbDriverBindingPackage([string]$InfPath) {
    $root=Split-Path ([IO.Path]::GetFullPath($InfPath))
    $pins=@{
        'libusb0.inf'='62abab852a7fe61cfc9490a57908ed2793f3681575fabfb52987feb44fd51aa3'
        'libusb0.cat'='8556e3387cede278353d0f6bbd74197f0ed43186e4891b1114491855bb9c66ab'
        'amd64/libusb0.sys'='cda0070fe4f7c5de5d3bb06fa6d7e29e6f6740c0f2fa319fb5c1af235d78d616'
        'amd64/libusb0.dll'='0cd18719d5701e9cc642fc93a83f13830931d6f29f664fedbdf212bcb7c349bd'
        'x86/libusb0_x86.dll'='f56c6f28bfb864761cebc05494b56af8bed97493f3b6e7b19afcb8f1abcd2cbe'
    }
    if ([IO.Path]::GetFileName($InfPath) -ine 'libusb0.inf') { throw 'Expected the unchanged official libusb0.inf.' }
    foreach($name in $pins.Keys) {
        $file=Get-Item -LiteralPath (Join-Path $root $name) -ErrorAction Stop
        if($file.PSIsContainer -or $file.Attributes -band [IO.FileAttributes]::ReparsePoint -or
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $pins[$name]) { throw 'Official driver package pin mismatch.' }
    }
}

function Assert-ReimsUsbDriverBindingInstance([string[]]$InstanceId) {
    if($InstanceId.Count -ne 1 -or $InstanceId[0].Length -gt 512 -or
        $InstanceId[0] -notmatch '^USB\\VID_05AC&PID_[0-9A-F]{4}(?:&REV_[0-9A-F]{4})?\\[^\\\x00-\x20*?]{1,256}$') {
        throw 'Exactly one complete physical Apple USB parent instance is required; interfaces and wildcards are refused.'
    }
    $InstanceId[0]
}

function Test-ReimsUsbDriverBindingModel($Actual,$Expected,[switch]$IgnoreClass) {
    if(!$Actual -or !$Expected -or !$Actual.Present -or !$Expected.Present) { return $false }
    if($Actual['ReadState'] -in @('unbound','incomplete')) { return $false }
    foreach($name in @('InfHash','Provider','Version','Service','MatchingId')) {
        if([string]$Actual[$name] -ine [string]$Expected[$name]) { return $false }
    }
    if([IO.Path]::GetFileName($Actual.InfPath) -ine [IO.Path]::GetFileName($Expected.InfPath) -or
        ($Actual.Section+$Actual.SectionExtension) -ine ($Expected.Section+$Expected.SectionExtension)) { return $false }
    $IgnoreClass -or $Actual.ClassGuid -ieq $Expected.ClassGuid
}

function Assert-ReimsUsbDriverBindingModel($Model) {
    if($Model['ReadState'] -and $Model.ReadState -cne 'bound') { throw 'A complete original driver model is required.' }
    foreach($key in @('InfPath','InfHash','Section','Provider','Version','ClassGuid','Service','MatchingId')) {
        if($Model[$key] -isnot [string] -or !$Model[$key].Length -or $Model[$key].Length -gt 1024) { throw 'Invalid typed driver binding model.' }
    }
    if($Model.Present -isnot [bool] -or !$Model.Present -or !($Model.Problem -is [int] -or $Model.Problem -is [long] -or $Model.Problem -is [uint32]) -or $Model.InfHash -notmatch '^[0-9a-fA-F]{64}$' -or
        ![IO.Path]::IsPathFullyQualified($Model.InfPath) -or [IO.Path]::GetExtension($Model.InfPath) -ine '.inf' -or
        $Model.SectionExtension -isnot [string]) { throw 'Invalid driver binding identity.' }
    [void][guid]::Parse($Model.ClassGuid); [void][version]::Parse($Model.Version)
}

function Assert-ReimsUsbDriverBindingReceiptPath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    for($part=$full;$part;$part=Split-Path -Parent $part) {
        if(Test-Path -LiteralPath $part) {
            $item=Get-Item -LiteralPath $part -Force
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint -or $part -eq $full -and $item.PSIsContainer) { throw 'Driver binding receipt must be an ordinary file without reparse ancestors.' }
        }
    }
    $full
}

function Read-ReimsUsbDriverBindingReceipt([string]$Path) {
    $Path=Assert-ReimsUsbDriverBindingReceiptPath $Path
    if(!(Test-Path -LiteralPath $Path)) { return $null }
    $file=Get-Item -LiteralPath $Path
    if($file.PSIsContainer -or $file.Attributes -band [IO.FileAttributes]::ReparsePoint -or $file.Length -gt 65536) { throw 'Invalid driver binding receipt file.' }
    $value=[IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json -AsHashtable -Depth 16
    if(!($value.schema -is [int] -or $value.schema -is [long]) -or $value.schema -ne 1 -or $value.instance_id -isnot [string] -or $value.mutation_attempted -isnot [bool] -or $value.needs_reboot -isnot [bool] -or
        $value.phase -notin @('prepared','staged','binding','bound','reboot-required','restoring','restored','restore-pending','restore-reboot-required','failed','rollback-failed')) { throw 'Invalid driver binding receipt.' }
    Assert-ReimsUsbDriverBindingModel $value.original; Assert-ReimsUsbDriverBindingModel $value.target
    [void](Assert-ReimsUsbDriverBindingInstance @($value.instance_id))
    if($value.target.InfHash -ine '62abab852a7fe61cfc9490a57908ed2793f3681575fabfb52987feb44fd51aa3' -or
        $value.target.Service -ine 'libusb0' -or $value.target.Version -ne '1.4.0.2' -or $value.original.Service -ieq 'libusb0' -or
        $value.target.Provider -ine 'libusb-win32' -or $value.target.ClassGuid -ine '{EB781AAF-9C70-4523-A5DF-642A87ECA567}' -or
        ($value.target.Section+$value.target.SectionExtension) -ine 'LIBUSB_WIN32_DEV_NEW.NTAMD64' -or $value.target.MatchingId -ine 'USB\VID_0B05&PID_190E') { throw 'Receipt target is not the pinned signed driver model.' }
    $value
}

function Write-ReimsUsbDriverBindingReceipt([string]$Path,$Value) {
    $Path=Assert-ReimsUsbDriverBindingReceiptPath $Path
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    [void](Assert-ReimsUsbDriverBindingReceiptPath $Path)
    if(Test-Path -LiteralPath $Path) { [void](Read-ReimsUsbDriverBindingReceipt $Path) }
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 16))
        $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($temporary,$Path,$true)
    } finally { if(Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

function Invoke-ReimsUsbDriverBindingLock([string]$Id,[scriptblock]$Action) {
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Id.ToUpperInvariant())))
    $mutex=[Threading.Mutex]::new($false,"Global\ReimsManager.UsbDriverBinding.$hash"); $held=$false
    try {
        try { $held=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held=$true }
        if(!$held) { throw 'Another driver binding operation owns this device.' }
        & $Action
    } finally { if($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}

function Test-ReimsUsbDriverBindingRecovery($Current,$Receipt) {
    if(!$Current.Present -or $Current['ReadState'] -cnotin @('unbound','incomplete') -or !$Receipt.mutation_attempted -or
        $Receipt.phase -in @('prepared','staged','restored') -or $Current.ClassGuid -notin @($Receipt.original.ClassGuid,$Receipt.target.ClassGuid)) { return $false }
    if($Current.ReadState -ceq 'unbound' -and $Current['DriverKey']) { return $false }
    if($Current.ReadState -ceq 'incomplete') {
        if($Current['DriverKey'] -notmatch '^\{[0-9a-fA-F-]{36}\}\\[0-9]{4}$') { return $false }
        if($Current.DriverKey -ine $Receipt.original['DriverKey'] -and $Current.DriverKey.Split('\')[0] -ine $Receipt.target.ClassGuid) { return $false }
    }
    foreach($known in @($Receipt.original,$Receipt.target)) {
        $matches=$true
        foreach($name in @('Service','InfHash','Provider','Version','MatchingId')) {
            if($null -ne $Current[$name] -and $Current[$name] -ine $known[$name]) { $matches=$false }
        }
        if($Current['InfPath'] -and [IO.Path]::GetFileName($Current.InfPath) -ine [IO.Path]::GetFileName($known.InfPath)) { $matches=$false }
        if($Current['Section'] -and ($Current.Section+$Current.SectionExtension) -ine ($known.Section+$known.SectionExtension) -and
            !($Current.Section -ieq $known.Section -and !$Current.SectionExtension)) { $matches=$false }
        if($matches) { return $true }
    }
    return $false
}

function Test-ReimsUsbDriverBindingSameProviderReplacement($Actual,$Original) {
    if(!$Actual -or !$Original -or !$Actual.Present -or !$Original.Present) { return $false }
    if(($Actual['ReadState'] -and $Actual['ReadState'] -cne 'bound') -or $Actual.Problem -ne 0) { return $false }
    if([string]::IsNullOrWhiteSpace($Actual.Service) -or $Actual.Service -ieq 'libusb0') { return $false }
    if($Actual.Provider -ine $Original.Provider) { return $false }
    $sameService=$Actual.Service -ieq $Original.Service -and $Actual.ClassGuid -ieq $Original.ClassGuid
    $appleService=$Actual.Provider -ieq 'Apple, Inc.' -and
        $Actual.Service -iin @('usbccgp','USBAAPL64','usbaapl') -and
        $Original.Service -iin @('usbccgp','USBAAPL64','usbaapl')
    $sameService -or $appleService
}

function Restore-ReimsUsbDriverBindingCore([string]$Id,[string]$ReceiptPath,$Receipt,$Operations) {
    $current=& $Operations.Read $Id
    if($Receipt.phase -eq 'restored') {
        if(Test-ReimsUsbDriverBindingModel $current $Receipt.original) {
            return @{state='restored';needs_reboot=$false;restored=$true;verified_model=$Receipt.original}
        }
        if(Test-ReimsUsbDriverBindingSameProviderReplacement $current $Receipt.original) {
            return @{state='restored';needs_reboot=$false;restored=$true;verified_model=$current}
        }
        throw 'Completed receipt no longer owns this driver binding.'
    }
    if(!$current.Present) {
        $Receipt.phase='restore-pending'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $Receipt
        return @{state='restore-pending';needs_reboot=[bool]$Receipt.needs_reboot;restored=$false}
    }
    if((Test-ReimsUsbDriverBindingModel $current $Receipt.original) -and $current.Problem -eq 0 -and !$Receipt.needs_reboot) {
        $Receipt.phase='restored'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $Receipt
        return @{state='restored';needs_reboot=$false;restored=$true;verified_model=$Receipt.original}
    }
    $updatedOriginal=$Receipt.mutation_attempted -and (Test-ReimsUsbDriverBindingSameProviderReplacement $current $Receipt.original)
    if($updatedOriginal) {
        if($Receipt.needs_reboot) {
            $Receipt.phase='restore-reboot-required'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $Receipt
            return @{state=$Receipt.phase;needs_reboot=$true;restored=$false;verified_model=$current}
        }
        $Receipt.phase='restored'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $Receipt
        return @{state='restored';needs_reboot=$false;restored=$true;verified_model=$current}
    }
    $partial=(Test-ReimsUsbDriverBindingModel $current $Receipt.original -IgnoreClass) -and $current.ClassGuid -ieq $Receipt.target.ClassGuid
    $unbound=Test-ReimsUsbDriverBindingRecovery $current $Receipt
    if(!$Receipt.mutation_attempted) { throw 'This receipt never changed the device; refusing to restore a later binding.' }
    if(!(Test-ReimsUsbDriverBindingModel $current $Receipt.target) -and !(Test-ReimsUsbDriverBindingModel $current $Receipt.original) -and
        !($partial -and $Receipt.phase -in @('binding','restoring','rollback-failed','failed')) -and !$unbound) { throw 'Current driver is foreign to this receipt; refusing to overwrite it.' }
    $Receipt.phase='restoring'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $Receipt
    $reboot=& $Operations.Install $Id $Receipt.original $current
    $Receipt.needs_reboot=[bool]$Receipt.needs_reboot -or [bool]$reboot
    if($Receipt.needs_reboot) {
        $Receipt.phase='restore-reboot-required'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $Receipt
        return @{state=$Receipt.phase;needs_reboot=$true;restored=$false;verified_model=$Receipt.original}
    }
    $restored=& $Operations.Read $Id
    if(!(Test-ReimsUsbDriverBindingModel $restored $Receipt.original) -or $restored.Problem -ne 0) { throw 'Exact original driver restoration was not confirmed healthy.' }
    $Receipt.phase='restored'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $Receipt
    @{state='restored';needs_reboot=$false;restored=$true;verified_model=$Receipt.original}
}

function Set-ReimsUsbDriverBinding {
    param([Parameter(Mandatory)][string[]]$InstanceId,[Parameter(Mandatory)][string]$ReceiptPath,
        [Parameter(Mandatory)][string]$PackageInf,[hashtable]$Operations=(Get-ReimsUsbDriverBindingOperations))
    $id=Assert-ReimsUsbDriverBindingInstance $InstanceId
    Invoke-ReimsUsbDriverBindingLock $id {
        $receipt=Read-ReimsUsbDriverBindingReceipt $ReceiptPath
        if($receipt -and $receipt.instance_id -ine $id) { throw 'Receipt belongs to another device.' }
        if($receipt -and $receipt.phase -ne 'restored') {
            $current=& $Operations.Read $id
            if($receipt.phase -eq 'bound' -and (Test-ReimsUsbDriverBindingModel $current $receipt.target)) {
                if($current.Problem -ne 0 -or $receipt.needs_reboot) { throw 'Owned driver binding is not healthy or still requires a reboot.' }
                return @{state='bound';needs_reboot=$false;bound=$true}
            }
            throw 'An unfinished driver transaction must be restored first.'
        }
        $original=& $Operations.Read $id; Assert-ReimsUsbDriverBindingModel $original
        if($original.Problem -ne 0) { throw 'Original driver is not healthy; refusing automatic replacement.' }
        if($original.Service -ieq 'libusb0') { throw 'An unowned libusb0 binding cannot become the original driver.' }
        $target=@{Present=$true;Problem=0;InfPath=[IO.Path]::GetFullPath($PackageInf);InfHash='62abab852a7fe61cfc9490a57908ed2793f3681575fabfb52987feb44fd51aa3';
            Section='LIBUSB_WIN32_DEV_NEW';SectionExtension='.NTAMD64';Provider='libusb-win32';Version='1.4.0.2';ClassGuid='{EB781AAF-9C70-4523-A5DF-642A87ECA567}';Service='libusb0';MatchingId='USB\VID_0B05&PID_190E'}
        $receipt=@{schema=1;instance_id=$id;phase='prepared';original=$original;target=$target;needs_reboot=$false;mutation_attempted=$false}
        Write-ReimsUsbDriverBindingReceipt $ReceiptPath $receipt
        $attempted=$false
        try {
            $target.InfPath=& $Operations.Stage $PackageInf
            $receipt.phase='staged'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $receipt
            $current=& $Operations.Read $id
            if(!(Test-ReimsUsbDriverBindingModel $current $original)) { throw 'Current driver changed while staging; refusing mutation.' }
            $receipt.phase='binding'; $receipt.mutation_attempted=$true; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $receipt; $attempted=$true
            $reboot=& $Operations.Install $id $target $original
            if($reboot) {
                $receipt.needs_reboot=$true;$receipt.phase='reboot-required'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $receipt
                return @{state='reboot-required';needs_reboot=$true;bound=$false}
            }
            $bound=& $Operations.Read $id
            if(!(Test-ReimsUsbDriverBindingModel $bound $target) -or $bound.Problem -ne 0) { throw 'Target driver binding was not confirmed healthy.' }
            $receipt.phase='bound'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $receipt
            @{state='bound';needs_reboot=$false;bound=$true}
        } catch {
            $failure=$_.Exception.Message
            if($attempted) {
                try { $rollback=Restore-ReimsUsbDriverBindingCore $id $ReceiptPath $receipt $Operations; $failure+=' Restoration state: '+$rollback.state }
                catch { $receipt.phase='rollback-failed'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $receipt; $failure+=' Exact restoration failed: '+$_.Exception.Message }
            } else { $receipt.phase='failed'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $receipt }
            throw $failure
        }
    }
}

function Restore-ReimsUsbDriverBinding {
    param([Parameter(Mandatory)][string[]]$InstanceId,[Parameter(Mandatory)][string]$ReceiptPath,
        [hashtable]$Operations=(Get-ReimsUsbDriverBindingOperations))
    $id=Assert-ReimsUsbDriverBindingInstance $InstanceId
    Invoke-ReimsUsbDriverBindingLock $id {
        $receipt=Read-ReimsUsbDriverBindingReceipt $ReceiptPath
        if(!$receipt -or $receipt.instance_id -ine $id) { throw 'No matching original driver receipt is available.' }
        try { Restore-ReimsUsbDriverBindingCore $id $ReceiptPath $receipt $Operations }
        catch {
            if($receipt.phase -eq 'restoring') { $receipt.phase='rollback-failed'; Write-ReimsUsbDriverBindingReceipt $ReceiptPath $receipt }
            throw
        }
    }
}
