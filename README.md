# Reims Manager

A native Windows desktop manager for Reims QEMU macOS virtual machines.
.NET 10 WPF Fluent controls put the console, power controls, settings and
machine library in one application.

## Use

Download the Windows artifact from **Actions → Windows build**, extract the
ZIP, and run `ReimsManager.exe`. Packages include .NET, PowerShell and Python;
no separate installation of those runtimes is needed. Windows 11 x64 and a
Reims-enabled QEMU engine are required. Generic QEMU does not include
Reims graphics.

Choose **Set up engine** and select a new directory. The matching package
supplies QEMU, its native DLLs, official OpenCore, OVMF, the GPU ROM and ISO
tools. Setup handles downloads and integrity checks without manual checksum
entry. An already downloaded CI engine ZIP can also be selected.

Alternatively choose an existing engine's `qemu-system-x86_64.exe`. A Reims checkout is detected
from its parent folders; a packaged engine uses `reims-engine.json` at its
root. **Windows engine package** is a separate, manually triggered CI job;
normal manager builds stay small. See [engine setup](docs/engine-setup.md)
for pinned sources, the first artifact/publication step, and the distinction
between official OpenCore binaries and authored configuration patches.

- **New VM:** select its name/location, installation ISO and disk capacity.
  The installer can start immediately after creation.
- **Open:** select a `.reimsvm` descriptor for an existing machine.
- **Import disk:** select a stopped VM's VMDK, QCOW2 or RAW disk. The source
  remains intact and a managed copy is created separately.
- **Console:** operate macOS inside the manager or detach its window.
- **Settings:** manage CPU, memory, storage, ISO, networking, USB, audio and
  display. Disk and firmware changes require a fully stopped VM.
- **Sharing:** pair SSH with the guest to share text clipboard contents.
  Clipboard contents and guest passwords are not stored in logs.
- **OpenCore:** manage generated SMBIOS identity and supported Apple Account
  compatibility settings. Identity changes can require reauthentication.

USB transfer requires the appropriate Windows driver. Only selected devices
are registered for reconnection. NAT is the default; bridging needs a TAP
adapter and explicit bridge setup. Audio uses the host's default devices
and normal OS permissions. The current VM directory is always visible in
the footer, with a button to open it.

The library and preferences live in local app data, independently of the
engine folder. A `.reimsvm` file identifies its VM data directory. Changing
engines does not delete VM disks.

## Build and CI

Install a Windows .NET 10 SDK and PowerShell 7, then run:

```powershell
./tools/fetch-runtimes.ps1
./tools/build.ps1
```

The first step downloads pinned official runtime archives and verifies their
SHA256 values. The build works from those local archives, checks executable
signatures and includes their original license files. It does not install
runtime tools onto the build host.

The output is `artifacts/ReimsManager-win-x64.zip`, a SHA256 file and a
self-check result. Windows CI runs the same commands. Checks cover QMP
response correlation/errors, stale process identities, settings/profile
handling, argument quoting, settings-page rendering and backend syntax.

Startup options are `--root <engine-root> --qemu <qemu-executable>` and
`--open <machine.reimsvm>`. A `.reimsvm` path can also be passed alone.
`REIMS_MANAGER_HOME` selects isolated preferences for validation; ordinary
installations need no environment variables.

## Validation limits

CI does not boot macOS. WHPX, GPU, USB unplug/replug, audio and guest
filesystem behavior require a prepared host and guest. 4K is selectable,
but Intel Tahoe's known wide-display limitations still apply. The WebGL
bridge has separate OS/driver compatibility bounds; macOS updates require
separate qualification.

This preview application is not code-signed. Apple software, macOS
installers, personal VM images, SMBIOS identities and credentials are not
included. See [NOTICE.md](NOTICE.md) for source and runtime licenses.
