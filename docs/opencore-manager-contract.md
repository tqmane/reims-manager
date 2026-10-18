# OpenCore / guest bridge backend contract

The UI invokes the packaged PowerShell 7 backend with an argument list:

```powershell
scripts/manager-backend.ps1 -Root '<engine-directory>' -VmDirectory '<selected-VM-data-directory>' -ToolsRoot '<manager-directory>/runtimes' -Action EnableGuestBridge
```

The UI must supply the selected VM's persistent data directory explicitly;
never derive it from the current working directory, checkout or application
location. `Root` selects the engine, not the VM library. The optional
`-Qemu <engine-executable>` is used by launch operations, not bridge actions.
Paths may contain spaces and Unicode. No administrator elevation or guest
password is needed for these offline firmware operations.
The application package must retain `backend/*.ps1`, `backend/*.json` and
`backend/opencore/*.py` under `scripts/` with the `opencore/` subdirectory;
the new `firmware.ps1` and `opencore-backup.ps1` helpers are dependencies of
the existing actions, not standalone UI actions.

| Action | Additional arguments | Behavior |
| --- | --- | --- |
| `EnableGuestBridge` | none | Fetch the qualified standalone addon and apply it to a new EFI copy. |
| `DisableGuestBridge` | none | Disable the bridge/activation flags in a new EFI copy without downloading. |
| `ApplyBootProfile` | none | Explicitly apply the APFS/HFS scan profile and rebuild the active EFI. |
| `PrepareUpdates` | none | Apply the existing pinned update-detection kext/configuration path. |

All four require QEMU to be fully stopped. A guest reporting `shutdown`
while its retained QEMU process still exists is not fully stopped. The UI
can expose its existing normal `Exit` operation first; do not substitute a
force-kill for firmware preparation.

Actions return progress/result text on stdout; a nonzero exit is failure,
with details on stderr. These action outputs are not JSON. The existing
`Status` and `GetIdentity` actions provide JSON for refresh after completion.
Do not call `GenerateIdentity` as part of bridge/profile application.
The manager actions use online bridge acquisition. Offline archive/digest
arguments belong to the direct `prepare-guest-bridge.ps1` CLI and are not
additional parameters of `manager-backend.ps1`.

For readiness text, the distributed `scripts/guest-bridge-lock.json` carries
`release_ready`, `scope`, `runtime_validation`, `qualified_kernels` and
`required_engine_id`. Current candidate metadata must not be presented as
working generic support. The backend itself rejects enable/publication
until qualified global Metal routing and runtime ABI checks are recorded,
and rejects a mismatched host engine. `generic-metal` means all clients,
with no process-name or suffix policy. The reviewed global `700379ce` pin
records native 22-stage WebGL acceptance on 25.4.0 and 25.6.0 with engine 2.
It discloses variable frame pacing and does not promise 60 FPS.
No bridge readiness fields have been added to `Status`; absent metadata
must remain unconfirmed in the UI, not inferred from an enabled EFI entry.

## New-VM onboarding

`GetPrerequisites` accepts `Root`, `ToolsRoot`, `Cpus` and `MemoryMiB` without
`VmDirectory`. It runs before VM environment initialization, writes no
files, executes no GPU workloads, and returns JSON:

```json
{"ready":true,"restart_required":false,"checks":[{"id":"usb","label":"USB passthrough (optional)","required":false,"ready":false,"message":"Install UsbDk if needed","action":"InstallUsbDk"}],"plan":{"cpus":4,"memory_mib":4096,"available_memory_mib":2048,"start_ready":false}}
```

Example numbers are synthetic. Checks use IDs `whpx`, `vulkan`, `openssh`,
`engine`, `runtimes`, `guest_addon`, `usb`, `capacity`; a host query failure
returns `host` instead. Missing `required` means required. Only `EnableWhpx`
and `InstallUsbDk` are action hints. Both require explicit elevation; neither
restarts Windows. OpenSSH and GPU driver failures provide guided messages.
Vulkan readiness means an ICD registration/file exists, not GPU execution
or performance acceptance. Addon readiness queries its exact public release;
unpublished/offline/unqualified assets block `ready` and never claim success.
No tokens are requested. Cache/download writes happen only in actual setup.
Engine readiness runs the complete immutable file/hash inventory check;
it does not accept only the presence of QEMU or its top-level manifest.
Release pins resolve to the fixed public tag/asset URL without any GitHub
API call, credentials or additional ZIP SHA requirement. Addon availability
uses a read-only HEAD request. Optional `bytes` checks detect truncated
downloads before extraction; fixed source/package validation still applies.
Unpublished assets/network failures remain explicit errors, with no other
tag or generic-engine fallback. Existing offline sidecar checks are unchanged.

`plan.start_ready` additionally requires current free RAM to cover the
requested guest allocation plus 1024 MiB for the host, no CPU oversubscription
and no reported restart requirement. Low capacity is optional: VM creation
may continue while automatic start must wait. These checks are a snapshot;
refresh immediately before starting. No host process is stopped to free RAM.

The UI installs/selects the immutable engine with the existing installer
first, saves its preferences before a required reboot, then calls `Setup`
with ISO, VM directory, DiskGiB, Cpus and MemoryMiB. Setup honors that hardware
plan and completes official OpenCore/firmware, identity, disks, ReimsTools/
SSH bundle and the qualified Lilu/bridge. It does not install macOS or start
a VM. Those remain the next UI/user step. Preparing guest tools does not
silently grant guest privileges; the guest's setup.command/pairing remains
explicit after macOS installation.

Only after all configuration succeeds does Setup write
`state/setup-preparation.json` with `schema=1`, `complete=true`, engine/addon
IDs, `macos_installation="user"` and `prepared_utc`. Existing selected config,
OpenCore manifest and previous completion marker are saved in
`backups/setup-*` and restored if setup fails. Fresh disks/EFI/identity are
retained for retry, but incomplete active config/completion markers are
removed. Existing identities and writable NVRAM are not regenerated.

## Persistent VM data

All paths below are relative to the explicit `VmDirectory`:

- `state/config.json`: active disk, EFI image and writable NVRAM paths.
- `state/opencore.json`: active plist/image, recorded matched OVMF pair and hashes.
- `state/opencore-identity.json`: saved VM identity.
- `backups/opencore/*.plist`: unique backups before identity/profile/account/update edits.
- `firmware/guest-bridge-*/`: new EFI image/root and prior metadata backups.
- `support/opencore/OpenCore-1.0.8-2011e8b7216e/`: verified extracted official tools, including `Utilities/ocvalidate/ocvalidate.exe` referenced by the manifest.
- `support/downloads`, `support/kexts`, `support/tmp`, `support/cache`, `support/diagnostics`: VM-owned downloads, extracted addons and runtime scratch/reports.

These are user data. Keep them with the VM outside developer `.work`
directories when registering/migrating the library. Changing the engine or
restarting the manager must not change `VmDirectory`. Existing files are
not moved by this backend change; migration is a separate operation.
Installed engines remain immutable, including their complete file inventory.
Portable runtime environment/cache/report writes go to the VM's `support`,
not engine-local `.work`. A portable engine requires an explicit VM directory
outside its package. Development source checkouts retain their own `.work`
build environment. OpenCore tools/downloads belong to VM support in either
mode. Do not persist old engine-cache `ocvalidate` paths during migration.

Existing firmware paths/hashes and identity are reused on rebuilds. A new
engine's OVMF does not replace an existing pair; an explicit incompatible
pair is refused. The bridge path switches validated EFI copies and retains
old images/metadata for rollback rather than editing an active image.
Explicit qualified `EnableGuestBridge` also migrates owned bridge/Lilu
entries from narrower bounds within the reviewed ABI family to the package
range. Custom Lilu policy is preserved or rejected if incompatible; unrelated
entries are untouched. Ordinary rebuilds and disable retain existing bounds.
