# Darwin 25 Metal / WebGL guest addon

This standalone addon path targets generic Metal/WebGL support across the
Darwin 25 ABI family used by macOS Tahoe 26.x. The kext must validate its
runtime ABI before enabling translation. The manager does not select apps
or browsers, and it does not treat a successful OS boot as WebGL proof.

The reviewed source is `700379ce`, paired with
`engine-b8b4486c-7100064d-2`. Native Safari and Chrome passed all 22 WebGL
acceptance stages on Darwin 25.4.0 (macOS 26.4) and 25.6.0 (macOS 26.7.1);
Edge also passed on 25.6.0. These measured kernels are recorded in the
package. Darwin 25.0.0 through 25.99.99 describes the generic translation
family, not a claim that every 26.x release was tested. Unknown layouts
are rejected by the runtime request checks.

**Frame pacing remains limited.** Later visible Safari runs showed fewer
animation callbacks despite successful pixels and no context loss. This
release does not promise 60 FPS or consistent animation timing.
`release_ready=true` permits publication/application of this reviewed pin;
setup still fails explicitly until its matching public asset is available.

The addon is independent of the published Windows core engine. Its build
and release do not rebuild or replace `engine-d8b9c2af-7100064d-1`.

## Inputs and build

`backend/guest-bridge-lock.json` records source commits, ABI range, routing
scope, runtime-validation status and actually qualified kernels:

- Reims: `700379ce32942b474dbbbb0f8c18f6736b10c863` (global Metal path).
- Lilu 1.7.2 headers/plugin startup: `e4748cc081bf060302c7d3c44a643ce1d11b7e1d`.
- MacKernelSDK: `3f750085caa17ec3a7880f11c11bf4f48cd6a164`.

The macOS workflow exports committed `guest/metal-compat` using its existing
`source_snapshot.py`, runs its source tests and authored CPU ABI fixture,
then invokes its `build.sh` with the pinned dependencies. That script checks
inputs, compiles a private source copy, and signs the authored x86 kext ad
hoc. CI verifies its signature and x86 architecture. Xcode command-line
tools, Python 3, Git, codesign and lipo are provided by `macos-15`.
No kext is loaded and no VM is booted on the runner.

The ZIP contains ReimsMetalCompat, official pinned Lilu, the exact authored
source snapshot, signed source provenance, configuration helper, dependency
pins and licenses. `bridge-manifest.json` records every payload hash,
source/ABI/qualification pins, manager revision, Xcode version and kext UUID.
Apple guest binaries, shader dumps, private captures and personal identities
are not inputs or outputs. Compiled files remain CI artifacts, outside Git.

Build a review artifact without any Windows engine build:

```powershell
gh workflow run guest-bridge.yml -R tqmane/reims-manager --ref main -f publish=false
```

Before the final release, the maintainer must pin the completed common
Reims implementation, review its safe runtime ABI checks, record native boot
and app/browser qualification results, set `scope=generic-metal` and
`runtime_validation=abi-checked`, and only then set `release_ready=true`.
`generic-metal` means global routing for all clients, without process-name
or suffix policy. Runtime ABI validation belongs in the common kext path.
Use a new immutable tag for that source/range. These metadata assertions do
not implement or replace ABI validation inside the kext. A publication
request fails before building/publishing while those conditions are pending.
After qualification, `publish=true` builds and publishes the separate addon.
Record the actual qualified host engine in `required_engine_id`. If a host
runtime fix is needed by the generic path, use its new engine release ID;
never silently pair that guest addon with the older engine. Application
requires the matching portable-engine manifest. Updating only the addon
does not itself trigger any Windows rebuild.

## Application and recovery

New-VM setup fetches and enables the reviewed addon before the first boot.
Existing VMs can explicitly reapply/disable it from the OpenCore page while
fully stopped. Online acquisition uses its fixed public tag/asset URL without
the GitHub API or a ZIP SHA requirement. Payload hashes, source manifest,
kext identities and pin data are checked. Offline CLI use accepts `prepare-guest-bridge.ps1 -Mode Enable
-Archive <zip>` with its matching `.zip.sha256` sidecar or explicit digest.
Qualification checks apply equally to online and offline packages.

Only a new VM-local EFI copy is modified. Official ocvalidate and FAT16 image
creation must succeed before switching metadata to it. Original images and
metadata backups remain available. Validation failures keep the old image;
metadata switch failures restore old metadata and retain both images.

Identity, SIP, unrelated patches, scan policy and other NVRAM values are
preserved. Applying enables the existing activation flag, orders Lilu before
the bridge and enables its required DisableLinkeditJettison quirk. New entry
bounds come from the reviewed ABI range. Explicit enable/reapply migrates
an existing authored bridge entry, and a manager-owned Lilu entry, from
narrower bounds within that family to the qualified package range. This
requires release-ready metadata; candidate packages cannot migrate bounds.
Custom Lilu bounds remain unchanged and must cover the package range.
An incompatible existing range or a conflicting Lilu build is rejected.
Disabling turns off the bridge and its activation flags, preserving bounds,
Lilu and shared quirks for other plugins. Ordinary OpenCore rebuilds follow
the active EFI rather than discarding the addon or regenerating identity.

Normal CI runs small offline fixtures. An additional disposable-firmware
integration check uses authored placeholder kext data and real ocvalidate /
qemu-img; it simulates future qualified metadata only in its private backend
copy. It checks switching, identity/bounds and rollback without loading a
kext or touching a live VM:

```powershell
./backend/tests/guest-bridge-integration.ps1 -EngineRoot '<engine>' -ToolsRoot '<manager>/runtimes'
```

A macOS compilation pass and these fixtures do not qualify a newly compiled
kext on a guest. Cold boot and actual Metal/WebGL acceptance remain required.

## Installer boundary

The fresh OpenCore profile includes APFS and HFS scanning (`0x301`), shows
auxiliary installer entries, and loads OpenHfsPlus/OpenPartitionDxe. Addon
application adds Lilu, ReimsMetalCompat and `-reimsmetal`; it preserves those
boot settings, identity and SIP. No installer-only kernel-version switch is
introduced. Activation selects `AppleParavirtGPUMetalIOGPUFamily` after the
Reims accelerator interceptor and completion routing are installed; it does
not first check that this user-space plugin exists in installer media.

The available preparation code inspects SystemVersion.plist, boot.efi,
architecture and media headers. It does not establish installer plugin
availability. Existing installed-system browser results therefore do not
prove that a fresh installer UI works with the addon enabled. Current-media
first boot remains a separate acceptance check; no additional OS versions
are required or claimed here. The unmodified EFI copy/backups are retained
so the addon can be disabled without regenerating identity or NVRAM.
