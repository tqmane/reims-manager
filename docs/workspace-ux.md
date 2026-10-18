# Fluent workstation UX and reliability

## Navigation and visual hierarchy

The native WPF/Fluent application keeps the existing engine, VM descriptor and
QEMU console integration. The layout uses a desktop workstation hierarchy rather
than a dashboard of cards:

- The left sidebar contains the searchable VM library, engine settings and
  registration recovery controls.
  The machine list gets the remaining vertical space and scrolls independently.
  A native splitter changes its width between 200 and 340 device-independent
  units, using dragging or keyboard arrows. Width is session-local, not persisted.
- A compact command bar stays above the workspace: create/open, start/resume,
  shutdown/restart, the guarded power menu, console connection and refresh.
  Disabled icon-and-text commands dim their entire content. New/open commands
  follow the library's busy state; handlers still enforce their own guards.
- The selected VM's name, configuration and state appear above its page tabs.
  Console, Hardware, Clipboard and Snapshots are horizontal page tabs, not
  separate VM sessions. SSH pairing, OpenCore identity, installation and bridge
  setup are categories of the dedicated settings page. Filtering the library does not switch
  or stop the active machine.
- The console has a thin frame and compact input-release/pop-out footer. Hardware
  settings use a device list on the left and an editor on the right, with save,
  revert and dirty-state feedback in a fixed footer outside the editor scroller.
- The operation log is collapsed by default. The current page and VM directory
  appear in the bottom status bar. Empty, loading, paused, shutdown and unknown
  states retain their existing distinct guidance.

Native dynamic Fluent brushes preserve system, light and dark themes. Window-local
styles avoid changing the engine setup dialog. Icons use the installed Segoe
Fluent Icons font, with matching RAM/CPU/drive/network-adapter glyphs from the
[Microsoft icon reference](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font).
No VMware assets, embedded web UI or new UI framework dependency are introduced.

`Ctrl+N` creates a VM descriptor, `Ctrl+O` opens one, `Ctrl+S` saves hardware
settings, and `F5` refreshes status. The collapsed operation log retains the latest
65,536 characters; clearing that view does not delete diagnostic files, VM disks
or snapshots.

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

The workstation layout uses isolated render/geometry checks and the existing
guest action policy. Preference persistence and unavailable library entries are
described below; they do not change guest disks or QEMU arguments. The console
command binds its enabled state to the same connection policy as its handler.

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
paused/error states and dark mode.

`CheckWorkstationRendering` additionally checks light/dark themes, narrow/wide
library columns, console and hardware pages, a long Japanese VM name and 50 extra
in-memory VM entries. It asserts that commands do not overlap, page tabs and
advanced navigation remain inside their viewports, the library stays bounded,
the console retains at least 240 units of height in the checked compact layouts,
and the settings save/revert controls remain reachable. Requested window sizes
are subject to the runner's desktop bounds; these are not large-monitor or
physical high-DPI acceptance tests. The PNGs contain synthetic VM metadata and
do not represent a running macOS guest.

The `ReimsManager-ui-checks` artifact preserves the native PNG captures plus
`result.json` or `error.txt` even when validation fails. The build manifest in the
packaged application identifies the exact checked-out revision; a pull-request
workflow can use GitHub's merge revision rather than its head commit.

## Remaining host acceptance and non-goals

Automated fixtures do not prove macOS installation/boot, WHPX/RTX rendering,
embedded console input, GPU performance, audio, USB reconnect, guest clipboard or
SSH power control. Validate these with a prepared Windows host and disposable VM.
Also check 125/150/200% desktop scaling, high contrast and screen-reader navigation
on the actual host. No frame-rate or guest compatibility claim follows from a
successful build or a rendered fixture.

This revision does not introduce multiple simultaneous VM session tabs, live
thumbnail previews, live performance charts or persistent splitter preferences.
The goal is a Fluent workstation layout using the already implemented VM features,
not a claim of VMware feature parity.

## Library persistence and recovery

The application stores its library, engine and last selection in
`%LOCALAPPDATA%\Reims\Manager\settings.json`, independently of the executable,
engine directory and working directory. Updating or moving the application does
not create a new library. `REIMS_MANAGER_HOME` explicitly selects an alternate
profile for isolated checks; a normal shortcut does not inherit a temporary
profile used by a development shell.

The sidebar's **登録・保存先** section shows the exact active settings file, opens
its directory and imports another profile's `settings.json`. Import adds VM
registrations without dropping the existing library or replacing an already
configured engine. When switching from a development launch or another profile,
use this import instead of re-creating VM disks.

Settings saves hold a per-profile cross-process mutex, merge registrations and
only update fields this instance changed. A stale instance cannot overwrite a
newer engine/selection with its startup snapshot. Writes flush a temporary file
and replace the destination atomically; the previous good file is retained as
`settings.json.backup.json`. If the current JSON is damaged, loading attempts that
backup and displays a warning. Before a replacement, unreadable original JSON is
preserved separately. I/O errors are surfaced rather than silently replacing an
unreadable file with an empty library.

Disconnected or invalid VM descriptors remain visible with an unavailable state
and their original path. Guest operations stay disabled for these entries.
Reconnect the drive and choose **ライブラリを更新**, or use **VM を開く** to register a
moved descriptor. This refresh also discovers registrations saved by another
manager instance. Library operations do not move, delete or recreate guest data.

The isolated self-check launches new application processes with a different
working directory and no inherited profile override. It verifies engine, selected
VM and disconnected registrations after restart; overlapping stale-instance
saves; JSON backup recovery across another restart; and additive profile import.
An unrelated save keeps the current window's active engine/selection fixed while
preserving newer values on disk, including on a second stale save. Library refresh
adopts another window's engine only when fully stopped; otherwise it explains why
the current connection is retained.
WPF checks cover the unavailable row/action state and compact recovery controls.
These checks do not simulate a host power failure or physical disk loss.

The OpenCore page exposes boot-profile reapplication and guest GPU bridge
enable/disable as separate, fully-stopped-only operations. The legacy Tahoe 26.4
Apple Account patch remains labelled separately. Installing a bridge is not a
qualification result for an OS version; missing backend status is shown as
unconfirmed. These UI checks do not install a bridge or assert Tahoe compatibility.
