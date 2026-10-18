# Console-first workspace and dedicated settings

This follow-up is based on PR #1 (`67c85e4`). It preserves the engine/backend and
VM descriptor/disk formats. It does not add VMware assets or claim VMware feature
parity.

## Navigation

When the selected VM is fully stopped, the library and the existing management
workspace remain visible. `仮想マシンの設定` opens a dedicated full-page settings
workspace, not a dropdown of independent pages. Its left-hand category list
contains all eight hardware editors plus clipboard sharing, SSH pairing, Mac
identity/OpenCore, installation, bridge preparation and snapshots. The editor
scrolls separately from the always-reachable Cancel / Apply / OK footer.

A running, paused, prelaunch or suspended VM automatically uses the console-first
layout: a 44-DIP title bar with Fluent actions and window controls, then the
console. Library, VM headings, management commands/page tabs, console footer,
status bar and operation log are removed from that layout. This is a focused
window, not an exclusive OS full-screen mode. The maximize control uses native
window commands. Drag/resize uses WPF WindowChrome.

The title bar provides Library, power, Pause/Resume, Restart, Settings, USB,
Clipboard, Snapshots and explicit pop-out. USB/sharing/snapshots open the matching
settings category directly; only *power operations*, not settings categories,
use a dropdown. The input-release shortcut remains available as a tooltip.

Library is an explicit, reversible presentation choice and does not stop a VM.
Status polling cannot immediately undo that choice. Settings close back to the
previous presentation; a guest power transition while editing never dismisses
the editor. `unknown` samples retain the previous view and disable mutating
commands. Selecting another VM resets the presentation state rather than using
the previous VM's status. Power-off restores the management layout.

Host Ctrl+N/O/S and F5 handling is suppressed in the focused console so these
keystrokes are not consumed as manager actions. In the settings page Ctrl+S
applies all staged hardware/identity changes, including from an options category.

## Draft and action semantics

Apply saves the staged hardware and/or identity forms. OK applies them and closes
only on success. Back/Cancel ask before discarding unsaved edits. Restoration is
from committed in-memory form checkpoints; it does not reset the VM context,
restart clipboard sharing, revoke USB registrations, remove snapshots, or write
any VM disk. Switching categories retains the draft.

Identity consent is requested before either write. The save sequence stops on
its first failure. Hardware and identity are separate backend operations, **not
one atomic transaction**: if hardware succeeds but identity fails, the hardware
stays committed and identity remains dirty, with the page open and error visible.
A later Cancel cannot pretend to undo the already committed operation.

The existing state policy remains authoritative in handlers and controls.
Offline configuration edits are disabled until their configuration is loaded and
the VM is fully stopped. Live display resolution and USB/clipboard operations
retain their existing capabilities. Buttons inside installation, pairing, USB,
clipboard and snapshot pages are immediate operations; the footer states that
Cancel does not undo them. Unknown state and guest-only shutdown are not treated
as permission to write offline configuration.

## Launcher completion

Windows can leave a detached QEMU holding inherited stdout/stderr pipe handles
after the PowerShell launcher exits. Pipe EOF is not launcher completion. The
manager waits for the launcher, then drains its buffered output for at most two
seconds. It retains captured output and exit diagnostics without waiting for the
VM to exit. Mutating commands remain non-cancellable while their launcher runs;
read-only cancellation never terminates descendants after that launcher exits.
The CPU-only `--backend-check <folder>` verifies this with owned detached helpers.

Start selects the embedded console. An unsuccessful attach is retried by normal
status polling; only an explicit pop-out suppresses attachment. Reopening the
manager can attach the already running VM without starting a second QEMU.

## Native console lifetime

The HwndHost remains in one stable visual-tree position. Showing settings hides
its native child explicitly; it never detaches, re-parents or launches a second
console merely to change pages. Returning restores an already attached display,
including after a transient unknown status. Only explicit pop-out, a VM switch or
window closure uses the existing detach path. Progress/error messages occupy a
separate grid row instead of overlaying an HWND, avoiding WPF/native airspace
occlusion.

## Validation and remaining host checks

The native Windows self-test covers the state presentation policy; all settings
routes; dirty/cancel/validation behavior; sequential save failure handling;
light/dark, compact and normal layouts; long labels and large libraries; caption
geometry; and an owned synthetic native child process exercised through attach,
settings hide/return, Library/focus, unknown state, pop-out and re-dock. The native
fixture never opens a real VM or modifies the host configuration. Render PNGs
show synthetic VM metadata and waiting-console placeholders, not fabricated guest
screenshots. Exact checked revision, result flags and image counts are in the CI
artifacts.

A successful fixture does **not** establish real macOS boot, WHPX/RTX output,
QEMU keyboard capture/release, physical USB/audio, real SSH/clipboard transfers,
125/150/200% desktop scaling, high-contrast/screen-reader behavior or Windows Snap
hover behavior for the custom caption buttons. Those still require a prepared
Windows host and disposable guest. No live thumbnails, performance metrics,
multiple simultaneous VM-session tabs, clone UI or unsupported device actions
are added.
