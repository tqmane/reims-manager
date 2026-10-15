# Workspace UX and reliability

## Navigation

The native WPF/Fluent application keeps the existing engine, VM descriptor and
QEMU console integration. The sidebar separates the searchable machine library
from Console, Hardware, Clipboard and Snapshots. SSH pairing, OpenCore identity,
installation and bridge setup remain available under the advanced menu. Filtering
the library does not switch or stop the active machine.

`Ctrl+N` creates a VM descriptor, `Ctrl+O` opens one, `Ctrl+S` saves hardware
settings, and `F5` refreshes status. The current VM directory remains visible.
The collapsed operation log retains the latest 65,536 characters; clearing that
view does not delete diagnostic files, VM disks or snapshots.

## State and data handling

- One action policy is checked by both controls and operation handlers. Unknown
  states disable mutations. Paused guests expose Resume rather than SSH shutdown.
  `shutdown` means the guest is off but QEMU may still own the disk; offline
  mutations require `stopped` (fully exited).
- Each VM/engine context has a generation and cancellation token. Stale status,
  identity, USB and clipboard replies cannot update a newly selected VM. State
  refreshes are coalesced rather than silently dropping the final selection.
- Hardware edits are tracked separately from programmatic loads. Switching VM,
  switching engine or closing prompts before discarding unsaved edits. Reload
  failures retain the unsaved flag. CPU/memory, ISO and snapshot inputs are
  validated before launching the backend; backend validation remains authoritative.
- Read-only PowerShell helpers have a 30-second deadline and bounded, concurrently
  drained output. Cancelling a stale read terminates only that helper's process
  tree. Mutating helpers are not killed on a UI deadline; closing is blocked while
  they are writing.
- VM descriptors are validated before creation and installed with a no-overwrite
  atomic move. Non-empty data directories are rejected. Unreadable library entries
  are retained, and corrupt preferences are backed up before replacement.
- New VM port selection reads the actual application library, including stopped
  machines, as well as the legacy engine-local library. Existing configured ports
  are not rewritten. This is not an atomic lease across multiple manager processes;
  simultaneous provisioning in separate instances still requires coordination.

## Automated validation

`tools/build.ps1` publishes the native Windows application and runs its isolated
`--self-test`. No prepared guest, existing user VM or host adapter is modified.
The Windows workflow also runs `backend/tests/manager-ports.ps1` against disposable
fixtures under `.work`.

The self-checks exercise QMP response correlation/rejection, reused PID rejection,
argument quoting, settings recovery, safe descriptor creation, action policy,
read-only process cancellation/output limits, exact 4 MiB memory synchronization,
unsaved edits, VM context invalidation, filtering and navigation. Actual WPF
render captures cover all pages and hardware categories, the minimum window size,
paused/error states and dark mode. The `ReimsManager-ui-checks` artifact preserves
those PNGs plus `result.json` or `error.txt` even when validation fails.

## Remaining host acceptance

Automated fixtures do not prove macOS installation/boot, WHPX/RTX rendering,
embedded console input, GPU performance, audio, USB reconnect, guest clipboard or
SSH power control. Validate these with a prepared Windows host and disposable VM.
Also check 125/150/200% desktop scaling, high contrast and screen-reader navigation
on the actual host. No frame-rate or guest compatibility claim follows from a
successful build or a rendered fixture.
