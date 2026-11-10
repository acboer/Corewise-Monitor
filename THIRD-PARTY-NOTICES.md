# Third-party components

Corewise itself is licensed under the [MIT License](LICENSE). It is built with the open-source components below,
which keep their own licenses. Unmodified upstream libraries are bundled inside the single-file executables.

| Component | Used for | License |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | CPU, GPU, motherboard, fan and disk sensors | MPL-2.0 |
| [HidSharp](https://www.zer7.com/software/hidsharp) (via LibreHardwareMonitorLib) | USB HID devices | Apache-2.0 |
| [.NET](https://github.com/dotnet/runtime), [ASP.NET Core](https://github.com/dotnet/aspnetcore), [WPF](https://github.com/dotnet/wpf) | Runtime, HTTPS service, user interface | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | History database | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | SQLite bindings | Apache-2.0 |
| [SQLite](https://sqlite.org) | History database engine | Public domain |
| System.Management, System.Diagnostics.PerformanceCounter, System.Security.Cryptography.ProtectedData, Microsoft.Extensions.Hosting.WindowsServices | Windows APIs | MIT |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D11, D3DCompiler) and SharpGen.Runtime | GPU benchmark (DirectX 11 compute) | MIT |
| [Inter](https://rsms.me/inter/) | Typeface used in the design files | SIL Open Font License 1.1 (`design/Inter/OFL.txt`) |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | Installer | Inno Setup License |

## Not bundled

- **[PawnIO](https://pawnio.eu/)** is the signed kernel driver LibreHardwareMonitor uses to read motherboard and CPU
  sensors. Corewise does not ship or install it; when it is installed separately, more sensors become available.
  Without it, Corewise still works with the sensors Windows exposes.
