# Third-party notices

PC Checker is released under the MIT licence (see [LICENSE](../LICENSE)). It is built on the open-source
components below. Each one keeps its own licence; the texts are available from the linked projects and are
included with the published packages.

| Component | Used for | Licence | Project |
|---|---|---|---|
| LibreHardwareMonitor (LibreHardwareMonitorLib) | Temperatures, loads, clocks, fan speeds and fan control | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| WPF-UI | Fluent user interface controls and theming | MIT | https://github.com/lepoco/wpfui |
| Velopack | Installer and self-update | MIT | https://github.com/velopack/velopack |
| NvAPIWrapper.Net | NVIDIA GPU clocks, power and temperature limits | LGPL-3.0 | https://github.com/falahati/NvAPIWrapper |
| Serilog, Serilog.Sinks.File, Serilog.Sinks.Debug, Serilog.Extensions.Logging | Logging | Apache-2.0 | https://serilog.net |
| CommunityToolkit.Mvvm | MVVM source generators (observable properties, commands) | MIT | https://github.com/CommunityToolkit/dotnet |
| Anthropic SDK (Anthropic) | The "Ask Claude" assistant and the overclock advisor | MIT | https://github.com/anthropics/anthropic-sdk-csharp |
| OpenRGB.NET | RGB lighting through the OpenRGB SDK server | MIT | https://github.com/OpenRGB-dotnet/OpenRGB.NET |
| NAudio (NAudio.Core, NAudio.Wasapi) | Sound devices, volumes and per-app sessions | MIT | https://github.com/naudio/NAudio |
| System.Diagnostics.PerformanceCounter | DPC/interrupt time and other Windows performance counters | MIT | https://github.com/dotnet/runtime |
| System.Management | WMI queries for hardware and system information | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.ProtectedData | Protecting the saved API key with DPAPI | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.* (DependencyInjection, Hosting, Logging) | Service container and logging abstractions | MIT | https://github.com/dotnet/runtime |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Unit tests (not shipped) | Apache-2.0 / MIT | https://xunit.net |

## Notes on specific licences

- **MPL-2.0 (LibreHardwareMonitor)**: the library is used unmodified as a NuGet package. The MPL covers the
  library's own source files; PC Checker's code stays under MIT. Modified copies of MPL files, if any are ever made,
  must be published under the MPL.
- **LGPL-3.0 (NvAPIWrapper.Net)**: the library is linked dynamically as a separate assembly, so it can be replaced
  with a compatible version by the user. No LGPL code is merged into PC Checker's own assemblies.
- **Vendor software**: PC Checker launches NVIDIA App, AMD Software: Adrenalin Edition, Intel Graphics Software,
  Intel XTU, AMD Ryzen Master, OpenRGB and peripheral suites when they are installed. Those programs are not
  distributed with PC Checker and keep their own terms.

The same list is shown on the Settings page under About.
