# Portable Windows engine

## First launch

From an empty library, choose **macOS をセットアップ**. The guided flow installs
or validates an existing pinned engine, checks Windows prerequisites, then asks
for an ISO/IMG, a VM name and a destination. It prepares OpenCore, matched OVMF,
the GPU ROM, persistent identity, disks, guest tools and the qualified addon;
macOS installation and first-run account setup remain in the VM console.

The engine defaults to the current user's local application-data directory
under `Reims/Engines/<pinned-id>`. VMs default to Documents under
`Reims/Virtual Machines`, each with its descriptor and data in one VM folder.
Both locations have folder pickers and accept absolute paths on other writable
drives. A developer checkout or working directory is not a product default.
The disk defaults to a sparse 300 GiB image, memory to 4096 MiB, and CPU to at
most four of the host's logical processors.

Engine selection is saved before a prerequisite can request administrator
elevation or a Windows restart. The Manager never restarts Windows. Relaunch
and choose setup again to resume the prerequisite/ISO flow with the saved
engine; an installed engine is reused without downloading again. Reuse checks
the engine's complete immutable inventory before accepting it.

Required failures remain visible and block preparation. USB is optional and
TAP is unnecessary for the default NAT network. Current available RAM and the
requested CPU/memory plan are displayed separately from required features.
Insufficient RAM permits creating the VM while disabling automatic start;
capacity is checked again after preparation immediately before automatic boot.
No other application or VM is stopped to make room. Setup errors retain the
descriptor for retry while the backend restores/removes incomplete active
configuration rather than reporting a ready VM.

In Reims Manager choose **Set up engine**, choose a new directory on any
writable drive, then start setup. A VM can live in a different directory.
No developer checkout, MSYS2 installation, Rust, system Python or separate
PowerShell installation is required by the delivered application.

Setup looks up the engine release paired with this manager in
`tqmane/reims-manager`. Its published ZIP size and tag/asset are pinned in
`engine-lock.json`: installation downloads that public HTTPS URL directly,
without GitHub API access, authentication or a ZIP SHA requirement.
Anonymous API rate limits therefore do not block this engine download.
Missing assets/network errors and size mismatches remain errors. Extracted
package inventory/source pins and native probes still apply before atomic
installation. No different tag or generic QEMU is substituted on failure.
If the engine release has not been published,
download the ZIP inside the **Windows engine package** Actions artifact and
choose **Use downloaded engine ZIP**. The corresponding `.zip.sha256` file
from that same release or CI artifact must be beside the ZIP. No checksum
entry is required in the app; setup reads the required sidecar automatically.
CLI callers may instead supply `-Sha256 <expected-release-digest>`.
Missing, malformed, mismatched or wrong-filename digests fail before ZIP
extraction or native tool execution. The ZIP stays read-locked from the
digest check through extraction, preventing replacement during that step.
Private repository artifacts must be downloaded using an authenticated
browser. The app never asks for or stores a GitHub token.

An unavailable release is reported as unavailable. The manager does not
download generic QEMU as a substitute: it lacks the Reims device.

## Components and source pins

`backend/engine-lock.json` is the reviewed source of version pins. It selects
Reims `b8b4486c`, QEMU `7100064d` and metal2vulkan `ea5d6b87`, rather than
following moving `windows-native-tahoe` branches. The latter runtime is the
pin in Reims's Cargo manifest; later runtime development commits are not
silently substituted. Engine `engine-b8b4486c-7100064d-2` is paired with the
common Metal path. Guest bridge source, recorded qualification and limits are
independently pinned in `guest-bridge-lock.json`; setup checks release readiness
rather than assuming that any available engine/addon combination is qualified.

The engine package contains:

- `bin`: Reims QEMU, qemu-img/qemu-io, Reims's Vulkan library and their
  recursively discovered native DLL dependencies; the Vulkan loader and
  full 7-Zip command-line executable are included explicitly.
- `share/qemu`: required x86 firmware/option ROM data and a matched OVMF
  code/variables pair from official QEMU 11.0.3. These firmware files are
  independent of the patched QEMU executable's revision.
- `share/reims/ReimsVgpu.rom`: built from Reims's own firmware source.
- `downloads`: the official OpenCore 1.0.8 RELEASE archive, including its
  tools and licenses, fetched from acidanthera's release.
- `licenses`: Reims/QEMU/native dependency licenses and MSYS2 package
  versions. Native dependency sources are available from
  <https://github.com/msys2/MINGW-packages> and <https://repo.msys2.org/sources/>;
  QEMU sources from <https://github.com/tqmane/qemu-reims-vgpu>, Reims sources
  from <https://github.com/tqmane/reims-vgpu>, and runtime sources from
  <https://github.com/tqmane/metal2vulkan>. 7-Zip sources and license are at
  <https://github.com/ip7z/7zip/releases/tag/26.04>.

All manifest paths are relative to the engine directory. QEMU's `-L`,
disk tools, OVMF templates, ISO inspection and GPU ROM resolve through that
manifest. VM-local writable firmware and disks stay in the VM directory.
Changing the engine never overwrites the VM's NVRAM or SMBIOS identity.
Portable runtimes require a VM directory outside the engine package. Their
scratch/cache/report directories live in `<VM>/support`; verified OpenCore
tools are extracted to `<VM>/support/opencore/OpenCore-1.0.8-2011e8b7216e`.
The installed engine is not modified by OpenCore setup, so full package-file
verification remains valid afterwards. Development checkout build caches
continue to use that checkout's `.work` directory.
Rebuilding an existing VM reuses its recorded `firmware_code` and variables
template, checking both recorded hashes before changing configuration or
the EFI image. A newer engine's OVMF is selected only for new VMs. An
explicit `-FirmwareDirectory` is checked against the existing pair: differing
code or template is refused with a firmware/NVRAM migration explanation.
Configuration and kext rebuilds do not migrate OVMF or regenerate identity.
OpenCore edit backups live under the selected VM's `backups/opencore`, not
the engine's build/scratch directories. See the [UI/backend contract](opencore-manager-contract.md)
for persistent paths and the OpenCore/guest bridge action arguments.

Downloads and the file manifest are checked automatically. ZIP traversal,
duplicates, links, excessive extraction size, unknown source revisions and
missing/modified files are rejected before any native executable runs.
Native probes run with only Windows system directories on PATH. Setup
checks for WHPX **support in the executable**, not host hypervisor readiness;
it never boots a VM. A native probe exceeding its 30-second deadline has
only its owned process tree terminated, with a bounded wait before disposal.
Failed setup removes only its own staging directory.
Existing destination folders are never replaced. Use the explicit reuse option
for a complete installed engine; an incomplete destination needs another folder.
Windows may briefly keep an executable image locked after a probe exits;
the final directory rename retries for at most five seconds before failing.

## OpenCore binaries versus configuration patches

OpenCore 1.0.8 is the official release checked for this implementation. Its
EFI binaries are copied unchanged; this is not a custom OpenCore binary.
The authored configuration changes are kept as executable source:

- `backend/opencore/base.py`: derives a fresh VM configuration from the
  official `Docs/Sample.plist`, with iMac20,1, firmware quirks and drivers.
  Its `ScanPolicy=0x301` combines filesystem lock (bit 0), APFS (bit 8),
  and HFS (bit 9); FAT/ESP entries cannot recursively start OpenCore itself.
  Auxiliary entries remain visible for installers. Existing VMs keep their
  custom scan policy on ordinary rebuilds. **Repair boot entries** explicitly
  applies this scan profile, preserving identity and auxiliary visibility;
  the previous configuration is backed up first. Rebuilds follow the active
  configuration/image recorded in the VM's OpenCore manifest.
- `backend/opencore/apple-account.py`: the existing reversible Apple
  Account kernel-string compatibility patch, limited to Darwin **25.4.0**.
  It does not change SIP or blindly extend the OS version bounds.
- `backend/prepare-opencore.ps1`: identity editing/generation, backup,
  `ocvalidate`, and FAT16 EFI image creation. No personal identity is shipped.
- `backend/prepare-update-compatibility.ps1`: existing pinned official Lilu
  and RestrictEvents acquisition and idempotent update-detection settings.

`ReimsMetalCompat` is a separate authored Lilu kernel plugin, not an OpenCore
binary patch. Its complete source/build checks remain at the pinned Reims
revision under `guest/metal-compat`. Existing VM configurations keep that
plugin and its version bounds when rebuilt. The Windows engine job does
**not** build or silently install this macOS-compiled guest plugin. The
manager's OpenCore page fetches and applies the separate [guest bridge
addon](guest-bridge.md), built by its own macOS CI job from those exact
sources. Its release does not rebuild or replace the Windows engine.
Application is optional and requires a qualified generic implementation and
runtime ABI validation. Source `700379ce` records native WebGL acceptance on
Darwin 25.4.0 and 25.6.0 with engine 2. Family bounds are not a claim that
every OS version was tested, and 60 FPS is not guaranteed. Fresh setup
requires the qualified addon release and applies it automatically. Explicit
enable/reapply migrates owned bridge/Lilu
entries to the reviewed package range; ordinary rebuilds preserve bounds.
Native guest and browser checks are required before release promotion.

## Build and publication

Run **Windows engine package** manually. It checks out the exact Reims and
QEMU commits into a fresh private build tree, uses Rust 1.98.1 GNU, and runs
the pinned Reims Windows build recipe. Its QEMU clone is independent; it
never initializes a shared worktree's submodule. The option ROM and Reims
DLL are built together, and all native dependencies are collected.

At Reims `d8b9c2af`, `xtask/src/dist.rs::cmd_dist` passes the committed
`crates/reims-vgpu-c/include/reims_vgpu.h` to `dist/windows.rs`, which copies
it. This distribution build does not invoke external cbindgen. The separate
`cargo xtask header` and `cargo xtask check` paths do require cbindgen 0.29.4;
install it if those checks are added to the engine job. The ordinary Windows
bootstrap installs it for development, but is not needed by this CI recipe.

The workflow pins actions and project/firmware/OpenCore/7-Zip inputs. MSYS2
is a rolling toolchain: installed package versions are recorded in the
artifact. This is a repeatable build recipe, **not a claim of bit-identical
output across future MSYS2 updates**. CI must pass the package checks and
clean-PATH native probes before an artifact is uploaded.

For an equivalent local build, provide an MSYS2 UCRT64 installation with the
packages listed in `engine.yml`, Rustup, Git and a build-time 7-Zip:

```powershell
./tools/build-engine.ps1 -MsysRoot '<your-msys2-directory>'
```

The output is under `artifacts/engine`. Select the workflow's **publish**
option on `main` to create the pinned release after a successful build. It
never overwrites an existing tag or asset. Normal manager PR CI only runs
small offline fixtures and .NET self-checks; it does not build QEMU or boot
macOS. Updating a pin requires a new package ID and compatibility validation.

Windows 11 x64, enabled WHPX, and a compatible GPU driver exposing Vulkan
remain host requirements. The Vulkan ICD belongs to the GPU vendor and is
not copied from a developer machine. USB drivers and microphone permission
remain explicit host setup steps. No Apple binaries, macOS installer, guest
disk, serialized GPU data, credentials or personal SMBIOS values are shipped.
