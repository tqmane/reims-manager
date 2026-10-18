# Cursor aid

The optional cursor aid draws the current macOS pointer as ordinary window
content so screen sharing can include it. It uses the system cursor's image,
hot spot and visibility. Its panel accepts no mouse or keyboard input, does
not become the key window, and stays above the Dock and menus. It does not
automatically move the pointer, hide the native cursor, change capture permissions or
modify another application.

Enable or disable it manually through the VM's sharing controls. Disable it
when it is no longer needed. It does not infer whether a particular capture
application or sharing session is active. Sharing applications choose what
they capture; enabling the aid does not change those filters.

The manager installs the verified helper and an ownership receipt in
`~/Library/Application Support/Reims/CursorAid/`. Its per-user GUI LaunchAgent
is `io.github.tqmane.reims.cursor-aid`. Disabling it unloads that service;
sending a signal alone to a service with `KeepAlive` enabled would restart it.
The native helper does not write the manager's ownership receipt.

## Native process

With no arguments, `ReimsCursorAid` runs until SIGTERM. The signal handler
sets a flag; normal main-thread cleanup closes the owned panel. There is no
automatic timeout or application-specific process check.

`ReimsCursorAid --duration 120` runs a bounded trial. `--self-test` checks
geometry, image-data bounds and argument handling without opening a window,
querying the current cursor or requesting capture permissions.

`--move-to-display main` moves once to the main display's center.
`--move-to-display extended` selects the unique active, non-main, non-mirrored
display and moves once to its center. No resident overlay is required for
these actions. They use global logical display bounds, including negative
origins, rather than multiplying by the display's backing pixel density.
They do not post an input event or lock subsequent pointer movement to that
display. An absent or ambiguous extension is refused. An unsuccessful move
does not stop an already enabled overlay.

The one-shot action prints one JSON object with these fields:

| Field | Value |
| --- | --- |
| `schema`, `action`, `target` | `1`, `move-to-display`, and `main` or `extended` |
| `ok`, `cgError` | Boolean success and the raw Core Graphics result, or null before a move |
| `displayID` | Selected positive display ID, or null |
| `boundsCG` | Finite `x`, `y`, `width`, `height` in logical coordinates; width and height are positive, or the object is null |
| `positionCG` | Finite requested `x`, `y` at the logical center, or null |
| `candidateCount` | Number of eligible active displays; success requires exactly one |
| `error` | Null, or one of the error names below |

Error names are `display-query-failed`, `no-main-display`,
`no-extended-display`, `ambiguous-extended-display`, `invalid-bounds` and
`warp-failed`. Selection failures have no Core Graphics move result. A query
failure may include its Core Graphics error. `warp-failed` includes the
selected geometry and a nonzero result. The exit code is 0 for success,
1 for a runtime refusal or failure with JSON, and 2 for invalid arguments
with usage on stderr. Availability of this action follows the bundled
helper's pinned source revision; manifest schema 1 is unchanged.

Position and glyph refreshes have separate schedules: position targets
60 Hz, while the image is checked at 10 Hz. The helper copies the selected
Core Graphics image provider's data and compares its format and color space;
it does not repeatedly encode all cursor representations into TIFF. It only
redraws when position, size or image content changes. A missing or unsupported
image is hidden rather than replaced with a synthetic arrow.

The main thread drains up to 64 of its own AppKit events per position tick,
dispatches them normally and updates its windows. It does not use an event
tap, a global input monitor, `finishLaunching` or an activation request.

The schedules are targets, not latency guarantees. WindowServer calls, guest
load and the sharing application's encoding and transport can delay visible
updates. Guest-side rendering, the destination's displayed pointer and CPU
cost during idle and motion require separate runtime checks.

## Build

On a Mac with the existing Xcode command-line tools selected, commit the
source and run `python3 tools/build_cursor_aid.py`. No SDK download is part of
the build. The macOS 15 CI job uses the same command, builds arm64 and x86_64
sequentially, combines them into a universal executable, ad-hoc signs it and
runs its pure self-test. The build host is not guest OS qualification.

The `ReimsCursorAid` Actions artifact contains `ReimsCursorAid.zip` and its
SHA256 sidecar. The ZIP has the executable, `manifest.json` and the executable's
SHA256 sidecar at its root. The manifest has schema `1`, executable
`ReimsCursorAid`, a lowercase binary SHA256 and the full source revision.
The Windows manager bundles the executable and manifest under
`runtimes/cursor-aid/`; release pinning and bundling are separate from this
native build.
