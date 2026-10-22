# ARM64 macOS applications in Reims Intel guests (experimental)

The ARM64 compatibility feature is **inside Reims Manager**, not a replacement
QEMU firmware or a second ARM64 VM. The Intel macOS guest runs Maclator
(arm64 Mach-O loader + AArch64 interpreter/x86-64 JIT + Darwin syscall/Mach
translation). Its host Intel Metal calls may then use Reims vGPU. Actual Reims
GPU interoperability is **not yet qualified**.

## Build and Apple Silicon dyld source

Run **Actions → Reims ARM64 application compatibility → Run workflow** in
`tqmane/reims-manager`. Check `collect_apple_sysroot` only if you are
authorized to obtain/use the Apple system files and understand the licensing.

The workflow runs on GitHub's Apple Silicon `macos-26` runner:

* `ReimsArm64Maclator`: Maclator built for an **x86_64-apple-darwin** guest,
  with separate x86 host and ARM guest Metal bridge libraries. Pinned upstream
  commit `bd80650fc494bc8bcdb7a291b9e97d5c0a38cb03` (MIT).
* `ReimsArm64AppleSysroot`: the runner's `/usr/lib/dyld`, the
  `dyld_shared_cache_arm64e` base and **all** available split subcaches,
  and a JSON source/build/hash manifest. Collection fails if any required
  source is missing. This short-lived workflow artifact is never committed to
  the repo or published to GitHub Releases.

GitHub artifacts are **not a license grant**. Read the Apple software
license before obtaining/transferring any Apple-supplied binary. In a public
repository, workflow artifacts may be accessible to others with repository
read access; use a private checkout/workflow when confidentiality is required.
The sysroot export is off for all automatic PR builds.

## Installation in a running Reims macOS VM

1. Install and boot an Intel x86_64 macOS 26 VM using Reims.
2. Pair the VM through Manager's existing SSH pairing page (verified host key).
3. Download the two ZIP artifacts from the successful workflow run, without
   extracting them on Windows.
4. In **Settings → ARM64 アプリ**, choose both ZIPs and select **VM に導入**.
5. The Manager validates ZIP paths/contents, SCP transfers them through the
   existing verified VM SSH channel, and installs atomically into the guest
   user's `~/.local/share/reims-arm64`. Existing data is kept on failure.
   It does not alter System files, OpenCore, SIP, or the virtual GPU.
6. Open the guest Terminal and launch an ARM64 executable with:
   `~/bin/reims-arm64 /path/to/App.app/Contents/MacOS/Executable`.
   Add `--gpu` before the executable to opt into Maclator's GPU bridge.

The guest setup requires `/usr/lib/dyld` to offer an ARM64e slice, as well
as a working x86_64 macOS environment. To diagnose use **VM の導入状態を確認**.

## Limits and compatibility

* This is a **user-mode translation runtime**, not ARM64 kernel code support.
* GUI integration, Electron spawn, graphics and sandboxed apps can have
  limitations. Maclator's native Intel-Mac demo results are not proof of
  functionality inside a Reims VM. The first smoke test should be a small
  unsigned/ad-hoc-signed command-line Mach-O before trying an AppKit app.
* FairPlay/DRM-encrypted apps, ARM64e-only apps, iOS apps and many system
  components remain unsupported.
* Large shared-cache artifacts can exceed runner disk space/upload quotas.
  GitHub Actions may need a runner with additional capacity. The Manager
  does not download Apple binaries without explicit user action.
* A successful install does not demonstrate execution of ARM64 apps.
  Perform native boot and workload verification before advertising support.

Upstream references: https://github.com/linuxkid473/maclator and
https://github.com/tqmane/reims-vgpu .
