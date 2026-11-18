# Third-party components

Nodekeeper itself is licensed under the [MIT License](LICENSE). It is built with the open-source components below,
which keep their own licenses. Unmodified upstream libraries are bundled inside the single-file executables.

| Component | Used for | License |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | CPU, GPU, motherboard, fan and disk sensors | MPL-2.0 |
| [PawnIO](https://pawnio.eu) | Kernel driver used by LibreHardwareMonitorLib for CPU, motherboard and RAM sensors. Not bundled: the installer downloads the official signed setup from [PawnIO.Setup](https://github.com/namazso/PawnIO.Setup) when it is missing | Its own license (see pawnio.eu) |
| [HidSharp](https://www.zer7.com/software/hidsharp) (via LibreHardwareMonitorLib) | USB HID devices | Apache-2.0 |
| [.NET](https://github.com/dotnet/runtime), [ASP.NET Core](https://github.com/dotnet/aspnetcore), [WPF](https://github.com/dotnet/wpf) | Runtime, HTTPS service, user interface (the accent brushes in `src/HwMon.App/Theme/FluentAccent.*.xaml` are adapted from the WPF Fluent theme) | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | History database | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | SQLite bindings | Apache-2.0 |
| [SQLite](https://sqlite.org) | History database engine | Public domain |
| System.Management, System.Diagnostics.PerformanceCounter, System.Security.Cryptography.ProtectedData, Microsoft.Extensions.Hosting.WindowsServices | Windows APIs | MIT |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D11, D3DCompiler) and SharpGen.Runtime | GPU benchmark (DirectX 11 compute) | MIT |
| [Manrope](https://github.com/googlefonts/manrope) | Interface typeface, embedded in the dashboard | SIL Open Font License 1.1 (`design/nodekeeper/fonts/OFL.txt`) |
| [Inter](https://rsms.me/inter/) | Typeface used in earlier design files | SIL Open Font License 1.1 (`design/Inter/OFL.txt`) |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | Installer | Inno Setup License |

## Not bundled

- **[PawnIO](https://pawnio.eu/)** is the signed kernel driver LibreHardwareMonitor uses to read motherboard and CPU
  sensors. Nodekeeper does not ship or install it; when it is installed separately, more sensors become available.
  Without it, Nodekeeper still works with the sensors Windows exposes.
