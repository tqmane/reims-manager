# Optional one-click TAP setup

`manager-backend.ps1 -Action InstallTap -Root <engine>` runs optional host
driver preparation without a file dialog. It requires explicit administrator
elevation. `VmDirectory` is optional; if supplied, that VM must be fully
stopped. No driver is installed just by reading prerequisites/inventory.
This action never creates a bridge, changes VM networking, or restarts
Windows. UI label: **TAP ドライバーを自動導入**.

With no `Installer`, the helper downloads official OpenVPN tap-windows6
9.27.0 `dist.win10.zip`. `backend/tap-lock.json` pins the public HTTPS URL
and the reviewed archive digest. The archive is read-locked during extraction,
and the four selected AMD64 files are read-locked through installation.
Microsoft Authenticode signatures on devcon.exe, tap0901.cat and tap0901.sys,
the Device Console file identity and INF version/catalog are checked before
execution. Windows validates the INF against the signed catalog during
installation. No SDK, shell-built command, legacy EXE installer, bridge or
other VPN configuration is used. Optional offline `-Installer <official.zip>`
uses the same pin/signature checks. This driver-specific digest requirement
does not change the API-free engine/guest ZIP download contract.

Existing TAP/VPN adapters are protected: `devcon install` can update all
matching hardware IDs, so it is refused while unselected existing devices
are present. `-Adapter <TAP interface GUID or friendly name>` explicitly
reuses a selected dedicated adapter without any install or device change.
It does not claim ownership of another application's adapter. An adapter
already recorded as Reims-owned is reused automatically. The helper checks
again after package verification in case another TAP appeared meanwhile.

Success returns JSON only:

```json
{"schema":1,"created":true,"reused":false,"owned":true,"adapter_id":"11111111-1111-4111-8111-111111111111","adapter_name":"Reims TAP 11111111","pnp_instance_id":"ROOT\\NET\\0001","restart_required":false,"bridge_created":false}
```

IDs/names above are synthetic. Devcon exit 1 is successful preparation with
`restart_required=true`; adapter ID/name may then be null. The UI must ask
the user to save work and restart Windows, never restart it automatically.
Rechecking/reusing after reboot resolves the recorded PnP identity without
adding another adapter. Nonzero failures other than 1 are explicit errors.
Re-run InstallTap after a reported restart to complete naming if the adapter
could not yet be enumerated. Only a newly created adapter (including that
pending creation) is named `Reims TAP <first eight GUID hex digits>` for
compatibility with QEMU's Windows ANSI TAP-name lookup. An explicitly reused
adapter or another VPN's names are not changed. GUID/PnP identity remains
the persistent key; Windows display-language names are not identity.

When `VmDirectory` is supplied, the backend parses the successful installer
JSON, requires schema 1, and writes the fixed VM-local
`state/tap-install-result.json` with the existing atomic JSON writer before
echoing the original JSON. Parsing/schema/write failures are operation
failures (nonzero process exit); the UI must read the file only after its
operation reports success, so a prior result cannot masquerade as a new one.
The UI bounds reads to 64 KiB and treats missing/invalid schema as unconfirmed.
This handoff supports elevated runas launches, whose stdout cannot be
redirected. No extra result-path argument is accepted. Without a VM, the
CLI's stdout-only behavior is unchanged.

Machine-wide private state is under the standard CommonApplicationData
directory, `ReimsManager/Drivers/tap-windows-9.27.0`: `tap-adapter.json`
records only an identified newly created PnP device, plus its interface GUID
and manual removal command; `last-install.log` retains native diagnostics.
Logs/device IDs and downloaded/extracted binaries are never committed.
Failure after successful creation removes only the exact proven instance
using `devcon remove @<PnP instance ID>`, never a hardware-ID wildcard.
Ambiguous/failed native creation does not authorize removing unknown devices.
Failed rollback reports the specific instance for manual Device Manager
recovery. CPU fixtures replace all native/driver/network operations; the
product helper has not installed a real driver during this change.
