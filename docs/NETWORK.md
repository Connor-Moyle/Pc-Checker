# Network page

The Network page (Performance group, between Latency and Overclock) tests the
Wi-Fi connection and the internet line, explains what it finds in plain
English, and offers reversible tuning. This document describes how each part
works, what it changes and where its limits are.

## What runs automatically

Opening the page reads the network adapters, analyzes Wi-Fi and reads the
current tuning state. It does **not** run the speed test (it uses up to
125 MB of data) or the packet loss test; both start with a click, and
**Run all tests** in the header runs Wi-Fi, packet loss and speed one after
another so they do not disturb each other.

## Wi-Fi analysis

Source: `netsh wlan show interfaces` and `netsh wlan show networks mode=bssid`.

Windows translates every label in that output ("Channel", "Kanal", "Canal"…),
so `NetshWlanParser` recognises fields by the **shape of their values** and
by position, and uses label lists in several languages only to break ties:

| Field | Recognised by |
|---|---|
| BSSID / adapter address | MAC address pattern; the first MAC is the adapter, the one labelled BSSID (or the second) is the access point |
| SSID | the label `SSID` (not translated) |
| Radio standard | value `802.11…` |
| Band | value `2.4 GHz` / `5 GHz` / `6 GHz` (also `2,4 GHz`) |
| Signal | a percentage, preferring the line labelled Signal/Señal/Segnale… |
| Link rates | labels carrying a unit in parentheses (`(Mbps)`, `(MBit/s)`); first = receive, second = send |
| Channel | known label, otherwise the first bare number 1–233 between the BSSID and the rate lines |
| Authentication / cipher | known label, otherwise the first `WPA…`/`WEP`/open value and the line after it |

When netsh reports no band, the band comes from the channel: 1–14 is 2.4 GHz,
32–177 is 5 GHz, and 181–233 is 6 GHz for a Wi-Fi 6E/7 radio. The signal
percentage is converted to an estimated dBm with `dBm = percent / 2 − 100`
(Windows 11 prints the real RSSI on some builds; that is used when present).

**Location permission.** Windows 11 24H2 and later only let desktop apps list
Wi-Fi networks when Location is allowed for them. PC Checker detects netsh's
message, shows an explanation and opens `ms-settings:privacy-location`. It
never reads your location.

**No Wi-Fi adapter** is not an error: the page says the PC uses a cable and
skips the Wi-Fi checks; the speed and packet loss tests still work.

### Findings (`WifiRules`)

| Id | When | Severity |
|---|---|---|
| `wifi.signal.weak` | signal under 40 % | High |
| `wifi.signal.fair` | signal under 60 % | Medium |
| `wifi.security.open` / `wifi.security.wep` | open network or WEP | Critical |
| `wifi.security.tkip` | WPA (v1) or TKIP | High |
| `wifi.channel.crowded` | more than 3 other access points within ±4 channels on 2.4 GHz; suggests the least crowded of 1/6/11 | Medium |
| `wifi.band.faster-available` | connected on 2.4 GHz while the same network is visible on 5/6 GHz at 35 % or better | Medium |
| `wifi.linkrate.low` | good signal but a link rate below what the standard normally reaches (for example under 300 Mbps for Wi-Fi 6 on 5 GHz) | Medium |
| `wifi.radio.legacy` | connected with 802.11a/b/g | Medium |
| `wifi.powersave` | the power plan lets the Wi-Fi adapter save power (fix: `wifi.powersave.max-performance`) | Low |

Channel congestion (`ChannelCongestionCalculator`) counts overlapping access
points: on 2.4 GHz networks less than five channels apart overlap, on 5 and
6 GHz only the same channel counts. The best 2.4 GHz channel weighs strong
neighbours more than faint ones and ignores your own router.

## Speed test

`NetworkSpeedTester` uses a Cloudflare-style speed server
(`speed.cloudflare.com` by default; `Network.SpeedTestServer` in
`settings.json`):

1. **Idle latency**: 11 requests for `/__down?bytes=0`; the first (DNS + TLS
   handshake) is dropped, the median of the rest is the idle ping and their
   jitter is reported.
2. **Download**: four parallel connections fetch `/__down?bytes=N` until the
   budget (`DownloadMegabytes`, 100 MB) is used or 15 seconds pass. The
   first 0.5 s (TCP ramp-up) is ignored.
3. **Upload**: four parallel POSTs of random (incompressible) data to `/__up`
   until `UploadMegabytes` (25 MB) or 15 seconds.
4. **Latency under load**: during both transfers a separate connection sends
   an empty request every 250 ms. Bufferbloat = median loaded − median idle
   latency, graded A+ (< 5 ms), A (< 30), B (< 60), C (< 200), D (< 400),
   F (≥ 400).

The page shows the data used. When the server cannot be reached (offline, a
proxy asking for sign-in, a filter) the result says so instead of showing
zeros. Results depend on the server and the time of day.

## Packet loss test

ICMP echo (`System.Net.NetworkInformation.Ping`) to the router (`gateway`)
and the targets in `Network.PacketLossTargets` (1.1.1.1 and 8.8.8.8 by
default): 50, 100 or 500 requests per target, one every 200 ms with a
1000 ms timeout, all targets in parallel. Per target: sent, received, loss %,
average, minimum, maximum, jitter and 95th percentile. When the router itself
loses packets the problem is inside the home (usually Wi-Fi); when only the
internet targets lose packets it is the provider. Some routers do not answer
ping at all; the page says that this is usually not a fault.

## Repair tools

| Tool | Command | Notes |
|---|---|---|
| Flush DNS | `ipconfig /flushdns` | nothing to undo |
| Renew IP | `ipconfig /release` + `/renew` | connection drops for a few seconds |
| Reset network stack (Advanced) | `netsh winsock reset` + `netsh int ip reset` | clears hand-typed IP/DNS settings; restart required; restore point first when enabled |

All three ask first and run through the change guard (journal + log).

## Tuning

Every change asks for confirmation, runs through `IChangeGuard`, and captures
the value it overwrites in `%LOCALAPPDATA%\PcChecker\network-tuning.json`
(and in the action journal) so **Revert** restores exactly that value, also
after the app or PC restarted. Safe tweaks rely on that capture; Moderate and
Advanced ones also get a restore point when Settings ask for one.

| Action id | What it does | Revert |
|---|---|---|
| `wifi.powersave.max-performance` | power plan *Wireless Adapter Settings → Power Saving Mode* (subgroup `19cbb8fa-…`, setting `12bbebe6-…`) = Maximum Performance on AC and DC | captured AC/DC values (default AC 0, DC 2) |
| `wifi.adapter.allow-turn-off.disable` | `PnPCapabilities` = 24 on the Wi-Fi adapter's driver key (clears "Allow the computer to turn off this device") | captured value, or the value removed |
| `net.tcp.autotuning.normal` | `netsh int tcp set global autotuninglevel=normal` | captured level |
| `net.tcp.ecn.default` | `netsh int tcp set global ecncapability=default`; the resulting value is remembered as this PC's default | captured value |
| `net.rss.enable` | `netsh int tcp set global rss=enabled` | captured value |
| `net.dns.fast` (`.cloudflare`, `.google`, `.quad9`) | `Set-DnsClientServerAddress` on every connected adapter with a gateway | captured `NameServer` per adapter: automatic adapters get `-ResetServerAddresses`, hand-typed servers are put back |
| `net.dns.doh` (Advanced, Windows 11) | DoH template with automatic upgrade for the DNS servers in use (`Add/Set-DnsClientDohServerAddress`, fallback to plain DNS allowed), plus the per-adapter switch Settings uses (`Dnscache\InterfaceSpecificParameters\{guid}\DohInterfaceSettings\Doh\<server>` `DohFlags` = 1, "On (automatic template)") and the global `EnableAutoDoh` = 2. The state only reads "Encrypted" when the template and every adapter's switch are on | captured template settings (or the entry removed), each adapter's previous `DohFlags` and the previous `EnableAutoDoh` |
| `net.ipv6.prefer-ipv4` (Advanced) | `Tcpip6\Parameters\DisabledComponents` bit 0x20 (prefer IPv4; IPv6 stays on), other bits kept | captured value |

Turning off Nagle's algorithm lives on the Latency page (`latency.nagle.off`);
the Network page links to it instead of duplicating it.

**Not offered on purpose:** `netsh wlan set autoconfig enabled=no` (often
suggested to stop background scans) turns off Windows' Wi-Fi management
entirely: no reconnects, no roaming, an empty network list. PC Checker does
not offer it.

### Adapter driver settings

Read with `Get-NetAdapterAdvancedProperty` and changed with
`Set-NetAdapterAdvancedProperty -RegistryKeyword … -RegistryValue …` (the
adapter restarts, so the connection drops briefly). Drivers name things
differently, so `AdapterSettingAdvisor` matches the registry keyword first
and the display name second, and only lists properties the adapter has:

| Setting | Recommendation |
|---|---|
| Preferred band | prefer 6 GHz / 5 GHz when your network is visible there |
| Roaming aggressiveness | Lowest on a desktop with one access point; Medium on laptops or mesh |
| MIMO power save | No SMPS |
| Transmit power | Highest |
| 802.11n/ac/ax wireless mode | the newest standard offered |
| Channel width 2.4 GHz | 20 MHz when the band is crowded, otherwise Auto |
| Channel width 5 GHz | Auto |
| Energy-Efficient / Green Ethernet, power saving mode | off on desktops; left alone on laptops |
| Speed & duplex | Auto negotiation |
| Packet coalescing, ARP/NS offload, interrupt moderation, flow control | left at the driver default |

Properties PC Checker does not know get no recommendation and are shown
read-only in Advanced mode only.

### Saved networks

`netsh wlan show profiles` / `show profile name=…` (parsed by shape, like the
interface output). Order (`set profileorder`), auto-connect and metered
(`set profileparameter connectionmode=… / cost=Fixed|Unrestricted`) and
forget (`delete profile`). Before a network is forgotten its profile is
exported (password still encrypted, readable only on this PC) to
`%LOCALAPPDATA%\PcChecker\wifi-profile-backups\`; the journal entry shows the
`netsh wlan add profile filename=…` command that brings it back. Names with
double quotes are refused rather than passed to netsh unsafely.

Before the first change to a saved network, its position, auto-connect and
metered settings are captured in `network-tuning.json` (entry
`profile:<name>`), so the row's **Revert** button puts them back, also after a
restart. A change that fails does not leave a capture behind.

### Quick improve Wi-Fi

`QuickImprovePlanner` lists every recommended change that differs from the
current state: the recommended actions and Safe adapter settings start
ticked, DNS and Moderate settings start unticked, Advanced actions are left
out. The changes run one after another with progress; afterwards **Re-run
tests and compare** repeats the Wi-Fi analysis and the speed test and shows
both results side by side (differences under 3 % count as the same).

## Settings (`settings.json`, section `Network`)

| Key | Default |
|---|---|
| `SpeedTestServer` | `speed.cloudflare.com` |
| `DownloadMegabytes` | 100 |
| `UploadMegabytes` | 25 |
| `PacketLossCount` | 100 |
| `PacketLossTargets` | `["gateway", "1.1.1.1", "8.8.8.8"]` |

## Demo mode

`DemoNetworkService` and `DemoNetworkTuningService` describe the sample
desktop: Intel Wi-Fi 6E AX211 on "HomeNet" (5 GHz, channel 36, 78 %,
1201 Mbps), nine nearby access points with a crowded 2.4 GHz channel 6, an
Intel I226-V Ethernet port, a clean packet loss test (1 % to 8.8.8.8) and a
412/38 Mbps speed test with grade B. Tuning changes are kept in memory, so
Apply, Revert and Quick improve behave like the real thing.
