# iPhone USB handoff

Select **WinUSB** as the VM's USB transfer mode, completely exit QEMU, and
start the VM again after changing that setting. Select the iPhone in the
USB list. The administrator prompt authorizes a temporary driver change
for that selected phone. Windows may require unplugging and reconnecting
the phone before the new driver becomes active.

The package uses an existing signed libusb-win32 driver and the released
libusbK user-mode library. It needs no purchased signing certificate,
test-signing mode, signature-enforcement change or WSL. The signed INF and
catalog remain unchanged. Other phones and USB devices are not selected by
a broad VID/PID capture filter.

Normal mode uses the Windows Apple service to confirm the selected phone's
UDID and ECID. Recovery and DFU use the ECID in its actual USB serial
descriptor. An unavailable, duplicate or mismatched identity refuses the
handoff. The supervisor follows that identity across mode changes and gives
QEMU the complete Windows instance ID. USB addresses and physical ports
do not authorize a different phone.

The iPhone path permits guest-requested USB resets after address assignment.
It retains QEMU's default virtual initial reset while the guest address is
zero: forwarding that reset to the already enumerated Windows device caused
a reset/close/reopen loop before the guest could enumerate it. The generic USB path's
reset policy is unchanged. Selecting the phone does not itself enter DFU,
restore firmware or erase its data; those operations belong to guest tools.

Disconnecting or ending the VM removes the owned QEMU device before
restoring its recorded Apple driver. If the phone is absent, restoration
remains pending until the corresponding instance returns. Protected driver
receipts remain outside the user-writable VM directory. A Windows reboot
or termination of the supervisor can interrupt recovery: open Manager and
use the USB disconnect operation to resume it. Do not delete a pending
receipt or treat a reboot request as verified restoration.

Multiple iPhones and iPads can be registered to the same VM. The original
`iphone_usb` profile is preserved; additional registrations are stored in
`iphone_usb_devices`. Each device keeps its own ECID, supervisor, cancellation
marker and protected driver receipts. Connecting or disconnecting one device
does not restart the others. Normal-mode product IDs 12A8 and 12AB use the
same exact-identity Apple-service path; recovery and DFU retain their existing
checks. New supervisors skip the known instances of other registrations.
Remove old generic Apple USB registrations before using the dedicated path.
When upgrading a running WinUSB VM, unregister only the old, disconnected
iPad row, refresh the device list and connect the dedicated iPad entry.
Keep the already connected iPhone registered. If Windows requests
reconnection after the driver switch, reconnect only the iPad cable. This
does not require restarting Manager or the VM. Changing the VM's USB
backend remains a separate operation that requires a full VM exit.

## Validation

The candidate's normal-mode physical check selected configurations 1, 3
and 4, read each value back through a real `GET_CONFIGURATION`, and claimed
and released the relevant interfaces. Its descriptor and original
configuration were also verified. The exact original Apple driver, native
driver stack and ECID were confirmed again afterward.

The recovery/DFU identity paths have CPU fixtures using authored descriptors.
QEMU's exact-instance property has passed paused device realization with an
authored absent identity, and rejects a DLL without the extension API.
A Manager-to-guest normal-mode session was verified on Windows with engine
`engine-07460212-fbba9f15-13` and macOS 26.7.1: an iPhone 8 was identified,
switched to the signed transfer driver, reconnected and recognized by the
guest USB mux service. A standard lockdown query matched its ECID, and the
user confirmed its appearance in Finder. QEMU's xHCI short-transfer repair
preserves the first ISP event and also reports the tail IOC event; suppressing
that second event had stalled guest communication despite completed host I/O.

On 2026-11-07, the multiple-device backend was applied to an already running
Windows engine `engine-28979c8c-fbba9f15-15`. The original iPhone profile,
supervisor and QEMU attachment stayed unchanged while a 12AB iPad used its
own profile and supervisor. After reconnecting only the iPad cable, both
supervisors reported connected and both exact QEMU objects were attached.
The user confirmed that the iPad connected. Neither Manager nor the VM was
restarted. This qualifies this normal-mode two-device session, not physical
recovery, DFU or firmware restoration.

Physical recovery, DFU and guest-driven firmware restoration remain
unqualified; the normal-mode check does not authorize or prove an erase or
firmware restore. The engine remains unpublished until separately released.
