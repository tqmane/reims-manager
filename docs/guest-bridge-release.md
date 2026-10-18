Standalone Metal / WebGL guest addon for the reviewed Darwin 25 ABI range.
The package carries the authored kext, pinned Lilu, exact authored source,
source/ABI/qualification manifests and licenses. The Windows core engine
release remains independent.

Publication requires the common Metal implementation and safe runtime ABI
validation to be qualified. The recorded qualified kernels describe actual
checks; they do not claim every macOS 26.x release is tested. Apply only the
reviewed release from the manager to a stopped VM. Identity and existing
kernel bounds are preserved. CI compilation alone is not guest acceptance.
