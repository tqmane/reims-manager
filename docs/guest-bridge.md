# Darwin 25 Metal / WebGL guest addon

This standalone addon path targets generic Metal/WebGL support across the
Darwin 25 ABI family used by macOS Tahoe 26.x. The kext must validate its
runtime ABI before enabling translation. The manager does not select apps
or browsers, and it does not treat a successful OS boot as WebGL proof.

**The current source pin is a candidate, not a generic release.** It points
to the previously qualified 25.4.0 implementation while the common Metal
routing/runtime fix is completed and qualified in the Reims repository.
`release_ready=false` prevents publication and application. Candidate CI
artifacts can be built for review, but cannot be applied through the manager.
The current pin's 25.4.0 bounds have not been widened. No claim that all
26.x versions work is made by this candidate.

The addon is independent of the published Windows core engine. Its build
and release do not rebuild or replace `engine-d8b9c2af-7100064d-1`.

## Inputs and build

`backend/guest-bridge-lock.json` records source commits, ABI range, routing
scope, runtime-validation status and actually qualified kernels:

- Reims: `d8b9c2afb778d456069bf54cb4cd40e4cc77a0e7` (current candidate).
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

The manager's OpenCore page fetches the reviewed addon for a fully stopped
VM. Nothing is enabled automatically during new-VM setup. The live release
asset digest, payload hashes, source manifest, kext identities and pin data
are checked. Offline CLI use accepts `prepare-guest-bridge.ps1 -Mode Enable
-Archive <zip>` with its matching `.zip.sha256` sidecar or explicit digest.
Qualification checks apply equally to online and offline packages.

Only a new VM-local EFI copy is modified. Official ocvalidate and FAT16 image
creation must succeed before switching metadata to it. Original images and
metadata backups remain available. Validation failures keep the old image;
metadata switch failures restore old metadata and retain both images.

Identity, SIP, unrelated patches, scan policy and other NVRAM values are
preserved. Applying enables the existing activation flag, orders Lilu before
the bridge and enables its required DisableLinkeditJettison quirk. New entry
bounds come from the reviewed ABI range; existing narrower bounds are kept.
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
