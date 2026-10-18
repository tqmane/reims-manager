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

## Remove an owned bridge

`RemoveBridge` takes the selected `VmDirectory` and no adapter/GUID arguments.
It requires a fully stopped QEMU process and administrator privileges. The
Manager exposes **ブリッジを解除して NAT に戻す** with a separate host-interruption
confirmation; pending settings must be saved or cancelled first.

The backend accepts only one live Windows bridge matching the VM's active
`state/host-bridge.json`, with matching recorded TAP/uplink identities and VM
network settings. Missing, removed, foreign or multiple/ambiguous bridge
topologies are refused. Native destroy receives only that recorded braced GUID;
unrelated bridge/VMware interfaces, TAP devices and IP/metric settings are not
explicitly altered. Before preparation and again immediately before native
destroy, actual member evidence must match exactly the recorded TAP/uplink:
enabled `ms_implat` bindings and native `netsh bridge show adapter`
`IsBridged=Yes` GUIDs must both be that same two-element set. Extra NICs,
other multiplexor teams, missing/duplicate identities, query failures and
unrecognized native output refuse removal. Neither bridge GUID alone nor
protocol binding alone establishes membership. IP/DHCP/connectivity must
still be checked separately.

Identity mapping uses the measured native binding `InstanceID` shape
`{GUID}::ms_implat`, verifies that each enabled GUID identifies exactly one
`Get-NetAdapter -IncludeHidden` result, and never parses friendly names or
assumes an undocumented InterfaceGuid property exists on a binding object.
Unknown InstanceID formats are refused. Native table parsing recognizes
GUID rows and explicit Yes/No flags only; unknown/localized flag formats
are refused instead of guessed. Binding `BindName` is an NDIS name, not
adapter identity. The [CIM class documentation](https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/netadaptercimprov/msft-netadapterbindingsettingdata)
describes those properties; [native bridge commands](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-bridge)
describe the adapter status table and destructive scope of destroy.

The native table format was checked against retained private captures with
the two intended members and against current read-only binding enumeration;
no bridge was created for this guard. Synthetic cases cover extra members
in either evidence source, unknown/duplicate/missing identities, query or
format failure, and a foreign NIC appearing after NAT was staged. This is
a revalidation guard, not an atomic OS topology lock: changes by another
administrator after the final check remain outside this transaction.

Before changing anything, original config/record bytes are retained under a
unique `backups/network-remove-*` directory with `transaction.json`. NAT config
is written before native destroy. A native failure restores the exact previous
configuration only if the same owned bridge remains and the NAT file has not
been changed by another writer. If the bridge is gone, topology is uncertain or
another writer changed config, recovery preserves NAT/the newer file and emits
a failure with the audit location rather than overwriting data or guessing a
host rollback. Metadata-write failures after native removal also keep NAT.

Success keeps the ownership record marked `state=removed`, with a removal time
and relative audit reference; it clears the VM's bridge adapter/uplink fields
while preserving unrelated VM settings. A new explicit CreateBridge can then
record another uplink. Removed ownership records cannot be reused to save bridge
mode. None of this qualifies Wi-Fi forwarding or guarantees DHCP recovery.

CPU-only checks run with synthetic files, inventory and native calls:
`pwsh -NoProfile -File backend/tests/network-uplinks.ps1` includes the focused
`remove-bridge.ps1` ownership/rollback cases. The affected WPF page can be checked
with `ReimsManager.exe --network-page-check <private-output-directory>`; it
does not invoke the host network backend or boot a VM.

## Proposed host-readiness gate (not implemented)

CreateBridge currently does not verify host lease recovery. The reviewed
minimal proposal is to read the selected physical uplink's IPv4/default
gateway before creation, aborting before mutation if that query fails. If
it previously had usable non-link-local IPv4 and a default gateway, wait up
to 30 seconds at 500 ms intervals for the uniquely identified new bridge
to regain usable IPv4 plus a default gateway. Do this before saving its
ownership record or applying bridged VM settings. On timeout/unresolved
identity, raise through the existing owned-bridge rollback path and leave
the VM's NAT configuration unchanged. Never reset adapters, renew leases,
change metrics/other NICs, or use an Internet ping as acceptance. Before
destroying during rollback, ownership/membership must remain provable.
Synthetic tests should cover immediate/delayed recovery, LLA-only/missing
gateway, missing bridge identity, timeout, rollback failure and NAT/foreign
NIC preservation. These tests and the lease gate are pending, not proof of
Internet access or guest DHCP.
