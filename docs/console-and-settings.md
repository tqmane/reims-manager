# Console-first workspace and dedicated settings

## Direct Manager control

The running Manager exposes a same-user, same-Windows-session named pipe. Its
CLI controls that selected VM through the existing Dispatcher, power policy,
backend operations and native console methods, without simulated input:

```powershell
Get-Process ReimsManager | Select-Object Id
Start-Process .\ReimsManager.exe -ArgumentList '--control','status','--pid','1234','--output','status.json' -Wait
Get-Content .\status.json
```

Use the actual Manager PID. `--pid` may be omitted only when exactly one other
Manager process from the same executable path exists in the session. Executable
paths are compared case-insensitively; inaccessible processes are skipped during
discovery and rejected as explicit targets. Commands are `status`, `start`,
`poweroff`, `restart`, `exit` (normal QEMU exit), `pause`, `resume`,
`dock-console` and `popout-console`. No command force-kills a VM, changes the
selected VM, sends guest keys, executes arbitrary code or accepts credentials.

Each invocation emits one schema-1 JSON response to redirected stdout and,
optionally, `--output`. Exit code zero means `ok=true`. Status includes
`manager_pid`, `busy`, `selected_descriptor`, `state`, `consoleAttached`,
`consoleDetached` and `runtime_pid`. Compare the selected descriptor before
requesting a mutation. `status` can run while a power operation is busy;
concurrent mutations, unavailable operations and unsaved settings are rejected
with an `error` code. `start` completes after the backend operation and status
refresh; poll `status` for `consoleAttached` when the display is still appearing.

The transport is local only, with `PipeOptions.CurrentUserOnly`, a
session/Manager-PID pipe name, and a server check of the client process session.
Frames are a four-byte little-endian payload length followed by UTF-8 JSON,
limited to 64 KiB. Requests contain only `schema:1` and a whitelisted `command`.
Connection/read/write deadlines bound idle clients. A CLI timeout or disconnect
does not cancel an already accepted operation: `operation_may_continue=true`
in a client error means query `status` before retrying. Normal Manager shutdown
closes its listener and detaches the display; it does not shut down the VM.

The isolated native self-check starts actual CLI processes, verifies stdout and
an output path containing spaces/Unicode, observes a pending Start, rejects a
second mutation, dispatches every normal power action, docks/pops out an owned
native child and completes an operation after its client disconnects. It also
checks unsaved-setting/policy refusal and frame/command bounds. It does not boot
macOS or replace real-guest acceptance.

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

## Cursor compatibility display

The display settings page offers explicit `カーソル互換表示` On/Off actions for
screen mirroring. The guest helper draws the actual macOS cursor as ordinary
Mac window content. Enabling is opt-in; its per-user LaunchAgent remains enabled
across logins until explicitly disabled. It does not detect mirroring processes
or sessions automatically; the helper can remain alive and idle. Turn it off
when it is no longer needed.

`EnableCursorAid` and `DisableCursorAid` use the existing selected-VM backend
operation path without elevation or additional arguments. Both require a running
VM, a configured engine and paired SSH. Failures use the normal operation error
and log feedback. No new status field or inferred enabled-state indicator is used.
The isolated display-page fixture checks button routing, policy and compact
visibility without invoking a guest helper. It does not qualify real cursor
appearance, screen mirroring, Dock behavior or latency.

## OpenCore controls

The macOS 26.x boot profile controls APFS/HFS disk scanning. It rebuilds the
recorded EFI configuration while retaining SMBIOS, saved identity and writable
NVRAM. Identity editing/generation remains a separate explicit operation. This
profile is not an Apple Account authentication patch or a sign-in guarantee.

The retired version-specific Apple Account patch is not exposed or allowed as
a Manager UI action. Its backend API remains for compatibility with older
automation and retains its original bounds; renaming it would not make it a
generic macOS 26.x feature. GPU bridge controls use the common ABI/runtime checks
and do not select a point release. Recorded historical kernel acceptance remains
evidence of those checks rather than a version-selection gate in the UI.

## Wi-Fi compatibility connection

`LAN 接続の準備` reuses the physical Wi-Fi / wired adapter selector. When
exactly one connected Wi-Fi is available, it suggests that adapter without
changing the VM or Windows configuration. Selecting Wi-Fi shows one preparation
action; the backend prepares a dedicated owned TAP automatically. Wired Windows
bridge preparation and removal remain separate. NAT remains the default.

Wi-Fi preparation and removal require a fully stopped VM and Windows elevation.
Before a missing Windows Packet Filter driver can be installed, the user must
check the explicit personal/academic-use or commercial-evaluation terms consent.
The page links the vendor's download and commercial-license terms and states
that installation can briefly interrupt Windows networking. It never grants
consent automatically or forces a Windows restart. A prerequisite restart is
reported as incomplete preparation; the user can repeat preparation afterward.

The optional `network_forwarder` status is authoritative for the forwarding
worker. Missing/malformed status is unconfirmed, a reported error stays visible,
and `ready` means the live worker initialized, not that DHCP, LAN or Internet
works. Active Wi-Fi status is polled from the backend instead of reusing a
cached readiness flag. New `macnat` profiles prepare IPv4/IPv6 forwarding. The
typed `ipv6_enabled` flag is true only for a matching prepared record with the
owned TAP's IPv6 router-discovery guard; `ipv4_only` must be its inverse. Missing,
untyped or contradictory capability fields are unconfirmed. Neither the flag nor
worker readiness establishes a usable IPv6 route or Internet connection. Physical
link presence is labeled separately from connectivity.

Legacy `macnat-ipv4` profiles retain their identifier in hardware drafts and
remain available for cleanup. They show an explicit IPv6 preparation notice and
require `Wi-Fi 互換接続を準備` again; opening or cancelling settings never silently
upgrades a profile. The backend supplies their unconfigured IPv6 status and
restores only its recorded owned TAP settings on disable. The UI never reads or
changes raw router-discovery records.

The hardware draft retains the selected physical GUID and forwarding mode.
Unrelated unsaved memory/identity changes must be applied or cancelled before
the immediate preparation action. `解除して NAT に戻す` delegates owned TAP
binding restoration to the backend; changing the hardware radio to NAT alone
cannot bypass cleanup. Preparation reads only the fixed, fresh schema-1 result
after successful elevated process exit, with a 64 KiB input limit. An unreadable
result reports possible partial changes rather than assuming rollback.

CPU fixtures cover status parsing, typed IPv6 capability and legacy migration,
error/readiness precedence, explicit consent, stopped-state policy and
fresh/bounded result validation. The focused network
page fixture checks the actual Fluent controls, draft retention and action
visibility with synthetic metadata and no host network calls. Neither fixture
establishes real Wi-Fi, driver installation or guest connectivity.

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
