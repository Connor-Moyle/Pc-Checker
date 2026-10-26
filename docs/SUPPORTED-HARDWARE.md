# Supported hardware

PC Checker runs on any 64-bit Windows 11 or Windows 10 22H2 PC. What it can
*control* depends on the hardware and on which vendor SDKs and tools are
installed. This page is the honest version of the matrix in the README.

Two rules apply everywhere in the app:

1. **Every feature shows its support level on your machine**: *Full*,
   *Experimental*, *Monitor only* or *Not supported on this device*, with a
   one-line reason.
2. **Nothing silently does nothing.** When the app cannot control something it
   says so and offers to launch the vendor's own software or explains the
   BIOS steps.

## Levels

| Level | Meaning |
|---|---|
| **Full** | The app reads and changes it directly and the path has been exercised on real hardware. |
| **Experimental** | Implemented against the vendor's public SDK headers but not yet verified across many cards. Every value is validated against the limits the driver reports. If any SDK call fails, the feature drops to *Monitor only* with the error code in its notes. Please report results. |
| **Monitor only** | Readings are shown; nothing can be changed from the app. |
| **Via vendor software** | The app detects the vendor tool, launches it (or links its download) and explains what to do there or in BIOS. |
| **Not supported** | The app cannot help on this device and says why. |

## The matrix

| Feature | NVIDIA | AMD | Intel | Other | Mechanism |
|---|---|---|---|---|---|
| Sensors and temperatures | Full | Full | Full | Full where LibreHardwareMonitor has a driver (see below) | LibreHardwareMonitor |
| Fan control | Full (GPU fans) | Full (GPU fans) | Monitor only (Arc fans) | Full on supported motherboard Super I/O chips and some coolers; usually unavailable on laptops and OEM boards | LibreHardwareMonitor controls |
| GPU overclocking | Full (clock offsets); power and temperature limits where the driver API exposes them | Experimental | Experimental (Arc); Monitor only (integrated graphics) | — | NVAPI, ADL/Overdrive8, IGCL |
| CPU tuning | — | Via vendor software (Ryzen Master; optional ryzenadj) | Via vendor software (XTU CLI) | Not supported (ARM) | XTU CLI, Ryzen Master, ryzenadj |
| Memory (XMP/EXPO) | — | Monitor only | Monitor only | Monitor only | WMI |
| RGB lighting | Via OpenRGB | Via OpenRGB | Via OpenRGB | Via OpenRGB; vendor suites linked | OpenRGB SDK |
| Display control | Full | Full | Full | Full; brightness and contrast depend on the monitor | Windows display APIs, DDC/CI, WMI |
| Audio | Full | Full | Full | Full for any WASAPI endpoint | Core Audio (NAudio) |
| Keyboard and mouse settings | — | — | — | Full for Windows settings; DPI and buttons via vendor software | SystemParametersInfo, raw input |
| Storage tests and repairs | — | — | — | Full for any disk Windows exposes as a physical drive | Direct read, chkdsk, Repair-Volume, Optimize-Volume |
| Drivers | Full | Full | Full | Full | Windows Update Agent, pnputil |

## Details by feature

### Sensors and temperatures

LibreHardwareMonitor provides every reading. It supports:

- **CPU**: Intel (Core 2 through the current generation, including hybrid
  P/E-core parts) and AMD (Ryzen, Threadripper, EPYC, older FX and
  Phenom). Package and per-core temperatures, loads, clocks, power and
  voltages where the CPU exposes them.
- **GPU**: NVIDIA through NVAPI, AMD through ADL, Intel Arc and integrated
  graphics through the Windows graphics stack (load and memory; temperatures
  on Arc).
- **Motherboard**: Super I/O chips from Nuvoton (NCT67xx), ITE (IT86xx,
  IT87xx), Fintek, SMSC and Winbond, plus the embedded controllers on many
  ASUS, MSI, Gigabyte and ASRock boards. This is where VRM, chipset, case-fan
  and water-flow readings come from.
- **Drives**: SATA and NVMe SMART (temperature, wear, host writes, power-on
  hours). USB-attached drives usually do not expose SMART.
- **Memory**: DDR4 and DDR5 module temperatures where the module has a sensor.
- **PSUs and coolers**: Corsair HX/RM i-series PSUs and a few AIO pumps and
  controllers.

Common reasons readings are missing: core isolation (memory integrity)
blocking the kernel driver, another monitoring tool holding the hardware, an
OEM laptop that hides its sensors, or a virtual machine. The Temperatures page
shows the reason.

### Fan control

Fan control uses LibreHardwareMonitor's *control* sensors. A fan is
controllable when the board or device exposes a PWM control the library knows
how to write:

- **Motherboard headers**: the same Super I/O chips listed above. Most
  enthusiast desktop boards from ASUS, MSI, Gigabyte and ASRock work. Some
  boards expose RPM but not control for certain headers.
- **GPU fans**: NVIDIA and AMD cards through the driver. Intel Arc cards show
  RPM but the library does not expose a control.
- **Coolers**: a few AIOs and fan controllers. Devices that only speak a
  vendor USB protocol (most iCUE, NZXT CAM and Lian Li controllers) are not
  controllable; use the vendor software.
- **Laptops and OEM desktops**: almost never. The embedded controller owns the
  fans and does not expose a standard interface. The page says so instead of
  showing a slider that does nothing.

Safety: a minimum percentage (Settings → Fans, default 20 %) is enforced
unless you explicitly allow lower; a critical temperature forces 100 %; fans
revert to automatic on exit and after a crash.

### GPU overclocking

| Vendor | Method | What works | Notes |
|---|---|---|---|
| NVIDIA (GeForce 900 series and newer) | NVAPI through NvAPIWrapper | Core and memory clock offsets (full range reported by the driver), fan percent | Power limit and temperature limit are applied when the wrapper exposes the driver calls; the page notes when it cannot. Offsets do not persist across reboots. |
| AMD (RDNA and newer with Overdrive8) | ADL / Overdrive8 through `atiadlxx.dll` | GPU clock maximum, memory clock, power limit percentage, fan minimum speed, target temperature, zero-RPM | **Experimental.** Every value is validated against the init-setting minimum and maximum the driver reports. If `atiadlxx.dll` is missing or any call fails the page drops to *Monitor only* and offers *Open AMD Software: Adrenalin Edition* (Performance → Tuning). |
| Intel Arc (A-series, B-series) | IGCL through `ControlLib.dll` | GPU frequency offset, power limit, temperature limit, fan | **Experimental.** Falls back to *Open Intel Graphics Software*. Integrated Intel graphics are *Monitor only*. |

### CPU tuning

The app never writes model-specific registers or BIOS settings itself.

| Vendor | What the app does |
|---|---|
| Intel | If Intel Extreme Tuning Utility is installed, the app uses its command-line client to list tunables and set the all-core ratio and voltage offset by name. If a tunable name does not match what the app expects, it refuses rather than guessing. Otherwise it links the XTU download and explains the BIOS steps. |
| AMD | Launches AMD Ryzen Master when installed. If `ryzenadj.exe` is found next to the app in `tools\` or on `PATH`, the app can set STAPM, fast and slow power limits and the temperature limit (useful on laptops and APUs). Otherwise BIOS guidance. |
| Windows on Arm | Not supported; the app is x64 only. |

### Memory

XMP (Intel) and EXPO (AMD) profiles can only be enabled in BIOS. The Memory
page compares the rated speed on each module with the configured speed and
tells you which profile to enable and roughly where to find it. The in-app
memory test, standby-list clearing, memory profiles and page-file editing work
on any PC.

### RGB lighting

All lighting goes through [OpenRGB](https://openrgb.org), which supports
hundreds of devices: ASUS Aura, MSI Mystic Light, Gigabyte RGB Fusion and
ASRock Polychrome motherboards; Corsair, G.Skill, Kingston, Crucial and
Team Group RAM; NVIDIA and AMD partner cards from ASUS, MSI, Gigabyte, EVGA,
Sapphire and others; keyboards and mice from Corsair, Razer, Logitech,
SteelSeries, HyperX, Cooler Master and more; many AIOs, fan hubs and strips.
The [OpenRGB supported devices list](https://openrgb.org/devices.html) is the
authority. Some devices need a one-time setup in OpenRGB (for example an SMBus
or USB access step); the app links the OpenRGB documentation when it sees a
known case.

Needs: OpenRGB installed and running with **SDK Server** enabled (default port
6742). The Lighting page can launch OpenRGB for you.

### Display control

Resolution, refresh rate, orientation, primary display, position and HDR use
the Windows display APIs and work on any GPU. Brightness and contrast use
DDC/CI on external monitors (many monitors answer; some ignore the command or
answer only after a delay, and the page says when it gave up) and WMI on
laptop panels. Variable refresh rate stays in Windows Graphics settings. The
GPU control panel shortcut opens the NVIDIA app or Control Panel, AMD Software
or Intel Graphics Software depending on the adapter.

### Audio

Any device Windows exposes through WASAPI: onboard codecs, USB and Bluetooth
headsets, HDMI and DisplayPort audio, virtual devices. Setting the default
device uses the same undocumented interface Windows' own Sound settings use;
if it fails on a future Windows build the page opens Sound settings instead.

### Keyboard and mouse

Pointer speed, acceleration, double-click, scrolling, trails, keyboard repeat,
blink rate and accessibility toggles are Windows settings and work with any
device. DPI, onboard profiles, button mapping and macros live in the vendor's
software; the page links Logitech G HUB, Razer Synapse, Corsair iCUE,
SteelSeries GG, Glorious Core, ASUS Armoury Crate, HyperX NGENUITY, Microsoft
Mouse and Keyboard Center, Wootility and Keychron Launcher. The polling-rate
meter counts raw input events and works with any mouse.

### Storage

The surface scan reads every physical drive Windows exposes (SATA, NVMe, USB,
RAID volumes presented as one disk) through a read-only handle. The benchmark
works on any volume with enough free space. `chkdsk`, `Repair-Volume` and
`Optimize-Volume` are the Windows tools. Vendor tool links are provided for
Samsung, Western Digital and SanDisk, Seagate, Crucial and Micron, Kingston,
Intel and Solidigm, SK hynix, Toshiba and KIOXIA, Corsair and ADATA drives.

### Drivers

The Windows Update driver catalog covers every device class. Vendor links are
provided for NVIDIA, AMD and Intel graphics and chipset drivers and for your
motherboard or system vendor's support page.

## Reporting what you have

If the app says *Not supported* for something that other software can
control, open a
[driver or hardware issue](https://github.com/Connor-Moyle/test-pcchecker/issues/new?template=driver_or_hardware_issue.yml)
with the model names, what the page said and your diagnostics export. That is
how the matrix grows.
