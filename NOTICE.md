# Source and third-party notices

The initial Windows manager and backend were extracted from
[tqmane/reims-vgpu](https://github.com/tqmane/reims-vgpu) at
`d8b9c2afb778d456069bf54cb4cd40e4cc77a0e7`.
The inherited project license is `GPL-2.0-or-later OR LGPL-3.0-or-later`;
license texts are retained in `LICENSES`.

Portable packages also carry separately licensed, unmodified runtimes:

- [.NET](https://github.com/dotnet/runtime): MIT and included third-party notices.
- [PowerShell](https://github.com/PowerShell/PowerShell): MIT and included third-party notices.
- [Python](https://www.python.org/): PSF license and included license text.

Official URLs and pinned SHA256 values are in `tools/runtimes.json` and
copied into each package. Source control contains no runtime binaries.
Reims/QEMU engines are separate and retain their own license and source
requirements.

Optional Wi-Fi IPv4 compatibility includes an authored helper statically
linked to the MIT-licensed [ndisapi SDK](https://github.com/wiresock/ndisapi)
and [Microsoft GSL](https://github.com/microsoft/GSL). Exact source pins and
both license texts accompany the helper. The separately licensed Windows
Packet Filter driver is not bundled or committed; optional installation
downloads the official vendor package after explicit terms acceptance.
See [delivery and licensing](docs/wifi-macnat-contract.md).
