Portable Reims-enabled Windows QEMU, native DLL dependencies, OVMF,
OpenCore and the Reims GPU ROM for the matching Reims Manager version.

Use the manager's engine setup, or select the downloaded ZIP with its
matching `.zip.sha256` sidecar from this release beside it. No checksum
input is needed. Setup validates the package and tests native tools before
installing it into the chosen directory.

Source revisions, component licenses and native dependency package versions
are included. Windows WHPX and a compatible GPU driver are still required.
CI checks packaging and native startup; it does not boot macOS or qualify
guest kernel plugins for a new OS version. See the included engine guide.
