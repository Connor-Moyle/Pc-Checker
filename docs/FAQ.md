# Frequently asked questions

### Is it free?

Yes. PC Checker is open source under the MIT licence. The optional Claude
assistant uses your own Anthropic API key, which Anthropic bills separately.

### Why does it ask for administrator rights?

Reading SMART data, installing drivers, cleaning system folders, changing
services and features, and tuning the GPU all need them. The app does not
install a background service and nothing runs when it is closed.

### Does it send my data anywhere?

No, unless you turn on the Claude assistant and send a message. The only other
network calls are the update check and driver downloads through Windows
Update. See [PRIVACY.md](PRIVACY.md).

### Windows SmartScreen says "unrecognised app"

New releases are not yet signed with a certificate SmartScreen trusts. Click
**More info → Run anyway**. You can verify the download came from this
repository's Releases page.

### Which Windows versions work?

Windows 11 and Windows 10 22H2, 64-bit. Older versions are not supported.

### Will it break my PC?

Every change is confirmed first, creates a restore point and is journaled with
an undo hint. Read [SAFETY.md](SAFETY.md) for the full rules. If something
goes wrong, the restore point the app made is the way back.

### No temperatures are showing

- Some laptops and OEM desktops expose very few sensors.
- Another monitoring tool (for example one that ships with a motherboard) can
  hold the sensor interface. Close it and click *Refresh*.
- Virtual machines report almost nothing.

The Temperatures page shows a reason when the sensor service could not start.

### Where are the settings and logs?

`%LOCALAPPDATA%\PcChecker\settings.json` and
`%LOCALAPPDATA%\PcChecker\logs`. Delete the folder to reset the app.

### Can I run it without installing?

Yes. Download the portable exe from Releases. It cannot self-update; download
the new version when you are told one is available.

### The overlay does not show in my game

The game is probably in exclusive full-screen mode. Switch it to borderless
window. See [OVERLAY.md](OVERLAY.md).

### Can it overclock AMD or Intel graphics?

Not directly. For AMD and Intel GPUs the Overclock page is read-only and
links to the vendor's tuning tool. NVIDIA cards are tuned through NVAPI.

### What does the health score mean?

It starts at 100 and loses points for each issue found, weighted by how
serious it is: failing SMART, high battery wear, device problems, repeated
system errors, crash dumps and high temperatures. The page lists every
deduction.

### A program is marked "protected", how do I remove it?

Edge, WebView2, Defender and the .NET and Visual C++ runtimes are protected
because other software depends on them. Advanced mode lets you override the
protection for non-critical items, with a warning.

### Can I use it on a work PC?

Check with your IT department. Some actions (features, services, drivers) may
be controlled by policy and will fail with an explanation.

### How do I report a bug?

Export the logs from the Logs page and open a
[bug report](https://github.com/Connor-Moyle/test-pcchecker/issues/new?template=bug_report.yml).
