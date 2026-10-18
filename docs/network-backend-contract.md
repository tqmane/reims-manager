# Physical uplink selection

`manager-backend.ps1 -Action GetNetworkUplinks -Root <engine>` is read-only
and requires no VM directory or elevation. It does not initialize the VM
environment, write engine files, install drivers or modify network bindings.

```json
{"schema":1,"uplinks":[{"id":"11111111-1111-4111-8111-111111111111","name":"Wireless","description":"Example WLAN","kind":"wifi","status":"Up","connected":true,"bridge_support":"unverified"}],"tap_adapters":[{"id":"22222222-2222-4222-8222-222222222222","name":"Internal TAP","description":"Example TAP-Windows Adapter","status":"Disconnected"}]}
```

Values above are synthetic. IDs are InterfaceGuid values without braces.
`kind` is `wifi` (interface type 71), `ethernet` (6), or `other`. Physical
uplinks are hardware interfaces; VMware/virtual adapters are not choices.
`connected` describes current link state, not successful guest bridging.
Wi-Fi bridging depends on the driver/access point and is always reported
as unverified until actually exercised. No physical Wi-Fi PCI passthrough
or direct QEMU use of a Windows NDIS interface is implemented.

The UI shows physical friendly names. Internal TAP choices can be optional
details, but the backend does not silently choose or alter another VPN's
TAP. `CreateBridge -PhysicalAdapter <uplink-id> -Adapter <dedicated-tap-name>`
is a separate explicit host operation requiring administrator privileges
and a fully stopped VM. It can briefly disconnect the host LAN and must
not run merely when the user changes a dropdown. Runtime policy/CLI callers
must obtain authorization for that interruption before invoking it.

The native command uses both interface GUIDs. Existing Windows bridges are
refused and preserved. After successful native creation, exactly one new
bridge GUID must be identifiable. Its selected uplink/TAP IDs are recorded
in the VM's `state/host-bridge.json`, with `connectivity=unverified`. Failure
after identifying that owned bridge removes only that bridge. A failed
native create or ambiguous concurrent topology does not justify deleting
unknown bridges; such errors require inspecting Windows network state.
Rollback is bridge removal, not a claim that DHCP/IP connectivity was tested
or fully restored. No VMware adapter or binding is explicitly modified.

After creation, `SetNetwork` or `SetHardware` accepts
`-Network bridge -PhysicalAdapter <uplink-id> -Adapter <dedicated-tap-name>`.
It requires the matching recorded bridge still to exist. The saved network
retains `adapter` as the QEMU TAP name and adds `physical_adapter_id` and
`physical_adapter_name`. `Status.network_uplink_id` returns the saved ID.
The legacy internal-TAP-only parameters remain accepted. NAT saving never
creates/destroys a host bridge and remains the default.

The optional `InstallTap` action can now prepare the official signed 9.27.0
driver and a dedicated adapter without a file dialog; see
[its contract](tap-installer-contract.md). It never changes host bridges.
A dedicated TAP must already be available for
`CreateBridge`. CPU fixtures simulate inventory/native operations/rollback;
actual wired/Wi-Fi bridge connectivity has not been tested by this change.

## Manager controls

The network editor and bridge-preparation page show physical friendly names,
Wi-Fi/wired type and link state; the persisted selection is the interface GUID.
Internal TAP selection lives in an expandable detail area. Changing a dropdown
only stages a setting. Host bridge creation remains a separate stopped-VM
action with an explicit interruption warning and elevation, followed by applying
the VM's bridged-mode settings. No TAP belonging to another VPN is auto-selected.

NAT stays the default and does not require an uplink selection. Existing legacy
TAP-only configurations remain saveable, and an unavailable saved physical ID
is displayed as missing instead of silently dropping the selection. Cancel
restores the staged GUID along with the other hardware fields. Inventory
failures preserve the current configuration and do not mark the VM itself dead.
