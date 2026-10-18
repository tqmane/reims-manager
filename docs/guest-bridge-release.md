Standalone Metal / WebGL guest addon for the reviewed Darwin 25 ABI range.
The package carries the authored kext, pinned Lilu, exact authored source,
source/ABI/qualification manifests and licenses. The Windows core engine
release remains independent.

Publication requires the common Metal implementation and safe runtime ABI
validation to be qualified. The recorded qualified kernels describe actual
checks; they do not claim every macOS 26.x release is tested. Apply only the
reviewed release from the manager to a stopped VM. Identity and unrelated
kernel entries are preserved. Explicit enable/reapply updates owned bridge
and Lilu entries to the qualified ABI range; disabling preserves bounds.
CI compilation alone is not guest acceptance.

Source 700379ce with engine-b8b4486c-7100064d-2 passed 22 WebGL acceptance
stages in Safari and Chrome on kernels 25.4.0 and 25.6.0; Edge also passed
on 25.6.0. Other 26.x versions are covered by the ABI-family contract, not
claimed as tested. Frame pacing can degrade on later runs; 60 FPS is not
guaranteed. Installer UI acceptance is separate from installed-system tests.
